open Saturn
open Giraffe
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open System
open System.Threading
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Shared
open Server
open Treemon.TerminalHosting

let readDeployBranch () =
    ProcessRunner.text
        { ProcessRunner.Spawn.create "git" with
            Context = "Startup"
            Limits = ProcessRunner.CaptureLimits.small }
        [ "rev-parse"; "--abbrev-ref"; "HEAD" ]
    |> Async.RunSynchronously
    |> Option.bind (fun branch ->
        match branch with
        | "main" | "master" -> None
        | name -> Some name)

let readAppVersion () =
    let serverGuid = System.Guid.NewGuid().ToString("N")

    let buildTime =
        let path = System.IO.Path.Combine("wwwroot", "version.json")

        match System.IO.File.Exists(path) with
        | false ->
            Log.log "Startup" "No wwwroot/version.json found"
            ""
        | true ->
            let json = System.IO.File.ReadAllText(path)
            use doc = System.Text.Json.JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty("buildTime") with
            | true, elem -> elem.GetString()
            | false, _ -> ""

    $"{buildTime}|{serverGuid}"

let internal configureLogging (builder: ILoggingBuilder) =
    builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore

[<RequireQualifiedAccess>]
type ServerMode =
    | Production
    | Standard of dashboardPort: int option * logDirectory: string option
    | Fixtures of path: string * dashboardPort: int option * logDirectory: string option
    | Demo of dashboardPort: int option * logDirectory: string option

type ServerConfig =
    { WorktreeRoots: string list
      Port: int
      CanvasPort: int option
      Mode: ServerMode }

[<RequireQualifiedAccess>]
type RunMode =
    | Server of ServerConfig
    | TerminalHostDeploymentPreflight

[<RequireQualifiedAccess>]
type ArgumentError =
    | InvalidPort of value: string
    | InvalidCanvasPort of value: string
    | InvalidDashboardPort of value: string
    | MultipleLogDestinations
    | DemoWithTestFixtures
    | ProductionLogRequiresStandaloneMode
    | CanvasPortMatchesServerPort of port: int
    | UnexpectedArgument of value: string

type private ParsedServerArguments =
    { Roots: string list
      Port: int
      CanvasPort: int option
      DashboardPort: int option
      TestFixtures: string option
      Demo: bool
      LogDestination: Log.Destination }

/// JSON contract consumed by Test-TerminalHostDeployment in treemon.ps1. Host fields are absent
/// when HasLiveHost is false.
type TerminalHostDeploymentPreflightResponse =
    { Layout: TerminalHostLayout
      HasLiveHost: bool
      Pid: int option
      ProcessStartTimeUtcTicks: int64 option
      ExecutablePath: string option
      TerminalCount: int option }

let private defaultCanvasPort = 5002

let private argumentErrorMessage = function
    | ArgumentError.InvalidPort value -> $"Invalid port number: {value}"
    | ArgumentError.InvalidCanvasPort value ->
        $"Invalid canvas port number: {value}"
    | ArgumentError.InvalidDashboardPort value ->
        $"Invalid dashboard port number: {value}"
    | ArgumentError.MultipleLogDestinations ->
        "Specify only one log destination"
    | ArgumentError.DemoWithTestFixtures ->
        "--demo and --test-fixtures are mutually exclusive"
    | ArgumentError.ProductionLogRequiresStandaloneMode ->
        "--production-log is not valid for development, demo, or fixture mode"
    | ArgumentError.CanvasPortMatchesServerPort port ->
        $"--canvas-port ({port}) must differ from the main --port ({port})"
    | ArgumentError.UnexpectedArgument value ->
        $"Unexpected argument: {value}"

