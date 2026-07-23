module Tests.SessionActivityStoreTests

open System
open System.IO
open NUnit.Framework
open Microsoft.Data.Sqlite
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.TestUtils

// These exercise the SQLite (WAL) durable mirror behind the push-model live state: last-write-wins
// upserts, INSERT OR IGNORE event dedupe, restart rebuild, durable resume lookup, and retention.
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

let private scalarInt dbPath sql =
    use conn = new SqliteConnection(connStr dbPath)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    Convert.ToInt32(cmd.ExecuteScalar())

let private eventCount dbPath =
    scalarInt dbPath "SELECT count(*) FROM activity_events;"

let private eventCountById dbPath eventId =
    use conn = new SqliteConnection(connStr dbPath)
    conn.Open()
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
    (event_id, session_id, worktree_path, provider, kind, status, skill, ts)
VALUES ($eventId, $sessionId, $worktreePath, 'copilot_cli', $kind, $status, $skill, $ts);
"""
    cmd.Parameters.AddWithValue("$eventId", EventId.value row.EventId) |> ignore
    cmd.Parameters.AddWithValue("$sessionId", SessionId.value row.SessionId) |> ignore
    cmd.Parameters.AddWithValue("$worktreePath", WorktreePath.value row.WorktreePath) |> ignore
    cmd.Parameters.AddWithValue("$kind", row.Kind) |> ignore
    cmd.Parameters.AddWithValue("$status", status) |> ignore
    cmd.Parameters.AddWithValue("$skill", row.Skill |> Option.map box |> Option.defaultValue DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$ts", row.Ts.ToUniversalTime().ToString("O")) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private contextWorktree = Path.Combine(Path.GetTempPath(), "treemon-context-worktree")

let private storedOf sid wt (status: SessionStatus) updatedAt lastSeen : StoredStatus =
    { SessionId = SessionId sid
      WorktreePath = WorktreePath wt
      Provider = CopilotCli
      Status = status
      UpdatedAt = ts updatedAt
      LastSeen = ts lastSeen
      ContextUsageAt = None }

let private withUsage (usage: ContextUsage) usageAt lastSeen (stored: StoredStatus) : StoredStatus =
    { stored with
        Status.ContextUsage = Some usage
        ContextUsageAt = Some usageAt
        LastSeen = lastSeen }

let private eventOf eid sid kind status skill t : ActivityEventRow =
    { EventId = EventId eid
      SessionId = SessionId sid
      WorktreePath = WorktreePath "C:/wt/a"
      Provider = CopilotCli
      Kind = kind
      Status = status
      Skill = skill
      Ts = ts t }

let private find sid (rows: StoredStatus list) =
    rows |> List.find (fun r -> r.SessionId = SessionId sid)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type UpsertStatusTests() =

    [<Test>]
    member _.``A newer report overwrites the session row (last-write-wins)``() =
        withStore (fun store ->
            let older =
                { emptyStatus with
                    Status = SessionLevelStatus.Working
                    Skill = Some "review" }

            let newer =
                { emptyStatus with
                    Status = SessionLevelStatus.WaitingForUser
                    Skill = Some "investigate" }

            store.UpsertStatus(storedOf "s1" "C:/wt/a" older "2026-03-01T10:00:00Z" "2026-03-01T12:00:00Z")
            store.UpsertStatus(storedOf "s1" "C:/wt/a" newer "2026-03-01T10:05:00Z" "2026-03-01T12:00:00Z")

            let row = store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo(SessionLevelStatus.WaitingForUser))
            Assert.That(row.Status.Skill, Is.EqualTo(Some "investigate"))
            Assert.That(row.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:05:00Z")))

    [<Test>]
    member _.``A stale (older) report is ignored, leaving the newer row intact``() =
        withStore (fun store ->
            let newer =
                { emptyStatus with
                    Status = SessionLevelStatus.WaitingForUser
                    Skill = Some "investigate" }

            let stale =
                { emptyStatus with
                    Status = SessionLevelStatus.Idle
                    Skill = None }

            store.UpsertStatus(storedOf "s1" "C:/wt/a" newer "2026-03-01T10:05:00Z" "2026-03-01T12:00:00Z")
            // Older updated_at AND a would-be-newer last_seen: the whole upsert must be a no-op.
            store.UpsertStatus(storedOf "s1" "C:/wt/a" stale "2026-03-01T10:02:00Z" "2026-03-01T12:30:00Z")

            let row = store.LoadLiveStatuses(ts "2026-03-01T12:30:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo(SessionLevelStatus.WaitingForUser))
            Assert.That(row.Status.Skill, Is.EqualTo(Some "investigate"))
            Assert.That(row.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:05:00Z"))
            Assert.That(row.LastSeen, Is.EqualTo(ts "2026-03-01T12:00:00Z"), "stale upsert must not bump last_seen"))

    [<Test>]
    member _.``An equal-timestamp replay lands identically (>= is idempotent)``() =
        withStore (fun store ->
            let a =
                { emptyStatus with
                    Status = SessionLevelStatus.Working
                    Skill = Some "review" }

            store.UpsertStatus(storedOf "s1" "C:/wt/a" a "2026-03-01T10:00:00Z" "2026-03-01T12:00:00Z")
            store.UpsertStatus(storedOf "s1" "C:/wt/a" a "2026-03-01T10:00:00Z" "2026-03-01T12:00:00Z")

            let rows = store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z")
            Assert.That(rows.Length, Is.EqualTo(1))
            Assert.That((find "s1" rows).Status.Status, Is.EqualTo(SessionLevelStatus.Working)))

    [<Test>]
    member _.``Skill, intent, title, and both messages round-trip through the store``() =
        withStore (fun store ->
            let rich =
                { Status = SessionLevelStatus.WaitingForUser
                  Skill = Some "review"
                  Intent = Some(msg "reviewing the auth changes" "2026-03-01T10:00:50Z")
                  Title = Some(msg "Review the auth changes" "2026-03-01T10:00:55Z")
                  LastUserMessage = Some(msg "the auth module" "2026-03-01T10:01:00Z")
                  LastAssistantMessage = Some(msg "which file?" "2026-03-01T10:00:30Z")
                  ContextUsage = None }

            store.UpsertStatus(storedOf "s1" "C:/wt/a" rich "2026-03-01T10:01:00Z" "2026-03-01T12:00:00Z")

            let row = store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status, Is.EqualTo(rich))
            Assert.That(row.WorktreePath, Is.EqualTo(WorktreePath "C:/wt/a"))
            Assert.That(row.Provider, Is.EqualTo(CopilotCli)))

    [<Test>]
    member _.``A status upsert preserves persisted context usage``() =
        withStore (fun store ->
            let usage = { CurrentTokens = 120000; TokenLimit = 200000 }
            let working = { emptyStatus with Status = SessionLevelStatus.Working }
            let idle = { emptyStatus with Status = SessionLevelStatus.Idle }

            store.UpsertStatus(storedOf "s1" contextWorktree working "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z")

            storedOf "s1" contextWorktree working "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z"
            |> withUsage usage (ts "2026-03-01T10:00:05Z") (ts "2026-03-01T10:00:05Z")
            |> store.UpsertContextUsage
            |> ignore

            store.UpsertStatus(storedOf "s1" contextWorktree idle "2026-03-01T10:00:10Z" "2026-03-01T10:00:10Z")

            let row = store.LoadLiveStatuses(ts "2026-03-01T10:10:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo(SessionLevelStatus.Idle))
            Assert.That(row.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(row.ContextUsageAt, Is.EqualTo(Some(ts "2026-03-01T10:00:05Z"))))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ContextUsagePersistenceTests() =

    [<Test>]
    member _.``An older usage update cannot replace a newer persisted snapshot``() =
        withStore (fun store ->
            let newer = { CurrentTokens = 150000; TokenLimit = 200000 }
            let older = { CurrentTokens = 80000; TokenLimit = 200000 }

            let stored = storedOf "s1" contextWorktree emptyStatus "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z"
            store.UpsertStatus stored

            stored
            |> withUsage newer (ts "2026-03-01T10:00:10Z") (ts "2026-03-01T10:00:10Z")
            |> store.UpsertContextUsage
            |> ignore

            let persisted =
                stored
                |> withUsage older (ts "2026-03-01T10:00:05Z") (ts "2026-03-01T10:00:05Z")
                |> store.UpsertContextUsage

            let row = store.LoadLiveStatuses(ts "2026-03-01T10:10:00Z") |> find "s1"
            Assert.That(persisted.Status.ContextUsage, Is.EqualTo(Some newer))
            Assert.That(row.Status.ContextUsage, Is.EqualTo(Some newer))
            Assert.That(row.ContextUsageAt, Is.EqualTo(Some(ts "2026-03-01T10:00:10Z")))
            Assert.That(row.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:10Z")))

    [<Test>]
    member _.``A context update recreates a session row removed by retention``() =
        withStore (fun store ->
            let usage = { CurrentTokens = 90000; TokenLimit = 200000 }
            let stored = storedOf "s1" contextWorktree emptyStatus "2026-03-01T08:00:00Z" "2026-03-01T08:00:00Z"
            store.UpsertStatus stored
            Assert.That(store.PruneOld(ts "2026-03-01T09:00:00Z"), Is.EqualTo(1))

            let recreated =
                stored
                |> withUsage usage (ts "2026-03-01T10:00:00Z") (ts "2026-03-01T10:00:00Z")
                |> store.UpsertContextUsage

            Assert.That(recreated.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(recreated.ContextUsageAt, Is.EqualTo(Some(ts "2026-03-01T10:00:00Z")))

            let row = store.LoadLiveStatuses(ts "2026-03-01T10:00:00Z") |> find "s1"
            Assert.That(row, Is.EqualTo(recreated)))


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
            let row = store.LoadLiveStatuses(ts "2026-03-01T10:00:00Z") |> find "s1"
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
            let row = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo SessionLevelStatus.Working, "the upsert was skipped with the append")
            Assert.That(row.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z")))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LoadLiveStatusesTests() =

    [<Test>]
    member _.``Only sessions whose last_seen is within the idle window are loaded``() =
        withStore (fun store ->
            let now = ts "2026-03-01T12:00:00Z"
            // idleWindow is 2h → cutoff 10:00. live: last_seen 11:00; stale: last_seen 09:00.
            store.UpsertStatus(storedOf "live" "C:/wt/a" emptyStatus "2026-03-01T11:00:00Z" "2026-03-01T11:00:00Z")
            store.UpsertStatus(storedOf "stale" "C:/wt/a" emptyStatus "2026-03-01T09:00:00Z" "2026-03-01T09:00:00Z")

            let rows = store.LoadLiveStatuses now
            Assert.That(rows |> List.map (_.SessionId >> SessionId.value), Is.EquivalentTo([ "live" ])))

    [<Test>]
    member _.``Live state survives a restart (new store instance over the same file)``() =
        withDbPath (fun dbPath ->
            let working =
                { emptyStatus with
                    Status = SessionLevelStatus.Working
                    Skill = Some "bd-execute" }

            // First instance writes, then is disposed (checkpoints WAL, releases the file).
            (use store = new SessionActivityStore(dbPath)
             store.UpsertStatus(storedOf "s1" "C:/wt/a" working "2026-03-01T11:30:00Z" "2026-03-01T11:30:00Z"))

            // A fresh instance over the same path rebuilds the live status with no new events.
            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo(SessionLevelStatus.Working))
            Assert.That(row.Status.Skill, Is.EqualTo(Some "bd-execute")))

    [<Test>]
    member _.``Context usage survives a restart with its ordering timestamp``() =
        withDbPath (fun dbPath ->
            let usage = { CurrentTokens = 120000; TokenLimit = 200000 }
            let usageAt = ts "2026-03-01T11:30:05Z"

            (use store = new SessionActivityStore(dbPath)
             storedOf
                 "s1"
                 contextWorktree
                 { emptyStatus with Status = SessionLevelStatus.Working }
                 "2026-03-01T11:30:00Z"
                 "2026-03-01T11:30:00Z"
             |> withUsage usage usageAt usageAt
             |> store.UpsertContextUsage
             |> ignore)

            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(row.ContextUsageAt, Is.EqualTo(Some usageAt))
            Assert.That(row.LastSeen, Is.EqualTo(usageAt)))

    [<Test>]
    member _.``An empty store loads no sessions``() =
        withStore (fun store ->
            Assert.That(store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z"), Is.Empty))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StatusesForWorktreeTests() =

    [<Test>]
    member _.``Sessions outside the idle window are still returned (the resume substrate, unlike LoadLiveStatuses)``() =
        withStore (fun store ->
            let now = ts "2026-03-01T12:00:00Z"
            // idleWindow is 2h → cutoff 10:00. Both sessions are >2h stale (last active 07:00 / 09:00),
            // so LoadLiveStatuses drops both — the exact post-restart gap F10/C-02 is about.
            store.UpsertStatus(storedOf "old" "C:/wt/a" emptyStatus "2026-03-01T07:00:00Z" "2026-03-01T07:00:00Z")
            store.UpsertStatus(storedOf "recent" "C:/wt/a" emptyStatus "2026-03-01T09:00:00Z" "2026-03-01T09:00:00Z")

            Assert.That(store.LoadLiveStatuses now, Is.Empty, "both sessions are outside the idle window")

            let ids = store.StatusesForWorktree(WorktreePath "C:/wt/a") |> List.map (_.SessionId >> SessionId.value)
            Assert.That(ids, Is.EqualTo([ "recent"; "old" ]), "durable rows returned newest last_seen first, no idle filter"))

    [<Test>]
    member _.``Only the requested worktree's sessions are returned``() =
        withStore (fun store ->
            store.UpsertStatus(storedOf "a1" "C:/wt/a" emptyStatus "2026-03-01T11:00:00Z" "2026-03-01T11:00:00Z")
            store.UpsertStatus(storedOf "b1" "C:/wt/b" emptyStatus "2026-03-01T11:30:00Z" "2026-03-01T11:30:00Z")

            let ids = store.StatusesForWorktree(WorktreePath "C:/wt/a") |> List.map (_.SessionId >> SessionId.value)
            Assert.That(ids, Is.EqualTo([ "a1" ])))

    [<Test>]
    member _.``A worktree that never reported yields an empty list``() =
        withStore (fun store ->
            Assert.That(store.StatusesForWorktree(WorktreePath "C:/wt/never"), Is.Empty))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RetainedByWorktreeTests() =

    [<Test>]
    member _.``Returns the newest session per worktree, ignoring the idle window``() =
        withStore (fun store ->
            // wt/a has two sessions well outside any idle window (last active 07:00 / 09:00); wt/b one.
            store.UpsertStatus(storedOf "a-old" "C:/wt/a" emptyStatus "2026-03-01T07:00:00Z" "2026-03-01T07:00:00Z")
            store.UpsertStatus(storedOf "a-new" "C:/wt/a" emptyStatus "2026-03-01T09:00:00Z" "2026-03-01T09:00:00Z")
            store.UpsertStatus(storedOf "b1" "C:/wt/b" emptyStatus "2026-03-01T08:00:00Z" "2026-03-01T08:00:00Z")

            let retained = store.RetainedByWorktree()
            Assert.That(retained.Count, Is.EqualTo 2, "one row per worktree")
            Assert.That(retained["C:/wt/a"].SessionId, Is.EqualTo(SessionId "a-new"), "the most-recent session for the worktree")
            Assert.That(retained["C:/wt/b"].SessionId, Is.EqualTo(SessionId "b1")))

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

            store.UpsertStatus(storedOf "old" "C:/wt/a" emptyStatus "2026-03-01T01:00:00Z" "2026-03-01T01:00:00Z")
            store.UpsertStatus(storedOf "recent" "C:/wt/a" emptyStatus "2026-03-01T03:00:00Z" "2026-03-01T03:00:00Z")

            // cutoff 02:30 → e1(01:00), e2(02:00), old(01:00) go; e3(03:00), recent(03:00) stay.
            let deleted = store.PruneOld(ts "2026-03-01T02:30:00Z")
            Assert.That(deleted, Is.EqualTo(3))

            Assert.That(eventCount dbPath, Is.EqualTo 1)
            Assert.That(eventCountById dbPath "e3", Is.EqualTo 1)

            let remainingSessions =
                store.LoadLiveStatuses(ts "2026-03-01T03:30:00Z")
                |> List.map (_.SessionId >> SessionId.value)

            Assert.That(remainingSessions, Is.EqualTo([ "recent" ])))

    [<Test>]
    member _.``pruneOld on an empty store deletes nothing``() =
        withStore (fun store -> Assert.That(store.PruneOld(ts "2026-03-01T12:00:00Z"), Is.EqualTo(0)))

    [<Test>]
    member _.``pruneOld rolls back every delete when a later statement fails``() =
        withDbPath (fun dbPath ->
            use store = new SessionActivityStore(dbPath)
            let cutoff = ts "2026-03-02T00:00:00Z"
            let oldEvent = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T01:00:00Z"

            insertEvent dbPath oldEvent
            store.UpsertStatus(storedOf "s1" "C:/wt/a" emptyStatus "2026-03-01T01:00:00Z" "2026-03-01T01:00:00Z")

            let connectionString =
                SqliteConnectionStringBuilder(DataSource = dbPath, Pooling = false).ConnectionString

            use conn = new SqliteConnection(connectionString)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """
