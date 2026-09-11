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

let private exactWorktree = WorktreePath(PathUtils.normalizePath "C:/wt/exact")

let private exactIdentity processId startTicks =
    ProcessIdentity.create processId startTicks |> Result.defaultWith invalidOp

let private terminal (hexSuffix: string) =
    TerminalSessionId.create (String('0', 32 - hexSuffix.Length) + hexSuffix)
    |> Result.defaultWith invalidOp

let private report processId sessionId terminalSessionId eventId occurredAt event =
    { ParentProcessId = processId
      SessionId = SessionId sessionId
      TerminalSessionId = terminalSessionId
      WorktreePath = exactWorktree
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
      WorktreePath = exactWorktree
      Provider = CopilotCli
      Status = emptyStatus
      UpdatedAt = lastSeen.AddSeconds(-1.0)
      LifecycleAt = Some(lastSeen.AddSeconds(-1.0))
      LastSeen = lastSeen
      ContextUsageAt = None
      ClosedAt = None }

/// Run `action` against a service over a throwaway temp .db, wired to `resolver` and `diagnostics`.
let private withServiceDiagnostics
    resolver
    diagnostics
    (action: SessionActivityService * SessionActivityStore * string -> unit)
    =
    let directory = uniquePath "exact-activity"
    Directory.CreateDirectory directory |> ignore
    let dbPath = Path.Combine(directory, "activity.db")
    use store = new SessionActivityStore(dbPath)
    let scheduler = SchedulerState.createAgent ()
    use service = new SessionActivityService(store, scheduler, resolver, diagnostics)

    try
        action (service, store, dbPath)
    finally
        try Directory.Delete(directory, recursive = true) with _ -> ()

let private withService resolver action =
    withServiceDiagnostics resolver LifecycleDiagnostics.ignore action

let private requirePresence =
    function
    | PresenceAcknowledge.Recorded identity -> identity
    | PresenceAcknowledge.NotRecorded(_, reason) ->
        Assert.Fail $"Expected acknowledged presence, got: {reason}"
        failwith "unreachable"