let private parseServerArguments serverArguments =
    let rec parse (parsed: ParsedServerArguments) remaining =
        match remaining with
        | "--port" :: value :: rest ->
            match System.Int32.TryParse(value) with
            | true, port -> parse { parsed with Port = port } rest
            | false, _ -> Error(ArgumentError.InvalidPort value)
        | "--canvas-port" :: value :: rest ->
            match System.Int32.TryParse(value) with
            | true, port ->
                parse { parsed with CanvasPort = Some port } rest
            | false, _ -> Error(ArgumentError.InvalidCanvasPort value)
        | "--dashboard-port" :: value :: rest ->
            match System.Int32.TryParse(value) with
            | true, port ->
                parse { parsed with DashboardPort = Some port } rest
            | false, _ ->
                Error(ArgumentError.InvalidDashboardPort value)
        | "--no-canvas" :: rest ->
            parse { parsed with CanvasPort = None } rest
        | "--test-fixtures" :: path :: rest ->
            parse { parsed with TestFixtures = Some path } rest
        | "--demo" :: rest ->
            parse { parsed with Demo = true } rest
        | "--log-dir" :: path :: rest ->
            match parsed.LogDestination with
            | Log.Destination.Isolated None ->
                parse
                    { parsed with
                        LogDestination =
                            Log.Destination.Isolated(Some path) }
                    rest
            | _ -> Error ArgumentError.MultipleLogDestinations
        | "--production-log" :: rest ->
            match parsed.LogDestination with
            | Log.Destination.Isolated None ->
                parse
                    { parsed with
                        LogDestination = Log.Destination.Production }
                    rest
            | _ -> Error ArgumentError.MultipleLogDestinations
        | path :: rest when not (path.StartsWith("--")) ->
            parse { parsed with Roots = path :: parsed.Roots } rest
        | [] -> Ok parsed
        | unexpected :: _ ->
            Error(ArgumentError.UnexpectedArgument unexpected)

    parse
        { Roots = []
          Port = 5000
          CanvasPort = Some defaultCanvasPort
          DashboardPort = None
          TestFixtures = None
          Demo = false
          LogDestination = Log.Destination.Isolated None }
        serverArguments

let private serverConfig parsed mode =
    match parsed.CanvasPort with
    | Some canvasPort when canvasPort = parsed.Port ->
        Error(ArgumentError.CanvasPortMatchesServerPort canvasPort)
    | _ ->
        Ok(
            RunMode.Server
                { WorktreeRoots =
                    parsed.Roots
                    |> List.rev
                    |> List.map _.TrimEnd([| '\\'; '/' |])
                  Port = parsed.Port
                  CanvasPort = parsed.CanvasPort
                  Mode = mode }
        )

let private toRunMode parsed =
    match
        parsed.Demo,
        parsed.TestFixtures,
        parsed.LogDestination
    with
    | true, Some _, _ ->
        Error ArgumentError.DemoWithTestFixtures
    | true, None, Log.Destination.Production
    | false, Some _, Log.Destination.Production ->
        Error ArgumentError.ProductionLogRequiresStandaloneMode
    | false, None, Log.Destination.Production
        when parsed.DashboardPort.IsSome ->
        Error ArgumentError.ProductionLogRequiresStandaloneMode
    | true, None, Log.Destination.Isolated logDirectory ->
        Ok(
            RunMode.Server
                { WorktreeRoots = []
                  Port = parsed.Port
                  CanvasPort = None
                  Mode =
                    ServerMode.Demo(
                        parsed.DashboardPort,
                        logDirectory
                    ) }
        )
    | false, Some path, Log.Destination.Isolated logDirectory ->
        ServerMode.Fixtures(
            path,
            parsed.DashboardPort,
            logDirectory
        )
        |> serverConfig parsed
    | false, None, Log.Destination.Isolated logDirectory ->
        ServerMode.Standard(parsed.DashboardPort, logDirectory)
        |> serverConfig parsed
    | false, None, Log.Destination.Production ->
        ServerMode.Production |> serverConfig parsed

let parseArgs (args: string array) =
    match args |> Array.toList with
    | [ "--terminal-host-deployment-preflight" ] ->
        Ok RunMode.TerminalHostDeploymentPreflight
    | serverArguments ->
        serverArguments
        |> parseServerArguments
        |> Result.bind toRunMode

let internal dashboardOrigins (config: ServerConfig) =
    match config.Mode with
    | ServerMode.Standard(Some port, _)
    | ServerMode.Demo(Some port, _)
    | ServerMode.Fixtures(_, Some port, _) ->
        [ $"http://localhost:{port}"
          $"http://127.0.0.1:{port}" ]
    | ServerMode.Production
    | ServerMode.Standard(None, _)
    | ServerMode.Demo(None, _)
    | ServerMode.Fixtures(_, None, _) -> []

