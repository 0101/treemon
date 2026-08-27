module Server.WorktreeApi

open System
open System.IO
open System.Text.RegularExpressions
open Shared
open Shared.EventUtils
open Shared.PathUtils
open Newtonsoft.Json
open FsToolkit.ErrorHandling
open Server.GlobalConfig
open Server.SessionActivityStore

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
                                        Sessions =
                                            if obj.ReferenceEquals(wt.Sessions, null) then []
                                            else wt.Sessions
                                        Planning =
                                            wt.Planning
                                            |> Option.ofObj
                                            |> Option.defaultValue BeadsPlanning.zero }) }) }
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
      startEmbeddedTerminal =
        fun _ -> async { return Error $"Embedded terminal is not available in {modeName}" }
      getEmbeddedTerminals = fun () -> async { return EmbeddedTerminalSnapshot.empty }
      closeEmbeddedTerminal = fun _ -> async { return Ok EmbeddedTerminalSnapshot.empty }
      openEditor = fun _ -> async { return () }
      toggleAutoSync = fun _ _ -> async { return Error $"Auto-sync is not available in {modeName}" }
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
      saveTerminalPaneOpen = fun _ -> async { return () }
      saveCanvasPaneOpen = fun _ -> async { return () }
      saveOverviewPanelOpen = fun _ -> async { return () }
      saveWorkspaceWidth = fun _ -> async { return () }
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
      getRoots = fun () -> async { return [] }
      getDiffCategoryReport = fun _ -> async { return Error $"Diff categories are not available in {modeName}" }
      // No durable activity history in demo/fixture modes, but preserve the anchored wire contract.
      getOverviewHistory =
        fun _ ->
            async {
                return
                    { OverviewData.OverviewHistoryResponse.Anchor =
                        OverviewSnapshotBoundary.floor DateTimeOffset.UtcNow
                      Snapshots = [] }
            } }

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
        if CanvasDocKinds.classify request.Filename <> AgentDoc then
            return! Error $"Cannot share system view: {request.Filename}"

        if not (File.Exists sourcePath) then
            return! Error $"File not found: {request.Filename}"

        let html = File.ReadAllText sourcePath
        let! sasUrl = Server.CanvasShare.publish request.Filename (Server.CanvasExport.buildStaticHtml html)
        return
            { Url = sasUrl
              Title = Server.CanvasExport.resolveTitle html request.Filename }
    }

type private OverviewWorktreeFields =
    { Beads: BeadsSummary
      Planning: BeadsPlanning
      CodingToolData: CodingToolStatus.CodingToolResult
      CodingTool: CodingToolStatus
      CodingToolSince: DateTimeOffset option
      IsArchived: bool }

let private overviewWorktreeFields
    (now: DateTimeOffset)
    (archivedBranches: Set<string>)
    (pushByWorktree: Map<string, CodingToolStatus.CodingToolResult>)
    (codingToolSince: Map<string, DateTimeOffset>)
    (repo: SchedulerState.PerRepoState)
    (wt: GitWorktree.WorktreeInfo)
    =
    let beads = repo.BeadsData |> Map.tryFind wt.Path |> Option.defaultValue BeadsSummary.zero
    let planning = repo.PlanningData |> Map.tryFind wt.Path |> Option.defaultValue BeadsPlanning.zero
    let codingToolData =
        pushByWorktree
        |> Map.tryFind wt.Path
        |> Option.defaultValue CodingToolStatus.noSessionPushResult
    let displayStatus =
        SessionActivity.debounceIdle
            SessionActivity.idleDebounceWindow
            now
            (codingToolSince |> Map.tryFind wt.Path)
            codingToolData.Status

    { Beads = beads
      Planning = planning
      CodingToolData = codingToolData
      CodingTool = displayStatus
      CodingToolSince =
        match displayStatus with
        | Idle -> codingToolSince |> Map.tryFind wt.Path
        | Working
        | WaitingForUser
        | NoSession -> None
      IsArchived =
        wt.Branch
        |> Option.map (fun b -> Set.contains b archivedBranches)
        |> Option.defaultValue false }

