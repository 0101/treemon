open Saturn
open Giraffe
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open System.IO
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
                eprintfn $"Invalid port number: {portStr}"
                exit 1
        | "--test-fixtures" :: path :: rest ->
            parse roots port (Some path) demo rest
        | "--demo" :: rest ->
            parse roots port testFixtures true rest
        | path :: rest when not (path.StartsWith("--")) ->
            parse (roots @ [ path ]) port testFixtures demo rest
        | [] -> roots, port, testFixtures, demo
        | unexpected :: _ ->
            eprintfn $"Unexpected argument: {unexpected}"
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

[<CLIMutable>]
type CanvasRegisterRequest =
    { worktreePath: string
      injectUrl: string }

let private canvasRegisterHandler : HttpHandler =
    fun next ctx -> task {
        let! body = ctx.BindJsonAsync<CanvasRegisterRequest>()
        CanvasBridge.register body.worktreePath body.injectUrl
        Log.log "Canvas" $"Bridge registered: {body.worktreePath} -> {body.injectUrl}"
        return! Successful.OK "registered" next ctx
    }

module CanvasDocServer =
    open Microsoft.AspNetCore.Hosting
    open Microsoft.Extensions.DependencyInjection
    let private canvasPort = 5002

    let private allKnownPaths (agent: MailboxProcessor<RefreshScheduler.StateMsg>) = async {
        let! state = agent.PostAndAsyncReply RefreshScheduler.GetState
        return
            state.Repos
            |> Map.values
            |> Seq.collect _.KnownPaths
            |> Set.ofSeq
    }

    let private isKnownWorktree agent path = async {
        let! paths = allKnownPaths agent
        return paths |> Set.contains path
    }

    let private handleCanvasRequest (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (ctx: HttpContext) : System.Threading.Tasks.Task = task {
        let catchAll = ctx.Request.RouteValues["path"] :?> string
        let lastSlash = catchAll.LastIndexOf('/')
        if lastSlash < 1 then
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsync("Invalid path format")
        else
            let worktreePathEncoded = catchAll.Substring(0, lastSlash)
            let filename = catchAll.Substring(lastSlash + 1)
            let worktreePath = System.Net.WebUtility.UrlDecode worktreePathEncoded

            let! isKnown = (isKnownWorktree agent worktreePath) |> Async.StartAsTask

            if not (filename.EndsWith(".html")) then
                ctx.Response.StatusCode <- 400
                do! ctx.Response.WriteAsync("Only .html files are served")
            elif not isKnown then
                ctx.Response.StatusCode <- 404
                do! ctx.Response.WriteAsync("Unknown worktree")
            else
                let canvasDir = Path.Combine(worktreePath, ".agents", "canvas")
                let resolvedPath = Path.GetFullPath(Path.Combine(canvasDir, filename))
                let canonicalCanvasDir = Path.GetFullPath(canvasDir + string Path.DirectorySeparatorChar)

                if not (resolvedPath.StartsWith(canonicalCanvasDir, System.StringComparison.OrdinalIgnoreCase)) then
                    ctx.Response.StatusCode <- 400
                    do! ctx.Response.WriteAsync("Path traversal rejected")
                elif not (File.Exists resolvedPath) then
                    ctx.Response.StatusCode <- 404
                    do! ctx.Response.WriteAsync("File not found")
                else
                    let! content = File.ReadAllBytesAsync(resolvedPath)
                    ctx.Response.ContentType <- "text/html"
                    do! ctx.Response.Body.WriteAsync(content)
    }

    let start (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (cts: CancellationToken) =
        let host =
            WebHostBuilder()
                .UseKestrel(fun opts ->
                    opts.Listen(System.Net.IPAddress.Loopback, canvasPort))
                .ConfigureServices(fun services ->
                    services.AddRouting() |> ignore)
                .Configure(fun (app: IApplicationBuilder) ->
                    app.UseRouting() |> ignore
                    app.UseEndpoints(fun endpoints ->
                        endpoints.MapGet("/{**path}", RequestDelegate(handleCanvasRequest agent)) |> ignore) |> ignore)
                .Build()
        Log.log "Startup" $"Canvas doc server starting on http://127.0.0.1:{canvasPort}"
        host.StartAsync(cts).ContinueWith(fun (t: System.Threading.Tasks.Task) ->
            if t.IsFaulted then
                Log.log "Canvas" $"Canvas doc server failed to start: {t.Exception.InnerException.Message}")
        |> ignore

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

    let remotingApi, schedulerAgent =
        if config.Demo then
            Log.log "Startup" "Demo mode: serving cycling fixture frames"
            buildDemoApi System.DateTimeOffset.Now |> buildRemotingHandler, None
        else
            let agent = RefreshScheduler.createAgent ()
            let syncAgent = SyncEngine.createSyncAgent ()
            let sessionAgent = SessionManager.createAgent ()

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
                RefreshScheduler.start agent config.WorktreeRoots cts.Token
                Log.log "Startup" "Scheduler background loop started"

            WorktreeApi.worktreeApi agent syncAgent sessionAgent config.WorktreeRoots config.TestFixtures appVersion deployBranch
            |> buildRemotingHandler, Some agent

    System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
        Log.log "Shutdown" "Cancelling scheduler"
        cts.Cancel()
        cts.Dispose())

    schedulerAgent |> Option.iter (fun agent -> CanvasDocServer.start agent cts.Token)

    let combinedRouter =
        choose [
            route "/api/canvas/register" >=> POST >=> canvasRegisterHandler
            remotingApi
        ]

    let app =
        application {
            use_router combinedRouter
            url serverUrl
            use_static "wwwroot"
            use_gzip
        }

    run app
    0
