module Tests.EmbeddedTerminalTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Shared
open Server

let private run workflow =
    workflow |> Async.RunSynchronously

let private canonical path =
    Server.PathUtils.toWorktreePath path

let private isPath path (tab: EmbeddedTerminalTab) =
    Shared.PathUtils.pathEquals
        (WorktreePath.value path)
        (WorktreePath.value tab.Worktree)

let private tryFindTab path snapshot =
    snapshot.Tabs |> List.tryFind (isPath path)

let private start manager path =
    match EmbeddedTerminal.start manager path |> run with
    | Ok snapshot -> snapshot
    | Error error ->
        Assert.Fail(error)
        EmbeddedTerminalSnapshot.empty

let private waitUntil description predicate =
    let deadline = DateTime.UtcNow.AddSeconds 10.0

    let rec poll () =
        if predicate () then
            ()
        elif DateTime.UtcNow >= deadline then
            Assert.Fail($"Timed out waiting for {description}")
        else
            Async.Sleep 50 |> run
            poll ()

    poll ()

let private waitForSnapshot manager predicate =
    let deadline = DateTime.UtcNow.AddSeconds 10.0

    let rec poll () =
        let snapshot = EmbeddedTerminal.get manager |> run

        if predicate snapshot then
            snapshot
        elif DateTime.UtcNow >= deadline then
            Assert.Fail($"Timed out waiting for embedded terminal snapshot. Last snapshot: {snapshot}")
            snapshot
        else
            Async.Sleep 50 |> run
            poll ()

    poll ()

let private waitForLifecycle manager path predicate =
    waitForSnapshot manager (fun snapshot ->
        snapshot
        |> tryFindTab path
        |> Option.exists (fun tab -> predicate tab.Lifecycle))

let private endpointFor path snapshot =
    match snapshot |> tryFindTab path |> Option.map _.Lifecycle with
    | Some (EmbeddedTerminalLifecycle.Running endpoint) -> endpoint
    | lifecycle ->
        Assert.Fail($"Expected running terminal for '{WorktreePath.value path}', got {lifecycle}")
        ""

let private errorFor path snapshot =
    match snapshot |> tryFindTab path |> Option.map _.Lifecycle with
    | Some (EmbeddedTerminalLifecycle.Failed error) -> error
    | lifecycle ->
        Assert.Fail($"Expected failed terminal for '{WorktreePath.value path}', got {lifecycle}")
        ""

let private processIsAlive pid =
    try
        use proc = Process.GetProcessById(pid)
        not proc.HasExited
    with :? ArgumentException ->
        false

let private waitForProcessExit pid =
    waitUntil $"PID {pid} to exit" (fun () -> processIsAlive pid |> not)

let private fakeServerScript =
    """
$Remaining = $args
$portIndex = [Array]::IndexOf($Remaining, '-p')
$interfaceIndex = [Array]::IndexOf($Remaining, '-i')
$fontSizeIndex = [Array]::IndexOf($Remaining, '-t')
$cwdIndex = [Array]::IndexOf($Remaining, '-w')
$port = [int]$Remaining[$portIndex + 1]
$cwd = $Remaining[$cwdIndex + 1]
$worktreeName = [IO.Path]::GetFileName($env:TREEMON_TERMINAL_WORKTREE)
$recordPath = Join-Path $env:FAKE_TERMINAL_RECORDS "$worktreeName.ttyd.txt"
$shellRecordPath = Join-Path $env:FAKE_TERMINAL_RECORDS "$worktreeName.shell.txt"
$shellIndex = $cwdIndex + 2
$shell = $Remaining[$shellIndex]
$shellArgs = $Remaining[($shellIndex + 1)..($Remaining.Length - 1)]
$psi = [Diagnostics.ProcessStartInfo]::new($shell)
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
foreach ($arg in $shellArgs) { [void]$psi.ArgumentList.Add($arg) }
$shellProcess = [Diagnostics.Process]::Start($psi)
$deadline = [DateTime]::UtcNow.AddSeconds(5)
while (-not [IO.File]::Exists($shellRecordPath) -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 25
}
if (-not [IO.File]::Exists($shellRecordPath)) {
    throw 'Timed out waiting for the fake shell to report its cwd'
}
$shellCwd = [IO.File]::ReadAllText($shellRecordPath)
$workingDirectoryIndex = [Array]::IndexOf($shellArgs, '-WorkingDirectory')
$childCommand = $shell + ' ' + ($shellArgs[$workingDirectoryIndex..($shellArgs.Length - 1)] -join ' ')
$record = "$PID|$port|$($Remaining[$interfaceIndex + 1])|$($Remaining -contains '-W')|$($Remaining -contains '-O')|$cwd|$shell|$shellCwd|$($shellArgs[$workingDirectoryIndex + 1])|$([Text.Encoding]::UTF8.GetByteCount($childCommand))|$($Remaining[$fontSizeIndex + 1])"
[IO.File]::WriteAllText($recordPath, $record)
$delayOnce = Join-Path $env:FAKE_TERMINAL_RECORDS 'delay-once'
if ([IO.File]::Exists($delayOnce)) {
    try {
        [IO.File]::Move($delayOnce, "$delayOnce.$PID")
        Start-Sleep -Seconds 2
    } catch [IO.IOException] {
    }
}
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
    if (-not $shellProcess.HasExited) {
        $shellProcess.Kill($true)
        $shellProcess.WaitForExit()
    }
}
"""

