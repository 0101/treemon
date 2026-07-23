module Tests.SessionBridgeTests

open System
open NUnit.Framework
open Server.SessionBridge

let private uniquePath prefix =
    $"/test/{prefix}/{Guid.NewGuid():N}"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PromptTransportTests() =

    [<Test>]
    member _.``Canvas prompt transport has an explicit canvas kind``() =
        Assert.That(
            serializePrompt (Prompt.canvas """{"action":"refresh"}"""),
            Is.EqualTo("""{"kind":"canvas","prompt":"{\u0022action\u0022:\u0022refresh\u0022}"}"""))

    [<Test>]
    member _.``Generic agent prompt transport has an explicit agent-prompt kind``() =
        Assert.That(
            serializePrompt (Prompt.agentPrompt "Sync with upstream/main when safe."),
            Is.EqualTo("""{"kind":"agent-prompt","prompt":"Sync with upstream/main when safe."}"""))

    [<Test>]
    member _.``Canvas heartbeat drain does not consume generic agent prompts``() =
        let path = uniquePath "kind-drain"

        let result =
            send
                { WorktreePath = path
                  SessionId = None
                  Prompt = Prompt.agentPrompt "sync" }
            |> Async.RunSynchronously

        Assert.That(result, Is.EqualTo(SendResult.Queued))
        Assert.That(drainPendingCanvas path, Is.Empty)
