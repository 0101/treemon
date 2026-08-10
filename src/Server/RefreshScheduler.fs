module Server.RefreshScheduler

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Shared
open Server.SchedulerState

type SchedulerServices =
    { SessionAgent: SessionManager.SessionAgent
      ActivityStore: SessionActivityStore.SessionActivityStore option
      MergedPrStore: MergedPrStore.Store
      AutoSyncStore: AutoSyncStore.Store }

/// The PR status of the branch checked out at `path`, resolved inside that worktree's own repo:
/// PR data is keyed by provider branch, which is only unambiguous within one repository. `None` is
/// "not known yet" — no PR refresh has succeeded for that repository — and is deliberately not
/// collapsed into `NoPr`, which the mechanical push decision would read as "nothing to publish to".
let internal prStatusForPath (state: DashboardState) (path: string) =
    state.Repos
    |> Map.values
    |> Seq.tryPick (fun repo ->
        repo.GitData
        |> Map.tryFind path
        |> Option.map (fun gitData -> PrStatus.tryLookupPrStatus repo.PrData (GitWorktree.prBranchName gitData)))
    |> Option.flatten

/// The repository owning a worktree path, resolved from one snapshot. `RepoId`'s value is the
/// normalized repository root, so a linked worktree resolves to the root it shares `.treemon.json`
/// with — which is what lets a per-worktree request read repository-level configuration.
let tryFindOwningRepo (state: DashboardState) (worktreePath: string) =
    let normalizedPath = PathUtils.normalizePath worktreePath

    state.Repos
    |> Map.tryPick (fun repoId repo ->
        if repo.KnownPaths |> Set.contains normalizedPath then
            Some(repoId, repo)
        else
            None)

/// Re-read one worktree's Git state into the dashboard. The scheduler's own `RefreshGit` pass uses
/// it, and so does a mechanical auto-sync: that sync moves the branch itself, so the behind count on
/// the card is stale the moment it succeeds and would otherwise stand until the next scheduled pass.
/// Returns the observation so a caller that needs it does not collect twice.
let internal reloadGitData (agent: MailboxProcessor<StateMsg>) (repoId: RepoId) (path: string) =
    async {
        let! state = agent.PostAndAsyncReply(GetState)
        let repo = state.Repos |> Map.tryFind repoId |> Option.defaultValue PerRepoState.empty

        let branch =
            repo.WorktreeList
            |> List.tryFind (fun wt -> wt.Path = path)
            |> Option.bind _.Branch

        let! gitData =
            GitWorktree.collectWorktreeGitData path branch repo.UpstreamRemote repo.BaseBranch

        agent.Post(UpdateGit(repoId, path, gitData))
        return gitData
    }

let internal autoSyncDependencies
    (agent: MailboxProcessor<StateMsg>)
    (sessionAgent: SessionManager.SessionAgent)
    (activityStore: SessionActivityStore.SessionActivityStore option)
    (autoSyncStore: AutoSyncStore.Store option)
    : AutoSync.TriggerDependencies =
    let launch worktreePath text =
        let provider = CodingToolStatus.readConfiguredProvider (WorktreePath.value worktreePath)
        let command =
            CodingToolCli.build provider (CodingToolCli.Interactive text)
        SessionManager.launchAction sessionAgent worktreePath command.AsShellString

    // Fixture mode runs without a durable store: nothing is recorded, so every observation of a
    // behind worktree is a first one and the operation guard remains the only serialization.
    { ReadAcceptedRevision =
        fun path ->
            match autoSyncStore with
            | Some store -> store.Get path
            | None -> async.Return None
      RecordAcceptedRevision =
        fun path baseRevision ->
            match autoSyncStore with
            | Some store ->
                AutoSyncStore.publishAccepted
                    store
                    path
                    { BaseRevision = baseRevision
                      AcceptedAt = DateTimeOffset.UtcNow }
            | None -> async.Return()
      ClearAcceptedRevision =
        fun path -> autoSyncStore |> Option.iter (fun store -> AutoSyncStore.clear store path)
      ReadPrStatus =
        fun path ->
            async {
                let! state = agent.PostAndAsyncReply(GetState)
                return prStatusForPath state path
            }
      ReadOwnership =
        fun path ->
            async {
                let! state = agent.PostAndAsyncReply(GetState)
                return AutoSync.readOwnership activityStore (state.SessionStatuses |> Map.values) path
            }
      TryBeginOperation =
        fun path -> agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncOperation(path, reply))
      CompleteOperation = CompleteAutoSyncOperation >> agent.Post
      MechanicalSync = AutoSync.mechanicalSync GitBranchSync.syncWithBase GitBranchSync.pushSyncedBranch
      ReloadGitData =
        fun path ->
            async {
                let! state = agent.PostAndAsyncReply(GetState)

                match tryFindOwningRepo state path with
                | Some(repoId, _) -> do! reloadGitData agent repoId path |> Async.Ignore
                | None -> Log.log "AutoSync" $"No repository owns {Path.GetFileName path}; skipped Git reload"
            }
      Deliver =
        AutoSync.deliver
            SessionBridge.tryDeliver
            (fun () -> Async.Sleep AutoSync.registrationGraceMilliseconds)
            launch }

