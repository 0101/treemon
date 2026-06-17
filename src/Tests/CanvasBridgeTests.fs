module Tests.CanvasBridgeTests

open System
open System.IO
open System.Text
open System.Threading
open System.Net
open System.Net.Sockets
open System.Collections.Concurrent
open NUnit.Framework
open Shared
open Server.CanvasBridge
open Server.RefreshScheduler.CanvasWatchers
open Tests.TestUtils

// Each test uses a unique worktree path to avoid shared-state interference
// between tests (the module uses ConcurrentDictionaries at module level).
let private uniquePath prefix =
    let id = Guid.NewGuid().ToString("N")[..7]
    $"/test/{prefix}/{id}"

// Unique session IDs keep tests isolated now that the registry is keyed by sessionId
// (a shared literal like "s1" would otherwise collide across tests).
let private uniqueSid prefix =
    let id = Guid.NewGuid().ToString("N")[..7]
    $"{prefix}-{id}"

// A minimal loopback HTTP sink used to assert *which* inject URL a message reaches.
// It speaks just enough HTTP/1.1 over a raw TcpListener so no HttpListener URL ACL /
// admin rights are needed on Windows. Each received request body is recorded and a
// 200 is returned, so by the time sendMessage's POST completes the body is observable.
// Payloads are ASCII in tests, so comparing char length to byte Content-Length is exact.
type private HttpSink(port: int) =
    let bodies = ConcurrentQueue<string>()
    let listener = new TcpListener(IPAddress.Loopback, port)
    let cts = new CancellationTokenSource()

    let handle (client: TcpClient) =
        async {
            try
                use client = client
                use stream = client.GetStream()
                let buffer = Array.zeroCreate 8192
                let sb = StringBuilder()

                // Read until the end-of-headers marker is seen (or the peer closes).
                let rec readToHeaderEnd () =
                    async {
                        let! n = stream.ReadAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
                        if n = 0 then
                            return sb.Length
                        else
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, n)) |> ignore
                            let idx = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)
                            if idx >= 0 then return idx
                            else return! readToHeaderEnd ()
                    }

                let! headerEnd = readToHeaderEnd ()

                let text = sb.ToString()

                let contentLength =
                    text.Substring(0, min headerEnd text.Length).Split([| "\r\n" |], StringSplitOptions.None)
                    |> Array.tryPick (fun line ->
                        let i = line.IndexOf(':')
                        if i > 0 && line.Substring(0, i).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) then
                            match Int32.TryParse(line.Substring(i + 1).Trim()) with
                            | true, v -> Some v
                            | _ -> None
                        else
                            None)
                    |> Option.defaultValue 0

                let body = StringBuilder()
                let bodyStart = headerEnd + 4
                if text.Length > bodyStart then body.Append(text.Substring(bodyStart)) |> ignore

                // Drain the socket until the full Content-Length is buffered (or the peer closes).
                let rec readToContentLength () =
                    async {
                        if body.Length >= contentLength then
                            return ()
                        else
                            let! n = stream.ReadAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
                            if n = 0 then
                                return ()
                            else
                                body.Append(Encoding.UTF8.GetString(buffer, 0, n)) |> ignore
                                return! readToContentLength ()
                    }

                do! readToContentLength ()

                bodies.Enqueue(body.ToString())

                let resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                do! stream.WriteAsync(resp, 0, resp.Length) |> Async.AwaitTask
                do! stream.FlushAsync() |> Async.AwaitTask
            with _ ->
                ()
        }

    /// The inject URL callers register; POSTs to it are captured by this sink.
    member _.Url = $"http://127.0.0.1:{port}/inject"
    /// Snapshot of request bodies received so far, in arrival order.
    member _.Bodies = bodies |> List.ofSeq

    member _.Start() =
        // Start() binds the listening socket synchronously, so connections are queued by
        // the OS even before the accept loop runs — no connect-races in tests.
        listener.Start()

        let rec acceptLoop () =
            async {
                if not cts.IsCancellationRequested then
                    let! client = listener.AcceptTcpClientAsync(cts.Token).AsTask() |> Async.AwaitTask
                    handle client |> Async.Start
                    return! acceptLoop ()
            }

        async {
            try
                do! acceptLoop ()
            with _ ->
                ()
        }
        |> Async.Start

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            try listener.Stop() with _ -> ()
            cts.Dispose()


