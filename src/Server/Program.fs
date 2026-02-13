open Saturn
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection

let webApp =
    choose [
        route "/" >=> text "mait"
    ]

[<EntryPoint>]
let main args =
    let worktreeRoot =
        match args |> Array.tryHead with
        | Some path -> path
        | None ->
            eprintfn "Usage: Server <worktree-root-path>"
            exit 1

    printfn "Monitoring worktrees under: %s" worktreeRoot

    let app =
        application {
            use_router webApp
            url "http://0.0.0.0:5000"
            use_static "wwwroot"
            use_gzip
        }

    run app
    0
