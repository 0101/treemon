module Tests.SessionActivityExactInstanceTests

open System
open System.Collections.Concurrent
open System.IO
open NUnit.Framework
open Shared
open Server
open Server.SessionActivity
open Server.SessionActivityProtocol
open Server.SessionActivityService
open Server.SessionActivityStore
open Server.TerminalSessionActivity
open Tests.TestUtils

let private exactIdentity processId startTicks =
    ProcessIdentity.create processId startTicks
    |> Result.defaultWith invalidOp

let private report
    processId
    sessionId
    terminalSessionId
    eventId
    occurredAt
    event
    =
    { ParentProcessId = processId
      SessionId = SessionId sessionId
      TerminalSessionId = terminalSessionId
      WorktreePath = WorktreePath(PathUtils.normalizePath "C:/wt/exact")
      Provider = CopilotCli
      EventId = EventId eventId
      OccurredAt = occurredAt
      Event = event }

let private present
    (service: SessionActivityService)
    (processId: int)
    (sessionId: string)
    (terminalSessionId: TerminalSessionId option)
    (receivedAt: DateTimeOffset)
    =
    let presence =
        report
            processId
            sessionId
            terminalSessionId
            $"presence-{processId}-{receivedAt.UtcTicks}"
            receivedAt
            SessionPresent

    service.Present(presence, receivedAt)

let private storedInstance
    (identity: ProcessIdentity)
    (sessionId: string)
    (terminalSessionId: TerminalSessionId)
    (lastSeen: DateTimeOffset)
    =
    { ProcessIdentity = identity
      SessionId = SessionId sessionId
      TerminalSessionId = Some terminalSessionId
      WorktreePath = WorktreePath(PathUtils.normalizePath "C:/wt/exact")
      Provider = CopilotCli
      Status = emptyStatus
      UpdatedAt = lastSeen.AddSeconds(-1.0)
      LifecycleAt = Some(lastSeen.AddSeconds(-1.0))
      LastSeen = lastSeen
      ContextUsageAt = None
      ClosedAt = None }

let private withService
    resolver
    (action:
        SessionActivityService
            * SessionActivityStore
            * string
            -> unit)
    =
    let directory =
        Path.Combine(
            Path.GetTempPath(),
            $"treemon-exact-activity-{Guid.NewGuid():N}"
        )

    Directory.CreateDirectory directory |> ignore
    let dbPath = Path.Combine(directory, "activity.db")
    use store = new SessionActivityStore(dbPath)
    let scheduler = SchedulerState.createAgent ()

    use service =
        new SessionActivityService(
            store,
            scheduler,
            resolver
        )

    try
        action (service, store, dbPath)
    finally
        try
            Directory.Delete(directory, recursive = true)
        with _ ->
            ()

let private requirePresence =
    function
    | PresenceAcknowledge.Recorded identity -> identity
    | PresenceAcknowledge.NotRecorded(_, reason) ->
        Assert.Fail $"Expected acknowledged presence, got: {reason}"
        failwith "unreachable"