let internal assembleFromState
    (now: DateTimeOffset)
    (activeSessions: Set<string>)
    (archivedBranches: Set<string>)
    (autoSyncBranches: Set<string>)
    (pushByWorktree: Map<string, CodingToolStatus.CodingToolResult>)
    (codingToolSince: Map<string, DateTimeOffset>)
    (repo: SchedulerState.PerRepoState)
    (wt: GitWorktree.WorktreeInfo)
    =
    let fields =
        overviewWorktreeFields now archivedBranches pushByWorktree codingToolSince repo wt
    let gitData = repo.GitData |> Map.tryFind wt.Path
    let comparison =
        gitData
        |> Option.map _.Comparison
        |> Option.defaultValue GitWorktree.Undetermined
    let prBranch = gitData |> Option.bind GitWorktree.prBranchName
    let pr = PrStatus.tryLookupPrStatus repo.PrData prBranch |> Option.defaultValue NoPr

    { Path = PathUtils.toWorktreePath wt.Path
      Branch = wt.Branch |> Option.defaultValue WorktreeStatus.DetachedBranchName
      LastCommitMessage = gitData |> Option.map (_.LastCommitMessage) |> Option.defaultValue ""
      LastCommitTime = gitData |> Option.map (_.LastCommitTime) |> Option.defaultValue DateTimeOffset.MinValue
      Beads = fields.Beads
      Planning = fields.Planning
      CodingTool = fields.CodingTool
      CodingToolProvider = fields.CodingToolData.Provider
      CodingToolSince = fields.CodingToolSince
      CurrentSkill = fields.CodingToolData.CurrentSkill
      AgentActivity = fields.CodingToolData.AgentActivity
      Sessions = fields.CodingToolData.SessionStatuses
      LastUserMessage = fields.CodingToolData.LastUserMessage
      LastAssistantMessage = fields.CodingToolData.LastAssistantMessage
      Pr = pr
      MainBehindCount = gitData |> Option.map (_.MainBehindCount) |> Option.defaultValue 0
      AutoSyncEnabled =
        wt.Branch
        |> Option.map (fun branch -> Set.contains branch autoSyncBranches)
        |> Option.defaultValue false
      IsDirty = gitData |> Option.map (_.IsDirty) |> Option.defaultValue false
      HasDiff = comparison = GitWorktree.HasContent
      WorkMetrics = gitData |> Option.bind _.WorkMetrics
      HasActiveSession = Set.contains wt.Path activeSessions
      IsMainWorktree = Directory.Exists(Path.Combine(wt.Path, ".git"))
      IsArchived = fields.IsArchived
      CanvasDocs =
        repo.CanvasData
        |> Map.tryFind wt.Path
        |> Option.defaultValue []
        |> DiffProvisioner.visibleDocs comparison }

type WorktreeContext =
    { Worktree: GitWorktree.WorktreeInfo
      RepoId: RepoId
      RepoRoot: string
      Branch: string option }

let private tryResolveWorktreeContext
    (rootPaths: Map<RepoId, string>)
    (state: SchedulerState.DashboardState)
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

let private allKnownPaths (state: SchedulerState.DashboardState) =
    state.Repos
    |> Map.values
    |> Seq.collect _.KnownPaths
    |> Set.ofSeq

let internal scopedBranchKey (repoId: RepoId) (branch: string) = $"{RepoId.value repoId}/{branch}"

let internal detachedBranchLabel (path: string) = $"(detached@{path})"

type RepoAssemblyInputs =
    { Now: DateTimeOffset
      IgnorePredicate: string -> bool
      RetainedByWorktree: Map<string, SessionActivityStore.StoredStatus>
      ArchivedBranches: Map<RepoId, Set<string>>
      AutoSyncBranches: Map<RepoId, Set<string>> }

type OverviewAssemblyInputs =
    { Now: DateTimeOffset
      IgnorePredicate: string -> bool
      ArchivedBranches: Map<RepoId, Set<string>> }

let loadOverviewAssemblyInputs
    (now: DateTimeOffset)
    (rootPaths: Map<RepoId, string>)
    =
    { Now = now
      IgnorePredicate = GlobalConfig.readIgnoreWorktreePatterns () |> GlobalConfig.buildIgnorePredicate
      ArchivedBranches =
        rootPaths
        |> Map.map (fun _ root -> TreemonConfig.readArchivedBranchSet (Some root)) }

let internal isOverviewCaptureReady
    (rootPaths: Map<RepoId, string>)
    (inputs: OverviewAssemblyInputs option)
    (state: SchedulerState.DashboardState)
    =
    let repoReady inputs repoId =
        match state.Repos |> Map.tryFind repoId with
        | Some repo when repo.IsReady ->
            let archivedBranches =
                inputs.ArchivedBranches
                |> Map.tryFind repoId
                |> Option.defaultValue Set.empty

            repo.WorktreeList
            |> List.filter (RefreshScheduler.isWorktreeIgnored inputs.IgnorePredicate >> not)
            |> List.filter (fun wt ->
                wt.Branch
                |> Option.exists (fun branch -> Set.contains branch archivedBranches)
                |> not)
            |> List.forall (fun wt ->
                Map.containsKey wt.Path repo.BeadsData
                && Map.containsKey wt.Path repo.PlanningData)
        | _ -> false

    state.SessionStatusesHydrated
    && (match inputs with
        | Some inputs -> rootPaths |> Map.forall (fun repoId _ -> repoReady inputs repoId)
        | None -> Map.isEmpty rootPaths)

