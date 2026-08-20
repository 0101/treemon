module Server.EmbeddedTerminal

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
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
      LaunchHost: ProcessStartInfo -> Result<unit, string> }

type private HostDiscovery =
    | MissingHost
    | HealthyHost of HostConnection
    | DeadHost of reason: string
    | UnusableHost of reason: string

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      LastHost: DiscoveryManifest option }

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

type Manager = private Manager of MailboxProcessor<Message>

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

let private processIdentityMatches (manifest: DiscoveryManifest) =
    try
        use child = Process.GetProcessById manifest.Pid

        if child.HasExited then
            Ok false
        else
            let startTicks =
                child.StartTime.ToUniversalTime().Ticks

            Ok(startTicks = manifest.ProcessStartTimeUtcTicks)
    with
    | :? ArgumentException -> Ok false
    | :? InvalidOperationException -> Ok false
    | error ->
        Error $"Could not verify TerminalHost process identity: {error.Message}"

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
            match processIdentityMatches manifest with
            | Error error -> return UnusableHost error
            | Ok false ->
                return
                    DeadHost
                        $"Recorded TerminalHost PID {manifest.Pid} is no longer the exact live process"
            | Ok true ->
                match! probe config manifest with
                | Ok connection -> return HealthyHost connection
                | Error probeError ->
                    match processIdentityMatches manifest with
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

    { HostExecutablePath = defaultHostExecutable ()
      HostStateDirectory = stateDirectory
      TtydExecutablePath = None
      ShellCommand = "pwsh"
      AllowedOrigins = allowedOrigins
      StartupTimeout = TimeSpan.FromSeconds 30.0
      ControlRequestTimeout = TimeSpan.FromSeconds 10.0
      ProbeInterval = TimeSpan.FromMilliseconds 100.0
      LaunchHost = launchDetached }

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

let private knownHostIsStillLive lastHost =
    match lastHost with
    | None -> Ok false
    | Some host -> processIdentityMatches host

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
            match knownHostIsStillLive lastHost with
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

let private applyRegistry state connection registry =
    { LastSnapshot =
        reconcileSnapshot
            state.LastHost
            connection.Manifest
            registry.Terminals
            state.LastSnapshot
      LastHost = Some connection.Manifest }

let private applyRegistryAfterClose path state connection registry =
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

let private startTerminal config state worktreePath =
    async {
        match! ensureHost config state.LastHost with
        | Error error ->
            return withHostFailure error state, Error error
        | Ok connection ->
            let body =
                JsonSerializer.Serialize(
                    {| worktreePath =
                        WorktreePath.value worktreePath |}
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
                let error =
                    match startResult with
                    | Error startError ->
                        $"{startError}; authoritative relist failed: {listError}"
                    | Ok _ ->
                        $"TerminalHost accepted the start request but its authoritative registry could not be read: {listError}"

                return withHostFailure error state, Error error
            | Ok registry ->
                let next = applyRegistry state connection registry
                let path = WorktreePath.value worktreePath

                match terminalForPath path registry.Terminals with
                | Some _ -> return next, Ok next.LastSnapshot
                | None ->
                    let error =
                        match startResult with
                        | Error startError -> startError
                        | Ok _ ->
                            "TerminalHost did not include the requested terminal in its authoritative registry"

                    return next, Error error
    }

let private safeWithoutHealthyHost state discovery =
    match discovery with
    | DeadHost _ -> Ok()
    | MissingHost ->
        match knownHostIsStillLive state.LastHost with
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
            match safeWithoutHealthyHost state discovery with
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
                match processIdentityMatches connection.Manifest with
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
                    | ShutdownHost reply ->
                        let! next, result = shutdown config state
                        reply.Reply result
                        return! loop next
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty
                  LastHost = None })

    Manager agent

let create serverOrigin =
    defaultConfig (originsFor serverOrigin)
    |> createWithConfig

let start (Manager agent) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Start(worktreePath, reply)),
        timeout = 60_000
    )

let get (Manager agent) =
    agent.PostAndAsyncReply(Get, timeout = 60_000)

let close (Manager agent) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Close(worktreePath, reply)),
        timeout = 60_000
    )

let internal closeStrict (Manager agent) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> CloseStrict(worktreePath, reply)),
        timeout = 60_000
    )

let internal shutdownHost (Manager agent) =
    agent.PostAndAsyncReply(ShutdownHost, timeout = 60_000)
