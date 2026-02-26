module Server.SessionManager

open System.Diagnostics

type private SessionMsg =
    | Spawn of worktreePath: string * prompt: string * AsyncReplyChannel<Result<unit, string>>
    | SpawnTerminal of worktreePath: string * AsyncReplyChannel<Result<unit, string>>
    | SpawnTerminalCmd of worktreePath: string * command: string * AsyncReplyChannel<Result<unit, string>>
    | Focus of worktreePath: string * AsyncReplyChannel<Result<unit, string>>
    | Kill of worktreePath: string * AsyncReplyChannel<Result<unit, string>>
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

let private spawnAndResolve (worktreePath: string) (prompt: string) =
    let beforeWindows = Win32.listWindowsTerminalWindows () |> Set.ofList
    let escapedPrompt = prompt.Replace("\"", "\\\"")

    let args = $"--window new new-tab -d \"{worktreePath}\" -- claude \"{escapedPrompt}\""
    Log.log "SessionManager" $"Spawning: wt.exe {args}"

    let psi =
        ProcessStartInfo(
            "wt.exe",
            args,
            UseShellExecute = false,
            CreateNoWindow = true)

    try
        let wtProcess = Process.Start(psi)
        Log.log "SessionManager" $"wt.exe started, PID={wtProcess.Id}"
        wtProcess.WaitForExit(5_000) |> ignore
        Log.log "SessionManager" $"wt.exe exited={wtProcess.HasExited} for {worktreePath}"

        match resolveNewHwnd beforeWindows 10_000 with
        | Some hwnd ->
            Log.log "SessionManager" $"HWND {hwnd} resolved for {worktreePath}"
            Ok hwnd
        | None ->
            Log.log "SessionManager" $"Failed to resolve HWND for {worktreePath}"
            Error "Failed to detect new window after spawn"
    with ex ->
        Log.log "SessionManager" $"Failed to spawn wt.exe: {ex.Message}"
        Error $"Failed to spawn: {ex.Message}"

let private spawnTerminalAndResolve (worktreePath: string) =
    let beforeWindows = Win32.listWindowsTerminalWindows () |> Set.ofList

    let args = $"--window new new-tab -d \"{worktreePath}\""
    Log.log "SessionManager" $"Spawning terminal: wt.exe {args}"

    let psi =
        ProcessStartInfo(
            "wt.exe",
            args,
            UseShellExecute = false,
            CreateNoWindow = true)

    try
        let wtProcess = Process.Start(psi)
        Log.log "SessionManager" $"wt.exe terminal started, PID={wtProcess.Id}"
        wtProcess.WaitForExit(5_000) |> ignore
        Log.log "SessionManager" $"wt.exe terminal exited={wtProcess.HasExited} for {worktreePath}"

        match resolveNewHwnd beforeWindows 10_000 with
        | Some hwnd ->
            Log.log "SessionManager" $"Terminal HWND {hwnd} resolved for {worktreePath}"
            Ok hwnd
        | None ->
            Log.log "SessionManager" $"Failed to resolve terminal HWND for {worktreePath}"
            Error "Failed to detect new terminal window after spawn"
    with ex ->
        Log.log "SessionManager" $"Failed to spawn terminal wt.exe: {ex.Message}"
        Error $"Failed to spawn terminal: {ex.Message}"

let private spawnTerminalWithCommandAndResolve (worktreePath: string) (command: string) =
    let beforeWindows = Win32.listWindowsTerminalWindows () |> Set.ofList

    let args = $"--window new new-tab -d \"{worktreePath}\" -- pwsh -NoProfile -Command \"{command}\""
    Log.log "SessionManager" $"Spawning terminal+cmd: wt.exe {args}"

    let psi =
        ProcessStartInfo(
            "wt.exe",
            args,
            UseShellExecute = false,
            CreateNoWindow = true)

    try
        let wtProcess = Process.Start(psi)
        Log.log "SessionManager" $"wt.exe terminal+cmd started, PID={wtProcess.Id}"
        wtProcess.WaitForExit(5_000) |> ignore
        Log.log "SessionManager" $"wt.exe terminal+cmd exited={wtProcess.HasExited} for {worktreePath}"

        match resolveNewHwnd beforeWindows 10_000 with
        | Some hwnd ->
            Log.log "SessionManager" $"Terminal+cmd HWND {hwnd} resolved for {worktreePath}"
            Ok hwnd
        | None ->
            Log.log "SessionManager" $"Failed to resolve terminal+cmd HWND for {worktreePath}"
            Error "Failed to detect new terminal window after spawn"
    with ex ->
        Log.log "SessionManager" $"Failed to spawn terminal+cmd wt.exe: {ex.Message}"
        Error $"Failed to spawn terminal+cmd: {ex.Message}"

