module Server.WorktreeApi

open System
open System.IO
open Shared
open Shared.EventUtils
open Newtonsoft.Json

type FixtureData =
    { Worktrees: DashboardResponse
      SyncStatus: Map<string, CardEvent list> }

let loadFixtures (path: string) =
    let json = File.ReadAllText(path)
    let converter = Fable.Remoting.Json.FableJsonConverter()
    JsonConvert.DeserializeObject<FixtureData>(json, converter)

let private assembleFromState
    (activeSessions: Set<string>)
    (archivedBranches: Set<string>)
    (repo: RefreshScheduler.PerRepoState)
    (wt: GitWorktree.WorktreeInfo)
    =
    let gitData = repo.GitData |> Map.tryFind wt.Path
    let beads = repo.BeadsData |> Map.tryFind wt.Path |> Option.defaultValue BeadsSummary.zero
    let codingToolStatus, codingToolProvider, lastUserMsg =
        repo.CodingToolData
        |> Map.tryFind wt.Path
        |> Option.map (fun (status, provider, userMsg) -> status, provider, userMsg)
        |> Option.defaultValue (CodingToolStatus.Idle, None, None)
    let upstreamBranch = gitData |> Option.bind _.UpstreamBranch
    let pr = PrStatus.lookupPrStatus repo.PrData upstreamBranch

    { Path = wt.Path
      Branch = wt.Branch |> Option.defaultValue "(detached)"
      LastCommitMessage = gitData |> Option.map (_.LastCommitMessage) |> Option.defaultValue ""
      LastCommitTime = gitData |> Option.map (_.LastCommitTime) |> Option.defaultValue DateTimeOffset.MinValue
      Beads = beads
      CodingTool = codingToolStatus
      CodingToolProvider = codingToolProvider
      LastUserMessage = lastUserMsg
      Pr = pr
      MainBehindCount = gitData |> Option.map (_.MainBehindCount) |> Option.defaultValue 0
      IsDirty = gitData |> Option.map (_.IsDirty) |> Option.defaultValue false
      WorkMetrics = gitData |> Option.bind _.WorkMetrics
      HasActiveSession = Set.contains wt.Path activeSessions
      IsArchived =
        wt.Branch
        |> Option.map (fun b -> Set.contains b archivedBranches)
        |> Option.defaultValue false }

let private allWorktrees (state: RefreshScheduler.DashboardState) =
    state.Repos
    |> Map.values
    |> Seq.collect _.WorktreeList
    |> Seq.toList

let private allKnownPaths (state: RefreshScheduler.DashboardState) =
    state.Repos
    |> Map.values
    |> Seq.collect _.KnownPaths
    |> Set.ofSeq

let private findRepoForPath (state: RefreshScheduler.DashboardState) (path: string) =
    state.Repos
    |> Map.tryPick (fun repoId repo ->
        if Set.contains path repo.KnownPaths then Some repoId
        else None)

let private scopedBranchKey (repoId: RepoId) (branch: string) = $"{RepoId.value repoId}/{branch}"

let getWorktrees
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (sessionAgent: SessionManager.SessionAgent)
    (rootPaths: Map<RepoId, string>)
    (appVersion: string)
    : Async<DashboardResponse> =
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
        let! activeSessions = SessionManager.getActiveSessions sessionAgent

        let activeSessionPaths = activeSessions |> Map.keys |> Set.ofSeq

        let repos =
            state.Repos
            |> Map.toList
            |> List.map (fun (repoId, repo) ->
                let repoRoot = rootPaths |> Map.tryFind repoId

                let liveBranches =
                    repo.WorktreeList
                    |> List.choose _.Branch
                    |> Set.ofList

                let archivedBranches =
                    repoRoot
                    |> Option.map TreemonConfig.readArchivedBranches
                    |> Option.defaultValue []
                    |> Set.ofList

                let cleanedArchived = Set.intersect archivedBranches liveBranches

                if cleanedArchived <> archivedBranches then
                    repoRoot |> Option.iter (fun root ->
                        TreemonConfig.setArchivedBranches root (Set.toList cleanedArchived))

                let statuses =
                    repo.WorktreeList
                    |> List.map (assembleFromState activeSessionPaths cleanedArchived repo)

                { RepoId = repoId
                  RootFolderName = Path.GetFileName(RepoId.value repoId)
                  Worktrees = statuses
                  IsReady = repo.IsReady })

        return
            { Repos = repos
              SchedulerEvents = mergeWithPinnedErrors state.SchedulerEvents state.PinnedErrors
              LatestByCategory = state.LatestByCategory
              AppVersion = appVersion }
    }

