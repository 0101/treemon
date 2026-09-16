module Tests.ServerFixtureTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Server
open Tests.TestUtils
open Treemon.TerminalHosting

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LogDestinationTests() =

    let workingDirectory =
        Path.Combine(Path.GetTempPath(), "treemon-log-tests", "worktree")

    let tempDirectory =
        Path.Combine(Path.GetTempPath(), "treemon-log-tests", "temp")

    let isolatedDirectory =
        Path.Combine(tempDirectory, "fixture")

    let resolve destination instanceId =
        Log.resolvePath
            workingDirectory
            tempDirectory
            58481
            1234
            instanceId
            destination
        |> Result.defaultWith invalidOp

    [<Test>]
    [<NonParallelizable>]
    member _.``Reading the fallback path does not prevent configured initialization``() =
        withTempDir "treemon-log-init-after-read" (fun root ->
            let key = "Treemon.Server.LogPath"
            let previous = AppContext.GetData key
            let configured = Path.Combine(root, "configured.log")

            try
                AppContext.SetData(key, null)
                Log.currentPath() |> ignore

                match Log.init configured with
                | Ok() -> ()
                | Error error ->
                    Assert.Fail($"Configured log initialization failed: {error}")

                Assert.That(Log.currentPath(), Is.EqualTo(configured))
            finally
                AppContext.SetData(key, previous))

    [<Test>]
    member _.``Production and fixture logs have different destinations``() =
        let instanceId =
            Guid.Parse("a8e720d9-bdf4-49b3-85ab-70eef805342f")

        let production =
            resolve Log.Destination.Production instanceId

        let fixture =
            resolve
                (Log.Destination.Isolated(Some isolatedDirectory))
                instanceId

        Assert.Multiple(fun () ->
            Assert.That(
                production,
                Is.EqualTo(
                    Path.Combine(
                        Path.GetFullPath workingDirectory,
                        "logs",
                        "server.log"
                    )
                )
            )

            Assert.That(
                fixture,
                Is.EqualTo(
                    Path.Combine(
                        Path.GetFullPath isolatedDirectory,
                        "server-58481-1234-a8e720d9bdf449b385ab70eef805342f.log"
                    )
                )
            )

            Assert.That(fixture, Is.Not.EqualTo(production)))

    [<Test>]
    member _.``Default isolated logs use the supplied temporary directory``() =
        let instanceId =
            Guid.Parse("1889540c-dc76-43a3-a9eb-dd64da8ddcbf")

        let path =
            resolve (Log.Destination.Isolated None) instanceId

        Assert.That(
            path,
            Is.EqualTo(
                Path.Combine(
                    Path.GetFullPath tempDirectory,
                    "treemon",
                    "server-logs",
                    "server-58481-1234-1889540cdc7643a3a9ebdd64da8ddcbf.log"
                )
            )
        )

    [<Test>]
    member _.``Concurrent non-production servers receive different log files``() =
        let directory =
            Log.Destination.Isolated(Some isolatedDirectory)

        let first =
            resolve
                directory
                (Guid.Parse("805b6101-19a3-4893-93e9-5743ff8fdac4"))

        let second =
            resolve
                directory
                (Guid.Parse("4e6db448-35bf-4f2a-822c-3566dc95cbaa"))

        Assert.That(second, Is.Not.EqualTo(first))

    [<Test>]
    member _.``Isolated log directory rejects control characters``() =
        let result =
            Log.resolvePath
                workingDirectory
                tempDirectory
                58481
                1234
                Guid.Empty
                (Log.Destination.Isolated(
                    Some($"fixture{Environment.NewLine}forged")
                ))

        match result with
        | Error error ->
            Assert.That(
                error,
                Is.EqualTo(
                    "Log directory must not contain control characters"
                )
            )
        | Ok path ->
            Assert.Fail($"Control-bearing log directory resolved to {path}")

    [<Test>]
    member _.``Log file creation reports an unusable directory``() =
        withTempDir "treemon-log-init" (fun root ->
            let blockingFile = Path.Combine(root, "not-a-directory")
            File.WriteAllText(blockingFile, "test")
            let logPath = Path.Combine(blockingFile, "server.log")

            match Log.tryCreateLogFile logPath with
            | Error(Log.InitializationError.CannotOpen(path, _)) ->
                Assert.That(path, Is.EqualTo(logPath))
            | Error error ->
                Assert.Fail($"Unexpected initialization error: {error}")
            | Ok() ->
                Assert.Fail("Unusable log directory was accepted"))