let private populateAgentFromFixtures (agent: MailboxProcessor<SchedulerState.StateMsg>) (fixtures: FixtureData) =
    fixtures.Worktrees.Repos
    |> List.iter (fun repo ->
        let worktreeInfos =
            repo.Worktrees
            |> List.map (fun wt ->
                { Path = WorktreePath.value wt.Path |> Server.PathUtils.normalizePath
                  Head = ""
                  Branch = Some wt.Branch }: GitWorktree.WorktreeInfo)

        agent.Post(SchedulerState.UpdateWorktreeList(repo.RepoId, worktreeInfos))
        Log.log "Startup" $"Populated agent with {List.length worktreeInfos} fixture worktrees for repo '{RepoId.value repo.RepoId}'")

let private buildDemoApi (startTime: System.DateTimeOffset) : IWorktreeApi =
    let cachedFrame = ref (None: (int * FixtureData) option)

    let getFrame () =
        let elapsed = System.DateTimeOffset.Now - startTime
        let positionSeconds = int elapsed.TotalSeconds
        match cachedFrame.Value with
        | Some (cached, frame) when cached = positionSeconds -> frame
        | _ ->
            let frame = DemoFixture.selectFrame startTime System.DateTimeOffset.Now
            cachedFrame.Value <- Some (positionSeconds, frame)
            frame

    WorktreeApi.readOnlyApi
        "demo mode"
        (fun () -> async { return (getFrame ()).Worktrees })
        (fun () -> async { return (getFrame ()).SyncStatus })

let private buildRemotingHandler (api: IWorktreeApi) =
    Remoting.createApi ()
    |> Remoting.fromValue api
    |> Remoting.withErrorHandler (fun ex routeInfo ->
        Log.log "API" $"Error in {routeInfo.methodName}: {ex}"
        Propagate ex.Message)
    |> Remoting.buildHttpHandler

/// Path of the orphan `roots.json` under the (TREEMON_CONFIG_DIR-aware) global config dir. The
/// file is a stale migration artifact read by nothing per the config investigation.
let private orphanRootsPath () =
    System.IO.Path.Combine(GlobalConfig.globalConfigDir (), "roots.json")

/// Reads the orphan `roots.json` (schema `{ "WorktreeRoots": [...] }`), returning its roots or
/// `[]` when absent/unreadable. Pure read — the file is deleted only after its roots are durably
/// persisted (see resolveWorktreeRoots), so a failed config write can never lose the migrated set.
let private readOrphanRoots () : string list =
    let orphanPath = orphanRootsPath ()
    if not (System.IO.File.Exists orphanPath) then []
    else
        try
            use doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText orphanPath)
            match doc.RootElement.TryGetProperty("WorktreeRoots") with
            | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.Array ->
                prop.EnumerateArray()
                |> Seq.choose (fun el ->
                    if el.ValueKind = System.Text.Json.JsonValueKind.String then Some(el.GetString())
                    else None)
                |> List.ofSeq
            | _ -> []
        with ex ->
            Log.log "Startup" $"Failed to read orphan roots.json: {ex.Message}"
            []

/// Removes the orphan `roots.json` once its roots have been migrated into the global config.
/// Best-effort: a delete failure is logged, not fatal (the migrated roots are already persisted).
let private deleteOrphanRoots () =
    let orphanPath = orphanRootsPath ()
    if System.IO.File.Exists orphanPath then
        try
            System.IO.File.Delete orphanPath
            Log.log "Startup" "Deleted migrated orphan roots.json"
        with ex ->
            Log.log "Startup" $"Failed to delete orphan roots.json: {ex.Message}"

/// Outcome of resolving the effective worktree roots at startup: the resolved set plus whether the
/// boundary should persist it (first-time migration into the global config) and consume the orphan
/// `roots.json`. A pure decision — the caller (`persistResolvedRoots`) performs the write/delete.
type internal RootsResolution =
    { Roots: string list
      /// First-time persist: the `worktreeRoots` key is absent and the resolved set is non-empty.
      PersistRoots: bool
      /// Resolved set came from the orphan `roots.json` — delete it after a successful persist.
      ConsumeOrphan: bool }

