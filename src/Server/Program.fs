open Saturn
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
open Server

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
        { WorktreeRoot = root
          Port = port
          TestFixtures = testFixtures }
    | None ->
        eprintfn "Usage: Server <worktree-root-path> [--port <port>] [--test-fixtures <path>]"
        exit 1

[<EntryPoint>]
let main args =
    let config = parseArgs args

    let serverUrl = sprintf "http://0.0.0.0:%d" config.Port

    Log.init ()
    Log.log "Startup" (sprintf "Worktree root: %s" config.WorktreeRoot)
    Log.log "Startup" (sprintf "Server URL: %s" serverUrl)

    match config.TestFixtures with
    | Some path -> Log.log "Startup" (sprintf "Test fixtures: %s" path)
    | None -> ()

    printfn "Monitoring worktrees under: %s" config.WorktreeRoot

    let remotingApi =
        Remoting.createApi ()
        |> Remoting.fromValue (WorktreeApi.worktreeApi config.WorktreeRoot config.TestFixtures)
        |> Remoting.withErrorHandler (fun ex routeInfo ->
            Log.log "API" (sprintf "Error in %s: %s" routeInfo.methodName (ex.ToString()))
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
