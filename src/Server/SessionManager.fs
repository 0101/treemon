module Server.SessionManager

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Shared

type private SessionMsg =
    | Spawn of worktreePath: WorktreePath * command: string * AsyncReplyChannel<Result<unit, string>>
    | SpawnTerminal of worktreePath: WorktreePath * AsyncReplyChannel<Result<unit, string>>
    | OpenNewTab of worktreePath: WorktreePath * AsyncReplyChannel<Result<unit, string>>
    | LaunchAction of worktreePath: WorktreePath * command: string * AsyncReplyChannel<Result<unit, string>>
    | Focus of worktreePath: WorktreePath * AsyncReplyChannel<Result<unit, string>>
    | Kill of worktreePath: WorktreePath * AsyncReplyChannel<Result<unit, string>>
    | GetActiveSessions of AsyncReplyChannel<Map<string, nativeint>>

type SessionAgent = private { Agent: MailboxProcessor<SessionMsg> }

let private resolveNewHwnd (beforeWindows: Set<nativeint>) (timeoutMs: int) =
    let stopwatch = Stopwatch.StartNew()

    let rec poll () =
        async {
            if stopwatch.ElapsedMilliseconds > int64 timeoutMs then
                return None
            else
                let newWindows =
                    Win32.listWindowsTerminalWindows ()
                    |> Set.ofList
                    |> fun current -> Set.difference current beforeWindows

                if Set.isEmpty newWindows then
                    do! Async.Sleep 100
                    return! poll ()
                else
                    return Some(Set.minElement newWindows)
        }

    poll ()

let private encodeCommand (command: string) =
    let bytes = System.Text.Encoding.Unicode.GetBytes(command)
    Convert.ToBase64String(bytes)

let private buildScript (nativePath: string) (command: string option) =
    match command with
    | Some cmd -> $"Set-Location '{nativePath}'; {cmd}"
    | None -> $"Set-Location '{nativePath}'"

let private waitForExitAsync (proc: Process) (timeoutMs: int) =
    async {
        use cts = new CancellationTokenSource(timeoutMs)

        try
            do! proc.WaitForExitAsync(cts.Token) |> Async.AwaitTask
        with :? OperationCanceledException ->
            ()
    }

let private spawnWtAndResolve (args: string) (logLabel: string) =
    async {
        let beforeWindows = Win32.listWindowsTerminalWindows () |> Set.ofList
        Log.log "SessionManager" $"Spawning {logLabel}: wt.exe {args}"

        let psi =
            ProcessStartInfo(
                "wt.exe",
                args,
                UseShellExecute = false,
                CreateNoWindow = true)

        try
            let wtProcess = Process.Start(psi)
            Log.log "SessionManager" $"wt.exe {logLabel} started, PID={wtProcess.Id}"
            do! waitForExitAsync wtProcess 5_000
            Log.log "SessionManager" $"wt.exe {logLabel} exited={wtProcess.HasExited}"

            let! resolved = resolveNewHwnd beforeWindows 10_000

            match resolved with
            | Some hwnd ->
                Log.log "SessionManager" $"{logLabel} HWND {hwnd} resolved"
                return Ok hwnd
            | None ->
                Log.log "SessionManager" $"Failed to resolve {logLabel} HWND"
                return Error $"Failed to detect new window after {logLabel} spawn"
        with ex ->
            Log.log "SessionManager" $"Failed to spawn {logLabel} wt.exe: {ex.Message}"
            return Error $"Failed to spawn {logLabel}: {ex.Message}"
    }

let private spawnWithCommand (worktreePath: string) (command: string option) (logLabel: string) =
    let nativePath = worktreePath.Replace('/', Path.DirectorySeparatorChar)
    let encoded = buildScript nativePath command |> encodeCommand
    spawnWtAndResolve $"--window new -- pwsh -NoExit -EncodedCommand {encoded}" logLabel

let private spawnAndResolve (worktreePath: string) (command: string) =
    spawnWithCommand worktreePath (Some command) "session"

let private spawnTerminalAndResolve (worktreePath: string) =
    spawnWithCommand worktreePath None "terminal"

