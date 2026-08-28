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
let private controlApiVersion = 2

[<Literal>]
let private maximumResponseBytes = 1_048_576L

// Mirrors TerminalHost.Protocol.MaximumAttachmentMessageBytes without coupling the server assembly.
[<Literal>]
let private maximumTerminalCommandFrameBytes = 16_384

[<Literal>]
let private terminalCommandSubprotocol = "treemon-command"

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

type internal TerminalMutationFailure =
    | MutationRejected of registry: RegistrySnapshot * reason: string
    | MutationUnverified of lastRegistry: RegistrySnapshot option * reason: string

let private httpClient =
    let handler = new SocketsHttpHandler(UseProxy = false, AllowAutoRedirect = false)

    new HttpClient(handler, disposeHandler = true, Timeout = Timeout.InfiniteTimeSpan)

let private apiPath version resource = $"/api/v{version}/{resource}"

let private responseError statusCode (reasonPhrase: string) (content: string) =
    let hostError =
        try
            use document = JsonDocument.Parse(content)
            let root = document.RootElement
            let error = root.GetProperty("error")

            if
                exactProperties (set [ "error" ]) Set.empty root
                && error.ValueKind = JsonValueKind.String
            then
                error.GetString()
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

            message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", manifest.BearerToken)

            body
            |> Option.iter (fun json ->
                message.Content <- new StringContent(json, Encoding.UTF8, "application/json"))

            use! response =
                httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
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
                            Error(responseError (int response.StatusCode) response.ReasonPhrase content)
        with
        | :? OperationCanceledException ->
            return Error $"TerminalHost request timed out after {config.ControlRequestTimeout.TotalSeconds:g} seconds"
        | error ->
            return Error $"TerminalHost request failed: {error.Message}"
    }

let private requestAtVersion config manifest method version resource body =
    request config manifest method (apiPath version resource) body

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
                Ok(ControlCompatibility.Incompatible $"TerminalHost control API version {apiVersion} is not supported (expected {controlApiVersion})")
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
        let! response =
            requestAtVersion config manifest HttpMethod.Get manifest.ControlApiVersion "health" None

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
                return DeadHost $"Recorded TerminalHost PID {manifest.Pid} is no longer the exact live process"
            | Ok true ->
                match! probe config manifest with
                | Ok ControlCompatibility.Compatible -> return HealthyHost manifest
                | Ok(ControlCompatibility.Incompatible error) ->
                    return IncompatibleHost(manifest, error)
                | Error probeError ->
                    match processIdentityMatches config manifest with
                    | Ok false ->
                        return DeadHost $"TerminalHost PID {manifest.Pid} exited while it was being checked"
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
    let fields = set [ "sessionId"; "worktreePath"; "attachmentEndpoint" ]

    if not (exactProperties fields Set.empty element) then
        Error "TerminalHost terminal record has an invalid shape"
    else
        try
            let stringProperty (name: string) =
                element.GetProperty(name).GetString()
                |> Option.ofObj
                |> Option.defaultValue ""

            let sessionId = stringProperty "sessionId"
            let worktreePath = stringProperty "worktreePath"
            let attachmentEndpoint = stringProperty "attachmentEndpoint"

            if not (validSessionId sessionId) then
                Error "TerminalHost returned an invalid terminal session ID"
            elif not (validCanonicalWorktreePath worktreePath) then
                Error "TerminalHost returned an invalid worktree path"
            elif not (validAttachmentEndpoint manifest sessionId attachmentEndpoint) then
                Error "TerminalHost returned an invalid attachment endpoint"
            else
                Ok
                    { SessionId = sessionId.ToLowerInvariant()
                      WorktreePath = worktreePath
                      AttachmentEndpoint = attachmentEndpoint }
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
                terminalsElement.EnumerateArray()
                |> Seq.toList
                |> List.traverseResultM (parseTerminal manifest)
                |> Result.bind (fun terminals ->
                    let distinctSessionIds = terminals |> List.map _.SessionId |> Set.ofList

                    if distinctSessionIds.Count <> terminals.Length then
                        Error "TerminalHost registry contains duplicate terminal session IDs"
                    else
                        Ok { Revision = revision; Terminals = terminals })
    with
    | :? JsonException
    | :? InvalidOperationException
    | :? FormatException
    | :? OverflowException ->
        Error "TerminalHost registry response is malformed"

