module Server.EmbeddedTerminal

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Shared

type internal Config =
    { NodeExecutable: string
      HostScriptPath: string
      HostStateDirectory: string
      TtydExecutablePath: string
      ShellCommand: string
      StartupTimeout: TimeSpan
      ControlRequestTimeout: TimeSpan
      ProbeInterval: TimeSpan }

type private HostIdentity =
    { Generation: string
      Pid: int
      ProcessStartTicks: int64 }

type private HostConnection =
    { Version: int
      Generation: string
      Pid: int
      ProcessStartTicks: int64
      ProcessStartExact: bool
      ControlPort: int
      ControlToken: string
      StartedAt: string }

type private HostSession =
    { Id: string
      Tab: EmbeddedTerminalTab }

type private Reservation =
    { Id: string
      Sessions: HostSession list }

type private CleanupReservation =
    { Renew:
        CancellationToken ->
            Async<Result<unit, string>>
      Release: unit -> Async<Result<unit, string>> }

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      AnnouncedHost: HostIdentity option
      KnownHost: HostIdentity option }

type private Message =
    | Start of WorktreePath * AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | Close of WorktreePath * AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | CloseStrict of
        WorktreePath *
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | ReserveCleanup of
        WorktreePath *
        AsyncReplyChannel<Result<CleanupReservation, string>>
    | ShutdownHost of AsyncReplyChannel<Result<unit, string>>

type Manager = private Manager of MailboxProcessor<Message>

let private hostProtocolVersion = 2

let private httpClient =
    new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

let private defaultConfig () =
    let root = Directory.GetCurrentDirectory()
    let stateDirectory =
        Environment.GetEnvironmentVariable("TREEMON_TERMINAL_STATE_DIR")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue (Path.Combine(root, ".agents", "durable-terminal"))

    { NodeExecutable = "node"
      HostScriptPath = Path.Combine(root, "scripts", "durable-terminal-host.mjs")
      HostStateDirectory = stateDirectory
      TtydExecutablePath =
        Path.Combine(root, ".tools", "ttyd", "1.7.7", "ttyd.exe")
      ShellCommand = "pwsh"
      StartupTimeout = TimeSpan.FromSeconds 30.0
      ControlRequestTimeout = TimeSpan.FromSeconds 30.0
      ProbeInterval = TimeSpan.FromMilliseconds 100.0 }

let private canonicalWorktreePath =
    WorktreePath.value >> PathUtils.toWorktreePath

let private isPath path (tab: EmbeddedTerminalTab) =
    Shared.PathUtils.pathEquals
        (WorktreePath.value path)
        (WorktreePath.value tab.Worktree)

let private isInterrupted tab =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Interrupted _ -> true
    | _ -> false

let private withoutPath path snapshot =
    { Tabs = snapshot.Tabs |> List.filter (isPath path >> not) }

let private mergeSnapshot previous current =
    let replacementFor tab =
        current.Tabs |> List.tryFind (isPath tab.Worktree)

    let retained =
        previous.Tabs
        |> List.choose (fun tab ->
            match replacementFor tab, tab.Lifecycle with
            | Some replacement, _ -> Some replacement
            | None, EmbeddedTerminalLifecycle.Interrupted _ -> Some tab
            | None, _ -> None)

    let appended =
        current.Tabs
        |> List.filter (fun tab ->
            retained
            |> List.exists (isPath tab.Worktree)
            |> not)

    { Tabs =
        retained @ appended
        |> List.fold (fun tabs tab ->
            if tabs |> List.exists (isPath tab.Worktree) then
                tabs
            else
                tabs @ [ tab ]) [] }

let private withFailure path error snapshot =
    match snapshot.Tabs |> List.tryFind (isPath path) with
    | Some _ ->
        { Tabs =
            snapshot.Tabs
            |> List.map (fun tab ->
                if isPath path tab then
                    { tab with
                        Lifecycle = EmbeddedTerminalLifecycle.Failed error }
                else
                    tab) }
    | None ->
        { Tabs =
            snapshot.Tabs
            @ [ { Worktree = path
                  Lifecycle = EmbeddedTerminalLifecycle.Failed error } ] }

let private withHostFailure error snapshot =
    { Tabs =
        snapshot.Tabs
        |> List.map (fun tab ->
            { tab with
                Lifecycle =
                    EmbeddedTerminalLifecycle.Interrupted(
                        $"Durable terminal host unavailable: {error}"
                    ) }) }

let private tryProperty (name: string) (element: JsonElement) =
    element.EnumerateObject()
    |> Seq.tryFind _.NameEquals(name)
    |> Option.map _.Value

let private requiredString name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.String ->
        value.GetString()
        |> Option.ofObj
        |> Result.requireSome $"Missing '{name}'"
    | _ -> Error $"Missing '{name}'"

let private optionalString name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.String ->
        value.GetString() |> Option.ofObj
    | _ -> None

let private requiredInt name element =
    match tryProperty name element with
    | Some value ->
        match value.TryGetInt32() with
        | true, result -> Ok result
        | false, _ -> Error $"Invalid '{name}'"
    | None -> Error $"Missing '{name}'"

let private requiredBool name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.True -> Ok true
    | Some value when value.ValueKind = JsonValueKind.False -> Ok false
    | _ -> Error $"Missing or invalid '{name}'"

let private requiredInt64String name element =
    result {
        let! text = requiredString name element

        match Int64.TryParse text with
        | true, value when value > 0L -> return value
        | _ -> return! Error $"Invalid '{name}'"
    }

