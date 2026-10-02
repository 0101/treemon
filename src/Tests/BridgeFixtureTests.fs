module Tests.BridgeFixtureTests

open System
open System.Net
open NUnit.Framework
open Tests.BridgeFixture
open Tests.TestUtils

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("BridgeTransport")>]
[<NonParallelizable>]
type BridgeFixtureTests() =

    [<Test>]
    member _.``body failure closes every acquired bridge listener``() =
        let ports = getFreeTcpPorts 2

        Assert.Throws<InvalidOperationException>(fun () ->
            withBridgesAt ports (fun _ -> invalidOp "Fixture body failed"))
        |> ignore

        withBridgesAt ports (fun bridges ->
            bridges
            |> List.iter (fun (listener, _) ->
                Assert.That(listener.IsListening, Is.True)))

    [<Test>]
    member _.``later setup failure closes previously acquired bridge listeners``() =
        let port = getFreeTcpPort ()

        let failure =
            Assert.Catch(fun () ->
                withBridgesAt [ port; -1 ] (fun _ ->
                    Assert.Fail("An invalid second listener must fail during setup")))

        Assert.That(
            failure,
            Is.InstanceOf<ArgumentException>().Or.InstanceOf<HttpListenerException>())

        withBridgesAt [ port ] (fun bridges ->
            let listener, _ = List.exactlyOne bridges
            Assert.That(listener.IsListening, Is.True))