// ── isAlive (via getStatus) ──────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type IsAliveTests() =

    [<Test>]
    member _.``Session registered just now shows alive``() =
        let path = uniquePath "alive-now"
        registerSession path "http://localhost/inject" (Some(uniqueSid "alive"))
        Assert.That((getStatus path).IsAlive, Is.True)

    [<Test>]
    member _.``Poll registered just now shows alive``() =
        let path = uniquePath "alive-poll"
        registerPoll path
        Assert.That((getStatus path).IsAlive, Is.True)

    [<Test>]
    member _.``Unregistered path shows not alive``() =
        let path = uniquePath "alive-unreg"
        Assert.That((getStatus path).IsAlive, Is.False)


// ── register + getStatus (SessionId tracking) ───────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RegisterAndStatusTests() =

    [<Test>]
    member _.``Register with sessionId makes getStatus show alive with sessionId``() =
        let path = uniquePath "reg-sid"
        let sid = uniqueSid "session"
        registerSession path "http://localhost:1234/inject" (Some sid)

        let status = getStatus path
        Assert.That(status.Registered, Is.True)
        Assert.That(status.IsAlive, Is.True)
        Assert.That(status.SessionId, Is.EqualTo(Some sid))

    [<Test>]
    member _.``Register without sessionId shows None``() =
        let path = uniquePath "reg-nosid"
        registerSession path "http://localhost:1234/inject" None

        let status = getStatus path
        Assert.That(status.Registered, Is.True)
        Assert.That(status.SessionId, Is.EqualTo(None))

    // Finding C-01: /api/canvas/register accepted a blank sessionId and Option.ofObj stored it as
    // Some "" (only null mapped to None). The scanner's fallbackOwner then stamped docs with owner
    // "", and sendMessage routed only to SessionId = Some "" — which no real Some "real-id" session
    // equals — so messages queued forever. A blank sessionId must collapse to None (anonymous).
    [<TestCase("")>]
    [<TestCase("   ")>]
    member _.``Register with blank sessionId is treated as anonymous None``(blank: string) =
        let path = uniquePath "reg-blank-sid"
        registerSession path "http://localhost:1234/inject" (Some blank)

        let status = getStatus path
        Assert.That(status.Registered, Is.True)
        Assert.That(status.SessionId, Is.EqualTo(None), "A blank sessionId must be stored as None, not Some \"\"")

        // No registry entry may carry Some "" — that is the value that would poison routing.
        Assert.That(
            sessionsForWorktree path |> List.choose _.SessionId,
            Is.Empty,
            "No session entry may carry Some \"\"")

        // The downstream scanner fallback therefore finds no id to credit: a single anonymous
        // session leaves docs unowned instead of stamping the sticky, unroutable owner "".
        Assert.That(fallbackOwner (sessionsForWorktree path), Is.EqualTo(None: string option),
                    "A single blank-id session must leave docs unowned, not owned by \"\"")

    [<Test>]
    member _.``getStatus for unregistered path returns not registered``() =
        let path = uniquePath "unreg"

        let status = getStatus path
        Assert.That(status.Registered, Is.False)
        Assert.That(status.IsAlive, Is.False)
        Assert.That(status.SessionId, Is.EqualTo(None))

    [<Test>]
    member _.``Distinct sessionIds for one worktree coexist; status reports the freshest``() =
        let path = uniquePath "re-reg"
        let sid1 = uniqueSid "s1"
        let sid2 = uniqueSid "s2"
        registerSession path "http://localhost:1111/inject" (Some sid1)
        registerSession path "http://localhost:2222/inject" (Some sid2)

        // The re-key fix: a second session no longer clobbers the first — both are retained.
        let sessions = sessionsForWorktree path
        Assert.That(List.length sessions, Is.EqualTo 2, "Distinct sessionIds for one worktree must coexist")
        Assert.That(
            sessions |> List.choose _.SessionId |> List.sort,
            Is.EqualTo(List.sort [ sid1; sid2 ]))

        // The single-status view reports the most-recently-registered session.
        let status = getStatus path
        Assert.That(status.IsAlive, Is.True)
        Assert.That(status.SessionId, Is.EqualTo(Some sid2))

    [<Test>]
    member _.``getAllLiveness returns liveness for registered paths``() =
        let path1 = uniquePath "live1"
        let path2 = uniquePath "live2"
        let path3 = uniquePath "live3"
        let sid1 = uniqueSid "s1"
        registerSession path1 "http://localhost/inject" (Some sid1)
        registerSession path2 "http://localhost/inject" None

        let result = getAllLiveness [ path1; path2; path3 ]

        Assert.That(result |> Map.containsKey path1, Is.True)
        Assert.That(result[path1].IsAlive, Is.True)
        Assert.That(result[path1].SessionId, Is.EqualTo(Some sid1))
        Assert.That(result |> Map.containsKey path2, Is.True)
        Assert.That(result[path2].SessionId, Is.EqualTo(None))
        Assert.That(result |> Map.containsKey path3, Is.False, "Unregistered path should not appear")

    [<Test>]
    member _.``Poll heartbeat does not overwrite session registration``() =
        let path = uniquePath "no-overwrite"
        let sid = uniqueSid "session"
        registerSession path "http://localhost:1234/inject" (Some sid)
        registerPoll path

        let status = getStatus path
        Assert.That(status.SessionId, Is.EqualTo(Some sid), "Poll should not erase session ID")
        Assert.That(status.IsAlive, Is.True)

    [<Test>]
    member _.``Poll-only registration shows alive without sessionId``() =
        let path = uniquePath "poll-only"
        registerPoll path

        let status = getStatus path
        Assert.That(status.Registered, Is.True)
        Assert.That(status.IsAlive, Is.True)
        Assert.That(status.SessionId, Is.EqualTo(None))


