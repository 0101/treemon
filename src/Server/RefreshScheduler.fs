module Server.RefreshScheduler

open System
open System.Diagnostics
open System.IO
open System.Threading
open Shared
open Shared.EventUtils

type PerRepoState =
    { WorktreeList: GitWorktree.WorktreeInfo list
      KnownPaths: Set<string>
      GitData: Map<string, GitWorktree.GitData>
      BeadsData: Map<string, BeadsSummary>
      CodingToolData: Map<string, CodingToolStatus * CodingToolProvider option * (string * DateTimeOffset) option>
      PrData: Map<string, PrStatus>
      IsReady: bool }

module PerRepoState =
    let empty =
        { WorktreeList = []
          KnownPaths = Set.empty
          GitData = Map.empty
          BeadsData = Map.empty
          CodingToolData = Map.empty
          PrData = Map.empty
          IsReady = false }

type DashboardState =
    { Repos: Map<RepoId, PerRepoState>
      SchedulerEvents: CardEvent list
      PinnedErrors: Map<string * string, CardEvent>
      LatestByCategory: Map<string, CardEvent>
      ExpeditedRepos: Set<RepoId> }

module DashboardState =
    let empty =
        { Repos = Map.empty
          SchedulerEvents = []
          PinnedErrors = Map.empty
          LatestByCategory = Map.empty
          ExpeditedRepos = Set.empty }

type StateMsg =
    | UpdateWorktreeList of repoId: RepoId * GitWorktree.WorktreeInfo list
    | UpdateGit of repoId: RepoId * path: string * GitWorktree.GitData
    | UpdateBeads of repoId: RepoId * path: string * BeadsSummary
    | UpdateCodingTool of repoId: RepoId * path: string * (CodingToolStatus * CodingToolProvider option * (string * DateTimeOffset) option)
    | UpdatePr of repoId: RepoId * Map<string, PrStatus>
    | RemoveWorktree of repoId: RepoId * path: string
    | GetState of AsyncReplyChannel<DashboardState>
    | LogSchedulerEvent of CardEvent
    | ExpediteRefresh of RepoId
    | ClearExpedite of RepoId

let private maxEvents = 50

let private trimEvents (events: CardEvent list) =
    events
    |> List.sortByDescending _.Timestamp
    |> List.truncate maxEvents

let private updatePinnedErrors (errors: Map<string * string, CardEvent>) (event: CardEvent) =
    let key = eventKey event
    match event.Status with
    | Some (StepStatus.Failed _) -> errors |> Map.add key event
    | Some StepStatus.Succeeded -> errors |> Map.remove key
    | _ -> errors

let private getRepo (repoId: RepoId) (state: DashboardState) =
    state.Repos
    |> Map.tryFind repoId
    |> Option.defaultValue PerRepoState.empty

let private updateRepo (repoId: RepoId) (repo: PerRepoState) (state: DashboardState) =
    { state with Repos = state.Repos |> Map.add repoId repo }

let private removeWorktreeData (path: string) (repo: PerRepoState) =
    { repo with
        WorktreeList = repo.WorktreeList |> List.filter (fun wt -> wt.Path <> path)
        GitData = repo.GitData |> Map.remove path
        BeadsData = repo.BeadsData |> Map.remove path
        CodingToolData = repo.CodingToolData |> Map.remove path }

let private processMessage (state: DashboardState) (msg: StateMsg) =
    match msg with
    | UpdateWorktreeList(repoId, worktrees) ->
        let repo = getRepo repoId state
        let newPaths = worktrees |> List.map _.Path |> Set.ofList
        let removedPaths = Set.difference repo.KnownPaths newPaths

        let cleaned =
            removedPaths
            |> Set.fold (fun r path -> removeWorktreeData path r) repo

        let updated =
            { cleaned with
                WorktreeList = worktrees
                KnownPaths = newPaths
                IsReady = true }

        updateRepo repoId updated state

    | UpdateGit(repoId, path, gitData) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId { repo with GitData = repo.GitData |> Map.add path gitData } state
        else
            state

    | UpdateBeads(repoId, path, beads) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId { repo with BeadsData = repo.BeadsData |> Map.add path beads } state
        else
            state

    | UpdateCodingTool(repoId, path, data) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId { repo with CodingToolData = repo.CodingToolData |> Map.add path data } state
        else
            state

    | UpdatePr(repoId, prMap) ->
        let repo = getRepo repoId state
        updateRepo repoId { repo with PrData = prMap } state

    | RemoveWorktree(repoId, path) ->
        let repo = getRepo repoId state
        updateRepo repoId (removeWorktreeData path repo) state

    | GetState replyChannel ->
        replyChannel.Reply(state)
        state

    | LogSchedulerEvent event ->
        { state with
            SchedulerEvents = trimEvents (event :: state.SchedulerEvents)
            PinnedErrors = updatePinnedErrors state.PinnedErrors event
            LatestByCategory = state.LatestByCategory |> Map.add event.Source event }

    | ExpediteRefresh repoId ->
        { state with ExpeditedRepos = state.ExpeditedRepos |> Set.add repoId }

    | ClearExpedite repoId ->
        { state with ExpeditedRepos = state.ExpeditedRepos |> Set.remove repoId }

