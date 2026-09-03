module Tests.TerminalSessionFixtureTests

open System
open System.Diagnostics
open System.IO
open FsToolkit.ErrorHandling
open NUnit.Framework
open Server.SessionManager
open Tests.TerminalSessionFixture
open Tests.TestUtils

let private encodedScript path =
    buildScript path |> Server.SessionManager.encodeCommand

let private encodedCommand path =
    $"{buildScript path}; copilot --yolo"
    |> Server.SessionManager.encodeCommand
    |> fun encoded -> $"pwsh -NoExit -EncodedCommand {encoded}"

let private startShell path =
    let encoded = encodedScript path

    let psi =
        ProcessStartInfo(
            "pwsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        )

    [ "-NoProfile"; "-NoExit"; "-EncodedCommand"; encoded ]
    |> List.iter psi.ArgumentList.Add

    Process.Start(psi)

let private stopIfRunning (proc: Process) =
    if proc.HasExited then
        Ok()
    else
        proc.Kill(entireProcessTree = true)

        if proc.WaitForExit(5_000) then
            Ok()
        else
            Error $"Fixture PowerShell PID {proc.Id} survived cleanup"

let private deleteDirectory path =
    try
        Directory.Delete(path, recursive = true)
        Ok()
    with ex ->
        Error $"Failed to delete {path}: {ex.Message}"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalSessionFixtureTests() =

    [<Test>]
    member _.``ownership matching decodes an exact fixture path``() =
        let path = @"C:\Temp\treemon-session-spawn-123\repo"
        Assert.That(isOwnedPowerShellCommand path (encodedCommand path), Is.True)

    [<Test>]
    member _.``ownership matching rejects another fixture path``() =
        Assert.That(
            isOwnedPowerShellCommand
                @"C:\Temp\treemon-session-spawn-123\repo"
                (encodedCommand @"C:\Temp\other\repo"),
            Is.False
        )

    [<TestCase(@"C:\Temp\treemon-session-spawn-123\repo")>]
    [<TestCase("-EncodedCommand invalid")>]
    member _.``ownership matching rejects unprovable commands``(commandLine: string) =
        Assert.That(
            isOwnedPowerShellCommand
                @"C:\Temp\treemon-session-spawn-123\repo"
                commandLine,
            Is.False
        )

    [<Test>]
    member _.``ownership matching rejects encoded-command text after another execution mode``() =
        let path = @"C:\Temp\treemon-session-spawn-123\repo"
        let encoded = encodedScript path

        Assert.Multiple(fun () ->
            Assert.That(
                isOwnedPowerShellCommand
                    path
                    $"pwsh -File helper.ps1 -- -EncodedCommand {encoded}",
                Is.False
            )

            Assert.That(
                isOwnedPowerShellCommand
                    path
                    $"pwsh -Command \"Write-Host '-EncodedCommand {encoded}'\"",
                Is.False
            ))

[<TestFixture>]
[<Category("Local")>]
[<Explicit("Spawns PowerShell processes - run manually during terminal cleanup development")>]
[<NonParallelizable>]
type TerminalSessionProcessCleanupTests() =

    [<Test>]
    member _.``fallback stops only the shell owned by the fixture path``() =
        let fixtureRoot = Path.Combine(Path.GetTempPath(), $"treemon-terminal-fallback-{Guid.NewGuid():N}")
        let ownedPath = Path.Combine(fixtureRoot, "owned")
        let otherPath = Path.Combine(fixtureRoot, "other")
        Directory.CreateDirectory(ownedPath) |> ignore
        Directory.CreateDirectory(otherPath) |> ignore
        use owned = startShell ownedPath
        use other = startShell otherPath

        try
            let result =
                stopOwnedPowerShellProcesses ownedPath
                |> Async.RunSynchronously

            assertOk result "Owned PowerShell cleanup should succeed"

            Assert.Multiple(fun () ->
                Assert.That(owned.WaitForExit(5_000), Is.True)
                Assert.That(other.HasExited, Is.False))
        finally
            let cleanupResult =
                [ stopIfRunning owned
                  stopIfRunning other
                  deleteDirectory fixtureRoot ]
                |> List.sequenceResultA
                |> Result.map ignore
                |> Result.mapError (String.concat Environment.NewLine)

            assertOk cleanupResult "Fixture process cleanup failed"