let private parseHostConnection (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! version = requiredInt "version" root

            if version <> 1 && version <> hostProtocolVersion then
                return! Error $"Unsupported durable terminal host protocol version {version}"

            let! pid = requiredInt "pid" root
            let! controlPort = requiredInt "controlPort" root
            let! controlToken = requiredString "controlToken" root
            let! startedAt = requiredString "startedAt" root
            let! generation, processStartTicks, processStartExact =
                if version = hostProtocolVersion then
                    result {
                        let! generation = requiredString "generation" root
                        let! processStartTicks =
                            requiredInt64String "processStartTicks" root
                        let! processStartExact =
                            requiredBool "processStartExact" root

                        return
                            generation,
                            processStartTicks,
                            processStartExact
                    }
                else
                    match DateTimeOffset.TryParse startedAt with
                    | true, started when started.UtcTicks > 0L ->
                        Ok(
                            $"legacy-v1-{pid}-{started.UtcTicks}",
                            started.UtcTicks,
                            false
                        )
                    | _ ->
                        Error
                            "Invalid protocol-1 durable terminal host start identity"

            if pid <= 0 then
                return! Error "Invalid durable terminal host PID"

            if String.IsNullOrWhiteSpace generation then
                return! Error "Invalid durable terminal host generation"

            if controlPort <= 0 || controlPort > 65535 || controlPort = 5000 then
                return! Error "Invalid durable terminal host control port"

            if String.IsNullOrWhiteSpace controlToken then
                return! Error "Invalid durable terminal host control token"

            return
                { Version = version
                  Generation = generation
                  Pid = pid
                  ProcessStartTicks = processStartTicks
                  ProcessStartExact = processStartExact
                  ControlPort = controlPort
                  ControlToken = controlToken
                  StartedAt = startedAt }
        }
    with
    | :? JsonException as ex ->
        Error $"Invalid durable terminal host state: {ex.Message}"
    | ex ->
        Error $"Could not read durable terminal host state: {ex.Message}"

let private lifecycleFor element =
    result {
        let! lifecycle = requiredString "lifecycle" element

        match lifecycle with
        | "starting" -> return EmbeddedTerminalLifecycle.Starting
        | "running" ->
            let! endpoint = requiredString "endpoint" element
            return EmbeddedTerminalLifecycle.Running endpoint
        | "failed" ->
            return
                EmbeddedTerminalLifecycle.Failed(
                    optionalString "error" element
                    |> Option.defaultValue "Durable terminal session failed"
                )
        | "closing" ->
            return
                EmbeddedTerminalLifecycle.Failed(
                    "Durable terminal session is closing"
                )
        | unsupported ->
            return
                EmbeddedTerminalLifecycle.Failed(
                    $"Durable terminal host returned unsupported lifecycle '{unsupported}'"
                )
    }

let private parseHostSessions (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        match tryProperty "sessions" root with
        | Some sessions when sessions.ValueKind = JsonValueKind.Array ->
            sessions.EnumerateArray()
            |> Seq.map (fun session ->
                result {
                    let! id = requiredString "id" session
                    let! path = requiredString "worktreePath" session
                    let! lifecycle = lifecycleFor session

                    return
                        { Id = id
                          Tab =
                            { Worktree = PathUtils.toWorktreePath path
                              Lifecycle = lifecycle } }
                })
            |> Seq.toList
            |> List.sequenceResultM
        | _ -> Error "Durable terminal host response omitted 'sessions'"
    with
    | :? JsonException as ex ->
        Error $"Invalid durable terminal host response: {ex.Message}"
    | ex ->
        Error $"Could not read durable terminal host response: {ex.Message}"

let private parseReservation (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! reservation =
                match tryProperty "reservation" root with
                | Some value
                    when value.ValueKind
                         = JsonValueKind.Object ->
                    Ok value
                | _ ->
                    Error
                        "Durable terminal host response omitted 'reservation'"

            let! id = requiredString "id" reservation
            let! sessions = parseHostSessions text
            return { Id = id; Sessions = sessions }
        }
    with
    | :? JsonException as ex ->
        Error
            $"Invalid durable terminal reservation response: {ex.Message}"
    | ex ->
        Error
            $"Could not read durable terminal reservation response: {ex.Message}"

let private snapshot sessions =
    { Tabs = sessions |> List.map _.Tab }

let private statePath config =
    Path.Combine(config.HostStateDirectory, "host.json")

let private readHostConnection config =
    let path = statePath config

    if not (File.Exists path) then
        Ok None
    else
        try
            File.ReadAllText path
            |> parseHostConnection
            |> Result.map Some
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException ->
            Ok None
        | ex ->
            Error $"Could not read durable terminal host state: {ex.Message}"

let private hostIdentity (connection: HostConnection) =
    { Generation = connection.Generation
      Pid = connection.Pid
      ProcessStartTicks = connection.ProcessStartTicks }

let private sameHostIdentity (left: HostIdentity) (right: HostIdentity) =
    left.Generation = right.Generation
    && left.Pid = right.Pid
    && left.ProcessStartTicks = right.ProcessStartTicks

let private pidIsAlive pid =
    try
        use proc = Process.GetProcessById pid
        not proc.HasExited
    with
    | :? ArgumentException -> false
    | :? InvalidOperationException -> false

let private currentProcessStartTicks () =
    use proc = Process.GetCurrentProcess()
    proc.StartTime.ToUniversalTime().Ticks

let private processIdentityMatches connection =
    try
        use proc = Process.GetProcessById connection.Pid

        if proc.HasExited then
            Ok false
        else
            let actual = proc.StartTime.ToUniversalTime().Ticks
            let difference =
                abs (actual - connection.ProcessStartTicks)

            if actual = connection.ProcessStartTicks then
                Ok true
            elif connection.ProcessStartExact then
                Ok false
            elif difference <= TimeSpan.FromSeconds(2.0).Ticks then
                Ok true
            else
                Ok false
    with
    | :? ArgumentException -> Ok false
    | :? InvalidOperationException -> Ok false
    | ex ->
        Error
            $"Could not verify durable terminal host PID {connection.Pid} start identity: {ex.Message}"

let private hostUri connection path =
    Uri($"http://127.0.0.1:{connection.ControlPort}{path}")

let private request
    config
    (connection: HostConnection)
    method
    path
    (body: string option)
    =
    async {
        try
            use timeout =
                new CancellationTokenSource(config.ControlRequestTimeout)
            use request = new HttpRequestMessage(method, hostUri connection path)
            request.Headers.Authorization <-
                AuthenticationHeaderValue("Bearer", connection.ControlToken)

            body
            |> Option.iter (fun json ->
                request.Content <-
                    new StringContent(json, Encoding.UTF8, "application/json"))

            use! response =
                httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token
                )
                |> Async.AwaitTask

            let contentLength =
                response.Content.Headers.ContentLength
                |> Option.ofNullable

            match contentLength with
            | Some length when length > 1024L * 1024L ->
                return Error "Durable terminal host response exceeded 1 MiB"
            | _ ->
                let! content =
                    response.Content.ReadAsStringAsync(timeout.Token)
                    |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    return Ok content
                else
                    return
                        Error
                            $"Durable terminal host returned HTTP {int response.StatusCode}: {content.Trim()}"
        with
        | :? OperationCanceledException ->
            return
                Error
                    $"Durable terminal host request timed out after {config.ControlRequestTimeout.TotalSeconds:g} seconds"
        | ex ->
            return Error $"Durable terminal host request failed: {ex.Message}"
    }

