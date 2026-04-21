module Server.WorktreeApi

open System
open System.IO
open Shared
open Shared.EventUtils
open Shared.PathUtils
open Newtonsoft.Json
open FsToolkit.ErrorHandling

let loadFixtures (path: string) =
    let json = File.ReadAllText(path)
    let converter = Fable.Remoting.Json.FableJsonConverter()
    JsonConvert.DeserializeObject<FixtureData>(json, converter)

let readOnlyApi
    (modeName: string)
    (getWorktrees: unit -> Async<DashboardResponse>)
    (getSyncStatus: unit -> Async<Map<string, CardEvent list>>)
    : IWorktreeApi =
    { getWorktrees = getWorktrees
      getSyncStatus = getSyncStatus
      openTerminal = fun _ -> async { return () }
      openEditor = fun _ -> async { return () }
      startSync = fun _ -> async { return Error $"Sync is not available in {modeName}" }
      cancelSync = fun _ -> async { return () }
      deleteWorktree = fun _ -> async { return Error $"Delete is not available in {modeName}" }
      launchSession = fun _ -> async { return Error $"Session management is not available in {modeName}" }
      focusSession = fun _ -> async { return Error $"Session management is not available in {modeName}" }
      killSession = fun _ -> async { return Error $"Session management is not available in {modeName}" }
      archiveWorktree = fun _ -> async { return Error $"Archive is not available in {modeName}" }
      unarchiveWorktree = fun _ -> async { return Error $"Archive is not available in {modeName}" }
      getBranches = fun _ -> async { return [] }
      createWorktree = fun _ -> async { return Error $"Create is not available in {modeName}" }
      openNewTab = fun _ -> async { return Error $"Session management is not available in {modeName}" }
      launchAction = fun _ -> async { return Error $"Session management is not available in {modeName}" }
      reportActivity = fun _ -> async { return () }
      saveCollapsedRepos = fun _ -> async { return () }
      resumeSession = fun _ -> async { return Error $"Session management is not available in {modeName}" } }

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
              LastAssistantMessage = None
              LastMessageProvider = None }
    let upstreamBranch = gitData |> Option.bind _.UpstreamBranch
    let pr = PrStatus.lookupPrStatus repo.PrData upstreamBranch

    { Path = PathUtils.toWorktreePath wt.Path
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
      IsMainWorktree = Directory.Exists(Path.Combine(wt.Path, ".git"))
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
        |> List.tryFind (fun wt -> pathEquals wt.Path path)
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
        |> Option.bind (fun data -> data.Provider |> Option.orElse data.LastMessageProvider))

let private globalConfigPath () =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".treemon",
        "config.json")

let private withConfigDocument (defaultValue: 'a) (f: System.Text.Json.JsonElement -> 'a) : 'a =
    let path = globalConfigPath ()
    if not (File.Exists path) then defaultValue
    else
        try
            let json = File.ReadAllText path
            use doc = System.Text.Json.JsonDocument.Parse json
            f doc.RootElement
        with ex ->
            Log.log "Config" $"Failed to read config: {ex.Message}"
            defaultValue

let private readGlobalConfig () =
    withConfigDocument Map.empty (fun root ->
        root.EnumerateObject()
        |> Seq.choose (fun prop ->
            if prop.Value.ValueKind = System.Text.Json.JsonValueKind.String
            then Some (prop.Name, prop.Value.GetString())
            else None)
        |> Map.ofSeq)

let private readCollapsedRepos () : Set<RepoId> =
    withConfigDocument Set.empty (fun root ->
        match root.TryGetProperty("collapsedRepos") with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.Array ->
            prop.EnumerateArray()
            |> Seq.choose (fun el ->
                if el.ValueKind = System.Text.Json.JsonValueKind.String then Some (RepoId (el.GetString()))
                else None)
            |> Set.ofSeq
        | _ -> Set.empty)

