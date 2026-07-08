module Server.WorktreeApi

open System
open System.Collections.Concurrent
open System.IO
open System.Text.RegularExpressions
open Shared
open Shared.EventUtils
open Shared.PathUtils
open Newtonsoft.Json
open FsToolkit.ErrorHandling
open Server.GlobalConfig

let private canvasSpawnInFlight = ConcurrentDictionary<string, bool>()

let loadFixtures (path: string) : Result<FixtureData, string> =
    try
        let json = File.ReadAllText(path)
        let converter = Fable.Remoting.Json.FableJsonConverter()
        let data = JsonConvert.DeserializeObject<FixtureData>(json, converter)
        // Sanitize null lists — Fable.Remoting client can't deserialize null as F# list
        let sanitized =
            { data with
                Worktrees.Repos =
                    data.Worktrees.Repos
                    |> List.map (fun r ->
                        { r with
                            Worktrees =
                                r.Worktrees
                                |> List.map (fun wt ->
                                    { wt with
                                        CanvasDocs =
                                            if obj.ReferenceEquals(wt.CanvasDocs, null) then []
                                            else wt.CanvasDocs
                                        Planning =
                                            if obj.ReferenceEquals(wt.Planning, null) then BeadsPlanning.zero
                                            else wt.Planning }) }) }
        Ok sanitized
    with ex ->
        Error $"Failed to load fixture file '{path}': {ex.Message}"

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
      saveCanvasPaneOpen = fun _ -> async { return () }
      saveOverviewPanelOpen = fun _ -> async { return () }
      saveCanvasPosition = fun _ -> async { return () }
      saveCanvasSize = fun _ -> async { return () }
      resumeSession = fun _ -> async { return Error $"Session management is not available in {modeName}" }
      sendCanvasMessage = fun _ -> async { return CanvasMessageResult.Queued }
      archiveCanvasDoc = fun _ -> async { return Error $"Archive canvas doc is not available in {modeName}" }
      shareCanvasDoc = fun _ -> async { return Error $"Share canvas doc is not available in {modeName}" }
      saveLastViewedHashes = fun _ -> async { return () }
      loadLastViewedHashes = fun () -> async { return Map.empty }
      getBridgeLiveness = fun _ -> async { return Map.empty }
      // Root management is unavailable in demo/fixture modes (roots stay []); getRoots is just empty.
      addRoot = fun _ -> async { return Error $"Root management is not available in {modeName}" }
      removeRoot = fun _ -> async { return Error $"Root management is not available in {modeName}" }
      getRoots = fun () -> async { return [] } }

let private archiveCanvasDocImpl (request: ArchiveCanvasDocRequest) =
    let path = WorktreePath.value request.WorktreePath
    asyncResult {
        let! sourcePath =
            Server.PathUtils.validateCanvasPath path request.Filename
            |> Result.mapError (fun _ -> "Invalid filename: path escapes canvas directory")

        if not (File.Exists sourcePath) then
            return! Error $"File not found: {request.Filename}"

        let canvasDir = Path.Combine(path, ".agents", "canvas")
        let archiveDir = Path.Combine(canvasDir, "archive")
        Directory.CreateDirectory archiveDir |> ignore
        let destPath = Path.Combine(archiveDir, request.Filename)
        File.Move(sourcePath, destPath, overwrite = true)
    }