/// Resolves the effective worktree roots at startup by priority:
///   1. roots passed as CLI args (used by `dev`/tests; preserves current arg behavior),
///   2. else `worktreeRoots` from the global `config.json` (a PRESENT key, even an explicit empty
///      list, wins here — the user may have curated every root away),
///   3. else (the key is ABSENT) a one-time import of the orphan `roots.json`.
/// Pure/read-only: it only reads config + orphan state and decides the resolved set, whether a
/// first-time persist is needed, and whether the orphan should be consumed — it performs no writes
/// or deletes, so the resolution decision is unit-testable without mutating the filesystem.
/// `persistResolvedRoots` applies those effects at the boundary. Demo/fixture modes never call this;
/// their roots stay `[]`.
let internal resolveWorktreeRoots (cliRoots: string list) : RootsResolution =
    // `None` = the `worktreeRoots` key is absent (fresh install / pre-migration); `Some roots` =
    // the key is present (possibly an explicit empty list). Gating migration on KEY ABSENCE — not
    // `List.isEmpty` — is what stops an explicit `worktreeRoots:[]` from being resurrected by a
    // stale orphan `roots.json` or overwritten by CLI args on restart.
    let configRoots = GlobalConfig.tryReadWorktreeRootsConfig ()
    let configHasKey = Option.isSome configRoots

    let resolved, cameFromOrphan =
        if not (List.isEmpty cliRoots) then cliRoots, false
        else
            match configRoots with
            | Some roots -> roots, false
            | None ->
                let orphanRoots = readOrphanRoots ()
                orphanRoots, not (List.isEmpty orphanRoots)

    { Roots = resolved
      PersistRoots = not configHasKey && not (List.isEmpty resolved)
      ConsumeOrphan = cameFromOrphan }

/// Boundary effect for `resolveWorktreeRoots`: persists a first-time-resolved root set into the
/// global config and deletes the migrated orphan `roots.json` only after that write succeeds (so a
/// failed write can never drop the migration). A no-op when the resolution needs no persistence.
let internal persistResolvedRoots (resolution: RootsResolution) =
    if resolution.PersistRoots then
        match GlobalConfig.writeWorktreeRoots resolution.Roots with
        | Ok () ->
            Log.log "Startup" $"Persisted {List.length resolution.Roots} worktree root(s) to global config"
            if resolution.ConsumeOrphan then deleteOrphanRoots ()
        | Error msg ->
            Log.log "Startup" $"Failed to persist worktree roots: {msg}"

let internal usesSessionActivity (config: ServerConfig) =
    match config.Mode with
    | ServerMode.Production
    | ServerMode.Standard _ -> true
    | ServerMode.Fixtures _
    | ServerMode.Demo _ -> false

/// Creates one port-scoped runtime store and seeds it from disk, logging the path so a stale or
/// unexpected file is visible in the startup log.
let private loadRuntimeStore
    (label: string)
    (path: string)
    (create: string -> PersistentStore.Store<'K, 'V>)
    : PersistentStore.Store<'K, 'V> =
    Log.log "Startup" $"{label}: {path}"
    let store = create path
    store.Load()
    store

/// Flushes runtime stores at shutdown, bounded so a wedged disk cannot hang exit. Each store
/// contributes a labelled flush rather than its own copy of this timeout handling.
let private flushRuntimeStores (stores: (string * (unit -> Async<Result<unit, string>>)) list) =
    stores
    |> List.iter (fun (label, flush) ->
        try
            match Async.RunSynchronously(flush (), timeout = 5000) with
            | Ok() -> ()
            | Error error -> Log.log "Shutdown" error
        with :? System.TimeoutException ->
            Log.log "Shutdown" $"Timed out flushing {label}")

let internal runHostWithCapture
    (startHost: unit -> unit)
    (waitForShutdown: unit -> unit)
    (stopping: CancellationToken)
    (capture: (CancellationToken -> Async<unit>) option)
    =
    match capture with
    | None ->
        startHost ()
        waitForShutdown ()
    | Some workflow ->
        let loop = BackgroundLoop.start workflow
        let stoppingRegistration =
            stopping.Register(fun () -> BackgroundLoop.cancel loop)
        Log.log "Startup" "Overview snapshot capture started"

        try
            startHost ()
            waitForShutdown ()
        finally
            stoppingRegistration.Dispose()
            BackgroundLoop.stop "Overview snapshot capture" loop

