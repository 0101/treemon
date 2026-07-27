module Tests.TerminalSessionFixtureTests

open System
open System.Diagnostics
open System.IO
open System.Text
open NUnit.Framework
open Server.SessionManager
open Tests.TerminalSessionFixture
open Tests.TestUtils

let private encodedCommand path =
    buildScript path (Some "copilot --yolo")
    |> Encoding.Unicode.GetBytes
    |> Convert.ToBase64String
    |> fun encoded -> $"pwsh -NoExit -EncodedCommand {encoded}"

let private startShell path =
    let encoded =
        buildScript path None
        |> Encoding.Unicode.GetBytes
        |> Convert.ToBase64String

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
    if not proc.HasExited then
        proc.Kill(entireProcessTree = true)
        proc.WaitForExit(5_000) |> ignore

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
            stopIfRunning owned
            stopIfRunning other
            Directory.Delete(fixtureRoot, recursive = true)
