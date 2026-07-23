module Tests.SessionBridgeTests

open System
open System.IO
open NUnit.Framework
open Server.SessionBridge

let private uniquePath prefix =
    Path.Combine(Path.GetTempPath(), "treemon-session-bridge-tests", prefix, $"{Guid.NewGuid():N}")

let private clockSnapshot = DateTime(2042, 7, 23, 12, 0, 0, DateTimeKind.Utc)

let private sessionEntry registeredAt =
    { WorktreePath = Path.Combine("test", "clock")
      InjectUrl = "http://localhost/inject"
      SessionId = Some "clock-session"
      RegisteredAt = registeredAt }

let private queuedPrompt enqueuedAt text =
    { EnqueuedAt = enqueuedAt
      TargetSessionId = None
      Prompt = Prompt.agentPrompt text }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ClockTests() =

    [<Test>]
    member _.``Queue TTL expires prompts at the exact threshold``() =
        let threshold = clockSnapshot - TimeSpan.FromMinutes 5.0
        let expired = queuedPrompt threshold "expired"
        let fresh = queuedPrompt (threshold.AddTicks 1L) "fresh"

        Assert.That(cleanExpired clockSnapshot [ expired; fresh ], Is.EqualTo [ fresh ])

    [<Test>]
    member _.``Session liveness becomes stale at the exact threshold``() =
        let threshold = clockSnapshot - TimeSpan.FromSeconds 60.0

        Assert.That(isSessionAlive clockSnapshot (sessionEntry (threshold.AddTicks 1L)), Is.True)
        Assert.That(isSessionAlive clockSnapshot (sessionEntry threshold), Is.False)

    [<Test>]
    member _.``Poll liveness becomes stale at the exact threshold``() =
        let threshold = clockSnapshot - TimeSpan.FromSeconds 60.0

        Assert.That(isPollAlive clockSnapshot (threshold.AddTicks 1L), Is.True)
        Assert.That(isPollAlive clockSnapshot threshold, Is.False)

    [<Test>]
    member _.``Combined liveness uses one supplied clock snapshot``() =
        let staleSession = sessionEntry (clockSnapshot - TimeSpan.FromSeconds 60.0)
        let liveHeartbeat = clockSnapshot - TimeSpan.FromSeconds 60.0 + TimeSpan.FromTicks 1L

        let age, liveness =
            computeLiveness clockSnapshot (Some staleSession) (true, liveHeartbeat)
            |> Option.get

        Assert.That(age, Is.EqualTo((clockSnapshot - liveHeartbeat).TotalSeconds))
        Assert.That(liveness.IsAlive, Is.True)
        Assert.That(liveness.SessionId, Is.EqualTo(staleSession.SessionId))

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
