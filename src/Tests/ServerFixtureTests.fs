module Tests.ServerFixtureTests

open NUnit.Framework
open Server

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
