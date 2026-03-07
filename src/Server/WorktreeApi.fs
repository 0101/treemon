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
        |> Option.defaultValue (CodingToolStatus.Idle, None, None)
    let upstreamBranch = gitData |> Option.bind _.UpstreamBranch
    let pr = PrStatus.lookupPrStatus repo.PrData upstreamBranch

    { Path = WorktreePath.create wt.Path
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

let private resolveProvider (state: RefreshScheduler.DashboardState) (path: string) =
    state.Repos
    |> Map.values
    |> Seq.tryPick (fun repo ->
        repo.CodingToolData
        |> Map.tryFind path
        |> Option.bind (fun (_, p, _) -> p))

let private readGlobalConfig () =
    let configPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".treemon",
            "config.json")

    if not (File.Exists(configPath)) then
        Map.empty
    else
        try
            let json = File.ReadAllText(configPath)
            use doc = System.Text.Json.JsonDocument.Parse(json)
            doc.RootElement.EnumerateObject()
            |> Seq.map (fun prop -> prop.Name, prop.Value.GetString())
            |> Map.ofSeq
        with ex ->
            Log.log "Config" $"Failed to read global config: {ex.Message}"
            Map.empty

let private getEditorConfig () =
    let config = readGlobalConfig ()
    let command = config |> Map.tryFind "editor" |> Option.defaultValue "code"
    let name =
        match config |> Map.tryFind "editorName", command with
        | Some n, _ -> n
        | None, "code" -> "VS Code"
        | None, cmd -> cmd
    command, name

let getWorktrees
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (sessionAgent: SessionManager.SessionAgent)
    (rootPaths: Map<RepoId, string>)
    (appVersion: string)
    (deployBranch: string option)
    : Async<DashboardResponse> =
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
        let! activeSessions = SessionManager.getActiveSessions sessionAgent

        let activeSessionPaths = activeSessions |> Map.keys |> Set.ofSeq

        let repos =
            state.Repos
            |> Map.toList
            |> List.map (fun (repoId, repo) ->
                let archivedBranches =
                    rootPaths
                    |> Map.tryFind repoId
                    |> TreemonConfig.readArchivedBranchSet

                let statuses =
                    repo.WorktreeList
                    |> List.map (assembleFromState activeSessionPaths archivedBranches repo)

                { RepoId = repoId
                  RootFolderName = Path.GetFileName(RepoId.value repoId)
                  Worktrees = statuses
                  IsReady = repo.IsReady })

        return
            { Repos = repos
              SchedulerEvents = mergeWithPinnedErrors state.SchedulerEvents state.PinnedErrors
              LatestByCategory = state.LatestByCategory
              AppVersion = appVersion
              DeployBranch = deployBranch
              SystemMetrics = SystemMetrics.getSystemMetrics ()
              EditorName = getEditorConfig () |> snd }
    }