type RefreshTask =
    | RefreshWorktreeList of repoId: RepoId
    | RefreshGit of repoId: RepoId * path: string
    | RefreshBeads of repoId: RepoId * path: string
    | RefreshPr of repoId: RepoId
    | RefreshFetch of repoId: RepoId

let private taskLabel = function
    | RefreshWorktreeList repoId -> "WorktreeList", RepoId.value repoId
    | RefreshGit(repoId, path) -> "GitRefresh", $"{RepoId.value repoId}/{Path.GetFileName(path)}"
    | RefreshBeads(repoId, path) -> "BeadsRefresh", $"{RepoId.value repoId}/{Path.GetFileName(path)}"
    | RefreshPr repoId -> "PrFetch", RepoId.value repoId
    | RefreshFetch repoId -> "GitFetch", RepoId.value repoId

let internal intervalOf (activity: ActivityLevel) (task: RefreshTask) =
    match activity, task with
    | ActivityLevel.Active,   RefreshWorktreeList _ -> TimeSpan.FromSeconds(10.0)
    | ActivityLevel.Idle,     RefreshWorktreeList _ -> TimeSpan.FromSeconds(15.0)
    | ActivityLevel.DeepIdle, RefreshWorktreeList _ -> TimeSpan.FromSeconds(60.0)
    | ActivityLevel.Active,   RefreshGit _          -> TimeSpan.FromSeconds(5.0)
    | ActivityLevel.Idle,     RefreshGit _          -> TimeSpan.FromSeconds(15.0)
    | ActivityLevel.DeepIdle, RefreshGit _          -> TimeSpan.FromSeconds(60.0)
    | ActivityLevel.Active,   RefreshBeads _        -> TimeSpan.FromSeconds(30.0)
    | ActivityLevel.Idle,     RefreshBeads _        -> TimeSpan.FromSeconds(60.0)
    | ActivityLevel.DeepIdle, RefreshBeads _        -> TimeSpan.FromSeconds(240.0)
    | ActivityLevel.Active,   RefreshPr _           -> TimeSpan.FromSeconds(10.0)
    | ActivityLevel.Idle,     RefreshPr _           -> TimeSpan.FromSeconds(120.0)
    | ActivityLevel.DeepIdle, RefreshPr _           -> TimeSpan.FromSeconds(600.0)
    | ActivityLevel.Active,   RefreshFetch _        -> TimeSpan.FromSeconds(10.0)
    | ActivityLevel.Idle,     RefreshFetch _        -> TimeSpan.FromSeconds(120.0)
    | ActivityLevel.DeepIdle, RefreshFetch _        -> TimeSpan.FromSeconds(600.0)

let private clientActivityTimeout = TimeSpan.FromMinutes(5.0)
let private clientDeepIdleTimeout = TimeSpan.FromMinutes(20.0)

let effectiveActivity (now: DateTimeOffset) (state: DashboardState) =
    let elapsed = now - state.ClientActivityAt

    if elapsed >= clientDeepIdleTimeout then ActivityLevel.DeepIdle
    elif elapsed >= clientActivityTimeout && state.ClientActivity = ActivityLevel.Active then ActivityLevel.Idle
    else state.ClientActivity

let readArchivedBranchSets (rootPaths: Map<RepoId, string>) =
    rootPaths
    |> Map.map (fun _ root -> TreemonConfig.readArchivedBranchSet (Some root))

let archivedPathsFor (archivedBranches: Set<string>) (repo: PerRepoState) =
    repo.WorktreeList
    |> List.filter (fun wt -> wt.Branch |> Option.exists (fun b -> Set.contains b archivedBranches))
    |> List.map _.Path
    |> Set.ofList

let resolveArchivedPaths (archivedBranchSets: Map<RepoId, Set<string>>) (repos: Map<RepoId, PerRepoState>) =
    repos
    |> Map.map (fun repoId repo ->
        let archivedBranches =
            archivedBranchSets |> Map.tryFind repoId |> Option.defaultValue Set.empty

        archivedPathsFor archivedBranches repo)

let isWorktreeIgnored (ignorePredicate: string -> bool) (wt: GitWorktree.WorktreeInfo) =
    (wt.Branch |> Option.exists ignorePredicate)
    || (wt.Path |> Path.GetFileName |> ignorePredicate)

