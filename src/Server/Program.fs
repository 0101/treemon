open Saturn
open Giraffe
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Shared
open Server

let readDeployBranch () =
    ProcessRunner.run "Startup" "git" "rev-parse --abbrev-ref HEAD"
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

type ServerConfig =
    { WorktreeRoots: string list
      Port: int
      CanvasPort: int option
      TestFixtures: string option
      Demo: bool }

let private defaultCanvasPort = 5002

let parseArgs (args: string array) =
    let rec parse roots port canvasPort testFixtures demo remaining =
        match remaining with
        | "--port" :: portStr :: rest ->
            match System.Int32.TryParse(portStr) with
            | true, p -> parse roots p canvasPort testFixtures demo rest
            | false, _ ->
                eprintfn $"Invalid port number: {portStr}"
                exit 1
        | "--canvas-port" :: portStr :: rest ->
            match System.Int32.TryParse(portStr) with
            | true, p -> parse roots port (Some p) testFixtures demo rest
            | false, _ ->
                eprintfn $"Invalid canvas port number: {portStr}"
                exit 1
        | "--no-canvas" :: rest ->
            parse roots port None testFixtures demo rest
        | "--test-fixtures" :: path :: rest ->
            parse roots port canvasPort (Some path) demo rest
        | "--demo" :: rest ->
            parse roots port canvasPort testFixtures true rest
        | path :: rest when not (path.StartsWith("--")) ->
            parse (roots @ [ path ]) port canvasPort testFixtures demo rest
        | [] -> roots, port, canvasPort, testFixtures, demo
        | unexpected :: _ ->
            eprintfn $"Unexpected argument: {unexpected}"
            exit 1

    match args |> Array.toList |> parse [] 5000 (Some defaultCanvasPort) None false with
    | _, _, _, Some _, true ->
        eprintfn "--demo and --test-fixtures are mutually exclusive"
        exit 1
    | _, port, _, _, true ->
        { WorktreeRoots = []
          Port = port
          CanvasPort = None
          TestFixtures = None
          Demo = true }
    | roots, port, canvasPort, testFixtures, _ ->
        // Zero positional roots is valid in normal mode: `start`/`dev` no longer require a path.
        // When no roots are passed the server resolves them from global config (or migrates a
        // legacy/orphan set) at startup — see resolveWorktreeRoots in main.
        match canvasPort with
        | Some cp when cp = port ->
            eprintfn $"--canvas-port ({cp}) must differ from the main --port ({port})"
            exit 1
        | _ ->
            { WorktreeRoots = roots |> List.map (fun r -> r.TrimEnd([| '\\'; '/' |]))
              Port = port
              CanvasPort = canvasPort
              TestFixtures = testFixtures
              Demo = false }

