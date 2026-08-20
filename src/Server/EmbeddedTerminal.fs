module Server.EmbeddedTerminal

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Shared

[<Literal>]
let private controlApiVersion = 1

[<Literal>]
let private maximumManifestBytes = 65_536L

[<Literal>]
let private maximumResponseBytes = 1_048_576L

type private DiscoveryManifest =
    { Pid: int
      ProcessStartTimeUtcTicks: int64
      Endpoint: string
      BearerToken: string
      HostVersion: string
      ControlApiVersion: int
      StagedExecutableVersion: string option }

type private HostConnection =
    { Manifest: DiscoveryManifest }

type private TerminalRecord =
    { SessionId: string
      WorktreePath: string
      AttachmentEndpoint: string }

type private RegistrySnapshot =
    { Revision: int64
      Terminals: TerminalRecord list }

type internal Config =
    { HostExecutablePath: string
      HostStateDirectory: string
      TtydExecutablePath: string option
      ShellCommand: string
      AllowedOrigins: string list
      StartupTimeout: TimeSpan
      ControlRequestTimeout: TimeSpan
      ProbeInterval: TimeSpan
      LaunchHost: ProcessStartInfo -> Result<unit, string>
      ProcessIdentityMatches: int -> int64 -> Result<bool, string>
      ResolveProcessExecutable: int -> int64 -> Result<string, string>
      SendTerminalCommand: string -> string -> Async<Result<unit, string>> }

type internal ReplacementOwnedSession =
    { TerminalSessionId: SessionActivity.TerminalSessionId
      CopilotSessionId: SessionActivity.SessionId
      Status: SessionActivity.SessionLevelStatus }

type internal ReplacementActivitySnapshot =
    { ActivityEpoch: int64
      OpenSessions: ReplacementOwnedSession list
      ResumableSessionIds:
        Map<SessionActivity.TerminalSessionId, SessionActivity.SessionId> }

type internal ReplacementActivityQuery =
    DateTimeOffset
        -> Set<SessionActivity.TerminalSessionId>
        -> Result<ReplacementActivitySnapshot, string>

[<RequireQualifiedAccess>]
type internal ReplacementOutcome =
    | NoCandidate
    | WaitingForIdle
    | RaceLost
    | Replaced of stagedVersion: string
    | Failed of stagedVersion: string * error: string

type private HostDiscovery =
    | MissingHost
    | HealthyHost of HostConnection
    | DeadHost of reason: string
    | UnusableHost of reason: string

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      LastHost: DiscoveryManifest option }

type private ReplacementPlan =
    { OldHost: DiscoveryManifest
      OldExecutablePath: string
      StagedVersion: string
      StagedExecutablePath: string
      RegistryRevision: int64
      Terminals: TerminalRecord list
      ActivityEpoch: int64 }

type private HostLaunchOutcome =
    | LaunchRejected of string
    | LaunchStartedButUnhealthy of string
    | HostLaunched of HostConnection

type private ReplacementRecheck =
    | ReadyToCommit of HostConnection * ReplacementActivitySnapshot
    | RecheckChanged
    | RecheckFailed of string

type private Message =
    | Start of
        WorktreePath *
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | Close of WorktreePath * AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | CloseStrict of
        WorktreePath *
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | ShutdownHost of AsyncReplyChannel<Result<unit, string>>
    | TryCommitReplacement of
        ReplacementPlan *
        ReplacementActivityQuery *
        AsyncReplyChannel<ReplacementOutcome>

type Manager =
    private
        | Manager of Config * MailboxProcessor<Message>

let private pathComparison =
    if OperatingSystem.IsWindows() then
        StringComparison.OrdinalIgnoreCase
    else
        StringComparison.Ordinal

let private samePath left right =
    String.Equals(left, right, pathComparison)

let private pathKey (path: string) =
    if OperatingSystem.IsWindows() then
        path.ToUpperInvariant()
    else
        path

let private hostIdentityMatches left right =
    left.Pid = right.Pid
    && left.ProcessStartTimeUtcTicks = right.ProcessStartTimeUtcTicks