let loadRepoAssemblyInputs
    (now: DateTimeOffset)
    (activityStore: SessionActivityStore.SessionActivityStore option)
    (rootPaths: Map<RepoId, string>)
    =
    let overviewInputs = loadOverviewAssemblyInputs now rootPaths

    { Now = overviewInputs.Now
      IgnorePredicate = overviewInputs.IgnorePredicate
      RetainedByWorktree =
        activityStore
        |> Option.map _.RetainedByWorktree()
        |> Option.defaultValue Map.empty
      ArchivedBranches = overviewInputs.ArchivedBranches
      AutoSyncBranches =
        rootPaths
        |> Map.map (fun _ root -> TreemonConfig.readAutoSyncBranchSet (Some root)) }

let private assembleReposCore
    (ignorePredicate: string -> bool)
    (archivedBranchesByRepo: Map<RepoId, Set<string>>)
    (autoSyncBranchesByRepo: Map<RepoId, Set<string>>)
    (rootPaths: Map<RepoId, string>)
    (state: SchedulerState.DashboardState)
    (assembleStatus:
        Set<string> ->
            Set<string> ->
            SchedulerState.PerRepoState ->
            GitWorktree.WorktreeInfo ->
            WorktreeStatus)
    : RepoWorktrees list =
    state.Repos
    |> Map.toList
    |> List.map (fun (repoId, repo) ->
        let archivedBranches =
            archivedBranchesByRepo
            |> Map.tryFind repoId
            |> Option.defaultValue Set.empty

        let autoSyncBranches =
            autoSyncBranchesByRepo
            |> Map.tryFind repoId
            |> Option.defaultValue Set.empty

        let statuses =
            repo.WorktreeList
            |> List.filter (RefreshScheduler.isWorktreeIgnored ignorePredicate >> not)
            |> List.map (assembleStatus archivedBranches autoSyncBranches repo)

        let originalPath = rootPaths |> Map.tryFind repoId |> Option.defaultValue (RepoId.value repoId)

        { RepoId = repoId
          RootFolderName = Path.GetFileName(originalPath)
          Worktrees = statuses
          IsReady = repo.IsReady
          Provider = repo.Provider
          BaseBranch = repo.BaseBranch })

/// Complete RepoWorktrees assembly for the dashboard response.
let assembleRepos
    (inputs: RepoAssemblyInputs)
    (rootPaths: Map<RepoId, string>)
    (activeSessionPaths: Set<string>)
    (state: SchedulerState.DashboardState)
    : RepoWorktrees list =
    let pushByWorktree =
        state.SessionStatuses
        |> Map.values
        |> CodingToolStatus.includeRetainedSessions inputs.RetainedByWorktree
        |> CodingToolStatus.collapseByWorktree inputs.Now

    assembleReposCore
        inputs.IgnorePredicate
        inputs.ArchivedBranches
        inputs.AutoSyncBranches
        rootPaths
        state
        (fun archivedBranches autoSyncBranches repo wt ->
            assembleFromState
                inputs.Now
                activeSessionPaths
                archivedBranches
                autoSyncBranches
                pushByWorktree
                state.CodingToolSinceByWorktree
                repo
                wt)

let internal assembleOverviewFromState
    (now: DateTimeOffset)
    (archivedBranches: Set<string>)
    (pushByWorktree: Map<string, CodingToolStatus.CodingToolResult>)
    (codingToolSince: Map<string, DateTimeOffset>)
    (repo: SchedulerState.PerRepoState)
    (wt: GitWorktree.WorktreeInfo)
    =
    let fields =
        overviewWorktreeFields now archivedBranches pushByWorktree codingToolSince repo wt

    { Path = PathUtils.toWorktreePath wt.Path
      Branch = wt.Branch |> Option.defaultValue WorktreeStatus.DetachedBranchName
      LastCommitMessage = ""
      LastCommitTime = DateTimeOffset.MinValue
      Beads = fields.Beads
      Planning = fields.Planning
      CodingTool = fields.CodingTool
      CodingToolProvider = fields.CodingToolData.Provider
      CodingToolSince = fields.CodingToolSince
      CurrentSkill = fields.CodingToolData.CurrentSkill
      AgentActivity = fields.CodingToolData.AgentActivity
      Sessions = fields.CodingToolData.SessionStatuses
      LastUserMessage = fields.CodingToolData.LastUserMessage
      LastAssistantMessage = fields.CodingToolData.LastAssistantMessage
      Pr = NoPr
      MainBehindCount = 0
      AutoSyncEnabled = false
      IsDirty = false
      HasDiff = false
      WorkMetrics = None
      HasActiveSession = false
      IsMainWorktree = false
      IsArchived = fields.IsArchived
      CanvasDocs = [] }

/// Lean canonical-Overview assembly for snapshot capture. It shares the live task/session/archive
/// projection while omitting card-only retained footers, terminal decoration, auto-sync state,
/// Git/PR fields, and canvas data.
let assembleOverviewRepos
    (inputs: OverviewAssemblyInputs)
    (rootPaths: Map<RepoId, string>)
    (state: SchedulerState.DashboardState)
    : RepoWorktrees list =
    let pushByWorktree =
        CodingToolStatus.collapseByWorktree inputs.Now (state.SessionStatuses |> Map.values)

    assembleReposCore
        inputs.IgnorePredicate
        inputs.ArchivedBranches
        Map.empty
        rootPaths
        state
        (fun archivedBranches _ repo wt ->
            assembleOverviewFromState
                inputs.Now
                archivedBranches
                pushByWorktree
                state.CodingToolSinceByWorktree
                repo
                wt)

