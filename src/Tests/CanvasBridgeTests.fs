module Tests.CanvasBridgeTests

open System
open NUnit.Framework
open Shared
open Server.CanvasBridge

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
            sessions |> List.choose (fun e -> e.SessionId) |> List.sort,
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

        let ids = sessionsForWorktree path |> List.choose (fun e -> e.SessionId) |> List.sort
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
        Assert.That(sessionsForWorktree pathB |> List.choose (fun e -> e.SessionId), Is.EqualTo [ b1 ])
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
