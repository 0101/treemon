module Server.TerminalHostClient

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open FsToolkit.ErrorHandling
open Server.TerminalHostEndpoint
open Server.TerminalHostManifest
open Server.TerminalHostProcess

[<Literal>]
let private controlApiVersion = 1

[<Literal>]
let private maximumResponseBytes = 1_048_576L

type internal TerminalRecord = { SessionId: string; WorktreePath: string; AttachmentEndpoint: string }

type internal RegistrySnapshot = { Revision: int64; Terminals: TerminalRecord list }

type internal DeploymentPreflightResult =
    { Pid: int; ProcessStartTimeUtcTicks: int64
      ExecutablePath: string; TerminalCount: int }

type internal HostDiscovery =
    | MissingHost
    | HealthyHost of DiscoveryManifest
    | IncompatibleHost of manifest: DiscoveryManifest * reason: string
    | DeadHost of reason: string
    | UnusableHost of reason: string

[<RequireQualifiedAccess>]
type private ControlCompatibility = Compatible | Incompatible of reason: string

type internal StartTerminalFailure =
    | StartRejected of registry: RegistrySnapshot * reason: string
    | StartUnverified of reason: string

let private httpClient =
    let handler =
        new SocketsHttpHandler(
            UseProxy = false,
            AllowAutoRedirect = false
        )

    new HttpClient(handler, disposeHandler = true, Timeout = Timeout.InfiniteTimeSpan)

let private responseError statusCode (reasonPhrase: string) (content: string) =
    let hostError =
        try
            use document = JsonDocument.Parse(content)
            let root = document.RootElement

            if
                exactProperties (set [ "error" ]) Set.empty root
                && root.GetProperty("error").ValueKind = JsonValueKind.String
            then
                root.GetProperty("error").GetString()
                |> Option.ofObj
                |> Option.filter (validBoundedText 512)
            else
                None
        with _ ->
            None

    let detail =
        hostError
        |> Option.orElseWith (fun () ->
            reasonPhrase
            |> Option.ofObj
            |> Option.filter (validBoundedText 128))
        |> Option.defaultValue "request failed"

    $"TerminalHost returned HTTP {statusCode}: {detail}"

let private request
    (config: Config)
    (manifest: DiscoveryManifest)
    (method: HttpMethod)
    (path: string)
    (body: string option)
    =
    async {
        try
            use timeout =
                new CancellationTokenSource(config.ControlRequestTimeout)

            let endpoint = Uri(manifest.Endpoint)
            use message = new HttpRequestMessage(method, Uri(endpoint, path))

            message.Headers.Authorization <-
                AuthenticationHeaderValue(
                    "Bearer",
                    manifest.BearerToken
                )

            body
            |> Option.iter (fun json ->
                message.Content <-
                    new StringContent(json, Encoding.UTF8, "application/json"))

            use! response =
                httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token
                )
                |> Async.AwaitTask

            match response.Content.Headers.ContentLength |> Option.ofNullable with
            | Some length when length > maximumResponseBytes ->
                return Error "TerminalHost response exceeded 1 MiB"
            | _ ->
                let! bytes =
                    response.Content.ReadAsByteArrayAsync(timeout.Token)
                    |> Async.AwaitTask

                if int64 bytes.Length > maximumResponseBytes then
                    return Error "TerminalHost response exceeded 1 MiB"
                else
                    let content = Encoding.UTF8.GetString bytes

                    if response.IsSuccessStatusCode then
                        return Ok content
                    else
                        return
                            Error(
                                responseError
                                    (int response.StatusCode)
                                    response.ReasonPhrase
                                    content
                            )
        with
        | :? OperationCanceledException ->
            return
                Error
                    $"TerminalHost request timed out after {config.ControlRequestTimeout.TotalSeconds:g} seconds"
        | error ->
            return Error $"TerminalHost request failed: {error.Message}"
    }