let getWorktrees
    (agent: MailboxProcessor<SchedulerState.StateMsg>)
    (sessionAgent: SessionManager.SessionAgent)
    (activityStore: SessionActivityStore.SessionActivityStore option)
    (rootPaths: Map<RepoId, string>)
    (appVersion: string)
    (deployBranch: string option)
    : Async<DashboardResponse> =
    async {
        let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)
        let! activeSessions = SessionManager.getActiveSessions sessionAgent

        let activeSessionPaths = activeSessions |> Map.keys |> Set.ofSeq
        let inputs = loadRepoAssemblyInputs DateTimeOffset.UtcNow activityStore rootPaths
        let repos = assembleRepos inputs rootPaths activeSessionPaths state

        return
            { Repos = repos
              SchedulerEvents = mergeWithPinnedErrors state.SchedulerEvents state.PinnedErrors
              LatestByCategory = state.LatestByCategory
              AppVersion = appVersion
              DeployBranch = deployBranch
              SystemMetrics = SystemMetrics.getSystemMetrics ()
              EditorName = getEditorConfig () |> snd
              WorktreeSkills = readWorktreeSkills ()
              CollapsedRepos = readCollapsedRepos ()
              TerminalPaneOpen = readTerminalPaneOpen ()
              CanvasPaneOpen = readCanvasPaneOpen ()
              OverviewPanelOpen = readOverviewPanelOpen ()
              WorkspaceWidth = readWorkspaceWidth () }
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

let internal deleteWorktreeWith
    (removeGitWorktree: string -> string -> string option -> Async<Result<unit, string>>)
    (withTerminalCleanup:
        WorktreePath ->
        (unit -> Async<Result<unit, string>>) ->
        Async<Result<unit, string>>)
    (removeWorktreeState: string -> Async<unit>)
    (agent: MailboxProcessor<SchedulerState.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (wtPath: WorktreePath)
    =
    let path = WorktreePath.value wtPath
    asyncResult {
        let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)

        match tryResolveWorktreeContext rootPaths state path with
        | None -> return! Error $"No worktree found at path '{path}'"
        | Some ctx when Directory.Exists(Path.Combine(ctx.Worktree.Path, ".git")) ->
            return! Error "Cannot delete the main worktree"
        | Some ctx ->
            return!
                withTerminalCleanup
                    (PathUtils.toWorktreePath ctx.Worktree.Path)
                    (fun () ->
                        asyncResult {
                            do!
                                removeGitWorktree
                                    ctx.RepoRoot
                                    ctx.Worktree.Path
                                    ctx.Worktree.Branch

                            agent.Post(
                                SchedulerState.StateMsg.RemoveWorktree(
                                    ctx.RepoId,
                                    ctx.Worktree.Path
                                )
                            )

                            do! removeWorktreeState ctx.Worktree.Path
                        })
    }

let private deleteWorktree
    agent
    embeddedTerminal
    (clearAcceptedSync: string -> unit)
    rootPaths
    wtPath
    =
    let removeWorktreeState path =
        async {
            do! CanvasDocOwnership.removeWorktree path
            do! WorktreeDiffApi.removeWorktree path
            clearAcceptedSync path
        }

    deleteWorktreeWith
        GitWorktree.removeWorktree
        (EmbeddedTerminal.withReservedCleanup embeddedTerminal)
        removeWorktreeState
        agent
        rootPaths
        wtPath

let internal updateArchivedBranchesWith
    (agent: MailboxProcessor<SchedulerState.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (withTerminalCleanup:
        WorktreePath ->
        (unit -> Async<Result<unit, string>>) ->
        Async<Result<unit, string>>)
    (setOp: string -> Set<string> -> Set<string>)
    (wtPath: WorktreePath)
    =
    let path = WorktreePath.value wtPath
    asyncResult {
        let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)

        match tryResolveWorktreeContext rootPaths state path with
        | None -> return! Error $"No worktree found at path '{path}'"
        | Some { Branch = None; Worktree = wt } ->
            return!
                Error
                    $"Worktree at '{wt.Path}' has no branch (detached HEAD)"
        | Some ({ Branch = Some branch } as ctx) ->
            let liveBranches =
                state.Repos
                |> Map.tryFind ctx.RepoId
                |> Option.map (fun repo -> repo.WorktreeList |> List.choose _.Branch |> Set.ofList)
                |> Option.defaultValue Set.empty

            return!
                withTerminalCleanup
                    (PathUtils.toWorktreePath ctx.Worktree.Path)
                    (fun () ->
                        async {
                            try
                                TreemonConfig.modifyArchivedBranches
                                    ctx.RepoRoot
                                    (fun existing ->
                                        existing
                                        |> Set.ofList
                                        |> setOp branch
                                        |> Set.intersect liveBranches
                                        |> Set.toList)

                                agent.Post(
                                    SchedulerState.StateMsg.ExpediteRefresh ctx.RepoId
                                )

                                return Ok ()
                            with ex ->
                                return
                                    Error
                                        $"Could not update archived worktrees: {ex.Message}"
                        })
    }