let ignoredPathsFor (ignorePredicate: string -> bool) (repo: PerRepoState) =
    repo.WorktreeList
    |> List.filter (isWorktreeIgnored ignorePredicate)
    |> List.map _.Path
    |> Set.ofList

let resolveIgnoredPaths (ignorePredicate: string -> bool) (repos: Map<RepoId, PerRepoState>) =
    repos |> Map.map (fun _ repo -> ignoredPathsFor ignorePredicate repo)

type PathFilters =
    { Archived: Map<RepoId, Set<string>>
      Ignored: Map<RepoId, Set<string>> }

let private isPathInSet (paths: Map<RepoId, Set<string>>) repoId path =
    paths |> Map.tryFind repoId |> Option.map (Set.contains path) |> Option.defaultValue false

type MergedPrBranchScope =
    { GitData: Map<string, GitWorktree.GitData>
      KnownBranches: Set<string>
      PruneBranches: Set<string> option }

/// Branch scope for one repo's merged-PR reconciliation. Archived worktrees are skipped by the
/// steady-state refresh, so one first seen while already archived never collects `GitData`. Its
/// worktree-list branch stands in and its path is exempt from the completeness check — otherwise a
/// single such worktree would block pruning for the rest of the process lifetime.
let internal mergedPrBranchScope (ignoredPaths: Set<string>) (archivedPaths: Set<string>) (repo: PerRepoState) =
    let eligiblePaths = Set.difference repo.KnownPaths ignoredPaths

    let eligibleGitData =
        repo.GitData |> Map.filter (fun path _ -> Set.contains path eligiblePaths)

    let collectedGitPaths = eligibleGitData |> Map.keys |> Set.ofSeq

    let uncollectedArchivedPaths =
        Set.difference (Set.intersect eligiblePaths archivedPaths) collectedGitPaths

    let archivedFallbackBranches =
        repo.WorktreeList
        |> List.filter (fun wt -> Set.contains wt.Path uncollectedArchivedPaths)
        |> List.choose _.Branch
        |> Set.ofList

    let knownBranches =
        eligibleGitData
        |> Map.values
        |> Seq.choose GitWorktree.prBranchName
        |> Set.ofSeq
        |> Set.union archivedFallbackBranches

    let readFailedPaths =
        eligibleGitData
        |> Map.filter (fun _ gitData -> gitData.Upstream = GitWorktree.UpstreamReadFailed)
        |> Map.keys
        |> Set.ofSeq

    { GitData = eligibleGitData
      KnownBranches = knownBranches
      PruneBranches =
        MergedPrStore.pruneScope
            (Set.difference eligiblePaths uncollectedArchivedPaths)
            collectedGitPaths
            readFailedPaths
            knownBranches }

/// Worktrees whose PR is merged, paired with their local branch. Deliberately not filtered by the
/// persisted preference: an operation already past its eligibility check when the preference was
/// removed records its revision afterwards, and a preference-filtered cleanup would never look at
/// that worktree again.
let internal mergedPrWorktrees (prData: Map<string, PrStatus>) (gitData: Map<string, GitWorktree.GitData>) =
    gitData
    |> Map.toList
    |> List.choose (fun (path, worktreeGit) ->
        let prStatus = PrStatus.lookupPrStatus prData (GitWorktree.prBranchName worktreeGit)

        if AutoSync.isMergedPr prStatus then
            Some(path, worktreeGit.Branch)
        else
            None)

/// Merged is terminal, so PR reconciliation ends auto-sync for every worktree it observes merged:
/// all of them lose their accepted-revision record, and those still holding the persisted
/// preference lose that too.
let internal deactivateMergedAutoSync
    (store: AutoSyncStore.Store)
    repoRoot
    prData
    gitData
    =
    let merged = mergedPrWorktrees prData gitData
    merged |> List.iter (fst >> AutoSyncStore.clear store)

    let enabledMergedBranches =
        merged
        |> List.map snd
        |> Set.ofList
        |> Set.intersect (TreemonConfig.readAutoSyncBranchSet (Some repoRoot))

    if not (Set.isEmpty enabledMergedBranches) then
        // A `.treemon.json` another process holds open, or that is read-only, must not discard the
        // PR refresh that reached this point. The preference survives the failure, so the next
        // reconciliation observes the same merged branch and writes again.
        try
            TreemonConfig.modifyAutoSyncBranches
                repoRoot
                (Set.ofList
                 >> fun branches -> Set.difference branches enabledMergedBranches |> Set.toList)
        with ex ->
            Log.log "AutoSync" $"Failed to disable auto-sync for merged branches in {repoRoot}: {ex.Message}"

