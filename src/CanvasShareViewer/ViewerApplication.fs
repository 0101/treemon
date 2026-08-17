namespace CanvasShareViewer

open System
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

module internal ViewerApplication =

    [<Literal>]
    let ShellRoute = "/c/{prefix}/{filename}"

    [<Literal>]
    let ContentRoute = "/c/{prefix}/{filename}/content"

    let private shellBytes =
        Encoding.UTF8.GetBytes(
            "<!doctype html><html><body></body></html>"
        )

    let private routeSegment name (context: HttpContext) =
        context.Request.RouteValues[name]
        |> Option.ofObj
        |> Option.map string
        |> Option.defaultValue ""

    let private writeShell
        (context: HttpContext)
        (_document: BlobDocument)
        : Task =
        task {
            context.Response.ContentType <- "text/html; charset=utf-8"
            context.Response.ContentLength <- shellBytes.LongLength
            do!
                context.Response.Body.WriteAsync(
                    shellBytes,
                    context.RequestAborted
                )
        }

    let private writeContent
        (context: HttpContext)
        (document: BlobDocument)
        : Task =
        task {
            context.Response.ContentType <- "text/html; charset=utf-8"
            context.Response.ContentLength <- document.Content.Length
            do!
                context.Response.Body.WriteAsync(
                    document.Content,
                    context.RequestAborted
                )
        }

    let private handle
        (reader: BlobReader)
        (clock: unit -> DateTimeOffset)
        (render: HttpContext -> BlobDocument -> Task)
        (context: HttpContext)
        : Task =
        task {
            let prefix = routeSegment "prefix" context
            let filename = routeSegment "filename" context

            let! result =
                ShareLookup.resolve
                    reader
                    clock
                    prefix
                    filename
                    context.RequestAborted

            match result with
            | Available document ->
                do! render context document
            | NotFound ->
                context.Response.StatusCode <-
                    StatusCodes.Status404NotFound
        }

    let private normalizeNotFound
        (context: HttpContext)
        (next: RequestDelegate)
        : Task =
        task {
            do! next.Invoke(context)

            if
                context.Response.StatusCode
                = StatusCodes.Status404NotFound
                && not context.Response.HasStarted
            then
                context.Response.Clear()
                context.Response.StatusCode <-
                    StatusCodes.Status404NotFound
                context.Response.ContentLength <- 0L
        }

    let create
        (builder: WebApplicationBuilder)
        (reader: BlobReader)
        clock
        =
        builder.Services.AddRouting() |> ignore

        let app = builder.Build()
        app.Use(fun context next -> normalizeNotFound context next)
        |> ignore
        app.UseRouting() |> ignore

        app.MapGet(
            ContentRoute,
            RequestDelegate(handle reader clock writeContent)
        )
        |> ignore

        app.MapGet(
            ShellRoute,
            RequestDelegate(handle reader clock writeShell)
        )
        |> ignore

        app
