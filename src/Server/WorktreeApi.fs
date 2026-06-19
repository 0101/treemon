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

let private canvasSpawnInFlight = ConcurrentDictionary<string, bool>()

let loadFixtures (path: string) : Result<FixtureData, string> =
    try
        let json = File.ReadAllText(path)
        let converter = Fable.Remoting.Json.FableJsonConverter()
        let data = JsonConvert.DeserializeObject<FixtureData>(json, converter)
        // Sanitize null lists — Fable.Remoting client can't deserialize null as F# list
        let sanitized =
            { data with
                Worktrees =
                    { data.Worktrees with
                        Repos =
                            data.Worktrees.Repos
                            |> List.map (fun r ->
                                { r with
                                    Worktrees =
                                        r.Worktrees
                                        |> List.map (fun wt ->
                                            { wt with
                                                CanvasDocs =
                                                    if obj.ReferenceEquals(wt.CanvasDocs, null) then []
                                                    else wt.CanvasDocs }) }) } }
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
      saveCanvasPosition = fun _ -> async { return () }
      resumeSession = fun _ -> async { return Error $"Session management is not available in {modeName}" }
      sendCanvasMessage = fun _ -> async { return CanvasMessageResult.Queued }
      archiveCanvasDoc = fun _ -> async { return Error $"Archive canvas doc is not available in {modeName}" }
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
      Branch = wt.Branch |> Option.defaultValue WorktreeStatus.DetachedBranchName
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

/// Directory holding the machine-level Treemon config (`config.json`), normally `~/.treemon`.
/// The `TREEMON_CONFIG_DIR` override exists for test isolation: on Windows
/// `Environment.GetFolderPath(UserProfile)` ignores the USERPROFILE/HOME env vars, so an
/// in-process test can only redirect the config dir via this explicit override.
let internal globalConfigDir () =
    Environment.GetEnvironmentVariable("TREEMON_CONFIG_DIR")
    |> Option.ofObj
    |> Option.filter (fun d -> d <> "")
    |> Option.defaultWith (fun () ->
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".treemon"))

let private globalConfigPath () =
    Path.Combine(globalConfigDir (), "config.json")

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

/// Serializes every write to the machine-level `config.json`. All global-config writers
/// (collapsedRepos, canvas state, lastViewedHashes, worktreeRoots) funnel through
/// `updateGlobalConfig`, so this lock makes the server the single serialized writer and stops
/// concurrent read-modify-write cycles from clobbering each other's keys.
let private globalConfigLock = obj ()

let private updateGlobalConfig (description: string) (update: System.Text.Json.Nodes.JsonObject -> unit) : Result<unit, string> =
    lock globalConfigLock (fun () ->
        let configPath = globalConfigPath ()
        try
            let dir = Path.GetDirectoryName(configPath)
            if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore

            let root =
                if File.Exists(configPath) then
                    try File.ReadAllText(configPath) |> System.Text.Json.Nodes.JsonNode.Parse :?> System.Text.Json.Nodes.JsonObject
                    with _ -> System.Text.Json.Nodes.JsonObject()
                else System.Text.Json.Nodes.JsonObject()

            update root

            let options = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
            File.WriteAllText(configPath, root.ToJsonString(options))
            Ok()
        with ex ->
            Log.log "Config" $"Failed to save {description}: {ex.Message}"
            Error $"Failed to save {description}: {ex.Message}")

let private writeCollapsedRepos (repos: RepoId list) =
    updateGlobalConfig "collapsed repos" (fun root ->
        let repoArray = System.Text.Json.Nodes.JsonArray(repos |> List.map (fun (RepoId s) -> System.Text.Json.Nodes.JsonValue.Create(s) :> System.Text.Json.Nodes.JsonNode) |> List.toArray)
        root["collapsedRepos"] <- repoArray)
    |> ignore // best-effort UI state; failures are logged in updateGlobalConfig