let private parseHealth connection (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! version = requiredInt "version" root

            if version <> connection.Version then
                return!
                    Error
                        $"Durable terminal host manifest protocol {connection.Version} does not match health protocol {version}"

            let! pid = requiredInt "pid" root
            let! startedAt = requiredString "startedAt" root

            if pid <> connection.Pid || startedAt <> connection.StartedAt then
                return!
                    Error
                        "Durable terminal host state does not match the running control endpoint"

            if version = hostProtocolVersion then
                let! generation = requiredString "generation" root
                let! processStartTicks =
                    requiredInt64String "processStartTicks" root
                let! processStartExact =
                    requiredBool "processStartExact" root

                if
                    generation <> connection.Generation
                    || processStartTicks
                       <> connection.ProcessStartTicks
                    || processStartExact
                       <> connection.ProcessStartExact
                then
                    return!
                        Error
                            "Durable terminal host state does not match the running control endpoint"
        }
    with
    | :? JsonException as ex ->
        Error $"Invalid durable terminal host health response: {ex.Message}"
    | ex ->
        Error $"Could not read durable terminal host health response: {ex.Message}"

let private probe config connection =
    asyncResult {
        let! response =
            request config connection HttpMethod.Get "/health" None

        return! parseHealth connection response
    }

type private HostDiscovery =
    | MissingHost
    | HealthyHost of HostConnection
    | DeadHost of HostConnection * reason: string

type private ManifestReclaim =
    | Reclaimed
    | ReclaimDeferred
    | OwnershipChanged

type private LegacyRetirement =
    | LegacyRetired
    | LegacyReplaced of HostConnection

let private discoverHost config =
    async {
        match readHostConnection config with
        | Error error -> return Error error
        | Ok None -> return Ok MissingHost
        | Ok (Some connection) ->
            match processIdentityMatches connection with
            | Error error -> return Error error
            | Ok false ->
                return
                    Ok(
                        DeadHost(
                            connection,
                            $"Durable terminal host PID {connection.Pid} is no longer the recorded process"
                        )
                    )
            | Ok true ->
                match! probe config connection with
                | Ok () -> return Ok(HealthyHost connection)
                | Error probeError ->
                    match processIdentityMatches connection with
                    | Ok false ->
                        return
                            Ok(
                                DeadHost(
                                    connection,
                                    $"Durable terminal host PID {connection.Pid} exited: {probeError}"
                                )
                            )
                    | Ok true ->
                        return
                            Error
                                $"Durable terminal host PID {connection.Pid} is alive but unhealthy: {probeError}"
                    | Error identityError -> return Error identityError
    }

let private sameConnectionOwner
    (left: HostConnection)
    (right: HostConnection)
    =
    if left.Version = 1 || right.Version = 1 then
        left.Version = right.Version
        && left.Pid = right.Pid
        && left.ProcessStartTicks = right.ProcessStartTicks
        && left.ControlPort = right.ControlPort
        && left.ControlToken = right.ControlToken
        && left.StartedAt = right.StartedAt
    else
        sameHostIdentity (hostIdentity left) (hostIdentity right)

let private removeManifestIfConnectionOwned path expected =
    let claimedPath =
        $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.reclaim"

    let restoreClaim () =
        try
            if File.Exists claimedPath && not (File.Exists path) then
                File.Move(claimedPath, path)

            Ok false
        with ex ->
            Error
                $"Could not restore unclaimed durable terminal host state: {ex.Message}"

    let claim () =
        try
            if File.Exists path then
                File.Move(path, claimedPath)
                Ok true
            else
                Ok false
        with
        | :? FileNotFoundException -> Ok false
        | ex ->
            Error
                $"Could not claim durable terminal host state for removal: {ex.Message}"

    match claim () with
    | Error error -> Error error
    | Ok false -> Ok true
    | Ok true ->
        let current =
            try
                File.ReadAllText claimedPath
                |> parseHostConnection
            with ex ->
                Error
                    $"Could not read claimed durable terminal host state: {ex.Message}"

        match current with
        | Error error ->
            restoreClaim ()
            |> Result.bind (fun _ -> Error error)
        | Ok current
            when sameConnectionOwner current expected |> not ->
            restoreClaim ()
        | Ok _ ->
            try
                File.Delete claimedPath
                Ok true
            with ex ->
                restoreClaim ()
                |> Result.bind (fun _ ->
                    Error
                        $"Could not remove claimed durable terminal host state: {ex.Message}")

let internal removeManifestIfOwned path expectedText =
    parseHostConnection expectedText
    |> Result.bind (removeManifestIfConnectionOwned path)

let private removeStaleState config expected =
    removeManifestIfConnectionOwned
        (statePath config)
        expected
    |> Result.map (function
        | true -> Reclaimed
        | false -> OwnershipChanged)

let private startupLockPath config =
    Path.Combine(config.HostStateDirectory, "host.lock")

let private worktreeLockPath config worktreePath =
    let key =
        worktreePath
        |> WorktreePath.value
        |> PathUtils.normalizePath
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    Path.Combine(
        config.HostStateDirectory,
        "worktree-locks",
        $"{key}.lock"
    )

let private tryAcquireWorktreeLock config worktreePath =
    try
        let path = worktreeLockPath config worktreePath
        Directory.CreateDirectory(Path.GetDirectoryName path)
        |> ignore

        new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        )
        |> Some
        |> Ok
    with
    | :? IOException -> Ok None
    | ex ->
        Error
            $"Could not acquire terminal worktree ownership: {ex.Message}"

let private tryAcquireStartupLock config =
    try
        Directory.CreateDirectory config.HostStateDirectory |> ignore

        new FileStream(
            startupLockPath config,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read
        )
        |> Some
        |> Ok
    with
    | :? IOException -> Ok None
    | ex ->
        Error
            $"Could not acquire durable terminal host startup ownership: {ex.Message}"