let private validBoundedText maximum (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && value.Length <= maximum
    && value
       |> Seq.forall (fun character ->
           not (Char.IsControl character))

let private validVersion (value: string) =
    validBoundedText 128 value
    && value
       |> Seq.forall (fun character ->
           Char.IsAsciiLetterOrDigit character
           || character = '.'
           || character = '-'
           || character = '_'
           || character = '+')

let private validStagedVersion (value: string) =
    validBoundedText 128 value
    && value
       |> Seq.forall (fun character ->
           Char.IsAsciiLetterOrDigit character
           || character = '.'
           || character = '-'
           || character = '_')

let private validBearerToken (value: string) =
    value.Length >= 32
    && value.Length <= 128
    && value
       |> Seq.forall (fun character ->
           Char.IsAsciiLetterOrDigit character
           || character = '-'
           || character = '_')

let private validSessionId (value: string) =
    value.Length = 32
    && value |> Seq.forall Uri.IsHexDigit

let private exactProperties required optional (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Object then
        false
    else
        let names =
            element.EnumerateObject()
            |> Seq.map _.Name
            |> Seq.toList

        let distinct = names |> Set.ofList
        let allowed = Set.union required optional

        names.Length = distinct.Count
        && Set.isSubset required distinct
        && Set.isSubset distinct allowed

let private optionalString name (element: JsonElement) =
    match
        element.EnumerateObject()
        |> Seq.tryFind (fun property -> property.Name = name)
    with
    | None -> Ok None
    | Some property when property.Value.ValueKind = JsonValueKind.String ->
        match property.Value.GetString() |> Option.ofObj with
        | Some value -> Ok(Some value)
        | None -> Error $"{name} must be a JSON string"
    | Some _ -> Error $"{name} must be a JSON string"

let private validControlEndpoint (value: string) =
    try
        let endpoint = Uri(value, UriKind.Absolute)

        endpoint.Scheme = Uri.UriSchemeHttp
        && endpoint.Host = "127.0.0.1"
        && endpoint.Port > 0
        && endpoint.Port <= 65_535
        && endpoint.AbsolutePath = "/"
        && String.IsNullOrEmpty endpoint.Query
        && String.IsNullOrEmpty endpoint.Fragment
        && String.IsNullOrEmpty endpoint.UserInfo
    with _ ->
        false

let private parseManifest (text: string) =
    try
        use document =
            JsonDocument.Parse(
                text,
                JsonDocumentOptions(
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                )
            )

        let root = document.RootElement

        let required =
            set
                [ "pid"
                  "processStartTimeUtcTicks"
                  "endpoint"
                  "bearerToken"
                  "hostVersion"
                  "controlApiVersion" ]

        let optional = set [ "stagedExecutableVersion" ]

        if not (exactProperties required optional root) then
            Error "TerminalHost discovery manifest has an invalid shape"
        else
            let pid = root.GetProperty("pid").GetInt32()
            let processStartTimeUtcTicks =
                root.GetProperty("processStartTimeUtcTicks").GetInt64()
            let endpoint = root.GetProperty("endpoint").GetString()
            let bearerToken = root.GetProperty("bearerToken").GetString()
            let hostVersion = root.GetProperty("hostVersion").GetString()
            let apiVersion = root.GetProperty("controlApiVersion").GetInt32()

            match optionalString "stagedExecutableVersion" root with
            | Error _ ->
                Error "TerminalHost discovery manifest has an invalid staged executable version"
            | Ok stagedVersion ->
                if pid <= 0 || processStartTimeUtcTicks <= 0L then
                    Error "TerminalHost discovery manifest has an invalid process identity"
                elif endpoint |> Option.ofObj |> Option.exists validControlEndpoint |> not then
                    Error "TerminalHost discovery manifest has an invalid control endpoint"
                elif bearerToken |> Option.ofObj |> Option.exists validBearerToken |> not then
                    Error "TerminalHost discovery manifest has an invalid bearer token"
                elif hostVersion |> Option.ofObj |> Option.exists validVersion |> not then
                    Error "TerminalHost discovery manifest has an invalid host version"
                elif
                    stagedVersion
                    |> Option.exists (validStagedVersion >> not)
                then
                    Error "TerminalHost discovery manifest has an invalid staged executable version"
                else
                    Ok
                        { Pid = pid
                          ProcessStartTimeUtcTicks = processStartTimeUtcTicks
                          Endpoint = endpoint
                          BearerToken = bearerToken
                          HostVersion = hostVersion
                          ControlApiVersion = apiVersion
                          StagedExecutableVersion = stagedVersion }
    with
    | :? JsonException
    | :? InvalidOperationException
    | :? FormatException
    | :? OverflowException ->
        Error "TerminalHost discovery manifest is malformed"

let private manifestPath config =
    Path.Combine(config.HostStateDirectory, "host.json")

let private readManifest config =
    let path = manifestPath config

    try
        let info = FileInfo path

        if not info.Exists then
            Ok None
        elif
            info.Length <= 0L
            || info.Length > maximumManifestBytes
            || (info.Attributes &&& FileAttributes.ReparsePoint) <> enum 0
        then
            Error "TerminalHost discovery manifest is invalid"
        else
            File.ReadAllText(path, Encoding.UTF8)
            |> parseManifest
            |> Result.map Some
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException ->
        Ok None
    | error ->
        Error $"Could not read the TerminalHost discovery manifest: {error.Message}"

let private processIdentityMatchesDefault pid processStartTimeUtcTicks =
    try
        use child = Process.GetProcessById pid

        if child.HasExited then
            Ok false
        else
            let startTicks =
                child.StartTime.ToUniversalTime().Ticks

            Ok(startTicks = processStartTimeUtcTicks)
    with
    | :? ArgumentException -> Ok false
    | :? InvalidOperationException -> Ok false
    | error ->
        Error $"Could not verify TerminalHost process identity: {error.Message}"

let private resolveProcessExecutableDefault pid processStartTimeUtcTicks =
    try
        use child = Process.GetProcessById pid

        if child.HasExited then
            Error "The recorded TerminalHost process has exited"
        elif child.StartTime.ToUniversalTime().Ticks <> processStartTimeUtcTicks then
            Error "The recorded TerminalHost process identity no longer matches"
        else
            match child.MainModule |> Option.ofObj with
            | Some mainModule when not (String.IsNullOrWhiteSpace mainModule.FileName) ->
                Ok(Path.GetFullPath mainModule.FileName)
            | _ ->
                Error "Could not resolve the exact TerminalHost executable path"
    with
    | :? ArgumentException
    | :? InvalidOperationException as error ->
        Error $"Could not resolve the exact TerminalHost executable path: {error.Message}"
    | error ->
        Error $"Could not resolve the exact TerminalHost executable path: {error.Message}"

let private processIdentityMatches config (manifest: DiscoveryManifest) =
    config.ProcessIdentityMatches
        manifest.Pid
        manifest.ProcessStartTimeUtcTicks

let private resolveProcessExecutable config (manifest: DiscoveryManifest) =
    config.ResolveProcessExecutable
        manifest.Pid
        manifest.ProcessStartTimeUtcTicks

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
    (connection: HostConnection)
    (method: HttpMethod)
    (path: string)
    (body: string option)
    =
    async {
        try
            use timeout =
                new CancellationTokenSource(config.ControlRequestTimeout)

            let endpoint = Uri(connection.Manifest.Endpoint)
            use message = new HttpRequestMessage(method, Uri(endpoint, path))

            message.Headers.Authorization <-
                AuthenticationHeaderValue(
                    "Bearer",
                    connection.Manifest.BearerToken
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
            set
                [ "pid"
                  "processStartTimeUtcTicks"
                  "hostVersion"
                  "controlApiVersion" ]

        if not (exactProperties fields Set.empty root) then
            Error "TerminalHost health response has an invalid shape"
        else
            let pid = root.GetProperty("pid").GetInt32()
            let startTicks =
                root.GetProperty("processStartTimeUtcTicks").GetInt64()
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
                Error
                    $"TerminalHost control API version {apiVersion} is not supported (expected {controlApiVersion})"
            else
                Ok()
    with
    | :? JsonException
    | :? InvalidOperationException
    | :? FormatException
    | :? OverflowException ->
        Error "TerminalHost health response is malformed"

let private probe config manifest =
    async {
        let connection = { Manifest = manifest }

        match! request config connection HttpMethod.Get "/api/v1/health" None with
        | Error error -> return Error error
        | Ok content ->
            return
                parseHealth manifest content
                |> Result.map (fun () -> connection)
    }

let private discoverHost config =
    async {
        match readManifest config with
        | Error error -> return UnusableHost error
        | Ok None -> return MissingHost
        | Ok(Some manifest) ->
            match processIdentityMatches config manifest with
            | Error error -> return UnusableHost error
            | Ok false ->
                return
                    DeadHost
                        $"Recorded TerminalHost PID {manifest.Pid} is no longer the exact live process"
            | Ok true ->
                match! probe config manifest with
                | Ok connection -> return HealthyHost connection
                | Error probeError ->
                    match processIdentityMatches config manifest with
                    | Ok false ->
                        return
                            DeadHost
                                $"TerminalHost PID {manifest.Pid} exited while it was being checked"
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

        endpoint.Scheme = Uri.UriSchemeHttp
        && endpoint.Host = "127.0.0.1"
        && endpoint.Port > 0
        && endpoint.Port <= 65_535
        && endpoint.Port <> 5000
        && endpoint.AbsolutePath = expectedPath
        && String.IsNullOrEmpty endpoint.Query
        && String.IsNullOrEmpty endpoint.Fragment
        && String.IsNullOrEmpty endpoint.UserInfo
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
            let worktreePath = element.GetProperty("worktreePath").GetString()
            let attachmentEndpoint =
                element.GetProperty("attachmentEndpoint").GetString()

            if sessionId |> Option.ofObj |> Option.exists validSessionId |> not then
                Error "TerminalHost returned an invalid terminal session ID"
            elif
                worktreePath
                |> Option.ofObj
                |> Option.exists validCanonicalWorktreePath
                |> not
            then
                Error "TerminalHost returned an invalid worktree path"
            elif
                attachmentEndpoint
                |> Option.ofObj
                |> Option.exists (validAttachmentEndpoint manifest sessionId)
                |> not
            then
                Error "TerminalHost returned an invalid attachment endpoint"
            else
                Ok
                    { SessionId = sessionId.ToLowerInvariant()
                      WorktreePath = worktreePath
                      AttachmentEndpoint = attachmentEndpoint }
        with
        | :? InvalidOperationException ->
            Error "TerminalHost terminal record is malformed"

let private sequenceResults results =
    let folder state result =
        match state, result with
        | Error error, _ -> Error error
        | Ok _, Error error -> Error error
        | Ok values, Ok value -> Ok(value :: values)

    results
    |> List.fold folder (Ok [])
    |> Result.map List.rev

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
                    |> Seq.map (parseTerminal manifest)
                    |> Seq.toList
                    |> sequenceResults
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

let private listTerminals config connection =
    async {
        match!
            request
                config
                connection
                HttpMethod.Get
                "/api/v1/terminals"
                None
        with
        | Error error -> return Error error
        | Ok content ->
            return parseRegistrySnapshot connection.Manifest content
    }

let private launchDetached (startInfo: ProcessStartInfo) =
    try
        if not (File.Exists startInfo.FileName) then
            Error $"TerminalHost executable was not found at '{startInfo.FileName}'"
        else
            match Process.Start startInfo |> Option.ofObj with
            | None -> Error "Windows did not start TerminalHost"
            | Some child ->
                child.Dispose()
                Ok()
    with error ->
        Error $"Could not start TerminalHost: {error.Message}"

let private terminalWebSocketEndpoint (attachmentEndpoint: string) =
    try
        let endpoint = Uri(attachmentEndpoint, UriKind.Absolute)

        if
            endpoint.Scheme <> Uri.UriSchemeHttp
            || endpoint.Host <> "127.0.0.1"
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

let private sendTerminalCommandDefault attachmentEndpoint command =
    async {
        if
            String.IsNullOrWhiteSpace command
            || command.Length > 65_000
            || command.IndexOf('\u0000') >= 0
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

let private defaultStateDirectory () =
    let localApplicationData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

    let root =
        if String.IsNullOrWhiteSpace localApplicationData then
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".treemon"
            )
        else
            Path.Combine(localApplicationData, "Treemon")

    Path.Combine(root, "TerminalHost")

let private defaultHostExecutable () =
    let executableName =
        if OperatingSystem.IsWindows() then
            "TerminalHost.exe"
        else
            "TerminalHost"

    [ Path.Combine(AppContext.BaseDirectory, "terminal-host", executableName)
      Path.Combine(AppContext.BaseDirectory, executableName)
      Path.Combine(
          __SOURCE_DIRECTORY__,
          "..",
          "TerminalHost",
          "bin",
          "Debug",
          "net10.0",
          executableName
      )
      Path.Combine(
          __SOURCE_DIRECTORY__,
          "..",
          "TerminalHost",
          "bin",
          "Release",
          "net10.0",
          executableName
      ) ]
    |> List.map Path.GetFullPath
    |> List.tryFind File.Exists
    |> Option.defaultWith (fun () ->
        Path.Combine(AppContext.BaseDirectory, "terminal-host", executableName))

let private distinctOrigins (origins: string list) =
    origins
    |> List.fold (fun seen origin -> Map.add (origin.ToUpperInvariant()) origin seen) Map.empty
    |> Map.values
    |> Seq.toList

let private originsFor (serverOrigin: string) =
    try
        let origin = Uri(serverOrigin, UriKind.Absolute)
        let scheme = origin.Scheme
        let port = origin.Port

        [ origin.GetLeftPart(UriPartial.Authority)
          $"{scheme}://localhost:{port}"
          $"{scheme}://127.0.0.1:{port}"
          if port = 5001 then
              "http://localhost:5174"
              "http://127.0.0.1:5174" ]
        |> distinctOrigins
    with _ ->
        [ serverOrigin ]

let private defaultConfig allowedOrigins =
    let stateDirectory =
        Environment.GetEnvironmentVariable("TREEMON_TERMINAL_HOST_STATE_DIR")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith defaultStateDirectory
        |> Path.GetFullPath

    let hostExecutable = defaultHostExecutable ()
    let adjacentTtyd =
        Path.Combine(
            hostExecutable
            |> Path.GetDirectoryName
            |> Option.ofObj
            |> Option.defaultValue AppContext.BaseDirectory,
            "ttyd.exe"
        )

    { HostExecutablePath = hostExecutable
      HostStateDirectory = stateDirectory
      TtydExecutablePath =
        adjacentTtyd
        |> Option.ofObj
        |> Option.filter File.Exists
      ShellCommand = "pwsh"
      AllowedOrigins = allowedOrigins
      StartupTimeout = TimeSpan.FromSeconds 30.0
      ControlRequestTimeout = TimeSpan.FromSeconds 10.0
      ProbeInterval = TimeSpan.FromMilliseconds 100.0
      LaunchHost = launchDetached
      ProcessIdentityMatches = processIdentityMatchesDefault
      ResolveProcessExecutable = resolveProcessExecutableDefault
      SendTerminalCommand = sendTerminalCommandDefault }

let private hostStartInfo config =
    let workingDirectory =
        config.HostExecutablePath
        |> Path.GetDirectoryName
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue AppContext.BaseDirectory

    let startInfo =
        ProcessStartInfo(
            FileName = config.HostExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        )

    [ "--state-dir"
      config.HostStateDirectory
      "--shell"
      config.ShellCommand
      match config.TtydExecutablePath with
      | Some path ->
          "--ttyd"
          path
      | None -> ()
      for origin in config.AllowedOrigins do
          "--allowed-origin"
          origin ]
    |> List.iter startInfo.ArgumentList.Add

    startInfo

let private startHostProcess config =
    try
        Directory.CreateDirectory config.HostStateDirectory
        |> ignore

        config.LaunchHost(hostStartInfo config)
    with error ->
        Error $"Could not prepare TerminalHost startup: {error.Message}"

let private probeDelayMilliseconds config =
    config.ProbeInterval.TotalMilliseconds
    |> max 1.0
    |> min (float Int32.MaxValue)
    |> int

let private waitForHealthyHost config =
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

let private knownHostIsStillLive config lastHost =
    match lastHost with
    | None -> Ok false
    | Some host -> processIdentityMatches config host

let private launchAndDiscover config =
    async {
        match startHostProcess config with
        | Error error -> return Error error
        | Ok() -> return! waitForHealthyHost config
    }

let private ensureHost config lastHost =
    async {
        match! discoverHost config with
        | HealthyHost connection -> return Ok connection
        | DeadHost _ -> return! launchAndDiscover config
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

let private terminalForPath path records =
    records
    |> List.tryFind (fun terminal ->
        samePath terminal.WorktreePath path)

let private withoutPath path snapshot =
    { Tabs =
        snapshot.Tabs
        |> List.filter (fun tab ->
            not (samePath (WorktreePath.value tab.Worktree) path)) }

let private interrupted error tab =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Running _
    | EmbeddedTerminalLifecycle.Starting ->
        { tab with
            Lifecycle = EmbeddedTerminalLifecycle.Interrupted error }
    | EmbeddedTerminalLifecycle.Failed _
    | EmbeddedTerminalLifecycle.Interrupted _ ->
        tab

let private interruptSnapshot error snapshot =
    { Tabs = snapshot.Tabs |> List.map (interrupted error) }

let private tabForRecord terminal =
    { Worktree = PathUtils.toWorktreePath terminal.WorktreePath
      Lifecycle =
        EmbeddedTerminalLifecycle.Running terminal.AttachmentEndpoint }

let private reconcileSnapshot previousHost currentHost records snapshot =
    let recordsByPath =
        records
        |> List.map (fun terminal ->
            pathKey terminal.WorktreePath, terminal)
        |> Map.ofList

    let hostChanged =
        previousHost
        |> Option.exists (fun previous ->
            hostIdentityMatches previous currentHost |> not)

    let missingReason =
        if hostChanged then
            "TerminalHost changed before this terminal could be verified. Restart the terminal to continue."
        else
            "The terminal is no longer present in the authoritative TerminalHost registry."

    let retained =
        snapshot.Tabs
        |> List.map (fun tab ->
            let key = tab.Worktree |> WorktreePath.value |> pathKey

            match Map.tryFind key recordsByPath with
            | Some terminal -> tabForRecord terminal
            | None -> interrupted missingReason tab)

    let retainedPaths =
        retained
        |> List.map (_.Worktree >> WorktreePath.value >> pathKey)
        |> Set.ofList

    let appended =
        records
        |> List.filter (fun terminal ->
            retainedPaths
            |> Set.contains (pathKey terminal.WorktreePath)
            |> not)
        |> List.map tabForRecord

    { Tabs = retained @ appended }

let private applyRegistry
    (state: ManagerState)
    (connection: HostConnection)
    (registry: RegistrySnapshot)
    =
    { LastSnapshot =
        reconcileSnapshot
            state.LastHost
            connection.Manifest
            registry.Terminals
            state.LastSnapshot
      LastHost = Some connection.Manifest }

let private applyRegistryAfterClose
    path
    (state: ManagerState)
    (connection: HostConnection)
    (registry: RegistrySnapshot)
    =
    let withoutClosed =
        withoutPath path state.LastSnapshot

    { LastSnapshot =
        reconcileSnapshot
            state.LastHost
            connection.Manifest
            registry.Terminals
            withoutClosed
      LastHost = Some connection.Manifest }

let private withHostFailure error state =
    { state with
        LastSnapshot = interruptSnapshot error state.LastSnapshot }

let private getTerminals config state =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            match! listTerminals config connection with
            | Ok registry ->
                return applyRegistry state connection registry
            | Error error ->
                return withHostFailure error state
        | MissingHost ->
            return
                withHostFailure
                    "TerminalHost discovery is missing; running terminals can no longer be verified."
                    state
        | DeadHost error ->
            return
                withHostFailure
                    $"{error}. Its terminals were interrupted."
                    state
        | UnusableHost error ->
            return withHostFailure error state
    }

let private startTerminalOnHost
    (config: Config)
    (connection: HostConnection)
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
                connection
                HttpMethod.Post
                "/api/v1/terminals"
                (Some body)

        match! listTerminals config connection with
        | Error listError ->
            return
                Error(
                    match startResult with
                    | Error startError ->
                        $"{startError}; authoritative relist failed: {listError}"
                    | Ok _ ->
                        $"TerminalHost accepted the start request but its authoritative registry could not be read: {listError}"
                )
        | Ok registry ->
            match terminalForPath path registry.Terminals with
            | Some terminal -> return Ok(registry, terminal)
            | None ->
                return
                    Error(
                        match startResult with
                        | Error startError -> startError
                        | Ok _ ->
                            "TerminalHost did not include the requested terminal in its authoritative registry"
                    )
    }

let private startTerminal config state worktreePath =
    async {
        match! ensureHost config state.LastHost with
        | Error error ->
            return withHostFailure error state, Error error
        | Ok connection ->
            let path = WorktreePath.value worktreePath

            match! startTerminalOnHost config connection path with
            | Error error ->
                return withHostFailure error state, Error error
            | Ok(registry, _) ->
                let next = applyRegistry state connection registry
                return next, Ok next.LastSnapshot
    }

let private safeWithoutHealthyHost config state discovery =
    match discovery with
    | DeadHost _ -> Ok()
    | MissingHost ->
        match knownHostIsStillLive config state.LastHost with
        | Ok false -> Ok()
        | Ok true ->
            Error
                "The TerminalHost manifest is missing while the exact recorded host is still running"
        | Error error -> Error error
    | UnusableHost error -> Error error
    | HealthyHost _ -> failwith "unreachable"

let private closeOnHost config state connection worktreePath =
    async {
        let path = WorktreePath.value worktreePath

        match! listTerminals config connection with
        | Error error ->
            return
                withHostFailure error state,
                Error error
        | Ok before ->
            let listed = applyRegistry state connection before

            match terminalForPath path before.Terminals with
            | None ->
                let next =
                    applyRegistryAfterClose
                        path
                        listed
                        connection
                        before

                return next, Ok next.LastSnapshot
            | Some terminal ->
                let closePath =
                    $"/api/v1/terminals/{terminal.SessionId}"

                let! closeResult =
                    request
                        config
                        connection
                        HttpMethod.Delete
                        closePath
                        None

                match! listTerminals config connection with
                | Error listError ->
                    let error =
                        match closeResult with
                        | Error closeError ->
                            $"{closeError}; authoritative relist failed: {listError}"
                        | Ok _ ->
                            $"TerminalHost accepted the close request but its authoritative registry could not be read: {listError}"

                    return
                        withHostFailure error listed,
                        Error error
                | Ok after ->
                    match terminalForPath path after.Terminals with
                    | None ->
                        let next =
                            applyRegistryAfterClose
                                path
                                listed
                                connection
                                after

                        return next, Ok next.LastSnapshot
                    | Some _ ->
                        let next =
                            applyRegistry listed connection after

                        let error =
                            match closeResult with
                            | Error closeError -> closeError
                            | Ok _ ->
                                "TerminalHost still lists the terminal after its close request"

                        return next, Error error
    }

let private closeTerminal config state worktreePath =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            return!
                closeOnHost
                    config
                    state
                    connection
                    worktreePath
        | discovery ->
            match safeWithoutHealthyHost config state discovery with
            | Error error ->
                return withHostFailure error state, Error error
            | Ok() ->
                let reason =
                    match discovery with
                    | DeadHost error -> $"{error}. Its terminals were interrupted."
                    | MissingHost ->
                        "TerminalHost is not running; no live terminal remains to close."
                    | UnusableHost _
                    | HealthyHost _ -> failwith "unreachable"

                let next =
                    { state with
                        LastSnapshot =
                            state.LastSnapshot
                            |> interruptSnapshot reason
                            |> withoutPath (WorktreePath.value worktreePath) }

                return next, Ok next.LastSnapshot
    }

let private shutdown config state =
    async {
        match! discoverHost config with
        | MissingHost
        | DeadHost _ ->
            return state, Ok()
        | UnusableHost error ->
            return withHostFailure error state, Error error
        | HealthyHost connection ->
            match!
                request
                    config
                    connection
                    HttpMethod.Post
                    "/api/v1/shutdown"
                    None
            with
            | Error error ->
                match processIdentityMatches config connection.Manifest with
                | Ok false ->
                    let next =
                        withHostFailure
                            "TerminalHost stopped; its terminals were interrupted."
                            state

                    return next, Ok()
                | Ok true
                | Error _ ->
                    return withHostFailure error state, Error error
            | Ok _ ->
                let next =
                    withHostFailure
                        "TerminalHost was shut down; its terminals were interrupted."
                        state

                return next, Ok()
    }

let private hostExecutableName =
    if OperatingSystem.IsWindows() then
        "TerminalHost.exe"
    else
        "TerminalHost"

let private stagedExecutablePath config version =
    try
        let stagingRoot =
            Path.Combine(config.HostStateDirectory, "staged")
            |> Path.GetFullPath
            |> Path.TrimEndingDirectorySeparator

        let directory =
            Path.Combine(stagingRoot, version)
            |> Path.GetFullPath
            |> Path.TrimEndingDirectorySeparator
            |> DirectoryInfo

        let hasExactParent =
            directory.Parent
            |> Option.ofObj
            |> Option.exists (fun parent ->
                samePath
                    (parent.FullName
                     |> Path.GetFullPath
                     |> Path.TrimEndingDirectorySeparator)
                    stagingRoot)

        let executable = Path.Combine(directory.FullName, hostExecutableName)
        let executableInfo = FileInfo executable

        if
            not (validStagedVersion version)
            || directory.Name <> version
            || not hasExactParent
        then
            Error "The staged TerminalHost version is not a direct version directory"
        elif
            not directory.Exists
            || (directory.Attributes &&& FileAttributes.ReparsePoint) <> enum 0
        then
            Error "The staged TerminalHost version directory is missing or unsafe"
        elif
            not executableInfo.Exists
            || (executableInfo.Attributes &&& FileAttributes.ReparsePoint) <> enum 0
        then
            Error
                $"The staged TerminalHost executable was not found at '{executableInfo.FullName}'"
        else
            Ok executableInfo.FullName
    with error ->
        Error $"Could not validate the staged TerminalHost executable: {error.Message}"

let private hasNonIdleOwnedSession
    (snapshot: ReplacementActivitySnapshot)
    =
    snapshot.OpenSessions
    |> List.exists (fun session ->
        match session.Status with
        | SessionActivity.SessionLevelStatus.Working
        | SessionActivity.SessionLevelStatus.WaitingForUser -> true
        | SessionActivity.SessionLevelStatus.Idle -> false)

let private terminalSessionIds (terminals: TerminalRecord list) =
    terminals
    |> List.map (_.SessionId >> SessionActivity.TerminalSessionId)
    |> Set.ofList

let private queryReplacementActivity
    (query: ReplacementActivityQuery)
    (terminals: TerminalRecord list)
    : Result<ReplacementActivitySnapshot, string> =
    try
        query DateTimeOffset.UtcNow (terminalSessionIds terminals)
    with error ->
        Error $"Could not query terminal-owned Copilot activity: {error.Message}"

let private waitForHostExit config manifest =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec wait () =
        async {
            match processIdentityMatches config manifest with
            | Error error -> return Error error
            | Ok false -> return Ok()
            | Ok true when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"TerminalHost PID {manifest.Pid} did not exit within {config.StartupTimeout.TotalSeconds:g} seconds"
            | Ok true ->
                do! Async.Sleep(probeDelayMilliseconds config)
                return! wait ()
        }

    wait ()

let private shutdownAndWait config connection =
    async {
        let! shutdownResult =
            request
                config
                connection
                HttpMethod.Post
                "/api/v1/shutdown"
                None

        match! waitForHostExit config connection.Manifest with
        | Ok() -> return Ok()
        | Error waitError ->
            return
                Error(
                    match shutdownResult with
                    | Ok _ -> waitError
                    | Error requestError ->
                        $"{requestError}; exact host shutdown could not be confirmed: {waitError}"
                )
    }

let private replacementTtydPath
    (config: Config)
    (oldExecutablePath: string)
    =
    match config.TtydExecutablePath with
    | Some path -> Some path
    | None ->
        oldExecutablePath
        |> Path.GetDirectoryName
        |> Option.ofObj
        |> Option.map (fun directory -> Path.Combine(directory, "ttyd.exe"))
        |> Option.filter File.Exists

let private configForExecutable config ttydExecutablePath executablePath =
    { config with
        HostExecutablePath = executablePath
        TtydExecutablePath = ttydExecutablePath }

let private launchHostAt config =
    async {
        match startHostProcess config with
        | Error error -> return LaunchRejected error
        | Ok() ->
            match! waitForHealthyHost config with
            | Ok connection -> return HostLaunched connection
            | Error error -> return LaunchStartedButUnhealthy error
    }

let private recreateTerminals
    (config: Config)
    (connection: HostConnection)
    (terminals: TerminalRecord list)
    (resumableSessionIds:
        Map<SessionActivity.TerminalSessionId, SessionActivity.SessionId>)
    =
    let rec recreate registry remaining =
        asyncResult {
            match remaining with
            | [] -> return registry
            | previous :: tail ->
                let! nextRegistry, recreated =
                    startTerminalOnHost config connection previous.WorktreePath
                    |> AsyncResult.mapError (fun error ->
                        $"Could not recreate the terminal for '{previous.WorktreePath}': {error}")

                match
                    resumableSessionIds
                    |> Map.tryFind (
                        SessionActivity.TerminalSessionId previous.SessionId
                    )
                with
                | None -> ()
                | Some(SessionActivity.SessionId sessionId) ->
                    let provider =
                        CodingToolStatus.readConfiguredProvider previous.WorktreePath

                    let command =
                        CodingToolCli.build provider (CodingToolCli.Resume(Some sessionId))

                    do!
                        config.SendTerminalCommand
                            recreated.AttachmentEndpoint
                            command.AsShellString
                        |> AsyncResult.mapError (fun error ->
                            $"Could not resume the terminal-owned Copilot session for '{previous.WorktreePath}': {error}")

                return! recreate nextRegistry tail
        }

    asyncResult {
        let! initial = listTerminals config connection

        if not (List.isEmpty initial.Terminals) then
            return!
                Error
                    "The replacement TerminalHost did not start with an empty terminal registry"

        return! recreate initial terminals
    }

let private replacementFailure
    stageVersion
    error
    (state: ManagerState)
    =
    let message = $"TerminalHost replacement failed: {error}"

    withHostFailure message state,
    ReplacementOutcome.Failed(stageVersion, error)

let private recoverOldHost
    (config: Config)
    (state: ManagerState)
    (plan: ReplacementPlan)
    resumableSessionIds
    failure
    =
    async {
        let oldConfig =
            configForExecutable
                config
                (replacementTtydPath config plan.OldExecutablePath)
                plan.OldExecutablePath

        let failed detail =
            replacementFailure
                plan.StagedVersion
                $"{failure}. {detail}"
                state

        match! launchHostAt oldConfig with
        | LaunchRejected recoveryError
        | LaunchStartedButUnhealthy recoveryError ->
            return failed $"The previous host could not be restarted: {recoveryError}"
        | HostLaunched connection ->
            let recover =
                asyncResult {
                    let! executablePath =
                        resolveProcessExecutable config connection.Manifest
                        |> Result.mapError (fun error ->
                            $"The restarted previous host could not be verified: {error}")

                    if not (samePath executablePath plan.OldExecutablePath) then
                        return!
                            Error
                                "Recovery started an unexpected TerminalHost executable."

                    let! registry =
                        recreateTerminals
                            oldConfig
                            connection
                            plan.Terminals
                            resumableSessionIds
                        |> AsyncResult.mapError (fun error ->
                            $"The previous host restarted, but its terminals could not be recovered: {error}")

                    return applyRegistry state connection registry
                }

            match! recover with
            | Error recoveryError -> return failed recoveryError
            | Ok recovered ->
                return
                    recovered,
                    ReplacementOutcome.Failed(
                        plan.StagedVersion,
                        $"{failure}. The previous host and its terminals were recovered."
                    )
    }

let private recheckReplacement
    (config: Config)
    (plan: ReplacementPlan)
    (query: ReplacementActivityQuery)
    =
    async {
        match! discoverHost config with
        | HealthyHost connection
            when hostIdentityMatches connection.Manifest plan.OldHost
                 && connection.Manifest.StagedExecutableVersion = Some plan.StagedVersion ->
            match! listTerminals config connection with
            | Error error ->
                return
                    RecheckFailed
                        $"Could not recheck the authoritative terminal registry: {error}"
            | Ok registry
                when registry.Revision <> plan.RegistryRevision
                     || registry.Terminals <> plan.Terminals ->
                return RecheckChanged
            | Ok registry ->
                match queryReplacementActivity query registry.Terminals with
                | Error error -> return RecheckFailed error
                | Ok activity
                    when activity.ActivityEpoch <> plan.ActivityEpoch
                         || hasNonIdleOwnedSession activity ->
                    return RecheckChanged
                | Ok activity ->
                    match resolveProcessExecutable config connection.Manifest with
                    | Error error -> return RecheckFailed error
                    | Ok executablePath
                        when samePath executablePath plan.OldExecutablePath ->
                        return ReadyToCommit(connection, activity)
                    | Ok _ -> return RecheckChanged
        | HealthyHost _
        | MissingHost
        | DeadHost _ ->
            return RecheckChanged
        | UnusableHost error ->
            return RecheckFailed $"Could not recheck the exact TerminalHost: {error}"
    }

let private commitReplacement
    (config: Config)
    (state: ManagerState)
    (plan: ReplacementPlan)
    (query: ReplacementActivityQuery)
    =
    async {
        let failed error =
            state,
            ReplacementOutcome.Failed(plan.StagedVersion, error)

        try
            match! recheckReplacement config plan query with
            | RecheckChanged -> return state, ReplacementOutcome.RaceLost
            | RecheckFailed error -> return failed error
            | ReadyToCommit(connection, activity) ->
                match! shutdownAndWait config connection with
                | Error error ->
                    return
                        failed
                            $"The previous TerminalHost was retained because shutdown did not complete: {error}"
                | Ok() ->
                    let stagedConfig =
                        configForExecutable
                            config
                            (replacementTtydPath config plan.OldExecutablePath)
                            plan.StagedExecutablePath

                    match! launchHostAt stagedConfig with
                    | LaunchRejected error ->
                        return!
                            recoverOldHost
                                config
                                state
                                plan
                                activity.ResumableSessionIds
                                $"The staged host could not be launched: {error}"
                    | LaunchStartedButUnhealthy error ->
                        return
                            replacementFailure
                                plan.StagedVersion
                                $"The staged host process started but did not become healthy; the previous host was not restarted because the staged process could not be proven stopped: {error}"
                                state
                    | HostLaunched replacement ->
                        let activate =
                            asyncResult {
                                let! executablePath =
                                    resolveProcessExecutable config replacement.Manifest
                                    |> Result.mapError (fun error ->
                                        $"The staged host identity could not be verified: {error}")

                                if not (samePath executablePath plan.StagedExecutablePath) then
                                    return!
                                        Error
                                            "The staged launch published an unexpected TerminalHost executable"

                                let! registry =
                                    recreateTerminals
                                        stagedConfig
                                        replacement
                                        plan.Terminals
                                        activity.ResumableSessionIds

                                return applyRegistry state replacement registry
                            }

                        match! activate with
                        | Error error ->
                            return
                                replacementFailure
                                    plan.StagedVersion
                                    error
                                    state
                        | Ok next ->
                            return
                                next,
                                ReplacementOutcome.Replaced plan.StagedVersion
        with error ->
            return
                replacementFailure
                    plan.StagedVersion
                    $"Unexpected replacement error: {error.Message}"
                    state
    }

let internal createWithConfig config =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop state =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Get reply ->
                        let! next = getTerminals config state
                        reply.Reply next.LastSnapshot
                        return! loop next
                    | Start(worktreePath, reply) ->
                        let! next, result =
                            startTerminal
                                config
                                state
                                worktreePath

                        reply.Reply result
                        return! loop next
                    | Close(worktreePath, reply) ->
                        let! next, result =
                            closeTerminal
                                config
                                state
                                worktreePath

                        reply.Reply(
                            result
                            |> Result.defaultValue next.LastSnapshot
                        )

                        return! loop next
                    | CloseStrict(worktreePath, reply) ->
                        let! next, result =
                            closeTerminal
                                config
                                state
                                worktreePath

                        reply.Reply result
                        return! loop next
                    | TryCommitReplacement(plan, query, reply) ->
                        let! next, outcome =
                            commitReplacement
                                config
                                state
                                plan
                                query

                        reply.Reply outcome
                        return! loop next
                    | ShutdownHost reply ->
                        let! next, result = shutdown config state
                        reply.Reply result
                        return! loop next
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty
                  LastHost = None })

    Manager(config, agent)

let create serverOrigin =
    defaultConfig (originsFor serverOrigin)
    |> createWithConfig

let private tryReplaceHostIgnoring
    ignoredStagedVersion
    beforeRecheck
    query
    (Manager(config, agent))
    =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            match connection.Manifest.StagedExecutableVersion with
            | None -> return ReplacementOutcome.NoCandidate
            | Some stagedVersion when ignoredStagedVersion = Some stagedVersion ->
                return ReplacementOutcome.NoCandidate
            | Some stagedVersion ->
                let candidate =
                    result {
                        let! stagedExecutable =
                            stagedExecutablePath config stagedVersion

                        let! oldExecutable =
                            resolveProcessExecutable config connection.Manifest

                        return stagedExecutable, oldExecutable
                    }

                match candidate with
                | Error error -> return ReplacementOutcome.Failed(stagedVersion, error)
                | Ok(stagedExecutable, oldExecutable)
                    when samePath oldExecutable stagedExecutable ->
                    return ReplacementOutcome.NoCandidate
                | Ok(stagedExecutable, oldExecutable) ->
                    match! listTerminals config connection with
                    | Error error ->
                        return
                            ReplacementOutcome.Failed(
                                stagedVersion,
                                $"Could not capture the authoritative terminal registry: {error}"
                            )
                    | Ok registry ->
                        match queryReplacementActivity query registry.Terminals with
                        | Error error ->
                            return ReplacementOutcome.Failed(stagedVersion, error)
                        | Ok activity when hasNonIdleOwnedSession activity ->
                            return ReplacementOutcome.WaitingForIdle
                        | Ok activity ->
                            let plan: ReplacementPlan =
                                { OldHost = connection.Manifest
                                  OldExecutablePath = oldExecutable
                                  StagedVersion = stagedVersion
                                  StagedExecutablePath = stagedExecutable
                                  RegistryRevision = registry.Revision
                                  Terminals = registry.Terminals
                                  ActivityEpoch = activity.ActivityEpoch }

                            try
                                do! beforeRecheck ()

                                return!
                                    agent.PostAndAsyncReply(
                                        (fun reply ->
                                            TryCommitReplacement(plan, query, reply)),
                                        timeout = 300_000
                                    )
                            with error ->
                                return
                                    ReplacementOutcome.Failed(
                                        stagedVersion,
                                        $"Could not coordinate TerminalHost replacement: {error.Message}"
                                    )
        | MissingHost
        | DeadHost _
        | UnusableHost _ ->
            return ReplacementOutcome.NoCandidate
    }