let createAgent () =
    MailboxProcessor<StateMsg>.Start(fun inbox ->
        let rec loop (state: DashboardState) =
            async {
                let! msg = inbox.Receive()
                let newState = processMessage state msg
                return! loop newState
            }

        loop DashboardState.empty)

type RefreshTask =
    | RefreshWorktreeList of repoId: RepoId
    | RefreshGit of repoId: RepoId * path: string
    | RefreshBeads of repoId: RepoId * path: string
    | RefreshCodingTool of repoId: RepoId * path: string
    | RefreshPr of repoId: RepoId
    | RefreshFetch of repoId: RepoId

let private taskLabel = function
    | RefreshWorktreeList repoId -> "WorktreeList", RepoId.value repoId
    | RefreshGit(repoId, path) -> "GitRefresh", $"{RepoId.value repoId}/{Path.GetFileName(path)}"
    | RefreshBeads(repoId, path) -> "BeadsRefresh", $"{RepoId.value repoId}/{Path.GetFileName(path)}"
    | RefreshCodingTool(repoId, path) -> "CodingToolRefresh", $"{RepoId.value repoId}/{Path.GetFileName(path)}"
    | RefreshPr repoId -> "PrFetch", RepoId.value repoId
    | RefreshFetch repoId -> "GitFetch", RepoId.value repoId

let private intervalOf = function
    | RefreshWorktreeList _ -> TimeSpan.FromSeconds(15.0)
    | RefreshGit _ -> TimeSpan.FromSeconds(15.0)
    | RefreshBeads _ -> TimeSpan.FromSeconds(60.0)
    | RefreshCodingTool _ -> TimeSpan.FromSeconds(15.0)
    | RefreshPr _ -> TimeSpan.FromSeconds(120.0)
    | RefreshFetch _ -> TimeSpan.FromSeconds(120.0)

let buildTaskList (repos: Map<RepoId, PerRepoState>) =
    let repoList = repos |> Map.toList

    let worktreeLists =
        repoList |> List.map (fun (repoId, _) -> RefreshWorktreeList repoId)

    let localTasks =
        repoList
        |> List.collect (fun (repoId, repo) ->
            repo.WorktreeList
            |> List.collect (fun wt ->
                [ RefreshGit(repoId, wt.Path)
                  RefreshBeads(repoId, wt.Path)
                  RefreshCodingTool(repoId, wt.Path) ]))

    let networkTasks =
        repoList
        |> List.collect (fun (repoId, _) ->
            [ RefreshPr repoId; RefreshFetch repoId ])

    worktreeLists @ localTasks @ networkTasks

let buildPhase1Tasks (rootPaths: Map<RepoId, string>) =
    rootPaths |> Map.toList |> List.map (fun (repoId, _) -> RefreshWorktreeList repoId)

let buildPhase2Tasks (repos: Map<RepoId, PerRepoState>) =
    repos
    |> Map.toList
    |> List.collect (fun (repoId, repo) ->
        let perWorktree =
            repo.WorktreeList
            |> List.collect (fun wt ->
                [ RefreshGit(repoId, wt.Path)
                  RefreshBeads(repoId, wt.Path)
                  RefreshCodingTool(repoId, wt.Path) ])

        RefreshFetch repoId :: perWorktree)

let buildPhase3Tasks (repos: Map<RepoId, PerRepoState>) =
    repos |> Map.toList |> List.map (fun (repoId, _) -> RefreshPr repoId)

let private deadlineOf (lastRuns: Map<RefreshTask, DateTimeOffset>) (task: RefreshTask) =
    lastRuns
    |> Map.tryFind task
    |> Option.map (fun t -> t + intervalOf task)
    |> Option.defaultValue DateTimeOffset.MinValue