let private listTerminalsAtVersion version config manifest =
    async {
        let! response =
            requestAtVersion config manifest HttpMethod.Get version "terminals" None

        return response |> Result.bind (parseRegistrySnapshot manifest)
    }

let internal listTerminals config manifest = listTerminalsAtVersion controlApiVersion config manifest

let internal findTerminalById sessionId records =
    records
    |> List.tryFind (fun terminal ->
        String.Equals(terminal.SessionId, sessionId, StringComparison.Ordinal))

let internal confirmTerminalOnHost config manifest sessionId =
    async {
        match! listTerminals config manifest with
        | Error error -> return Error(MutationUnverified(None, error))
        | Ok registry ->
            return
                if findTerminalById sessionId registry.Terminals |> Option.isSome then
                    Ok registry
                else
                    Error(MutationRejected(registry, "TerminalHost did not retain the started terminal after command delivery"))
    }

let private authoritativeRelist action lastRegistry config manifest requestResult =
    async {
        match! listTerminals config manifest with
        | Ok registry -> return Ok registry
        | Error listError ->
            let error =
                match requestResult with
                | Error requestError ->
                    $"{requestError}; authoritative relist failed: {listError}"
                | Ok _ ->
                    $"TerminalHost accepted the {action} request but its authoritative registry could not be read: {listError}"

            return Error(MutationUnverified(lastRegistry, error))
    }

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
            | Ok true -> return Error $"TerminalHost PID {manifest.Pid} did not exit within {config.StartupTimeout.TotalSeconds:g} seconds"
        }

    wait ()

let private shutdownAndWaitAtVersion version config manifest =
    async {
        let! shutdownResult =
            requestAtVersion config manifest HttpMethod.Post version "shutdown" None

        let! waitResult = waitForHostExit config manifest

        return
            match shutdownResult, waitResult with
            | _, Ok() -> Ok()
            | Ok _, Error waitError -> Error waitError
            | Error requestError, Error waitError ->
                Error $"{requestError}; exact host shutdown could not be confirmed: {waitError}"
    }

let internal shutdownAndWait config manifest = shutdownAndWaitAtVersion controlApiVersion config manifest

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

let internal validateTerminalCommand command =
    if
        String.IsNullOrWhiteSpace command
        || (command |> Seq.exists Char.IsControl)
        || Encoding.UTF8.GetByteCount($"0{command}\r") > maximumTerminalCommandFrameBytes
    then
        Error "The terminal command is invalid"
    else
        Ok command

let internal sendTerminalCommandDefault attachmentEndpoint command =
    async {
        match validateTerminalCommand command with
        | Error error -> return Error error
        | Ok validatedCommand ->
            match terminalWebSocketEndpoint attachmentEndpoint with
            | Error error -> return Error error
            | Ok endpoint ->
                use socket = new ClientWebSocket()
                socket.Options.AddSubProtocol terminalCommandSubprotocol

                use cancellation =
                    new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

                let send bytes =
                    socket.SendAsync(ArraySegment<byte> bytes, WebSocketMessageType.Binary, true, cancellation.Token)
                    |> Async.AwaitTask

                try
                    do! socket.ConnectAsync(endpoint, cancellation.Token) |> Async.AwaitTask
                    do! Encoding.UTF8.GetBytes("""{"AuthToken":"","columns":120,"rows":30}""") |> send
                    do! Encoding.UTF8.GetBytes($"0{validatedCommand}\r") |> send

                    let! _ =
                        socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Terminal command submitted", cancellation.Token)
                        |> Async.AwaitTask
                        |> Async.Catch

                    return Ok()
                with
                | :? OperationCanceledException ->
                    return Error "Timed out submitting the terminal command"
                | _ ->
                    // A ClientWebSocket exception can include its request URI. The attachment URI
                    // carries the host bearer, so never copy transport exception text to diagnostics.
                    return Error "Could not submit the terminal command"
    }

let internal defaultConfig allowedOrigins = TerminalHostProcess.defaultConfig allowedOrigins sendTerminalCommandDefault

