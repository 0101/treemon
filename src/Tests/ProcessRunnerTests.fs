module Tests.ProcessRunnerTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open NUnit.Framework
open Server
open Tests.GitTestHelpers

// Five tests below need a real child process that outlives a deadline, overruns a capture limit, or
// spawns a grandchild for the process-tree kill to reach, so each one shells out. On Unix that
// shell is `sh` and is unremarkable. On Windows it is PowerShell, and running it trips Microsoft
// Defender for Endpoint's "Suspicious PowerShell command line" rule on a managed device, raising a
// security incident that has to be attested by hand. It has fired twice — 30 Jul 2026, and
// 03 Aug 2026 (MSDetectID 24562977), the latter quoting the `%TEMP%\treemon-process-runner
// <guid>\child.pid` path written by the timeout test below.
//
// What scores is the scaffolding, not the code under test: `ping 127.0.0.1 -n 30` is a documented
// evasion sleep (MITRE T1497.003), and around it sit an inline `-Command`, a detached `-PassThru`
// spawn, a PID persisted under a GUID-named temp directory, and `Wait-Process` — collectively the
// shape of a loader. Substituting the delay (`timeout /t`, `waitfor /t`, `Start-Sleep`) does not
// help: those are the same technique, the surrounding shape still scores, and the rule is a
// heuristic we cannot read, so no tweak can be confirmed short of provoking another incident.
//
// They are therefore excluded on Windows only. CI runs `ubuntu-latest`, so the `sh` branches keep
// covering real-process timeout, process-tree termination, and the shell-driven truncation paths on
// every push; what a Windows developer gives up is local pre-push feedback on these five.
//
// To restore them on Windows, replace the shell scaffolding rather than these attributes. Add
// `src/TestChild` (`OutputType=Exe`, ~40 lines) supporting `sleep <ms>`, `spawn-child <pidFile>
// <ms>` (re-launches itself via `Environment.ProcessPath`, writes the grandchild PID, then waits),
// and `emit --stdout <n> --stderr <n> --exit <code>`; reference it from `Tests.fsproj` and resolve
// it at `AppContext.BaseDirectory`. A `ProjectReference` to an `Exe` copies its apphost into the
// test output — `Cli.exe` and `Treemon.exe` already arrive that way — so no new build machinery is
// needed, and `Kill(entireProcessTree = true)` still reaches the grandchild. That also retires the
// `IsOSPlatform` command branching and both quote-escaping schemes in this file, since one helper
// serves both platforms.
let [<Literal>] private WindowsEdrExclusion =
    "Spawns PowerShell, which trips Defender's suspicious-command-line rule; see the note at the top of this file"

/// An OS-appropriate command that writes `stderrBytes` bytes to stderr, `ok` to stdout, and exits 0
/// — a chatty-but-successful child, which is what a real `post-fork` hook looks like.
let private noisyStderrCommand (stderrBytes: int) =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        "powershell",
        [ "-NoProfile"
          "-Command"
          $"[Console]::Error.Write('x' * {stderrBytes}); Write-Output 'ok'" ]
    else
        "sh", [ "-c"; $"printf '%%{stderrBytes}s' '' >&2; echo ok" ]

/// An OS-appropriate command that writes `stdoutBytes` bytes to stdout, a short message to stderr,
/// and exits non-zero — a failing child that also overruns its stdout cap.
let private failingNoisyStdoutCommand (stdoutBytes: int) =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        "powershell",
        [ "-NoProfile"
          "-Command"
          $"Write-Output ('x' * {stdoutBytes}); [Console]::Error.Write('boom'); exit 1" ]
    else
        "sh", [ "-c"; $"printf '%%{stdoutBytes}s' ''; echo boom >&2; exit 1" ]