CREATE TRIGGER fail_status_prune
BEFORE DELETE ON session_status
BEGIN
    SELECT RAISE(ABORT, 'forced prune failure');
END;
"""
            cmd.ExecuteNonQuery() |> ignore

            Assert.Throws<SqliteException>(fun () -> store.PruneOld cutoff |> ignore) |> ignore
            Assert.That(eventCountById dbPath "e1", Is.EqualTo 1)
            Assert.That(store.StatusBySession(SessionId "s1").IsSome, Is.True))

    [<Test>]
    member _.``pruneOld keeps the latest old event for a retained session``() =
        withStoreAndPath (fun dbPath store ->
            let oldEvent = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2025-12-01T10:00:00Z"
            insertEvent dbPath oldEvent
            store.UpsertStatus(
                storedOf
                    "s1"
                    "C:/wt/a"
                    { emptyStatus with Status = SessionLevelStatus.Working }
                    "2025-12-01T10:00:00Z"
                    "2025-12-01T10:00:00Z"
            )
            store.RecordLiveness(SessionId "s1", ts "2026-03-01T11:59:00Z")

            store.PruneOld(ts "2026-01-01T00:00:00Z") |> ignore

            Assert.That(eventCountById dbPath "e1", Is.EqualTo 1))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LegacyDoneStatusTests() =

    // Pre-idle-only builds persisted the retired "done" status; live DBs still hold such rows. The
    // idempotent construction-time migration rewrites 'done' rows to 'idle' so the unguarded reads
    // (LoadLiveStatuses at startup, StatusesForWorktree on resume) never hit an unknown status.

    /// Insert a raw session_status row with an arbitrary status text, bypassing the store's typed
    /// writers (which can only emit the live vocabulary) — the shape of a row a pre-idle-only build
    /// persisted with status='done'.
    let insertRawStatus (dbPath: string) (sessionId: string) (worktree: string) (status: string) (tsStr: string) =
        use conn = new SqliteConnection(connStr dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "INSERT INTO session_status (session_id, worktree_path, provider, status, updated_at, last_seen)
             VALUES ($sid, $wt, 'copilot_cli', $status, $ts, $ts);"

        cmd.Parameters.AddWithValue("$sid", sessionId) |> ignore
        cmd.Parameters.AddWithValue("$wt", worktree) |> ignore
        cmd.Parameters.AddWithValue("$status", status) |> ignore
        cmd.Parameters.AddWithValue("$ts", (ts tsStr).ToUniversalTime().ToString("O")) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    let readRawStatus (dbPath: string) (sessionId: string) : string =
        use conn = new SqliteConnection(connStr dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT status FROM session_status WHERE session_id = $sid;"
        cmd.Parameters.AddWithValue("$sid", sessionId) |> ignore
        cmd.ExecuteScalar() :?> string

    [<Test>]
    member _.``LoadLiveStatuses does not crash on a legacy 'done' row (startup rehydrate)``() =
        withDbPath (fun dbPath ->
            (use _ = new SessionActivityStore(dbPath)
             insertRawStatus dbPath "legacy" "C:/wt/a" "done" "2026-03-01T11:30:00Z")

            // Fresh instance = a server restart: LoadLiveStatuses is the unguarded startup read.
            use reopened = new SessionActivityStore(dbPath)
            let rows = reopened.LoadLiveStatuses(ts "2026-03-01T12:00:00Z")
            Assert.That(rows |> List.map _.Status.Status, Is.EqualTo([ SessionLevelStatus.Idle ])))

    [<Test>]
    member _.``Construction migrates legacy 'done' status rows to 'idle' in place``() =
        withDbPath (fun dbPath ->
            // Seed a 'done' row, dispose, then reopen: the second construction runs the migration.
            (use _ = new SessionActivityStore(dbPath)
             insertRawStatus dbPath "legacy" "C:/wt/a" "done" "2026-03-01T11:00:00Z")

            use reopened = new SessionActivityStore(dbPath)
            Assert.That(readRawStatus dbPath "legacy", Is.EqualTo("idle"), "stored row should be rewritten to 'idle'"))


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

    [<Test>]
    member _.``Construction adds metadata columns idempotently and preserves legacy rows``() =
        withDbPath (fun dbPath ->
            seedLegacyDatabase dbPath

            (use store = new SessionActivityStore(dbPath)
             let legacy = store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "legacy"
             Assert.That(legacy.Status.Intent, Is.EqualTo(None))
             Assert.That(legacy.Status.Title, Is.EqualTo(None))

             let intent = msg "investigating the fold" "2026-03-01T11:45:00Z"
             let title = msg "Investigate the fold" "2026-03-01T11:46:00Z"

             { legacy with
                 Status.Intent = Some intent
                 Status.Title = Some title
                 UpdatedAt = ts "2026-03-01T11:46:00Z"
                 LastSeen = ts "2026-03-01T11:50:00Z" }
             |> store.UpsertStatus)

            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "legacy"
            Assert.That(row.Status.Intent, Is.EqualTo(Some(msg "investigating the fold" "2026-03-01T11:45:00Z")))
            Assert.That(row.Status.Title, Is.EqualTo(Some(msg "Investigate the fold" "2026-03-01T11:46:00Z"))))

    [<Test>]
    member _.``Construction adds context columns idempotently and preserves legacy rows``() =
        withDbPath (fun dbPath ->
            seedLegacyDatabase dbPath
            let usage = { CurrentTokens = 50000; TokenLimit = 200000 }
            let usageAt = ts "2026-03-01T11:45:00Z"

            (use store = new SessionActivityStore(dbPath)
             let legacy = store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "legacy"
             Assert.That(legacy.Status.ContextUsage, Is.EqualTo(None))
             Assert.That(legacy.ContextUsageAt, Is.EqualTo(None))

             let persisted =
                 { legacy with
                     Status.ContextUsage = Some usage
                     ContextUsageAt = Some usageAt
                     LastSeen = usageAt }
                 |> store.UpsertContextUsage

             Assert.That(persisted.Status.ContextUsage, Is.EqualTo(Some usage))
             Assert.That(persisted.ContextUsageAt, Is.EqualTo(Some usageAt)))

            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "legacy"
            Assert.That(row.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(row.ContextUsageAt, Is.EqualTo(Some usageAt)))
