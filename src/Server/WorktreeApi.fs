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
    (hasTestFailureLog: bool)
    (repo: RefreshScheduler.PerRepoState)
    (wt: GitWorktree.WorktreeInfo)
    =
    let gitData = repo.GitData |> Map.tryFind wt.Path
    let beads = repo.BeadsData |> Map.tryFind wt.Path |> Option.defaultValue BeadsSummary.zero
    let codingToolData =
        repo.CodingToolData
        |> Map.tryFind wt.Path
        |> Option.defaultValue
            { CodingToolStatus.CodingToolResult.Status = CodingToolStatus.Idle
              Provider = None
              LastUserMessage = None
              LastAssistantMessage = None }
    let upstreamBranch = gitData |> Option.bind _.UpstreamBranch
    let pr = PrStatus.lookupPrStatus repo.PrData upstreamBranch

    { Path = WorktreePath.create wt.Path
      Branch = wt.Branch |> Option.defaultValue GitWorktree.DetachedBranchName
      LastCommitMessage = gitData |> Option.map (_.LastCommitMessage) |> Option.defaultValue ""
      LastCommitTime = gitData |> Option.map (_.LastCommitTime) |> Option.defaultValue DateTimeOffset.MinValue
      Beads = beads
      CodingTool = codingToolData.Status
      CodingToolProvider = codingToolData.Provider
      LastUserMessage = codingToolData.LastUserMessage
      Pr = pr
      MainBehindCount = gitData |> Option.map (_.MainBehindCount) |> Option.defaultValue 0
      IsDirty = gitData |> Option.map (_.IsDirty) |> Option.defaultValue false
      WorkMetrics = gitData |> Option.bind _.WorkMetrics
      HasActiveSession = Set.contains wt.Path activeSessions
      HasTestFailureLog = hasTestFailureLog
      IsArchived =
        wt.Branch
        |> Option.map (fun b -> Set.contains b archivedBranches)
        |> Option.defaultValue false }

type WorktreeContext =
    { Worktree: GitWorktree.WorktreeInfo
      RepoId: RepoId
      RepoRoot: string
      Branch: string option }

let private tryResolveWorktreeContext
    (rootPaths: Map<RepoId, string>)
    (state: RefreshScheduler.DashboardState)
    (path: string)
    =
    state.Repos
    |> Map.toList
    |> List.tryPick (fun (repoId, repo) ->
        repo.WorktreeList
        |> List.tryFind (fun wt -> wt.Path = path)
        |> Option.bind (fun wt ->
            rootPaths
            |> Map.tryFind repoId
            |> Option.map (fun root ->
                { Worktree = wt
                  RepoId = repoId
                  RepoRoot = root
                  Branch = wt.Branch })))

let private allKnownPaths (state: RefreshScheduler.DashboardState) =
    state.Repos
    |> Map.values
    |> Seq.collect _.KnownPaths
    |> Set.ofSeq

let internal scopedBranchKey (repoId: RepoId) (branch: string) = $"{RepoId.value repoId}/{branch}"

let internal detachedBranchLabel (path: string) = $"(detached@{path})"

let private resolveProvider (state: RefreshScheduler.DashboardState) (path: string) =
    state.Repos
    |> Map.values
    |> Seq.tryPick (fun repo ->
        repo.CodingToolData
        |> Map.tryFind path
        |> Option.bind _.Provider)

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
                    |> List.map (fun wt ->
                        let hasLog = SyncEngine.testFailureLogPath wt.Path |> System.IO.File.Exists
                        assembleFromState activeSessionPaths archivedBranches hasLog repo wt)

                { RepoId = repoId
                  RootFolderName = Path.GetFileName(RepoId.value repoId)
                  Worktrees = statuses
                  IsReady = repo.IsReady
                  Provider = repo.Provider })

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
    (wtPath: WorktreePath)
    =
    let path = WorktreePath.value wtPath
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

        match tryResolveWorktreeContext rootPaths state path with
        | None -> return Error $"No worktree found at path '{path}'"
        | Some ctx ->
            agent.Post(RefreshScheduler.StateMsg.RemoveWorktree(ctx.RepoId, ctx.Worktree.Path))
            return! GitWorktree.removeWorktree ctx.RepoRoot ctx.Worktree.Path ctx.Worktree.Branch
    }