// ── enqueue via sendMessage (no bridge registered) ──────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EnqueueTests() =

    [<Test>]
    member _.``sendMessage to unregistered bridge returns Queued``() =
        let path = uniquePath "enq-basic"
        let request = { WorktreePath = WorktreePath path; Filename = ""; Payload = "test-payload" }

        let result = sendMessage request |> Async.RunSynchronously

        Assert.That(result, Is.EqualTo(CanvasMessageResult.Queued))

    [<Test>]
    member _.``Multiple messages to unregistered bridge all return Queued``() =
        let path = uniquePath "enq-multi"

        let results =
            [ 1..5 ]
            |> List.map (fun i ->
                let request = { WorktreePath = WorktreePath path; Filename = ""; Payload = $"msg-{i}" }
                sendMessage request |> Async.RunSynchronously)

        results |> List.iter (fun r -> Assert.That(r, Is.EqualTo(CanvasMessageResult.Queued)))

    [<Test>]
    member _.``Queue respects max 10 size limit — oldest messages dropped``() =
        let path = uniquePath "enq-limit"

        // Enqueue 12 messages — only the last 10 should survive (oldest two evicted).
        [ 1..12 ]
        |> List.iter (fun i ->
            let request = { WorktreePath = WorktreePath path; Filename = ""; Payload = $"msg-{i}" }
            sendMessage request |> Async.RunSynchronously |> ignore)

        // Drain in-process (no HTTP) and inspect the survivors directly.
        let survivors = drainPending path

        Assert.That(List.length survivors, Is.EqualTo(10), "Queue should cap at 10 survivors")
        Assert.That(
            survivors,
            Is.EqualTo([ for i in 3..12 -> $"msg-{i}" ]),
            "Oldest two (msg-1, msg-2) should be evicted, leaving msg-3..msg-12 in order"
        )


