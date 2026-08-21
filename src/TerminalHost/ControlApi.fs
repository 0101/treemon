namespace TerminalHost

open System
open System.IO
open System.Net.Http.Headers
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

type ControlApiConfig =
    { Port: int
      AllowedOrigins: string list }

type RunningControlApi =
    internal
        { Application: WebApplication
          Endpoint: string }

[<RequireQualifiedAccess>]
module ControlApi =
    let private jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)

    let private terminalResponseV1 (terminal: TerminalRecord) =
        {| SessionId = terminal.SessionId; WorktreePath = terminal.WorktreePath
           AttachmentEndpoint = terminal.AttachmentEndpoint |}

    let private registryResponseV1 (snapshot: RegistrySnapshot) =
        {| Revision = snapshot.Revision
           Terminals = snapshot.Terminals |> List.map terminalResponseV1 |}

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
        writeJson statusCode {| Error = message |} context

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
                        {| Pid = hostPid
                           ProcessStartTimeUtcTicks = processStartTimeUtcTicks
                           HostVersion = hostVersion
                           ControlApiVersion = Protocol.ControlApiVersion |}
                        context
            | "GET", "/api/v1/terminals" ->
                let! snapshot = TerminalRegistry.list registry |> Async.StartAsTask
                return! writeJson StatusCodes.Status200OK (registryResponseV1 snapshot) context
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
                            return! writeJson StatusCodes.Status200OK (registryResponseV1 snapshot) context
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
                        {| Accepted = true |}
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

                    return! writeJson StatusCodes.Status200OK (registryResponseV1 snapshot) context
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
        (context: HttpContext)
        =
        task {
            let authorizationHeaders =
                context.Request.Headers.Authorization
                |> Seq.toList

            match
                RequestSecurity.validate
                    config.AllowedOrigins
                    bearerToken
                    (RequestSecurity.metadata authorizationHeaders context)
            with
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
            let buildPipeline (application: WebApplication) =
                let lifetime =
                    application.Services.GetRequiredService<IHostApplicationLifetime>()

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

            let! application, boundPort =
                LoopbackHost.start config.Port buildPipeline

            let endpoint = $"http://127.0.0.1:{boundPort}"

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
