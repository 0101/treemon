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

type private EntryResource =
    | Launching of CancellationTokenSource
    | Live of OwnedProcess
    | Inactive

type private RegistryEntry =
    { Public: EmbeddedTerminalTab
      Generation: int64
      Order: int64
      Resource: EntryResource }

type private PendingLaunchClose =
    { Cancellation: CancellationTokenSource
      Reply: AsyncReplyChannel<EmbeddedTerminalSnapshot> option }

type private RegistryState =
    { Entries: Map<string, RegistryEntry>
      PendingLaunchCloses: Map<int64, PendingLaunchClose>
      PendingStops: int
      NextGeneration: int64
      NextOrder: int64
      ShuttingDown: bool
      ShutdownReplies: AsyncReplyChannel<unit> list }

type private Message =
    | Start of WorktreePath * AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | LaunchCompleted of key: string * generation: int64 * Result<string * OwnedProcess, string>
    | ProcessExited of key: string * generation: int64 * exitCode: int * stderr: string
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | Close of WorktreePath * AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | CleanupCompleted of AsyncReplyChannel<EmbeddedTerminalSnapshot> option
    | CloseAll of AsyncReplyChannel<unit>

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
                "Set-Location -LiteralPath $env:TREEMON_TERMINAL_WORKTREE"
                |> SessionManager.encodeCommand

            let psi =
                ProcessStartInfo(
                    FileName = config.ExecutablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            psi.Environment["TREEMON_TERMINAL_WORKTREE"] <- path

            config.PrefixArguments
            @ [ "-p"
                string port
                "-i"
                "127.0.0.1"
                "-W"
                "-O"
                "-t"
                "fontSize=16"
                "-w"
                path
                config.ShellCommand ]
            @ config.ShellPrefixArguments
            @ [ "-WorkingDirectory"
                "."
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

let private snapshot state =
    { Tabs =
        state.Entries
        |> Map.values
        |> Seq.sortBy _.Order
        |> Seq.map _.Public
        |> Seq.toList }

let private canonicalWorktreePath =
    WorktreePath.value >> PathUtils.toWorktreePath

let private registryKey = WorktreePath.value

let internal createWithConfig (config: Config) =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let startCleanup owned reply =
                Async.Start(
                    async {
                        do! stopOwnedProcess owned
                        inbox.Post(CleanupCompleted reply)
                    }
                )

            let watchProcess key generation (owned: OwnedProcess) =
                Async.Start(
                    async {
                        try
                            do! owned.Process.WaitForExitAsync() |> Async.AwaitTask
                            let stderr = tryReadOutput owned.Stderr
                            inbox.Post(ProcessExited(key, generation, owned.Process.ExitCode, stderr))
                        with :? ObjectDisposedException ->
                            ()
                    }
                )

            let finishShutdown state =
                if
                    state.ShuttingDown
                    && Map.isEmpty state.Entries
                    && Map.isEmpty state.PendingLaunchCloses
                    && state.PendingStops = 0
                then
                    state.ShutdownReplies
                    |> List.iter (fun reply -> reply.Reply ())

                    { state with ShutdownReplies = [] }
                else
                    state

            let discardLaunchResult state result =
                match result with
                | Ok(_, owned) ->
                    startCleanup owned None
                    { state with PendingStops = state.PendingStops + 1 }
                | Error _ -> state

            let rec loop state =
                async {
                    let! message = inbox.Receive()

                    try
                        match message with
                        | Get reply ->
                            reply.Reply(snapshot state)
                            return! loop state
                        | Start(worktreePath, reply) when state.ShuttingDown ->
                            reply.Reply(Error "Embedded terminal manager is shutting down")
                            return! loop state
                        | Start(worktreePath, reply) ->
                            let key = registryKey worktreePath
                            let existing = state.Entries |> Map.tryFind key

                            match existing |> Option.map _.Resource with
                            | Some (Launching _)
                            | Some (Live _) ->
                                reply.Reply(Ok(snapshot state))
                                return! loop state
                            | Some Inactive
                            | None ->
                                let generation = state.NextGeneration + 1L
                                let order, nextOrder =
                                    match existing with
                                    | Some entry -> entry.Order, state.NextOrder
                                    | None -> state.NextOrder, state.NextOrder + 1L

                                if executableIsMissing config.ExecutablePath then
                                    let entry =
                                        { Public =
                                            { Worktree = worktreePath
                                              Lifecycle =
                                                EmbeddedTerminalLifecycle.Failed(
                                                    $"ttyd is not installed at '{config.ExecutablePath}'. Run '.\\treemon.ps1 setup-ttyd'."
                                                ) }
                                          Generation = generation
                                          Order = order
                                          Resource = Inactive }

                                    let next =
                                        { state with
                                            Entries = state.Entries |> Map.add key entry
                                            NextGeneration = generation
                                            NextOrder = nextOrder }

                                    reply.Reply(Ok(snapshot next))
                                    return! loop next
                                else
                                    let cancellation = new CancellationTokenSource()
                                    let entry =
                                        { Public =
                                            { Worktree = worktreePath
                                              Lifecycle = EmbeddedTerminalLifecycle.Starting }
                                          Generation = generation
                                          Order = order
                                          Resource = Launching cancellation }

                                    let next =
                                        { state with
                                            Entries = state.Entries |> Map.add key entry
                                            NextGeneration = generation
                                            NextOrder = nextOrder }

                                    reply.Reply(Ok(snapshot next))

                                    Async.Start(
                                        async {
                                            let! result = launch config worktreePath cancellation.Token
                                            inbox.Post(LaunchCompleted(key, generation, result))
                                        }
                                    )

                                    return! loop next
                        | LaunchCompleted(key, generation, result) ->
                            match state.PendingLaunchCloses |> Map.tryFind generation with
                            | Some pending ->
                                pending.Cancellation.Dispose()

                                let next =
                                    { state with
                                        PendingLaunchCloses =
                                            state.PendingLaunchCloses
                                            |> Map.remove generation }

                                match result with
                                | Ok(_, owned) ->
                                    startCleanup owned pending.Reply

                                    return!
                                        loop (
                                            finishShutdown
                                                { next with
                                                    PendingStops = next.PendingStops + 1 }
                                        )
                                | Error _ ->
                                    pending.Reply
                                    |> Option.iter (fun reply -> reply.Reply(snapshot next))

                                    return! loop (finishShutdown next)
                            | None ->
                                match state.Entries |> Map.tryFind key with
                                | Some entry when entry.Generation = generation ->
                                    match entry.Resource with
                                    | Launching cancellation ->
                                        cancellation.Dispose()

                                        match result with
                                        | Ok(endpoint, owned) ->
                                            watchProcess key generation owned

                                            let running =
                                                { entry with
                                                    Public.Lifecycle =
                                                        EmbeddedTerminalLifecycle.Running endpoint
                                                    Resource = Live owned }

                                            return!
                                                loop
                                                    { state with
                                                        Entries =
                                                            state.Entries
                                                            |> Map.add key running }
                                        | Error error ->
                                            let failed =
                                                { entry with
                                                    Public.Lifecycle =
                                                        EmbeddedTerminalLifecycle.Failed error
                                                    Resource = Inactive }

                                            return!
                                                loop
                                                    { state with
                                                        Entries =
                                                            state.Entries
                                                            |> Map.add key failed }
                                    | Live _
                                    | Inactive ->
                                        return!
                                            loop (
                                                result
                                                |> discardLaunchResult state
                                                |> finishShutdown
                                            )
                                | None
                                | Some _ ->
                                    return!
                                        loop (
                                            result
                                            |> discardLaunchResult state
                                            |> finishShutdown
                                        )
                        | ProcessExited(key, generation, exitCode, stderr) ->
                            match state.Entries |> Map.tryFind key with
                            | Some entry when entry.Generation = generation ->
                                match entry.Resource with
                                | Live owned ->
                                    owned.Process.Dispose()
                                    let baseError = $"ttyd exited with code {exitCode}"
                                    let error =
                                        if String.IsNullOrWhiteSpace stderr then baseError
                                        else $"{baseError}: {stderr.Trim()}"

                                    let failed =
                                        { entry with
                                            Public.Lifecycle =
                                                EmbeddedTerminalLifecycle.Failed error
                                            Resource = Inactive }

                                    return!
                                        loop
                                            { state with
                                                Entries =
                                                    state.Entries
                                                    |> Map.add key failed }
                                | Launching _
                                | Inactive ->
                                    return! loop state
                            | None
                            | Some _ ->
                                return! loop state
                        | Close(worktreePath, reply) ->
                            let key = registryKey worktreePath

                            match state.Entries |> Map.tryFind key with
                            | None ->
                                reply.Reply(snapshot state)
                                return! loop state
                            | Some entry ->
                                let next =
                                    { state with
                                        Entries = state.Entries |> Map.remove key }

                                match entry.Resource with
                                | Inactive ->
                                    reply.Reply(snapshot next)
                                    return! loop (finishShutdown next)
                                | Live owned ->
                                    startCleanup owned (Some reply)

                                    return!
                                        loop (
                                            finishShutdown
                                                { next with
                                                    PendingStops = next.PendingStops + 1 }
                                        )
                                | Launching cancellation ->
                                    cancellation.Cancel()

                                    let pending =
                                        { Cancellation = cancellation
                                          Reply = Some reply }

                                    return!
                                        loop
                                            { next with
                                                PendingLaunchCloses =
                                                    next.PendingLaunchCloses
                                                    |> Map.add entry.Generation pending }
                        | CleanupCompleted reply ->
                            let next =
                                { state with
                                    PendingStops = max 0 (state.PendingStops - 1) }

                            reply
                            |> Option.iter (fun channel -> channel.Reply(snapshot next))

                            return! loop (finishShutdown next)
                        | CloseAll reply ->
                            let entries = state.Entries |> Map.values |> Seq.toList

                            let launching =
                                entries
                                |> List.choose (fun entry ->
                                    match entry.Resource with
                                    | Launching cancellation ->
                                        Some(entry.Generation, cancellation)
                                    | Live _
                                    | Inactive ->
                                        None)

                            launching
                            |> List.iter (fun (_, cancellation) -> cancellation.Cancel())

                            let pendingLaunchCloses =
                                launching
                                |> List.fold
                                    (fun pending (generation, cancellation) ->
                                        pending
                                        |> Map.add
                                            generation
                                            { Cancellation = cancellation
                                              Reply = None })
                                    state.PendingLaunchCloses

                            let running =
                                entries
                                |> List.choose (fun entry ->
                                    match entry.Resource with
                                    | Live owned -> Some owned
                                    | Launching _
                                    | Inactive ->
                                        None)

                            running
                            |> List.iter (fun owned -> startCleanup owned None)

                            let next =
                                { state with
                                    Entries = Map.empty
                                    PendingLaunchCloses = pendingLaunchCloses
                                    PendingStops = state.PendingStops + running.Length
                                    ShuttingDown = true
                                    ShutdownReplies = reply :: state.ShutdownReplies }
                                |> finishShutdown

                            return! loop next
                    with ex ->
                        Log.log "EmbeddedTerminal" $"Registry message failed: {ex.Message}"

                        match message with
                        | Start(_, reply) ->
                            reply.Reply(Error $"Embedded terminal registry failed: {ex.Message}")
                        | Get reply ->
                            reply.Reply(snapshot state)
                        | Close(_, reply) ->
                            reply.Reply(snapshot state)
                        | CloseAll reply ->
                            reply.Reply ()
                        | LaunchCompleted _
                        | ProcessExited _
                        | CleanupCompleted _ ->
                            ()

                        return! loop state
                }

            loop
                { Entries = Map.empty
                  PendingLaunchCloses = Map.empty
                  PendingStops = 0
                  NextGeneration = 0L
                  NextOrder = 0L
                  ShuttingDown = false
                  ShutdownReplies = [] })

    Manager agent

let create () = createWithConfig (defaultConfig ())

let start (Manager agent) worktreePath =
    let canonical = canonicalWorktreePath worktreePath
    agent.PostAndAsyncReply((fun reply -> Start(canonical, reply)), timeout = 30_000)

let get (Manager agent) =
    agent.PostAndAsyncReply(Get, timeout = 30_000)

let close (Manager agent) worktreePath =
    let canonical = canonicalWorktreePath worktreePath
    agent.PostAndAsyncReply((fun reply -> Close(canonical, reply)), timeout = 30_000)

let closeAll (Manager agent) =
    agent.PostAndAsyncReply(CloseAll, timeout = 30_000)
