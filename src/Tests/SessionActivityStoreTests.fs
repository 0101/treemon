module Tests.SessionActivityStoreTests

open System
open System.IO
open NUnit.Framework
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
                    Status = Working
                    Skill = Some "review" }

            let newer =
                { emptyStatus with
                    Status = WaitingForUser
                    Skill = Some "investigate" }

            store.UpsertStatus(storedOf "s1" "C:/wt/a" older "2026-03-01T10:00:00Z" "2026-03-01T12:00:00Z")
            store.UpsertStatus(storedOf "s1" "C:/wt/a" newer "2026-03-01T10:05:00Z" "2026-03-01T12:00:00Z")

            let row = store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo(WaitingForUser))
            Assert.That(row.Status.Skill, Is.EqualTo(Some "investigate"))
            Assert.That(row.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:05:00Z")))

    [<Test>]
    member _.``A stale (older) report is ignored, leaving the newer row intact``() =
        withStore (fun store ->
            let newer =
                { emptyStatus with
                    Status = WaitingForUser
                    Skill = Some "investigate" }

            let stale =
                { emptyStatus with
                    Status = Idle
                    Skill = None }

            store.UpsertStatus(storedOf "s1" "C:/wt/a" newer "2026-03-01T10:05:00Z" "2026-03-01T12:00:00Z")
            // Older updated_at AND a would-be-newer last_seen: the whole upsert must be a no-op.
            store.UpsertStatus(storedOf "s1" "C:/wt/a" stale "2026-03-01T10:02:00Z" "2026-03-01T12:30:00Z")

            let row = store.LoadLiveStatuses(ts "2026-03-01T12:30:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo(WaitingForUser))
            Assert.That(row.Status.Skill, Is.EqualTo(Some "investigate"))
            Assert.That(row.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:05:00Z"))
            Assert.That(row.LastSeen, Is.EqualTo(ts "2026-03-01T12:00:00Z"), "stale upsert must not bump last_seen"))

    [<Test>]
    member _.``An equal-timestamp replay lands identically (>= is idempotent)``() =
        withStore (fun store ->
            let a =
                { emptyStatus with
                    Status = Working
                    Skill = Some "review" }

            store.UpsertStatus(storedOf "s1" "C:/wt/a" a "2026-03-01T10:00:00Z" "2026-03-01T12:00:00Z")
            store.UpsertStatus(storedOf "s1" "C:/wt/a" a "2026-03-01T10:00:00Z" "2026-03-01T12:00:00Z")

            let rows = store.LoadLiveStatuses(ts "2026-03-01T12:00:00Z")
            Assert.That(rows.Length, Is.EqualTo(1))
            Assert.That((find "s1" rows).Status.Status, Is.EqualTo(Working)))

    [<Test>]
    member _.``Skill and both messages round-trip through the store``() =
        withStore (fun store ->
            let rich =
                { Status = WaitingForUser
                  Skill = Some "review"
                  LastUserMessage = Some(msg "the auth module" "2026-03-01T10:01:00Z")
                  LastAssistantMessage = Some(msg "which file?" "2026-03-01T10:00:30Z") }

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
            let e = eventOf "e1" "s1" "turn_started" Working None "2026-03-01T10:00:00Z"

            Assert.That(store.AppendEvent e, Is.True, "first insert should report inserted")
            Assert.That(store.AppendEvent e, Is.False, "duplicate event_id should report ignored")

            let rows = store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-02T00:00:00Z")
            Assert.That(rows.Length, Is.EqualTo(1), "duplicate must not add a second row"))

    [<Test>]
    member _.``Distinct event_ids are all appended``() =
        withStore (fun store ->
            Assert.That(store.AppendEvent(eventOf "e1" "s1" "turn_started" Working None "2026-03-01T10:00:00Z"), Is.True)
            Assert.That(
                store.AppendEvent(eventOf "e2" "s1" "skill_invoked" Working (Some "review") "2026-03-01T10:00:01Z"),
                Is.True
            )

            let rows = store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-02T00:00:00Z")
            Assert.That(rows.Length, Is.EqualTo(2)))

    [<Test>]
    member _.``An appended event round-trips its fields``() =
        withStore (fun store ->
            let e = eventOf "e1" "s1" "skill_invoked" Working (Some "review") "2026-03-01T10:00:00Z"
            store.AppendEvent e |> ignore

            let row =
                store.QueryWindow(ts "2026-03-01T00:00:00Z", ts "2026-03-02T00:00:00Z") |> List.exactlyOne

            Assert.That(row, Is.EqualTo(e)))


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
                    Status = Working
                    Skill = Some "bd-execute" }

            // First instance writes, then is disposed (checkpoints WAL, releases the file).
            (use store = new SessionActivityStore(dbPath)
             store.UpsertStatus(storedOf "s1" "C:/wt/a" working "2026-03-01T11:30:00Z" "2026-03-01T11:30:00Z"))

            // A fresh instance over the same path rebuilds the live status with no new events.
            use reopened = new SessionActivityStore(dbPath)
            let row = reopened.LoadLiveStatuses(ts "2026-03-01T12:00:00Z") |> find "s1"
            Assert.That(row.Status.Status, Is.EqualTo(Working))
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
type QueryWindowTests() =

    let seed (store: SessionActivityStore) =
        [ "e0", "2026-03-01T09:00:00Z"
          "e1", "2026-03-01T10:00:00Z"
          "e2", "2026-03-01T11:00:00Z"
          "e3", "2026-03-01T12:00:00Z" ]
        |> List.iter (fun (eid, t) -> store.AppendEvent(eventOf eid "s1" "turn_started" Working None t) |> ignore)

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
            store.AppendEvent(eventOf "e1" "s1" "turn_started" Working None "2026-03-01T01:00:00Z") |> ignore
            store.AppendEvent(eventOf "e2" "s1" "turn_started" Working None "2026-03-01T02:00:00Z") |> ignore
            store.AppendEvent(eventOf "e3" "s1" "turn_started" Working None "2026-03-01T03:00:00Z") |> ignore

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