/// Reads the machine-level set of watched worktree roots (`worktreeRoots` in `config.json`),
/// distinguishing a MISSING key (`None`) from a present-but-empty list (`Some []`). The startup
/// resolver depends on that distinction: an explicit `worktreeRoots:[]` means the user curated
/// every root away, so it must NOT be treated like a fresh install and repopulated from CLI args
/// or a stale orphan `roots.json`. A malformed (non-array) value is reported as `None` — absent —
/// matching the original lenient behavior. `internal` so the resolver (`Program.fs`) shares it.
let internal tryReadWorktreeRootsConfig () : string list option =
    withConfigDocument None (fun root ->
        match root.TryGetProperty("worktreeRoots") with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.Array ->
            prop.EnumerateArray()
            |> Seq.choose (fun el ->
                if el.ValueKind = System.Text.Json.JsonValueKind.String then Some(el.GetString())
                else None)
            |> List.ofSeq
            |> Some
        | _ -> None)

/// Flattens `tryReadWorktreeRootsConfig` to a plain list (missing key -> `[]`) for callers that
/// don't need the missing-vs-empty distinction: the `getRoots` endpoint and the add/remove
/// read-modify-write. `internal` so the startup resolver and the endpoint can share the reader.
let internal readWorktreeRootsConfig () : string list =
    tryReadWorktreeRootsConfig () |> Option.defaultValue []

/// Persists the watched worktree roots through the locked single-writer path, leaving every
/// other global-config key untouched. Returns the write outcome so the `addRoot`/`removeRoot`
/// endpoints can surface a persistence failure to the CLI instead of reporting a false success.
/// `internal` so the startup resolver can also write through this one helper.
let internal writeWorktreeRoots (roots: string list) : Result<unit, string> =
    updateGlobalConfig "worktree roots" (fun root ->
        let rootArray =
            System.Text.Json.Nodes.JsonArray(
                roots
                |> List.map (fun r -> System.Text.Json.Nodes.JsonValue.Create(r) :> System.Text.Json.Nodes.JsonNode)
                |> List.toArray)
        root["worktreeRoots"] <- rootArray)

/// Canonical comparison form for a worktree root: absolute path with trailing separators
/// trimmed. Total (never throws) so it is safe to fold over already-stored roots — a malformed
/// stored entry falls back to its raw value rather than aborting the whole add/remove.
let private canonicalRoot (path: string) =
    try Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    with _ -> path