let private openEditor (validatePath: string -> Async<bool>) (wtPath: WorktreePath) =
    let path = WorktreePath.value wtPath
    async {
        let! isValid = validatePath path

        if not isValid then
            Log.log "API" $"openEditor: rejected unknown path '{path}'"
        else
            let editor, _ = getEditorConfig ()
            Log.log "API" $"openEditor: opening '{editor}' for '{path}'"

            try
                let psi =
                    System.Diagnostics.ProcessStartInfo(
                        "cmd.exe",
                        $"/c {editor} \"{path}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    )

                System.Diagnostics.Process.Start(psi) |> ignore
            with ex ->
                Log.log "API" $"openEditor: failed for '{path}': {ex.Message}"
    }

let private openTerminal
    (validatePath: string -> Async<bool>)
    (sessionAgent: SessionManager.SessionAgent)
    (wtPath: WorktreePath)
    =
    let path = WorktreePath.value wtPath
    async {
        let! isValid = validatePath path

        if not isValid then
            Log.log "API" $"openTerminal: rejected unknown path '{path}'"
        else
            Log.log "API" $"openTerminal: launching terminal for '{path}'"
            let! result = SessionManager.spawnTerminal sessionAgent wtPath

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

let private updateArchivedBranches
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (setOp: string -> Set<string> -> Set<string>)
    (branchName: BranchName)
    =
    let branch = BranchName.value branchName
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
        let worktrees = allWorktrees state

        let worktree =
            worktrees
            |> List.tryFind (fun wt -> wt.Branch = Some branch)

        let repoId =
            worktree
            |> Option.bind (fun wt -> findRepoForPath state wt.Path)

        let repoRoot =
            repoId
            |> Option.bind (fun rid -> rootPaths |> Map.tryFind rid)

        match worktree, repoId, repoRoot with
        | Some _, Some rid, Some root ->
            let liveBranches =
                state.Repos
                |> Map.tryFind rid
                |> Option.map (fun repo -> repo.WorktreeList |> List.choose _.Branch |> Set.ofList)
                |> Option.defaultValue Set.empty

            TreemonConfig.modifyArchivedBranches root (fun existing ->
                existing
                |> Set.ofList
                |> setOp branch
                |> Set.intersect liveBranches
                |> Set.toList)
            agent.Post(RefreshScheduler.StateMsg.ExpediteRefresh rid)
            return Ok ()
        | Some wt, _, _ ->
            return Error $"Could not identify repo root for worktree at '{wt.Path}'"
        | None, _, _ ->
            return Error $"No worktree found for branch '{branch}'"
    }

let worktreeApi
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (syncAgent: MailboxProcessor<SyncEngine.SyncMsg>)
    (sessionAgent: SessionManager.SessionAgent)
    (worktreeRoots: string list)
    (testFixtures: string option)
    (appVersion: string)
    (deployBranch: string option)
    : IWorktreeApi =
    let fixtures = testFixtures |> Option.map loadFixtures

    let rootPaths = RefreshScheduler.buildRootPaths worktreeRoots

    let validatePath path =
        async {
            let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
            let knownPaths = allKnownPaths state
            return Set.contains path knownPaths
        }

    let withValidatedPath (wtPath: WorktreePath) opName (action: unit -> Async<Result<unit, string>>) =
        let path = WorktreePath.value wtPath
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
        { getWorktrees = fun () -> async { return { f.Worktrees with DeployBranch = None; SystemMetrics = None; EditorName = getEditorConfig () |> snd } }
          openTerminal = fun _ -> async { return () }
          openEditor = fun _ -> async { return () }
          startSync = fun _ -> async { return Error "Sync is not available in fixture mode" }
          cancelSync = fun _ -> async { return () }
          getSyncStatus = fun () -> async { return f.SyncStatus }
          deleteWorktree = fun _ -> async { return Error "Delete is not available in fixture mode" }
          launchSession = fun _ -> async { return Error "Session management is not available in fixture mode" }
          focusSession = fun _ -> async { return Error "Session management is not available in fixture mode" }
          killSession = fun _ -> async { return Error "Session management is not available in fixture mode" }
          archiveWorktree = fun _ -> async { return Error "Archive is not available in fixture mode" }
          unarchiveWorktree = fun _ -> async { return Error "Archive is not available in fixture mode" }
          getBranches = fun _ -> async { return [ "main"; "develop"; "feature/sample" ] }
          createWorktree = fun _ -> async { return Ok() }
          openNewTab = fun _ -> async { return Error "Session management is not available in fixture mode" }
          launchAction = fun _ -> async { return Error "Session management is not available in fixture mode" } }
    | None ->
        { getWorktrees = fun () -> getWorktrees agent sessionAgent rootPaths appVersion deployBranch
          openTerminal = openTerminal validatePath sessionAgent
          openEditor = openEditor validatePath
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
                              let provider = resolveProvider state wt.Path
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

                          let merged = (claudeEvt |> Option.toList) @ syncEvts

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
                  async {
                      let path = WorktreePath.value req.Path
                      let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
                      let provider = resolveProvider state path
                      let command = CodingToolStatus.buildInteractiveCommand provider req.Prompt
                      return! SessionManager.spawnSession sessionAgent req.Path command
                  })
          focusSession = fun wtPath ->
              withValidatedPath wtPath "focusSession" (fun () ->
                  SessionManager.focusSession sessionAgent wtPath)
          killSession = fun wtPath ->
              withValidatedPath wtPath "killSession" (fun () ->
                  SessionManager.killSession sessionAgent wtPath)
          archiveWorktree = updateArchivedBranches agent rootPaths Set.add
          unarchiveWorktree = updateArchivedBranches agent rootPaths Set.remove
          getBranches = fun repoIdStr ->
              async {
                  let repoId = RepoId.create repoIdStr
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  return
                      state.Repos
                      |> Map.tryFind repoId
                      |> Option.map (fun repo ->
                          repo.WorktreeList
                          |> List.choose _.Branch
                          |> List.sortBy GitWorktree.branchSortKey)
                      |> Option.defaultValue []
              }
          createWorktree = fun req ->
              async {
                  let repoId = RepoId.create req.RepoId

                  match rootPaths |> Map.tryFind repoId with
                  | None ->
                      return Error $"Unknown repo: {req.RepoId}"
                  | Some root ->
                      let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                      let sourceWorktree =
                          state.Repos
                          |> Map.tryFind repoId
                          |> Option.bind (fun repo ->
                              repo.WorktreeList
                              |> List.tryFind (fun wt -> wt.Branch = Some (BranchName.value req.BaseBranch)))

                      match sourceWorktree with
                      | None ->
                          return Error $"No worktree found for branch '{BranchName.value req.BaseBranch}'"
                      | Some wt ->
                          let! result = GitWorktree.createWorktree root wt.Path (BranchName.value req.BranchName)

                          match result with
                          | Ok () ->
                              agent.Post(RefreshScheduler.StateMsg.ExpediteRefresh repoId)
                          | Error _ -> ()

                          return result
              }
          openNewTab = fun wtPath ->
              withValidatedPath wtPath "openNewTab" (fun () ->
                  SessionManager.openNewTab sessionAgent wtPath)
          launchAction = fun req ->
              withValidatedPath req.Path "launchAction" (fun () ->
                  async {
                      let path = WorktreePath.value req.Path
                      let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
                      let provider = resolveProvider state path
                      let command = CodingToolStatus.buildInteractiveCommand provider req.Prompt
                      return! SessionManager.launchAction sessionAgent req.Path command
                  }) }