let buildTaskList (filters: PathFilters) (repos: Map<RepoId, PerRepoState>) =
    let repoList = repos |> Map.toList

    let worktreeLists =
        repoList |> List.map (fun (repoId, _) -> RefreshWorktreeList repoId)

    let localTasks =
        repoList
        |> List.collect (fun (repoId, repo) ->
            repo.WorktreeList
            |> List.filter (fun wt ->
                not (isPathInSet filters.Archived repoId wt.Path)
                && not (isPathInSet filters.Ignored repoId wt.Path))
            |> List.collect (fun wt ->
                [ RefreshGit(repoId, wt.Path)
                  RefreshBeads(repoId, wt.Path) ]))

    let networkTasks =
        repoList
        |> List.collect (fun (repoId, _) ->
            [ RefreshPr repoId; RefreshFetch repoId ])

    worktreeLists @ localTasks @ networkTasks

let buildPhase1Tasks (rootPaths: Map<RepoId, string>) =
    rootPaths |> Map.toList |> List.map (fun (repoId, _) -> RefreshWorktreeList repoId)

let buildPhase2Tasks (filters: PathFilters) (repos: Map<RepoId, PerRepoState>) =
    repos
    |> Map.toList
    |> List.collect (fun (repoId, repo) ->
        let perWorktree =
            repo.WorktreeList
            |> List.filter (fun wt -> not (isPathInSet filters.Ignored repoId wt.Path))
            |> List.collect (fun wt ->
                let archived = isPathInSet filters.Archived repoId wt.Path
                [ RefreshGit(repoId, wt.Path)
                  if not archived then
                      RefreshBeads(repoId, wt.Path) ])

        RefreshFetch repoId :: perWorktree)

let buildPhase3Tasks (repos: Map<RepoId, PerRepoState>) =
    repos |> Map.toList |> List.map (fun (repoId, _) -> RefreshPr repoId)

let repositoryDiscoveryUpdate
    (repoId: RepoId)
    (worktrees: GitWorktree.WorktreeInfo list option)
    upstreamRemote
    baseBranch
    =
    UpdateRepositoryDiscovery(
        repoId,
        { Worktrees = worktrees
          UpstreamRemote = upstreamRemote
          BaseBranch = baseBranch }
    )

/// Paths a successful discovery no longer lists. `None` is a Git failure, not an empty repository:
/// treating it as "everything vanished" would clear live records on a transient error, so it removes
/// nothing.
let internal removedWorktreePaths (previous: Set<string>) (discovered: GitWorktree.WorktreeInfo list option) =
    match discovered with
    | None -> Set.empty
    | Some live -> Set.difference previous (live |> List.map _.Path |> Set.ofList)

let private deadlineOf (activity: ActivityLevel) (lastRuns: Map<RefreshTask, DateTimeOffset>) (task: RefreshTask) =
    lastRuns
    |> Map.tryFind task
    |> Option.map (fun t -> t + intervalOf activity task)
    |> Option.defaultValue DateTimeOffset.MinValue