[<TestFixture>]
[<Category("E2E")>]
type FixtureServerLogTests() =
    inherit ServerFixture.SharedServerFixture()

    [<Test>]
    member _.``Fixture server writes startup diagnostics to its owned log directory``() =
        let logFiles =
            Directory.GetFiles(
                ServerFixture.serverLogDirectory,
                "server-*.log"
            )

        Assert.That(logFiles, Has.Length.EqualTo(1))

        let logPath = logFiles[0]
        use stream =
            new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            )
        use reader = new StreamReader(stream)
        let content = reader.ReadToEnd()

        Assert.Multiple(fun () ->
            Assert.That(
                Path.GetDirectoryName logPath,
                Is.EqualTo(ServerFixture.serverLogDirectory)
            )

            Assert.That(
                content,
                Does.Contain($"Server URL: {ServerFixture.serverUrl}")
            )

            Assert.That(content, Does.Contain("Test fixtures:"))
            Assert.That(Path.GetFileName logPath, Is.Not.EqualTo("server.log")))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FableCompileTests() =

    [<Test>]
    member _.``Fable compile propagates its bounded timeout``() =
        task {
            let capture (spawn: ProcessRunner.Spawn) (arguments: string list) =
                async {
                    Assert.That(
                        spawn.Deadline,
                        Is.EqualTo(ProcessRunner.Timeout 60_000)
                    )

                    Assert.That(
                        arguments,
                        Is.EqualTo(
                            [ "fable"
                              System.IO.Path.Combine("src", "Client")
                              "--outDir"
                              System.IO.Path.Combine("src", "Client", "output") ]
                        )
                    )

                    return Error ProcessRunner.TimedOut
                }

            let! error =
                task {
                    try
                        do! ServerFixture.runFableCompile capture
                        return None
                    with ex ->
                        return Some ex.Message
                }

            Assert.That(
                error,
                Is.EqualTo(Some "Fable compilation timed out after 60s")
            )
        }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalHostStateCleanupTests() =

    [<Test>]
    member _.``cleanup removes isolated state and server logs when no host exists``() =
        let stateDirectory = terminalHostStateDirectory ()
        let logDirectory = serverLogDirectory stateDirectory
        Directory.CreateDirectory(logDirectory) |> ignore
        File.WriteAllText(Path.Combine(logDirectory, "server-test.log"), "test")

        stopTerminalHostState stateDirectory
        |> fun result ->
            assertOk result "Empty TerminalHost state cleanup should succeed"

        Assert.That(Directory.Exists stateDirectory, Is.False)

    [<Test>]
    member _.``cleanup preserves invalid manifest evidence and reports the error``() =
        let stateDirectory = terminalHostStateDirectory ()
        let manifestPath =
            Path.Combine(stateDirectory, TerminalHostLayout.ManifestFileName)

        try
            File.WriteAllText(manifestPath, "{}")

            match stopTerminalHostState stateDirectory with
            | Ok() ->
                Assert.Fail("Invalid TerminalHost state cleanup unexpectedly succeeded")
            | Error error ->
                Assert.Multiple(fun () ->
                    Assert.That(
                        error,
                        Does.Contain("TerminalHost discovery manifest has an invalid shape")
                    )

                    Assert.That(Directory.Exists stateDirectory, Is.True))
        finally
            if Directory.Exists stateDirectory then
                Directory.Delete(stateDirectory, recursive = true)

    [<Test>]
    member _.``cleanup never kills a process whose start time does not match``() =
        let stateDirectory = terminalHostStateDirectory ()
        let manifestPath =
            Path.Combine(stateDirectory, TerminalHostLayout.ManifestFileName)
        use currentProcess = Process.GetCurrentProcess()
        let mismatchedStartTime =
            currentProcess.StartTime.ToUniversalTime().Ticks + 1L
        let bearerToken = String('a', 32)

        File.WriteAllText(
            manifestPath,
            $"""{{"pid":{currentProcess.Id},"processStartTimeUtcTicks":{mismatchedStartTime},"endpoint":"http://127.0.0.1:1/","bearerToken":"{bearerToken}","hostVersion":"test","controlApiVersion":2}}"""
        )

        stopTerminalHostState stateDirectory
        |> fun result ->
            assertOk result "Mismatched TerminalHost identity cleanup should succeed"

        Assert.Multiple(fun () ->
            Assert.That(currentProcess.HasExited, Is.False)
            Assert.That(Directory.Exists stateDirectory, Is.False))