let private writeStartupClaim
    (stream: FileStream)
    generation
    (hostProcess: (int * int64) option)
    =
    try
        let claim =
            match hostProcess with
            | None ->
                JsonSerializer.SerializeToUtf8Bytes(
                    {| generation = generation
                       ownerPid = Environment.ProcessId
                       ownerProcessStartTicks =
                        currentProcessStartTicks () |}
                )
            | Some(hostPid, hostProcessStartTicks) ->
                JsonSerializer.SerializeToUtf8Bytes(
                    {| generation = generation
                       ownerPid = Environment.ProcessId
                       ownerProcessStartTicks =
                        currentProcessStartTicks ()
                       hostPid = hostPid
                       hostProcessStartTicks =
                        string hostProcessStartTicks |}
                )

        stream.Position <- 0L
        stream.SetLength 0L
        stream.Write(claim, 0, claim.Length)
        stream.Flush true
        Ok ()
    with ex ->
        Error $"Could not write durable terminal startup ownership: {ex.Message}"

let private startHostProcess config generation =
    try
        Directory.CreateDirectory config.HostStateDirectory |> ignore

        let psi =
            ProcessStartInfo(
                FileName = config.NodeExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            )

        [ config.HostScriptPath
          "--state-dir"
          config.HostStateDirectory
          "--ttyd"
          config.TtydExecutablePath
          "--shell"
          config.ShellCommand
          "--generation"
          generation ]
        |> List.iter psi.ArgumentList.Add

        use proc = new Process(StartInfo = psi)

        if proc.Start() then
            Ok(
                proc.Id,
                proc.StartTime.ToUniversalTime().Ticks
            )
        else
            Error "Node did not start the durable terminal host"
    with ex ->
        Error $"Failed to start the durable terminal host: {ex.Message}"

let private waitForHost config deadline startedPid =
    let rec wait () =
        async {
            match! discoverHost config with
            | Ok (HealthyHost connection) -> return Ok connection
            | Error error when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {startedPid}: {error}"
            | Ok (DeadHost(_, error)) when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {startedPid}: {error}"
            | Ok MissingHost when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {startedPid}"
            | Error _
            | Ok (DeadHost _)
            | Ok MissingHost ->
                if not (pidIsAlive startedPid) then
                    return
                        Error
                            $"Durable terminal host PID {startedPid} exited during startup"
                else
                    do! Async.Sleep config.ProbeInterval
                    return! wait ()
        }

    wait ()

let private ensureHostWithTtydRequirement requireTtyd config =
    async {
        if not (File.Exists config.HostScriptPath) then
            return
                Error
                    $"Durable terminal host script is missing at '{config.HostScriptPath}'"
        elif requireTtyd && not (File.Exists config.TtydExecutablePath) then
            return
                Error
                    $"ttyd is not installed at '{config.TtydExecutablePath}'. Run '.\\treemon.ps1 setup-ttyd'."
        else
            let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

            let rec acquireOrDiscover () =
                async {
                    match! discoverHost config with
                    | Ok (HealthyHost connection) ->
                        return Ok connection
                    | Error error -> return Error error
                    | Ok (DeadHost _)
                    | Ok MissingHost ->
                        match tryAcquireStartupLock config with
                        | Error error -> return Error error
                        | Ok None when DateTimeOffset.UtcNow >= deadline ->
                            return
                                Error
                                    "Timed out waiting for durable terminal host startup ownership"
                        | Ok None ->
                            do! Async.Sleep config.ProbeInterval
                            return! acquireOrDiscover ()
                        | Ok (Some startupLock) ->
                            use startupLock = startupLock

                            match! discoverHost config with
                            | Ok (HealthyHost connection) ->
                                return Ok connection
                            | Error error -> return Error error
                            | Ok discovery ->
                                let startNewHost () =
                                    async {
                                        let generation =
                                            Guid.NewGuid().ToString("N")

                                        match
                                            writeStartupClaim
                                                startupLock
                                                generation
                                                None
                                        with
                                        | Error error -> return Error error
                                        | Ok () ->
                                            match startHostProcess config generation with
                                            | Error error -> return Error error
                                            | Ok(startedPid, startedAt) ->
                                                match
                                                    writeStartupClaim
                                                        startupLock
                                                        generation
                                                        (Some(
                                                            startedPid,
                                                            startedAt
                                                        ))
                                                with
                                                | Error error ->
                                                    return Error error
                                                | Ok () ->
                                                    return!
                                                        waitForHost
                                                            config
                                                            deadline
                                                            startedPid
                                    }

                                match discovery with
                                | DeadHost(connection, _) ->
                                    match removeStaleState config connection with
                                    | Error error -> return Error error
                                    | Ok Reclaimed -> return! startNewHost ()
                                    | Ok OwnershipChanged ->
                                        match! discoverHost config with
                                        | Ok (HealthyHost replacement) ->
                                            return Ok replacement
                                        | Ok _ ->
                                            return
                                                Error
                                                    "Durable terminal host ownership changed to an unavailable replacement"
                                        | Error error -> return Error error
                                    | Ok ReclaimDeferred ->
                                        return
                                            Error
                                                "Durable terminal host reclamation was unexpectedly deferred"
                                | MissingHost ->
                                    return! startNewHost ()
                                | HealthyHost connection ->
                                    return Ok connection
                }

            return! acquireOrDiscover ()
    }

let private ensureHost =
    ensureHostWithTtydRequirement true

let private ensureControlHost =
    ensureHostWithTtydRequirement false

let private getHostSessions config connection =
    asyncResult {
        let! content =
            request config connection HttpMethod.Get "/sessions" None

        return! parseHostSessions content
    }

let private announce config connection instanceId =
    let body =
        JsonSerializer.Serialize(
            {| kind = "treemon-connected"
               treemonPid = Environment.ProcessId
               instanceId = instanceId |}
        )

    request config connection HttpMethod.Post "/events" (Some body)
    |> AsyncResult.ignore

let private announceIfNeeded config state connection instanceId =
    async {
        let identity = hostIdentity connection
        let known = { state with KnownHost = Some identity }

        if
            known.AnnouncedHost
            |> Option.exists (sameHostIdentity identity)
        then
            return known
        else
            match! announce config connection instanceId with
            | Ok () ->
                return
                    { known with
                        AnnouncedHost = Some identity }
            | Error error ->
                Log.log
                    "EmbeddedTerminal"
                    $"Failed to record Treemon reconnect with durable host PID {connection.Pid}: {error}"

                return known
    }

