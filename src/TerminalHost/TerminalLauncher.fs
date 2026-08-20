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
    let private freeLoopbackPort () =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let endpoint = listener.LocalEndpoint :?> IPEndPoint
        endpoint.Port

    let internal startSpecification config sessionId worktree port =
        let path = CanonicalWorktree.path worktree

        { Executable = config.TtydExecutable
          WorkingDirectory = path
          Environment =
            [ "TREEMON_TERMINAL_SESSION_ID", sessionId
              "TREEMON_TERMINAL_WORKTREE", path ]
          Arguments =
            [ "-p"
              string port
              "-i"
              "127.0.0.1"
              "-W"
              "-O"
              "-o"
              "-t"
              "fontSize=16"
              "-t"
              "disableLeaveAlert=true"
              "-w"
              path
              config.ShellCommand
              "-WorkingDirectory"
              "."
              "-NoExit" ] }

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

    let private waitUntilReady timeout port owned =
        let deadline = DateTimeOffset.UtcNow + timeout

        let rec wait () =
            async {
                if JobProcess.hasExited owned then
                    return Error "ttyd exited before its loopback endpoint became ready"
                elif DateTimeOffset.UtcNow >= deadline then
                    return Error "Timed out waiting for ttyd to bind its loopback endpoint"
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
                match! waitUntilReady config.StartupTimeout port owned with
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