let internal executeTask
    (agent: MailboxProcessor<StateMsg>)
    (services: SchedulerServices)
    (rootPaths: Map<RepoId, string>)
    (task: RefreshTask)
    =
    async {
        match task with
        | RefreshWorktreeList repoId ->
            let root = rootPaths |> Map.find repoId
            let! worktrees = GitWorktree.listWorktrees root
            let! upstreamRemote = GitWorktree.resolveUpstreamRemote root
            let baseBranch = TreemonConfig.readBaseBranch root
            let! state = agent.PostAndAsyncReply(GetState)
            agent.Post(repositoryDiscoveryUpdate repoId worktrees upstreamRemote baseBranch)

            // A worktree removed outside Treemon never gets another observation, so nothing would
            // ever clear its accepted-revision record and it could suppress the first sync of a
            // worktree later recreated at the same path — which the deletion cleanup documented in
            // `docs/spec/worktree-monitor.md` (Branch Sync) promises does not happen. The API
            // deletion path already clears its own; this covers every other way one disappears.
            let knownPaths =
                state.Repos
                |> Map.tryFind repoId
                |> Option.map _.KnownPaths
                |> Option.defaultValue Set.empty

            removedWorktreePaths knownPaths worktrees
            |> Set.iter (AutoSyncStore.clear services.AutoSyncStore)

            let alreadyDetected =
                state.Repos |> Map.tryFind repoId |> Option.bind _.Provider |> Option.isSome

            if not alreadyDetected then
                let! remoteUrl = PrStatus.getRemoteUrl root upstreamRemote
                let provider = remoteUrl |> Option.bind PrStatus.detectProvider |> Option.map PrStatus.toRepoProvider |> Option.defaultValue UnknownProvider
                agent.Post(UpdateProvider(repoId, Some provider))

        | RefreshGit(repoId, path) ->
            let! gitData = reloadGitData agent repoId path
            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.tryFind repoId |> Option.defaultValue PerRepoState.empty
            let repoRoot = rootPaths |> Map.find repoId
            AutoSync.triggerInBackground
                (autoSyncDependencies
                    agent
                    services.SessionAgent
                    services.ActivityStore
                    (Some services.AutoSyncStore))
                repoRoot
                repo.UpstreamRemote
                repo.BaseBranch
                (PrStatus.tryLookupPrStatus repo.PrData (GitWorktree.prBranchName gitData)
                 |> Option.defaultValue NoPr)
                gitData

            DiffProvisioner.provisionViewer path
            |> Option.iter (Log.log "DiffProvisioner")

            let! canvasDocs = CanvasScanner.scan path
            let branch = Path.GetFileName(path)
            let previous = repo.CanvasData |> Map.tryFind path |> Option.defaultValue []
            let prevNames = previous |> List.map _.Filename |> Set.ofList
            let currNames = canvasDocs |> List.map _.Filename |> Set.ofList
            let added = Set.difference currNames prevNames
            let removed = Set.difference prevNames currNames
            let changed =
                canvasDocs
                |> List.filter (fun doc ->
                    previous |> List.exists (fun prev -> prev.Filename = doc.Filename && prev.ContentHash <> doc.ContentHash))
                |> List.map _.Filename
            if not (Set.isEmpty added) then
                let names = added |> String.concat ", "
                Log.log "CanvasScanner" $"Added in {branch}: {names}"
            if not (Set.isEmpty removed) then
                let names = removed |> String.concat ", "
                Log.log "CanvasScanner" $"Removed from {branch}: {names}"
            if not (List.isEmpty changed) then
                let names = changed |> String.concat ", "
                Log.log "CanvasScanner" $"Changed in {branch}: {names}"
            agent.Post(UpdateCanvasDoc(repoId, path, canvasDocs))

        | RefreshBeads(repoId, path) ->
            let! (beads, planning) = BeadsStatus.getBeadsData path
            agent.Post(UpdateBeads(repoId, path, beads, planning))

            BeadspaceProvisioner.provisionDashboard path beads
            |> Option.iter (Log.log "BeadspaceProvisioner")

        | RefreshPr repoId ->
            let root = rootPaths |> Map.find repoId
            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.tryFind repoId |> Option.defaultValue PerRepoState.empty

            let ignorePredicate =
                GlobalConfig.readIgnoreWorktreePatterns () |> GlobalConfig.buildIgnorePredicate

            let branchScope =
                mergedPrBranchScope
                    (ignoredPathsFor ignorePredicate repo)
                    (archivedPathsFor (TreemonConfig.readArchivedBranchSet (Some root)) repo)
                    repo

            let! livePrObservations =
                PrStatus.fetchPrStatusesByRepoRoot root repo.UpstreamRemote branchScope.KnownBranches

            let livePrMap = livePrObservations |> Map.map (fun _ (status, _) -> status)

            let liveHeadShas =
                livePrObservations
                |> Map.toSeq
                |> Seq.choose (fun (branch, (_, headSha)) ->
                    headSha
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.map (fun sha -> branch, sha))
                |> Map.ofSeq

            let! persisted = MergedPrStore.getForRepo services.MergedPrStore repoId

            let worktreeHeads =
                branchScope.GitData
                |> Map.values
                |> Seq.choose (fun gitData ->
                    match GitWorktree.prBranchName gitData with
                    | Some branch when gitData.HeadCommit <> "" -> Some(branch, gitData.HeadCommit)
                    | _ -> None)
                |> Seq.groupBy fst
                |> Seq.map (fun (branch, pairs) -> branch, pairs |> Seq.map snd |> Set.ofSeq)
                |> Map.ofSeq

            let effectiveMap, newPersisted =
                MergedPrStore.reconcileMergedPrs
                    livePrMap
                    liveHeadShas
                    persisted
                    worktreeHeads
                    branchScope.PruneBranches

            if newPersisted <> persisted then
                MergedPrStore.setForRepo services.MergedPrStore repoId newPersisted

            deactivateMergedAutoSync services.AutoSyncStore root effectiveMap branchScope.GitData
            agent.Post(UpdatePr(repoId, effectiveMap))

        | RefreshFetch repoId ->
            let root = rootPaths |> Map.find repoId
            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.tryFind repoId |> Option.defaultValue PerRepoState.empty
            do! GitWorktree.fetchUpstream root repo.UpstreamRemote repo.BaseBranch
    }

let private timeoutMs = 60_000

let private executeWithTimeout
    (agent: MailboxProcessor<StateMsg>)
    (services: SchedulerServices)
    (rootPaths: Map<RepoId, string>)
    (task: RefreshTask)
    =
    async {
        let sw = Stopwatch.StartNew()

        try
            let! child = Async.StartChild(executeTask agent services rootPaths task, timeoutMs)
            do! child
            sw.Stop()
            return Ok sw.Elapsed
        with
        | :? TimeoutException ->
            sw.Stop()
            return Error $"Timed out after {timeoutMs}ms"
        | ex ->
            sw.Stop()
            return Error ex.Message
    }