let private startTerminal config instanceId state worktreePath =
    async {
        match! ensureHost config with
        | Error error ->
            let current = withFailure worktreePath error state.LastSnapshot
            return Ok current, { state with LastSnapshot = current }
        | Ok connection ->
            let! announced =
                announceIfNeeded config state connection instanceId

            let reconcile failure =
                async {
                    match! getHostSessions config connection with
                    | Ok sessions
                        when sessions
                             |> List.exists (fun session ->
                                 isPath worktreePath session.Tab) ->
                        let current =
                            sessions
                            |> snapshot
                            |> mergeSnapshot announced.LastSnapshot

                        return
                            Ok current,
                            { announced with LastSnapshot = current }
                    | Ok sessions ->
                        let current =
                            sessions
                            |> snapshot
                            |> mergeSnapshot announced.LastSnapshot

                        return
                            Error
                                $"{failure}; reconciliation found no session for '{WorktreePath.value worktreePath}'",
                            { announced with LastSnapshot = current }
                    | Error reconcileError ->
                        let error =
                            $"{failure}; start reconciliation failed: {reconcileError}"

                        let current =
                            withFailure
                                worktreePath
                                error
                                announced.LastSnapshot

                        return
                            Error error,
                            { announced with LastSnapshot = current }
                }

            if connection.Version = 1 then
                match! getHostSessions config connection with
                | Error error -> return! reconcile error
                | Ok sessions
                    when sessions
                         |> List.exists (fun session ->
                             isPath worktreePath session.Tab) ->
                    let current =
                        sessions
                        |> snapshot
                        |> mergeSnapshot announced.LastSnapshot

                    return
                        Ok current,
                        { announced with LastSnapshot = current }
                | Ok sessions ->
                    let error =
                        "The protocol-1 durable terminal host is in drain-only compatibility mode; close its remaining tabs before starting a new terminal"

                    let current =
                        sessions
                        |> snapshot
                        |> mergeSnapshot announced.LastSnapshot
                        |> withFailure worktreePath error

                    return Error error, { announced with LastSnapshot = current }
            else
                let body =
                    JsonSerializer.Serialize(
                        {| worktreePath =
                            WorktreePath.value worktreePath |}
                    )

                match!
                    request
                        config
                        connection
                        HttpMethod.Post
                        "/sessions"
                        (Some body)
                with
                | Error error -> return! reconcile error
                | Ok content ->
                    match parseHostSessions content with
                    | Error error -> return! reconcile error
                    | Ok sessions ->
                        let current =
                            sessions
                            |> snapshot
                            |> mergeSnapshot announced.LastSnapshot

                        return
                            Ok current,
                            { announced with LastSnapshot = current }
    }

let private reclaimDeadHost config connection =
    match tryAcquireStartupLock config with
    | Error error -> Error error
    | Ok None -> Ok ReclaimDeferred
    | Ok (Some startupLock) ->
        use startupLock = startupLock
        removeStaleState config connection

let private hostFailure error state =
    let current = withHostFailure error state.LastSnapshot
    current, { state with LastSnapshot = current }

let private getTerminals instanceId state config =
    async {
        match! discoverHost config with
        | Ok MissingHost
            when state.KnownHost.IsNone
                 && (state.LastSnapshot.Tabs
                     |> List.exists isInterrupted
                     |> not) ->
            return
                EmbeddedTerminalSnapshot.empty,
                { state with
                    LastSnapshot = EmbeddedTerminalSnapshot.empty
                    AnnouncedHost = None }
        | Ok MissingHost when state.KnownHost.IsNone ->
            return state.LastSnapshot, state
        | Ok MissingHost ->
            let error =
                "Previously known durable terminal host metadata disappeared; terminal processes may have been interrupted"

            return hostFailure error state
        | Ok (DeadHost(connection, error)) ->
            match reclaimDeadHost config connection with
            | Ok _ -> ()
            | Error reclaimError ->
                Log.log "EmbeddedTerminal" reclaimError

            return hostFailure error state
        | Error error ->
            Log.log "EmbeddedTerminal" error
            return hostFailure error state
        | Ok (HealthyHost connection) ->
            let! announced =
                announceIfNeeded config state connection instanceId

            match! getHostSessions config connection with
            | Error error ->
                Log.log "EmbeddedTerminal" error
                return hostFailure error announced
            | Ok sessions ->
                let current =
                    sessions
                    |> snapshot
                    |> mergeSnapshot announced.LastSnapshot

                return current, { announced with LastSnapshot = current }
    }

let private waitForLegacyHostExit config connection =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let waitAgain continueWaiting timeoutError =
        async {
            if DateTimeOffset.UtcNow >= deadline then
                return Error timeoutError
            else
                do! Async.Sleep config.ProbeInterval
                return! continueWaiting ()
        }

    let rec validateReplacement () =
        async {
            match! discoverHost config with
            | Ok MissingHost -> return Ok LegacyRetired
            | Ok (HealthyHost current)
                when sameConnectionOwner current connection ->
                return!
                    waitAgain
                        validateReplacement
                        "Timed out waiting for protocol-1 durable terminal ownership to change"
            | Ok (HealthyHost current)
                when current.Version = hostProtocolVersion ->
                return Ok(LegacyReplaced current)
            | Ok (HealthyHost _) ->
                return
                    Error
                        "Protocol-1 durable terminal ownership changed to another legacy host"
            | Ok (DeadHost(current, _))
                when sameConnectionOwner current connection ->
                return! wait ()
            | Ok (DeadHost _) ->
                return!
                    waitAgain
                        validateReplacement
                        "Timed out waiting for replacement durable terminal host to become healthy"
            | Error error when DateTimeOffset.UtcNow >= deadline ->
                return Error error
            | Error _ ->
                do! Async.Sleep config.ProbeInterval
                return! validateReplacement ()
        }

    and wait () =
        async {
            match processIdentityMatches connection with
            | Ok false ->
                match reclaimDeadHost config connection with
                | Ok Reclaimed -> return Ok LegacyRetired
                | Ok OwnershipChanged ->
                    return! validateReplacement ()
                | Ok ReclaimDeferred ->
                    match readHostConnection config with
                    | Ok None -> return Ok LegacyRetired
                    | Ok (Some current)
                        when sameConnectionOwner current connection
                             |> not ->
                        return! validateReplacement ()
                    | Ok _
                        when DateTimeOffset.UtcNow >= deadline ->
                        return
                            Error
                                "Timed out reclaiming protocol-1 durable terminal metadata"
                    | Ok _ ->
                        do! Async.Sleep config.ProbeInterval
                        return! wait ()
                    | Error error -> return Error error
                | Error error -> return Error error
            | Ok true ->
                return!
                    waitAgain
                        wait
                        $"Timed out waiting for protocol-1 durable terminal host PID {connection.Pid} to drain"
            | Error error -> return Error error
        }

    wait ()