let private openNewTabInWindow (hwnd: nativeint) (worktreePath: string) (command: string option) =
    async {
        let nativePath = worktreePath.Replace('/', Path.DirectorySeparatorChar)

        if not (Win32.isWindowValid hwnd) then
            return Error "Tracked window is no longer valid"
        else
            if not (Win32.focusWindow hwnd) then
                Log.log "SessionManager" $"Failed to focus HWND={hwnd} for new-tab"

            let encoded = buildScript nativePath command |> encodeCommand

            let psi =
                ProcessStartInfo(
                    "wt.exe",
                    $"-w 0 new-tab -- pwsh -NoExit -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true)

            try
                let p = Process.Start(psi)
                Log.log "SessionManager" $"wt.exe new-tab started, PID={p.Id}, dir={nativePath}"
                do! waitForExitAsync p 5_000
                return Ok()
            with ex ->
                Log.log "SessionManager" $"Failed to open new tab: {ex.Message}"
                return Error $"Failed to open new tab: {ex.Message}"
    }

let private killByHwnd (hwnd: nativeint) =
    if Win32.isWindowValid hwnd then
        if Win32.closeWindow hwnd then
            Log.log "SessionManager" $"Closed window HWND={hwnd}"
        else
            Log.log "SessionManager" $"Failed to close window HWND={hwnd}"

let private validateSessions (sessions: Map<string, nativeint>) =
    sessions
    |> Map.filter (fun _ hwnd -> Win32.isWindowValid hwnd)

let private spawnAndTrack (validated: Map<string, nativeint>) path (spawnFn: unit -> Async<Result<nativeint, string>>) (reply: AsyncReplyChannel<Result<unit, string>>) =
    async {
        match validated |> Map.tryFind path with
        | Some existingHwnd -> killByHwnd existingHwnd
        | None -> ()

        let! result = spawnFn ()

        match result with
        | Ok hwnd ->
            reply.Reply(Ok())
            return validated |> Map.add path hwnd
        | Error msg ->
            reply.Reply(Error msg)
            return validated |> Map.remove path
    }

let private sessionsFilePath =
    Path.Combine("data", "sessions.json")

let internal persistSessions (sessions: Map<string, nativeint>) =
    async {
        try
            let dir = Path.GetDirectoryName(sessionsFilePath)
            if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore

            let options = JsonWriterOptions(Indented = true)
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, options)
            writer.WriteStartObject()
            writer.WritePropertyName("sessions")
            writer.WriteStartObject()
            sessions |> Map.iter (fun path hwnd -> writer.WriteNumber(path, int64 hwnd))
            writer.WriteEndObject()
            writer.WriteEndObject()
            writer.Flush()

            let json = System.Text.Encoding.UTF8.GetString(stream.ToArray())
            let tempPath = sessionsFilePath + ".tmp"
            do! File.WriteAllTextAsync(tempPath, json) |> Async.AwaitTask
            File.Move(tempPath, sessionsFilePath, overwrite = true)
        with ex ->
            Log.log "SessionManager" $"Failed to persist sessions: {ex.Message}"
    }

let internal loadSessions () =
    try
        if not (File.Exists(sessionsFilePath)) then
            Map.empty
        else
            let json = File.ReadAllText(sessionsFilePath)
            use doc = JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty("sessions") with
            | false, _ -> Map.empty
            | true, sessionsElement ->
                sessionsElement.EnumerateObject()
                |> Seq.fold (fun acc prop ->
                    let hwnd = nativeint (prop.Value.GetInt64())
                    if Win32.isWindowValid hwnd then
                        acc |> Map.add prop.Name hwnd
                    else
                        acc
                ) Map.empty
    with ex ->
        Log.log "SessionManager" $"Failed to load sessions: {ex.Message}"
        Map.empty