let private logTaskResult (agent: MailboxProcessor<StateMsg>) (task: RefreshTask) (result: Result<TimeSpan, string>) =
    let source, target = taskLabel task

    let status, duration, message =
        match result with
        | Ok elapsed ->
            Some StepStatus.Succeeded,
            Some elapsed,
            target
        | Error msg ->
            Some(StepStatus.Failed msg),
            None,
            target

    agent.Post(
        LogSchedulerEvent
            { Source = source
              Message = message
              Timestamp = DateTimeOffset.Now
              Status = status
              Duration = duration })

    match result with
    | Ok elapsed ->
        Log.log "Scheduler" $"{source} {target} completed in {elapsed.TotalMilliseconds:F0}ms"
    | Error msg ->
        Log.log "Scheduler" $"{source} {target} failed: {msg}"

let private runPhase
    (agent: MailboxProcessor<StateMsg>)
    (services: SchedulerServices)
    (rootPaths: Map<RepoId, string>)
    (tasks: RefreshTask list)
    =
    async {
        let now = DateTimeOffset.UtcNow

        let! results =
            tasks
            |> List.map (fun task ->
                async {
                    let! result = executeWithTimeout agent services rootPaths task
                    logTaskResult agent task result
                    return task, now
                })
            |> Async.Parallel

        return results |> Array.toList
    }

let runInitialBurst
    (agent: MailboxProcessor<StateMsg>)
    (services: SchedulerServices)
    (rootPaths: Map<RepoId, string>)
    =
    async {
        Log.log "Scheduler" "Starting initial burst — Phase 1 (discover worktrees)"
        let phase1Tasks = buildPhase1Tasks rootPaths
        let! phase1Runs = runPhase agent services rootPaths phase1Tasks

        let! state = agent.PostAndAsyncReply(GetState)
        let archivedBranchSets = readArchivedBranchSets rootPaths
        let archivedPaths = resolveArchivedPaths archivedBranchSets state.Repos
        let ignorePredicate = GlobalConfig.readIgnoreWorktreePatterns () |> GlobalConfig.buildIgnorePredicate
        let ignoredPaths = resolveIgnoredPaths ignorePredicate state.Repos
        let filters = { Archived = archivedPaths; Ignored = ignoredPaths }
        Log.log "Scheduler" "Starting initial burst — Phase 2 (local data + fetch)"
        let phase2Tasks = buildPhase2Tasks filters state.Repos
        let! phase2Runs = runPhase agent services rootPaths phase2Tasks

        let! state = agent.PostAndAsyncReply(GetState)
        Log.log "Scheduler" "Starting initial burst — Phase 3 (PR data)"
        let phase3Tasks = buildPhase3Tasks state.Repos
        let! phase3Runs = runPhase agent services rootPaths phase3Tasks

        Log.log "Scheduler" "Initial burst complete"

        return
            [ phase1Runs; phase2Runs; phase3Runs ]
            |> List.collect id
            |> Map.ofList
    }

let pickMostOverdue (activity: ActivityLevel) (now: DateTimeOffset) (lastRuns: Map<RefreshTask, DateTimeOffset>) (tasks: RefreshTask list) =
    tasks
    |> List.filter (fun task -> deadlineOf activity lastRuns task <= now)
    |> List.sortBy (deadlineOf activity lastRuns)
    |> List.tryHead

let computeSleepMs (activity: ActivityLevel) (now: DateTimeOffset) (lastRuns: Map<RefreshTask, DateTimeOffset>) (tasks: RefreshTask list) =
    tasks
    |> List.map (fun task ->
        let deadline = deadlineOf activity lastRuns task
        (deadline - now).TotalMilliseconds |> int)
    |> List.fold min Int32.MaxValue
    |> max 100

let buildRootPaths (worktreeRoots: string list) =
    worktreeRoots
    |> List.map (fun root -> PathUtils.toRepoId root, root)
    |> Map.ofList

