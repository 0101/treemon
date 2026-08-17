module Server.EmbeddedTerminal

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading
open Shared

type internal Config =
    { ExecutablePath: string
      ShellCommand: string
      ShellPrefixArguments: string list
      PrefixArguments: string list
      StartupTimeout: TimeSpan
      ProbeInterval: TimeSpan }

type private OwnedProcess =
    { Process: Process
      Stdout: Threading.Tasks.Task<string>
      Stderr: Threading.Tasks.Task<string> }

type private ManagerState =
    { Public: EmbeddedTerminalState
      Generation: int
      Starting: CancellationTokenSource option
      Running: OwnedProcess option }

type private Message =
    | Start of WorktreePath * AsyncReplyChannel<EmbeddedTerminalState>
    | LaunchCompleted of int * WorktreePath * Result<string * OwnedProcess, string>
    | ProcessExited of int * WorktreePath * int * string
    | Get of AsyncReplyChannel<EmbeddedTerminalState>
    | Close of AsyncReplyChannel<EmbeddedTerminalState>

type Manager = private Manager of MailboxProcessor<Message>

let private defaultConfig () =
    { ExecutablePath =
        Path.Combine(
            Directory.GetCurrentDirectory(),
            ".tools",
            "ttyd",
            "1.7.7",
            "ttyd.exe"
        )
      ShellCommand = "pwsh"
      ShellPrefixArguments = []
      PrefixArguments = []
      StartupTimeout = TimeSpan.FromSeconds 10.0
      ProbeInterval = TimeSpan.FromMilliseconds 100.0 }

let private reserveLoopbackPort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port

let private stopOwnedProcess (owned: OwnedProcess) =
    async {
        try
            try
                if not owned.Process.HasExited then
                    owned.Process.Kill(entireProcessTree = true)

                    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

                    try
                        do!
                            owned.Process.WaitForExitAsync(timeout.Token)
                            |> Async.AwaitTask
                    with :? OperationCanceledException ->
                        Log.log "EmbeddedTerminal" $"Timed out waiting for owned PID {owned.Process.Id} to exit"
            with ex ->
                Log.log "EmbeddedTerminal" $"Failed to stop owned PID {owned.Process.Id}: {ex.Message}"
        finally
            owned.Process.Dispose()
    }

let private tryReadOutput (output: Threading.Tasks.Task<string>) =
    if output.IsCompletedSuccessfully then output.Result.Trim()
    else ""

let private startupFailure (owned: OwnedProcess) message =
    async {
        let stderr = tryReadOutput owned.Stderr
        let detail = if String.IsNullOrWhiteSpace stderr then message else $"{message}: {stderr}"
        do! stopOwnedProcess owned
        return Error detail
    }

let private probeUntilReady
    (config: Config)
    (endpoint: string)
    (owned: OwnedProcess)
    (cancellationToken: CancellationToken)
    =
    async {
        use client = new HttpClient(Timeout = TimeSpan.FromSeconds 1.0)
        let deadline = DateTime.UtcNow + config.StartupTimeout

        let rec probe () =
            async {
                try
                    cancellationToken.ThrowIfCancellationRequested()

                    if owned.Process.HasExited then
                        return! startupFailure owned $"ttyd exited with code {owned.Process.ExitCode}"
                    elif DateTime.UtcNow >= deadline then
                        return! startupFailure owned "Timed out waiting for ttyd to become ready"
                    else
                        use! response =
                            client.GetAsync(endpoint, cancellationToken)
                            |> Async.AwaitTask

                        if int response.StatusCode < 500 then
                            return Ok(endpoint, owned)
                        else
                            do! Async.Sleep config.ProbeInterval
                            return! probe ()
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    do! stopOwnedProcess owned
                    return Error "Terminal startup was cancelled"
                | _ ->
                    do! Async.Sleep config.ProbeInterval
                    return! probe ()
            }

        return! probe ()
    }

