module Server.SessionManager

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Shared

type private SessionMsg =
    | Spawn of worktreePath: WorktreePath * prompt: string * AsyncReplyChannel<Result<unit, string>>
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
        if stopwatch.ElapsedMilliseconds > int64 timeoutMs then
            None
        else
            let newWindows =
                Win32.listWindowsTerminalWindows ()
                |> Set.ofList
                |> fun current -> Set.difference current beforeWindows

            if Set.isEmpty newWindows then
                System.Threading.Thread.Sleep(100)
                poll ()
            else
                Some(Set.minElement newWindows)

    poll ()

let private encodeCommand (command: string) =
    let bytes = System.Text.Encoding.Unicode.GetBytes(command)
    Convert.ToBase64String(bytes)

let private buildScript (nativePath: string) (command: string option) =
    match command with
    | Some cmd -> $"Set-Location '{nativePath}'; {cmd}"
    | None -> $"Set-Location '{nativePath}'"

let internal buildInteractiveCommand (provider: CodingToolProvider option) (prompt: string) =
    let escapedPrompt = prompt.Replace("'", "''")
    match provider |> Option.defaultValue CodingToolProvider.Claude with
    | CodingToolProvider.Claude -> $"claude --dangerously-skip-permissions '{escapedPrompt}'"
    | CodingToolProvider.Copilot -> $"copilot --yolo -i '{escapedPrompt}'"

let private spawnWtAndResolve (args: string) (logLabel: string) =
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
        wtProcess.WaitForExit(5_000) |> ignore
        Log.log "SessionManager" $"wt.exe {logLabel} exited={wtProcess.HasExited}"

        match resolveNewHwnd beforeWindows 10_000 with
        | Some hwnd ->
            Log.log "SessionManager" $"{logLabel} HWND {hwnd} resolved"
            Ok hwnd
        | None ->
            Log.log "SessionManager" $"Failed to resolve {logLabel} HWND"
            Error $"Failed to detect new window after {logLabel} spawn"
    with ex ->
        Log.log "SessionManager" $"Failed to spawn {logLabel} wt.exe: {ex.Message}"
        Error $"Failed to spawn {logLabel}: {ex.Message}"

let private spawnWithCommand (worktreePath: string) (command: string option) (logLabel: string) =
    let nativePath = worktreePath.Replace('/', Path.DirectorySeparatorChar)
    let encoded = buildScript nativePath command |> encodeCommand
    spawnWtAndResolve $"--window new -- pwsh -NoExit -EncodedCommand {encoded}" logLabel

let private spawnAndResolve (worktreePath: string) (prompt: string) =
    let command = buildInteractiveCommand (Some CodingToolProvider.Claude) prompt
    spawnWithCommand worktreePath (Some command) "session"

let private spawnTerminalAndResolve (worktreePath: string) =
    spawnWithCommand worktreePath None "terminal"

let private openNewTabInWindow (hwnd: nativeint) (worktreePath: string) (command: string option) =
    let nativePath = worktreePath.Replace('/', Path.DirectorySeparatorChar)
    if not (Win32.isWindowValid hwnd) then
        Error "Tracked window is no longer valid"
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
            p.WaitForExit(5_000) |> ignore
            Ok ()
        with ex ->
            Log.log "SessionManager" $"Failed to open new tab: {ex.Message}"
            Error $"Failed to open new tab: {ex.Message}"

let private killByHwnd (hwnd: nativeint) =
    if Win32.isWindowValid hwnd then
        if Win32.closeWindow hwnd then
            Log.log "SessionManager" $"Closed window HWND={hwnd}"
        else
            Log.log "SessionManager" $"Failed to close window HWND={hwnd}"

let private validateSessions (sessions: Map<string, nativeint>) =
    sessions
    |> Map.filter (fun _ hwnd -> Win32.isWindowValid hwnd)

let private sessionsFilePath =
    Path.Combine("data", "sessions.json")

let internal persistSessions (sessions: Map<string, nativeint>) =
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
        File.WriteAllText(tempPath, json)
        File.Move(tempPath, sessionsFilePath, overwrite = true)
    with ex ->
        Log.log "SessionManager" $"Failed to persist sessions: {ex.Message}"

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

let private spawnAndTrack (validated: Map<string, nativeint>) path spawnFn (reply: AsyncReplyChannel<Result<unit, string>>) =
    match validated |> Map.tryFind path with
    | Some existingHwnd -> killByHwnd existingHwnd
    | None -> ()

    match spawnFn () with
    | Ok hwnd ->
        reply.Reply(Ok())
        validated |> Map.add path hwnd
    | Error msg ->
        reply.Reply(Error msg)
        validated |> Map.remove path