let private fakeShellScript =
    """
$Remaining = $args
Set-Location $env:FAKE_PROFILE_CWD
$profileCwd = $pwd.Path
$encodedIndex = [Array]::IndexOf($Remaining, '-EncodedCommand')
if ($encodedIndex -lt 0) { throw 'Missing -EncodedCommand' }
$script = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Remaining[$encodedIndex + 1]))
Invoke-Expression $script
$worktreeName = [IO.Path]::GetFileName($env:TREEMON_TERMINAL_WORKTREE)
$recordPath = Join-Path $env:FAKE_TERMINAL_RECORDS "$worktreeName.shell.txt"
[IO.File]::WriteAllLines($recordPath, @($profileCwd, $pwd.Path))
while ($true) { Start-Sleep -Seconds 1 }
"""

let private withFakeManager probeInterval test =
    Tests.TestUtils.withTempDir "embedded-terminal" (fun tempDir ->
        let scriptPath = Path.Combine(tempDir, "fake-ttyd.ps1")
        let shellScriptPath = Path.Combine(tempDir, "fake-shell.ps1")
        let recordDir = Path.Combine(tempDir, "records")
        let profileCwd = Path.Combine(tempDir, "profile-cwd")
        Directory.CreateDirectory(recordDir) |> ignore
        Directory.CreateDirectory(profileCwd) |> ignore
        File.WriteAllText(scriptPath, fakeServerScript)
        File.WriteAllText(shellScriptPath, fakeShellScript)

        let previousRecords =
            Environment.GetEnvironmentVariable("FAKE_TERMINAL_RECORDS")

        let previousProfileCwd =
            Environment.GetEnvironmentVariable("FAKE_PROFILE_CWD")

        Environment.SetEnvironmentVariable("FAKE_TERMINAL_RECORDS", recordDir)
        Environment.SetEnvironmentVariable("FAKE_PROFILE_CWD", profileCwd)

        let manager =
            EmbeddedTerminal.createWithConfig
                { ExecutablePath = "pwsh"
                  ShellCommand = "pwsh"
                  ShellPrefixArguments =
                    [ "-NoLogo"
                      "-NoProfile"
                      "-File"
                      shellScriptPath ]
                  PrefixArguments =
                    [ "-NoLogo"
                      "-NoProfile"
                      "-File"
                      scriptPath ]
                  StartupTimeout = TimeSpan.FromSeconds 5.0
                  ProbeInterval = probeInterval }

        try
            test tempDir recordDir profileCwd manager
        finally
            EmbeddedTerminal.closeAll manager |> run
            Environment.SetEnvironmentVariable(
                "FAKE_TERMINAL_RECORDS",
                previousRecords
            )
            Environment.SetEnvironmentVariable(
                "FAKE_PROFILE_CWD",
                previousProfileCwd
            ))

let private recordPath recordDir path =
    let name =
        path
        |> WorktreePath.value
        |> Path.GetFileName

    Path.Combine(recordDir, $"{name}.ttyd.txt")

let private readRecord recordDir path =
    File.ReadAllText(recordPath recordDir path).Split('|')

let private readPid recordDir path =
    readRecord recordDir path |> Array.head |> int