let private openTerminal
    (validatePath: string -> Async<bool>)
    (sessionAgent: SessionManager.SessionAgent)
    (path: string)
    =
    async {
        let! isValid = validatePath path

        if not isValid then
            Log.log "API" $"openTerminal: rejected unknown path '{path}'"
        else
            Log.log "API" $"openTerminal: launching terminal for '{path}'"
            let! result = SessionManager.spawnTerminal sessionAgent path

            match result with
            | Ok () -> ()
            | Error msg -> Log.log "API" $"openTerminal: failed for '{path}': {msg}"
    }

let private deleteWorktree
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (branch: string)
    =
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
        let worktrees = allWorktrees state

        let worktree =
            worktrees
            |> List.tryFind (fun wt -> wt.Branch = Some branch)

        match worktree with
        | None -> return Error $"No worktree found for branch '{branch}'"
        | Some wt ->
            let repoId = findRepoForPath state wt.Path

            let repoRoot =
                repoId
                |> Option.bind (fun rid -> rootPaths |> Map.tryFind rid)

            match repoId, repoRoot with
            | Some rid, Some root ->
                agent.Post(RefreshScheduler.StateMsg.RemoveWorktree(rid, wt.Path))
                let! result = GitWorktree.removeWorktree root wt.Path branch
                return result
            | _ ->
                return Error $"Could not identify repo root for worktree at '{wt.Path}'"
    }

let private findRepoRootForBranch
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (branch: string)
    =
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
        let worktrees = allWorktrees state

        let worktree =
            worktrees
            |> List.tryFind (fun wt -> wt.Branch = Some branch)

        match worktree with
        | None -> return Error $"No worktree found for branch '{branch}'"
        | Some wt ->
            let repoRoot =
                findRepoForPath state wt.Path
                |> Option.bind (fun rid -> rootPaths |> Map.tryFind rid)

            match repoRoot with
            | Some root -> return Ok root
            | None -> return Error $"Could not identify repo root for worktree at '{wt.Path}'"
    }

let private archiveWorktree
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (branch: string)
    =
    async {
        let! repoRoot = findRepoRootForBranch agent rootPaths branch

        match repoRoot with
        | Error msg -> return Error msg
        | Ok root ->
            let existing = TreemonConfig.readArchivedBranches root |> Set.ofList

            if Set.contains branch existing then
                return Ok ()
            else
                let updated = Set.add branch existing |> Set.toList
                TreemonConfig.setArchivedBranches root updated
                return Ok ()
    }

let private unarchiveWorktree
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (branch: string)
    =
    async {
        let! repoRoot = findRepoRootForBranch agent rootPaths branch

        match repoRoot with
        | Error msg -> return Error msg
        | Ok root ->
            let existing = TreemonConfig.readArchivedBranches root |> Set.ofList
            let updated = Set.remove branch existing |> Set.toList
            TreemonConfig.setArchivedBranches root updated
            return Ok ()
    }

