open Saturn
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
open Server

let parseArgs (args: string array) =
    let rec parse worktreeRoot port remaining =
        match remaining with
        | "--port" :: portStr :: rest ->
            match System.Int32.TryParse(portStr) with
            | true, p -> parse worktreeRoot p rest
            | false, _ ->
                eprintfn "Invalid port number: %s" portStr
                exit 1
        | path :: rest when worktreeRoot = None ->
            parse (Some path) port rest
        | [] -> worktreeRoot, port
        | unexpected :: _ ->
            eprintfn "Unexpected argument: %s" unexpected
            exit 1

    let worktreeRoot, port = args |> Array.toList |> parse None 5000

    match worktreeRoot with
    | Some root -> root, port
    | None ->
        eprintfn "Usage: Server <worktree-root-path> [--port <port>]"
        exit 1

[<EntryPoint>]
let main args =
    let worktreeRoot, port = parseArgs args

    let serverUrl = sprintf "http://0.0.0.0:%d" port

    Log.init ()
    Log.log "Startup" (sprintf "Worktree root: %s" worktreeRoot)
    Log.log "Startup" (sprintf "Server URL: %s" serverUrl)

    printfn "Monitoring worktrees under: %s" worktreeRoot

    let remotingApi =
        Remoting.createApi ()
        |> Remoting.fromValue (WorktreeApi.worktreeApi worktreeRoot)
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
