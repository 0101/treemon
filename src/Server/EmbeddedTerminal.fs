module Server.EmbeddedTerminal

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
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
    { Generation: string
      Pid: int
      ProcessStartTicks: int64
      ProcessStartExact: bool
      ControlPort: int
      ControlToken: string
      StartedAt: string }

type private HostSession =
    { Id: string
      Tab: EmbeddedTerminalTab }

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

let private withoutPath path snapshot =
    { Tabs = snapshot.Tabs |> List.filter (isPath path >> not) }

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
                    EmbeddedTerminalLifecycle.Failed(
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

            if version <> hostProtocolVersion then
                return! Error $"Unsupported durable terminal host protocol version {version}"

            let! generation = requiredString "generation" root
            let! pid = requiredInt "pid" root
            let! processStartTicks =
                requiredInt64String "processStartTicks" root
            let! processStartExact =
                requiredBool "processStartExact" root
            let! controlPort = requiredInt "controlPort" root
            let! controlToken = requiredString "controlToken" root
            let! startedAt = requiredString "startedAt" root

            if pid <= 0 then
                return! Error "Invalid durable terminal host PID"

            if String.IsNullOrWhiteSpace generation then
                return! Error "Invalid durable terminal host generation"

            if controlPort <= 0 || controlPort > 65535 || controlPort = 5000 then
                return! Error "Invalid durable terminal host control port"

            if String.IsNullOrWhiteSpace controlToken then
                return! Error "Invalid durable terminal host control token"

            return
                { Generation = generation
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
                Error
                    $"Durable terminal host PID {connection.Pid} has an unverifiable estimated start identity"
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

            if version <> hostProtocolVersion then
                return!
                    Error
                        $"Unsupported durable terminal host protocol version {version}"

            let! generation = requiredString "generation" root
            let! pid = requiredInt "pid" root
            let! processStartTicks =
                requiredInt64String "processStartTicks" root
            let! processStartExact =
                requiredBool "processStartExact" root
            let! startedAt = requiredString "startedAt" root

            if
                generation <> connection.Generation
                || pid <> connection.Pid
                || processStartTicks <> connection.ProcessStartTicks
                || processStartExact <> connection.ProcessStartExact
                || startedAt <> connection.StartedAt
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
    sameHostIdentity (hostIdentity left) (hostIdentity right)

let private removeStaleState config expected =
    match readHostConnection config with
    | Ok None -> Ok ()
    | Ok (Some current) when sameConnectionOwner current expected ->
        try
            File.Delete(statePath config)
            Ok ()
        with ex ->
            Error
                $"Could not remove stale durable terminal host state: {ex.Message}"
    | Ok (Some _) ->
        Error
            "Durable terminal host ownership changed while stale state was being reclaimed"
    | Error error -> Error error

let private startupLockPath config =
    Path.Combine(config.HostStateDirectory, "host.lock")

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

let private ensureHost config =
    async {
        if not (File.Exists config.HostScriptPath) then
            return
                Error
                    $"Durable terminal host script is missing at '{config.HostScriptPath}'"
        elif not (File.Exists config.TtydExecutablePath) then
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
                                    | Ok () -> return! startNewHost ()
                                | MissingHost ->
                                    return! startNewHost ()
                                | HealthyHost connection ->
                                    return Ok connection
                }

            return! acquireOrDiscover ()
    }

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

            let body =
                JsonSerializer.Serialize(
                    {| worktreePath = WorktreePath.value worktreePath |}
                )

            let reconcile failure =
                async {
                    match! getHostSessions config connection with
                    | Ok sessions
                        when sessions
                             |> List.exists (fun session ->
                                 isPath worktreePath session.Tab) ->
                        let current = snapshot sessions
                        return
                            Ok current,
                            { announced with LastSnapshot = current }
                    | Ok sessions ->
                        let current = snapshot sessions
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
                    let current = snapshot sessions
                    return Ok current, { announced with LastSnapshot = current }
    }

let private reclaimDeadHost config connection =
    match tryAcquireStartupLock config with
    | Error error -> Error error
    | Ok None -> Ok false
    | Ok (Some startupLock) ->
        use startupLock = startupLock
        removeStaleState config connection
        |> Result.map (fun () -> true)

let private hostFailure error state =
    let current = withHostFailure error state.LastSnapshot
    current, { state with LastSnapshot = current }

let private getTerminals instanceId state config =
    async {
        match! discoverHost config with
        | Ok MissingHost when state.KnownHost.IsNone ->
            return
                EmbeddedTerminalSnapshot.empty,
                { state with
                    LastSnapshot = EmbeddedTerminalSnapshot.empty
                    AnnouncedHost = None }
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
                let current = snapshot sessions
                return current, { announced with LastSnapshot = current }
    }

let private closeTerminalStrict instanceId state config worktreePath =
    async {
        let closeFailure error failureSnapshot failureState =
            let current =
                withFailure worktreePath error failureSnapshot

            Error error, { failureState with LastSnapshot = current }

        let confirmedClosed sessions confirmedState =
            let current = snapshot sessions
            Ok current, { confirmedState with LastSnapshot = current }

        let reconcileClose connection failure reconcileState =
            async {
                match! getHostSessions config connection with
                | Ok sessions
                    when sessions
                         |> List.exists (fun session ->
                             isPath worktreePath session.Tab)
                         |> not ->
                    return confirmedClosed sessions reconcileState
                | Ok sessions ->
                    let error =
                        $"{failure}; the durable host still reports the terminal session"

                    return
                        closeFailure
                            error
                            (snapshot sessions)
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
                    return confirmedClosed sessions announced
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
                                        (snapshot remaining)
                                        announced
                            else
                                return confirmedClosed remaining announced
    }

let private waitForHostExit config connection =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec wait () =
        async {
            match processIdentityMatches connection with
            | Ok false ->
                match reclaimDeadHost config connection with
                | Ok true -> return Ok ()
                | Ok false ->
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
                        let! result, next =
                            startTerminal config instanceId state canonical

                        reply.Reply result
                        return! loop next
                    | Get reply ->
                        let! current, next = getTerminals instanceId state config
                        reply.Reply current
                        return! loop next
                    | Close(worktreePath, reply) ->
                        let canonical = canonicalWorktreePath worktreePath
                        let! result, next =
                            closeTerminalStrict
                                instanceId
                                state
                                config
                                canonical

                        result
                        |> Result.defaultValue next.LastSnapshot
                        |> reply.Reply
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

let internal shutdownHost (Manager agent) =
    agent.PostAndAsyncReply(ShutdownHost, timeout = 60_000)
