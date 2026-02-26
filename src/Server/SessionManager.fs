module Server.SessionManager

open System.Diagnostics

type private SessionMsg =
    | Spawn of worktreePath: string * prompt: string * AsyncReplyChannel<Result<unit, string>>
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

    let psi =
        ProcessStartInfo(
            "wt.exe",
            $"--window new -d \"{worktreePath}\" -- claude \"{escapedPrompt}\"",
            UseShellExecute = false,
            CreateNoWindow = true)

    try
        let wtProcess = Process.Start(psi)
        wtProcess.WaitForExit(10_000) |> ignore
        Log.log "SessionManager" $"wt.exe launched for {worktreePath}"

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

let private killByHwnd (hwnd: nativeint) =
    if Win32.isWindowValid hwnd then
        let pid = Win32.getWindowPid hwnd

        try
            let proc = Process.GetProcessById(pid)
            proc.Kill(entireProcessTree = true)
            Log.log "SessionManager" $"Killed process PID={pid} (HWND={hwnd})"
        with ex ->
            Log.log "SessionManager" $"Failed to kill PID={pid}: {ex.Message}"

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
                    let newSessions = processMessage sessions msg
                    return! loop newSessions
                }

            loop Map.empty)

    { Agent = agent }

let spawnSession (agent: SessionAgent) (worktreePath: string) (prompt: string) =
    agent.Agent.PostAndAsyncReply(fun reply -> Spawn(worktreePath, prompt, reply))

let focusSession (agent: SessionAgent) (worktreePath: string) =
    agent.Agent.PostAndAsyncReply(fun reply -> Focus(worktreePath, reply))

let killSession (agent: SessionAgent) (worktreePath: string) =
    agent.Agent.PostAndAsyncReply(fun reply -> Kill(worktreePath, reply))

let getActiveSessions (agent: SessionAgent) =
    agent.Agent.PostAndAsyncReply(GetActiveSessions)