/// Share a canvas doc: validate the path → read the on-disk file → static-export it
/// (`CanvasExport.buildStaticHtml` re-injects theme + no-op canvasSend) → publish to Azure Blob and
/// mint a per-doc read-only SAS (`CanvasShare.publish`) → assemble the `CanvasShareResult` with the
/// SAS URL and the doc's resolved title. Mirrors `archiveCanvasDocImpl`. `Title` uses
/// `CanvasExport.resolveTitle` (the doc's `<title>`, falling back to a prettified filename) because
/// `CanvasShareResult.Title` is a plain string, not an option; the title is read from the original
/// HTML (`buildStaticHtml` injects only at `</head>`, so it never alters the doc's `<title>`).
let private shareCanvasDocImpl (request: ShareCanvasDocRequest) : Async<Result<CanvasShareResult, string>> =
    let path = WorktreePath.value request.WorktreePath
    asyncResult {
        let! sourcePath =
            Server.PathUtils.validateCanvasPath path request.Filename
            |> Result.mapError (fun _ -> "Invalid filename: path escapes canvas directory")

        // Sharing is AgentDoc-only per spec (a SystemView like beads.html is server-generated,
        // data-driven, and not shareable). The client only shows the Share button for AgentDocs;
        // this gate enforces the same contract when the endpoint is called directly.
        if CanvasDocKind.classify request.Filename <> AgentDoc then
            return! Error $"Cannot share system view: {request.Filename}"

        if not (File.Exists sourcePath) then
            return! Error $"File not found: {request.Filename}"

        let html = File.ReadAllText sourcePath
        let! sasUrl = Server.CanvasShare.publish request.Filename (Server.CanvasExport.buildStaticHtml html)
        return
            { Url = sasUrl
              Title = Server.CanvasExport.resolveTitle html request.Filename }
    }

