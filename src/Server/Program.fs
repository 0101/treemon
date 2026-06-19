open Saturn
open Giraffe
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open System.Threading
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
    System.IO.Path.Combine(WorktreeApi.globalConfigDir (), "roots.json")

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

/// Resolves the effective worktree roots at startup by priority:
///   1. roots passed as CLI args (used by `dev`/tests; preserves current arg behavior),
///   2. else `worktreeRoots` from the global `config.json` (a PRESENT key, even an explicit empty
///      list, wins here — the user may have curated every root away),
///   3. else (the key is ABSENT) a one-time import of the orphan `roots.json`.
/// When `config.json` has no `worktreeRoots` KEY yet, the resolved set is persisted so an
/// arg-provided or migrated set becomes the durable source of truth. A present key (even
/// `worktreeRoots:[]`) is left untouched, so CLI args act as an ephemeral override and an explicit
/// empty is never repopulated on restart. An orphan-sourced set is deleted only after that persist
/// succeeds — never before — so a failed write can't drop the migration. Demo/fixture modes never
/// call this; their roots stay `[]`.
let internal resolveWorktreeRoots (cliRoots: string list) : string list =
    // `None` = the `worktreeRoots` key is absent (fresh install / pre-migration); `Some roots` =
    // the key is present (possibly an explicit empty list). Gating migration on KEY ABSENCE — not
    // `List.isEmpty` — is what stops an explicit `worktreeRoots:[]` from being resurrected by a
    // stale orphan `roots.json` or overwritten by CLI args on restart.
    let configRoots = WorktreeApi.tryReadWorktreeRootsConfig ()
    let configHasKey = Option.isSome configRoots

    let resolved, cameFromOrphan =
        if not (List.isEmpty cliRoots) then cliRoots, false
        else
            match configRoots with
            | Some roots -> roots, false
            | None ->
                let orphanRoots = readOrphanRoots ()
                orphanRoots, not (List.isEmpty orphanRoots)

    if not configHasKey && not (List.isEmpty resolved) then
        match WorktreeApi.writeWorktreeRoots resolved with
        | Ok () ->
            Log.log "Startup" $"Persisted {List.length resolved} worktree root(s) to global config"
            if cameFromOrphan then deleteOrphanRoots ()
        | Error msg ->
            Log.log "Startup" $"Failed to persist worktree roots: {msg}"

    resolved

[<EntryPoint>]
let main args =
    let config = parseArgs args

    let serverUrl = $"http://localhost:{config.Port}"

    Log.init ()

    // Effective roots: CLI args > global config > orphan import (persisted first-time). Demo and
    // fixture modes bypass resolution entirely — they serve synthetic data, so roots stay [].
    let worktreeRoots =
        if config.Demo || config.TestFixtures.IsSome then []
        else resolveWorktreeRoots config.WorktreeRoots

    worktreeRoots |> List.iter (fun root -> Log.log "Startup" $"Worktree root: {root}")
    Log.log "Startup" $"Server URL: {serverUrl}"

    let appVersion = readAppVersion ()
    Log.log "Startup" $"App version: {appVersion}"

    let deployBranch = readDeployBranch ()
    let deployBranchDisplay = deployBranch |> Option.defaultValue "(main)"
    Log.log "Startup" $"Deploy branch: {deployBranchDisplay}"

    config.TestFixtures |> Option.iter (fun path -> Log.log "Startup" $"Test fixtures: {path}")

    worktreeRoots |> List.iter (fun root -> printfn "Monitoring worktrees under: %s" root)

    let cts = new CancellationTokenSource()

    let remotingApi, schedulerAgent =
        if config.Demo then
            Log.log "Startup" "Demo mode: serving cycling fixture frames"
            buildDemoApi System.DateTimeOffset.Now |> buildRemotingHandler, None
        else
            let agent = RefreshScheduler.createAgent ()
            let syncAgent = SyncEngine.createSyncAgent ()
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
            | None ->
                RefreshScheduler.start agent worktreeRoots cts.Token
                Log.log "Startup" "Scheduler background loop started"

            WorktreeApi.worktreeApi agent syncAgent sessionAgent worktreeRoots config.TestFixtures appVersion deployBranch
            |> buildRemotingHandler, Some agent

    System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
        Log.log "Shutdown" "Cancelling scheduler"
        cts.Cancel()
        cts.Dispose())

    match schedulerAgent, config.CanvasPort with
    | Some agent, Some canvasPort -> CanvasDocServer.start agent canvasPort cts.Token
    | _ -> ()

    // The register/attribute routes need the scheduler agent for their known-worktree guard. In
    // demo mode there is no agent (and the canvas doc server is never started — see above), so
    // these are unavailable there; bridge-status stays available and simply reports nothing
    // registered.
    let canvasAgentRoutes =
        match schedulerAgent with
        | Some agent ->
            [ route "/api/canvas/register" >=> POST >=> CanvasDocServer.canvasRegisterHandler agent
              route "/api/canvas/attribute" >=> POST >=> CanvasDocServer.canvasAttributeHandler agent ]
        | None -> []

    let combinedRouter =
        choose (
            canvasAgentRoutes
            @ [ route "/api/canvas/bridge-status" >=> GET >=> CanvasDocServer.bridgeStatusHandler
                remotingApi ])

    let app =
        application {
            use_router combinedRouter
            url serverUrl
            use_static "wwwroot"
            use_gzip
        }

    run app
    0