let private queryAt
    (service: SessionActivityService)
    now
    terminalSessionIds
    =
    match
        service.QueryTerminalActivityAt(
            now,
            terminalSessionIds
        )
    with
    | Ok value -> value
    | Error error ->
        Assert.Fail error
        failwith "unreachable"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PresenceAcknowledgementTests() =

    [<Test>]
    member _.``presence acknowledges only after the exact row is durable``() =
        let identity = exactIdentity 4101 5101L

        let resolver =
            ProcessIdentityResolver.create (fun processId ->
                if processId = 4101 then Ok(Some identity) else Ok None)

        withService resolver (fun (service, store, dbPath) ->
            let receivedAt = ts "2026-09-04T10:00:00Z"

            let acknowledged =
                present
                    service
                    4101
                    "shared-session"
                    None
                    receivedAt
                |> requirePresence

            let durable = store.InstanceByIdentity identity

            Assert.Multiple(fun () ->
                Assert.That(acknowledged, Is.EqualTo identity)
                Assert.That(durable.IsSome, Is.True)
                Assert.That(durable |> Option.map _.LastSeen, Is.EqualTo(Some receivedAt))
                Assert.That(
                    SqliteTestDatabase.scalarInt
                        dbPath
                        "SELECT count(*) FROM session_instances;",
                    Is.EqualTo 1
                )
                Assert.That(
                    SqliteTestDatabase.scalarInt
                        dbPath
                        "SELECT count(*) FROM activity_events;",
                    Is.Zero
                ))

            present
                service
                4101
                "shared-session"
                None
                (receivedAt.AddSeconds(1.0))
            |> requirePresence
            |> ignore

            Assert.That(
                SqliteTestDatabase.scalarInt
                    dbPath
                    "SELECT count(*) FROM session_instances;",
                Is.EqualTo 1,
                "presence is idempotent for one exact identity"
            ))

    [<Test>]
    member _.``presence returns explicit resolver and store failures``() =
        let missingResolver =
            ProcessIdentityResolver.create (fun _ -> Ok None)

        withService missingResolver (fun (service, store, _) ->
            match
                present
                    service
                    4201
                    "missing-process"
                    None
                    (ts "2026-09-04T10:00:00Z")
            with
            | PresenceAcknowledge.NotRecorded(false, _) ->
                Assert.That(
                    store.LoadRecentInstances(
                        ts "2026-09-04T10:00:00Z"
                    ),
                    Is.Empty
                )
            | outcome ->
                Assert.Fail $"Expected terminal missing-process acknowledgement, got {outcome}")

        let failingResolver =
            ProcessIdentityResolver.create (fun _ ->
                Error "simulated resolver failure")

        withService failingResolver (fun (service, _, _) ->
            match
                present
                    service
                    4202
                    "resolver-failure"
                    None
                    (ts "2026-09-04T10:00:00Z")
            with
            | PresenceAcknowledge.NotRecorded(true, _) -> ()
            | outcome ->
                Assert.Fail $"Expected retryable resolver acknowledgement, got {outcome}")

        let directory =
            Path.Combine(
                Path.GetTempPath(),
                $"treemon-presence-failure-{Guid.NewGuid():N}"
            )

        Directory.CreateDirectory directory |> ignore
        let dbPath = Path.Combine(directory, "activity.db")
        // Test fault injection is mutable because connection creation is the impure boundary under test.
        let mutable failConnections = false

        try
            use store =
                new SessionActivityStore(
                    dbPath,
                    connectionOpened =
                        (fun _ ->
                            if failConnections then
                                failwith "simulated store failure")
                )

            let identity = exactIdentity 4203 5203L
            let resolver =
                ProcessIdentityResolver.create (fun _ ->
                    Ok(Some identity))
            let scheduler = SchedulerState.createAgent ()

            use service =
                new SessionActivityService(
                    store,
                    scheduler,
                    resolver
                )

            failConnections <- true

            match
                present
                    service
                    4203
                    "store-failure"
                    None
                    (ts "2026-09-04T10:00:00Z")
            with
            | PresenceAcknowledge.NotRecorded(true, _) -> ()
            | outcome ->
                Assert.Fail $"Expected retryable store acknowledgement, got {outcome}"
        finally
            try
                Directory.Delete(directory, recursive = true)
            with _ ->
                ()

    [<Test>]
    member _.``presence returns an explicit negative acknowledgement after mailbox shutdown``() =
        let directory =
            Path.Combine(
                Path.GetTempPath(),
                $"treemon-presence-stopped-{Guid.NewGuid():N}"
            )

        Directory.CreateDirectory directory |> ignore

        try
            use store =
                new SessionActivityStore(
                    Path.Combine(directory, "activity.db")
                )

            let identity = exactIdentity 4204 5204L
            let resolver =
                ProcessIdentityResolver.create (fun _ ->
                    Ok(Some identity))
            let scheduler = SchedulerState.createAgent ()

            let service =
                new SessionActivityService(
                    store,
                    scheduler,
                    resolver
                )

            (service :> IDisposable).Dispose()

            match
                present
                    service
                    4204
                    "stopped-service"
                    None
                    (ts "2026-09-04T10:00:00Z")
            with
            | PresenceAcknowledge.NotRecorded(true, _) -> ()
            | outcome ->
                Assert.Fail $"Expected stopped-mailbox acknowledgement, got {outcome}"
        finally
            try
                Directory.Delete(directory, recursive = true)
            with _ ->
                ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ExactInstanceIsolationTests() =

    [<Test>]
    member _.``same durable session keeps exact status idempotency closure and PID reuse isolated``() =
        let first = exactIdentity 4301 5301L
        let second = exactIdentity 4302 5302L
        let reused = exactIdentity 4301 6301L
        // The running-process table is mutable because this test exercises PID reuse over time.
        let mutable running =
            Map.ofList
                [ 4301, first
                  4302, second ]

        let resolver =
            ProcessIdentityResolver.create (fun processId ->
                Ok(Map.tryFind processId running))

        withService resolver (fun (service, store, dbPath) ->
            let terminalA =
                TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            let terminalB =
                TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            let at = ts "2026-09-04T10:00:00Z"

            present service 4301 "same-session" (Some terminalA) at
            |> requirePresence
            |> ignore

            present service 4302 "same-session" (Some terminalB) at
            |> requirePresence
            |> ignore

            service.Submit(
                report
                    4301
                    "same-session"
                    (Some terminalA)
                    "same-event"
                    (at.AddSeconds(1.0))
                    TurnStarted
            )

            service.Submit(
                report
                    4302
                    "same-session"
                    (Some terminalB)
                    "same-event"
                    (at.AddSeconds(2.0))
                    WentIdle
            )

            let exact = service.ExactSnapshot()

            Assert.Multiple(fun () ->
                Assert.That(exact.Count, Is.EqualTo 2)
                Assert.That(
                    exact[first].Status.Status,
                    Is.EqualTo SessionLevelStatus.Working
                )
                Assert.That(
                    exact[second].Status.Status,
                    Is.EqualTo SessionLevelStatus.Idle
                )
                Assert.That(
                    SqliteTestDatabase.scalarInt
                        dbPath
                        "SELECT count(*) FROM activity_events
                         WHERE event_id = 'same-event';",
                    Is.EqualTo 2,
                    "event idempotency is scoped to exact identity"
                )
                Assert.That(
                    service.ExactSnapshot().Count,
                    Is.EqualTo 2,
                    "application-visible exact state must not collapse duplicate durable SessionIds"
                ))

            Assert.That(
                service.CloseProcess(first, at.AddSeconds(3.0)),
                Is.EqualTo ClosureAcknowledge.Closed
            )

            let closed = store.InstanceByIdentity first |> Option.get
            let secondBefore = store.InstanceByIdentity second |> Option.get

            service.Submit(
                report
                    4301
                    "same-session"
                    (Some terminalA)
                    "late-heartbeat"
                    (at.AddSeconds(4.0))
                    Heartbeat
            )

            service.ExactSnapshot() |> ignore
            let closedAfter = store.InstanceByIdentity first |> Option.get
            let secondAfter = store.InstanceByIdentity second |> Option.get

            Assert.Multiple(fun () ->
                Assert.That(closedAfter.ClosedAt, Is.EqualTo closed.ClosedAt)
                Assert.That(closedAfter.LastSeen, Is.EqualTo closed.LastSeen)
                Assert.That(secondAfter, Is.EqualTo secondBefore))

            match
                present
                    service
                    4301
                    "same-session"
                    (Some terminalA)
                    (at.AddSeconds(5.0))
            with
            | PresenceAcknowledge.NotRecorded(false, _) -> ()
            | outcome ->
                Assert.Fail $"A closed identity must not re-open, got {outcome}"

            running <- running |> Map.add 4301 reused

            service.Submit(
                report
                    4301
                    "same-session"
                    (Some terminalA)
                    "reused-heartbeat"
                    (at.AddSeconds(6.0))
                    Heartbeat
            )

            service.ExactSnapshot() |> ignore
            Assert.That(store.InstanceByIdentity reused, Is.EqualTo None)

            present
                service
                4301
                "same-session"
                (Some terminalA)
                (at.AddSeconds(7.0))
            |> requirePresence
            |> ignore

            Assert.Multiple(fun () ->
                Assert.That(
                    store.InstanceByIdentity first
                    |> Option.bind _.ClosedAt
                    |> Option.isSome,
                    Is.True
                )
                Assert.That(
                    store.InstanceByIdentity reused
                    |> Option.bind _.ClosedAt
                    |> Option.isNone,
                    Is.True
                )))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StartupReconciliationTests() =

    [<Test>]
    member _.``recent terminal-owned identity stays pending until the same identity re-presents``() =
        let now = ts "2026-09-04T10:00:00Z"
        let identity = exactIdentity 4401 5401L
        let terminal =
            TerminalSessionId "cccccccccccccccccccccccccccccccc"
        let resolver =
            ProcessIdentityResolver.create (fun _ ->
                Ok(Some identity))

        withService resolver (fun (service, store, _) ->
            store.UpsertInstance(
                storedInstance
                    identity
                    "pending-session"
                    terminal
                    (now.AddMinutes(-1.0))
            )
            |> ignore

            service.StartAt now

            let epoch, instances, pending =
                queryAt service now (Set.singleton terminal)

            let snapshot =
                ownedSessionSnapshot
                    now
                    (Set.singleton terminal)
                    (epoch, instances, pending)

            Assert.Multiple(fun () ->
                Assert.That(pending, Is.EqualTo(Set.singleton identity))
                Assert.That(
                    replacementSessionPlan
                        (fun _ -> Some CopilotCli)
                        [ { TerminalHostReplacement.ReplacementTerminal.TerminalSessionId =
                                TerminalSessionId.value terminal
                            WorktreePath = "C:/wt/exact" } ]
                        snapshot,
                    Is.EqualTo
                        TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle
                ))

            present
                service
                4401
                "pending-session"
                (Some terminal)
                (now.AddSeconds(1.0))
            |> requirePresence
            |> ignore

            let _, _, afterPresence =
                queryAt
                    service
                    (now.AddSeconds(1.0))
                    (Set.singleton terminal)

            Assert.That(afterPresence, Is.Empty))

    [<Test>]
    member _.``pending reconciliation clears on proven death missing origin and open-window expiry``() =
        let runCase
            processId
            terminal
            queryAtTime
            authoritativeOrigins
            resolvedIdentity
            expectClosed
            =
            let now = ts "2026-09-04T10:00:00Z"
            let identity = exactIdentity processId (int64 processId + 10_000L)

            let resolver =
                ProcessIdentityResolver.create (fun _ ->
                    Ok resolvedIdentity)

            withService resolver (fun (service, store, _) ->
                store.UpsertInstance(
                    storedInstance
                        identity
                        $"pending-{processId}"
                        terminal
                        (now.AddMinutes(-1.0))
                )
                |> ignore

                service.StartAt now
                let activity =
                    queryAt service queryAtTime authoritativeOrigins
                let _, _, pending = activity
                let snapshot =
                    ownedSessionSnapshot
                        queryAtTime
                        authoritativeOrigins
                        activity

                Assert.Multiple(fun () ->
                    Assert.That(pending, Is.Empty)
                    Assert.That(snapshot.OpenSessions, Is.Empty)
                    Assert.That(
                        store.InstanceByIdentity identity
                        |> Option.bind _.ClosedAt
                        |> Option.isSome,
                        Is.EqualTo expectClosed
                    )))

        let deadTerminal =
            TerminalSessionId "dddddddddddddddddddddddddddddddd"

        runCase
            4402
            deadTerminal
            (ts "2026-09-04T10:00:00Z")
            (Set.singleton deadTerminal)
            None
            true

        let missingTerminal =
            TerminalSessionId "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
        let missingIdentity = exactIdentity 4403 14_403L

        runCase
            4403
            missingTerminal
            (ts "2026-09-04T10:00:00Z")
            Set.empty
            (Some missingIdentity)
            false

        let expiredTerminal =
            TerminalSessionId "ffffffffffffffffffffffffffffffff"
        let expiredIdentity = exactIdentity 4404 14_404L

        runCase
            4404
            expiredTerminal
            (ts "2026-09-04T10:03:00Z")
            (Set.singleton expiredTerminal)
            (Some expiredIdentity)
            false

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SharedProcessResolverTests() =

    [<Test>]
    member _.``activity and TerminalHost exact checks use the same resolver contract``() =
        let activityIdentity = exactIdentity 4501 5501L
        let hostIdentity = exactIdentity 4502 5502L
        let calls = ConcurrentQueue<int>()

        let resolver =
            ProcessIdentityResolver.create (fun processId ->
                calls.Enqueue processId

                match processId with
                | 4501 -> Ok(Some activityIdentity)
                | 4502 -> Ok(Some hostIdentity)
                | _ -> Ok None)

        withService resolver (fun (service, _, _) ->
            present
                service
                4501
                "resolver-session"
                None
                (ts "2026-09-04T10:00:00Z")
            |> requirePresence
            |> ignore

            let config =
                TerminalHostClient.defaultConfigWithProcessIdentityResolver
                    resolver
                    []

            let manifest: TerminalHostManifest.DiscoveryManifest =
                { Pid = 4502
                  ProcessStartTimeUtcTicks = 5502L
                  Endpoint = "http://127.0.0.1:1/"
                  BearerToken = "test-token"
                  HostVersion = "test"
                  ControlApiVersion = 2
                  StagedExecutableVersion = None }

            Assert.Multiple(fun () ->
                match
                    TerminalHostManifest.processIdentityMatches
                        config
                        manifest
                with
                | Ok true -> ()
                | outcome ->
                    Assert.Fail $"Expected exact live host identity, got {outcome}"

                Assert.That(calls, Does.Contain 4501)
                Assert.That(calls, Does.Contain 4502)))

    [<Test>]
    member _.``exact liveness rejects a reused PID``() =
        let original = exactIdentity 4503 5503L
        let replacement = exactIdentity 4503 6503L
        let resolver =
            ProcessIdentityResolver.create (fun _ ->
                Ok(Some replacement))

        match ProcessIdentityResolver.isAlive resolver original with
        | Ok false -> ()
        | outcome ->
            Assert.Fail $"Expected reused PID to be rejected, got {outcome}"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ExactFoldAndWireTests() =

    [<Test>]
    member _.``presence and closure do not mutate the conversation fold``() =
        let status =
            { emptyStatus with
                Status = SessionLevelStatus.Working
                Skill = Some "review" }

        Assert.Multiple(fun () ->
            Assert.That(fold status SessionPresent, Is.EqualTo status)
            Assert.That(fold status SessionClosed, Is.EqualTo status))

    [<TestCase("session_present")>]
    [<TestCase("session_closed")>]
    member _.``wire parses exact instance state events``(kind: string) =
        let request: SessionActivityRequest =
            { parentProcessId = 4601
              sessionId = "wire-session"
              terminalSessionId = null
              worktreePath = "C:/wt/exact"
              provider = "copilot_cli"
              eventId = "wire-event"
              occurredAt = "2026-09-04T10:00:00Z"
              kind = kind
              message = Unchecked.defaultof<MessageDto>
              skillName = null
              toolCallId = null
              currentTokens = 0
              tokenLimit = 0 }

        let parsed =
            parseReport
                (ts "2026-09-04T10:01:00Z")
                request

        match kind, parsed with
        | "session_present", Ok report ->
            Assert.That(report.Event, Is.EqualTo SessionPresent)
        | "session_closed", Ok report ->
            Assert.That(report.Event, Is.EqualTo SessionClosed)
        | _, outcome -> Assert.Fail $"Unexpected parse outcome: {outcome}"

    [<Test>]
    member _.``wire rejects PID-less reporters``() =
        let request: SessionActivityRequest =
            { parentProcessId = 0
              sessionId = "wire-session"
              terminalSessionId = null
              worktreePath = "C:/wt/exact"
              provider = "copilot_cli"
              eventId = "wire-event"
              occurredAt = "2026-09-04T10:00:00Z"
              kind = "session_present"
              message = Unchecked.defaultof<MessageDto>
              skillName = null
              toolCallId = null
              currentTokens = 0
              tokenLimit = 0 }

        match
            parseReport
                (ts "2026-09-04T10:01:00Z")
                request
        with
        | Error "missing or invalid parentProcessId" -> ()
        | outcome ->
            Assert.Fail $"Expected PID-less rejection, got {outcome}"