// ── drainQueue via register ─────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DrainQueueTests() =

    [<Test>]
    member _.``Register drains queue — subsequent sendMessage returns Queued not from old queue``() =
        let path = uniquePath "drain-basic"

        // Enqueue a message (no bridge registered)
        let req = { WorktreePath = WorktreePath path; Filename = ""; Payload = "queued-msg" }
        sendMessage req |> Async.RunSynchronously |> ignore

        // Register bridge — this triggers drainQueue (HTTP will fail silently)
        registerSession path "http://127.0.0.1:1/inject" None

        // After drain, sending another message should go to the bridge (not queue)
        // Since the bridge URL is unreachable, it will return Error, not Queued
        let req2 = { WorktreePath = WorktreePath path; Filename = ""; Payload = "new-msg" }
        let result = sendMessage req2 |> Async.RunSynchronously

        Assert.That(result, Is.Not.EqualTo(CanvasMessageResult.Queued),
            "After registration, messages should attempt delivery, not queue")

    [<Test>]
    member _.``Register with no queued messages works without error``() =
        let path = uniquePath "drain-empty"

        // Register without any queued messages — should not throw
        registerSession path "http://127.0.0.1:1/inject" (Some(uniqueSid "s1"))

        let status = getStatus path
        Assert.That(status.Registered, Is.True)

    [<Test>]
    member _.``Register with a sessionId drains the worktree-keyed queue``() =
        let path = uniquePath "drain-sid"

        // Enqueue with no bridge registered (the queue is keyed by worktree path).
        let req = { WorktreePath = WorktreePath path; Filename = ""; Payload = "queued-msg" }
        sendMessage req |> Async.RunSynchronously |> ignore

        // Register an *identified* (sid:-keyed) session. The registry is no longer
        // worktree-keyed, so this guards that drainQueue still locates the worktree-keyed
        // queue and removes it (forwarding to the entry; the unreachable HTTP fails silently).
        registerSession path "http://127.0.0.1:1/inject" (Some(uniqueSid "drainer"))

        // The worktree-keyed queue was drained on registration — nothing remains to poll.
        Assert.That(drainPending path, Is.Empty,
            "A sid-keyed registration must drain the worktree-keyed queue")

    [<Test>]
    member _.``Queue is cleared after drain — second register has nothing to drain``() =
        let path = uniquePath "drain-clear"

        // Enqueue
        let req = { WorktreePath = WorktreePath path; Filename = ""; Payload = "test" }
        sendMessage req |> Async.RunSynchronously |> ignore

        // First register drains
        registerSession path "http://127.0.0.1:1/inject" None
        // Second register — queue should already be empty
        registerSession path "http://127.0.0.1:2/inject" None

        // If queue was properly drained, this just works without issues
        let status = getStatus path
        Assert.That(status.Registered, Is.True)


