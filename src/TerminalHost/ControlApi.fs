namespace TerminalHost

open System
open System.IO
open System.Net
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type ControlApiConfig =
    { Port: int
      AllowedOrigins: string list }

type RequestMetadata =
    { RemoteAddress: IPAddress option
      LocalAddress: IPAddress option
      LocalPort: int
      HostHeaders: string list
      OriginHeaders: string list
      AuthorizationHeaders: string list
      ContentLength: int64 option }

[<RequireQualifiedAccess>]
type RequestRejection =
    | Forbidden
    | Unauthorized
    | TooLarge

type RunningControlApi =
    internal
        { Application: WebApplication
          Endpoint: string }

type HealthResponse =
    { Pid: int
      ProcessStartTimeUtcTicks: int64
      HostVersion: string
      ControlApiVersion: int }

type ErrorResponse = { Error: string }
type ShutdownResponse = { Accepted: bool }

[<RequireQualifiedAccess>]
module RequestSecurity =
    let private fixedTimeEquals (expected: string) (actual: string) =
        let expectedBytes = Encoding.UTF8.GetBytes expected
        let actualBytes = Encoding.UTF8.GetBytes actual

        expectedBytes.Length = actualBytes.Length
        && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes)

    let private validAuthorization (bearerToken: string) (values: string list) =
        match values with
        | [ value ] when value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
            let supplied = value.Substring("Bearer ".Length)
            fixedTimeEquals bearerToken supplied
        | _ -> false

    let private matchesOne (expected: string) (values: string list) =
        match values with
        | [ value ] -> String.Equals(value, expected, StringComparison.OrdinalIgnoreCase)
        | _ -> false

    let validate (allowedOrigins: string list) (bearerToken: string) (metadata: RequestMetadata) =
        let controlOrigin = $"http://127.0.0.1:{metadata.LocalPort}"

        let validOrigin =
            match metadata.OriginHeaders with
            | [] -> true
            | [ origin ] ->
                controlOrigin :: allowedOrigins
                |> List.exists (fun allowed ->
                    String.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
            | _ -> false

        match metadata.RemoteAddress, metadata.LocalAddress with
        | Some remoteAddress, Some localAddress
            when IPAddress.IsLoopback remoteAddress && IPAddress.IsLoopback localAddress ->
            let expectedHost = $"127.0.0.1:{metadata.LocalPort}"

            if not (matchesOne expectedHost metadata.HostHeaders) then
                Error RequestRejection.Forbidden
            elif not validOrigin then
                Error RequestRejection.Forbidden
            elif metadata.ContentLength |> Option.exists (fun length -> length > Protocol.MaximumRequestBodyBytes) then
                Error RequestRejection.TooLarge
            elif not (validAuthorization bearerToken metadata.AuthorizationHeaders) then
                Error RequestRejection.Unauthorized
            else
                Ok()
        | _ ->
            Error RequestRejection.Forbidden

[<RequireQualifiedAccess>]
module ControlApi =
    let private jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)

    let private writeJson statusCode payload (context: HttpContext) =
        task {
            context.Response.StatusCode <- statusCode
            context.Response.ContentType <- "application/json; charset=utf-8"

            do!
                JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    payload,
                    jsonOptions,
                    context.RequestAborted
                )
        }

    let private writeError statusCode message context =
        writeJson statusCode { Error = message } context

    let private requestMetadata (context: HttpContext) =
        { RemoteAddress = context.Connection.RemoteIpAddress |> Option.ofObj
          LocalAddress = context.Connection.LocalIpAddress |> Option.ofObj
          LocalPort = context.Connection.LocalPort
          HostHeaders = context.Request.Headers.Host |> Seq.toList
          OriginHeaders = context.Request.Headers.Origin |> Seq.toList
          AuthorizationHeaders = context.Request.Headers.Authorization |> Seq.toList
          ContentLength = context.Request.ContentLength |> Option.ofNullable }

    let private reject rejection context =
        match rejection with
        | RequestRejection.Forbidden ->
            writeError StatusCodes.Status403Forbidden "Request origin rejected" context
        | RequestRejection.Unauthorized ->
            context.Response.Headers.WWWAuthenticate <- "Bearer"
            writeError StatusCodes.Status401Unauthorized "Authentication required" context
        | RequestRejection.TooLarge ->
            writeError StatusCodes.Status413PayloadTooLarge "Request body too large" context

    let private hasJsonContentType (context: HttpContext) =
        // MediaTypeHeaderValue.TryParse is a byref-only framework parser; mutation stays at this boundary.
        let mutable parsed = Unchecked.defaultof<MediaTypeHeaderValue>

        MediaTypeHeaderValue.TryParse(context.Request.ContentType, &parsed)
        && String.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)

    let private readWorktreePath (context: HttpContext) =
        task {
            if not (hasJsonContentType context) then
                return Error(StatusCodes.Status415UnsupportedMediaType, "Expected application/json")
            else
                try
                    use! document =
                        JsonDocument.ParseAsync(
                            context.Request.Body,
                            JsonDocumentOptions(MaxDepth = 4, AllowTrailingCommas = false),
                            context.RequestAborted
                        )

                    let root = document.RootElement

                    if root.ValueKind <> JsonValueKind.Object then
                        return Error(StatusCodes.Status400BadRequest, "Malformed start request")
                    else
                        let properties = root.EnumerateObject() |> Seq.toList

                        match properties with
                        | [ property ]
                            when property.Name = "worktreePath"
                                 && property.Value.ValueKind = JsonValueKind.String ->
                            match property.Value.GetString() |> Option.ofObj with
                            | Some path when not (String.IsNullOrWhiteSpace path) ->
                                return Ok path
                            | _ ->
                                return Error(StatusCodes.Status400BadRequest, "Malformed start request")
                        | _ ->
                            return Error(StatusCodes.Status400BadRequest, "Malformed start request")
                with
                | :? BadHttpRequestException as error
                    when error.StatusCode = StatusCodes.Status413PayloadTooLarge ->
                    return Error(StatusCodes.Status413PayloadTooLarge, "Request body too large")
                | :? JsonException
                | :? IOException ->
                    return Error(StatusCodes.Status400BadRequest, "Malformed start request")
        }

    let private validSessionId (value: string) =
        value.Length = 32 && value |> Seq.forall Uri.IsHexDigit

    let private route
        hostPid
        processStartTimeUtcTicks
        hostVersion
        registry
        (lifetime: IHostApplicationLifetime)
        (context: HttpContext)
        =
        task {
            let method = context.Request.Method
            let path = context.Request.Path.Value |> Option.ofObj |> Option.defaultValue ""

            match method, path with
            | "GET", "/api/v1/health" ->
                return!
                    writeJson
                        StatusCodes.Status200OK
                        { Pid = hostPid
                          ProcessStartTimeUtcTicks = processStartTimeUtcTicks
                          HostVersion = hostVersion
                          ControlApiVersion = Protocol.ControlApiVersion }
                        context
            | "GET", "/api/v1/terminals" ->
                let! snapshot = TerminalRegistry.list registry |> Async.StartAsTask
                return! writeJson StatusCodes.Status200OK snapshot context
            | "POST", "/api/v1/terminals" ->
                match! readWorktreePath context with
                | Error(status, message) ->
                    return! writeError status message context
                | Ok path ->
                    match PathValidation.validate path with
                    | Error WorktreeValidationError.InvalidPath ->
                        return!
                            writeError
                                StatusCodes.Status400BadRequest
                                "Invalid worktree path"
                                context
                    | Error WorktreeValidationError.UnknownWorktree ->
                        return!
                            writeError
                                StatusCodes.Status404NotFound
                                "Unknown worktree path"
                                context
                    | Ok worktree ->
                        match! TerminalRegistry.start registry worktree |> Async.StartAsTask with
                        | Ok snapshot ->
                            return! writeJson StatusCodes.Status200OK snapshot context
                        | Error error ->
                            return!
                                writeError
                                    StatusCodes.Status500InternalServerError
                                    error
                                    context
            | "POST", "/api/v1/shutdown" ->
                context.Response.OnCompleted(
                    Func<Task>(fun () ->
                        lifetime.StopApplication()
                        Task.CompletedTask)
                )

                return!
                    writeJson
                        StatusCodes.Status202Accepted
                        { Accepted = true }
                        context
            | "DELETE", closePath
                when closePath.StartsWith("/api/v1/terminals/", StringComparison.Ordinal) ->
                let sessionId = closePath.Substring("/api/v1/terminals/".Length)

                if not (validSessionId sessionId) then
                    return!
                        writeError
                            StatusCodes.Status400BadRequest
                            "Invalid terminal session ID"
                            context
                else
                    let! snapshot =
                        TerminalRegistry.close registry (sessionId.ToLowerInvariant())
                        |> Async.StartAsTask

                    return! writeJson StatusCodes.Status200OK snapshot context
            | _ ->
                return!
                    writeError
                        StatusCodes.Status404NotFound
                        "Control endpoint not found"
                        context
        }

    let private handle
        config
        bearerToken
        hostPid
        processStartTimeUtcTicks
        hostVersion
        registry
        lifetime
        context
        =
        task {
            match RequestSecurity.validate config.AllowedOrigins bearerToken (requestMetadata context) with
            | Error rejection ->
                return! reject rejection context
            | Ok() ->
                return!
                    route
                        hostPid
                        processStartTimeUtcTicks
                        hostVersion
                        registry
                        lifetime
                        context
        }

    let start config bearerToken hostPid processStartTimeUtcTicks hostVersion registry =
        task {
            let builder = WebApplication.CreateSlimBuilder()
            builder.Logging.ClearProviders() |> ignore

            builder.WebHost.ConfigureKestrel(fun options ->
                options.Limits.MaxRequestBodySize <- Protocol.MaximumRequestBodyBytes
                options.AddServerHeader <- false
                options.Listen(IPAddress.Loopback, config.Port))
            |> ignore

            let application = builder.Build()
            let lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>()

            application.Run(
                RequestDelegate(fun context ->
                    handle
                        config
                        bearerToken
                        hostPid
                        processStartTimeUtcTicks
                        hostVersion
                        registry
                        lifetime
                        context
                    :> Task)
            )

            do! application.StartAsync()

            let server = application.Services.GetRequiredService<IServer>()
            let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
            let bound = addresses |> Seq.exactlyOne |> Uri
            let endpoint = $"http://127.0.0.1:{bound.Port}"

            return
                { Application = application
                  Endpoint = endpoint }
        }

    let waitForShutdown running =
        running.Application.WaitForShutdownAsync()

    let stop running =
        task {
            do! running.Application.StopAsync()
            do! running.Application.DisposeAsync().AsTask()
        }
