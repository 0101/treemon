module Tests.EmbeddedTerminalTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Shared
open Server

let private waitForState manager predicate =
    let deadline = DateTime.UtcNow.AddSeconds 10.0

    let rec poll () =
        let state = EmbeddedTerminal.get manager |> Async.RunSynchronously

        if predicate state then state
        elif DateTime.UtcNow >= deadline then
            Assert.Fail($"Timed out waiting for embedded terminal state. Last state: {state}")
            state
        else
            Async.Sleep 50 |> Async.RunSynchronously
            poll ()

    poll ()

let private fakeServerScript =
    """
$Remaining = $args
$portIndex = [Array]::IndexOf($Remaining, '-p')
$interfaceIndex = [Array]::IndexOf($Remaining, '-i')
$cwdIndex = [Array]::IndexOf($Remaining, '-w')
$port = [int]$Remaining[$portIndex + 1]
$cwd = $Remaining[$cwdIndex + 1]
$shell = $Remaining[$Remaining.Length - 1]
$record = "$PID|$port|$($Remaining[$interfaceIndex + 1])|$($Remaining -contains '-W')|$($Remaining -contains '-O')|$cwd|$shell"
[IO.File]::WriteAllText($env:FAKE_TTYD_RECORD, $record)
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
$listener.Start()
try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
            while (($line = $reader.ReadLine()) -ne '') {
                if ($null -eq $line) { break }
            }
            $body = [Text.Encoding]::UTF8.GetBytes('ready')
            $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
            $stream.Write($header)
            $stream.Write($body)
            $stream.Flush()
        } finally {
            $client.Dispose()
        }
    }
} finally {
    $listener.Stop()
}
"""

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type EmbeddedTerminalTests() =

    [<Test>]
    member _.``missing ttyd reports the setup command``() =
        let path = Path.Combine(Path.GetTempPath(), $"missing-ttyd-{Guid.NewGuid():N}.exe")

        let manager =
            EmbeddedTerminal.createWithConfig
                { ExecutablePath = path
                  ShellCommand = "pwsh"
                  PrefixArguments = []
                  StartupTimeout = TimeSpan.FromSeconds 1.0
                  ProbeInterval = TimeSpan.FromMilliseconds 10.0 }

        match EmbeddedTerminal.start manager (WorktreePath Environment.CurrentDirectory) |> Async.RunSynchronously with
        | EmbeddedTerminalState.Failed(_, error) ->
            Assert.That(error, Does.Contain(@".\treemon.ps1 setup-ttyd"))
        | state -> Assert.Fail($"Expected failed state, got {state}")

    [<Test>]
    member _.``API rejects a path outside the scheduler worktrees before launch``() =
        let agent = SchedulerState.createAgent ()
        let manager = EmbeddedTerminal.create ()

        let api =
            WorktreeApi.worktreeApi
                { Agent = agent
                  CardLog = CardEventLog.createAgent ()
                  SessionAgent = SessionManager.createAgent ()
                  EmbeddedTerminal = manager
                  ActivityStore = None
                  SnapshotStore = None
                  AutoSyncStore = None
                  WorktreeRoots = []
                  TestFixtures = None
                  AppVersion = "test"
                  DeployBranch = None }

        let unknown = WorktreePath(Path.Combine(Path.GetTempPath(), $"unknown-{Guid.NewGuid():N}"))

        match api.startEmbeddedTerminal unknown |> Async.RunSynchronously with
        | EmbeddedTerminalState.Failed(path, error) ->
            Assert.Multiple(fun () ->
                Assert.That(path, Is.EqualTo unknown)
                Assert.That(error, Does.StartWith "Unknown worktree path:"))
        | state -> Assert.Fail($"Expected rejected path, got {state}")

        Assert.That(
            api.getEmbeddedTerminal () |> Async.RunSynchronously,
            Is.EqualTo EmbeddedTerminalState.Closed
        )

    [<Test>]
    member _.``manager launches fixed loopback ttyd arguments and closes only its owned process``() =
        Tests.TestUtils.withTempDir "embedded-terminal" (fun tempDir ->
            let scriptPath = Path.Combine(tempDir, "fake-ttyd.ps1")
            let recordPath = Path.Combine(tempDir, "record.txt")
            File.WriteAllText(scriptPath, fakeServerScript)
            let previousRecord = Environment.GetEnvironmentVariable("FAKE_TTYD_RECORD")
            Environment.SetEnvironmentVariable("FAKE_TTYD_RECORD", recordPath)
            let escapedScriptPath = scriptPath.Replace("'", "''")

            let manager =
                EmbeddedTerminal.createWithConfig
                    { ExecutablePath = "pwsh"
                      ShellCommand = "pwsh"
                      PrefixArguments =
                        [ "-NoLogo"
                          "-NoProfile"
                          "-Command"
                          $"& '{escapedScriptPath}' @args" ]
                      StartupTimeout = TimeSpan.FromSeconds 5.0
                      ProbeInterval = TimeSpan.FromMilliseconds 25.0 }

            try
                let starting =
                    EmbeddedTerminal.start manager (WorktreePath tempDir)
                    |> Async.RunSynchronously

                Assert.That(
                    starting,
                    Is.EqualTo(EmbeddedTerminalState.Starting(WorktreePath tempDir))
                )

                let running =
                    waitForState manager (function
                        | EmbeddedTerminalState.Running _ -> true
                        | EmbeddedTerminalState.Failed(_, error) ->
                            Assert.Fail(error)
                            false
                        | _ -> false)

                let endpoint =
                    match running with
                    | EmbeddedTerminalState.Running(_, value) -> value
                    | _ -> failwith "unreachable"

                let fields = File.ReadAllText(recordPath).Split('|')
                let pid = int fields[0]
                let port = Uri(endpoint).Port

                Assert.Multiple(fun () ->
                    Assert.That(port, Is.Not.EqualTo 5000)
                    Assert.That(fields[1], Is.EqualTo(string port))
                    Assert.That(fields[2], Is.EqualTo "127.0.0.1")
                    Assert.That(fields[3], Is.EqualTo "True")
                    Assert.That(fields[4], Is.EqualTo "True")
                    Assert.That(fields[5], Is.EqualTo tempDir)
                    Assert.That(fields[6], Is.EqualTo "pwsh"))

                let second =
                    EmbeddedTerminal.start manager (WorktreePath(Path.Combine(tempDir, "other")))
                    |> Async.RunSynchronously

                Assert.That(second, Is.EqualTo running)
                Assert.That(EmbeddedTerminal.close manager |> Async.RunSynchronously, Is.EqualTo EmbeddedTerminalState.Closed)

                Assert.Throws<ArgumentException>(fun () -> Process.GetProcessById(pid) |> ignore)
                |> ignore
            finally
                EmbeddedTerminal.close manager |> Async.RunSynchronously |> ignore
                Environment.SetEnvironmentVariable("FAKE_TTYD_RECORD", previousRecord))
