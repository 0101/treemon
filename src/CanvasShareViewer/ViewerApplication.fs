namespace CanvasShareViewer

open System
open System.Text
open System.Text.Encodings.Web
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

module internal ViewerApplication =

    [<Literal>]
    let ShellRoute = "/c/{prefix}/{filename}"

    [<Literal>]
    let ContentRoute = "/c/{prefix}/{filename}/content"

    [<Literal>]
    let private ShellContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; frame-src 'self'; form-action 'none'; base-uri 'none'"

    [<Literal>]
    let private ContentContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; img-src data:; font-src data:; media-src data:; connect-src 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts"

    let private routeSegment name (context: HttpContext) =
        context.Request.RouteValues[name]
        |> Option.ofObj
        |> Option.map string
        |> Option.defaultValue ""

    let private applyResponsePolicy
        (contentSecurityPolicy: string)
        (context: HttpContext)
        =
        context.Response.Headers["Content-Security-Policy"] <-
            contentSecurityPolicy
        context.Response.Headers["X-Content-Type-Options"] <-
            "nosniff"
        context.Response.Headers["Referrer-Policy"] <-
            "no-referrer"
        context.Response.Headers["Cache-Control"] <-
            "no-store"

    let private shellBytes (context: HttpContext) =
        let prefix =
            routeSegment "prefix" context
            |> Uri.EscapeDataString

        let filename =
            routeSegment "filename" context
            |> Uri.EscapeDataString

        let contentPath =
            $"/c/{prefix}/{filename}/content"
            |> HtmlEncoder.Default.Encode

        String.Concat(
            "<!doctype html><html><head><meta charset=\"utf-8\" /><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\" /><title>Shared canvas</title><style>html,body{height:100%;margin:0}body{overflow:hidden}iframe{display:block;width:100%;height:100%;border:0}</style></head><body><iframe title=\"Shared canvas\" sandbox=\"allow-scripts\" src=\"",
            contentPath,
            "\"></iframe></body></html>"
        )
        |> Encoding.UTF8.GetBytes

    let private writeShell
        (context: HttpContext)
        (_document: BlobDocument)
        : Task =
        task {
            let content = shellBytes context
            context.Response.ContentType <- "text/html; charset=utf-8"
            context.Response.ContentLength <- content.LongLength
            do!
                context.Response.Body.WriteAsync(
                    content,
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
        contentSecurityPolicy
        (render: HttpContext -> BlobDocument -> Task)
        (context: HttpContext)
        : Task =
        task {
            applyResponsePolicy contentSecurityPolicy context

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
                context.Response.ContentLength <- 0L
        }

    let create
        (builder: WebApplicationBuilder)
        (reader: BlobReader)
        clock
        =
        builder.WebHost.ConfigureKestrel(fun options ->
            options.AddServerHeader <- false)
        |> ignore
        builder.Services.AddRouting() |> ignore

        let app = builder.Build()
        app.UseRouting() |> ignore

        app.MapGet(
            ContentRoute,
            RequestDelegate(
                handle
                    reader
                    clock
                    ContentContentSecurityPolicy
                    writeContent
            )
        )
        |> ignore

        app.MapGet(
            ShellRoute,
            RequestDelegate(
                handle
                    reader
                    clock
                    ShellContentSecurityPolicy
                    writeShell
            )
        )
        |> ignore

        app