let private populateAgentFromFixtures (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (fixtures: FixtureData) =
    fixtures.Worktrees.Repos
    |> List.iter (fun repo ->
        let worktreeInfos =
            repo.Worktrees
            |> List.map (fun wt ->
                { Path = WorktreePath.value wt.Path |> Server.PathUtils.normalizePath
                  Head = ""
                  Branch = Some wt.Branch }: GitWorktree.WorktreeInfo)

        agent.Post(RefreshScheduler.UpdateWorktreeList(repo.RepoId, worktreeInfos))
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

type internal BackgroundLoop =
    { Cancellation: CancellationTokenSource
      Completion: Task }

type internal SessionActivityComponents =
    { Store: SessionActivityStore.SessionActivityStore
      Service: SessionActivityService.SessionActivityService }

type internal SessionActivityRuntime =
    { Components: SessionActivityComponents
      SnapshotStore: OverviewSnapshotStore.OverviewSnapshotStore
      Capture: OverviewSnapshotCapture.SnapshotCapture }

let internal usesSessionActivity (config: ServerConfig) =
    not config.Demo && config.TestFixtures.IsNone

let internal createSessionActivityComponents
    (dbPath: string)
    (scheduler: MailboxProcessor<RefreshScheduler.StateMsg>)
    =
    let store = new SessionActivityStore.SessionActivityStore(dbPath)

    try
        { Store = store
          Service = new SessionActivityService.SessionActivityService(store, scheduler) }
    with _ ->
        (store :> System.IDisposable).Dispose()
        reraise ()

let internal startBackgroundLoop (workflow: CancellationToken -> Async<unit>) =
    let cancellation = new CancellationTokenSource()

    { Cancellation = cancellation
      Completion =
        Async.StartAsTask(
            workflow cancellation.Token,
            cancellationToken = CancellationToken.None
        )
        :> Task }

let internal stopBackgroundLoop name (loop: BackgroundLoop) =
    try
        loop.Cancellation.Cancel()

        try
            loop.Completion.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException -> ()
        | ex -> Log.log "Shutdown" $"{name} stopped with an error: {ex.Message}"
    finally
        loop.Cancellation.Dispose()

let internal createSessionActivityRuntime
    (dbPath: string)
    (scheduler: MailboxProcessor<RefreshScheduler.StateMsg>)
    (sessionAgent: SessionManager.SessionAgent)
    (rootPaths: Map<RepoId, string>)
    =
    let snapshotStore = OverviewSnapshotStore.OverviewSnapshotStore(dbPath)
    let components = createSessionActivityComponents dbPath scheduler

    try
        { Components = components
          SnapshotStore = snapshotStore
          Capture =
            OverviewSnapshotCapture.create
                scheduler
                sessionAgent
                (Some components.Store)
                rootPaths
                snapshotStore }
    with _ ->
        (components.Service :> System.IDisposable).Dispose()
        (components.Store :> System.IDisposable).Dispose()
        reraise ()

let internal shutdownStoreUsers
    (disposeIngestion: unit -> unit)
    (stopScheduler: unit -> unit)
    (disposeStore: unit -> unit)
    =
    try
        disposeIngestion ()
    finally
        try
            stopScheduler ()
        finally
            disposeStore ()

let internal shutdownSessionActivityRuntime
    (runtime: SessionActivityRuntime)
    (schedulerLoop: BackgroundLoop option)
    =
    shutdownStoreUsers
        (fun () -> (runtime.Components.Service :> System.IDisposable).Dispose())
        (fun () ->
            schedulerLoop
            |> Option.iter (stopBackgroundLoop "Refresh scheduler"))
        (fun () -> (runtime.Components.Store :> System.IDisposable).Dispose())

let internal runHostWithCapture
    (startHost: unit -> unit)
    (waitForShutdown: unit -> unit)
    (stopping: CancellationToken)
    (capture: (CancellationToken -> Async<unit>) option)
    =
    startHost ()

    match capture with
    | None -> waitForShutdown ()
    | Some workflow ->
        let loop = startBackgroundLoop workflow
        let stoppingRegistration =
            stopping.Register(fun () -> loop.Cancellation.Cancel())
        Log.log "Startup" "Overview snapshot capture started"

        try
            waitForShutdown ()
        finally
            stoppingRegistration.Dispose()
            stopBackgroundLoop "Overview snapshot capture" loop

[<EntryPoint>]
let main args =
    let config = parseArgs args

    let serverUrl = $"http://localhost:{config.Port}"

    Log.init ()

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

    config.TestFixtures |> Option.iter (fun path -> Log.log "Startup" $"Test fixtures: {path}")

    worktreeRoots |> List.iter (fun root -> printfn "Monitoring worktrees under: %s" root)

    let remotingApi, schedulerAgent, activityRuntime, schedulerLoop =
        if config.Demo then
            Log.log "Startup" "Demo mode: serving cycling fixture frames"
            buildDemoApi System.DateTimeOffset.Now |> buildRemotingHandler, None, None, None
        else
            let agent = RefreshScheduler.createAgent ()
            let syncAgent = SyncEngine.createSyncAgent ()
            let cardLog = CardEventLog.createAgent ()
            let sessionAgent = SessionManager.createAgent ()
            CanvasDocOwnership.load ()

            match config.TestFixtures with
            | Some path ->
                match WorktreeApi.loadFixtures path with
                | Ok fixtures ->
                    populateAgentFromFixtures agent fixtures
                    Log.log "Startup" "Fixture mode: scheduler background loop skipped"
                | Error msg ->
                    Log.log "Startup" $"ERROR: {msg}"
                    System.Environment.Exit(1)

                WorktreeApi.worktreeApi agent syncAgent cardLog sessionAgent None worktreeRoots config.TestFixtures appVersion deployBranch
                |> buildRemotingHandler,
                Some agent,
                None,
                None
            | None ->
                let dbPath = System.IO.Path.Combine("data", $"session-activity-{config.Port}.db")
                Log.log "Startup" $"Session activity store db: {dbPath}"
                let rootPaths = RefreshScheduler.buildRootPaths worktreeRoots
                let activity =
                    createSessionActivityRuntime
                        dbPath
                        agent
                        sessionAgent
                        rootPaths
                let store = activity.Components.Store

                let schedulerCancellation = new CancellationTokenSource()

                let scheduler =
                    try
                        { Cancellation = schedulerCancellation
                          Completion =
                            RefreshScheduler.start
                                agent
                                worktreeRoots
                                schedulerCancellation.Token }
                    with _ ->
                        schedulerCancellation.Dispose()
                        shutdownSessionActivityRuntime activity None
                        reraise ()

                try
                    activity.Components.Service.Start()
                    Log.log "Startup" "Session activity ingestion started"
                    Log.log "Startup" "Scheduler background loop started"

                    WorktreeApi.worktreeApi
                        agent
                        syncAgent
                        cardLog
                        sessionAgent
                        (Some store)
                        worktreeRoots
                        config.TestFixtures
                        appVersion
                        deployBranch
                    |> buildRemotingHandler,
                    Some agent,
                    Some activity,
                    Some scheduler
                with _ ->
                    shutdownSessionActivityRuntime activity (Some scheduler)
                    reraise ()

    let sessionActivityService =
        activityRuntime |> Option.map _.Components.Service

    let capture =
        activityRuntime
        |> Option.map (fun runtime -> runtime.Capture.Run)

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
            [ route "/api/canvas/register" >=> POST >=> HttpSecurity.csrfGuard >=> CanvasDocServer.canvasRegisterHandler agent
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
            use_router combinedRouter
            url serverUrl
            use_static "wwwroot"
            use_gzip
        }

    try
        let canvasHost =
            match schedulerAgent, config.CanvasPort with
            | Some agent, Some canvasPort -> Some(CanvasDocServer.start agent canvasPort)
            | _ -> None

        try
            use host = app.Build()
            let applicationLifetime =
                host.Services.GetService(typeof<IHostApplicationLifetime>)
                :?> IHostApplicationLifetime

            runHostWithCapture
                (fun () -> host.Start())
                (fun () -> host.WaitForShutdownAsync().GetAwaiter().GetResult())
                applicationLifetime.ApplicationStopping
                capture
        finally
            canvasHost
            |> Option.iter (fun host ->
                try
                    host.StopAsync().GetAwaiter().GetResult()
                finally
                    host.Dispose())
    finally
        activityRuntime
        |> Option.iter (fun runtime ->
            Log.log "Shutdown" "Stopping session activity"
            shutdownSessionActivityRuntime runtime schedulerLoop)

    0