// ── multi-session registry (sessionId-keyed re-key) ─────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MultiSessionRegistryTests() =

    [<Test>]
    member _.``sessionsForWorktree returns every distinct session for the worktree``() =
        let path = uniquePath "multi-all"
        let a = uniqueSid "a"
        let b = uniqueSid "b"
        let c = uniqueSid "c"
        registerSession path "http://localhost:1/inject" (Some a)
        registerSession path "http://localhost:2/inject" (Some b)
        registerSession path "http://localhost:3/inject" (Some c)

        let ids = sessionsForWorktree path |> List.choose _.SessionId |> List.sort
        Assert.That(ids, Is.EqualTo(List.sort [ a; b; c ]))

    [<Test>]
    member _.``Re-registering the same sessionId upserts in place (no duplicate)``() =
        let path = uniquePath "multi-upsert"
        let sid = uniqueSid "dup"
        registerSession path "http://localhost:1/inject" (Some sid)
        registerSession path "http://localhost:2/inject" (Some sid)

        let sessions = sessionsForWorktree path
        Assert.That(List.length sessions, Is.EqualTo 1, "Same sessionId must not create a second entry")
        Assert.That(sessions.Head.InjectUrl, Is.EqualTo "http://localhost:2/inject", "Latest registration wins the slot")

    [<Test>]
    member _.``Two None registrations for one worktree collapse to a single slot``() =
        let path = uniquePath "multi-none"
        registerSession path "http://localhost:1/inject" None
        registerSession path "http://localhost:2/inject" None

        let sessions = sessionsForWorktree path
        Assert.That(List.length sessions, Is.EqualTo 1, "None registrations share the per-worktree fallback slot")
        Assert.That(sessions.Head.InjectUrl, Is.EqualTo "http://localhost:2/inject")
        Assert.That(sessions.Head.SessionId, Is.EqualTo None)

    [<Test>]
    member _.``A None registration and a sessionId registration coexist``() =
        let path = uniquePath "multi-mixed"
        let sid = uniqueSid "s"
        registerSession path "http://localhost:1/inject" None
        registerSession path "http://localhost:2/inject" (Some sid)

        let sessions = sessionsForWorktree path
        Assert.That(List.length sessions, Is.EqualTo 2, "Anonymous and identified sessions must coexist")
        Assert.That(sessions |> List.exists (fun e -> e.SessionId = None), Is.True)
        Assert.That(sessions |> List.exists (fun e -> e.SessionId = Some sid), Is.True)

    [<Test>]
    member _.``sessionsForWorktree isolates sessions by worktree``() =
        let pathA = uniquePath "multi-iso-a"
        let pathB = uniquePath "multi-iso-b"
        let a1 = uniqueSid "a1"
        let a2 = uniqueSid "a2"
        let b1 = uniqueSid "b1"
        registerSession pathA "http://localhost:1/inject" (Some a1)
        registerSession pathA "http://localhost:2/inject" (Some a2)
        registerSession pathB "http://localhost:3/inject" (Some b1)

        Assert.That(sessionsForWorktree pathA |> List.length, Is.EqualTo 2)
        Assert.That(sessionsForWorktree pathB |> List.choose _.SessionId, Is.EqualTo [ b1 ])
        Assert.That(sessionsForWorktree (uniquePath "multi-iso-empty") |> List.isEmpty, Is.True)

    [<Test>]
    member _.``getSessionForWorktree returns the most recently registered sessionId``() =
        let path = uniquePath "multi-latest"
        let older = uniqueSid "older"
        let newer = uniqueSid "newer"
        registerSession path "http://localhost:1/inject" (Some older)
        registerSession path "http://localhost:2/inject" (Some newer)

        Assert.That(getSessionForWorktree path, Is.EqualTo(Some newer))

    [<Test>]
    member _.``Multi-session worktree reports alive while at least one session is live``() =
        let path = uniquePath "multi-live"
        let a = uniqueSid "a"
        let b = uniqueSid "b"
        registerSession path "http://localhost:1/inject" (Some a)
        registerSession path "http://localhost:2/inject" (Some b)

        let liveness = getAllLiveness [ path ]
        Assert.That(liveness |> Map.containsKey path, Is.True)
        Assert.That(liveness[path].IsAlive, Is.True)
        Assert.That(liveness[path].SessionId, Is.EqualTo(Some b), "Liveness reflects the freshest session")