let private preflightIncompatibleHost config manifest incompatibility =
    async {
        match! listTerminalsAtVersion manifest.ControlApiVersion config manifest with
        | Error listError ->
            return Error $"{incompatibility}; authoritative terminal list failed: {listError}"
        | Ok registry when not registry.Terminals.IsEmpty ->
            return Error incompatibility
        | Ok _ ->
            match processIdentityMatches config manifest with
            | Error error -> return Error error
            | Ok false -> return Ok None
            | Ok true ->
                let! stopped =
                    shutdownAndWaitAtVersion manifest.ControlApiVersion config manifest

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
                return
                    resolveProcessExecutable config manifest
                    |> Result.map (fun executablePath ->
                        Some
                            { Pid = manifest.Pid
                              ProcessStartTimeUtcTicks = manifest.ProcessStartTimeUtcTicks
                              ExecutablePath = executablePath
                              TerminalCount = registry.Terminals.Length })
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
                    return Error $"TerminalHost did not become healthy within {config.StartupTimeout.TotalSeconds:g} seconds: {lastError |> Option.defaultValue currentError}"
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
                return Error "The TerminalHost discovery manifest disappeared while the exact recorded host is still running"
            | Ok false -> return! launchAndDiscover config
    }

let internal startTerminalOnHost (config: Config) (manifest: DiscoveryManifest) (path: string) =
    async {
        match! listTerminals config manifest with
        | Error error -> return Error(MutationUnverified(None, error))
        | Ok before ->
            let body = JsonSerializer.Serialize({| worktreePath = path |})

            let! startResult =
                requestAtVersion config manifest HttpMethod.Post controlApiVersion "terminals" (Some body)

            match! authoritativeRelist "start" (Some before) config manifest startResult with
            | Error error -> return Error error
            | Ok after ->
                let existingIds = before.Terminals |> List.map _.SessionId |> Set.ofList
                let started =
                    after.Terminals
                    |> List.filter (fun terminal -> not (Set.contains terminal.SessionId existingIds))

                match started with
                | [ terminal ] when samePath terminal.WorktreePath path ->
                    return Ok(after, terminal)
                | _ ->
                    let error =
                        match startResult, started with
                        | Error startError, [] -> startError
                        | Ok _, [] -> "TerminalHost did not add the requested terminal to its authoritative registry"
                        | _, [ _ ] -> "TerminalHost added a terminal for an unexpected worktree"
                        | _ -> "TerminalHost added multiple terminals for one start request"

                    return Error(MutationRejected(after, error))
    }

let internal closeTerminalOnHost config manifest sessionId =
    async {
        match! listTerminals config manifest with
        | Error error -> return Error(MutationUnverified(None, error))
        | Ok before ->
            match findTerminalById sessionId before.Terminals with
            | None -> return Ok before
            | Some _ ->
                let terminalsPath = apiPath controlApiVersion "terminals"
                let! closeResult =
                    request config manifest HttpMethod.Delete $"{terminalsPath}/{sessionId}" None

                match! authoritativeRelist "close" (Some before) config manifest closeResult with
                | Error error -> return Error error
                | Ok after ->
                    match findTerminalById sessionId after.Terminals with
                    | None -> return Ok after
                    | Some _ ->
                        let error =
                            match closeResult with
                            | Error closeError -> closeError
                            | Ok _ -> "TerminalHost still lists the terminal after its close request"

                        return Error(MutationRejected(after, error))
    }

let internal closeTerminalsForWorktreeOnHost config manifest path =
    async {
        match! listTerminals config manifest with
        | Error error -> return Error(MutationUnverified(None, error))
        | Ok before ->
            let terminalIds =
                before.Terminals
                |> List.filter (fun terminal -> samePath terminal.WorktreePath path)
                |> List.map _.SessionId

            let rec closeAll latest = function
                | [] -> async.Return(Ok latest)
                | sessionId :: remaining ->
                    async {
                        match! closeTerminalOnHost config manifest sessionId with
                        | Error error -> return Error error
                        | Ok after -> return! closeAll after remaining
                    }

            return! closeAll before terminalIds
    }
