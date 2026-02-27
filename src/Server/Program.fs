open Saturn
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
open System.Threading
open Shared
open Server

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
      TestFixtures: string option }

let parseArgs (args: string array) =
    let rec parse roots port testFixtures remaining =
        match remaining with
        | "--port" :: portStr :: rest ->
            match System.Int32.TryParse(portStr) with
            | true, p -> parse roots p testFixtures rest
            | false, _ ->
                eprintfn "Invalid port number: %s" portStr
                exit 1
        | "--test-fixtures" :: path :: rest ->
            parse roots port (Some path) rest
        | path :: rest when not (path.StartsWith("--")) ->
            parse (roots @ [ path ]) port testFixtures rest
        | [] -> roots, port, testFixtures
        | unexpected :: _ ->
            eprintfn "Unexpected argument: %s" unexpected
            exit 1

    match args |> Array.toList |> parse [] 5000 None with
    | roots, port, testFixtures when roots <> [] ->
        { WorktreeRoots = roots |> List.map (fun r -> r.TrimEnd([| '\\'; '/' |]))
          Port = port
          TestFixtures = testFixtures }
    | _ ->
        eprintfn "Usage: Server <worktree-root-path> [<additional-roots>...] [--port <port>] [--test-fixtures <path>]"
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
        Log.log "Startup" $"Populated agent with {List.length worktreeInfos} fixture worktrees for repo '{RepoId.value repo.RepoId}'")

[<EntryPoint>]
let main args =
    let config = parseArgs args

    let serverUrl = $"http://0.0.0.0:{config.Port}"

    Log.init ()
    config.WorktreeRoots |> List.iter (fun root -> Log.log "Startup" $"Worktree root: {root}")
    Log.log "Startup" $"Server URL: {serverUrl}"

    let appVersion = readAppVersion ()
    Log.log "Startup" $"App version: {appVersion}"

    config.TestFixtures |> Option.iter (fun path -> Log.log "Startup" $"Test fixtures: {path}")

    config.WorktreeRoots |> List.iter (fun root -> printfn "Monitoring worktrees under: %s" root)

    let cts = new CancellationTokenSource()
    let agent = RefreshScheduler.createAgent ()
    let syncAgent = SyncEngine.createSyncAgent ()

    match config.TestFixtures with
    | Some path ->
        let fixtures = WorktreeApi.loadFixtures path
        populateAgentFromFixtures agent fixtures
        Log.log "Startup" "Fixture mode: scheduler background loop skipped"
    | None ->
        RefreshScheduler.start agent config.WorktreeRoots cts.Token
        Log.log "Startup" "Scheduler background loop started"

    let remotingApi =
        Remoting.createApi ()
        |> Remoting.fromValue (WorktreeApi.worktreeApi agent syncAgent config.WorktreeRoots config.TestFixtures appVersion)
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