let private replyError (msg: SessionMsg) (sessions: Map<string, nativeint>) (ex: exn) =
    match msg with
    | Spawn(_, _, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
    | SpawnTerminal(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
    | OpenNewTab(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
    | LaunchAction(_, _, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
    | Focus(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
    | Kill(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
    | GetActiveSessions reply -> reply.Reply(sessions)

let private processMessage (sessions: Map<string, nativeint>) (msg: SessionMsg) =
    async {
        match msg with
        | Spawn(wtPath, command, reply) ->
            let path = WorktreePath.value wtPath
            return! spawnAndTrack (validateSessions sessions) path (fun () -> spawnAndResolve path command) reply

        | SpawnTerminal(wtPath, reply) ->
            let path = WorktreePath.value wtPath
            return! spawnAndTrack (validateSessions sessions) path (fun () -> spawnTerminalAndResolve path) reply

        | OpenNewTab(wtPath, reply) ->
            let path = WorktreePath.value wtPath
            let validated = validateSessions sessions

            match validated |> Map.tryFind path with
            | Some hwnd ->
                let! result = openNewTabInWindow hwnd path None
                reply.Reply(result)
                return validated
            | None ->
                reply.Reply(Error "No active session for this worktree")
                return validated

        | LaunchAction(wtPath, command, reply) ->
            let path = WorktreePath.value wtPath
            let validated = validateSessions sessions

            match validated |> Map.tryFind path with
            | Some hwnd ->
                let! result = openNewTabInWindow hwnd path (Some command)
                reply.Reply(result)
                return validated
            | None ->
                return! spawnAndTrack validated path (fun () -> spawnWithCommand path (Some command) "action") reply

        | Focus(wtPath, reply) ->
            let path = WorktreePath.value wtPath
            let validated = validateSessions sessions

            match validated |> Map.tryFind path with
            | Some hwnd ->
                if Win32.focusWindow hwnd then
                    reply.Reply(Ok())
                else
                    reply.Reply(Error "SetForegroundWindow failed")

                return validated
            | None ->
                reply.Reply(Error "No active session for this worktree")
                return validated

        | Kill(wtPath, reply) ->
            let path = WorktreePath.value wtPath
            let validated = validateSessions sessions

            match validated |> Map.tryFind path with
            | Some hwnd ->
                killByHwnd hwnd
                reply.Reply(Ok())
                return validated |> Map.remove path
            | None ->
                reply.Reply(Error "No active session for this worktree")
                return validated

        | GetActiveSessions reply ->
            let validated = validateSessions sessions
            reply.Reply(validated)
            return validated
    }

let createAgent () =
    let initialSessions = loadSessions ()
    Log.log "SessionManager" $"Loaded {initialSessions.Count} persisted session(s)"

    let agent =
        MailboxProcessor<SessionMsg>.Start(fun inbox ->
            let rec loop (sessions: Map<string, nativeint>) =
                async {
                    let! msg = inbox.Receive()

                    let! newSessions =
                        async {
                            try
                                return! processMessage sessions msg
                            with ex ->
                                Log.log "SessionManager" $"processMessage crashed: {ex}"
                                replyError msg sessions ex
                                return sessions
                        }

                    if newSessions <> sessions then
                        do! persistSessions newSessions

                    return! loop newSessions
                }

            loop initialSessions)

    agent.Error.Add(fun ex ->
        Log.log "SessionManager" $"MailboxProcessor error: {ex.Message}")

    { Agent = agent }

let spawnSession (agent: SessionAgent) (worktreePath: WorktreePath) (command: string) =
    agent.Agent.PostAndAsyncReply((fun reply -> Spawn(worktreePath, command, reply)), timeout = 30_000)

let spawnTerminal (agent: SessionAgent) (worktreePath: WorktreePath) =
    agent.Agent.PostAndAsyncReply((fun reply -> SpawnTerminal(worktreePath, reply)), timeout = 30_000)

let focusSession (agent: SessionAgent) (worktreePath: WorktreePath) =
    agent.Agent.PostAndAsyncReply((fun reply -> Focus(worktreePath, reply)), timeout = 10_000)

let killSession (agent: SessionAgent) (worktreePath: WorktreePath) =
    agent.Agent.PostAndAsyncReply((fun reply -> Kill(worktreePath, reply)), timeout = 10_000)

let openNewTab (agent: SessionAgent) (worktreePath: WorktreePath) =
    agent.Agent.PostAndAsyncReply((fun reply -> OpenNewTab(worktreePath, reply)), timeout = 10_000)

let launchAction (agent: SessionAgent) (worktreePath: WorktreePath) (command: string) =
    agent.Agent.PostAndAsyncReply((fun reply -> LaunchAction(worktreePath, command, reply)), timeout = 30_000)

let getActiveSessions (agent: SessionAgent) =
    agent.Agent.PostAndAsyncReply(GetActiveSessions, timeout = 10_000)