let private launch (config: Config) (worktreePath: WorktreePath) (cancellationToken: CancellationToken) =
    async {
        try
            let path = WorktreePath.value worktreePath
            let port = reserveLoopbackPort ()
            let endpoint = $"http://127.0.0.1:{port}/"
            let encoded =
                SessionManager.buildScript path None
                |> SessionManager.encodeCommand

            let psi =
                ProcessStartInfo(
                    FileName = config.ExecutablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            config.PrefixArguments
            @ [ "-p"
                string port
                "-i"
                "127.0.0.1"
                "-W"
                "-O"
                "-w"
                path
                config.ShellCommand ]
            @ config.ShellPrefixArguments
            @ [ "-WorkingDirectory"
                path
                "-NoExit"
                "-EncodedCommand"
                encoded ]
            |> List.iter psi.ArgumentList.Add

            let proc = new Process(StartInfo = psi)

            if not (proc.Start()) then
                proc.Dispose()
                return Error "ttyd did not start"
            else
                let owned =
                    { Process = proc
                      Stdout = proc.StandardOutput.ReadToEndAsync()
                      Stderr = proc.StandardError.ReadToEndAsync() }

                Log.log "EmbeddedTerminal" $"Started owned ttyd PID {proc.Id} for '{path}'"
                return! probeUntilReady config endpoint owned cancellationToken
        with
        | :? OperationCanceledException ->
            return Error "Terminal startup was cancelled"
        | ex ->
            return Error $"Failed to start ttyd: {ex.Message}"
    }

let private executableIsMissing (path: string) =
    let explicitlyPathed =
        Path.IsPathRooted path
        || path.Contains(Path.DirectorySeparatorChar)
        || path.Contains(Path.AltDirectorySeparatorChar)

    explicitlyPathed && not (File.Exists path)

let internal createWithConfig (config: Config) =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop state =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Get reply ->
                        reply.Reply state.Public
                        return! loop state
                    | Start(worktreePath, reply) ->
                        match state.Public with
                        | EmbeddedTerminalState.Starting _
                        | EmbeddedTerminalState.Running _ ->
                            reply.Reply state.Public
                            return! loop state
                        | EmbeddedTerminalState.Closed
                        | EmbeddedTerminalState.Failed _ ->
                            if executableIsMissing config.ExecutablePath then
                                let failed =
                                    EmbeddedTerminalState.Failed(
                                        worktreePath,
                                        $"ttyd is not installed at '{config.ExecutablePath}'. Run '.\\treemon.ps1 setup-ttyd'."
                                    )

                                reply.Reply failed
                                return! loop { state with Public = failed }
                            else
                                let generation = state.Generation + 1
                                let cts = new CancellationTokenSource()
                                let starting = EmbeddedTerminalState.Starting worktreePath
                                reply.Reply starting

                                Async.Start(
                                    async {
                                        let! result = launch config worktreePath cts.Token
                                        inbox.Post(LaunchCompleted(generation, worktreePath, result))
                                    }
                                )

                                return!
                                    loop
                                        { Public = starting
                                          Generation = generation
                                          Starting = Some cts
                                          Running = None }
                    | LaunchCompleted(generation, worktreePath, result) ->
                        if generation <> state.Generation then
                            match result with
                            | Ok(_, owned) -> do! stopOwnedProcess owned
                            | Error _ -> ()

                            return! loop state
                        else
                            state.Starting |> Option.iter _.Dispose()

                            match result with
                            | Ok(endpoint, owned) ->
                                let running =
                                    EmbeddedTerminalState.Running(worktreePath, endpoint)

                                Async.Start(
                                    async {
                                        try
                                            do! owned.Process.WaitForExitAsync() |> Async.AwaitTask
                                            let stderr = tryReadOutput owned.Stderr
                                            inbox.Post(
                                                ProcessExited(
                                                    generation,
                                                    worktreePath,
                                                    owned.Process.ExitCode,
                                                    stderr
                                                )
                                            )
                                        with :? ObjectDisposedException ->
                                            ()
                                    }
                                )

                                return!
                                    loop
                                        { state with
                                            Public = running
                                            Starting = None
                                            Running = Some owned }
                            | Error error ->
                                let failed =
                                    EmbeddedTerminalState.Failed(worktreePath, error)

                                return!
                                    loop
                                        { state with
                                            Public = failed
                                            Starting = None
                                            Running = None }
                    | ProcessExited(generation, worktreePath, exitCode, stderr) ->
                        if generation <> state.Generation then
                            return! loop state
                        else
                            state.Running |> Option.iter (_.Process.Dispose())
                            let baseError = $"ttyd exited with code {exitCode}"
                            let error =
                                if String.IsNullOrWhiteSpace stderr then baseError
                                else $"{baseError}: {stderr.Trim()}"

                            return!
                                loop
                                    { state with
                                        Public = EmbeddedTerminalState.Failed(worktreePath, error)
                                        Running = None }
                    | Close reply ->
                        state.Starting
                        |> Option.iter (fun cts ->
                            cts.Cancel()
                            cts.Dispose())

                        match state.Running with
                        | Some owned -> do! stopOwnedProcess owned
                        | None -> ()

                        let closed = EmbeddedTerminalState.Closed
                        reply.Reply closed

                        return!
                            loop
                                { Public = closed
                                  Generation = state.Generation + 1
                                  Starting = None
                                  Running = None }
                }

            loop
                { Public = EmbeddedTerminalState.Closed
                  Generation = 0
                  Starting = None
                  Running = None })

    Manager agent

let create () = createWithConfig (defaultConfig ())

let start (Manager agent) worktreePath =
    agent.PostAndAsyncReply(fun reply -> Start(worktreePath, reply))

let get (Manager agent) =
    agent.PostAndAsyncReply Get

let close (Manager agent) =
    agent.PostAndAsyncReply Close
