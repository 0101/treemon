module Tests.SessionActivityStoreTests

open System
open System.IO
open NUnit.Framework
open Microsoft.Data.Sqlite
open Server
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.TestUtils

// These exercise the SQLite (WAL) durable mirror behind the push-model live state: exact snapshot
// replacement, process-scoped event dedupe, restart rebuild, durable resume lookup, and retention.
// Each test runs against a fresh temp .db file that is disposed + deleted in teardown.

/// Like withStore but hands the raw db path to the test so it can construct + dispose multiple store
/// instances over the SAME file — the shape of a server restart.
let private withDbPath (action: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"treemon-store-test-{Guid.NewGuid()}")
    Directory.CreateDirectory dir |> ignore

    try
        action (Path.Combine(dir, "activity.db"))
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private connStr (dbPath: string) =
    SqliteConnectionStringBuilder(DataSource = dbPath, Pooling = false).ConnectionString

let private withStoreAndPath (action: string -> SessionActivityStore -> unit) =
    withDbPath (fun dbPath ->
        use store = new SessionActivityStore(dbPath)
        action dbPath store)

/// A fresh store over a throwaway temp .db, disposed (releasing the file handle) and its dir deleted
/// afterwards. Store construction creates the schema, so the DB is ready to use inside `action`.
let private withStore action =
    withStoreAndPath (fun _ store -> action store)

let private seedInstance (store: SessionActivityStore) (stored: StoredInstance) =
    store.UpsertInstance stored |> ignore

let private eventCount dbPath =
    Tests.SqliteTestDatabase.scalarInt dbPath "SELECT count(*) FROM activity_events;"

let private eventCountById dbPath eventId =
    use conn = Tests.SqliteTestDatabase.openConnection dbPath
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT count(*) FROM activity_events WHERE event_id = $eventId;"
    cmd.Parameters.AddWithValue("$eventId", eventId) |> ignore
    Convert.ToInt32(cmd.ExecuteScalar())