let worktreeApi
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (syncAgent: MailboxProcessor<SyncEngine.SyncMsg>)
    (sessionAgent: SessionManager.SessionAgent)
    (worktreeRoots: string list)
    (testFixtures: string option)
    (appVersion: string)
    : IWorktreeApi =
    let fixtures = testFixtures |> Option.map loadFixtures

    let rootPaths = RefreshScheduler.buildRootPaths worktreeRoots

    let validatePath path =
        async {
            let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
            let knownPaths = allKnownPaths state
            return Set.contains path knownPaths
        }

    let withValidatedPath path opName (action: unit -> Async<Result<unit, string>>) =
        async {
            let! isValid = validatePath path

            if not isValid then
                Log.log "API" $"{opName}: rejected unknown path '{path}'"
                return Error $"Unknown worktree path: {path}"
            else
                return! action ()
        }

    match fixtures with
    | Some f ->
        { getWorktrees = fun () -> async { return f.Worktrees }
          openTerminal = fun _ -> async { return () }
          startSync = fun _ -> async { return Error "Sync is not available in fixture mode" }
          cancelSync = fun _ -> async { return () }
          getSyncStatus = fun () -> async { return f.SyncStatus }
          deleteWorktree = fun _ -> async { return Error "Delete is not available in fixture mode" }
          launchSession = fun _ -> async { return Error "Session management is not available in fixture mode" }
          focusSession = fun _ -> async { return Error "Session management is not available in fixture mode" }
          killSession = fun _ -> async { return Error "Session management is not available in fixture mode" }
          archiveWorktree = fun _ -> async { return Error "Archive is not available in fixture mode" }
          unarchiveWorktree = fun _ -> async { return Error "Archive is not available in fixture mode" }
          openNewTab = fun _ -> async { return Error "Session management is not available in fixture mode" } }
    | None ->
        { getWorktrees = fun () -> getWorktrees agent sessionAgent rootPaths appVersion
          openTerminal = openTerminal validatePath sessionAgent
          startSync = fun branch ->
              async {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  let worktreeWithRepo =
                      state.Repos
                      |> Map.toList
                      |> List.tryPick (fun (repoId, repo) ->
                          repo.WorktreeList
                          |> List.tryFind (fun wt -> wt.Branch = Some branch)
                          |> Option.map (fun wt ->
                              let repoRoot =
                                  rootPaths
                                  |> Map.tryFind repoId
                                  |> Option.defaultValue (worktreeRoots |> List.head)
                              let syncKey = scopedBranchKey repoId branch
                              let provider =
                                  repo.CodingToolData
                                  |> Map.tryFind wt.Path
                                  |> Option.bind (fun (_, p, _) -> p)
                              wt.Path, repoRoot, syncKey, provider))

                  match worktreeWithRepo with
                  | None -> return Error $"No worktree found for branch '{branch}'"
                  | Some (path, repoRoot, syncKey, provider) ->
                      let! beginResult = syncAgent.PostAndAsyncReply(fun reply -> SyncEngine.BeginSync (syncKey, reply))

                      match beginResult with
                      | Error msg -> return Error msg
                      | Ok ct ->
                          let post = syncAgent.Post
                          Async.Start(SyncEngine.executeSyncPipeline post syncKey path repoRoot provider ct, ct)
                          return Ok ()
              }
          cancelSync = fun branch ->
              async {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  state.Repos
                  |> Map.toList
                  |> List.tryPick (fun (repoId, repo) ->
                      repo.WorktreeList
                      |> List.tryFind (fun wt -> wt.Branch = Some branch)
                      |> Option.map (fun _ -> scopedBranchKey repoId branch))
                  |> Option.iter (fun key -> syncAgent.Post(SyncEngine.CancelSync key))
              }
          getSyncStatus = fun () ->
              async {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  let branchToScopedKey =
                      state.Repos
                      |> Map.toList
                      |> List.collect (fun (repoId, repo) ->
                          repo.WorktreeList
                          |> List.choose (fun wt ->
                              wt.Branch |> Option.map (fun b ->
                                  let key = scopedBranchKey repoId b
                                  key, wt.Path)))
                      |> Map.ofList

                  let! syncEvents = syncAgent.PostAndAsyncReply(SyncEngine.GetAllEvents)

                  let allKeys =
                      [ yield! syncEvents |> Map.keys
                        yield! branchToScopedKey |> Map.keys ]
                      |> List.distinct

                  return
                      allKeys
                      |> List.choose (fun key ->
                          let syncEvts =
                              syncEvents
                              |> Map.tryFind key
                              |> Option.defaultValue []

                          let claudeEvt =
                              branchToScopedKey
                              |> Map.tryFind key
                              |> Option.bind CodingToolStatus.getLastMessage

                          let merged =
                              match claudeEvt with
                              | Some evt -> evt :: syncEvts
                              | None -> syncEvts

                          match merged with
                          | [] -> None
                          | events ->
                              let recent =
                                  events
                                  |> List.sortByDescending _.Timestamp
                                  |> List.truncate 2
                                  |> List.rev

                              Some(key, recent))
                      |> Map.ofList
              }
          deleteWorktree = deleteWorktree agent rootPaths
          launchSession = fun req ->
              withValidatedPath req.Path "launchSession" (fun () ->
                  SessionManager.spawnSession sessionAgent req.Path req.Prompt)
          focusSession = fun path ->
              withValidatedPath path "focusSession" (fun () ->
                  SessionManager.focusSession sessionAgent path)
          killSession = fun path ->
              withValidatedPath path "killSession" (fun () ->
                  SessionManager.killSession sessionAgent path)
          archiveWorktree = archiveWorktree agent rootPaths
          unarchiveWorktree = unarchiveWorktree agent rootPaths
          openNewTab = fun path ->
              withValidatedPath path "openNewTab" (fun () ->
                  SessionManager.openNewTab sessionAgent path) }