let rec private closeTerminalStrict instanceId state config worktreePath =
    async {
        let closeFailure error failureSnapshot failureState =
            let current =
                withFailure worktreePath error failureSnapshot

            Error error, { failureState with LastSnapshot = current }

        let confirmedClosed sessions confirmedState =
            let current =
                sessions
                |> snapshot
                |> mergeSnapshot confirmedState.LastSnapshot
                |> withoutPath worktreePath

            Ok current, { confirmedState with LastSnapshot = current }

        let finishConfirmed connection sessions confirmedState =
            async {
                if connection.Version <> 1 || not (List.isEmpty sessions) then
                    return confirmedClosed sessions confirmedState
                else
                    match!
                        request
                            config
                            connection
                            HttpMethod.Post
                            "/shutdown"
                            None
                    with
                    | Error error ->
                        return
                            closeFailure
                                $"Protocol-1 terminal closed, but its empty host did not drain: {error}"
                                confirmedState.LastSnapshot
                                confirmedState
                    | Ok _ ->
                        match! waitForLegacyHostExit config connection with
                        | Ok LegacyRetired ->
                            return
                                confirmedClosed
                                    sessions
                                    { confirmedState with
                                        AnnouncedHost = None
                                        KnownHost = None }
                        | Ok (LegacyReplaced _) ->
                            let current =
                                sessions
                                |> snapshot
                                |> mergeSnapshot confirmedState.LastSnapshot
                                |> withoutPath worktreePath

                            return!
                                closeTerminalStrict
                                    instanceId
                                    { confirmedState with
                                        LastSnapshot = current
                                        AnnouncedHost = None
                                        KnownHost = None }
                                    config
                                    worktreePath
                        | Error error ->
                            return
                                closeFailure
                                    error
                                    confirmedState.LastSnapshot
                                    confirmedState
            }

        let reconcileClose connection failure reconcileState =
            async {
                match! getHostSessions config connection with
                | Ok sessions
                    when sessions
                         |> List.exists (fun session ->
                             isPath worktreePath session.Tab)
                         |> not ->
                    return!
                        finishConfirmed
                            connection
                            sessions
                            reconcileState
                | Ok sessions ->
                    let error =
                        $"{failure}; the durable host still reports the terminal session"

                    return
                        closeFailure
                            error
                            (sessions
                             |> snapshot
                             |> mergeSnapshot reconcileState.LastSnapshot)
                            reconcileState
                | Error reconcileError ->
                    let error =
                        $"{failure}; close reconciliation failed: {reconcileError}"

                    return
                        closeFailure
                            error
                            reconcileState.LastSnapshot
                            reconcileState
            }

        match! discoverHost config with
        | Ok MissingHost when state.KnownHost.IsNone ->
            let current = withoutPath worktreePath state.LastSnapshot
            return
                Ok current,
                { state with
                    LastSnapshot = current
                    AnnouncedHost = None }
        | Ok MissingHost ->
            let error =
                "Cannot confirm terminal cleanup because the previously known durable host disappeared"

            return closeFailure error state.LastSnapshot state
        | Ok (DeadHost(connection, reason)) ->
            match reclaimDeadHost config connection with
            | Ok _ -> ()
            | Error reclaimError ->
                Log.log "EmbeddedTerminal" reclaimError

            let error =
                $"Cannot confirm terminal cleanup because {reason}"

            return closeFailure error state.LastSnapshot state
        | Error error ->
            Log.log "EmbeddedTerminal" error
            let actionable =
                $"Cannot discover the durable terminal host to confirm terminal cleanup: {error}"

            return closeFailure actionable state.LastSnapshot state
        | Ok (HealthyHost connection) ->
            let! announced =
                announceIfNeeded config state connection instanceId

            match! getHostSessions config connection with
            | Error error ->
                Log.log "EmbeddedTerminal" error
                let actionable =
                    $"Cannot list durable terminal sessions to confirm terminal cleanup: {error}"

                return
                    closeFailure
                        actionable
                        announced.LastSnapshot
                        announced
            | Ok sessions ->
                match sessions |> List.tryFind (fun session -> isPath worktreePath session.Tab) with
                | None ->
                    return!
                        finishConfirmed
                            connection
                            sessions
                            announced
                | Some session ->
                    let path = $"/sessions/{Uri.EscapeDataString session.Id}"

                    match!
                        request
                            config
                            connection
                            HttpMethod.Delete
                            path
                            None
                    with
                    | Error error ->
                        Log.log "EmbeddedTerminal" error
                        return!
                            reconcileClose
                                connection
                                $"Durable terminal close request failed: {error}"
                                announced
                    | Ok content ->
                        match parseHostSessions content with
                        | Error error ->
                            Log.log "EmbeddedTerminal" error
                            return!
                                reconcileClose
                                    connection
                                    $"Durable terminal close response was invalid: {error}"
                                    announced
                        | Ok remaining ->
                            if
                                remaining
                                |> List.exists (fun candidate ->
                                    isPath worktreePath candidate.Tab)
                            then
                                let error =
                                    "Durable terminal host did not remove the terminal session"

                                return
                                    closeFailure
                                        error
                                        (remaining
                                         |> snapshot
                                         |> mergeSnapshot announced.LastSnapshot)
                                        announced
                            else
                                return!
                                    finishConfirmed
                                        connection
                                        remaining
                                        announced
    }

