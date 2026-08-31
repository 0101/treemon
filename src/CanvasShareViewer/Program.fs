module CanvasShareViewer.Program

open System
open Azure.Identity
open Microsoft.AspNetCore.Builder

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    match ViewerConfiguration.read builder.Configuration with
    | Error error ->
        eprintfn $"{error}"
        1
    | Ok configuration ->
        let reader =
            BlobStorage.azure
                configuration
                (DefaultAzureCredential())

        let app =
            ViewerApplication.create
                builder
                reader
                (fun () -> DateTimeOffset.UtcNow)

        app.Run()
        0