let private parseHealth (manifest: DiscoveryManifest) (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        let fields =
            set [ "pid"; "processStartTimeUtcTicks"; "hostVersion"; "controlApiVersion" ]

        if not (exactProperties fields Set.empty root) then
            Error "TerminalHost health response has an invalid shape"
        else
            let pid = root.GetProperty("pid").GetInt32()
            let startTicks = root.GetProperty("processStartTimeUtcTicks").GetInt64()
            let hostVersion = root.GetProperty("hostVersion").GetString()
            let apiVersion = root.GetProperty("controlApiVersion").GetInt32()

            if
                pid <> manifest.Pid
                || startTicks <> manifest.ProcessStartTimeUtcTicks
                || hostVersion <> manifest.HostVersion
                || apiVersion <> manifest.ControlApiVersion
            then
                Error "TerminalHost health identity does not match its discovery manifest"
            elif apiVersion <> controlApiVersion then
                let error =
                    $"TerminalHost control API version {apiVersion} is not supported (expected {controlApiVersion})"

                Ok(ControlCompatibility.Incompatible error)
            else
                Ok ControlCompatibility.Compatible
    with
    | :? JsonException
    | :? InvalidOperationException
    | :? FormatException
    | :? OverflowException ->
        Error "TerminalHost health response is malformed"

let private probe config manifest =
    async {
        let! response = request config manifest HttpMethod.Get "/api/v1/health" None
        return response |> Result.bind (parseHealth manifest)
    }

let internal discoverHost config =
    async {
        match readManifest config with
        | Error error -> return UnusableHost error
        | Ok None -> return MissingHost
        | Ok(Some manifest) ->
            match processIdentityMatches config manifest with
            | Error error -> return UnusableHost error
            | Ok false ->
                let error =
                    $"Recorded TerminalHost PID {manifest.Pid} is no longer the exact live process"

                return DeadHost error
            | Ok true ->
                match! probe config manifest with
                | Ok ControlCompatibility.Compatible -> return HealthyHost manifest
                | Ok(ControlCompatibility.Incompatible error) ->
                    return IncompatibleHost(manifest, error)
                | Error probeError ->
                    match processIdentityMatches config manifest with
                    | Ok false ->
                        let error =
                            $"TerminalHost PID {manifest.Pid} exited while it was being checked"

                        return DeadHost error
                    | Ok true -> return UnusableHost probeError
                    | Error identityError -> return UnusableHost identityError
    }

let private validCanonicalWorktreePath path =
    try
        not (String.IsNullOrWhiteSpace path)
        && path.Length <= 32_767
        && path.IndexOf('\u0000') < 0
        && Path.IsPathFullyQualified path
        && samePath
            (Path.GetFullPath(path)
             |> Path.TrimEndingDirectorySeparator)
            path
    with _ ->
        false

let private validAttachmentEndpoint manifest sessionId value =
    try
        let endpoint = Uri(value, UriKind.Absolute)
        let expectedPath =
            $"/_treemon/{sessionId}/{manifest.BearerToken}/"

        isLoopbackHttpUri endpoint
        && endpoint.Port <> 5000
        && endpoint.AbsolutePath = expectedPath
    with _ ->
        false

let private parseTerminal manifest (element: JsonElement) =
    let fields =
        set
            [ "sessionId"
              "worktreePath"
              "attachmentEndpoint" ]

    if not (exactProperties fields Set.empty element) then
        Error "TerminalHost terminal record has an invalid shape"
    else
        try
            let sessionId = element.GetProperty("sessionId").GetString()
            let worktreePath = element.GetProperty("worktreePath").GetString() |> Option.ofObj
            let attachmentEndpoint = element.GetProperty("attachmentEndpoint").GetString() |> Option.ofObj

            if sessionId |> Option.ofObj |> Option.exists validSessionId |> not then
                Error "TerminalHost returned an invalid terminal session ID"
            elif
                worktreePath
                |> Option.exists validCanonicalWorktreePath
                |> not
            then
                Error "TerminalHost returned an invalid worktree path"
            elif
                attachmentEndpoint
                |> Option.exists (validAttachmentEndpoint manifest sessionId)
                |> not
            then
                Error "TerminalHost returned an invalid attachment endpoint"
            else
                Ok
                    { SessionId = sessionId.ToLowerInvariant()
                      WorktreePath = worktreePath |> Option.get
                      AttachmentEndpoint = attachmentEndpoint |> Option.get }
        with
        | :? InvalidOperationException ->
            Error "TerminalHost terminal record is malformed"

let private parseRegistrySnapshot (manifest: DiscoveryManifest) (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        if not (exactProperties (set [ "revision"; "terminals" ]) Set.empty root) then
            Error "TerminalHost registry response has an invalid shape"
        else
            let revision = root.GetProperty("revision").GetInt64()
            let terminalsElement = root.GetProperty("terminals")

            if
                revision < 0L
                || terminalsElement.ValueKind <> JsonValueKind.Array
                || terminalsElement.GetArrayLength() > 1024
            then
                Error "TerminalHost registry response is malformed"
            else
                match
                    terminalsElement.EnumerateArray()
                    |> Seq.toList
                    |> List.traverseResultM (parseTerminal manifest)
                with
                | Error error -> Error error
                | Ok terminals ->
                    let sessionIds =
                        terminals
                        |> List.map _.SessionId
                        |> Set.ofList

                    let worktreeKeys =
                        terminals
                        |> List.map (_.WorktreePath >> pathKey)
                        |> Set.ofList

                    if
                        sessionIds.Count <> terminals.Length
                        || worktreeKeys.Count <> terminals.Length
                    then
                        Error "TerminalHost registry contains duplicate terminals"
                    else
                        Ok
                            { Revision = revision
                              Terminals = terminals }
    with
    | :? JsonException
    | :? InvalidOperationException
    | :? FormatException
    | :? OverflowException ->
        Error "TerminalHost registry response is malformed"

let internal listTerminals config manifest =
    async {
        let! response = request config manifest HttpMethod.Get "/api/v1/terminals" None
        return response |> Result.bind (parseRegistrySnapshot manifest)
    }

let internal findTerminalByPath path records =
    records |> List.tryFind (fun terminal -> samePath terminal.WorktreePath path)

let internal requestTerminalClose config manifest sessionId =
    request config manifest HttpMethod.Delete $"/api/v1/terminals/{sessionId}" None

let internal requestHostShutdown config manifest =
    request config manifest HttpMethod.Post "/api/v1/shutdown" None

let internal waitForHostExit config manifest =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec wait () =
        async {
            match processIdentityMatches config manifest with
            | Error error -> return Error error
            | Ok false -> return Ok()
            | Ok true when DateTimeOffset.UtcNow < deadline ->
                do! Async.Sleep(probeDelayMilliseconds config)
                return! wait ()
            | Ok true ->
                return
                    Error
                        $"TerminalHost PID {manifest.Pid} did not exit within {config.StartupTimeout.TotalSeconds:g} seconds"
        }

    wait ()

let internal shutdownAndWait config manifest =
    async {
        let! shutdownResult = requestHostShutdown config manifest
        let! waitResult = waitForHostExit config manifest

        return
            match shutdownResult, waitResult with
            | _, Ok() -> Ok()
            | Ok _, Error waitError -> Error waitError
            | Error requestError, Error waitError ->
                Error
                    $"{requestError}; exact host shutdown could not be confirmed: {waitError}"
    }

let private terminalWebSocketEndpoint (attachmentEndpoint: string) =
    try
        let endpoint = Uri(attachmentEndpoint, UriKind.Absolute)

        if
            not (isLoopbackHttpUri endpoint)
            || not (endpoint.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        then
            Error "TerminalHost returned an invalid command attachment endpoint"
        else
            let builder = UriBuilder(endpoint)
            builder.Scheme <- "ws"
            builder.Path <- $"{endpoint.AbsolutePath}ws"
            Ok builder.Uri
    with _ ->
        Error "TerminalHost returned an invalid command attachment endpoint"

let internal sendTerminalCommandDefault attachmentEndpoint command =
    async {
        if
            String.IsNullOrWhiteSpace command
            || command.Length > 65_000
            || (command |> Seq.exists Char.IsControl)
        then
            return Error "The terminal resume command is invalid"
        else
            match terminalWebSocketEndpoint attachmentEndpoint with
            | Error error -> return Error error
            | Ok endpoint ->
                use socket = new ClientWebSocket()
                socket.Options.AddSubProtocol("tty")

                use cancellation =
                    new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

                let send bytes =
                    socket.SendAsync(
                        ArraySegment<byte> bytes,
                        WebSocketMessageType.Binary, true, cancellation.Token
                    )
                    |> Async.AwaitTask

                try
                    do! socket.ConnectAsync(endpoint, cancellation.Token) |> Async.AwaitTask
                    do! Encoding.UTF8.GetBytes("""{"AuthToken":"","columns":120,"rows":30}""") |> send
                    do! Encoding.UTF8.GetBytes($"0{command}\r") |> send

                    let! _ =
                        socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Resume command submitted",
                            cancellation.Token
                        )
                        |> Async.AwaitTask
                        |> Async.Catch

                    return Ok()
                with
                | :? OperationCanceledException ->
                    return Error "Timed out submitting the Copilot resume command"
                | _ ->
                    // A ClientWebSocket exception can include its request URI. The attachment URI
                    // carries the host bearer, so never copy transport exception text to diagnostics.
                    return Error "Could not submit the Copilot resume command"
    }

let internal defaultConfig allowedOrigins = TerminalHostProcess.defaultConfig allowedOrigins sendTerminalCommandDefault

let private deploymentPreflightResult config manifest registry =
    resolveProcessExecutable config manifest
    |> Result.map (fun executablePath ->
            Some
                { Pid = manifest.Pid
                  ProcessStartTimeUtcTicks =
                    manifest.ProcessStartTimeUtcTicks
                  ExecutablePath = executablePath
                  TerminalCount = registry.Terminals.Length })

let private preflightIncompatibleHost config manifest incompatibility =
    async {
        match! listTerminals config manifest with
        | Error listError ->
            return Error $"{incompatibility}; authoritative terminal list failed: {listError}"
        | Ok registry when not registry.Terminals.IsEmpty ->
            return Error incompatibility
        | Ok _ ->
            match processIdentityMatches config manifest with
            | Error error -> return Error error
            | Ok false -> return Ok None
            | Ok true ->
                let! stopped = shutdownAndWait config manifest
                return
                    stopped
                    |> Result.map (fun () -> None)
                    |> Result.mapError (fun error ->
                        $"The incompatible empty TerminalHost could not be stopped: {error}")
    }

let internal preflightDeploymentWith config =
    async {
        match! discoverHost config with
        | MissingHost
        | DeadHost _ -> return Ok None
        | UnusableHost error -> return Error error
        | IncompatibleHost(manifest, error) ->
            return! preflightIncompatibleHost config manifest error
        | HealthyHost manifest ->
            match! listTerminals config manifest with
            | Error error -> return Error error
            | Ok registry ->
                return deploymentPreflightResult config manifest registry
    }

let internal waitForHealthyHost config =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec wait lastError =
        async {
            match! discoverHost config with
            | HealthyHost connection -> return Ok connection
            | discovery ->
                let currentError =
                    match discovery with
                    | MissingHost -> "TerminalHost has not published its discovery manifest"
                    | DeadHost error
                    | UnusableHost error -> error
                    | IncompatibleHost(_, error) -> error
                    | HealthyHost _ -> failwith "unreachable"

                if DateTimeOffset.UtcNow >= deadline then
                    return
                        Error
                            $"TerminalHost did not become healthy within {config.StartupTimeout.TotalSeconds:g} seconds: {lastError |> Option.defaultValue currentError}"
                else
                    do! Async.Sleep(probeDelayMilliseconds config)
                    return! wait (Some currentError)
        }

    wait None

let internal knownHostIsStillLive config = function
    | None -> Ok false
    | Some host -> processIdentityMatches config host

let private launchAndDiscover config =
    asyncResult {
        do! startHostProcess config
        return! waitForHealthyHost config
    }

let internal ensureHost config lastHost =
    async {
        match! discoverHost config with
        | HealthyHost connection -> return Ok connection
        | DeadHost _ -> return! launchAndDiscover config
        | IncompatibleHost(_, error)
        | UnusableHost error -> return Error error
        | MissingHost ->
            match knownHostIsStillLive config lastHost with
            | Error error -> return Error error
            | Ok true ->
                return
                    Error
                        "The TerminalHost discovery manifest disappeared while the exact recorded host is still running"
            | Ok false -> return! launchAndDiscover config
    }

let internal startTerminalOnHost
    (config: Config)
    (manifest: DiscoveryManifest)
    (path: string)
    =
    async {
        let body =
            JsonSerializer.Serialize(
                {| worktreePath = path |}
            )

        let! startResult =
            request
                config
                manifest
                HttpMethod.Post
                "/api/v1/terminals"
                (Some body)

        match! listTerminals config manifest with
        | Error listError ->
            let error =
                match startResult with
                | Error startError ->
                    $"{startError}; authoritative relist failed: {listError}"
                | Ok _ ->
                    $"TerminalHost accepted the start request but its authoritative registry could not be read: {listError}"

            return Error(StartUnverified error)
        | Ok registry ->
            match findTerminalByPath path registry.Terminals with
            | Some terminal -> return Ok(registry, terminal)
            | None ->
                let error =
                    match startResult with
                    | Error startError -> startError
                    | Ok _ ->
                        "TerminalHost did not include the requested terminal in its authoritative registry"

                return Error(StartRejected(registry, error))
    }