let private processMessage (sessions: Map<string, nativeint>) (msg: SessionMsg) =
    match msg with
    | Spawn(wtPath, prompt, reply) ->
        let path = WorktreePath.value wtPath
        spawnAndTrack (validateSessions sessions) path (fun () -> spawnAndResolve path prompt) reply

    | SpawnTerminal(wtPath, reply) ->
        let path = WorktreePath.value wtPath
        spawnAndTrack (validateSessions sessions) path (fun () -> spawnTerminalAndResolve path) reply

    | OpenNewTab(wtPath, reply) ->
        let path = WorktreePath.value wtPath
        let validated = validateSessions sessions

        match validated |> Map.tryFind path with
        | Some hwnd ->
            let result = openNewTabInWindow hwnd path None
            reply.Reply(result)
            validated
        | None ->
            reply.Reply(Error "No active session for this worktree")
            validated

    | LaunchAction(wtPath, command, reply) ->
        let path = WorktreePath.value wtPath
        let validated = validateSessions sessions

        match validated |> Map.tryFind path with
        | Some hwnd ->
            let result = openNewTabInWindow hwnd path (Some command)
            reply.Reply(result)
            validated
        | None ->
            let spawnResult = spawnWithCommand path (Some command) "action"
            match spawnResult with
            | Ok hwnd ->
                reply.Reply(Ok())
                validated |> Map.add path hwnd
            | Error msg ->
                reply.Reply(Error msg)
                validated

    | Focus(wtPath, reply) ->
        let path = WorktreePath.value wtPath
        let validated = validateSessions sessions

        match validated |> Map.tryFind path with
        | Some hwnd ->
            if Win32.focusWindow hwnd then
                reply.Reply(Ok())
            else
                reply.Reply(Error "SetForegroundWindow failed")

            validated
        | None ->
            reply.Reply(Error "No active session for this worktree")
            validated

    | Kill(wtPath, reply) ->
        let path = WorktreePath.value wtPath
        let validated = validateSessions sessions

        match validated |> Map.tryFind path with
        | Some hwnd ->
            killByHwnd hwnd
            reply.Reply(Ok())
            validated |> Map.remove path
        | None ->
            reply.Reply(Error "No active session for this worktree")
            validated

    | GetActiveSessions reply ->
        let validated = validateSessions sessions
        reply.Reply(validated)
        validated

let createAgent () =
    let initialSessions = loadSessions ()
    Log.log "SessionManager" $"Loaded {initialSessions.Count} persisted session(s)"

    let agent =
        MailboxProcessor<SessionMsg>.Start(fun inbox ->
            let rec loop (sessions: Map<string, nativeint>) =
                async {
                    let! msg = inbox.Receive()

                    let newSessions =
                        try
                            processMessage sessions msg
                        with ex ->
                            Log.log "SessionManager" $"processMessage crashed: {ex}"

                            match msg with
                            | Spawn(_, _, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | SpawnTerminal(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | OpenNewTab(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | LaunchAction(_, _, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | Focus(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | Kill(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | GetActiveSessions reply -> reply.Reply(sessions)

                            sessions

                    if newSessions <> sessions then
                        persistSessions newSessions

                    return! loop newSessions
                }

            loop initialSessions)

    agent.Error.Add(fun ex ->
        Log.log "SessionManager" $"MailboxProcessor error: {ex.Message}")

    { Agent = agent }

let spawnSession (agent: SessionAgent) (worktreePath: WorktreePath) (prompt: string) =
    agent.Agent.PostAndAsyncReply((fun reply -> Spawn(worktreePath, prompt, reply)), timeout = 30_000)

let spawnTerminal (agent: SessionAgent) (worktreePath: WorktreePath) =
    agent.Agent.PostAndAsyncReply((fun reply -> SpawnTerminal(worktreePath, reply)), timeout = 30_000)

let focusSession (agent: SessionAgent) (worktreePath: WorktreePath) =
    agent.Agent.PostAndAsyncReply((fun reply -> Focus(worktreePath, reply)), timeout = 10_000)

let killSession (agent: SessionAgent) (worktreePath: WorktreePath) =
    agent.Agent.PostAndAsyncReply((fun reply -> Kill(worktreePath, reply)), timeout = 10_000)

let openNewTab (agent: SessionAgent) (worktreePath: WorktreePath) =
    agent.Agent.PostAndAsyncReply((fun reply -> OpenNewTab(worktreePath, reply)), timeout = 10_000)

let launchAction (agent: SessionAgent) (worktreePath: WorktreePath) (prompt: string) (provider: CodingToolProvider option) =
    let command = buildInteractiveCommand provider prompt
    agent.Agent.PostAndAsyncReply((fun reply -> LaunchAction(worktreePath, command, reply)), timeout = 30_000)

let getActiveSessions (agent: SessionAgent) =
    agent.Agent.PostAndAsyncReply(GetActiveSessions, timeout = 10_000)