let private insertEvent dbPath (row: ActivityEventRow) =
    let status =
        match row.Status with
        | SessionLevelStatus.Working -> "working"
        | SessionLevelStatus.WaitingForUser -> "waiting_for_user"
        | SessionLevelStatus.Idle -> "idle"

    use conn = new SqliteConnection(connStr dbPath)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """
INSERT INTO activity_events
    (process_id, process_start_ticks, event_id, session_id, worktree_path,
     provider, kind, status, skill, ts)
VALUES ($processId, $processStartTicks, $eventId, $sessionId, $worktreePath,
        'copilot_cli', $kind, $status, $skill, $ts);
"""
    cmd.Parameters.AddWithValue("$processId", ProcessIdentity.processId row.ProcessIdentity) |> ignore
    cmd.Parameters.AddWithValue(
        "$processStartTicks",
        ProcessIdentity.processStartTimeUtcTicks row.ProcessIdentity
    )
    |> ignore
    cmd.Parameters.AddWithValue("$eventId", EventId.value row.EventId) |> ignore
    cmd.Parameters.AddWithValue("$sessionId", SessionId.value row.SessionId) |> ignore
    cmd.Parameters.AddWithValue("$worktreePath", WorktreePath.value row.WorktreePath) |> ignore
    cmd.Parameters.AddWithValue("$kind", row.Kind) |> ignore
    cmd.Parameters.AddWithValue("$status", status) |> ignore
    cmd.Parameters.AddWithValue("$skill", row.Skill |> Option.map box |> Option.defaultValue DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$ts", row.Ts.ToUniversalTime().ToString("O")) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private contextWorktree = Path.Combine(Path.GetTempPath(), "treemon-context-worktree")
let private otherWorktree = Path.Combine(Path.GetTempPath(), "treemon-other-worktree")

let private storedOf sid wt (status: SessionStatus) updatedAt lastSeen : StoredInstance =
    let sessionId = SessionId sid
    let updated = ts updatedAt

    { ProcessIdentity =
        sessionId
        |> SessionId.value
        |> collisionResistantProcessIdentityForSessionId
      SessionId = sessionId
      TerminalSessionId = None
      WorktreePath = WorktreePath wt
      Provider = CopilotCli
      Status = status
      UpdatedAt = updated
      LifecycleAt = Some updated
      LastSeen = ts lastSeen
      ContextUsageAt = None
      ClosedAt = None }

let private withTerminalOrigin terminalSessionId (stored: StoredInstance) =
    { stored with TerminalSessionId = Some terminalSessionId }

let private withUsage
    (usage: ContextUsage)
    usageAt
    lastSeen
    (stored: StoredInstance)
    : StoredInstance =
    { stored with
        Status.ContextUsage = Some usage
        ContextUsageAt = Some usageAt
        LastSeen = lastSeen }

let private eventOf eid sid kind status skill t : ActivityEventRow =
    let sessionId = SessionId sid

    { ProcessIdentity =
        sessionId
        |> SessionId.value
        |> collisionResistantProcessIdentityForSessionId
      EventId = EventId eid
      SessionId = sessionId
      WorktreePath = WorktreePath "C:/wt/a"
      Provider = CopilotCli
      Kind = kind
      Status = status
      Skill = skill
      Ts = ts t }

let private find sid (rows: StoredInstance list) =
    rows |> List.find (fun r -> r.SessionId = SessionId sid)

let private retainedBySession
    (store: SessionActivityStore)
    sessionId
    =
    store.RetainedByWorktree()
    |> Map.values
    |> Seq.tryFind (fun retained ->
        retained.SessionId = sessionId)

let private exactFromRetained (stored: RetainedSession) =
    { ProcessIdentity =
        stored.SessionId
        |> SessionId.value
        |> collisionResistantProcessIdentityForSessionId
      SessionId = stored.SessionId
      TerminalSessionId = None
      WorktreePath = stored.WorktreePath
      Provider = stored.Provider
      Status = stored.Status
      UpdatedAt = stored.UpdatedAt
      LifecycleAt = Some stored.UpdatedAt
      LastSeen = stored.UpdatedAt
      ContextUsageAt = stored.ContextUsageAt
      ClosedAt = None }

type SessionActivityStore with
    member store.RecordLiveness
        (
            sessionId: SessionId,
            lastSeen: DateTimeOffset,
            terminalSessionId: TerminalSessionId option
        ) =
        store.RecordHeartbeat(
            sessionId
            |> SessionId.value
            |> collisionResistantProcessIdentityForSessionId,
            lastSeen,
            terminalSessionId
        )
        |> ignore


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type UpsertInstanceTests() =

    [<Test>]
    member _.``An exact instance is replaced by the supplied snapshot regardless of lifecycle clock``() =
        withStore (fun store ->
            let initial =
                storedOf
                    "s1"
                    "C:/wt/a"
                    { emptyStatus with
                        Status = SessionLevelStatus.WaitingForUser
                        Skill = Some "investigate" }
                    "2026-03-01T10:05:00Z"
                    "2026-03-01T12:00:00Z"

            let replacement =
                storedOf
                    "s1"
                    "C:/wt/a"
                    { emptyStatus with Status = SessionLevelStatus.Idle }
                    "2026-03-01T10:02:00Z"
                    "2026-03-01T12:30:00Z"

            seedInstance store initial
            let persisted = store.UpsertInstance replacement
            let loaded = store.LoadRecentInstances replacement.LastSeen |> find "s1"

            Assert.Multiple(fun () ->
                Assert.That(persisted, Is.EqualTo replacement)
                Assert.That(loaded, Is.EqualTo replacement)))

    [<Test>]
    member _.``Replaying an exact snapshot leaves one identical row``() =
        withStore (fun store ->
            let stored =
                storedOf
                    "s1"
                    "C:/wt/a"
                    { emptyStatus with
                        Status = SessionLevelStatus.Working
                        Skill = Some "review" }
                    "2026-03-01T10:00:00Z"
                    "2026-03-01T12:00:00Z"

            seedInstance store stored
            seedInstance store stored

            let rows = store.LoadRecentInstances(ts "2026-03-01T12:00:00Z")
            Assert.That(rows.Length, Is.EqualTo(1))
            Assert.That(find "s1" rows, Is.EqualTo stored))

    [<Test>]
    member _.``Session content and user-input clocks round-trip through the store``() =
        withStore (fun store ->
            let rich =
                { Status = SessionLevelStatus.Idle
                  Skill = Some "review"
                  Intent = Some(msg "reviewing the auth changes" "2026-03-01T10:00:50Z")
                  Title = Some(msg "Review the auth changes" "2026-03-01T10:00:55Z")
                  LastUserMessage = Some(msg "the auth module" "2026-03-01T10:01:00Z")
                  LastAssistantMessage = Some(msg "which file?" "2026-03-01T10:00:30Z")
                  ContextUsage = None
                  AwaitingUserSince = Some(ts "2026-03-01T10:00:30Z")
                  UserInputCompletedAt = Some(ts "2026-03-01T10:00:00Z")
                  BackgroundAgentClocks = Map.empty }

            seedInstance store (storedOf "s1" "C:/wt/a" rich "2026-03-01T10:01:00Z" "2026-03-01T12:00:00Z")

            let row = store.LoadRecentInstances(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status, Is.EqualTo(rich))
            Assert.That(effectiveStatus row.Status, Is.EqualTo SessionLevelStatus.WaitingForUser)
            Assert.That(row.WorktreePath, Is.EqualTo(WorktreePath "C:/wt/a"))
            Assert.That(row.Provider, Is.EqualTo(CopilotCli)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ContextUsagePersistenceTests() =

    [<Test>]
    member _.``A usage-only snapshot changes context fields without advancing liveness``() =
        withStore (fun store ->
            let usage = { CurrentTokens = 150000; TokenLimit = 200000 }
            let usageAt = ts "2026-03-01T10:00:10Z"
            let terminalSessionId =
                TerminalSessionId "0123456789abcdef0123456789abcdef"

            let initial =
                storedOf
                    "s1"
                    contextWorktree
                    { emptyStatus with
                        Status = SessionLevelStatus.Working
                        Skill = Some "investigate" }
                    "2026-03-01T10:00:00Z"
                    "2026-03-01T10:00:05Z"
                |> withTerminalOrigin terminalSessionId

            let usageSnapshot =
                initial
                |> withUsage usage usageAt initial.LastSeen

            seedInstance store initial
            let persisted = store.UpsertInstance usageSnapshot
            let loaded = store.LoadRecentInstances usageAt |> find "s1"

            Assert.Multiple(fun () ->
                Assert.That(persisted, Is.EqualTo usageSnapshot)
                Assert.That(loaded, Is.EqualTo usageSnapshot)
                Assert.That(loaded.LastSeen, Is.EqualTo initial.LastSeen)))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AppendAndUpsertTests() =

    [<Test>]
    member _.``A new event is appended and the live status upserted in one call``() =
        withStoreAndPath (fun dbPath store ->
            let status = { emptyStatus with Status = SessionLevelStatus.Working; Skill = Some "review" }
            let e = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working (Some "review") "2026-03-01T10:00:00Z"
            let stored = storedOf "s1" "C:/wt/a" status "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z"

            Assert.That(store.AppendAndUpsert(e, stored), Is.EqualTo(Some stored), "a new event returns the persisted row")

            Assert.That(eventCount dbPath, Is.EqualTo 1, "the event was appended")
            let row = store.LoadRecentInstances(ts "2026-03-01T10:00:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo SessionLevelStatus.Working, "the status was upserted in the same call"))

    [<Test>]
    member _.``A duplicate event_id skips BOTH the append and the upsert (coupled idempotency)``() =
        withStoreAndPath (fun dbPath store ->
            let first = { emptyStatus with Status = SessionLevelStatus.Working }
            let e = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T10:00:00Z"
            Assert.That(
                store.AppendAndUpsert(e, storedOf "s1" "C:/wt/a" first "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z")
                |> Option.isSome,
                Is.True
            )

            // Same event_id but a would-be-newer status: the dedupe must skip the upsert together with
            // the append, so the status can never advance off a deduped event.
            let laterStatus = { emptyStatus with Status = SessionLevelStatus.WaitingForUser }
            Assert.That(
                store.AppendAndUpsert(e, storedOf "s1" "C:/wt/a" laterStatus "2026-03-01T10:05:00Z" "2026-03-01T10:05:00Z")
                |> Option.isNone,
                Is.True,
                "a duplicate event_id reports ignored"
            )

            Assert.That(eventCount dbPath, Is.EqualTo 1, "no second event row")
            let row = store.LoadRecentInstances(ts "2026-03-01T10:05:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo SessionLevelStatus.Working, "the upsert was skipped with the append")
            Assert.That(row.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z")))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LoadRecentInstancesTests() =

    [<Test>]
    member _.``Only sessions whose last_seen is within the idle window are loaded``() =
        withStore (fun store ->
            let now = ts "2026-03-01T12:00:00Z"
            // idleWindow is 2h → cutoff 10:00. live: last_seen 11:00; stale: last_seen 09:00.
            seedInstance store (storedOf "live" "C:/wt/a" emptyStatus "2026-03-01T11:00:00Z" "2026-03-01T11:00:00Z")
            seedInstance store (storedOf "stale" "C:/wt/a" emptyStatus "2026-03-01T09:00:00Z" "2026-03-01T09:00:00Z")

            let rows = store.LoadRecentInstances now
            Assert.That(rows |> List.map (_.SessionId >> SessionId.value), Is.EquivalentTo([ "live" ])))

    [<Test>]
    member _.``Live state survives a restart (new store instance over the same file)``() =
        withDbPath (fun dbPath ->
            let terminalSessionId =
                TerminalSessionId "0123456789abcdef0123456789abcdef"
            let working =
                { emptyStatus with
                    Status = SessionLevelStatus.Working
                    Skill = Some "bd-execute" }

            // First instance writes, then is disposed (checkpoints WAL, releases the file).
            (use store = new SessionActivityStore(dbPath)
             storedOf
                 "s1"
                 "C:/wt/a"
                 working
                 "2026-03-01T11:30:00Z"
                 "2026-03-01T11:30:00Z"
             |> withTerminalOrigin terminalSessionId
             |> seedInstance store)

            // A fresh instance over the same path rebuilds the live status with no new events.
            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadRecentInstances(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo(SessionLevelStatus.Working))
            Assert.That(row.Status.Skill, Is.EqualTo(Some "bd-execute"))
            Assert.That(row.TerminalSessionId, Is.EqualTo(Some terminalSessionId)))

    [<Test>]
    member _.``Terminal attribution survives heartbeat, snapshot replacement, and restart``() =
        withDbPath (fun dbPath ->
            let terminalSessionId =
                TerminalSessionId "fedcba9876543210fedcba9876543210"
            let usage = { CurrentTokens = 120000; TokenLimit = 200000 }
            let heartbeatAt = ts "2026-03-01T11:31:00Z"
            let usageAt = ts "2026-03-01T11:32:00Z"

            (use store = new SessionActivityStore(dbPath)
             let attributed =
                 storedOf
                     "s1"
                     "C:/wt/a"
                     { emptyStatus with Status = SessionLevelStatus.Working }
                     "2026-03-01T11:30:00Z"
                     "2026-03-01T11:30:00Z"
                 |> withTerminalOrigin terminalSessionId

             seedInstance store attributed
             store.RecordLiveness(SessionId "s1", heartbeatAt, None)

             let afterHeartbeat =
                 store.InstanceByIdentity attributed.ProcessIdentity
                 |> Option.get

             let afterUsage =
                 afterHeartbeat
                 |> withUsage usage usageAt afterHeartbeat.LastSeen
                 |> store.UpsertInstance

             let afterEvent =
                 { afterUsage with
                     Status.Status = SessionLevelStatus.Idle
                     UpdatedAt = ts "2026-03-01T11:33:00Z"
                     LastSeen = ts "2026-03-01T11:33:00Z" }

             let persisted =
                 store.AppendAndUpsert(
                     eventOf
                         "ended"
                         "s1"
                         "turn_ended"
                         SessionLevelStatus.Idle
                         None
                         "2026-03-01T11:33:00Z",
                     afterEvent
                 )
                 |> Option.get

             Assert.Multiple(fun () ->
                 Assert.That(afterUsage.TerminalSessionId, Is.EqualTo(Some terminalSessionId))
                 Assert.That(afterUsage.LastSeen, Is.EqualTo heartbeatAt)
                 Assert.That(persisted.TerminalSessionId, Is.EqualTo(Some terminalSessionId))))

            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadRecentInstances(ts "2026-03-01T12:00:00Z") |> find "s1"

            Assert.Multiple(fun () ->
                Assert.That(row.TerminalSessionId, Is.EqualTo(Some terminalSessionId))
                Assert.That(row.Status.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(row.Status.ContextUsage, Is.EqualTo(Some usage))))

    [<Test>]
    member _.``Context usage survives a restart with its ordering timestamp``() =
        withDbPath (fun dbPath ->
            let usage = { CurrentTokens = 120000; TokenLimit = 200000 }
            let lastSeen = ts "2026-03-01T11:30:00Z"
            let usageAt = ts "2026-03-01T11:30:05Z"
            let initial =
                storedOf
                    "s1"
                    contextWorktree
                    { emptyStatus with Status = SessionLevelStatus.Working }
                    "2026-03-01T11:30:00Z"
                    "2026-03-01T11:30:00Z"

            (use store = new SessionActivityStore(dbPath)
             seedInstance store initial
             initial
             |> withUsage usage usageAt lastSeen
             |> store.UpsertInstance
             |> ignore)

            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadRecentInstances(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(row.ContextUsageAt, Is.EqualTo(Some usageAt))
            Assert.That(row.LastSeen, Is.EqualTo(lastSeen)))

    [<Test>]
    member _.``An empty store loads no sessions``() =
        withStore (fun store ->
            Assert.That(store.LoadRecentInstances(ts "2026-03-01T12:00:00Z"), Is.Empty))

    [<Test>]
    member _.``Restart restores background clocks over the persisted base status``() =
        withDbPath (fun dbPath ->
            let withActiveAgent =
                fold
                    emptyStatus
                    (BackgroundAgentStarted("tool-active", ts "2026-03-01T11:31:00Z"))

            (use store = new SessionActivityStore(dbPath)
             seedInstance store (
                 storedOf
                     "active"
                     contextWorktree
                     withActiveAgent
                     "2026-03-01T11:31:00Z"
                     "2026-03-01T11:31:00Z"
             ))

            use reopened = new SessionActivityStore(dbPath)
            let restored = reopened.LoadRecentInstances(ts "2026-03-01T12:00:00Z") |> find "active"
            Assert.Multiple(fun () ->
                Assert.That(
                    restored.Status.BackgroundAgentClocks,
                    Is.EqualTo withActiveAgent.BackgroundAgentClocks
                )
                Assert.That(restored.Status.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(effectiveStatus restored.Status, Is.EqualTo SessionLevelStatus.Working)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LatestSessionIdForWorktreeTests() =

    [<Test>]
    member _.``Session outside the idle window is returned by latest activity``() =
        withDbPath (fun dbPath ->
            let now = ts "2026-03-01T12:00:00Z"
            (use store = new SessionActivityStore(dbPath)
             seedInstance store (storedOf "heartbeat" contextWorktree emptyStatus "2026-03-01T07:00:00Z" "2026-03-01T09:30:00Z")
             seedInstance store (storedOf "activity" contextWorktree emptyStatus "2026-03-01T09:00:00Z" "2026-03-01T09:00:00Z")
             Assert.That(store.LoadRecentInstances now, Is.Empty, "both sessions are outside the idle window"))

            use reopened = new SessionActivityStore(dbPath)
            Assert.That(
                reopened.LatestSessionIdForWorktree(WorktreePath contextWorktree),
                Is.EqualTo(Some(SessionId "activity"))
            ))

    [<Test>]
    member _.``Latest session query is scoped by worktree and uses session id as tie breaker``() =
        withStore (fun store ->
            seedInstance store (storedOf "a1" contextWorktree emptyStatus "2026-03-01T11:00:00Z" "2026-03-01T11:00:00Z")
            seedInstance store (storedOf "a2" contextWorktree emptyStatus "2026-03-01T11:00:00Z" "2026-03-01T10:00:00Z")
            seedInstance store (storedOf "b1" otherWorktree emptyStatus "2026-03-01T11:30:00Z" "2026-03-01T11:30:00Z")

            Assert.That(
                store.LatestSessionIdForWorktree(WorktreePath contextWorktree),
                Is.EqualTo(Some(SessionId "a2"))
            ))

    [<Test>]
    member _.``A worktree that never reported yields no session id``() =
        withStore (fun store ->
            let unknownWorktree = Path.Combine(Path.GetTempPath(), "treemon-unknown-worktree")
            Assert.That(store.LatestSessionIdForWorktree(WorktreePath unknownWorktree), Is.EqualTo None))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PersistedIdentityValidationTests() =

    [<Test>]
    member _.``Invalid persisted session identity fails instead of entering typed state``() =
        withStoreAndPath (fun dbPath store ->
            seedInstance store (
                storedOf
                    "valid-session"
                    contextWorktree
                    emptyStatus
                    "2026-03-01T11:00:00Z"
                    "2026-03-01T11:00:00Z"
            )

            Tests.SqliteTestDatabase.execute
                dbPath
                "UPDATE session_instances SET session_id = 'invalid session';"

            let failure =
                Assert.Throws<InvalidOperationException>(fun () ->
                    store.LoadRecentInstances(ts "2026-03-01T12:00:00Z")
                    |> ignore)

            Assert.That(
                failure.Message,
                Does.Contain("invalid persisted session id")
            ))

    [<Test>]
    member _.``Invalid persisted terminal identity fails instead of entering typed state``() =
        withStoreAndPath (fun dbPath store ->
            storedOf
                "valid-session"
                contextWorktree
                emptyStatus
                "2026-03-01T11:00:00Z"
                "2026-03-01T11:00:00Z"
            |> withTerminalOrigin
                (TerminalSessionId
                    "0123456789abcdef0123456789abcdef")
            |> seedInstance store

            Tests.SqliteTestDatabase.execute
                dbPath
                "UPDATE session_instances SET terminal_session_id = 'invalid';"

            let failure =
                Assert.Throws<InvalidOperationException>(fun () ->
                    store.LoadRecentInstances(ts "2026-03-01T12:00:00Z")
                    |> ignore)

            Assert.That(
                failure.Message,
                Does.Contain("invalid persisted terminal session id")
            ))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RetainedByWorktreeTests() =

    [<Test>]
    member _.``Returns the most recently active session per worktree``() =
        withStore (fun store ->
            seedInstance store (storedOf "a-heartbeat" "C:/wt/a" emptyStatus "2026-03-01T07:00:00Z" "2026-03-01T09:30:00Z")
            seedInstance store (storedOf "a-activity" "C:/wt/a" emptyStatus "2026-03-01T09:00:00Z" "2026-03-01T09:00:00Z")
            seedInstance store (storedOf "b1" "C:/wt/b" emptyStatus "2026-03-01T08:00:00Z" "2026-03-01T08:00:00Z")

            let retained = store.RetainedByWorktree()
            Assert.That(retained.Count, Is.EqualTo 2, "one row per worktree")
            Assert.That(retained["C:/wt/a"].SessionId, Is.EqualTo(SessionId "a-activity"))
            Assert.That(retained["C:/wt/b"].SessionId, Is.EqualTo(SessionId "b1")))

    [<Test>]
    member _.``Winning representative refreshes payload without changing its ordering key``() =
        withStore (fun store ->
            let original =
                storedOf
                    "same-session"
                    "C:/wt/a"
                    { emptyStatus with
                        Title =
                            Some(
                                msg
                                    "Original title"
                                    "2026-03-01T08:59:00Z"
                            ) }
                    "2026-03-01T09:00:00Z"
                    "2026-03-01T09:00:00Z"

            seedInstance store original
            seedInstance store
                { original with
                    Status.Title =
                        Some(
                            msg
                                "Bootstrapped title"
                                "2026-03-01T08:58:00Z"
                        ) }

            let retained = store.RetainedByWorktree()

            Assert.That(
                retained["C:/wt/a"].Status.Title
                |> Option.map _.Text,
                Is.EqualTo(Some "Bootstrapped title")
            ))

    [<Test>]
    member _.``Repeated retained reads use only bounded worktree representatives``() =
        withDbPath (fun dbPath ->
            (use _schema = new SessionActivityStore(dbPath)
             ())

            SqliteTestDatabase.execute
                dbPath
                """
WITH digits(value) AS (
    VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)
),
numbers(value) AS (
    SELECT
        ones.value
        + 10 * tens.value
        + 100 * hundreds.value
        + 1000 * thousands.value
    FROM digits AS ones
    CROSS JOIN digits AS tens
    CROSS JOIN digits AS hundreds
    CROSS JOIN digits AS thousands
)
INSERT INTO session_instances
    (process_id, process_start_ticks, session_id, worktree_path,
     provider, status, updated_at, last_seen)
SELECT
    value + 1,
    (value + 1) * 1000,
    printf('session-%04d', value + 1),
    printf('C:/wt/%02d', value % 25),
    'copilot_cli',
    'idle',
    '2026-03-01T10:00:00.0000000+00:00',
    '2026-03-01T10:00:00.0000000+00:00'
FROM numbers
WHERE value < 5000;
"""

            use store = new SessionActivityStore(dbPath)
            let expected = store.RetainedByWorktree()

            Assert.Multiple(fun () ->
                Assert.That(
                    SqliteTestDatabase.scalarInt
                        dbPath
                        "SELECT count(*) FROM session_instances;",
                    Is.EqualTo 5000
                )
                Assert.That(
                    SqliteTestDatabase.scalarInt
                        dbPath
                        "SELECT count(*) FROM worktree_representatives;",
                    Is.EqualTo 25
                )
                Assert.That(expected.Count, Is.EqualTo 25))

            SqliteTestDatabase.execute
                dbPath
                "DROP TABLE session_instances; DROP TABLE retained_sessions;"

            [ 1 .. 10 ]
            |> List.iter (fun _ ->
                Assert.That(
                    store.RetainedByWorktree(),
                    Is.EqualTo expected
                )))

    [<Test>]
    member _.``An empty store yields no retained rows``() =
        withStore (fun store ->
            Assert.That(store.RetainedByWorktree() |> Map.isEmpty, Is.True))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PruneOldTests() =

    [<Test>]
    member _.``pruneOld drops events and session rows older than the cutoff and returns the count``() =
        withStoreAndPath (fun dbPath store ->
            insertEvent dbPath (eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T01:00:00Z")
            insertEvent dbPath (eventOf "e2" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T02:00:00Z")
            insertEvent dbPath (eventOf "e3" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T03:00:00Z")

            seedInstance store (storedOf "old" "C:/wt/a" emptyStatus "2026-03-01T01:00:00Z" "2026-03-01T01:00:00Z")
            seedInstance store (storedOf "recent" "C:/wt/a" emptyStatus "2026-03-01T03:00:00Z" "2026-03-01T03:00:00Z")

            // cutoff 02:30 → e1(01:00), e2(02:00), and old(01:00) go;
            // e3(03:00) and recent(03:00) stay.
            let deleted = store.PruneOld(ts "2026-03-01T02:30:00Z")
            Assert.That(deleted, Is.EqualTo(3))

            Assert.That(eventCount dbPath, Is.EqualTo 1)
            Assert.That(eventCountById dbPath "e3", Is.EqualTo 1)

            let remainingSessions = store.LoadRecentInstances(ts "2026-03-01T03:30:00Z")

            Assert.That(
                remainingSessions |> List.map (_.SessionId >> SessionId.value),
                Is.EqualTo([ "recent" ])
            ))

    [<Test>]
    member _.``pruneOld on an empty store deletes nothing``() =
        withStore (fun store -> Assert.That(store.PruneOld(ts "2026-03-01T12:00:00Z"), Is.EqualTo(0)))

    [<Test>]
    member _.``pruneOld rebuilds a removed winner from surviving liveness history``() =
        withStore (fun store ->
            seedInstance store (
                storedOf
                    "fallback"
                    "C:/wt/a"
                    emptyStatus
                    "2026-03-01T02:00:00Z"
                    "2026-03-01T05:00:00Z"
            )
            seedInstance store (
                storedOf
                    "stale-winner"
                    "C:/wt/a"
                    emptyStatus
                    "2026-03-01T03:00:00Z"
                    "2026-03-01T03:00:00Z"
            )

            let beforePrune = store.RetainedByWorktree()

            Assert.That(
                beforePrune["C:/wt/a"].SessionId,
                Is.EqualTo(SessionId "stale-winner")
            )

            Assert.That(
                store.PruneOld(ts "2026-03-01T04:00:00Z"),
                Is.EqualTo 1
            )
            let afterPrune = store.RetainedByWorktree()
            Assert.That(
                afterPrune["C:/wt/a"].SessionId,
                Is.EqualTo(SessionId "fallback")
            ))

    [<Test>]
    member _.``pruneOld rolls back every delete when a later statement fails``() =
        withDbPath (fun dbPath ->
            use store = new SessionActivityStore(dbPath)
            let cutoff = ts "2026-03-02T00:00:00Z"
            let oldEvent = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T01:00:00Z"

            insertEvent dbPath oldEvent
            seedInstance store (storedOf "s1" "C:/wt/a" emptyStatus "2026-03-01T01:00:00Z" "2026-03-01T01:00:00Z")

            let connectionString =
                SqliteConnectionStringBuilder(DataSource = dbPath, Pooling = false).ConnectionString

            use conn = new SqliteConnection(connectionString)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """
CREATE TRIGGER fail_status_prune
BEFORE DELETE ON session_instances
BEGIN
    SELECT RAISE(ABORT, 'forced prune failure');
END;
"""
            cmd.ExecuteNonQuery() |> ignore

            Assert.Throws<SqliteException>(fun () -> store.PruneOld cutoff |> ignore) |> ignore
            Assert.That(eventCountById dbPath "e1", Is.EqualTo 1)
            Assert.That(
                retainedBySession store (SessionId "s1")
                |> Option.isSome,
                Is.True
            ))

    [<Test>]
    member _.``pruneOld keeps the latest old event for a retained session``() =
        withStoreAndPath (fun dbPath store ->
            let oldEvent = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2025-12-01T10:00:00Z"
            insertEvent dbPath oldEvent
            seedInstance store (
                storedOf
                    "s1"
                    "C:/wt/a"
                    { emptyStatus with Status = SessionLevelStatus.Working }
                    "2025-12-01T10:00:00Z"
                    "2025-12-01T10:00:00Z"
            )
            store.RecordLiveness(
                SessionId "s1",
                ts "2026-03-01T11:59:00Z",
                None
            )

            store.PruneOld(ts "2026-01-01T00:00:00Z") |> ignore

            Assert.That(eventCountById dbPath "e1", Is.EqualTo 1))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LegacyDoneStatusTests() =

    // Pre-idle-only builds persisted the retired "done" status; live DBs still hold such rows. The
    // idempotent construction-time migration rewrites 'done' rows to 'idle' so startup hydration
    // never hits an unknown status.

    /// Insert a raw session_status row with an arbitrary status text, bypassing the store's typed
    /// writers (which can only emit the live vocabulary) — the shape of a row a pre-idle-only build
    /// persisted with status='done'.
    let insertRawStatus (dbPath: string) (sessionId: string) (worktree: string) (status: string) (tsStr: string) =
        use conn = new SqliteConnection(connStr dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "CREATE TABLE IF NOT EXISTS session_status (
                 session_id TEXT PRIMARY KEY,
                 worktree_path TEXT NOT NULL,
                 provider TEXT NOT NULL,
                 status TEXT NOT NULL,
                 current_skill TEXT,
                 last_user_msg TEXT,
                 last_user_ts TEXT,
                 last_asst_msg TEXT,
                 last_asst_ts TEXT,
                 updated_at TEXT NOT NULL,
                 last_seen TEXT NOT NULL
             );
             INSERT INTO session_status (session_id, worktree_path, provider, status, updated_at, last_seen)
             VALUES ($sid, $wt, 'copilot_cli', $status, $ts, $ts);"

        cmd.Parameters.AddWithValue("$sid", sessionId) |> ignore
        cmd.Parameters.AddWithValue("$wt", worktree) |> ignore
        cmd.Parameters.AddWithValue("$status", status) |> ignore
        cmd.Parameters.AddWithValue("$ts", (ts tsStr).ToUniversalTime().ToString("O")) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    [<Test>]
    member _.``Legacy done history migrates to retained idle history``() =
        withDbPath (fun dbPath ->
            insertRawStatus dbPath "legacy" "C:/wt/a" "done" "2026-03-01T11:30:00Z"

            use reopened = new SessionActivityStore(dbPath)
            let retained =
                retainedBySession reopened (SessionId "legacy")
                |> Option.get

            Assert.Multiple(fun () ->
                Assert.That(retained.Status.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(reopened.LoadRecentInstances(ts "2026-03-01T12:00:00Z"), Is.Empty)))

    [<Test>]
    member _.``Construction retires legacy done rows after preserving idle history``() =
        withDbPath (fun dbPath ->
            insertRawStatus dbPath "legacy" "C:/wt/a" "done" "2026-03-01T11:00:00Z"

            use reopened = new SessionActivityStore(dbPath)
            let retained =
                retainedBySession reopened (SessionId "legacy")
                |> Option.get

            Assert.Multiple(fun () ->
                Assert.That(retained.Status.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(
                    SqliteTestDatabase.scalarInt
                        dbPath
                        "SELECT count(*) FROM sqlite_master
                         WHERE type = 'table' AND name = 'session_status';",
                    Is.Zero
                )))

    [<Test>]
    member _.``Construction preserves legacy waiting state as retained user-input clocks``() =
        withDbPath (fun dbPath ->
            insertRawStatus dbPath "legacy" "C:/wt/a" "waiting_for_user" "2026-03-01T11:00:00Z"

            use reopened = new SessionActivityStore(dbPath)
            let row =
                retainedBySession reopened (SessionId "legacy")
                |> Option.get

            Assert.Multiple(fun () ->
                Assert.That(row.Status.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(row.Status.AwaitingUserSince, Is.EqualTo(Some(ts "2026-03-01T11:00:00Z")))
                Assert.That(effectiveStatus row.Status, Is.EqualTo SessionLevelStatus.WaitingForUser)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AdditiveColumnMigrationTests() =

    let seedLegacyDatabase (dbPath: string) =
        use conn = new SqliteConnection(connStr dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
CREATE TABLE session_status (
    session_id    TEXT PRIMARY KEY,
    worktree_path TEXT NOT NULL,
    provider      TEXT NOT NULL,
    status        TEXT NOT NULL,
    current_skill TEXT,
    last_user_msg TEXT,
    last_user_ts  TEXT,
    last_asst_msg TEXT,
    last_asst_ts  TEXT,
    updated_at    TEXT NOT NULL,
    last_seen     TEXT NOT NULL
);
INSERT INTO session_status
    (session_id, worktree_path, provider, status, updated_at, last_seen)
VALUES
    ('legacy', $wt, 'copilot_cli', 'working', $ts, $ts);
"""

        cmd.Parameters.AddWithValue("$wt", contextWorktree) |> ignore
        cmd.Parameters.AddWithValue("$ts", (ts "2026-03-01T11:30:00Z").ToUniversalTime().ToString("O")) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    let indexColumns dbPath indexName =
        use conn = new SqliteConnection(connStr dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"PRAGMA index_info('{indexName}');"
        use reader = cmd.ExecuteReader()

        let rec read columns =
            if reader.Read() then
                read (reader.GetString 2 :: columns)
            else
                columns |> List.rev

        read []

    [<Test>]
    member _.``Construction adds metadata columns idempotently and preserves legacy rows``() =
        withDbPath (fun dbPath ->
            seedLegacyDatabase dbPath
            let terminalSessionId =
                TerminalSessionId "fedcba9876543210fedcba9876543210"

            (use store = new SessionActivityStore(dbPath)
             let legacy =
                 retainedBySession store (SessionId "legacy")
                 |> Option.get
             Assert.That(legacy.Status.Intent, Is.EqualTo(None))
             Assert.That(legacy.Status.Title, Is.EqualTo(None))
             Assert.That(legacy.Status.BackgroundAgentClocks, Is.Empty)

             let intent = msg "investigating the fold" "2026-03-01T11:45:00Z"
             let title = msg "Investigate the fold" "2026-03-01T11:46:00Z"

             { exactFromRetained legacy with
                 TerminalSessionId = Some terminalSessionId
                 Status.Intent = Some intent
                 Status.Title = Some title
                 UpdatedAt = ts "2026-03-01T11:46:00Z"
                 LastSeen = ts "2026-03-01T11:50:00Z" }
             |> seedInstance store)

            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadRecentInstances(ts "2026-03-01T12:00:00Z") |> find "legacy"
            Assert.Multiple(fun () ->
                Assert.That(row.Status.Intent, Is.EqualTo(Some(msg "investigating the fold" "2026-03-01T11:45:00Z")))
                Assert.That(row.Status.Title, Is.EqualTo(Some(msg "Investigate the fold" "2026-03-01T11:46:00Z")))
                Assert.That(row.TerminalSessionId, Is.EqualTo(Some terminalSessionId))
                Assert.That(
                    indexColumns dbPath "ix_instances_terminal_activity",
                    Is.EqualTo(
                        [ "terminal_session_id"
                          "updated_at"
                          "session_id" ]
                    ),
                    "the terminal index must be created after the additive column migration"
                )))

    [<Test>]
    member _.``Construction adds context columns idempotently and preserves legacy rows``() =
        withDbPath (fun dbPath ->
            seedLegacyDatabase dbPath
            let usage = { CurrentTokens = 50000; TokenLimit = 200000 }
            let usageAt = ts "2026-03-01T11:45:00Z"

            (use store = new SessionActivityStore(dbPath)
             let legacy =
                 retainedBySession store (SessionId "legacy")
                 |> Option.get
             Assert.That(legacy.Status.ContextUsage, Is.EqualTo(None))
             Assert.That(legacy.ContextUsageAt, Is.EqualTo(None))

             let exact = exactFromRetained legacy

             let persisted =
                 exact
                 |> withUsage usage usageAt exact.LastSeen
                 |> store.UpsertInstance

             Assert.That(persisted.Status.ContextUsage, Is.EqualTo(Some usage))
             Assert.That(persisted.ContextUsageAt, Is.EqualTo(Some usageAt))
             Assert.That(persisted.LastSeen, Is.EqualTo exact.LastSeen))

            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadRecentInstances(ts "2026-03-01T12:00:00Z") |> find "legacy"
            Assert.That(row.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(row.ContextUsageAt, Is.EqualTo(Some usageAt))
            Assert.That(row.LastSeen, Is.EqualTo(ts "2026-03-01T11:30:00Z")))