let private closeTerminal instanceId state config worktreePath =
    let currentTab =
        state.LastSnapshot.Tabs
        |> List.tryFind (isPath worktreePath)

    let dismiss snapshot nextState =
        let current =
            snapshot |> withoutPath worktreePath

        current, { nextState with LastSnapshot = current }

    async {
        match currentTab with
        | None ->
            return state.LastSnapshot, state
        | Some tab when not (isInterrupted tab) ->
            let! result, next =
                closeTerminalStrict
                    instanceId
                    state
                    config
                    worktreePath

            return Result.defaultValue next.LastSnapshot result, next
        | Some _ ->
            match! discoverHost config with
            | Ok (HealthyHost connection) ->
                let! announced =
                    announceIfNeeded config state connection instanceId

                match! getHostSessions config connection with
                | Ok sessions
                    when sessions
                         |> List.exists (fun session ->
                             isPath worktreePath session.Tab) ->
                    let! result, next =
                        closeTerminalStrict
                            instanceId
                            announced
                            config
                            worktreePath

                    return
                        Result.defaultValue next.LastSnapshot result,
                        next
                | Ok sessions ->
                    return
                        dismiss
                            (sessions
                             |> snapshot
                             |> mergeSnapshot announced.LastSnapshot)
                            announced
                | Error error ->
                    Log.log "EmbeddedTerminal" error
                    return dismiss announced.LastSnapshot announced
            | Ok (DeadHost(connection, _)) ->
                match reclaimDeadHost config connection with
                | Ok _ -> ()
                | Error error ->
                    Log.log "EmbeddedTerminal" error

                return dismiss state.LastSnapshot state
            | Ok MissingHost ->
                return dismiss state.LastSnapshot state
            | Error error ->
                Log.log "EmbeddedTerminal" error
                return dismiss state.LastSnapshot state
    }

let private renewReservation
    config
    connection
    (reservationId: string)
    (cancellation: CancellationToken)
    =
    let path =
        $"/reservations/{Uri.EscapeDataString reservationId}/renew"

    let rec renew () =
        async {
            try
                do!
                    Task.Delay(
                        TimeSpan.FromSeconds 30.0,
                        cancellation
                    )
                    |> Async.AwaitTask

                match!
                    request
                        config
                        connection
                        HttpMethod.Post
                        path
                        None
                with
                | Ok _ -> return! renew ()
                | Error error ->
                    return
                        Error
                            $"Durable terminal cleanup reservation renewal failed: {error}"
            with :? OperationCanceledException ->
                return Ok ()
        }

    renew ()

let private releaseReservation
    config
    connection
    (reservationId: string)
    =
    let path =
        $"/reservations/{Uri.EscapeDataString reservationId}"

    request config connection HttpMethod.Delete path None
    |> AsyncResult.ignore

let private runReservedOperation
    reservation
    operation
    =
    async {
        use renewalCancellation = new CancellationTokenSource()
        let! renewal =
            reservation.Renew renewalCancellation.Token
            |> Async.StartChild

        let! operationResult =
            async {
                try
                    return! operation ()
                with ex ->
                    return
                        Error
                            $"Worktree mutation failed unexpectedly: {ex.Message}"
            }

        renewalCancellation.Cancel()
        let! renewalResult = renewal
        let! releaseResult = reservation.Release ()

        return
            match operationResult, renewalResult, releaseResult with
            | Ok (), Ok (), Ok () -> Ok ()
            | Error error, Ok (), Ok () -> Error error
            | Ok (), Error error, Ok () -> Error error
            | Ok (), Ok (), Error error ->
                Error
                    $"Worktree mutation completed, but terminal cleanup reservation release failed: {error}"
            | Error operationError, _, Error releaseError ->
                Error
                    $"{operationError}; terminal cleanup reservation release also failed: {releaseError}"
            | Error operationError, Error renewalError, Ok () ->
                Error $"{operationError}; {renewalError}"
            | Ok (), Error renewalError, Error releaseError ->
                Error
                    $"{renewalError}; terminal cleanup reservation release also failed: {releaseError}"
    }

let private reserveOnCurrentHost
    config
    instanceId
    state
    connection
    worktreePath
    =
    async {
        let! announced =
            announceIfNeeded config state connection instanceId

        let reservationId =
            Guid.NewGuid().ToString("N")

        let body =
            JsonSerializer.Serialize(
                {| worktreePath = WorktreePath.value worktreePath
                   reservationId = reservationId |}
            )

        let reservationFailure error =
            async {
                let! release =
                    releaseReservation
                        config
                        connection
                        reservationId

                let actionable =
                    match release with
                    | Ok () ->
                        $"Could not reserve authoritative terminal cleanup: {error}"
                    | Error releaseError ->
                        $"Could not reserve authoritative terminal cleanup: {error}; reservation release also failed: {releaseError}"

                let current =
                    withFailure
                        worktreePath
                        actionable
                        announced.LastSnapshot

                return
                    Error actionable,
                    { announced with LastSnapshot = current }
            }

        match!
            request
                config
                connection
                HttpMethod.Post
                "/reservations"
                (Some body)
        with
        | Error error -> return! reservationFailure error
        | Ok content ->
            match parseReservation content with
            | Error error -> return! reservationFailure error
            | Ok reservation
                when reservation.Id <> reservationId ->
                return!
                    reservationFailure
                        "Durable terminal host returned a different reservation identity"
            | Ok reservation ->
                let current =
                    reservation.Sessions
                    |> snapshot
                    |> mergeSnapshot announced.LastSnapshot
                    |> withoutPath worktreePath

                let next =
                    { announced with LastSnapshot = current }

                return
                    Ok
                        { Renew =
                            renewReservation
                                config
                                connection
                                reservation.Id
                          Release =
                            fun () ->
                                releaseReservation
                                    config
                                    connection
                                    reservation.Id },
                    next
    }

let private acquireWorktreeLock config worktreePath =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec acquire () =
        async {
            match tryAcquireWorktreeLock config worktreePath with
            | Ok (Some worktreeLock) -> return Ok worktreeLock
            | Error error -> return Error error
            | Ok None when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        "Timed out waiting for terminal worktree ownership"
            | Ok None ->
                do! Async.Sleep config.ProbeInterval
                return! acquire ()
        }

    acquire ()

