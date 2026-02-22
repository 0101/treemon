open Saturn
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
open System.Threading
open Server

let readAppVersion () =
    let path = System.IO.Path.Combine("wwwroot", "version.json")

    match System.IO.File.Exists(path) with
    | false ->
        Log.log "Startup" "No wwwroot/version.json found, using empty version"
        ""
    | true ->
        let json = System.IO.File.ReadAllText(path)
        use doc = System.Text.Json.JsonDocument.Parse(json)

        match doc.RootElement.TryGetProperty("buildTime") with
        | true, elem -> elem.GetString()
        | false, _ -> ""

type ServerConfig =
    { WorktreeRoot: string
      Port: int
      TestFixtures: string option }

let parseArgs (args: string array) =
    let rec parse worktreeRoot port testFixtures remaining =
        match remaining with
        | "--port" :: portStr :: rest ->
            match System.Int32.TryParse(portStr) with
            | true, p -> parse worktreeRoot p testFixtures rest
            | false, _ ->
                eprintfn "Invalid port number: %s" portStr
                exit 1
        | "--test-fixtures" :: path :: rest ->
            parse worktreeRoot port (Some path) rest
        | path :: rest when Option.isNone worktreeRoot ->
            parse (Some path) port testFixtures rest
        | [] -> worktreeRoot, port, testFixtures
        | unexpected :: _ ->
            eprintfn "Unexpected argument: %s" unexpected
            exit 1

    match args |> Array.toList |> parse None 5000 None with
    | Some root, port, testFixtures ->
        { WorktreeRoot = root.TrimEnd([| '\\'; '/' |])
          Port = port
          TestFixtures = testFixtures }
    | None, _, _ ->
        eprintfn "Usage: Server <worktree-root-path> [--port <port>] [--test-fixtures <path>]"
        exit 1

let private populateAgentFromFixtures (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (fixtures: WorktreeApi.FixtureData) =
    fixtures.Worktrees.Repos
    |> List.iter (fun repo ->
        let worktreeInfos =
            repo.Worktrees
            |> List.map (fun wt ->
                { Path = wt.Path
                  Head = ""
                  Branch = Some wt.Branch }: GitWorktree.WorktreeInfo)

        agent.Post(RefreshScheduler.UpdateWorktreeList(repo.RepoId, worktreeInfos))
        Log.log "Startup" $"Populated agent with {List.length worktreeInfos} fixture worktrees for repo '{repo.RepoId}'")

[<EntryPoint>]
let main args =
    let config = parseArgs args

    let serverUrl = $"http://0.0.0.0:{config.Port}"

    Log.init ()
    Log.log "Startup" $"Worktree root: {config.WorktreeRoot}"
    Log.log "Startup" $"Server URL: {serverUrl}"

    let appVersion = readAppVersion ()
    Log.log "Startup" $"App version: {appVersion}"

    config.TestFixtures |> Option.iter (fun path -> Log.log "Startup" $"Test fixtures: {path}")

    printfn "Monitoring worktrees under: %s" config.WorktreeRoot

    let cts = new CancellationTokenSource()
    let agent = RefreshScheduler.createAgent ()

    match config.TestFixtures with
    | Some path ->
        let fixtures = WorktreeApi.loadFixtures path
        populateAgentFromFixtures agent fixtures
        Log.log "Startup" "Fixture mode: scheduler background loop skipped"
    | None ->
        RefreshScheduler.start agent [ config.WorktreeRoot ] cts.Token
        Log.log "Startup" "Scheduler background loop started"

    let remotingApi =
        Remoting.createApi ()
        |> Remoting.fromValue (WorktreeApi.worktreeApi agent config.WorktreeRoot config.TestFixtures appVersion)
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
