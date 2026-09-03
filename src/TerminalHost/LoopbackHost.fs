namespace TerminalHost

open System
open System.Net
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

[<RequireQualifiedAccess>]
module internal LoopbackHost =
    let start port (buildPipeline: WebApplication -> RequestDelegate) =
        task {
            let builder = WebApplication.CreateSlimBuilder()
            builder.Logging.ClearProviders() |> ignore

            builder.WebHost.ConfigureKestrel(fun options ->
                options.Limits.MaxRequestBodySize <- Protocol.MaximumRequestBodyBytes
                options.AddServerHeader <- false
                options.Listen(IPAddress.Loopback, port))
            |> ignore

            let application = builder.Build()

            try
                application.Run(buildPipeline application)
                do! application.StartAsync()

                let server = application.Services.GetRequiredService<IServer>()
                let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
                let bound = addresses |> Seq.exactlyOne |> Uri
                return application, bound.Port
            with error ->
                try
                    do! application.DisposeAsync().AsTask()
                with _ ->
                    ()

                return raise error
        }