let private reserveTerminalCleanup
    config
    instanceId
    state
    worktreePath
    =
    let reserveCurrent currentState =
        async {
            match! ensureControlHost config with
            | Error error -> return Error error, currentState
            | Ok connection
                when connection.Version
                     = hostProtocolVersion ->
                return!
                    reserveOnCurrentHost
                        config
                        instanceId
                        currentState
                        connection
                        worktreePath
            | Ok _ ->
                return
                    Error
                        "Protocol-1 durable terminal host did not finish draining",
                    currentState
        }

    async {
        match! discoverHost config with
        | Ok (HealthyHost connection)
            when connection.Version = hostProtocolVersion ->
            return!
                reserveOnCurrentHost
                    config
                    instanceId
                    state
                    connection
                    worktreePath
        | Error error ->
            let actionable =
                $"Cannot reserve terminal cleanup because host discovery failed: {error}"

            return Error actionable, state
        | Ok (HealthyHost connection) ->
            match!
                request
                    config
                    connection
                    HttpMethod.Post
                    "/shutdown"
                    None
            with
            | Error error ->
                return
                    Error
                        $"Could not drain protocol-1 durable terminal host: {error}",
                    state
            | Ok _ ->
                match! waitForLegacyHostExit config connection with
                | Error error -> return Error error, state
                | Ok retirement ->
                    let interrupted =
                        state.LastSnapshot
                        |> withHostFailure
                            "the protocol-1 host was drained during its bounded compatibility window"
                        |> withoutPath worktreePath

                    let retiredState =
                        { state with
                            LastSnapshot = interrupted
                            AnnouncedHost = None
                            KnownHost = None }

                    match retirement with
                    | LegacyRetired ->
                        return! reserveCurrent retiredState
                    | LegacyReplaced replacement ->
                        return!
                            reserveOnCurrentHost
                                config
                                instanceId
                                retiredState
                                replacement
                                worktreePath
        | Ok (DeadHost _)
        | Ok MissingHost ->
            return! reserveCurrent state
    }

let private waitForHostExit config connection =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec wait () =
        async {
            match processIdentityMatches connection with
            | Ok false ->
                match reclaimDeadHost config connection with
                | Ok Reclaimed
                | Ok OwnershipChanged ->
                    return Ok ()
                | Ok ReclaimDeferred ->
                    match readHostConnection config with
                    | Ok None -> return Ok ()
                    | Ok (Some current)
                        when sameConnectionOwner current connection
                             |> not ->
                        return Ok ()
                    | _ when DateTimeOffset.UtcNow >= deadline ->
                        return
                            Error
                                "Timed out waiting to reclaim stopped durable terminal host metadata"
                    | _ ->
                        do! Async.Sleep config.ProbeInterval
                        return! wait ()
                | Error error -> return Error error
            | Ok true when DateTimeOffset.UtcNow < deadline ->
                do! Async.Sleep config.ProbeInterval
                return! wait ()
            | Ok true ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {connection.Pid} to stop"
            | Error error when DateTimeOffset.UtcNow >= deadline ->
                return Error error
            | Error _ ->
                do! Async.Sleep config.ProbeInterval
                return! wait ()
        }

    wait ()

let private shutdown config =
    async {
        match! discoverHost config with
        | Error error -> return Error error
        | Ok MissingHost -> return Ok ()
        | Ok (DeadHost(connection, _)) ->
            return! waitForHostExit config connection
        | Ok (HealthyHost connection) ->
            match!
                request
                    config
                    connection
                    HttpMethod.Post
                    "/shutdown"
                    None
                |> AsyncResult.ignore
            with
            | Error error -> return Error error
            | Ok () -> return! waitForHostExit config connection
    }

let internal createWithConfig config =
    let instanceId = Guid.NewGuid().ToString("N")

    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop state =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Start(worktreePath, reply) ->
                        let canonical = canonicalWorktreePath worktreePath
                        match! acquireWorktreeLock config canonical with
                        | Error error ->
                            let current =
                                withFailure
                                    canonical
                                    error
                                    state.LastSnapshot

                            reply.Reply(Error error)
                            return!
                                loop
                                    { state with
                                        LastSnapshot = current }
                        | Ok worktreeLock ->
                            let! result, next =
                                async {
                                    use worktreeLock = worktreeLock
                                    return!
                                        startTerminal
                                            config
                                            instanceId
                                            state
                                            canonical
                                }

                            reply.Reply result
                            return! loop next
                    | Get reply ->
                        let! current, next = getTerminals instanceId state config
                        reply.Reply current
                        return! loop next
                    | Close(worktreePath, reply) ->
                        let canonical = canonicalWorktreePath worktreePath
                        let! current, next =
                            closeTerminal
                                instanceId
                                state
                                config
                                canonical

                        reply.Reply current
                        return! loop next
                    | CloseStrict(worktreePath, reply) ->
                        let canonical = canonicalWorktreePath worktreePath
                        let! result, next =
                            closeTerminalStrict
                                instanceId
                                state
                                config
                                canonical

                        reply.Reply result
                        return! loop next
                    | ReserveCleanup(worktreePath, reply) ->
                        let canonical =
                            canonicalWorktreePath worktreePath

                        match! acquireWorktreeLock config canonical with
                        | Error error ->
                            reply.Reply(Error error)
                            return! loop state
                        | Ok worktreeLock ->
                            let! result, next =
                                async {
                                    use worktreeLock = worktreeLock
                                    return!
                                        reserveTerminalCleanup
                                            config
                                            instanceId
                                            state
                                            canonical
                                }

                            reply.Reply result
                            return! loop next
                    | ShutdownHost reply ->
                        let! result = shutdown config
                        reply.Reply result

                        let next =
                            match result with
                            | Ok () ->
                                { LastSnapshot =
                                    EmbeddedTerminalSnapshot.empty
                                  AnnouncedHost = None
                                  KnownHost = None }
                            | Error error ->
                                let current =
                                    withHostFailure
                                        error
                                        state.LastSnapshot

                                { state with LastSnapshot = current }

                        return! loop next
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty
                  AnnouncedHost = None
                  KnownHost = None })

    Manager agent

let create () = createWithConfig (defaultConfig ())

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

let internal withReservedCleanup
    (Manager agent)
    worktreePath
    operation
    =
    async {
        match!
            agent.PostAndAsyncReply(
                (fun reply ->
                    ReserveCleanup(worktreePath, reply)),
                timeout = 120_000
            )
        with
        | Error error -> return Error error
        | Ok reservation ->
            return!
                runReservedOperation
                    reservation
                    operation
    }

let internal shutdownHost (Manager agent) =
    agent.PostAndAsyncReply(ShutdownHost, timeout = 60_000)
