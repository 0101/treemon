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
// upserts, INSERT OR IGNORE event dedupe, the restart rebuild (loadLiveStatuses within the idle
// window), the history-substrate window query, and retention pruning. Each test runs against a fresh
// temp .db file that is disposed + deleted in teardown.

/// A fresh store over a throwaway temp .db, disposed (releasing the file handle) and its dir deleted
/// afterwards. Store construction creates the schema, so the DB is ready to use inside `action`.
let private withStore (action: SessionActivityStore -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"treemon-store-test-{Guid.NewGuid()}")
    Directory.CreateDirectory dir |> ignore
    let store = new SessionActivityStore(Path.Combine(dir, "activity.db"))

    try
        action store
    finally
        (store :> IDisposable).Dispose()

        try
            Directory.Delete(dir, true)
        with _ ->
            ()

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

let private storedOf sid wt (status: SessionStatus) updatedAt lastSeen : StoredStatus =
    { SessionId = SessionId sid
      WorktreePath = WorktreePath wt
      Provider = CopilotCli
      Status = status
      UpdatedAt = ts updatedAt
      LastSeen = ts lastSeen }

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
    member _.``Skill and both messages round-trip through the store``() =
        withStore (fun store ->
            let rich =
                { Status = SessionLevelStatus.WaitingForUser
                  Skill = Some "review"
                  LastUserMessage = Some(msg "the auth module" "2026-03-01T10:01:00Z")
                  LastAssistantMessage = Some(msg "which file?" "2026-03-01T10:00:30Z")
                  ContextUsage = None }

            store.UpsertStatus(storedOf "s1" "C:/wt/a" rich "2026-03-01T10:01:00Z" "2026-03-01T12:00:00Z")

            let row = store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status, Is.EqualTo(rich))
            Assert.That(row.WorktreePath, Is.EqualTo(WorktreePath "C:/wt/a"))
            Assert.That(row.Provider, Is.EqualTo(CopilotCli)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AppendEventTests() =

    [<Test>]
    member _.``A duplicate event_id is ignored (dedupe) and the row count is unchanged``() =
        withStore (fun store ->
            let e = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T10:00:00Z"

            Assert.That(store.AppendEvent e, Is.True, "first insert should report inserted")
            Assert.That(store.AppendEvent e, Is.False, "duplicate event_id should report ignored")

            let rows = store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-02T00:00:00Z")
            Assert.That(rows.Length, Is.EqualTo(1), "duplicate must not add a second row"))

    [<Test>]
    member _.``Distinct event_ids are all appended``() =
        withStore (fun store ->
            Assert.That(store.AppendEvent(eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T10:00:00Z"), Is.True)
            Assert.That(
                store.AppendEvent(eventOf "e2" "s1" "skill_invoked" SessionLevelStatus.Working (Some "review") "2026-03-01T10:00:01Z"),
                Is.True
            )

            let rows = store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-02T00:00:00Z")
            Assert.That(rows.Length, Is.EqualTo(2)))

    [<Test>]
    member _.``An appended event round-trips its fields``() =
        withStore (fun store ->
            let e = eventOf "e1" "s1" "skill_invoked" SessionLevelStatus.Working (Some "review") "2026-03-01T10:00:00Z"
            store.AppendEvent e |> ignore

            let row =
                store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-02T00:00:00Z") |> List.exactlyOne

            Assert.That(row, Is.EqualTo(e)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AppendAndUpsertTests() =

    [<Test>]
    member _.``A new event is appended and the live status upserted in one call``() =
        withStore (fun store ->
            let status = { emptyStatus with Status = SessionLevelStatus.Working; Skill = Some "review" }
            let e = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working (Some "review") "2026-03-01T10:00:00Z"
            let stored = storedOf "s1" "C:/wt/a" status "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z"

            Assert.That(store.AppendAndUpsert(e, stored), Is.True, "a new event reports inserted")

            let events = store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-02T00:00:00Z")
            Assert.That(events.Length, Is.EqualTo 1, "the event was appended")
            let row = store.LoadLiveStatuses(ts "2026-03-01T10:00:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo SessionLevelStatus.Working, "the status was upserted in the same call"))

    [<Test>]
    member _.``A duplicate event_id skips BOTH the append and the upsert (coupled idempotency)``() =
        withStore (fun store ->
            let first = { emptyStatus with Status = SessionLevelStatus.Working }
            let e = eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T10:00:00Z"
            Assert.That(
                store.AppendAndUpsert(e, storedOf "s1" "C:/wt/a" first "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z"),
                Is.True
            )

            // Same event_id but a would-be-newer status: the dedupe must skip the upsert together with
            // the append, so the status can never advance off a deduped event.
            let laterStatus = { emptyStatus with Status = SessionLevelStatus.WaitingForUser }
            Assert.That(
                store.AppendAndUpsert(e, storedOf "s1" "C:/wt/a" laterStatus "2026-03-01T10:05:00Z" "2026-03-01T10:05:00Z"),
                Is.False,
                "a duplicate event_id reports ignored"
            )

            let events = store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-02T00:00:00Z")
            Assert.That(events.Length, Is.EqualTo 1, "no second event row")
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
type QueryWindowTests() =

    let seed (store: SessionActivityStore) =
        [ "e0", "2026-03-01T09:00:00Z"
          "e1", "2026-03-01T10:00:00Z"
          "e2", "2026-03-01T11:00:00Z"
          "e3", "2026-03-01T12:00:00Z" ]
        |> List.iter (fun (eid, t) -> store.AppendEvent(eventOf eid "s1" "turn_started" SessionLevelStatus.Working None t) |> ignore)

    [<Test>]
    member _.``Only events inside the window are returned, oldest first``() =
        withStore (fun store ->
            seed store
            let rows = store.QueryWindow(ts "2026-03-01T09:30:00Z", ts "2026-03-01T11:30:00Z")

            Assert.That(
                rows |> List.map (_.EventId >> EventId.value),
                Is.EqualTo([ "e1"; "e2" ]),
                "window should drop out-of-range events and stay ordered by ts"
            ))

    [<Test>]
    member _.``Window boundaries are inclusive on both ends``() =
        withStore (fun store ->
            seed store
            let rows = store.QueryWindow(ts "2026-03-01T10:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(rows |> List.map (_.EventId >> EventId.value), Is.EqualTo([ "e1"; "e2" ])))

    [<Test>]
    member _.``A window covering nothing yields an empty list``() =
        withStore (fun store ->
            seed store
            Assert.That(store.QueryWindow(ts "2026-03-01T13:00:00Z", ts "2026-03-01T14:00:00Z"), Is.Empty))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PruneOldTests() =

    [<Test>]
    member _.``pruneOld drops events and session rows older than the cutoff and returns the count``() =
        withStore (fun store ->
            store.AppendEvent(eventOf "e1" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T01:00:00Z") |> ignore
            store.AppendEvent(eventOf "e2" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T02:00:00Z") |> ignore
            store.AppendEvent(eventOf "e3" "s1" "turn_started" SessionLevelStatus.Working None "2026-03-01T03:00:00Z") |> ignore

            store.UpsertStatus(storedOf "old" "C:/wt/a" emptyStatus "2026-03-01T01:00:00Z" "2026-03-01T01:00:00Z")
            store.UpsertStatus(storedOf "recent" "C:/wt/a" emptyStatus "2026-03-01T03:00:00Z" "2026-03-01T03:00:00Z")

            // cutoff 02:30 → e1(01:00), e2(02:00), old(01:00) go; e3(03:00), recent(03:00) stay.
            let deleted = store.PruneOld(ts "2026-03-01T02:30:00Z")
            Assert.That(deleted, Is.EqualTo(3))

            let remainingEvents =
                store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-01T23:59:59Z")
                |> List.map (_.EventId >> EventId.value)

            Assert.That(remainingEvents, Is.EqualTo([ "e3" ]))

            let remainingSessions =
                store.LoadLiveStatuses(ts "2026-03-01T03:30:00Z")
                |> List.map (_.SessionId >> SessionId.value)

            Assert.That(remainingSessions, Is.EqualTo([ "recent" ])))

    [<Test>]
    member _.``pruneOld on an empty store deletes nothing``() =
        withStore (fun store -> Assert.That(store.PruneOld(ts "2026-03-01T12:00:00Z"), Is.EqualTo(0)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LegacyDoneStatusTests() =

    // Pre-idle-only builds persisted the retired "done" status; live DBs still hold such rows. The
    // idempotent construction-time migration rewrites 'done' rows to 'idle' so the unguarded reads
    // (LoadLiveStatuses at startup, StatusesForWorktree on resume) never hit an unknown status.

    let connStr (dbPath: string) =
        SqliteConnectionStringBuilder(DataSource = dbPath, Pooling = false).ConnectionString

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