let private waitForRecord recordDir path predicate =
    let file = recordPath recordDir path
    let deadline = DateTime.UtcNow.AddSeconds 10.0

    let rec poll () =
        if File.Exists file then
            let fields = File.ReadAllText(file).Split('|')

            if fields.Length > 0 && predicate fields then
                fields
            elif DateTime.UtcNow >= deadline then
                Assert.Fail($"Timed out waiting for record for '{WorktreePath.value path}'")
                fields
            else
                Async.Sleep 50 |> run
                poll ()
        elif DateTime.UtcNow >= deadline then
            Assert.Fail($"Timed out waiting for record for '{WorktreePath.value path}'")
            [||]
        else
            Async.Sleep 50 |> run
            poll ()

    poll ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type EmbeddedTerminalTests() =

    [<Test>]
    member _.``missing ttyd reports the setup command on its tab``() =
        let path =
            Path.Combine(
                Path.GetTempPath(),
                $"missing-ttyd-{Guid.NewGuid():N}.exe"
            )

        let manager =
            EmbeddedTerminal.createWithConfig
                { ExecutablePath = path
                  ShellCommand = "pwsh"
                  ShellPrefixArguments = []
                  PrefixArguments = []
                  StartupTimeout = TimeSpan.FromSeconds 1.0
                  ProbeInterval = TimeSpan.FromMilliseconds 10.0 }

        try
            let worktree = canonical Environment.CurrentDirectory
            let snapshot = start manager worktree
            Assert.That(errorFor worktree snapshot, Does.Contain(@".\treemon.ps1 setup-ttyd"))
        finally
            EmbeddedTerminal.closeAll manager |> run

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

        let unknown =
            WorktreePath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"unknown-{Guid.NewGuid():N}"
                )
            )

        try
            match api.startEmbeddedTerminal unknown |> run with
            | Error error ->
                Assert.That(error, Does.StartWith "Unknown worktree path:")
            | Ok snapshot ->
                Assert.Fail($"Expected rejected path, got {snapshot}")

            Assert.That(
                api.getEmbeddedTerminals () |> run,
                Is.EqualTo EmbeddedTerminalSnapshot.empty
            )
        finally
            EmbeddedTerminal.closeAll manager |> run

    [<Test>]
    member _.``launch is compact safe and restores the worktree after profile startup``() =
        withFakeManager
            (TimeSpan.FromMilliseconds 25.0)
            (fun tempDir recordDir profileCwd manager ->
                let rawPath = Path.Combine(tempDir, "worktree with ' quote")
                Directory.CreateDirectory(rawPath) |> ignore
                let worktree = canonical rawPath

                let starting = start manager worktree

                Assert.That(
                    (starting |> tryFindTab worktree).Value.Lifecycle,
                    Is.EqualTo EmbeddedTerminalLifecycle.Starting
                )

                let running =
                    waitForLifecycle manager worktree (function
                        | EmbeddedTerminalLifecycle.Running _ -> true
                        | EmbeddedTerminalLifecycle.Failed error ->
                            Assert.Fail(error)
                            false
                        | EmbeddedTerminalLifecycle.Starting -> false)

                let endpoint = endpointFor worktree running
                let fields = readRecord recordDir worktree

                let shellCwds =
                    fields[7].Split(
                        [| "\r\n"; "\n" |],
                        StringSplitOptions.RemoveEmptyEntries
                    )

                let pid = int fields[0]
                let port = Uri(endpoint).Port

                Assert.Multiple(fun () ->
                    Assert.That(port, Is.Not.EqualTo 5000)
                    Assert.That(fields[1], Is.EqualTo(string port))
                    Assert.That(fields[2], Is.EqualTo "127.0.0.1")
                    Assert.That(fields[3], Is.EqualTo "True")
                    Assert.That(fields[4], Is.EqualTo "True")
                    Assert.That(fields[5], Is.EqualTo(WorktreePath.value worktree))
                    Assert.That(fields[6], Is.EqualTo "pwsh")
                    Assert.That(shellCwds[0], Is.EqualTo profileCwd)
                    Assert.That(
                        Server.PathUtils.normalizePath shellCwds[1],
                        Is.EqualTo(WorktreePath.value worktree)
                    )
                    Assert.That(fields[8], Is.EqualTo ".")
                    Assert.That(int fields[9], Is.LessThan 256)
                    Assert.That(fields[10], Is.EqualTo "fontSize=16"))

                let alias =
                    WorktreePath(
                        WorktreePath.value worktree
                        + string Path.DirectorySeparatorChar
                    )

                let reused = start manager alias
                Assert.That(reused, Is.EqualTo running)
                Assert.That(readPid recordDir worktree, Is.EqualTo pid)

                let closed = EmbeddedTerminal.close manager worktree |> run
                Assert.That(closed, Is.EqualTo EmbeddedTerminalSnapshot.empty)
                waitForProcessExit pid)

    [<Test>]
    member _.``different worktrees start concurrently and retain opening order``() =
        withFakeManager
            (TimeSpan.FromMilliseconds 25.0)
            (fun tempDir recordDir _ manager ->
                let zebraPath = Path.Combine(tempDir, "zebra")
                let applePath = Path.Combine(tempDir, "apple")
                Directory.CreateDirectory(zebraPath) |> ignore
                Directory.CreateDirectory(applePath) |> ignore
                let zebra = canonical zebraPath
                let apple = canonical applePath

                start manager zebra |> ignore
                let bothStarting = start manager apple

                Assert.That(
                    bothStarting.Tabs |> List.map _.Worktree,
                    Is.EqualTo([ zebra; apple ])
                )

                let running =
                    waitForSnapshot manager (fun snapshot ->
                        snapshot.Tabs.Length = 2
                        && snapshot.Tabs
                           |> List.forall (fun tab ->
                               match tab.Lifecycle with
                               | EmbeddedTerminalLifecycle.Running _ -> true
                               | EmbeddedTerminalLifecycle.Starting
                               | EmbeddedTerminalLifecycle.Failed _ -> false))

                let zebraEndpoint = endpointFor zebra running
                let appleEndpoint = endpointFor apple running
                let zebraPid = readPid recordDir zebra
                let applePid = readPid recordDir apple

                Assert.Multiple(fun () ->
                    Assert.That(zebraEndpoint, Is.Not.EqualTo appleEndpoint)
                    Assert.That(zebraPid, Is.Not.EqualTo applePid)
                    Assert.That(running.Tabs |> List.map _.Worktree, Is.EqualTo([ zebra; apple ])))

                let reused = start manager zebra
                Assert.That(reused, Is.EqualTo running)
                Assert.That(readPid recordDir zebra, Is.EqualTo zebraPid)

                let afterClose = EmbeddedTerminal.close manager zebra |> run
                Assert.That(afterClose.Tabs |> List.map _.Worktree, Is.EqualTo([ apple ]))
                waitForProcessExit zebraPid
                Assert.That(processIsAlive applePid, Is.True)

                EmbeddedTerminal.close manager apple |> run |> ignore
                waitForProcessExit applePid)

    [<Test>]
    member _.``failed tab restarts without disturbing another running worktree``() =
        withFakeManager
            (TimeSpan.FromMilliseconds 25.0)
            (fun tempDir recordDir _ manager ->
                let firstPath = Path.Combine(tempDir, "first")
                let secondPath = Path.Combine(tempDir, "second")
                Directory.CreateDirectory(firstPath) |> ignore
                Directory.CreateDirectory(secondPath) |> ignore
                let first = canonical firstPath
                let second = canonical secondPath

                start manager first |> ignore
                start manager second |> ignore

                let running =
                    waitForSnapshot manager (fun snapshot ->
                        snapshot.Tabs.Length = 2
                        && snapshot.Tabs
                           |> List.forall (fun tab ->
                               match tab.Lifecycle with
                               | EmbeddedTerminalLifecycle.Running _ -> true
                               | EmbeddedTerminalLifecycle.Starting
                               | EmbeddedTerminalLifecycle.Failed _ -> false))

                let firstEndpoint = endpointFor first running
                let secondEndpoint = endpointFor second running
                let firstPid = readPid recordDir first
                let secondPid = readPid recordDir second

                use firstProcess = Process.GetProcessById(firstPid)
                firstProcess.Kill(entireProcessTree = true)
                firstProcess.WaitForExit()

                let failed =
                    waitForLifecycle manager first (function
                        | EmbeddedTerminalLifecycle.Failed _ -> true
                        | EmbeddedTerminalLifecycle.Starting
                        | EmbeddedTerminalLifecycle.Running _ -> false)

                Assert.Multiple(fun () ->
                    Assert.That(errorFor first failed, Does.Contain("ttyd exited with code"))
                    Assert.That(endpointFor second failed, Is.EqualTo secondEndpoint)
                    Assert.That(processIsAlive secondPid, Is.True))

                let restarting = start manager first
                Assert.That(
                    restarting.Tabs |> List.map _.Worktree,
                    Is.EqualTo([ first; second ])
                )

                Assert.That(
                    (restarting |> tryFindTab first).Value.Lifecycle,
                    Is.EqualTo EmbeddedTerminalLifecycle.Starting
                )

                let restarted =
                    waitForLifecycle manager first (function
                        | EmbeddedTerminalLifecycle.Running _ -> true
                        | EmbeddedTerminalLifecycle.Failed error ->
                            Assert.Fail(error)
                            false
                        | EmbeddedTerminalLifecycle.Starting -> false)

                let replacementFields =
                    waitForRecord recordDir first (fun fields -> int fields[0] <> firstPid)

                let replacementPid = int replacementFields[0]

                Assert.Multiple(fun () ->
                    Assert.That(replacementPid, Is.Not.EqualTo firstPid)
                    Assert.That(endpointFor first restarted, Is.Not.EqualTo firstEndpoint)
                    Assert.That(endpointFor second restarted, Is.EqualTo secondEndpoint)
                    Assert.That(processIsAlive secondPid, Is.True)))

    [<Test>]
    member _.``stale cancelled launch cannot replace a newer generation``() =
        withFakeManager
            (TimeSpan.FromMilliseconds 500.0)
            (fun tempDir recordDir _ manager ->
                let rawPath = Path.Combine(tempDir, "stale")
                Directory.CreateDirectory(rawPath) |> ignore
                let worktree = canonical rawPath
                File.WriteAllText(Path.Combine(recordDir, "delay-once"), "")

                start manager worktree |> ignore
                let firstFields = waitForRecord recordDir worktree (fun _ -> true)
                let firstPid = int firstFields[0]

                let closeTask =
                    EmbeddedTerminal.close manager worktree
                    |> Async.StartAsTask

                waitForSnapshot manager (fun snapshot ->
                    tryFindTab worktree snapshot |> Option.isNone)
                |> ignore

                start manager worktree |> ignore

                let replacementFields =
                    waitForRecord recordDir worktree (fun fields -> int fields[0] <> firstPid)

                let replacementPid = int replacementFields[0]

                let running =
                    waitForLifecycle manager worktree (function
                        | EmbeddedTerminalLifecycle.Running _ -> true
                        | EmbeddedTerminalLifecycle.Failed error ->
                            Assert.Fail(error)
                            false
                        | EmbeddedTerminalLifecycle.Starting -> false)

                let replacementEndpoint = endpointFor worktree running
                closeTask.GetAwaiter().GetResult() |> ignore
                waitForProcessExit firstPid
                Async.Sleep 750 |> run

                let afterStaleCompletion = EmbeddedTerminal.get manager |> run

                Assert.Multiple(fun () ->
                    Assert.That(
                        endpointFor worktree afterStaleCompletion,
                        Is.EqualTo replacementEndpoint
                    )
                    Assert.That(processIsAlive replacementPid, Is.True)))

    [<Test>]
    member _.``server shutdown closes every owned terminal in parallel``() =
        withFakeManager
            (TimeSpan.FromMilliseconds 25.0)
            (fun tempDir recordDir _ manager ->
                let paths =
                    [ "one"; "two"; "three" ]
                    |> List.map (fun name ->
                        let path = Path.Combine(tempDir, name)
                        Directory.CreateDirectory(path) |> ignore
                        canonical path)

                paths |> List.iter (start manager >> ignore)

                waitForSnapshot manager (fun snapshot ->
                    snapshot.Tabs.Length = paths.Length
                    && snapshot.Tabs
                       |> List.forall (fun tab ->
                           match tab.Lifecycle with
                           | EmbeddedTerminalLifecycle.Running _ -> true
                           | EmbeddedTerminalLifecycle.Starting
                           | EmbeddedTerminalLifecycle.Failed _ -> false))
                |> ignore

                let pids =
                    paths
                    |> List.map (readPid recordDir)

                EmbeddedTerminal.closeAll manager |> run
                pids |> List.iter waitForProcessExit

                Assert.That(
                    EmbeddedTerminal.get manager |> run,
                    Is.EqualTo EmbeddedTerminalSnapshot.empty
                ))
