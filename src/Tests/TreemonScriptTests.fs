module Tests.TreemonScriptTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Tests.TestUtils

/// Exercises the lifecycle script itself. Everything it keeps - the pid file, the run logs - lives
/// beside the script, so each case runs against a copy in a temp directory and can never see, or
/// disturb, a real server started from the checkout.
[<TestFixture>]
[<Category("Unit")>]
type TreemonScriptTests() =

    let repoRoot =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then
                failwith "Could not locate the repository root from the test assembly"
            elif File.Exists(Path.Combine(directory.FullName, "treemon.fsx")) then
                directory.FullName
            else
                find directory.Parent

        find (DirectoryInfo AppContext.BaseDirectory)

    /// The script is run from a copy so `__SOURCE_DIRECTORY__` - and with it the pid file and logs -
    /// points at the temp directory. A port no server in this repo uses keeps a stray run harmless.
    let runIn (directory: string) (arguments: string list) (environment: (string * string) list) =
        File.Copy(Path.Combine(repoRoot, "treemon.fsx"), Path.Combine(directory, "treemon.fsx"), overwrite = true)

        let startInfo =
            ProcessStartInfo(
                FileName = "dotnet",
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true)

        [ "fsi"; Path.Combine(directory, "treemon.fsx") ] @ arguments
        |> List.iter startInfo.ArgumentList.Add

        startInfo.Environment["TREEMON_PORT"] <- "5099"
        startInfo.Environment["TREEMON_CANVAS_PORT"] <- "5098"
        // Inherited from whatever launched the test run; it would otherwise make `start` refuse.
        startInfo.Environment.Remove "TREEMON_TERMINAL_SESSION_ID" |> ignore
        environment |> List.iter (fun (key, value) -> startInfo.Environment[key] <- value)

        use script = Process.Start startInfo
        let output = script.StandardOutput.ReadToEnd() + script.StandardError.ReadToEnd()
        script.WaitForExit()
        script.ExitCode, output

    /// A live process that is definitely not a Treemon server, standing in for a reused pid. It has
    /// to outlive the command under test on both platforms, and Windows has no `sleep` binary, so it
    /// idles in fsi rather than in a shell.
    let withStandInProcess (directory: string) (action: Process -> unit) =
        let script = Path.Combine(directory, "stand-in.fsx")
        File.WriteAllText(script, "System.Threading.Thread.Sleep 120_000\n")

        let startInfo =
            ProcessStartInfo(
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true)

        [ "fsi"; script ] |> List.iter startInfo.ArgumentList.Add
        use stand = Process.Start startInfo

        try
            // fsi takes a moment to reach the script, which then idles for two minutes, so this only
            // has to catch a stand-in that failed to start at all.
            System.Threading.Thread.Sleep 1_000
            Assert.That(stand.HasExited, Is.False, "the stand-in process should still be running")

            action stand
        finally
            if not stand.HasExited then
                stand.Kill true
                stand.WaitForExit 5_000 |> ignore

    [<Test>]
    member _.``an unknown command is named rather than answered with bare usage``() =
        withTempDir "treemon-script-dispatch" (fun directory ->
            let exitCode, output = runIn directory [ "frobnicate" ] []

            Assert.Multiple(fun () ->
                Assert.That(output, Does.Contain "Unknown command 'frobnicate'")
                Assert.That(exitCode, Is.Not.EqualTo 0)))

    [<Test>]
    member _.``status and log report plainly when nothing is running``() =
        withTempDir "treemon-script-idle" (fun directory ->
            let statusCode, status = runIn directory [ "status" ] []
            let _, log = runIn directory [ "log" ] []

            Assert.Multiple(fun () ->
                Assert.That(status, Does.Contain "not running")
                Assert.That(statusCode, Is.EqualTo 0)
                Assert.That(log, Does.Contain "No server log yet")))

    // The pid file outlives crashes and reboots, so the number in it can belong to anything.
    [<Test>]
    member _.``a pid belonging to another process is not reported as the server``() =
        withTempDir "treemon-script-stale-status" (fun directory ->
            withStandInProcess directory (fun stand ->
                File.WriteAllText(Path.Combine(directory, ".treemon.pid"), string stand.Id)

                let _, output = runIn directory [ "status" ] []
                Assert.That(output, Does.Contain "not running")))

    [<Test>]
    member _.``stop refuses a pid that is not the server and clears the stale file``() =
        withTempDir "treemon-script-stale-stop" (fun directory ->
            withStandInProcess directory (fun stand ->
                let pidFile = Path.Combine(directory, ".treemon.pid")
                File.WriteAllText(pidFile, string stand.Id)

                let exitCode, output = runIn directory [ "stop" ] []

                Assert.Multiple(fun () ->
                    Assert.That(output, Does.Contain "stale pid file")
                    Assert.That(exitCode, Is.EqualTo 0)
                    Assert.That(File.Exists pidFile, Is.False, "the stale pid file should have been cleared")
                    Assert.That(stand.HasExited, Is.False, "an unrelated process must not be killed"))))

    [<Test>]
    member _.``start fails cleanly when nothing has been published``() =
        withTempDir "treemon-script-unpublished" (fun directory ->
            let exitCode, output = runIn directory [ "start" ] []

            Assert.Multiple(fun () ->
                Assert.That(output, Does.Contain "publish")
                Assert.That(exitCode, Is.Not.EqualTo 0)
                Assert.That(File.Exists(Path.Combine(directory, ".treemon.pid")), Is.False)))

    // Production started from an embedded terminal inherits that terminal's shutdown boundary, so
    // closing the tab would take the server with it.
    [<Test>]
    member _.``start refuses to run from inside a Treemon embedded terminal``() =
        withTempDir "treemon-script-embedded" (fun directory ->
            let exitCode, output =
                runIn directory [ "start" ] [ "TREEMON_TERMINAL_SESSION_ID", "terminal-1" ]

            Assert.Multiple(fun () ->
                Assert.That(output, Does.Contain "embedded terminal")
                Assert.That(exitCode, Is.Not.EqualTo 0)))