/// Normalizes a caller-supplied root path (absolute, trailing separators trimmed), surfacing a
/// readable error for blank or malformed input so the CLI can report it.
let private tryNormalizeRoot (path: string) : Result<string, string> =
    if String.IsNullOrWhiteSpace path then Error "Path is empty."
    else
        try Ok(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        with ex -> Error $"Invalid path '{path}': {ex.Message}"

/// Adds a worktree root to the global config (restart-to-apply). Normalizes and verifies the
/// path is an existing directory, then read-modify-writes the roots list via the locked
/// single-writer helpers, surfacing a persistence failure rather than a false success. Adding an
/// already-watched path is a no-op success. add/remove are driven by the (serialized)
/// `tm add`/`tm remove` CLI, so the read-then-write is not contended in practice; the write
/// itself is serialized by `globalConfigLock`.
let internal addRootToConfig (path: string) : Result<unit, string> =
    tryNormalizeRoot path
    |> Result.bind (fun normalized ->
        if not (Directory.Exists normalized) then
            Error $"Path does not exist or is not a directory: {normalized}"
        else
            let existing = readWorktreeRootsConfig ()
            if existing |> List.exists (fun r -> pathEquals (canonicalRoot r) normalized) then
                Ok()
            else
                writeWorktreeRoots (existing @ [ normalized ]))

/// Removes a worktree root from the global config (restart-to-apply). Does not require the path
/// to still exist on disk (a deleted root is removable); reports an error when the path is not
/// currently watched, and surfaces a persistence failure instead of a false success.
let internal removeRootFromConfig (path: string) : Result<unit, string> =
    tryNormalizeRoot path
    |> Result.bind (fun normalized ->
        let existing = readWorktreeRootsConfig ()
        let remaining = existing |> List.filter (fun r -> not (pathEquals (canonicalRoot r) normalized))
        if List.length remaining = List.length existing then
            Error $"Not a watched root: {normalized}"
        else
            writeWorktreeRoots remaining)

let private readCanvasPaneOpen () : bool =
    withConfigDocument false (fun root ->
        match root.TryGetProperty("canvasPaneOpen") with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.True -> true
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.False -> false
        | _ -> false)

let private writeCanvasPaneOpen (isOpen: bool) =
    updateGlobalConfig "canvas pane open state" (fun root ->
        root["canvasPaneOpen"] <- System.Text.Json.Nodes.JsonValue.Create(isOpen))
    |> ignore // best-effort UI state; failures are logged in updateGlobalConfig

let private readCanvasPosition () : CanvasPosition =
    withConfigDocument CanvasPosition.Right (fun root ->
        match root.TryGetProperty("canvasPosition") with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.String ->
            match prop.GetString() with
            | "left" -> CanvasPosition.Left
            | "right" -> CanvasPosition.Right
            | "top" -> CanvasPosition.Top
            | "bottom" -> CanvasPosition.Bottom
            | _ -> CanvasPosition.Right
        | _ -> CanvasPosition.Right)

let private writeCanvasPosition (position: CanvasPosition) =
    let value =
        match position with
        | CanvasPosition.Left -> "left"
        | CanvasPosition.Right -> "right"
        | CanvasPosition.Top -> "top"
        | CanvasPosition.Bottom -> "bottom"
    updateGlobalConfig "canvas position" (fun root ->
        root["canvasPosition"] <- System.Text.Json.Nodes.JsonValue.Create(value))
    |> ignore // best-effort UI state; failures are logged in updateGlobalConfig

let private readLastViewedHashes () : Map<string, Map<string, string>> =
    withConfigDocument Map.empty (fun root ->
        match root.TryGetProperty("lastViewedHashes") with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.Object ->
            prop.EnumerateObject()
            |> Seq.choose (fun worktreeProp ->
                if worktreeProp.Value.ValueKind = System.Text.Json.JsonValueKind.Object then
                    let fileHashes =
                        worktreeProp.Value.EnumerateObject()
                        |> Seq.choose (fun fileProp ->
                            if fileProp.Value.ValueKind = System.Text.Json.JsonValueKind.String
                            then Some (fileProp.Name, fileProp.Value.GetString())
                            else None)
                        |> Map.ofSeq
                    Some (worktreeProp.Name, fileHashes)
                else None)
            |> Map.ofSeq
        | _ -> Map.empty)

let private writeLastViewedHashes (hashes: Map<string, Map<string, string>>) =
    updateGlobalConfig "last viewed hashes" (fun root ->
        let outerObj = System.Text.Json.Nodes.JsonObject()
        hashes |> Map.iter (fun worktreePath fileHashes ->
            let innerObj = System.Text.Json.Nodes.JsonObject()
            fileHashes |> Map.iter (fun filename hash ->
                innerObj[filename] <- System.Text.Json.Nodes.JsonValue.Create(hash))
            outerObj[worktreePath] <- innerObj)
        root["lastViewedHashes"] <- outerObj)
    |> ignore // best-effort UI state; failures are logged in updateGlobalConfig

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
        let ignorePredicate = TreemonConfig.readIgnoreWorktreePatterns () |> TreemonConfig.buildIgnorePredicate

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
              CanvasPosition = readCanvasPosition () }
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
            (fun () -> async { return { f.Worktrees with DeployBranch = None; SystemMetrics = None; EditorName = getEditorConfig () |> snd; CollapsedRepos = readCollapsedRepos (); CanvasPaneOpen = false; CanvasPosition = CanvasPosition.Right } })
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
          saveCanvasPaneOpen = fun isOpen -> async { writeCanvasPaneOpen isOpen }
          saveCanvasPosition = fun pos -> async { writeCanvasPosition pos }
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