let private deploymentPreflightResponse
    (layout: TerminalHostLayout)
    (result: TerminalHostClient.DeploymentPreflightResult option)
    =
    match result with
    | None ->
        { Layout = layout
          HasLiveHost = false
          Pid = None
          ProcessStartTimeUtcTicks = None
          ExecutablePath = None
          TerminalCount = None }
    | Some host ->
        { Layout = layout
          HasLiveHost = true
          Pid = Some host.Pid
          ProcessStartTimeUtcTicks =
            Some host.ProcessStartTimeUtcTicks
          ExecutablePath = Some host.ExecutablePath
          TerminalCount = Some host.TerminalCount }

let private runTerminalHostDeploymentPreflight () =
    let config = TerminalHostClient.defaultConfig []

    match
        TerminalHostClient.preflightDeploymentWith config
        |> Async.RunSynchronously
    with
    | Error error ->
        Console.Error.WriteLine($"TerminalHost deployment preflight failed: {error}")
        2
    | Ok result ->
        let layout =
            TerminalHostLayout.forStateDirectory config.HostStateDirectory

        let response = deploymentPreflightResponse layout result

        let jsonOptions =
            System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web,
                DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            )

        System.Text.Json.JsonSerializer.Serialize(response, jsonOptions)
        |> Console.Out.WriteLine
        0