let private executeTask
    (agent: MailboxProcessor<StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (task: RefreshTask)
    =
    async {
        match task with
        | RefreshWorktreeList repoId ->
            let root = rootPaths |> Map.find repoId
            let! worktrees = GitWorktree.listWorktrees root
            agent.Post(UpdateWorktreeList(repoId, worktrees))

        | RefreshGit(repoId, path) ->
            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.tryFind repoId |> Option.defaultValue PerRepoState.empty

            let branch =
                repo.WorktreeList
                |> List.tryFind (fun wt -> wt.Path = path)
                |> Option.bind _.Branch

            let! gitData = GitWorktree.collectWorktreeGitData path branch
            agent.Post(UpdateGit(repoId, path, gitData))

        | RefreshBeads(repoId, path) ->
            let! beads = BeadsStatus.getBeadsSummary path
            agent.Post(UpdateBeads(repoId, path, beads))

        | RefreshCodingTool(repoId, path) ->
            let status, provider = CodingToolStatus.getStatus path
            let lastUserMsg = CodingToolStatus.getLastUserMessage path provider
            agent.Post(UpdateCodingTool(repoId, path, (status, provider, lastUserMsg)))

        | RefreshPr repoId ->
            let root = rootPaths |> Map.find repoId
            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.tryFind repoId |> Option.defaultValue PerRepoState.empty

            let knownBranches =
                repo.GitData
                |> Map.values
                |> Seq.choose _.UpstreamBranch
                |> set

            let! prMap = PrStatus.fetchPrStatusesByRepoRoot root knownBranches
            agent.Post(UpdatePr(repoId, prMap))

        | RefreshFetch repoId ->
            let root = rootPaths |> Map.find repoId
            do! GitWorktree.fetchFromOrigin root
    }

let private timeoutMs = 60_000

let private executeWithTimeout
    (agent: MailboxProcessor<StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (task: RefreshTask)
    =
    async {
        let sw = Stopwatch.StartNew()

        try
            let! child = Async.StartChild(executeTask agent rootPaths task, timeoutMs)
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
    (rootPaths: Map<RepoId, string>)
    (tasks: RefreshTask list)
    =
    async {
        let now = DateTimeOffset.UtcNow

        let! results =
            tasks
            |> List.map (fun task ->
                async {
                    let! result = executeWithTimeout agent rootPaths task
                    logTaskResult agent task result
                    return task, now
                })
            |> Async.Parallel

        return results |> Array.toList
    }

let runInitialBurst (agent: MailboxProcessor<StateMsg>) (rootPaths: Map<RepoId, string>) =
    async {
        Log.log "Scheduler" "Starting initial burst — Phase 1 (discover worktrees)"
        let phase1Tasks = buildPhase1Tasks rootPaths
        let! phase1Runs = runPhase agent rootPaths phase1Tasks

        let! state = agent.PostAndAsyncReply(GetState)
        Log.log "Scheduler" "Starting initial burst — Phase 2 (local data + fetch)"
        let phase2Tasks = buildPhase2Tasks state.Repos
        let! phase2Runs = runPhase agent rootPaths phase2Tasks

        let! state = agent.PostAndAsyncReply(GetState)
        Log.log "Scheduler" "Starting initial burst — Phase 3 (PR data)"
        let phase3Tasks = buildPhase3Tasks state.Repos
        let! phase3Runs = runPhase agent rootPaths phase3Tasks

        Log.log "Scheduler" "Initial burst complete"

        return
            [ phase1Runs; phase2Runs; phase3Runs ]
            |> List.collect id
            |> Map.ofList
    }

let pickMostOverdue (now: DateTimeOffset) (lastRuns: Map<RefreshTask, DateTimeOffset>) (tasks: RefreshTask list) =
    tasks
    |> List.filter (fun task -> deadlineOf lastRuns task <= now)
    |> List.tryHead

let computeSleepMs (now: DateTimeOffset) (lastRuns: Map<RefreshTask, DateTimeOffset>) (tasks: RefreshTask list) =
    tasks
    |> List.map (fun task ->
        let deadline = deadlineOf lastRuns task
        (deadline - now).TotalMilliseconds |> int)
    |> List.fold min Int32.MaxValue
    |> max 100

let buildRootPaths (worktreeRoots: string list) =
    worktreeRoots
    |> List.map (fun root -> RepoId.create (Path.GetFullPath(root)), root)
    |> Map.ofList

let start (agent: MailboxProcessor<StateMsg>) (worktreeRoots: string list) (ct: CancellationToken) =
    let rootPaths = buildRootPaths worktreeRoots

    let initialRepos =
        rootPaths
        |> Map.map (fun _ _ -> PerRepoState.empty)

    rootPaths
    |> Map.iter (fun repoId _ ->
        agent.Post(UpdateWorktreeList(repoId, [])))

    let rec loop (lastRuns: Map<RefreshTask, DateTimeOffset>) =
        async {
            let! state = agent.PostAndAsyncReply(GetState)

            let repos =
                if Map.isEmpty state.Repos then initialRepos
                else state.Repos

            let tasks = buildTaskList repos
            let now = DateTimeOffset.UtcNow

            let effectiveLastRuns =
                tasks
                |> List.fold (fun runs task ->
                    match task with
                    | RefreshWorktreeList repoId when Set.contains repoId state.ExpeditedRepos ->
                        runs |> Map.remove task
                    | _ -> runs) lastRuns

            match pickMostOverdue now effectiveLastRuns tasks with
            | Some task ->
                let! result = executeWithTimeout agent rootPaths task
                logTaskResult agent task result

                match task with
                | RefreshWorktreeList repoId when Set.contains repoId state.ExpeditedRepos ->
                    agent.Post(ClearExpedite repoId)
                | _ -> ()

                let updatedRuns = lastRuns |> Map.add task now
                return! loop updatedRuns
            | None ->
                let sleepMs = computeSleepMs now effectiveLastRuns tasks
                do! Async.Sleep sleepMs
                return! loop lastRuns
        }

    let startup =
        async {
            let! lastRuns = runInitialBurst agent rootPaths
            return! loop lastRuns
        }

    Async.Start(startup, ct)