module CanvasWatchers =
    /// Fallback attribution target for a worktree's scanner. Explicit `/api/canvas/attribute`
    /// declarations are the primary attribution path; the scanner only fills the gap for docs
    /// with no declared owner, and only when it can do so *unambiguously* — i.e. exactly one
    /// **live** session is registered for the worktree. Zero or many live sessions (or a single
    /// anonymous `SessionId = None` registration) leave the doc unowned. This replaces the
    /// previous last-registered attribution (`getSessionForWorktree`) that credited every
    /// changed doc to whichever session registered last — the misattribution bug that
    /// cross-credited docs whenever two sessions shared a worktree.
    ///
    /// Liveness is part of the rule, not a detail: `sessionRegistry` never evicts entries, so
    /// without this filter "exactly one session" is permanently false for any worktree that has
    /// hosted a second session since the server started — silently disabling the fallback rather
    /// than making it ambiguous.
    let fallbackOwner (now: DateTime) (sessions: SessionBridge.SessionEntry list) : string option =
        match sessions |> List.filter (SessionBridge.isSessionAlive now) with
        | [ single ] -> single.SessionId
        | _ -> None

    /// Apply fallback-only scanner attribution for a batch of (re-)scanned docs. An AgentDoc is
    /// attributed to the worktree's single live registered session only when it is new-or-changed
    /// (relative to the watcher's previous baseline) *and* has no declared owner. Ownership is
    /// surfaced as `CanvasDoc.OwnerSessionId` by the scan, so an AgentDoc that already has an owner —
    /// declared via the endpoint or previously attributed — is skipped: the scanner never
    /// overwrites it. SystemViews never participate. With zero or many live sessions,
    /// nothing is attributed.
    let attributeChangedDocs
        (now: DateTime)
        (sessions: SessionBridge.SessionEntry list)
        (worktreePath: string)
        (previousDocs: CanvasDoc list)
        (currentDocs: CanvasDoc list)
        =
        match fallbackOwner now sessions with
        | None -> ()
        | Some sessionId ->
            let prevByName = previousDocs |> List.map (fun d -> d.Filename, d.ContentHash) |> Map.ofList
            currentDocs
            |> List.iter (fun doc ->
                let isNewOrChanged =
                    match prevByName |> Map.tryFind doc.Filename with
                    | None -> true
                    | Some prevHash -> prevHash <> doc.ContentHash
                if isNewOrChanged && Option.isNone doc.OwnerSessionId then
                    match doc.Kind with
                    | AgentDoc -> CanvasDocOwnership.attribute worktreePath doc.Filename sessionId
                    | SystemView -> ())

    let reconcile
        (agent: MailboxProcessor<StateMsg>)
        (repos: Map<RepoId, PerRepoState>)
        (current: Map<string, FileSystemWatcher>)
        =
        async {
            let allPaths =
                repos
                |> Map.toSeq
                |> Seq.collect (fun (_, repo) -> repo.KnownPaths)
                |> Set.ofSeq

            if repos |> Map.forall (fun _ repo -> repo.IsReady) then
                do! CanvasDocOwnership.prune allPaths
                do! WorktreeDiffApi.prune allPaths

            let removed =
                current
                |> Map.filter (fun path _ -> not (Set.contains path allPaths))

            removed |> Map.iter (fun path watcher ->
                try watcher.Dispose()
                with _ -> ()
                Log.log "CanvasWatcher" $"Disposed watcher for {Path.GetFileName(path)}")

            let surviving = current |> Map.filter (fun path _ -> Set.contains path allPaths)

            let repoIdByPath =
                repos
                |> Map.toSeq
                |> Seq.collect (fun (repoId, repo) -> repo.KnownPaths |> Seq.map (fun p -> p, repoId))
                |> Map.ofSeq

            let newPaths = Set.difference allPaths (current |> Map.keys |> Set.ofSeq)

            let! added =
                newPaths
                |> Set.toList
                |> List.map (fun path ->
                    async {
                        let repoId = repoIdByPath |> Map.find path
                        // Track previous docs per-watcher to diff on each callback.
                        // ref cell is isolated per closure — not shared across watchers.
                        let! initialDocs = CanvasScanner.scan path
                        let previousDocs = ref initialDocs
                        let post (canvasDocs: CanvasDoc list) =
                            // Unsynchronized read-compute-write of previousDocs: handleEvent does Async.Start,
                            // so two rapid FS events for this path can race the baseline. Tolerated, not locked —
                            // attribution is idempotent (worst case over-attribution, never loss) and a stale
                            // baseline self-heals on the next watcher event / periodic RefreshGit.
                            let prev = previousDocs.Value
                            // Fallback-only attribution: explicit /api/canvas/attribute declarations are the
                            // primary path. The scanner only attributes a no-owner changed doc when exactly one
                            // LIVE session is registered for the worktree — never the old last-registered guess
                            // that misattributed every changed doc whenever two sessions shared a worktree.
                            attributeChangedDocs DateTime.UtcNow (SessionBridge.sessionsForWorktree path) path prev canvasDocs
                            previousDocs.Value <- canvasDocs
                            agent.Post(UpdateCanvasDoc(repoId, path, canvasDocs))
                        return
                            CanvasScanner.tryCreateWatcher post path
                            |> Option.map (fun watcher ->
                                Log.log "CanvasWatcher" $"Created watcher for {Path.GetFileName(path)}"
                                path, watcher)
                    })
                |> Async.Sequential

            let added =
                added |> Array.toList |> List.choose id |> Map.ofList

            return Map.fold (fun acc k v -> Map.add k v acc) surviving added
        }

    let disposeAll (watchers: Map<string, FileSystemWatcher>) =
        watchers |> Map.iter (fun _ watcher ->
            try watcher.Dispose() with _ -> ())