let private assembleFromState
    (activeSessions: Set<string>)
    (archivedBranches: Set<string>)
    (hasTestFailureLog: bool)
    (repo: RefreshScheduler.PerRepoState)
    (wt: GitWorktree.WorktreeInfo)
    =
    let gitData = repo.GitData |> Map.tryFind wt.Path
    let beads = repo.BeadsData |> Map.tryFind wt.Path |> Option.defaultValue BeadsSummary.zero
    let planning = repo.PlanningData |> Map.tryFind wt.Path |> Option.defaultValue BeadsPlanning.zero
    let codingToolData =
        repo.CodingToolData
        |> Map.tryFind wt.Path
        |> Option.defaultValue
            { CodingToolStatus.CodingToolResult.Status = CodingToolStatus.Idle
              Provider = None
              CurrentSkill = None
              LastUserMessage = None
              LastAssistantMessage = None
              LastMessageProvider = None }
    let upstreamBranch = gitData |> Option.bind _.UpstreamBranch
    let pr = PrStatus.lookupPrStatus repo.PrData upstreamBranch

    { Path = PathUtils.toWorktreePath wt.Path
      Branch = wt.Branch |> Option.defaultValue WorktreeStatus.DetachedBranchName
      LastCommitMessage = gitData |> Option.map (_.LastCommitMessage) |> Option.defaultValue ""
      LastCommitTime = gitData |> Option.map (_.LastCommitTime) |> Option.defaultValue DateTimeOffset.MinValue
      Beads = beads
      Planning = planning
      CodingTool = codingToolData.Status
      CodingToolProvider = codingToolData.Provider
      CurrentSkill = codingToolData.CurrentSkill
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
        |> Option.defaultValue false
      CanvasDocs = repo.CanvasData |> Map.tryFind wt.Path |> Option.defaultValue [] }

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
        let ignorePredicate = GlobalConfig.readIgnoreWorktreePatterns () |> GlobalConfig.buildIgnorePredicate

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
                    |> List.filter (RefreshScheduler.isWorktreeIgnored ignorePredicate >> not)
                    |> List.map (fun wt ->
                        let hasLog = SyncEngine.testFailureLogPath wt.Path |> System.IO.File.Exists
                        assembleFromState activeSessionPaths archivedBranches hasLog repo wt)

                let originalPath = rootPaths |> Map.tryFind repoId |> Option.defaultValue (RepoId.value repoId)

                { RepoId = repoId
                  RootFolderName = Path.GetFileName(originalPath)
                  Worktrees = statuses
                  IsReady = repo.IsReady
                  Provider = repo.Provider
                  BaseBranch = repo.BaseBranch })

        return
            { Repos = repos
              SchedulerEvents = mergeWithPinnedErrors state.SchedulerEvents state.PinnedErrors
              LatestByCategory = state.LatestByCategory
              AppVersion = appVersion
              DeployBranch = deployBranch
              SystemMetrics = SystemMetrics.getSystemMetrics ()
              EditorName = getEditorConfig () |> snd
              CollapsedRepos = readCollapsedRepos ()
              CanvasPaneOpen = readCanvasPaneOpen ()
              OverviewPanelOpen = readOverviewPanelOpen ()
              CanvasPosition = readCanvasPosition ()
              CanvasSize = readCanvasSize () }
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
    let fixtures = testFixtures |> Option.bind (fun p -> loadFixtures p |> Result.toOption)

    let rootPaths = RefreshScheduler.buildRootPaths worktreeRoots

    let validatePath path =
        async {
            let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
            let knownPaths = allKnownPaths state
            return knownPaths |> Set.exists (fun p -> pathEquals p path)
        }

    let withValidatedPath (wtPath: WorktreePath) opName (action: unit -> Async<Result<'a, string>>) =
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
            (fun () -> async { return { f.Worktrees with DeployBranch = None; SystemMetrics = None; EditorName = getEditorConfig () |> snd; CollapsedRepos = readCollapsedRepos (); CanvasPaneOpen = false; OverviewPanelOpen = false; CanvasPosition = CanvasPosition.Right; CanvasSize = CanvasSize.Ratio1To1 } })
            (fun () -> async { return f.SyncStatus })
          with
            getBranches = fun _ -> async { return [ "main"; "develop"; "feature/sample" ] }
            createWorktree = fun _ -> async { return Ok [] } }
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
                  Async.Start(SyncEngine.executeSyncPipeline post syncKey ctx.Worktree.Path ctx.RepoRoot provider repo.UpstreamRemote repo.BaseBranch prStatus ct, ct)
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

                  let syncKeyToPath =
                      state.Repos
                      |> Map.toList
                      |> List.collect (fun (repoId, repo) ->
                          repo.WorktreeList
                          |> List.map (fun wt ->
                              let branch = wt.Branch |> Option.defaultValue (detachedBranchLabel wt.Path)
                              let syncKey = scopedBranchKey repoId branch
                              syncKey, wt.Path))
                      |> Map.ofList

                  let! syncEvents = syncAgent.PostAndAsyncReply(SyncEngine.GetAllEvents)

                  let allKeys =
                      [ yield! syncEvents |> Map.keys
                        yield! syncKeyToPath |> Map.keys ]
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
                      |> List.choose (fun syncKey ->
                          let wtPath = syncKeyToPath |> Map.tryFind syncKey

                          let syncEvts =
                              syncEvents
                              |> Map.tryFind syncKey
                              |> Option.defaultValue []

                          let claudeEvt =
                              wtPath
                              |> Option.bind (fun p -> cachedLastMessages |> Map.tryFind p)

                          let merged = (claudeEvt |> Option.toList) @ syncEvts

                          match merged, wtPath with
                          | [], _ -> None
                          | events, Some path ->
                              let recent =
                                  events
                                  |> List.sortByDescending _.Timestamp
                                  |> List.truncate 2
                                  |> List.rev

                              Some(path, recent)
                          | _, None -> None)
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
                          |> List.sortBy (GitWorktree.branchSortKey repo.BaseBranch))
                      |> Option.defaultValue []
              }
          createWorktree = fun req ->
              asyncResult {
                  let repoId = PathUtils.toRepoId req.RepoId

                  let! root =
                      rootPaths
                      |> Map.tryFind repoId
                      |> Result.requireSome $"Unknown repo: {req.RepoId}"

                  let! warnings = GitWorktree.createWorktree root (BranchName.value req.BaseBranch) (BranchName.value req.BranchName)
                  agent.Post(RefreshScheduler.StateMsg.ExpediteRefresh repoId)
                  return warnings
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
          saveCanvasPaneOpen = fun isOpen -> async { writeCanvasPaneOpen isOpen }
          saveOverviewPanelOpen = fun isOpen -> async { writeOverviewPanelOpen isOpen }
          saveCanvasPosition = fun pos -> async { writeCanvasPosition pos }
          saveCanvasSize = fun size -> async { writeCanvasSize size }
          resumeSession = fun wtPath ->
              withValidatedPath wtPath "resumeSession" (fun () ->
                  async {
                      let path = WorktreePath.value wtPath
                      let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
                      let provider = resolveProvider state path
                      let sessionId = CodingToolStatus.getLastSessionId provider path
                      let inv = CodingToolCli.build provider (CodingToolCli.Resume sessionId)
                      return! SessionManager.spawnSession sessionAgent wtPath inv.AsShellString
                  })
          sendCanvasMessage = fun request ->
              async {
                  let! result = CanvasBridge.sendMessage request
                  match result with
                  | CanvasMessageResult.Queued ->
                      let path = WorktreePath.value request.WorktreePath
                      let guardKey = path
                      if canvasSpawnInFlight.TryAdd(guardKey, true) then
                          try
                              let! owner = CanvasDocOwnership.getOwner path request.Filename
                              let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
                              let provider = resolveProvider state path
                              // Open a new tab in the live session window when one is tracked, and
                              // spawn only when none exists (launchAction semantics). This path is
                              // reached automatically by a canvas-iframe postMessage and has no
                              // resume identity to preserve, so it must never kill a live session
                              // window by path the way spawnSession (via spawnAndTrack) does.
                              let startOrContinueSession () =
                                  async {
                                      let prompt = CanvasPrompt.continueWorking path request.Filename
                                      let command = CodingToolCli.build provider (CodingToolCli.Interactive prompt)
                                      let! _ = SessionManager.launchAction sessionAgent request.WorktreePath command.AsShellString
                                      ()
                                  }
                              match owner with
                              | Some ownerSessionId ->
                                  // Resume intentionally uses spawnSession (kill-by-path then respawn),
                                  // mirroring the user-initiated resumeSession flow: replacing the
                                  // worktree's window with a fresh resume of the owner session is the
                                  // desired behavior when there is a resume identity to preserve.
                                  Log.log "API" $"sendCanvasMessage: resuming owner session {ownerSessionId} for {request.Filename}"
                                  let inv = CodingToolCli.build provider (CodingToolCli.Resume (Some ownerSessionId))
                                  let! resumeResult = SessionManager.spawnSession sessionAgent request.WorktreePath inv.AsShellString
                                  match resumeResult with
                                  | Ok () ->
                                      Log.log "API" $"sendCanvasMessage: resume succeeded for {request.Filename}"
                                  | Error err ->
                                      Log.log "API" $"sendCanvasMessage: resume failed ({err}), starting/continuing session for {request.Filename}"
                                      do! startOrContinueSession ()
                              | None ->
                                  Log.log "API" $"sendCanvasMessage: no owner for {request.Filename}, starting/continuing session"
                                  do! startOrContinueSession ()
                          finally
                              canvasSpawnInFlight.TryRemove(guardKey) |> ignore
                      else
                          Log.log "API" $"sendCanvasMessage: resume/spawn already in flight for {path}, skipping"
                  | _ -> ()
                  return result
              }
          archiveCanvasDoc = fun req ->
              withValidatedPath req.WorktreePath "archiveCanvasDoc" (fun () ->
                  archiveCanvasDocImpl req)
          shareCanvasDoc = fun req ->
              withValidatedPath req.WorktreePath "shareCanvasDoc" (fun () ->
                  shareCanvasDocImpl req)
          saveLastViewedHashes = fun hashes -> async { writeLastViewedHashes hashes }
          loadLastViewedHashes = fun () -> async { return readLastViewedHashes () }
          getBridgeLiveness = fun paths -> async { return CanvasBridge.getAllLiveness paths }
          // Roots are managed restart-to-apply: persist to global config only (no scheduler
          // message, no live-roots read). getWorktrees/createWorktree/path-validation keep using
          // the `rootPaths` captured at startup above — correct, since roots only change across
          // (re)starts (the treemon.ps1 add/remove shims trigger the restart).
          addRoot = fun path -> async { return addRootToConfig path }
          removeRoot = fun path -> async { return removeRootFromConfig path }
          getRoots = fun () -> async { return readWorktreeRootsConfig () } }