// ── owner-aware routing (sendMessage by doc owner) ──────────────────
// These prove the core fix: a doc's message goes to its *owning* session and is never
// cross-routed to a co-located non-owner. The result value is itself a routing probe —
// Queued means "not delivered" while Error means "delivery attempted but failed" — so the
// no-owner / offline branches are verifiable against unreachable URLs without a live sink.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
// CWD-mutating (withTempCwd writes data/canvas-owners.json) and port-reserving (getFreeTcpPorts):
// only safe because NUnit runs sequentially today. Mark NonParallelizable so enabling assembly
// parallelization can never race CWD against another fixture or clobber the real owners file.
[<NonParallelizable>]
type OwnerRoutingTests() =

    [<Test>]
    member _.``Owner-alive message is delivered to the owner, never the co-located non-owner``() =
        withTempCwd (fun () ->
            let ports = getFreeTcpPorts 2
            use ownerSink = new HttpSink(ports[0])
            use otherSink = new HttpSink(ports[1])
            ownerSink.Start()
            otherSink.Start()

            let path = uniquePath "own-deliver"
            let ownerSid = uniqueSid "owner"
            let otherSid = uniqueSid "other"

            // Owner registers first; the non-owner registers last so it is the *freshest*
            // session. The old "deliver to the freshest live session" path would wrongly
            // pick the non-owner — owner-aware routing must still target the owner.
            registerSession path ownerSink.Url (Some ownerSid)
            registerSession path otherSink.Url (Some otherSid)
            Server.CanvasDocOwnership.attribute path "a.html" ownerSid

            let request = { WorktreePath = WorktreePath path; Filename = "a.html"; Payload = "p1" }
            let result = runAsync (sendMessage request)

            Assert.That(result, Is.EqualTo(CanvasMessageResult.Ok), "Owner is live, so delivery should succeed")
            Assert.That(ownerSink.Bodies, Is.EqualTo([ "p1" ]), "Owner session must receive the message")
            Assert.That(otherSink.Bodies, Is.Empty, "Non-owner in the same worktree must never receive it"))

    [<Test>]
    member _.``Each doc routes to its own owner within a shared worktree``() =
        withTempCwd (fun () ->
            let ports = getFreeTcpPorts 2
            use sinkA = new HttpSink(ports[0])
            use sinkB = new HttpSink(ports[1])
            sinkA.Start()
            sinkB.Start()

            let path = uniquePath "own-perdoc"
            let sidA = uniqueSid "A"
            let sidB = uniqueSid "B"
            registerSession path sinkA.Url (Some sidA)
            registerSession path sinkB.Url (Some sidB)
            Server.CanvasDocOwnership.attribute path "a.html" sidA
            Server.CanvasDocOwnership.attribute path "b.html" sidB

            let rA = runAsync (sendMessage { WorktreePath = WorktreePath path; Filename = "a.html"; Payload = "pa" })
            let rB = runAsync (sendMessage { WorktreePath = WorktreePath path; Filename = "b.html"; Payload = "pb" })

            Assert.That(rA, Is.EqualTo(CanvasMessageResult.Ok))
            Assert.That(rB, Is.EqualTo(CanvasMessageResult.Ok))
            Assert.That(sinkA.Bodies, Is.EqualTo([ "pa" ]), "a.html delivers to owner A only")
            Assert.That(sinkB.Bodies, Is.EqualTo([ "pb" ]), "b.html delivers to owner B only"))

    [<Test>]
    member _.``Owner offline queues and never falls back to a live non-owner``() =
        withTempCwd (fun () ->
            let path = uniquePath "own-offline"
            let ownerSid = uniqueSid "owner-gone"
            let otherSid = uniqueSid "other-live"

            // The owner is attributed but never registers a bridge; a *different* session is
            // live with an unreachable URL. If routing wrongly fell back to that non-owner it
            // would POST and fail (-> Error); correct owner-aware routing queues instead.
            registerSession path "http://127.0.0.1:1/inject" (Some otherSid)
            Server.CanvasDocOwnership.attribute path "a.html" ownerSid

            let result = runAsync (sendMessage { WorktreePath = WorktreePath path; Filename = "a.html"; Payload = "p1" })

            Assert.That(result, Is.EqualTo(CanvasMessageResult.Queued),
                "Owner offline must queue, never deliver to a co-located non-owner"))

    [<Test>]
    member _.``No owner with exactly one live session attempts delivery (back-compat)``() =
        // No attribution -> no owner. One live session with an unreachable URL: single-session
        // back-compat delivers to it (POST attempted -> Error), proving it does not queue.
        let path = uniquePath "own-single"
        registerSession path "http://127.0.0.1:1/inject" (Some(uniqueSid "solo"))

        let result = runAsync (sendMessage { WorktreePath = WorktreePath path; Filename = "lonely.html"; Payload = "p1" })

        Assert.That(result, Is.Not.EqualTo(CanvasMessageResult.Queued),
            "A single live session with no owner should attempt delivery, not queue")

    [<Test>]
    member _.``No owner with multiple live sessions queues (ambiguous)``() =
        // Two live sessions and no declared owner: the target is ambiguous, so the message is
        // queued and delivered to neither. A wrong "freshest wins" path would POST -> Error.
        let path = uniquePath "own-ambig"
        registerSession path "http://127.0.0.1:1/inject" (Some(uniqueSid "s1"))
        registerSession path "http://127.0.0.1:2/inject" (Some(uniqueSid "s2"))

        let result = runAsync (sendMessage { WorktreePath = WorktreePath path; Filename = "x.html"; Payload = "p1" })

        Assert.That(result, Is.EqualTo(CanvasMessageResult.Queued),
            "Two live sessions and no owner is ambiguous -> queue, deliver to neither")

    [<Test>]
    member _.``No owner with no live session queues``() =
        let path = uniquePath "own-empty"
        let result = runAsync (sendMessage { WorktreePath = WorktreePath path; Filename = "x.html"; Payload = "p1" })
        Assert.That(result, Is.EqualTo(CanvasMessageResult.Queued))

    [<Test>]
    member _.``Owner-offline queued message drains to the owner, never a non-owner or anonymous poll``() =
        // The core drain fix: a message queued while its owner is offline must survive an
        // anonymous heartbeat poll and a co-located NON-owner re-registering, and only the
        // owner may drain it when it (re-)registers. drainQueue POSTs asynchronously, so the
        // owner's sink is polled with a bounded wait.
        withTempCwd (fun () ->
            let ports = getFreeTcpPorts 2
            use ownerSink = new HttpSink(ports[0])
            use otherSink = new HttpSink(ports[1])
            ownerSink.Start()
            otherSink.Start()

            let path = uniquePath "drain-owner"
            let ownerSid = uniqueSid "owner"
            let otherSid = uniqueSid "other"

            // Attribute the doc to an owner that never registers -> the send queues (owner offline).
            Server.CanvasDocOwnership.attribute path "a.html" ownerSid
            let queued = runAsync (sendMessage { WorktreePath = WorktreePath path; Filename = "a.html"; Payload = "p1" })
            Assert.That(queued, Is.EqualTo(CanvasMessageResult.Queued), "Owner offline must queue")

            // An anonymous heartbeat poll must NOT collect an owner-bound message (it is re-queued).
            Assert.That(drainPending path, Is.Empty, "Anonymous poll must not receive an owner-bound message")

            // A co-located NON-owner re-registering must NOT drain it either.
            registerSession path otherSink.Url (Some otherSid)
            Thread.Sleep 250 // let any (incorrect) async drain POST land before asserting absence
            Assert.That(otherSink.Bodies, Is.Empty, "A non-owner must never drain an owner-bound message")

            // The owner registering drains it to the owner's inject URL.
            registerSession path ownerSink.Url (Some ownerSid)

            let deadline = DateTime.UtcNow.AddSeconds 5.0
            let rec waitForDelivery () =
                if List.isEmpty ownerSink.Bodies && DateTime.UtcNow < deadline then
                    Thread.Sleep 50
                    waitForDelivery ()

            waitForDelivery ()

            Assert.That(ownerSink.Bodies, Is.EqualTo([ "p1" ]), "Owner must receive its queued message on re-register")
            Assert.That(otherSink.Bodies, Is.Empty, "Non-owner still must not have received it"))