let private killByHwnd (hwnd: nativeint) =
    if Win32.isWindowValid hwnd then
        if Win32.closeWindow hwnd then
            Log.log "SessionManager" $"Closed window HWND={hwnd}"
        else
            Log.log "SessionManager" $"Failed to close window HWND={hwnd}"

let private validateSessions (sessions: Map<string, nativeint>) =
    sessions
    |> Map.filter (fun _ hwnd -> Win32.isWindowValid hwnd)

let private processMessage (sessions: Map<string, nativeint>) (msg: SessionMsg) =
    match msg with
    | Spawn(path, prompt, reply) ->
        let validated = validateSessions sessions

        match validated |> Map.tryFind path with
        | Some existingHwnd -> killByHwnd existingHwnd
        | None -> ()

        match spawnAndResolve path prompt with
        | Ok hwnd ->
            reply.Reply(Ok())
            validated |> Map.add path hwnd
        | Error msg ->
            reply.Reply(Error msg)
            validated |> Map.remove path

    | SpawnTerminal(path, reply) ->
        let validated = validateSessions sessions

        match validated |> Map.tryFind path with
        | Some existingHwnd -> killByHwnd existingHwnd
        | None -> ()

        match spawnTerminalAndResolve path with
        | Ok hwnd ->
            reply.Reply(Ok())
            validated |> Map.add path hwnd
        | Error msg ->
            reply.Reply(Error msg)
            validated |> Map.remove path

    | SpawnTerminalCmd(path, command, reply) ->
        let validated = validateSessions sessions

        match validated |> Map.tryFind path with
        | Some existingHwnd -> killByHwnd existingHwnd
        | None -> ()

        match spawnTerminalWithCommandAndResolve path command with
        | Ok hwnd ->
            reply.Reply(Ok())
            validated |> Map.add path hwnd
        | Error msg ->
            reply.Reply(Error msg)
            validated |> Map.remove path

    | Focus(path, reply) ->
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

    | Kill(path, reply) ->
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
                            | SpawnTerminalCmd(_, _, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | Focus(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | Kill(_, reply) -> reply.Reply(Error $"Internal error: {ex.Message}")
                            | GetActiveSessions reply -> reply.Reply(sessions)

                            sessions

                    return! loop newSessions
                }

            loop Map.empty)

    agent.Error.Add(fun ex ->
        Log.log "SessionManager" $"MailboxProcessor error: {ex.Message}")

    { Agent = agent }

let spawnSession (agent: SessionAgent) (worktreePath: string) (prompt: string) =
    agent.Agent.PostAndAsyncReply((fun reply -> Spawn(worktreePath, prompt, reply)), timeout = 30_000)

let spawnTerminal (agent: SessionAgent) (worktreePath: string) =
    agent.Agent.PostAndAsyncReply((fun reply -> SpawnTerminal(worktreePath, reply)), timeout = 30_000)

let spawnTerminalWithCommand (agent: SessionAgent) (worktreePath: string) (command: string) =
    agent.Agent.PostAndAsyncReply((fun reply -> SpawnTerminalCmd(worktreePath, command, reply)), timeout = 30_000)

let focusSession (agent: SessionAgent) (worktreePath: string) =
    agent.Agent.PostAndAsyncReply((fun reply -> Focus(worktreePath, reply)), timeout = 10_000)

let killSession (agent: SessionAgent) (worktreePath: string) =
    agent.Agent.PostAndAsyncReply((fun reply -> Kill(worktreePath, reply)), timeout = 10_000)

let getActiveSessions (agent: SessionAgent) =
    agent.Agent.PostAndAsyncReply(GetActiveSessions, timeout = 10_000)
