open Saturn
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
open Server

let readAppVersion () =
    let path = System.IO.Path.Combine("wwwroot", "version.json")

    match System.IO.File.Exists(path) with
    | false ->
        Log.log "Startup" "No wwwroot/version.json found, using empty version"
        ""
    | true ->
        let json = System.IO.File.ReadAllText(path)
        let doc = System.Text.Json.JsonDocument.Parse(json)

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
        | path :: rest when worktreeRoot = None ->
            parse (Some path) port testFixtures rest
        | [] -> worktreeRoot, port, testFixtures
        | unexpected :: _ ->
            eprintfn "Unexpected argument: %s" unexpected
            exit 1

    let worktreeRoot, port, testFixtures = args |> Array.toList |> parse None 5000 None

    match worktreeRoot with
    | Some root ->
        { WorktreeRoot = root.TrimEnd([| '\\'; '/' |])
          Port = port
          TestFixtures = testFixtures }
    | None ->
        eprintfn "Usage: Server <worktree-root-path> [--port <port>] [--test-fixtures <path>]"
        exit 1

[<EntryPoint>]
let main args =
    let config = parseArgs args

    let serverUrl = $"http://0.0.0.0:{config.Port}"

    Log.init ()
    Log.log "Startup" $"Worktree root: {config.WorktreeRoot}"
    Log.log "Startup" $"Server URL: {serverUrl}"

    let appVersion = readAppVersion ()
    Log.log "Startup" $"App version: {appVersion}"

    match config.TestFixtures with
    | Some path -> Log.log "Startup" $"Test fixtures: {path}"
    | None -> ()

    printfn "Monitoring worktrees under: %s" config.WorktreeRoot

    let remotingApi =
        let agent = RefreshScheduler.createAgent ()

        Remoting.createApi ()
        |> Remoting.fromValue (WorktreeApi.worktreeApi agent config.WorktreeRoot config.TestFixtures appVersion)
        |> Remoting.withErrorHandler (fun ex routeInfo ->
            Log.log "API" $"Error in {routeInfo.methodName}: {ex}"
            Propagate ex.Message)
        |> Remoting.buildHttpHandler

    let app =
        application {
            use_router remotingApi
            url serverUrl
            use_static "wwwroot"
            use_gzip
        }

    run app
    0