// ── scanner fallback-only attribution (RefreshScheduler.CanvasWatchers) ──────
// The scanner is the *fallback* attribution path only: it may attribute a no-owner changed
// doc to the worktree's bridge session ONLY when exactly one session is registered. The
// original bug attributed every changed doc to the last-registered session
// (getSessionForWorktree), cross-crediting docs whenever two sessions shared a worktree.
// These guard the fix end-to-end (through the real CanvasBridge registry + CanvasDocOwnership)
// and the pure single-session decision in isolation.

// A doc as the scanner would surface it: OwnerSessionId carries the owner read from the
// ownership map during the scan (None = no declared owner yet).
let private scannedDoc (owner: string option) filename : CanvasDoc =
    { Filename = filename
      ContentHash = "h-" + filename
      LastModified = DateTimeOffset(DateTime(2026, 1, 1), TimeSpan.Zero)
      OwnerSessionId = owner
      Kind = CanvasDocKind.classify filename }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
// withTempCwd mutates CWD and the tests share the module-level ownership agent: only safe
// because NUnit runs sequentially today. NonParallelizable guards against enabling assembly
// parallelization (matches OwnerRoutingTests above).
[<NonParallelizable>]
type ScannerFallbackAttributionTests() =

    [<Test>]
    member _.``fallbackOwner attributes only when exactly one session is registered``() =
        let entry sid : SessionEntry =
            { WorktreePath = "/w"; InjectUrl = "http://localhost/inject"; SessionId = sid; RegisteredAt = DateTime.UtcNow }

        Assert.That(fallbackOwner [], Is.EqualTo None, "Zero sessions -> no fallback owner")
        Assert.That(fallbackOwner [ entry (Some "solo") ], Is.EqualTo(Some "solo"), "Exactly one session -> it is the owner")
        Assert.That(fallbackOwner [ entry None ], Is.EqualTo None, "A single anonymous session has no id to attribute")
        Assert.That(fallbackOwner [ entry (Some "a"); entry (Some "b") ], Is.EqualTo None,
            "Two sessions are ambiguous -> leave unowned (the misattribution guard)")

    [<Test>]
    member _.``Two registered sessions leave a no-owner changed doc UNOWNED (misattribution regression)``() =
        withTempCwd (fun () ->
            let path = uniquePath "scan-two"
            registerSession path "http://localhost:1/inject" (Some(uniqueSid "a"))
            registerSession path "http://localhost:2/inject" (Some(uniqueSid "b"))

            // previousDocs = [] makes the doc new (change-gate satisfied); two sessions means the
            // scanner must NOT guess an owner — the exact scenario the old last-registered code broke.
            attributeChangedDocs (sessionsForWorktree path) path [] [ scannedDoc None "report.html" ]

            Assert.That(runAsync (Server.CanvasDocOwnership.getOwner path "report.html"), Is.EqualTo None,
                "Two sessions share the worktree: a no-owner doc must be left unowned, never last-registered"))

    [<Test>]
    member _.``Exactly one registered session attributes a no-owner changed doc to it``() =
        withTempCwd (fun () ->
            let path = uniquePath "scan-one"
            let sid = uniqueSid "solo"
            registerSession path "http://localhost:1/inject" (Some sid)

            attributeChangedDocs (sessionsForWorktree path) path [] [ scannedDoc None "report.html" ]

            Assert.That(runAsync (Server.CanvasDocOwnership.getOwner path "report.html"), Is.EqualTo(Some sid),
                "A single registered session is the unambiguous fallback owner"))

    [<Test>]
    member _.``A pre-declared owner is never overwritten by the scanner``() =
        withTempCwd (fun () ->
            let path = uniquePath "scan-declared"
            let declared = uniqueSid "declared"
            let scanner = uniqueSid "scanner"
            // The single registered session differs from the doc's declared owner.
            registerSession path "http://localhost:1/inject" (Some scanner)
            Server.CanvasDocOwnership.attribute path "owned.html" declared

            // The scan surfaces the declared owner on the doc (OwnerSessionId = Some declared); the
            // scanner must skip it even though the doc looks new and a single session is registered.
            attributeChangedDocs (sessionsForWorktree path) path [] [ scannedDoc (Some declared) "owned.html" ]

            Assert.That(runAsync (Server.CanvasDocOwnership.getOwner path "owned.html"), Is.EqualTo(Some declared),
                "An explicit declaration is primary: the scanner must not overwrite it with the registered session"))

    [<Test>]
    member _.``An unchanged doc is not attributed even with a single session``() =
        withTempCwd (fun () ->
            let path = uniquePath "scan-unchanged"
            registerSession path "http://localhost:1/inject" (Some(uniqueSid "solo"))

            // Same doc in the previous baseline and current scan (same hash) -> not new-or-changed,
            // so the change-gated fallback leaves it alone.
            let doc = scannedDoc None "stable.html"
            attributeChangedDocs (sessionsForWorktree path) path [ doc ] [ doc ]

            Assert.That(runAsync (Server.CanvasDocOwnership.getOwner path "stable.html"), Is.EqualTo None,
                "Attribution is change-gated: an unchanged doc is left as-is"))
