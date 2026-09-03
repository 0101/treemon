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
    member _.``cleanup removes isolated state when no host exists``() =
        let stateDirectory = terminalHostStateDirectory ()

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
