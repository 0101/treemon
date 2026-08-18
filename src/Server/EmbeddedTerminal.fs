module Server.EmbeddedTerminal

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open FsToolkit.ErrorHandling
open Shared

type internal Config =
    { NodeExecutable: string
      HostScriptPath: string
      HostStateDirectory: string
      TtydExecutablePath: string
      ShellCommand: string
      StartupTimeout: TimeSpan
      ProbeInterval: TimeSpan }

type private HostConnection =
    { Pid: int
      ControlPort: int
      ControlToken: string
      StartedAt: string }

type private HostSession =
    { Id: string
      Tab: EmbeddedTerminalTab }

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      AnnouncedHostPid: int option }

type private Message =
    | Start of WorktreePath * AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | Close of WorktreePath * AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ShutdownHost of AsyncReplyChannel<Result<unit, string>>

type Manager = private Manager of MailboxProcessor<Message>

let private httpClient =
    new HttpClient(Timeout = TimeSpan.FromSeconds 10.0)

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
      StartupTimeout = TimeSpan.FromSeconds 10.0
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

let private parseHostConnection (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! version = requiredInt "version" root
            let! pid = requiredInt "pid" root
            let! controlPort = requiredInt "controlPort" root
            let! controlToken = requiredString "controlToken" root
            let! startedAt = requiredString "startedAt" root

            if version <> 1 then
                return! Error $"Unsupported durable terminal host protocol version {version}"

            if pid <= 0 then
                return! Error "Invalid durable terminal host PID"

            if controlPort <= 0 || controlPort > 65535 || controlPort = 5000 then
                return! Error "Invalid durable terminal host control port"

            if String.IsNullOrWhiteSpace controlToken then
                return! Error "Invalid durable terminal host control token"

            return
                { Pid = pid
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
        with ex ->
            Error $"Could not read durable terminal host state: {ex.Message}"

let private processIsAlive pid =
    try
        use proc = Process.GetProcessById pid
        not proc.HasExited
    with
    | :? ArgumentException -> false
    | :? InvalidOperationException -> false

let private hostUri connection path =
    Uri($"http://127.0.0.1:{connection.ControlPort}{path}")

let private request
    (connection: HostConnection)
    method
    path
    (body: string option)
    =
    async {
        try
            use request = new HttpRequestMessage(method, hostUri connection path)
            request.Headers.Authorization <-
                AuthenticationHeaderValue("Bearer", connection.ControlToken)

            body
            |> Option.iter (fun json ->
                request.Content <-
                    new StringContent(json, Encoding.UTF8, "application/json"))

            use! response =
                httpClient.SendAsync request
                |> Async.AwaitTask

            let contentLength =
                response.Content.Headers.ContentLength
                |> Option.ofNullable

            match contentLength with
            | Some length when length > 1024L * 1024L ->
                return Error "Durable terminal host response exceeded 1 MiB"
            | _ ->
                let! content =
                    response.Content.ReadAsStringAsync()
                    |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    return Ok content
                else
                    return
                        Error
                            $"Durable terminal host returned HTTP {int response.StatusCode}: {content.Trim()}"
        with ex ->
            return Error $"Durable terminal host request failed: {ex.Message}"
    }

let private probe connection =
    asyncResult {
        let! response = request connection HttpMethod.Get "/health" None

        use document = JsonDocument.Parse(response)
        let root = document.RootElement
        let! version = requiredInt "version" root
        let! pid = requiredInt "pid" root

        if version <> 1 || pid <> connection.Pid then
            return!
                Error
                    "Durable terminal host state does not match the running control endpoint"
    }

let private removeStaleState config =
    try
        File.Delete(statePath config)
        Ok ()
    with ex ->
        Error $"Could not remove stale durable terminal host state: {ex.Message}"

let private connectExisting config =
    async {
        match readHostConnection config with
        | Error error -> return Error error
        | Ok None -> return Ok None
        | Ok (Some connection) ->
            match! probe connection with
            | Ok () -> return Ok(Some connection)
            | Error error when not (processIsAlive connection.Pid) ->
                return removeStaleState config |> Result.map (fun () -> None)
            | Error error ->
                return
                    Error
                        $"Durable terminal host PID {connection.Pid} is alive but unhealthy: {error}"
    }

let private startHostProcess config =
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
          config.ShellCommand ]
        |> List.iter psi.ArgumentList.Add

        use proc = new Process(StartInfo = psi)

        if proc.Start() then
            Ok proc.Id
        else
            Error "Node did not start the durable terminal host"
    with ex ->
        Error $"Failed to start the durable terminal host: {ex.Message}"

let private waitForHost config startedPid =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec wait () =
        async {
            match! connectExisting config with
            | Ok (Some connection) -> return Ok connection
            | Error error when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {startedPid}: {error}"
            | Ok None when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {startedPid}"
            | Error _
            | Ok None ->
                if not (processIsAlive startedPid) then
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
            match! connectExisting config with
            | Error error -> return Error error
            | Ok (Some connection) -> return Ok connection
            | Ok None ->
                match startHostProcess config with
                | Error error -> return Error error
                | Ok startedPid -> return! waitForHost config startedPid
    }

let private getHostSessions connection =
    asyncResult {
        let! content = request connection HttpMethod.Get "/sessions" None
        return! parseHostSessions content
    }

let private announce connection instanceId =
    let body =
        JsonSerializer.Serialize(
            {| kind = "treemon-connected"
               treemonPid = Environment.ProcessId
               instanceId = instanceId |}
        )

    request connection HttpMethod.Post "/events" (Some body)
    |> AsyncResult.ignore

let private announceIfNeeded state connection instanceId =
    async {
        if state.AnnouncedHostPid = Some connection.Pid then
            return state
        else
            match! announce connection instanceId with
            | Ok () ->
                return { state with AnnouncedHostPid = Some connection.Pid }
            | Error error ->
                Log.log
                    "EmbeddedTerminal"
                    $"Failed to record Treemon reconnect with durable host PID {connection.Pid}: {error}"

                return state
    }

let private startTerminal config instanceId state worktreePath =
    async {
        match! ensureHost config with
        | Error error ->
            let current = withFailure worktreePath error state.LastSnapshot
            return Ok current, { state with LastSnapshot = current }
        | Ok connection ->
            let! announced = announceIfNeeded state connection instanceId
            let body =
                JsonSerializer.Serialize(
                    {| worktreePath = WorktreePath.value worktreePath |}
                )

            match! request connection HttpMethod.Post "/sessions" (Some body) with
            | Error error -> return Error error, announced
            | Ok content ->
                match parseHostSessions content with
                | Error error -> return Error error, announced
                | Ok sessions ->
                    let current = snapshot sessions
                    return Ok current, { announced with LastSnapshot = current }
    }

let private getTerminals instanceId state config =
    async {
        match! connectExisting config with
        | Ok None ->
            let current = EmbeddedTerminalSnapshot.empty
            return current, { state with LastSnapshot = current; AnnouncedHostPid = None }
        | Error error ->
            Log.log "EmbeddedTerminal" error
            let current = withHostFailure error state.LastSnapshot
            return current, { state with LastSnapshot = current }
        | Ok (Some connection) ->
            let! announced = announceIfNeeded state connection instanceId

            match! getHostSessions connection with
            | Error error ->
                Log.log "EmbeddedTerminal" error
                let current = withHostFailure error announced.LastSnapshot
                return current, { announced with LastSnapshot = current }
            | Ok sessions ->
                let current = snapshot sessions
                return current, { announced with LastSnapshot = current }
    }

let private closeTerminal instanceId state config worktreePath =
    async {
        match! connectExisting config with
        | Ok None ->
            let current = withoutPath worktreePath state.LastSnapshot
            return current, { state with LastSnapshot = current; AnnouncedHostPid = None }
        | Error error ->
            Log.log "EmbeddedTerminal" error
            let current = withHostFailure error state.LastSnapshot
            return current, { state with LastSnapshot = current }
        | Ok (Some connection) ->
            let! announced = announceIfNeeded state connection instanceId

            match! getHostSessions connection with
            | Error error ->
                Log.log "EmbeddedTerminal" error
                let current = withHostFailure error announced.LastSnapshot
                return current, { announced with LastSnapshot = current }
            | Ok sessions ->
                match sessions |> List.tryFind (fun session -> isPath worktreePath session.Tab) with
                | None ->
                    let current = snapshot sessions
                    return current, { announced with LastSnapshot = current }
                | Some session ->
                    let path = $"/sessions/{Uri.EscapeDataString session.Id}"

                    match! request connection HttpMethod.Delete path None with
                    | Error error ->
                        Log.log "EmbeddedTerminal" error
                        let current = withHostFailure error announced.LastSnapshot
                        return current, { announced with LastSnapshot = current }
                    | Ok content ->
                        match parseHostSessions content with
                        | Error error ->
                            Log.log "EmbeddedTerminal" error
                            let current = withHostFailure error announced.LastSnapshot
                            return current, { announced with LastSnapshot = current }
                        | Ok remaining ->
                            let current = snapshot remaining
                            return current, { announced with LastSnapshot = current }
    }

let private waitForHostExit config pid =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec wait () =
        async {
            if not (processIsAlive pid) && not (File.Exists(statePath config)) then
                return Ok ()
            elif DateTimeOffset.UtcNow >= deadline then
                return Error $"Timed out waiting for durable terminal host PID {pid} to stop"
            else
                do! Async.Sleep config.ProbeInterval
                return! wait ()
        }

    wait ()

let private shutdown config =
    async {
        match! connectExisting config with
        | Error error -> return Error error
        | Ok None -> return Ok ()
        | Ok (Some connection) ->
            match!
                request connection HttpMethod.Post "/shutdown" None
                |> AsyncResult.ignore
            with
            | Error error -> return Error error
            | Ok () -> return! waitForHostExit config connection.Pid
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
                        let! current, next =
                            closeTerminal instanceId state config canonical

                        reply.Reply current
                        return! loop next
                    | ShutdownHost reply ->
                        let! result = shutdown config
                        reply.Reply result

                        return!
                            loop
                                { LastSnapshot = EmbeddedTerminalSnapshot.empty
                                  AnnouncedHostPid = None }
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty
                  AnnouncedHostPid = None })

    Manager agent

let create () = createWithConfig (defaultConfig ())

let start (Manager agent) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Start(worktreePath, reply)),
        timeout = 30_000
    )

let get (Manager agent) =
    agent.PostAndAsyncReply(Get, timeout = 30_000)

let close (Manager agent) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Close(worktreePath, reply)),
        timeout = 30_000
    )

let internal shutdownHost (Manager agent) =
    agent.PostAndAsyncReply(ShutdownHost, timeout = 30_000)