let private queryAt (service: SessionActivityService) now terminalSessionIds =
    match service.QueryTerminalActivityAt(now, terminalSessionIds) with
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
            let instanceCount () =
                SqliteTestDatabase.scalarInt dbPath "SELECT count(*) FROM session_instances;"

            let acknowledged =
                present service 4101 "shared-session" None receivedAt |> requirePresence

            let durable = store.InstanceByIdentity identity

            Assert.Multiple(fun () ->
                Assert.That(acknowledged, Is.EqualTo identity)
                Assert.That(durable |> Option.map _.LastSeen, Is.EqualTo(Some receivedAt))
                Assert.That(instanceCount (), Is.EqualTo 1)
                Assert.That(
                    SqliteTestDatabase.scalarInt dbPath "SELECT count(*) FROM activity_events;",
                    Is.Zero,
                    "presence is not an activity event"
                ))

            present service 4101 "shared-session" None (receivedAt.AddSeconds(1.0))
            |> requirePresence
            |> ignore

            Assert.That(
                instanceCount (),
                Is.EqualTo 1,
                "presence is idempotent for one exact identity"
            ))

    [<Test>]
    member _.``presence diagnostics distinguish reconnects normal sessions and duplicate processes``() =
        let first = exactIdentity 4151 5151L
        let second = exactIdentity 4152 5152L
        let third = exactIdentity 4153 5153L

        let identities =
            [ first; second; third ]
            |> List.map (fun identity -> ProcessIdentity.processId identity, identity)
            |> Map.ofList

        let resolver =
            ProcessIdentityResolver.create (fun processId -> Ok(identities |> Map.tryFind processId))

        let firstTerminal = terminal "51"
        let secondTerminal = terminal "52"
        let diagnostics = ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

        withServiceDiagnostics resolver diagnostics.Enqueue (fun (service, _, _) ->
            let at = ts "2026-09-04T10:00:00Z"

            [ 4151, "shared-session", firstTerminal, 0.0
              4152, "shared-session", secondTerminal, 1.0
              4153, "independent-session", firstTerminal, 2.0
              4151, "shared-session", firstTerminal, 3.0 ]
            |> List.iter (fun (processId, sessionId, terminalSessionId, offset) ->
                present service processId sessionId (Some terminalSessionId) (at.AddSeconds offset)
                |> requirePresence
                |> ignore))

        let events = diagnostics.ToArray()

        let presenceKinds =
            events
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.PresenceAcknowledged presence -> Some presence.Kind
                | _ -> None)

        let sameSession =
            events
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved multiplicity when
                    multiplicity.Boundary = LifecycleDiagnostics.ObservationBoundary.Presence
                    ->
                    Some multiplicity
                | _ -> None)
            |> Array.last

        let multipleSessions =
            events
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved multiple when
                    multiple.Boundary = LifecycleDiagnostics.ObservationBoundary.Presence
                    ->
                    Some multiple
                | _ -> None)
            |> Array.last

        Assert.Multiple(fun () ->
            Assert.That(
                presenceKinds,
                Is.EqualTo(
                    [| LifecycleDiagnostics.PresenceKind.FirstSeen
                       LifecycleDiagnostics.PresenceKind.FirstSeen
                       LifecycleDiagnostics.PresenceKind.FirstSeen
                       LifecycleDiagnostics.PresenceKind.Reconnected |]
                )
            )
            Assert.That(sameSession.ProcessIdentities, Is.EquivalentTo([ first; second ]))
            Assert.That(
                sameSession.TerminalSessionIds,
                Is.EquivalentTo([ firstTerminal; secondTerminal ])
            )
            Assert.That(
                multipleSessions.SessionIds,
                Is.EquivalentTo([ SessionId "shared-session"; SessionId "independent-session" ])
            ))

    [<Test>]
    member _.``an unresolvable process is a terminal negative acknowledgement``() =
        let missingResolver = ProcessIdentityResolver.create (fun _ -> Ok None)
        let at = ts "2026-09-04T10:00:00Z"

        withService missingResolver (fun (service, store, _) ->
            match present service 4201 "missing-process" None at with
            | PresenceAcknowledge.NotRecorded(false, _) ->
                Assert.That(store.LoadRecentInstances at, Is.Empty)
            | outcome -> Assert.Fail $"Expected terminal missing-process acknowledgement, got {outcome}")

    [<Test>]
    member _.``a failing resolver is a retryable negative acknowledgement``() =
        let failingResolver =
            ProcessIdentityResolver.create (fun _ -> Error "simulated resolver failure")

        withService failingResolver (fun (service, _, _) ->
            match present service 4202 "resolver-failure" None (ts "2026-09-04T10:00:00Z") with
            | PresenceAcknowledge.NotRecorded(true, _) -> ()
            | outcome -> Assert.Fail $"Expected retryable resolver acknowledgement, got {outcome}")

    [<Test>]
    member _.``a failing store is a retryable negative acknowledgement``() =
        let directory = uniquePath "presence-failure"
        Directory.CreateDirectory directory |> ignore
        // Test fault injection is mutable because connection creation is the impure boundary under test.
        let mutable failConnections = false

        try
            use store =
                new SessionActivityStore(
                    Path.Combine(directory, "activity.db"),
                    connectionOpened =
                        (fun _ -> if failConnections then failwith "simulated store failure")
                )

            let identity = exactIdentity 4203 5203L
            let resolver = ProcessIdentityResolver.create (fun _ -> Ok(Some identity))
            use service = new SessionActivityService(store, SchedulerState.createAgent (), resolver)
            failConnections <- true

            match present service 4203 "store-failure" None (ts "2026-09-04T10:00:00Z") with
            | PresenceAcknowledge.NotRecorded(true, _) -> ()
            | outcome -> Assert.Fail $"Expected retryable store acknowledgement, got {outcome}"
        finally
            try Directory.Delete(directory, recursive = true) with _ -> ()

    [<Test>]
    member _.``presence returns an explicit negative acknowledgement after mailbox shutdown``() =
        let directory = uniquePath "presence-stopped"
        Directory.CreateDirectory directory |> ignore

        try
            use store = new SessionActivityStore(Path.Combine(directory, "activity.db"))
            let identity = exactIdentity 4204 5204L
            let resolver = ProcessIdentityResolver.create (fun _ -> Ok(Some identity))
            let service = new SessionActivityService(store, SchedulerState.createAgent (), resolver)
            (service :> IDisposable).Dispose()

            match present service 4204 "stopped-service" None (ts "2026-09-04T10:00:00Z") with
            | PresenceAcknowledge.NotRecorded(true, _) -> ()
            | outcome -> Assert.Fail $"Expected stopped-mailbox acknowledgement, got {outcome}"
        finally
            try Directory.Delete(directory, recursive = true) with _ -> ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CloseProcessIsolationTests() =

    [<Test>]
    member _.``A new session under the same process supersedes the prior binding``() =
        let identity = exactIdentity 4300 5300L
        let resolver = ProcessIdentityResolver.create (fun _ -> Ok(Some identity))
        let owningTerminal = terminal "4300"
        let at = ts "2026-09-04T10:00:00Z"

        withService resolver (fun (service, store, dbPath) ->
            present service 4300 "first-session" (Some owningTerminal) at
            |> requirePresence
            |> ignore

            service.Submit(
                report
                    4300
                    "first-session"
                    (Some owningTerminal)
                    "first-prompt"
                    (at.AddMilliseconds(500.0))
                    (UserPrompt
                        { Text = "first prompt"
                          At = at.AddMilliseconds(500.0) })
            )

            service.ExactSnapshot() |> ignore

            present service 4300 "second-session" (Some owningTerminal) (at.AddSeconds(1.0))
            |> requirePresence
            |> ignore

            service.Submit(
                report
                    4300
                    "second-session"
                    (Some owningTerminal)
                    "second-prompt"
                    (at.AddSeconds(2.0))
                    (UserPrompt
                        { Text = "second prompt"
                          At = at.AddSeconds(2.0) })
            )

            service.Submit(
                report
                    4300
                    "first-session"
                    (Some owningTerminal)
                    "late-first-shutdown"
                    (at.AddSeconds(3.0))
                    SessionClosed
            )

            service.ExactSnapshot() |> ignore

            let first =
                store.InstancesBySession(SessionId "first-session")
                |> List.exactlyOne

            let second =
                store.InstancesBySession(SessionId "second-session")
                |> List.exactlyOne

            let retained = store.RetainedByWorktree()[WorktreePath.value exactWorktree]

            Assert.Multiple(fun () ->
                Assert.That(first.ClosedAt, Is.EqualTo(Some(at.AddSeconds(1.0))))
                Assert.That(second.ClosedAt, Is.EqualTo None)
                Assert.That(
                    second.Status.LastUserMessage |> Option.map _.Text,
                    Is.EqualTo(Some "second prompt")
                )
                Assert.That(retained.SessionId, Is.EqualTo(SessionId "second-session"))
                Assert.That(
                    store.InstanceByIdentity identity |> Option.map _.SessionId,
                    Is.EqualTo(Some(SessionId "second-session"))
                )
                Assert.That(
                    SqliteTestDatabase.scalarInt dbPath "SELECT count(*) FROM session_instances;",
                    Is.EqualTo 2,
                    "both durable session bindings must remain available"
                )))

    [<Test>]
    member _.``CloseProcess closes only the selected exact identity and rejects its reopening while a reused PID starts a fresh row``() =
        let first = exactIdentity 4301 5301L
        let second = exactIdentity 4302 5302L
        let reused = exactIdentity 4301 6301L
        // The running-process table is mutable because this test exercises PID reuse over time.
        let mutable running = Map.ofList [ 4301, first; 4302, second ]

        let resolver =
            ProcessIdentityResolver.create (fun processId -> Ok(Map.tryFind processId running))

        withService resolver (fun (service, store, _dbPath) ->
            let terminalA = TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            let terminalB = TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            let at = ts "2026-09-04T10:00:00Z"

            present service 4301 "same-session" (Some terminalA) at |> requirePresence |> ignore
            present service 4302 "same-session" (Some terminalB) at |> requirePresence |> ignore

            Assert.That(
                service.CloseProcess(first, at.AddSeconds(1.0)),
                Is.EqualTo ClosureAcknowledge.Closed
            )

            let closedProcesses =
                service.ClosedProcessSnapshot()
                |> Async.RunSynchronously
                |> Result.defaultWith invalidOp

            let closed = store.InstanceByIdentity first |> Option.get
            let secondBefore = store.InstanceByIdentity second |> Option.get

            service.Submit(
                report 4301 "same-session" (Some terminalA) "late-heartbeat" (at.AddSeconds(2.0)) Heartbeat
            )

            service.ExactSnapshot() |> ignore
            let closedAfter = store.InstanceByIdentity first |> Option.get
            let secondAfter = store.InstanceByIdentity second |> Option.get

            Assert.Multiple(fun () ->
                Assert.That(closedProcesses, Does.Contain first, "the closed identity must be reported closed")
                Assert.That(closedProcesses, Does.Not.Contain second, "the sibling identity must remain open")
                Assert.That(closedAfter.ClosedAt, Is.EqualTo closed.ClosedAt, "a late heartbeat must not revive ClosedAt")
                Assert.That(closedAfter.LastSeen, Is.EqualTo closed.LastSeen, "a late heartbeat must not revive LastSeen")
                Assert.That(secondAfter, Is.EqualTo secondBefore, "closing one identity must not touch its sibling"))

            match present service 4301 "same-session" (Some terminalA) (at.AddSeconds(3.0)) with
            | PresenceAcknowledge.NotRecorded(false, _) -> ()
            | outcome -> Assert.Fail $"A closed identity must not re-open, got {outcome}"

            running <- running |> Map.add 4301 reused

            present service 4301 "same-session" (Some terminalA) (at.AddSeconds(4.0))
            |> requirePresence
            |> ignore

            Assert.Multiple(fun () ->
                Assert.That(
                    store.InstanceByIdentity first |> Option.bind _.ClosedAt |> Option.isSome,
                    Is.True,
                    "the original closed identity must stay closed"
                )
                Assert.That(
                    store.InstanceByIdentity reused |> Option.bind _.ClosedAt |> Option.isNone,
                    Is.True,
                    "PID reuse with a new start-ticks identity must open a fresh row"
                )))

