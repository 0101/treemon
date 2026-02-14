open Saturn
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
open Server

[<EntryPoint>]
let main args =
    let worktreeRoot =
        match args |> Array.tryHead with
        | Some path -> path
        | None ->
            eprintfn "Usage: Server <worktree-root-path>"
            exit 1

    printfn "Monitoring worktrees under: %s" worktreeRoot

    let remotingApi =
        Remoting.createApi ()
        |> Remoting.fromValue (WorktreeApi.worktreeApi worktreeRoot)
        |> Remoting.buildHttpHandler

    let app =
        application {
            use_router remotingApi
            url "http://0.0.0.0:5000"
            use_static "wwwroot"
            use_gzip
        }

    run app
    0