let private updateArchivedBranches
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (setOp: string -> Set<string> -> Set<string>)
    (wtPath: WorktreePath)
    =
    let path = WorktreePath.value wtPath
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

        match tryResolveWorktreeContext rootPaths state path with
        | None ->
            return Error $"No worktree found at path '{path}'"
        | Some { Branch = None; Worktree = wt } ->
            return Error $"Worktree at '{wt.Path}' has no branch (detached HEAD)"
        | Some ({ Branch = Some branch } as ctx) ->
            let liveBranches =
                state.Repos
                |> Map.tryFind ctx.RepoId
                |> Option.map (fun repo -> repo.WorktreeList |> List.choose _.Branch |> Set.ofList)
                |> Option.defaultValue Set.empty

            TreemonConfig.modifyArchivedBranches ctx.RepoRoot (fun existing ->
                existing
                |> Set.ofList
                |> setOp branch
                |> Set.intersect liveBranches
                |> Set.toList)
            agent.Post(RefreshScheduler.StateMsg.ExpediteRefresh ctx.RepoId)
            return Ok ()
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
          startSync = fun wtPath ->
              let path = WorktreePath.value wtPath
              async {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  match tryResolveWorktreeContext rootPaths state path with
                  | None -> return Error $"No worktree found at path '{path}'"
                  | Some { Branch = None } ->
                      return Error $"Cannot sync worktree at '{path}': detached HEAD (no branch)"
                  | Some ({ Branch = Some branch } as ctx) ->
                      let syncKey = scopedBranchKey ctx.RepoId branch
                      let provider = resolveProvider state ctx.Worktree.Path

                      let! beginResult = syncAgent.PostAndAsyncReply(fun reply -> SyncEngine.BeginSync (syncKey, reply))

                      match beginResult with
                      | Error msg -> return Error msg
                      | Ok ct ->
                          let post = syncAgent.Post
                          let repo = state.Repos |> Map.tryFind ctx.RepoId |> Option.defaultValue RefreshScheduler.PerRepoState.empty
                          Async.Start(SyncEngine.executeSyncPipeline post syncKey ctx.Worktree.Path ctx.RepoRoot provider repo.UpstreamRemote ct, ct)
                          return Ok ()
              }
          cancelSync = fun wtPath ->
              let path = WorktreePath.value wtPath
              async {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  match tryResolveWorktreeContext rootPaths state path with
                  | None ->
                      Log.log "API" $"cancelSync: no worktree found at path '{path}'"
                  | Some { Branch = None } ->
                      Log.log "API" $"cancelSync: worktree at '{path}' has detached HEAD, nothing to cancel"
                  | Some ({ Branch = Some branch } as ctx) ->
                      let syncKey = scopedBranchKey ctx.RepoId branch
                      syncAgent.Post(SyncEngine.CancelSync syncKey)
              }
          getSyncStatus = fun () ->
              async {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  let branchToScopedKey =
                      state.Repos
                      |> Map.toList
                      |> List.collect (fun (repoId, repo) ->
                          repo.WorktreeList
                          |> List.map (fun wt ->
                              let branch = wt.Branch |> Option.defaultValue (detachedBranchLabel wt.Path)
                              let key = scopedBranchKey repoId branch
                              key, wt.Path))
                      |> Map.ofList

                  let! syncEvents = syncAgent.PostAndAsyncReply(SyncEngine.GetAllEvents)

                  let allKeys =
                      [ yield! syncEvents |> Map.keys
                        yield! branchToScopedKey |> Map.keys ]
                      |> List.distinct

                  let cachedLastMessages =
                      state.Repos
                      |> Map.toList
                      |> List.collect (fun (_, repo) ->
                          repo.CodingToolData
                          |> Map.toList
                          |> List.choose (fun (path, data) ->
                              data.LastAssistantMessage |> Option.map (fun msg -> path, msg)))
                      |> Map.ofList

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
                              |> Option.bind (fun wtPath -> cachedLastMessages |> Map.tryFind wtPath)

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
                      let prompt = CodingToolStatus.actionPrompt provider req.Action
                      let command = CodingToolStatus.buildInteractiveCommand provider prompt
                      return! SessionManager.launchAction sessionAgent req.Path command
                  }) }
