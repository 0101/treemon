module Tests.SessionBridgeTests

open System
open System.IO
open System.Net
open System.Threading.Tasks
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
type QueueCoalescingTests() =

    let pendingAt enqueuedAt targetSessionId prompt =
        { EnqueuedAt = enqueuedAt
          TargetSessionId = targetSessionId
          Prompt = prompt }

    [<Test>]
    member _.``An identical pending prompt is not queued twice``() =
        let existing = pendingAt (clockSnapshot.AddMinutes -1.0) None (Prompt.agentPrompt "sync")
        let duplicate = pendingAt clockSnapshot None (Prompt.agentPrompt "sync")

        Assert.That(appendPending clockSnapshot duplicate [ existing ], Is.EqualTo [ existing ])

    [<Test>]
    member _.``Coalescing keeps FIFO order and still appends distinct prompts``() =
        let first = pendingAt (clockSnapshot.AddMinutes -2.0) None (Prompt.agentPrompt "first")
        let second = pendingAt (clockSnapshot.AddMinutes -1.0) None (Prompt.agentPrompt "second")
        let duplicateOfFirst = pendingAt clockSnapshot None (Prompt.agentPrompt "first")
        let third = pendingAt clockSnapshot None (Prompt.agentPrompt "third")

        Assert.Multiple(fun () ->
            Assert.That(
                appendPending clockSnapshot duplicateOfFirst [ first; second ],
                Is.EqualTo [ first; second ])
            Assert.That(
                appendPending clockSnapshot third [ first; second ],
                Is.EqualTo [ first; second; third ]))

    [<Test>]
    member _.``Every equality field keeps a pending prompt distinct``() =
        let pending =
            pendingAt (clockSnapshot.AddMinutes -1.0) (Some "session-a") (Prompt.canvasFor "review.html" "text")

        let variants =
            [ { pending with TargetSessionId = Some "session-b" }
              { pending with Prompt = { pending.Prompt with Kind = PromptKind.AgentPrompt } }
              { pending with Prompt = { pending.Prompt with Text = "other text" } }
              { pending with Prompt = { pending.Prompt with Filename = Some "other.html" } } ]
            |> List.map (fun variant -> { variant with EnqueuedAt = clockSnapshot })

        Assert.Multiple(fun () ->
            for variant in variants do
                Assert.That(appendPending clockSnapshot variant [ pending ], Is.EqualTo [ pending; variant ]))

    [<Test>]
    member _.``An expired duplicate does not suppress the new prompt``() =
        let expired = pendingAt (clockSnapshot - TimeSpan.FromMinutes 5.0) None (Prompt.agentPrompt "sync")
        let fresh = pendingAt clockSnapshot None (Prompt.agentPrompt "sync")

        Assert.That(appendPending clockSnapshot fresh [ expired ], Is.EqualTo [ fresh ])

    [<Test>]
    member _.``The queue cap still drops the oldest entry``() =
        let pending =
            [ for index in 1..10 ->
                pendingAt (clockSnapshot.AddSeconds(float index)) None (Prompt.agentPrompt $"prompt-{index}") ]

        let extra = pendingAt clockSnapshot None (Prompt.agentPrompt "prompt-11")

        Assert.That(appendPending clockSnapshot extra pending, Is.EqualTo(List.tail pending @ [ extra ]))

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
    member _.``Untargeted agent prompt uses the only live bridge``() =
        let path = uniquePath "unique-live"
        let sessionId = $"session-{Guid.NewGuid():N}"
        let port = Tests.TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()
        registerSession path $"http://127.0.0.1:{port}/" (Some sessionId)

        let received = listener.GetContextAsync()
        let delivery =
            tryDeliver
                { WorktreePath = path
                  SessionId = None
                  Prompt = Prompt.agentPrompt "sync" }
            |> Async.StartAsTask

        let context = received.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        context.Response.StatusCode <- 200
        context.Response.Close()

        Assert.That(delivery.GetAwaiter().GetResult(), Is.EqualTo(DeliveryResult.Delivered))

    [<Test>]
    member _.``Untargeted agent prompt remains ambiguous with multiple live bridges``() =
        let path = uniquePath "ambiguous-live"
        let firstPort, secondPort =
            match Tests.TestUtils.getFreeTcpPorts 2 with
            | [ first; second ] -> first, second
            | ports -> failwith $"expected two free ports, got {ports.Length}"

        use first = new HttpListener()
        use second = new HttpListener()
        first.Prefixes.Add($"http://127.0.0.1:{firstPort}/")
        second.Prefixes.Add($"http://127.0.0.1:{secondPort}/")
        first.Start()
        second.Start()
        registerSession path $"http://127.0.0.1:{firstPort}/" (Some $"first-{Guid.NewGuid():N}")
        registerSession path $"http://127.0.0.1:{secondPort}/" (Some $"second-{Guid.NewGuid():N}")

        let result =
            tryDeliver
                { WorktreePath = path
                  SessionId = None
                  Prompt = Prompt.agentPrompt "sync" }
            |> Async.RunSynchronously

        Assert.That(result, Is.EqualTo(DeliveryResult.NoLiveSession))

    [<Test>]
    member _.``Anonymous canvas prompt still queues for canvas polling``() =
        let path = uniquePath "canvas-poll"
        let payload = """{"action":"refresh"}"""
        let port = Tests.TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()
        registerSession path $"http://127.0.0.1:{port}/" (Some $"canvas-{Guid.NewGuid():N}")

        let unexpectedPost = listener.GetContextAsync()
        let sending =
            send
                { WorktreePath = path
                  SessionId = None
                  Prompt = Prompt.canvas payload }
            |> Async.StartAsTask
        let firstCompleted =
            Task.WhenAny(sending, unexpectedPost).WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        if Object.ReferenceEquals(firstCompleted, unexpectedPost) then
            let context = unexpectedPost.GetAwaiter().GetResult()
            context.Response.StatusCode <- 200
            context.Response.Close()

        let result = sending.GetAwaiter().GetResult()

        Assert.That(result, Is.EqualTo(SendResult.Queued))
        Assert.That(drainPendingCanvas path, Is.EqualTo [ Prompt.canvas payload ])

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

    [<Test>]
    member _.``Duplicate pending prompts deliver once when a session registers``() =
        let path = uniquePath "duplicate-pending"
        let prompt = Prompt.agentPrompt "sync"
        let port = Tests.TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let queueOnce () =
            send
                { WorktreePath = path
                  SessionId = None
                  Prompt = prompt }
            |> Async.RunSynchronously

        Assert.That(queueOnce (), Is.EqualTo(SendResult.Queued))
        Assert.That(queueOnce (), Is.EqualTo(SendResult.Queued))

        let firstPost = listener.GetContextAsync()
        registerSession path $"http://127.0.0.1:{port}/" (Some $"drain-{Guid.NewGuid():N}")

        let context = firstPost.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        context.Response.StatusCode <- 200
        context.Response.Close()

        let secondPost = listener.GetContextAsync()
        let settled =
            Task.WhenAny(secondPost, Task.Delay(TimeSpan.FromSeconds 1.0)).GetAwaiter().GetResult()

        Assert.That(Object.ReferenceEquals(settled, secondPost), Is.False, "duplicate prompt was delivered twice")

    [<Test>]
    member _.``Bridge failure formatting excludes the response body``() =
        let secretBody = $"first line{Environment.NewLine}secret-token=abc123"
        let failure = formatPostFailure 503 secretBody

        Assert.Multiple(fun () ->
            Assert.That(failure, Does.Contain("status=503"))
            Assert.That(failure, Does.Contain($"bodyLength={secretBody.Length}"))
            Assert.That(failure, Does.Not.Contain("first line"))
            Assert.That(failure, Does.Not.Contain("secret-token")))