/// What one repository's declared diff categories do to its tracked files. Only a `Configured`
/// repository pays for enumerating them — the other states are the whole report — and the enumeration
/// runs at the repository root the shared `.treemon.json` was read from, so every linked worktree
/// gets the same answer for the configuration they share.
let internal diffCategoryReport (repoRoot: string) : Async<Result<DiffCategoryReport, string>> =
    asyncResult {
        match DiffCategories.read repoRoot with
        | DiffCategories.Configured _ as configuration ->
            let! tracked =
                WorktreeDiff.listTrackedFiles repoRoot
                |> AsyncResult.mapError (fun _ -> $"Could not list the tracked files of '{repoRoot}'")

            return DiffCategories.coverage configuration tracked
        | unconfigured -> return DiffCategories.coverage unconfigured []
    }

/// Everything `worktreeApi` needs to serve the dashboard. A record rather than a parameter list
/// because most of these are optional or plain strings — `TestFixtures` and `DeployBranch` are both
/// `string option`, so a positional call could transpose them silently, and the three optional
/// stores read as a run of bare `None`s at every call site. Naming each one makes the fixture-mode
/// wiring readable and a swap a compile error.
type WorktreeApiDependencies =
    { Agent: MailboxProcessor<SchedulerState.StateMsg>
      CardLog: MailboxProcessor<CardEventLog.CardEventLogMsg>
      SessionAgent: SessionManager.SessionAgent
      EmbeddedTerminal: EmbeddedTerminal.Manager
      ActivityStore: SessionActivityStore.SessionActivityStore option
      SnapshotStore: OverviewSnapshotStore.OverviewSnapshotStore option
      AutoSyncStore: AutoSyncStore.Store option
      WorktreeRoots: string list
      TestFixtures: string option
      AppVersion: string
      DeployBranch: string option }