let internal tryReplaceHostWith beforeRecheck query manager =
    tryReplaceHostIgnoring
        None
        beforeRecheck
        query
        manager

let internal tryReplaceHost query manager =
    tryReplaceHostWith
        (fun () -> async.Return())
        query
        manager

let internal runReplacementCoordinator
    manager
    query
    (cancellationToken: CancellationToken)
    =
    let rec loop ignoredStagedVersion =
        async {
            if cancellationToken.IsCancellationRequested then
                return ()
            else
                let! outcome =
                    tryReplaceHostIgnoring
                        ignoredStagedVersion
                        (fun () -> async.Return())
                        query
                        manager

                let nextIgnored =
                    match outcome with
                    | ReplacementOutcome.Replaced stagedVersion ->
                        Log.log
                            "TerminalHost"
                            $"Replaced the host with staged version {stagedVersion} at a natural Copilot-idle window"

                        None
                    | ReplacementOutcome.Failed(stagedVersion, error) ->
                        Log.log
                            "TerminalHost"
                            $"Replacement of staged version {stagedVersion} failed: {error}"

                        Some stagedVersion
                    | ReplacementOutcome.NoCandidate
                    | ReplacementOutcome.WaitingForIdle
                    | ReplacementOutcome.RaceLost ->
                        ignoredStagedVersion

                try
                    do!
                        Task.Delay(
                            TimeSpan.FromSeconds 1.0,
                            cancellationToken
                        )
                        |> Async.AwaitTask

                    return! loop nextIgnored
                with :? OperationCanceledException ->
                    return ()
        }

    loop None

let start (Manager(_, agent)) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Start(worktreePath, reply)),
        timeout = 60_000
    )

let get (Manager(_, agent)) =
    agent.PostAndAsyncReply(Get, timeout = 60_000)

let close (Manager(_, agent)) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Close(worktreePath, reply)),
        timeout = 60_000
    )

let internal closeStrict (Manager(_, agent)) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> CloseStrict(worktreePath, reply)),
        timeout = 60_000
    )

let internal shutdownHost (Manager(_, agent)) =
    agent.PostAndAsyncReply(ShutdownHost, timeout = 60_000)