[<EntryPoint>]
let main args =
    let config =
        match parseArgs args with
        | Error error ->
            eprintfn $"{argumentErrorMessage error}"
            exit 1
        | Ok RunMode.TerminalHostDeploymentPreflight ->
            runTerminalHostDeploymentPreflight () |> exit
        | Ok(RunMode.Server config) -> config

    let serverUrl = $"http://localhost:{config.Port}"
    let logDestination, fixtures, demo =
        match config.Mode with
        | ServerMode.Production ->
            Log.Destination.Production, None, false
        | ServerMode.Standard(_, directory) ->
            Log.Destination.Isolated directory, None, false
        | ServerMode.Fixtures(path, _, directory) ->
            Log.Destination.Isolated directory, Some path, false
        | ServerMode.Demo(_, directory) ->
            Log.Destination.Isolated directory, None, true

    let logPath =
        match
            Log.resolvePath
                (System.IO.Directory.GetCurrentDirectory())
                (System.IO.Path.GetTempPath())
                config.Port
                Environment.ProcessId
                (Guid.NewGuid())
                logDestination
        with
        | Ok path -> path
        | Error error ->
            eprintfn $"Invalid log configuration: {error}"
            exit 1

    match Log.init logPath with
    | Ok() ->
        printfn $"Server log: {logPath}"
    | Error Log.InitializationError.InvalidPath ->
        eprintfn "Invalid log configuration: log path must be absolute and contain no control characters"
        exit 1
    | Error Log.InitializationError.AlreadyInitialized ->
        eprintfn "Invalid log configuration: log destination was already selected"
        exit 1
    | Error(Log.InitializationError.CannotOpen(path, error)) ->
        eprintfn $"Could not initialize server log '{path}': {error}"
        exit 1

    // Effective roots: CLI args > global config > orphan import (persisted first-time). Resolution
    // is a pure decision; `persistResolvedRoots` applies the first-time persist + orphan cleanup at
    // this startup boundary. Demo and fixture modes bypass resolution entirely — they serve
    // synthetic data, so roots stay [].
    let worktreeRoots =
        if not (usesSessionActivity config) then
            []
        else
            let resolution = resolveWorktreeRoots config.WorktreeRoots
            persistResolvedRoots resolution
            resolution.Roots

    worktreeRoots |> List.iter (fun root -> Log.log "Startup" $"Worktree root: {root}")
    Log.log "Startup" $"Server URL: {serverUrl}"

    let appVersion = readAppVersion ()
    Log.log "Startup" $"App version: {appVersion}"

    let deployBranch = readDeployBranch ()
    let deployBranchDisplay = deployBranch |> Option.defaultValue "(main)"
    Log.log "Startup" $"Deploy branch: {deployBranchDisplay}"

    fixtures |> Option.iter (fun path ->
        Log.log "Startup" $"Test fixtures: {path}")

    worktreeRoots |> List.iter (fun root -> printfn "Monitoring worktrees under: %s" root)

    let processIdentityResolver =
        ProcessIdentityResolverRuntime.defaultResolver

    let embeddedTerminal =
        if demo then None
        else
            dashboardOrigins config
            |> EmbeddedTerminal.createWithProcessIdentityResolver
                processIdentityResolver
                serverUrl
            |> Some

    let remotingApi, schedulerAgent, activityRuntime, schedulerLoop, runtimeStoreFlushes =
        if demo then
            Log.log "Startup" "Demo mode: serving cycling fixture frames"
            buildDemoApi System.DateTimeOffset.Now |> buildRemotingHandler, None, None, None, []
        else
            let agent = SchedulerState.createAgent ()
            let cardLog = CardEventLog.createAgent ()
            let sessionAgent = SessionManager.createAgent ()
            let terminalLaunch =
                TerminalLaunch.create
                    sessionAgent
                    embeddedTerminal.Value
            CanvasDocOwnership.load ()

            match fixtures with
            | Some path ->
                match WorktreeApi.loadFixtures path with
                | Ok fixtures ->
                    populateAgentFromFixtures agent fixtures
                    Log.log "Startup" "Fixture mode: scheduler background loop skipped"
                | Error msg ->
                    Log.log "Startup" $"ERROR: {msg}"
                    System.Environment.Exit(1)

                WorktreeApi.worktreeApiWithLaunch
                    terminalLaunch
                    { Agent = agent
                      CardLog = cardLog
                      SessionAgent = sessionAgent
                      EmbeddedTerminal = embeddedTerminal.Value
                      TerminalSessionCleanup = WorktreeCleanup.noSessionClose
                      ActivityStore = None
                      SnapshotStore = None
                      AutoSyncStore = None
                      TerminalHostRestartSessions = None
                      WorktreeRoots = worktreeRoots
                      TestFixtures = fixtures
                      AppVersion = appVersion
                      DeployBranch = deployBranch }
                |> buildRemotingHandler,
                Some agent,
                None,
                None,
                []
            | None ->
                let dbPath = System.IO.Path.Combine("data", $"session-activity-{config.Port}.db")
                Log.log "Startup" $"Session activity store db: {dbPath}"
                let rootPaths = RefreshScheduler.buildRootPaths worktreeRoots
                let activity =
                    SessionActivityRuntime.createWithProcessIdentityResolver
                        processIdentityResolver
                        dbPath
                        agent
                        rootPaths
                let store = activity.Components.Store

                let mergedStore =
                    loadRuntimeStore
                        "Merged PR store"
                        (MergedPrStore.filePathForPort config.Port)
                        MergedPrStore.create

                let autoSyncStore =
                    loadRuntimeStore
                        "Auto-sync store"
                        (AutoSyncStore.filePathForPort config.Port)
                        AutoSyncStore.create

                let schedulerServices: RefreshScheduler.SchedulerServices =
                    { StartEmbeddedCommand =
                        terminalLaunch.StartEmbeddedCommand
                      ActivityStore = Some store
                      MergedPrStore = mergedStore
                      AutoSyncStore = autoSyncStore }

                let scheduler =
                    try
                        BackgroundLoop.start
                            (RefreshScheduler.run
                                agent
                                schedulerServices
                                worktreeRoots)
                    with _ ->
                        SessionActivityRuntime.shutdown activity None
                        reraise ()

                try
                    activity.Components.Service.Start()
                    Log.log "Startup" "Session activity ingestion started"
                    Log.log "Startup" "Scheduler background loop started"

                    WorktreeApi.worktreeApiWithLaunch
                        terminalLaunch
                        { Agent = agent
                          CardLog = cardLog
                          SessionAgent = sessionAgent
                          EmbeddedTerminal = embeddedTerminal.Value
                          TerminalSessionCleanup =
                            TerminalSessionCleanup.terminalSessionCleanup
                                activity.Components.Service
                          ActivityStore = Some store
                          SnapshotStore = Some activity.SnapshotStore
                          AutoSyncStore = Some autoSyncStore
                          TerminalHostRestartSessions =
                            Some(fun terminals ->
                                TerminalSessionActivity.restartSessions
                                    CodingToolStatus.readConfiguredProvider
                                    activity.Components.Service.QueryTerminalActivity
                                    DateTimeOffset.UtcNow
                                    terminals)
                          WorktreeRoots = worktreeRoots
                          TestFixtures = fixtures
                          AppVersion = appVersion
                          DeployBranch = deployBranch }
                    |> buildRemotingHandler,
                    Some agent,
                    Some activity,
                    Some scheduler,
                    [ "merged PR store", mergedStore.Flush
                      "auto-sync store", autoSyncStore.Flush ]
                with _ ->
                    SessionActivityRuntime.shutdown activity (Some scheduler)
                    reraise ()

    let sessionActivityService =
        activityRuntime |> Option.map _.Components.Service

    let capture =
        activityRuntime
        |> Option.map _.Capture.Run

    // The register/attribute routes need the scheduler agent for their known-worktree guard. In
    // demo mode there is no agent (and the canvas doc server is never started — see above), so
    // these are unavailable there; bridge-status stays available and simply reports nothing
    // registered.
    // register/attribute are state-changing POST endpoints reachable by the same cross-origin
    // vector as the remoting surface (attribute's sessionId feeds a coding-agent launch), so they
    // carry the same HttpSecurity.csrfGuard: a POST with a non-loopback Origin/Referer is rejected
    // 403 while a MISSING one is allowed — the non-browser canvas-bridge extension (Node fetch)
    // sends neither header, so it keeps registering/attributing. POST filters first, so the guard
    // only ever evaluates the state-changing request it protects.
    let canvasAgentRoutes =
        match schedulerAgent with
        | Some agent ->
            [ route "/api/canvas/register" >=> POST >=> HttpSecurity.csrfGuard >=> CanvasDocServer.canvasRegisterHandler processIdentityResolver agent
              route "/api/canvas/attribute" >=> POST >=> HttpSecurity.csrfGuard >=> CanvasDocServer.canvasAttributeHandler agent ]
        | None -> []

    // POST /api/session/activity: the push-model status ingestion endpoint. Same cross-origin
    // vector as the canvas POSTs, so it carries the same HttpSecurity.csrfGuard (a non-loopback
    // Origin/Referer is rejected 403; a MISSING one — the non-browser reporting extension's Node
    // fetch — is allowed). POST filters first so the guard only evaluates the request it protects.
    let sessionActivityRoutes =
        match sessionActivityService with
        | Some svc -> [ route "/api/session/activity" >=> POST >=> HttpSecurity.csrfGuard >=> svc.Handler ]
        | None -> []

    let combinedRouter =
        choose (
            canvasAgentRoutes
            @ sessionActivityRoutes
            @ [ route "/api/canvas/bridge-status" >=> GET >=> CanvasDocServer.bridgeStatusHandler
                // CSRF hardening: the Fable.Remoting surface has no auth/CSRF token and does not
                // enforce a content type, so a cross-origin page could POST to state-changing
                // methods (e.g. createWorktree, which auto-launches a coding agent). The guard
                // rejects state-changing requests with a non-loopback Origin/Referer; a missing one
                // is allowed so the non-browser Cli and same-origin SPA keep working.
                HttpSecurity.csrfGuard >=> remotingApi ])

    let app =
        application {
            logging configureLogging
            use_router combinedRouter
            url serverUrl
            use_static "wwwroot"
            use_gzip
        }

    try
        let canvasHost =
            match schedulerAgent, config.CanvasPort with
            | Some agent, Some canvasPort ->
                Some(CanvasDocServer.start agent canvasPort)
            | _ -> None

        try
            use host = app.Build()
            let applicationLifetime =
                host.Services.GetService(typeof<IHostApplicationLifetime>)
                :?> IHostApplicationLifetime

            runHostWithCapture
                host.Start
                (fun () ->
                    host.WaitForShutdownAsync()
                        .GetAwaiter()
                        .GetResult())
                applicationLifetime.ApplicationStopping
                capture
        finally
            canvasHost
            |> Option.iter (fun host ->
                try
                    host.StopAsync().GetAwaiter().GetResult()
                finally
                    host.DisposeAsync().GetAwaiter().GetResult())
    finally
        activityRuntime
        |> Option.iter (fun runtime ->
            Log.log "Shutdown" "Stopping session activity"
            SessionActivityRuntime.shutdown runtime schedulerLoop)

        flushRuntimeStores runtimeStoreFlushes

    0