let worktreeApi (dependencies: WorktreeApiDependencies) : IWorktreeApi =
    let { Agent = agent
          CardLog = cardLog
          SessionAgent = sessionAgent
          EmbeddedTerminal = embeddedTerminal
          ActivityStore = activityStore
          SnapshotStore = snapshotStore
          AutoSyncStore = autoSyncStore
          WorktreeRoots = worktreeRoots
          TestFixtures = testFixtures
          AppVersion = appVersion
          DeployBranch = deployBranch } =
        dependencies

    let fixtures = testFixtures |> Option.bind (fun p -> loadFixtures p |> Result.toOption)

    let rootPaths = RefreshScheduler.buildRootPaths worktreeRoots
    let autoSyncDependencies =
        RefreshScheduler.autoSyncDependencies agent sessionAgent activityStore autoSyncStore

    /// Ends auto-sync bookkeeping for a worktree: disabling the preference or deleting the worktree
    /// leaves nothing for the accepted-revision record to suppress.
    let clearAcceptedRecord = autoSyncDependencies.ClearAcceptedRevision

    let validatePath path =
        async {
            let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)
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

    /// Same guard for an endpoint whose result type is its own DU rather than `Result`.
    let withValidatedPathValue (wtPath: WorktreePath) opName (reject: string -> 'a) (action: unit -> Async<'a>) =
        let path = WorktreePath.value wtPath
        async {
            let! isValid = validatePath path

            if not isValid then
                Log.log "API" $"{opName}: rejected unknown path '{path}'"
                return reject $"Unknown worktree path: {path}"
            else
                return! action ()
        }

    let withReportedTerminalIntents snapshot =
        async {
            let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)

            return
                snapshot
                |> TerminalSessionActivity.withReportedIntents
                    DateTimeOffset.UtcNow
                    (state.SessionStatuses |> Map.values)
        }

    let terminalMutation operation =
        asyncResult {
            let! snapshot = operation
            let! enriched = withReportedTerminalIntents snapshot
            return enriched
        }

    let startEmbeddedTerminal wtPath =
        withValidatedPath
            wtPath
            "startEmbeddedTerminal"
            (fun () ->
                EmbeddedTerminal.start embeddedTerminal wtPath
                |> terminalMutation)

    let getEmbeddedTerminals () =
        async {
            let! snapshot = EmbeddedTerminal.get embeddedTerminal
            return! withReportedTerminalIntents snapshot
        }

    let closeEmbeddedTerminal terminalId =
        EmbeddedTerminal.close embeddedTerminal terminalId
        |> terminalMutation

    match fixtures with
    | Some f ->
        { readOnlyApi
            "fixture mode"
            (fun () -> async { return { f.Worktrees with DeployBranch = None; SystemMetrics = None; EditorName = getEditorConfig () |> snd; WorktreeSkills = readWorktreeSkills (); CollapsedRepos = readCollapsedRepos (); TerminalPaneOpen = false; CanvasPaneOpen = false; OverviewPanelOpen = false; WorkspaceWidth = WorkspaceWidth.EqualThirds } })
            (fun () -> async { return f.SyncStatus })
          with
            getBranches = fun _ -> async { return [ "main"; "develop"; "feature/sample" ] }
            createWorktree = fun _ -> async { return Ok [] }
            startEmbeddedTerminal = startEmbeddedTerminal
            getEmbeddedTerminals = getEmbeddedTerminals
            closeEmbeddedTerminal = closeEmbeddedTerminal }
    | None ->
        { getWorktrees = fun () -> getWorktrees agent sessionAgent activityStore rootPaths appVersion deployBranch
          openTerminal = openTerminal validatePath sessionAgent
          startEmbeddedTerminal = startEmbeddedTerminal
          getEmbeddedTerminals = getEmbeddedTerminals
          closeEmbeddedTerminal = closeEmbeddedTerminal
          openEditor = openEditor validatePath
          toggleAutoSync = fun wtPath enabled ->
              let path = WorktreePath.value wtPath
              async {
                  let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)

                  match tryResolveWorktreeContext rootPaths state path with
                  | None -> return Error $"No worktree found at path '{path}'"
                  | Some { Branch = None; Worktree = wt } ->
                      return Error $"Worktree at '{wt.Path}' has no branch (detached HEAD)"
                  | Some ({ Branch = Some branch } as ctx) ->
                      let repo = state.Repos |> Map.tryFind ctx.RepoId
                      let worktreeGit =
                          repo |> Option.bind (fun repo -> repo.GitData |> Map.tryFind ctx.Worktree.Path)
                      let prStatus =
                          RefreshScheduler.prStatusForPath state ctx.Worktree.Path
                          |> Option.defaultValue NoPr

                      try
                          TreemonConfig.modifyAutoSyncBranches ctx.RepoRoot (fun existing ->
                              existing
                              |> Set.ofList
                              |> (if enabled then Set.add branch else Set.remove branch)
                              |> Set.toList)

                          if not enabled then
                              clearAcceptedRecord ctx.Worktree.Path
                          else
                              match repo, worktreeGit with
                              | Some repo, Some gitData ->
                                  do!
                                      AutoSync.trigger
                                          autoSyncDependencies
                                          ctx.RepoRoot
                                          repo.UpstreamRemote
                                          repo.BaseBranch
                                          prStatus
                                          gitData
                              | _ -> ()

                          return Ok ()
                      with ex ->
                          Log.log "API" $"toggleAutoSync failed for '{path}': {ex.Message}"
                          return Error $"Failed to persist auto-sync preference: {ex.Message}"
              }
          getSyncStatus = fun () ->
              async {
                  let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)

                  let eventKeyToPath =
                      state.Repos
                      |> Map.toList
                      |> List.collect (fun (repoId, repo) ->
                          repo.WorktreeList
                          |> List.map (fun wt ->
                              let branch = wt.Branch |> Option.defaultValue (detachedBranchLabel wt.Path)
                              let eventKey = scopedBranchKey repoId branch
                              eventKey, wt.Path))
                      |> Map.ofList

                  let! cardEvents = cardLog.PostAndAsyncReply(CardEventLog.GetAll)

                  return
                      cardEvents
                      |> Map.toList
                      |> List.choose (fun (eventKey, branchEvents) ->
                          match eventKeyToPath |> Map.tryFind eventKey, branchEvents with
                          | Some path, (_ :: _) ->
                              let recent =
                                  branchEvents
                                  |> List.sortByDescending _.Timestamp
                                  |> List.truncate 2
                                  |> List.rev

                              Some(path, recent)
                          | _ -> None)
                      |> Map.ofList
              }
          deleteWorktree = deleteWorktree agent embeddedTerminal clearAcceptedRecord rootPaths
          launchSession = fun req ->
              withValidatedPath req.Path "launchSession" (fun () ->
                  async {
                      let path = WorktreePath.value req.Path
                      let provider = CodingToolStatus.readConfiguredProvider path
                      let inv = CodingToolCli.build provider (CodingToolCli.Interactive req.Prompt)
                      return! SessionManager.spawnSession sessionAgent req.Path inv.AsShellString
                  })
          focusSession = fun wtPath ->
              withValidatedPath wtPath "focusSession" (fun () ->
                  SessionManager.focusSession sessionAgent wtPath)
          killSession = fun wtPath ->
              withValidatedPath wtPath "killSession" (fun () ->
                  SessionManager.killSession sessionAgent wtPath)
          archiveWorktree =
              updateArchivedBranchesWith
                  agent
                  rootPaths
                  (EmbeddedTerminal.withReservedCleanup embeddedTerminal)
                  Set.add
          unarchiveWorktree =
              updateArchivedBranchesWith
                  agent
                  rootPaths
                  (fun _ operation -> operation ())
                  Set.remove
          getBranches = fun repoIdStr ->
              async {
                  let repoId = PathUtils.toRepoId repoIdStr
                  let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)

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

                  let branchName = BranchName.value req.BranchName
                  let! fork = GitWorktree.forkWorktree root (BranchName.value req.BaseBranch) branchName
                  agent.Post(SchedulerState.StateMsg.ExpediteRefresh repoId)

                  // Fire-and-forget: when a prompt was supplied, spawn a tracked coding-agent
                  // window in the new worktree seeded with the config-driven skill invocation.
                  // Reuses SessionManager.launchAction (spawns+tracks when no window exists yet).
                  // A blank prompt is a no-op. Deferred until post-fork finishes below so the
                  // session starts with dependencies already installed.
                  let launchPromptSession () =
                      match req.Prompt with
                      | Some prompt when not (String.IsNullOrWhiteSpace prompt) ->
                          let newPath = fork.WorktreePath
                          // Provider is read directly from .treemon.json — the new worktree first (its
                          // config exists once create returns and can differ from the root working
                          // copy), then the root as fallback. A just-created worktree needs the root
                          // fallback because its own config may not exist yet; the other launch sites
                          // read the (already-present) per-worktree config directly.
                          let provider =
                              CodingToolStatus.readConfiguredProvider newPath
                              |> Option.orElse (CodingToolStatus.readConfiguredProvider root)
                          // The chosen skill wraps the prompt; "None" (req.Skill = None) launches
                          // the prompt verbatim, with no skill invocation.
                          let wrapped =
                              match req.Skill with
                              | Some skill -> CodingToolStatus.skillInvocation provider skill prompt
                              | None -> prompt
                          let cmd = (CodingToolCli.build provider (CodingToolCli.Interactive wrapped)).AsShellString
                          // The try/with is required: launchAction's PostAndAsyncReply(timeout=30s)
                          // throws on timeout, and Async.Ignore would swallow the Error case — an
                          // unguarded Async.Start could fault silently.
                          async {
                              try
                                  match! SessionManager.launchAction sessionAgent (WorktreePath newPath) cmd with
                                  | Ok () -> ()
                                  | Error msg -> Log.log "API" $"Auto-launch failed for {newPath}: {msg}"
                              with ex ->
                                  Log.log "API" $"Auto-launch crashed for {newPath}: {ex}"
                          }
                          |> Async.Start
                      | _ -> ()

                  // Post-fork setup (junctions, bd init, npm install) can take minutes, so run it in
                  // the background and surface its lifecycle on the worktree card via CardEventLog —
                  // the create call returns as soon as `git worktree add` succeeds, closing the
                  // modal promptly. The prompt auto-launch waits for deps, so it runs once post-fork
                  // finishes (success or failure); with no post-fork script there is nothing to wait
                  // for, so launch immediately.
                  match GitWorktree.postForkScriptPath root with
                  | None -> launchPromptSession ()
                  | Some _ ->
                      let eventKey = scopedBranchKey repoId branchName
                      Async.Start(
                          async {
                              try
                                  cardLog.Post(CardEventLog.PostForkStarted eventKey)
                                  let! result = GitWorktree.runPostFork root fork.WorktreePath fork.BaseRef branchName
                                  let status =
                                      match result with
                                      | Ok () -> StepStatus.Succeeded
                                      | Error msg ->
                                          Log.log "API" $"post-fork setup failed for {branchName}: {msg}"
                                          StepStatus.Failed msg
                                  cardLog.Post(CardEventLog.PostForkEnded(eventKey, status))
                                  agent.Post(SchedulerState.StateMsg.ExpediteRefresh repoId)
                              with ex ->
                                  Log.log "API" $"post-fork background task faulted for {branchName}: {ex.Message}"
                                  cardLog.Post(CardEventLog.PostForkEnded(eventKey, StepStatus.Failed ex.Message))
                              launchPromptSession ()
                          })

                  return fork.Warnings
              }
          openNewTab = fun wtPath ->
              withValidatedPath wtPath "openNewTab" (fun () ->
                  SessionManager.openNewTab sessionAgent wtPath)
          launchAction = fun req ->
              withValidatedPath req.Path "launchAction" (fun () ->
                  async {
                      let path = WorktreePath.value req.Path
                      let provider = CodingToolStatus.readConfiguredProvider path
                      let prompt = CodingToolStatus.actionPrompt provider req.Action
                      let command = CodingToolCli.build provider (CodingToolCli.Interactive prompt)
                      return! SessionManager.launchAction sessionAgent req.Path command.AsShellString
                  })
          reportActivity = fun level -> async { agent.Post(SchedulerState.StateMsg.ReportClientActivity(level, DateTimeOffset.UtcNow)) }
          saveCollapsedRepos = fun repos -> async { writeCollapsedRepos repos }
          saveTerminalPaneOpen = fun isOpen -> async { writeTerminalPaneOpen isOpen }
          saveCanvasPaneOpen = fun isOpen -> async { writeCanvasPaneOpen isOpen }
          saveOverviewPanelOpen = fun isOpen -> async { writeOverviewPanelOpen isOpen }
          saveWorkspaceWidth = fun width -> async { writeWorkspaceWidth width }
          resumeSession = fun wtPath ->
              withValidatedPath wtPath "resumeSession" (fun () ->
                  async {
                      let path = WorktreePath.value wtPath
                      let provider = CodingToolStatus.readConfiguredProvider path
                      // Only resume by stored ID when it belongs to the configured provider. Push
                      // Per-provider resume policy: the Copilot CLI resumes by stored session id. A
                      // future provider that resumes differently (or can't) gets its own arm — the
                      // compiler flags this match when a new provider case is added.
                      let sessionId =
                          match provider |> Option.defaultValue CodingToolProvider.Default with
                          | CodingToolProvider.CopilotCli ->
                              activityStore
                              |> Option.bind _.LatestSessionIdForWorktree(PathUtils.toWorktreePath path)
                      let inv = CodingToolCli.build provider (CodingToolCli.Resume sessionId)
                      return! SessionManager.spawnSession sessionAgent wtPath inv.AsShellString
                  })
          sendCanvasMessage = fun request ->
              withValidatedPathValue request.WorktreePath "sendCanvasMessage" CanvasMessageResult.Error (fun () ->
                  async {
                      let path = WorktreePath.value request.WorktreePath
                      let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)

                      let! outcome =
                          CanvasBridge.sendMessage (state.SessionStatuses |> Map.values) request

                      match outcome with
                      | CanvasBridge.Routed result -> return result
                      | CanvasBridge.QueuedNeedingSession result ->
                          match! CanvasBridge.beginPendingLaunch path with
                          | CanvasBridge.PendingLaunchJoined ->
                              Log.log
                                  "API"
                                  $"sendCanvasMessage: a launch is already starting for {request.Filename}"

                              return result
                          | CanvasBridge.PendingLaunchStarted ->
                              let provider = CodingToolStatus.readConfiguredProvider path
                              let prompt = CanvasPrompt.continueWorking path request.Filename
                              let command =
                                  CodingToolCli.build provider (CodingToolCli.Interactive prompt)

                              Log.log
                                  "API"
                                  $"sendCanvasMessage: no reachable session for {request.Filename}; launching one"

                              match!
                                  SessionManager.launchAction
                                      sessionAgent
                                      request.WorktreePath
                                      command.AsShellString
                                  |> Async.Catch
                              with
                              | Choice1Of2(Ok()) -> return result
                              | Choice1Of2(Error err) ->
                                  do! CanvasBridge.cancelPendingLaunch path

                                  return
                                      CanvasMessageResult.Error
                                          $"Could not start an interaction session for {request.Filename}: {err}"
                              | Choice2Of2 ex ->
                                  do! CanvasBridge.cancelPendingLaunch path

                                  return
                                      CanvasMessageResult.Error
                                          $"Could not start an interaction session for {request.Filename}: {ex.Message}"
                  })
          archiveCanvasDoc = fun req ->
              withValidatedPath req.WorktreePath "archiveCanvasDoc" (fun () ->
                  archiveCanvasDocImpl req)
          shareCanvasDoc = fun req ->
              withValidatedPath req.WorktreePath "shareCanvasDoc" (fun () ->
                  shareCanvasDocImpl req)
          saveLastViewedHashes = fun hashes -> async { writeLastViewedHashes hashes }
          loadLastViewedHashes = fun () -> async { return readLastViewedHashes () }
          getBridgeLiveness = fun paths -> async { return SessionBridge.getAllLiveness paths }
          // Roots are managed restart-to-apply: persist to global config only (no scheduler
          // message, no live-roots read). getWorktrees/createWorktree/path-validation keep using
          // the `rootPaths` captured at startup above — correct, since roots only change across
          // (re)starts (the treemon.ps1 add/remove shims trigger the restart).
          addRoot = fun path -> async { return addRootToConfig path }
          removeRoot = fun path -> async { return removeRootFromConfig path }
          getRoots = fun () -> async { return readWorktreeRootsConfig () }
          getDiffCategoryReport =
            fun path ->
                async {
                    let! state = agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)

                    match RefreshScheduler.tryFindOwningRepo state path with
                    | None -> return Error $"No watched worktree found at '{path}'"
                    | Some(repoId, _) -> return! diffCategoryReport (RepoId.value repoId)
                }
          getOverviewHistory =
            fun requestedWindow ->
                match snapshotStore with
                | Some store ->
                    async {
                        return
                            store.ReadLatestWindow(
                                DateTimeOffset.UtcNow,
                                requestedWindow
                            )
                    }
                | None ->
                    async {
                        return
                            raise (
                                InvalidOperationException(
                                    "Overview history store is required outside demo and fixture modes."
                                )
                            )
                    } }