let run
    (agent: MailboxProcessor<StateMsg>)
    (services: SchedulerServices)
    (worktreeRoots: string list)
    (ct: CancellationToken)
    =
    let rootPaths = buildRootPaths worktreeRoots

    let initialRepos =
        rootPaths
        |> Map.map (fun _ _ -> PerRepoState.empty)

    rootPaths
    |> Map.iter (fun repoId _ ->
        agent.Post(InitializeRepo repoId))

    // The registration owns the watcher set for the current recursive state. Reconciliation replaces
    // the registration immutably, so cancellation always disposes the latest set without a shared cell.
    let registerWatcherCleanup watchers =
        ct.Register(fun () -> CanvasWatchers.disposeAll watchers)

    let recoverIteration (ex: exn) =
        async {
            Log.log "Scheduler" $"Refresh iteration failed, continuing with last snapshot: {ex.Message}"
            do! Async.Sleep 5000
        }

    let prepareIteration
        (watchers: Map<string, FileSystemWatcher>)
        (watcherCleanup: CancellationTokenRegistration)
        =
        async {
            try
                let! state = agent.PostAndAsyncReply(GetState)

                let repos =
                    if Map.isEmpty state.Repos then initialRepos
                    else state.Repos

                let! nextWatchers = CanvasWatchers.reconcile agent repos watchers
                watcherCleanup.Dispose()
                let nextCleanup = registerWatcherCleanup nextWatchers
                return Some(state, repos, nextWatchers, nextCleanup)
            with ex ->
                do! recoverIteration ex
                return None
        }

    let runPreparedIteration
        state
        repos
        watchers
        watcherCleanup
        lastRuns
        =
        async {
            try
                let archivedBranchSets = readArchivedBranchSets rootPaths
                let archivedPaths = resolveArchivedPaths archivedBranchSets repos
                let ignorePredicate = GlobalConfig.readIgnoreWorktreePatterns () |> GlobalConfig.buildIgnorePredicate
                let ignoredPaths = resolveIgnoredPaths ignorePredicate repos
                let tasks = buildTaskList { Archived = archivedPaths; Ignored = ignoredPaths } repos
                let now = DateTimeOffset.UtcNow
                let activity = effectiveActivity now state

                let effectiveLastRuns =
                    tasks
                    |> List.fold (fun runs task ->
                        match task with
                        | RefreshWorktreeList repoId when Set.contains repoId state.ExpeditedRepos ->
                            runs |> Map.remove task
                        | _ -> runs) lastRuns

                match pickMostOverdue activity now effectiveLastRuns tasks with
                | Some task ->
                    let! result = executeWithTimeout agent services rootPaths task
                    logTaskResult agent task result

                    match task with
                    | RefreshWorktreeList repoId when Set.contains repoId state.ExpeditedRepos ->
                        agent.Post(ClearExpedite repoId)
                    | _ -> ()

                    let updatedRuns = lastRuns |> Map.add task now
                    return updatedRuns, watchers, watcherCleanup
                | None ->
                    let sleepMs = computeSleepMs activity now effectiveLastRuns tasks
                    do! Async.Sleep sleepMs
                    return lastRuns, watchers, watcherCleanup
            with ex ->
                do! recoverIteration ex
                return lastRuns, watchers, watcherCleanup
        }

    let rec loop
        (lastRuns: Map<RefreshTask, DateTimeOffset>)
        (watchers: Map<string, FileSystemWatcher>)
        (watcherCleanup: CancellationTokenRegistration)
        =
        async {
            let! prepared = prepareIteration watchers watcherCleanup

            match prepared with
            | None ->
                return! loop lastRuns watchers watcherCleanup
            | Some(state, repos, nextWatchers, nextCleanup) ->
                let! nextRuns, nextWatchers, nextCleanup =
                    runPreparedIteration
                        state
                        repos
                        nextWatchers
                        nextCleanup
                        lastRuns

                return! loop nextRuns nextWatchers nextCleanup
        }

    let startup =
        async {
            let! lastRuns = runInitialBurst agent services rootPaths
            let! state = agent.PostAndAsyncReply(GetState)
            let! initialWatchers = CanvasWatchers.reconcile agent state.Repos Map.empty
            let watcherCleanup = registerWatcherCleanup initialWatchers
            return! loop lastRuns initialWatchers watcherCleanup
        }

    startup