let private writeCollapsedRepos (repos: RepoId list) =
    let configPath = globalConfigPath ()
    try
        let dir = Path.GetDirectoryName(configPath)
        if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore

        let root =
            if File.Exists(configPath) then
                try File.ReadAllText(configPath) |> System.Text.Json.Nodes.JsonNode.Parse :?> System.Text.Json.Nodes.JsonObject
                with _ -> System.Text.Json.Nodes.JsonObject()
            else System.Text.Json.Nodes.JsonObject()

        let repoArray = System.Text.Json.Nodes.JsonArray(repos |> List.map (fun (RepoId s) -> System.Text.Json.Nodes.JsonValue.Create(s) :> System.Text.Json.Nodes.JsonNode) |> List.toArray)
        root["collapsedRepos"] <- repoArray

        let options = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(configPath, root.ToJsonString(options))
    with ex ->
        Log.log "Config" $"Failed to save collapsed repos: {ex.Message}"

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

                let originalPath = rootPaths |> Map.tryFind repoId |> Option.defaultValue (RepoId.value repoId)

                { RepoId = repoId
                  RootFolderName = Path.GetFileName(originalPath)
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
              EditorName = getEditorConfig () |> snd
              CollapsedRepos = readCollapsedRepos () }
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
        | Some ctx when Directory.Exists(Path.Combine(ctx.Worktree.Path, ".git")) ->
            return Error "Cannot delete the main worktree"
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
            return knownPaths |> Set.exists (fun p -> pathEquals p path)
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
        { readOnlyApi
            "fixture mode"
            (fun () -> async { return { f.Worktrees with DeployBranch = None; SystemMetrics = None; EditorName = getEditorConfig () |> snd; CollapsedRepos = readCollapsedRepos () } })
            (fun () -> async { return f.SyncStatus })
          with
            getBranches = fun _ -> async { return [ "main"; "develop"; "feature/sample" ] }
            createWorktree = fun _ -> async { return Ok() } }
    | None ->
        { getWorktrees = fun () -> getWorktrees agent sessionAgent rootPaths appVersion deployBranch
          openTerminal = openTerminal validatePath sessionAgent
          openEditor = openEditor validatePath
          startSync = fun wtPath ->
              let path = WorktreePath.value wtPath
              asyncResult {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  let! ctx, branch =
                      match tryResolveWorktreeContext rootPaths state path with
                      | None -> Error $"No worktree found at path '{path}'"
                      | Some { Branch = None } -> Error $"Cannot sync worktree at '{path}': detached HEAD (no branch)"
                      | Some ({ Branch = Some branch } as ctx) -> Ok (ctx, branch)
                  let syncKey = scopedBranchKey ctx.RepoId branch
                  let provider = resolveProvider state ctx.Worktree.Path

                  let! ct = syncAgent.PostAndAsyncReply(fun reply -> SyncEngine.BeginSync (syncKey, reply))

                  let post = syncAgent.Post
                  let repo = state.Repos |> Map.tryFind ctx.RepoId |> Option.defaultValue RefreshScheduler.PerRepoState.empty
                  let upstreamBranch = repo.GitData |> Map.tryFind ctx.Worktree.Path |> Option.bind _.UpstreamBranch
                  let prStatus = PrStatus.lookupPrStatus repo.PrData upstreamBranch
                  Async.Start(SyncEngine.executeSyncPipeline post syncKey ctx.Worktree.Path ctx.RepoRoot provider repo.UpstreamRemote prStatus ct, ct)
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
                      let inv = CodingToolCli.build provider (CodingToolCli.Interactive req.Prompt)
                      return! SessionManager.spawnSession sessionAgent req.Path inv.AsShellString
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
                  let repoId = PathUtils.toRepoId repoIdStr
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
              asyncResult {
                  let repoId = PathUtils.toRepoId req.RepoId

                  let! root =
                      rootPaths
                      |> Map.tryFind repoId
                      |> Result.requireSome $"Unknown repo: {req.RepoId}"

                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

                  let! wt =
                      state.Repos
                      |> Map.tryFind repoId
                      |> Option.bind (fun repo ->
                          repo.WorktreeList
                          |> List.tryFind (fun wt -> wt.Branch = Some (BranchName.value req.BaseBranch)))
                      |> Result.requireSome $"No worktree found for branch '{BranchName.value req.BaseBranch}'"

                  do! GitWorktree.createWorktree root wt.Path (BranchName.value req.BranchName)
                  agent.Post(RefreshScheduler.StateMsg.ExpediteRefresh repoId)
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
                      let prompt =
                          match req.Action with
                          | ConfigureTests ->
                              let root = tryResolveWorktreeContext rootPaths state path |> Option.map _.RepoRoot |> Option.defaultValue path
                              CodingToolStatus.configureTestsPrompt root
                          | action -> CodingToolStatus.actionPrompt provider action
                      let command = CodingToolCli.build provider (CodingToolCli.Interactive prompt)
                      return! SessionManager.launchAction sessionAgent req.Path command.AsShellString
                  })
          reportActivity = fun level -> async { agent.Post(RefreshScheduler.StateMsg.ReportClientActivity(level, DateTimeOffset.UtcNow)) }
          saveCollapsedRepos = fun repos -> async { writeCollapsedRepos repos }
          resumeSession = fun wtPath ->
              withValidatedPath wtPath "resumeSession" (fun () ->
                  async {
                      let path = WorktreePath.value wtPath
                      let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
                      let provider = resolveProvider state path
                      let sessionId = CodingToolStatus.getLastSessionId provider path
                      let inv = CodingToolCli.build provider (CodingToolCli.Resume sessionId)
                      return! SessionManager.spawnSession sessionAgent wtPath inv.AsShellString
                  }) }