/// Every case here pins the caps it exercises, so the shared spawn starts from the smallest preset
/// and each test overrides only the limit under test.
let private testSpawn fileName =
    { ProcessRunner.Spawn.create fileName with
        Context = "Test"
        Limits = ProcessRunner.CaptureLimits.tiny }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ProcessRunnerArgumentListTests() =

    let mutable tempDir = "" // NUnit SetUp and TearDown must share the per-test directory through fixture state.

    [<SetUp>]
    member _.Setup() =
        tempDir <-
            Path.Combine(
                Path.GetTempPath(),
                $"treemon-process-runner {Guid.NewGuid():N}"
            )

        Directory.CreateDirectory(tempDir) |> ignore
        initRepoOnMain tempDir

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            try
                Directory.Delete(tempDir, recursive = true)
            with _ ->
                ()

    [<Test>]
    member _.``argument-list execution preserves a working path containing spaces``() =
        let result =
            ProcessRunner.capture
                (testSpawn "git")
                [ "-C"; tempDir; "rev-parse"; "--is-inside-work-tree" ]
            |> TestUtils.runAsync

        match result with
        | Ok output ->
            Assert.That(output.ExitCode, Is.EqualTo(0))
            Assert.That(Encoding.UTF8.GetString(output.Stdout).Trim(), Is.EqualTo("true"))
        | Error error -> Assert.Fail($"Expected process output, got {error}")

    [<Test>]
    member _.``stdout capture returns its bounded prefix with truncation metadata``() =
        writeText tempDir "large.txt" (String('x', 4096))
        gitOk tempDir [ "add"; "--"; "large.txt" ]
        gitOk tempDir [ "commit"; "-m"; "large output" ]

        let result =
            ProcessRunner.capture
                { testSpawn "git" with Limits.StdoutBytes = 16 }
                [ "-C"; tempDir; "show"; "HEAD:large.txt" ]
            |> TestUtils.runAsync

        match result with
        | Error error -> Assert.Fail($"Expected a completed run, got {error}")
        | Ok output ->
            Assert.Multiple(fun () ->
                Assert.That(
                    output.Truncated,
                    Is.EqualTo([ ProcessRunner.StandardOutput ]),
                    "the stdout capture limit must be reported")

                Assert.That(output.Stdout.Length, Is.EqualTo(16), "no bytes past the cap are kept")

                Assert.That(
                    output.ExitCode,
                    Is.EqualTo(0),
                    "the exit code survives a truncated capture"))

    [<Test>]
    member _.``stderr capture reports its byte limit``() =
        let result =
            ProcessRunner.capture
                { testSpawn "git" with Limits.StderrBytes = 1 }
                [ "not-a-real-git-command" ]
            |> TestUtils.runAsync

        match result with
        | Error error -> Assert.Fail($"Expected a completed run, got {error}")
        | Ok output ->
            Assert.Multiple(fun () ->
                Assert.That(
                    output.Truncated,
                    Is.EqualTo([ ProcessRunner.StandardError ]),
                    "the stderr capture limit must be reported")

                Assert.That(output.ExitCode, Is.Not.EqualTo(0), "the failing exit code survives"))

    [<Test>]
    [<Platform(Exclude = "Win", Reason = WindowsEdrExclusion)>]
    member _.``timeout returns a typed error and terminates the process tree``() =
        let childPidPath = Path.Combine(tempDir, "child.pid")

        let fileName, arguments =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                let escapedPidPath = childPidPath.Replace("'", "''")

                "powershell",
                [ "-NoProfile"
                  "-Command"
                  // -NoNewWindow keeps ping in the (already hidden) parent console; without it
                  // Start-Process allocates a visible console that flashes on screen mid-test.
                  $"$child = Start-Process ping -ArgumentList '127.0.0.1','-n','30' -NoNewWindow -PassThru; Set-Content -NoNewline -Path '{escapedPidPath}' -Value $child.Id; Wait-Process -Id $child.Id" ]
            else
                let escapedPidPath = childPidPath.Replace("'", "'\\''")
                "sh", [ "-c"; $"sleep 30 & echo $! > '{escapedPidPath}'; wait" ]

        let result =
            ProcessRunner.capture
                { testSpawn fileName with Deadline = ProcessRunner.Timeout 2_000 }
                arguments
            |> TestUtils.runAsync

        Assert.That(
            (result = Error ProcessRunner.TimedOut),
            Is.True,
            $"Expected timeout, got {result}"
        )

        Assert.That(
            File.Exists(childPidPath),
            Is.True,
            "The child process did not start before the timeout"
        )

        let childPid = File.ReadAllText(childPidPath) |> Int32.Parse

        let childExited =
            SpinWait.SpinUntil(
                (fun () ->
                    try
                        use child = Process.GetProcessById(childPid)
                        child.HasExited
                    with :? ArgumentException ->
                        true),
                TimeSpan.FromSeconds(5.0)
            )

        if not childExited then
            use child = Process.GetProcessById(childPid)
            child.Kill(entireProcessTree = true)

        Assert.That(childExited, Is.True, "Timed-out child process was left running")

    [<Test>]
    member _.``missing executable returns a typed start failure``() =
        let result =
            ProcessRunner.capture
                (testSpawn $"missing-executable-{Guid.NewGuid():N}")
                []
            |> TestUtils.runAsync

        match result with
        | Error(ProcessRunner.StartFailed _) -> ()
        | _ -> Assert.Fail($"Expected start failure, got {result}")

    [<Test>]
    member _.``text capture decodes UTF-8 stdout and trims trailing whitespace``() =
        writeText tempDir "utf8.txt" "žluťoučký kůň\n\n"
        gitOk tempDir [ "add"; "--"; "utf8.txt" ]
        gitOk tempDir [ "commit"; "-m"; "utf8 content" ]

        let result =
            ProcessRunner.textResult
                (testSpawn "git")
                [ "-C"; tempDir; "show"; "HEAD:utf8.txt" ]
            |> TestUtils.runAsync

        let expected: Result<string, string> = Ok "žluťoučký kůň"

        Assert.That(
            (result = expected),
            Is.True,
            $"Expected decoded and trimmed stdout, got {result}"
        )

    [<Test>]
    member _.``text capture yields stdout on success and None on a non-zero exit``() =
        let succeeded =
            ProcessRunner.text
                (testSpawn "git")
                [ "-C"; tempDir; "rev-parse"; "--is-inside-work-tree" ]
            |> TestUtils.runAsync

        let failed =
            ProcessRunner.text
                (testSpawn "git")
                [ "-C"; tempDir; "rev-parse"; "--verify"; "refs/heads/missing" ]
            |> TestUtils.runAsync

        let expectedFailure: string option = None

        Assert.Multiple(fun () ->
            Assert.That(succeeded, Is.EqualTo(Some "true"))
            Assert.That(failed, Is.EqualTo(expectedFailure)))

    [<Test>]
    member _.``text capture returns decoded stderr for a non-zero exit``() =
        let result =
            ProcessRunner.textResult
                (testSpawn "git")
                [ "-C"; tempDir; "cat-file"; "-p"; "definitely-not-an-object" ]
            |> TestUtils.runAsync

        match result with
        | Ok stdout -> Assert.Fail($"Expected a non-zero exit, got stdout '{stdout}'")
        | Error error ->
            Assert.That(error, Does.StartWith("fatal:"), $"Expected Git stderr, got '{error}'")

    [<Test>]
    member _.``text capture maps a missing executable to a start failure message``() =
        let result =
            ProcessRunner.textResult
                (testSpawn $"missing-executable-{Guid.NewGuid():N}")
                []
            |> TestUtils.runAsync

        match result with
        | Ok stdout -> Assert.Fail($"Expected a start failure, got stdout '{stdout}'")
        | Error error ->
            Assert.That(
                error,
                Does.StartWith("Failed to start process:"),
                $"Expected a start failure message, got '{error}'"
            )

    [<Test>]
    [<Platform(Exclude = "Win", Reason = WindowsEdrExclusion)>]
    member _.``text capture maps a timeout to a message naming the configured timeout``() =
        // The sleeper only has to outlive the deadline; the deadline itself is the test's whole
        // wall-clock cost, so it stays short.
        let fileName, arguments =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                "powershell", [ "-NoProfile"; "-Command"; "Start-Sleep -Seconds 30" ]
            else
                "sh", [ "-c"; "sleep 30" ]

        let result =
            ProcessRunner.textResult
                { testSpawn fileName with Deadline = ProcessRunner.Timeout 300 }
                arguments
            |> TestUtils.runAsync

        let expected: Result<string, string> = Error "Timed out after 300ms"

        Assert.That(
            (result = expected),
            Is.True,
            $"Expected the mapped timeout message, got {result}"
        )

    [<Test>]
    member _.``text capture fails closed when the parsed stdout was truncated``() =
        writeText tempDir "large.txt" (String('x', 4096))
        gitOk tempDir [ "add"; "--"; "large.txt" ]
        gitOk tempDir [ "commit"; "-m"; "large output" ]

        // `git show` exits 0 here: the text wrappers still fail, because their callers parse the
        // string they get back and a prefix would silently read as the whole output.
        let result =
            ProcessRunner.textResult
                { testSpawn "git" with Limits.StdoutBytes = 16 }
                [ "-C"; tempDir; "show"; "HEAD:large.txt" ]
            |> TestUtils.runAsync

        let expected: Result<string, string> =
            Error "Standard output exceeded its capture limit"

        Assert.That(
            (result = expected),
            Is.True,
            $"Expected the mapped stdout limit message, got {result}"
        )

    [<Test>]
    [<Platform(Exclude = "Win", Reason = WindowsEdrExclusion)>]
    member _.``text capture reports stderr when a failing command also truncated stdout``() =
        // Both conditions at once: the command fails *and* overruns its stdout cap. stdout is never
        // returned on the failure path, so the capture limit must not mask the actual diagnostic.
        let fileName, arguments = failingNoisyStdoutCommand 4096

        let result =
            ProcessRunner.textResult
                { testSpawn fileName with Limits.StdoutBytes = 16 }
                arguments
            |> TestUtils.runAsync

        let expected: Result<string, string> = Error "boom"

        Assert.That(
            (result = expected),
            Is.True,
            $"Expected the child's stderr rather than the capture-limit message, got {result}"
        )

    [<Test>]
    [<Platform(Exclude = "Win", Reason = WindowsEdrExclusion)>]
    member _.``text capture keeps complete stdout when only stderr was truncated``() =
        let fileName, arguments = noisyStderrCommand 4096

        let result =
            ProcessRunner.textResult
                { testSpawn fileName with Limits.StderrBytes = 16 }
                arguments
            |> TestUtils.runAsync

        let expected: Result<string, string> = Ok "ok"

        Assert.That(
            (result = expected),
            Is.True,
            $"Truncated stderr must not fail a command whose stdout is complete, got {result}"
        )

    [<Test>]
    [<Platform(Exclude = "Win", Reason = WindowsEdrExclusion)>]
    member _.``exit-code capture succeeds on exit 0 despite a truncated capture``() =
        let fileName, arguments = noisyStderrCommand 4096

        let result =
            ProcessRunner.exitResult
                { testSpawn fileName with
                    Limits.StdoutBytes = 16
                    Limits.StderrBytes = 16 }
                arguments
            |> TestUtils.runAsync

        let expected: Result<unit, string> = Ok()

        Assert.That(
            (result = expected),
            Is.True,
            $"A chatty but successful child must stay a success, got {result}"
        )

    [<Test>]
    member _.``exit-code capture reports stderr for a non-zero exit``() =
        let result =
            ProcessRunner.exitResult
                (testSpawn "git")
                [ "-C"; tempDir; "cat-file"; "-p"; "definitely-not-an-object" ]
            |> TestUtils.runAsync

        match result with
        | Ok() -> Assert.Fail("Expected a non-zero exit to fail")
        | Error error ->
            Assert.That(error, Does.StartWith("fatal:"), $"Expected Git stderr, got '{error}'")
