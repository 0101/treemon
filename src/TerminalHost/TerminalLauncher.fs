namespace TerminalHost

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

type TerminalLaunchConfig =
    { TtydExecutable: string
      ShellCommand: string
      StartupTimeout: TimeSpan }

[<RequireQualifiedAccess>]
module TerminalLauncher =
    let rec private freeLoopbackPort () =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let endpoint = listener.LocalEndpoint :?> IPEndPoint

        if endpoint.Port = 5000 then
            freeLoopbackPort ()
        else
            endpoint.Port

    let internal startSpecification config sessionId worktree port =
        let path = CanonicalWorktree.path worktree
        let shell = TerminalShell.forCurrentPlatform config.ShellCommand

        { Executable = config.TtydExecutable
          WorkingDirectory = path
          Environment =
            [ "TREEMON_TERMINAL_SESSION_ID", sessionId
              TerminalShell.WorktreeEnvironmentVariable, path ]
          Arguments =
            [ "-p"; string port; "-i"; "127.0.0.1"; "-W"; "-O"; "-o"
              "-t"; "fontSize=16"; "-t"; "disableLeaveAlert=true"
              "-w"; path
              TerminalShell.launchExecutable shell
              yield! TerminalShell.arguments shell ] }

    let private canConnect port =
        task {
            use client = new TcpClient()
            use cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds 250.0)

            try
                do! client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token).AsTask()
                return client.Connected
            with
            | :? SocketException
            | :? OperationCanceledException ->
                return false
        }

    let private waitUntilReady executable timeout port owned =
        let deadline = DateTimeOffset.UtcNow + timeout

        let rec wait () =
            async {
                if JobProcess.hasExited owned then
                    match JobProcess.exitCode owned with
                    | Ok exitCode ->
                        return
                            Error
                                $"ttyd '{executable}' exited with code {exitCode} before binding loopback port {port}"
                    | Error error ->
                        return
                            Error
                                $"ttyd '{executable}' exited before binding loopback port {port}: {error}"
                elif DateTimeOffset.UtcNow >= deadline then
                    return
                        Error
                            $"Timed out waiting for ttyd '{executable}' to bind loopback port {port}"
                else
                    let! ready = canConnect port |> Async.AwaitTask

                    if ready then
                        return Ok()
                    else
                        do! Async.Sleep 50
                        return! wait ()
            }

        wait ()

    let start config sessionId worktree =
        async {
            let port = freeLoopbackPort ()
            let specification = startSpecification config sessionId worktree port

            match JobProcess.start specification with
            | Error error -> return Error error
            | Ok owned ->
                match!
                    waitUntilReady
                        config.TtydExecutable
                        config.StartupTimeout
                        port
                        owned
                with
                | Error error ->
                    JobProcess.close owned
                    return Error error
                | Ok() ->
                    return
                        Ok
                            { ProcessId = JobProcess.processId owned
                              ProcessStartTimeUtcTicks =
                                JobProcess.processStartTimeUtcTicks owned
                              TtydPort = port
                              HasExited = fun () -> JobProcess.hasExited owned
                              Close = fun () -> JobProcess.close owned }
        }
