open Saturn
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
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
      TestFixtures: string option
      Demo: bool }

let parseArgs (args: string array) =
    let rec parse roots port testFixtures demo remaining =
        match remaining with
        | "--port" :: portStr :: rest ->
            match System.Int32.TryParse(portStr) with
            | true, p -> parse roots p testFixtures demo rest
            | false, _ ->
                eprintfn "Invalid port number: %s" portStr
                exit 1
        | "--test-fixtures" :: path :: rest ->
            parse roots port (Some path) demo rest
        | "--demo" :: rest ->
            parse roots port testFixtures true rest
        | path :: rest when not (path.StartsWith("--")) ->
            parse (roots @ [ path ]) port testFixtures demo rest
        | [] -> roots, port, testFixtures, demo
        | unexpected :: _ ->
            eprintfn "Unexpected argument: %s" unexpected
            exit 1

    match args |> Array.toList |> parse [] 5000 None false with
    | _, _, Some _, true ->
        eprintfn "--demo and --test-fixtures are mutually exclusive"
        exit 1
    | _, port, _, true ->
        { WorktreeRoots = []
          Port = port
          TestFixtures = None
          Demo = true }
    | roots, port, testFixtures, _ when roots <> [] ->
        { WorktreeRoots = roots |> List.map (fun r -> r.TrimEnd([| '\\'; '/' |]))
          Port = port
          TestFixtures = testFixtures
          Demo = false }
    | _ ->
        eprintfn "Usage: Server <worktree-root-path> [<additional-roots>...] [--port <port>] [--test-fixtures <path>]"
        eprintfn "       Server --demo [--port <port>]"
        exit 1

let private populateAgentFromFixtures (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (fixtures: FixtureData) =
    fixtures.Worktrees.Repos
    |> List.iter (fun repo ->
        let worktreeInfos =
            repo.Worktrees
            |> List.map (fun wt ->
                { Path = WorktreePath.value wt.Path
                  Head = ""
                  Branch = Some wt.Branch }: GitWorktree.WorktreeInfo)

        agent.Post(RefreshScheduler.UpdateWorktreeList(repo.RepoId, worktreeInfos))
        Log.log "Startup" $"Populated agent with {List.length worktreeInfos} fixture worktrees for repo '{RepoId.value repo.RepoId}'")

let private buildDemoApi (startTime: System.DateTimeOffset) : IWorktreeApi =
    { getWorktrees = fun () ->
        async {
            let frame = DemoFixture.selectFrame startTime System.DateTimeOffset.Now
            return frame.Worktrees
        }
      getSyncStatus = fun () ->
        async {
            let frame = DemoFixture.selectFrame startTime System.DateTimeOffset.Now
            return frame.SyncStatus
        }
      openTerminal = fun _ -> async { return () }
      openEditor = fun _ -> async { return () }
      startSync = fun _ -> async { return Error "Sync is not available in demo mode" }
      cancelSync = fun _ -> async { return () }
      deleteWorktree = fun _ -> async { return Error "Delete is not available in demo mode" }
      launchSession = fun _ -> async { return Error "Session management is not available in demo mode" }
      focusSession = fun _ -> async { return Error "Session management is not available in demo mode" }
      killSession = fun _ -> async { return Error "Session management is not available in demo mode" }
      archiveWorktree = fun _ -> async { return Error "Archive is not available in demo mode" }
      unarchiveWorktree = fun _ -> async { return Error "Archive is not available in demo mode" }
      getBranches = fun _ -> async { return [] }
      createWorktree = fun _ -> async { return Error "Create is not available in demo mode" }
      openNewTab = fun _ -> async { return Error "Session management is not available in demo mode" } }

[<EntryPoint>]
let main args =
    let config = parseArgs args

    let serverUrl = $"http://localhost:{config.Port}"

    Log.init ()
    config.WorktreeRoots |> List.iter (fun root -> Log.log "Startup" $"Worktree root: {root}")
    Log.log "Startup" $"Server URL: {serverUrl}"

    let appVersion = readAppVersion ()
    Log.log "Startup" $"App version: {appVersion}"

    let deployBranch = readDeployBranch ()
    let deployBranchDisplay = deployBranch |> Option.defaultValue "(main)"
    Log.log "Startup" $"Deploy branch: {deployBranchDisplay}"

    config.TestFixtures |> Option.iter (fun path -> Log.log "Startup" $"Test fixtures: {path}")

    config.WorktreeRoots |> List.iter (fun root -> printfn "Monitoring worktrees under: %s" root)

    let cts = new CancellationTokenSource()
    let agent = RefreshScheduler.createAgent ()
    let syncAgent = SyncEngine.createSyncAgent ()
    let sessionAgent = SessionManager.createAgent ()

    let remotingApi =
        if config.Demo then
            Log.log "Startup" "Demo mode: serving cycling fixture frames"
            let demoApi = buildDemoApi System.DateTimeOffset.Now

            Remoting.createApi ()
            |> Remoting.fromValue demoApi
            |> Remoting.withErrorHandler (fun ex routeInfo ->
                Log.log "API" $"Error in {routeInfo.methodName}: {ex}"
                Propagate ex.Message)
            |> Remoting.buildHttpHandler
        else
            match config.TestFixtures with
            | Some path ->
                let fixtures = WorktreeApi.loadFixtures path
                populateAgentFromFixtures agent fixtures
                Log.log "Startup" "Fixture mode: scheduler background loop skipped"
            | None ->
                RefreshScheduler.start agent config.WorktreeRoots cts.Token
                Log.log "Startup" "Scheduler background loop started"

            Remoting.createApi ()
            |> Remoting.fromValue (WorktreeApi.worktreeApi agent syncAgent sessionAgent config.WorktreeRoots config.TestFixtures appVersion deployBranch)
            |> Remoting.withErrorHandler (fun ex routeInfo ->
                Log.log "API" $"Error in {routeInfo.methodName}: {ex}"
                Propagate ex.Message)
            |> Remoting.buildHttpHandler

    System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
        Log.log "Shutdown" "Cancelling scheduler"
        cts.Cancel()
        cts.Dispose())

    let app =
        application {
            use_router remotingApi
            url serverUrl
            use_static "wwwroot"
            use_gzip
        }

    run app
    0