/// One startup-reconciliation case: a durable terminal-owned row that the server did not see
/// re-present, resolved against a live process (or not) at a query time inside or past the open
/// window.
type PendingReconciliationScenario =
    { Name: string
      ProcessId: int
      QueryAt: string
      AuthoritativeOrigin: bool
      ProcessStillAlive: bool
      ExpectClosed: bool }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StartupReconciliationTests() =

    static member PendingCases: TestCaseData seq =
        [ { Name = "a proven-dead process closes its pending identity"
            ProcessId = 4402
            QueryAt = "2026-09-04T10:00:00Z"
            AuthoritativeOrigin = true
            ProcessStillAlive = false
            ExpectClosed = true }
          { Name = "a missing terminal origin drops the pending identity without closing it"
            ProcessId = 4403
            QueryAt = "2026-09-04T10:00:00Z"
            AuthoritativeOrigin = false
            ProcessStillAlive = true
            ExpectClosed = false }
          { Name = "an expired open window drops the pending identity without closing it"
            ProcessId = 4404
            QueryAt = "2026-09-04T10:03:00Z"
            AuthoritativeOrigin = true
            ProcessStillAlive = true
            ExpectClosed = false } ]
        |> Seq.map (fun scenario -> TestCaseData(scenario).SetName(scenario.Name))

    [<Test>]
    member _.``recent terminal-owned identity stays pending until the same identity re-presents``() =
        let now = ts "2026-09-04T10:00:00Z"
        let identity = exactIdentity 4401 5401L
        let owningTerminal = terminal "cccccccccccccccccccccccccccccccc"
        let resolver = ProcessIdentityResolver.create (fun _ -> Ok(Some identity))

        withService resolver (fun (service, store, _) ->
            store.UpsertInstance(
                storedInstance identity "pending-session" owningTerminal (now.AddMinutes(-1.0))
            )
            |> ignore

            service.StartAt now
            let epoch, instances, pending = queryAt service now (Set.singleton owningTerminal)

            let snapshot =
                ownedSessionSnapshot now (Set.singleton owningTerminal) (epoch, instances, pending)

            Assert.Multiple(fun () ->
                Assert.That(pending, Is.EqualTo(Set.singleton identity))
                Assert.That(
                    replacementSessionPlan
                        (fun _ -> Some CopilotCli)
                        [ { TerminalHostReplacement.ReplacementTerminal.TerminalSessionId = owningTerminal
                            WorktreePath = "C:/wt/exact" } ]
                        snapshot,
                    Is.EqualTo TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle
                ))

            present service 4401 "pending-session" (Some owningTerminal) (now.AddSeconds(1.0))
            |> requirePresence
            |> ignore

            let _, _, afterPresence =
                queryAt service (now.AddSeconds(1.0)) (Set.singleton owningTerminal)

            Assert.That(afterPresence, Is.Empty))

    [<TestCaseSource("PendingCases")>]
    member _.``pending reconciliation clears without reopening a session``
        (scenario: PendingReconciliationScenario)
        =
        let now = ts "2026-09-04T10:00:00Z"
        let queryTime = ts scenario.QueryAt
        let identity = exactIdentity scenario.ProcessId (int64 scenario.ProcessId + 10_000L)
        let owningTerminal = terminal (string scenario.ProcessId)

        let resolver =
            ProcessIdentityResolver.create (fun _ ->
                Ok(if scenario.ProcessStillAlive then Some identity else None))

        let authoritativeOrigins =
            if scenario.AuthoritativeOrigin then Set.singleton owningTerminal else Set.empty

        withService resolver (fun (service, store, _) ->
            store.UpsertInstance(
                storedInstance
                    identity
                    $"pending-{scenario.ProcessId}"
                    owningTerminal
                    (now.AddMinutes(-1.0))
            )
            |> ignore

            service.StartAt now
            let activity = queryAt service queryTime authoritativeOrigins
            let _, _, pending = activity
            let snapshot = ownedSessionSnapshot queryTime authoritativeOrigins activity

            Assert.Multiple(fun () ->
                Assert.That(pending, Is.Empty)
                Assert.That(snapshot.OpenSessions, Is.Empty)
                Assert.That(
                    store.InstanceByIdentity identity |> Option.bind _.ClosedAt |> Option.isSome,
                    Is.EqualTo scenario.ExpectClosed
                )))

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
            present service 4501 "resolver-session" None (ts "2026-09-04T10:00:00Z")
            |> requirePresence
            |> ignore

            let config =
                TerminalHostClient.defaultConfigWithProcessIdentityResolver resolver []

            let manifest: TerminalHostManifest.DiscoveryManifest =
                { Pid = 4502
                  ProcessStartTimeUtcTicks = 5502L
                  Endpoint = "http://127.0.0.1:1/"
                  BearerToken = "test-token"
                  HostVersion = "test"
                  ControlApiVersion = 2
                  StagedExecutableVersion = None }

            Assert.Multiple(fun () ->
                match TerminalHostManifest.processIdentityMatches config manifest with
                | Ok true -> ()
                | outcome -> Assert.Fail $"Expected exact live host identity, got {outcome}"

                Assert.That(calls, Does.Contain 4501)
                Assert.That(calls, Does.Contain 4502)))

    [<Test>]
    member _.``exact liveness rejects a reused PID``() =
        let original = exactIdentity 4503 5503L
        let replacement = exactIdentity 4503 6503L
        let resolver = ProcessIdentityResolver.create (fun _ -> Ok(Some replacement))

        match ProcessIdentityResolver.isAlive resolver original with
        | Ok false -> ()
        | outcome -> Assert.Fail $"Expected reused PID to be rejected, got {outcome}"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ExactFoldAndWireTests() =

    let wireRequest parentProcessId kind : SessionActivityRequest =
        { parentProcessId = parentProcessId
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
        let parsed = parseReport (ts "2026-09-04T10:01:00Z") (wireRequest 4601 kind)

        match kind, parsed with
        | "session_present", Ok report -> Assert.That(report.Event, Is.EqualTo SessionPresent)
        | "session_closed", Ok report -> Assert.That(report.Event, Is.EqualTo SessionClosed)
        | _, outcome -> Assert.Fail $"Unexpected parse outcome: {outcome}"

    [<Test>]
    member _.``wire rejects PID-less reporters``() =
        match parseReport (ts "2026-09-04T10:01:00Z") (wireRequest 0 "session_present") with
        | Error ProtocolError.InvalidParentProcessId -> ()
        | outcome -> Assert.Fail $"Expected PID-less rejection, got {outcome}"
