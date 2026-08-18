namespace CanvasShareViewer

open System
open System.Text
open System.Text.Encodings.Web
open System.Threading.Tasks
open Azure
open Azure.Identity
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

module internal ViewerApplication =

    type private DependencyFailureDetails =
        { ExceptionType: string
          AzureStatus: string
          AzureErrorCode: string }

    [<Literal>]
    let ShellRoute = "/c/{prefix}/{filename}"

    [<Literal>]
    let ContentRoute = "/c/{prefix}/{filename}/content"

    [<Literal>]
    let private ShellContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; frame-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'"

    [<Literal>]
    let private ContentContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; img-src data:; font-src data:; media-src data:; connect-src 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts"

    [<Literal>]
    let private DependencyFailureContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; form-action 'none'; base-uri 'none'"

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

    let rec private tryAzureFailureDetails
        (error: exn)
        =
        match error with
        | :? RequestFailedException as failure ->
            Some(
                failure.Status,
                failure.ErrorCode |> Option.ofObj
            )
        | _ ->
            error.InnerException
            |> Option.ofObj
            |> Option.bind tryAzureFailureDetails

    let private (|DependencyFailure|_|)
        (error: exn)
        =
        match error with
        | :? RequestFailedException
        | :? AuthenticationFailedException
        | :? CredentialUnavailableException ->
            let status, errorCode =
                error
                |> tryAzureFailureDetails
                |> Option.map (fun (status, errorCode) ->
                    Some(string status), errorCode)
                |> Option.defaultValue (None, None)

            Some
                { ExceptionType = error.GetType().Name
                  AzureStatus =
                    status
                    |> Option.defaultValue "unavailable"
                  AzureErrorCode =
                    errorCode
                    |> Option.defaultValue "unavailable" }
        | _ ->
            None

    let private handleDependencyFailures
        (logger: ILogger)
        (context: HttpContext)
        (next: RequestDelegate)
        : Task =
        task {
            try
                do! next.Invoke(context)
            with
            | DependencyFailure failure ->
                logger.LogError(
                    "Viewer dependency failure: ExceptionType={ExceptionType}; AzureStatus={AzureStatus}; AzureErrorCode={AzureErrorCode}",
                    [|
                        box failure.ExceptionType
                        box failure.AzureStatus
                        box failure.AzureErrorCode
                    |]
                )

                context.Response.Clear()
                context.Response.StatusCode <-
                    StatusCodes.Status503ServiceUnavailable
                applyResponsePolicy
                    DependencyFailureContentSecurityPolicy
                    context
                context.Response.ContentLength <- 0L
        }

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
        (_metadata: Map<string, string>)
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
        resolve
        contentSecurityPolicy
        (render: HttpContext -> 'stored -> Task)
        (context: HttpContext)
        : Task =
        task {
            applyResponsePolicy contentSecurityPolicy context

            let prefix = routeSegment "prefix" context
            let filename = routeSegment "filename" context

            let! result =
                resolve
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
        app.Use(fun context next ->
            handleDependencyFailures app.Logger context next)
        |> ignore
        app.UseRouting() |> ignore

        app.MapGet(
            ContentRoute,
            RequestDelegate(
                handle
                    (ShareLookup.resolveDocument reader clock)
                    ContentContentSecurityPolicy
                    writeContent
            )
        )
        |> ignore

        app.MapGet(
            ShellRoute,
            RequestDelegate(
                handle
                    (ShareLookup.resolveProperties reader clock)
                    ShellContentSecurityPolicy
                    writeShell
            )
        )
        |> ignore

        app
