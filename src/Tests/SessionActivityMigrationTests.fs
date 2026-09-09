module Tests.SessionActivityMigrationTests

open Microsoft.Data.Sqlite
open NUnit.Framework
open Server
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.TestUtils
open Tests.SessionActivityMigrationFixture

let private withDbPath action =
    SqliteTestDatabase.withDbPath "treemon-session-migration" action

let private scalarInt path sql =
    SqliteTestDatabase.scalarInt path sql

let private currentWriterIdentity =
    ProcessIdentity.create 7300 8300L
    |> Result.defaultWith invalidOp

let private currentWriterInstance updatedAt =
    let updated = ts updatedAt

    { ProcessIdentity = currentWriterIdentity
      SessionId = SessionId "legacy-session"
      TerminalSessionId = None
      WorktreePath = WorktreePath "C:/wt/legacy"
      Provider = CopilotCli
      Status =
        { emptyStatus with
            Status = SessionLevelStatus.Idle
            Title = Some(msg "Updated legacy title" updatedAt) }
      UpdatedAt = updated
      LifecycleAt = Some updated
      LastSeen = updated
      ContextUsageAt = None
      ClosedAt = None }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ProcessIdentitySchemaTests() =

    [<Test>]
    member _.``Process identity requires a positive PID and process-start timestamp``() =
        let identity = ProcessIdentity.create 42 638_925_000_000_000L

        Assert.Multiple(fun () ->
            Assert.That(
                identity
                |> Result.map ProcessIdentity.sortKey
                |> Result.defaultValue (0, 0L),
                Is.EqualTo((42, 638_925_000_000_000L))
            )
            Assert.That(ProcessIdentity.create 0 1L |> Result.isError, Is.True)
            Assert.That(ProcessIdentity.create 1 0L |> Result.isError, Is.True))

    [<Test>]
    member _.``Session instances key reused PIDs by their distinct start ticks``() =
        withDbPath (fun path ->
            use _store = new SessionActivityStore(path)

            insertExactInstance path 7001 8001L "shared-session"
            insertExactInstance path 7001 8002L "shared-session"

            Assert.Multiple(fun () ->
                Assert.That(
                    primaryKeyColumns path "session_instances",
                    Is.EqualTo([ "process_id"; "process_start_ticks" ])
                )
                Assert.That(
                    indexColumns path "ix_instances_terminal_activity",
                    Is.EqualTo([ "terminal_session_id"; "updated_at"; "session_id" ])
                )
                Assert.That(scalarInt path "SELECT count(*) FROM session_instances;", Is.EqualTo 2))

            Assert.Throws<SqliteException>(fun () ->
                insertExactInstance path 7001 8001L "other-session")
            |> ignore

            Assert.Throws<SqliteException>(fun () ->
                insertExactInstance path 0 8003L "invalid-pid")
            |> ignore

            Assert.Throws<SqliteException>(fun () ->
                insertExactInstance path 7002 0L "invalid-start")
            |> ignore)

    [<Test>]
    member _.``Activity event idempotency is scoped to one exact process identity``() =
        withDbPath (fun path ->
            use _store = new SessionActivityStore(path)

            insertExactEvent path 7001 8001L "same-event"
            insertExactEvent path 7001 8002L "same-event"

            Assert.Throws<SqliteException>(fun () ->
                insertExactEvent path 7001 8001L "same-event")
            |> ignore

            Assert.Multiple(fun () ->
                Assert.That(
                    primaryKeyColumns path "activity_events",
                    Is.EqualTo([ "process_id"; "process_start_ticks"; "event_id" ])
                )
                Assert.That(
                    tableColumns path "activity_events",
                    Is.EqualTo([ "process_id"; "process_start_ticks"; "event_id"; "ts" ]),
                    "event rows carry the dedupe key only"
                )
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 2)))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SessionHistoryMigrationTests() =

    [<Test>]
    member _.``Main's session rows migrate to the latest resume identity per worktree``() =
        withDbPath (fun path ->
            createLegacyDatabase path

            use store = new SessionActivityStore(path)

            Assert.Multiple(fun () ->
                Assert.That(
                    tableColumns path "resume_sessions",
                    Is.EqualTo([ "session_id"; "worktree_path"; "updated_at" ])
                )
                Assert.That(
                    resumeIdentity path "legacy-session",
                    Is.EqualTo(
                        Some("C:/wt/legacy", "2026-09-01T10:04:00.0000000+00:00")
                    )
                )
                Assert.That(
                    store.LatestSessionIdForWorktree(WorktreePath "C:/wt/legacy"),
                    Is.EqualTo(Some(SessionId "legacy-session")),
                    "the greatest pre-upgrade activity clock stays explicitly resumable"
                )
                Assert.That(
                    store.RetainedByWorktree() |> Map.isEmpty,
                    Is.True,
                    "pre-upgrade footer content is deliberately not migrated"
                )
                Assert.That(
                    scalarInt path "SELECT count(*) FROM activity_events;",
                    Is.Zero,
                    "event rows keyed by event id alone cannot prove their producer"
                )
                Assert.That(
                    schemaNames path "table" |> List.contains "session_status",
                    Is.False
                ))

            let stored = currentWriterInstance "2026-09-04T10:00:00Z"

            let eventRow =
                { ProcessIdentity = currentWriterIdentity
                  EventId = EventId "current-writer-event"
                  Ts = ts "2026-09-04T10:00:00Z" }

            Assert.That(store.AppendAndUpsert(eventRow, stored) |> Option.isSome, Is.True)
            Assert.That(store.AppendAndUpsert(eventRow, stored), Is.EqualTo None)
            Assert.That(
                scalarInt
                    path
                    "SELECT count(*) FROM activity_events
                     WHERE process_id = 7300 AND process_start_ticks = 8300;",
                Is.EqualTo 1
            )
            Assert.That(
                store.RetainedByWorktree() |> Map.containsKey "C:/wt/legacy",
                Is.True,
                "new exact reports restore the worktree representative"
            ))

    [<Test>]
    member _.``This branch's prior tables collapse into resume identity and dedupe keys``() =
        withDbPath (fun path ->
            createPriorBranchDatabase path

            use store = new SessionActivityStore(path)
            let retained = store.RetainedByWorktree()

            Assert.Multiple(fun () ->
                Assert.That(
                    resumeIdentity path "prior-retained-session",
                    Is.EqualTo(
                        Some("C:/wt/prior-retained", "2026-09-01T10:04:00.0000000+00:00")
                    )
                )
                Assert.That(
                    store.LatestSessionIdForWorktree(WorktreePath "C:/wt/prior-retained"),
                    Is.EqualTo(Some(SessionId "prior-retained-session"))
                )
                Assert.That(
                    schemaNames path "table"
                    |> List.filter (fun name ->
                        name = "retained_sessions"
                        || name = "worktree_representatives"),
                    Is.Empty
                )
                Assert.That(
                    tableColumns path "activity_events",
                    Is.EqualTo([ "process_id"; "process_start_ticks"; "event_id"; "ts" ])
                )
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM activity_events
                         WHERE process_id = 9100 AND process_start_ticks = 9200
                           AND event_id = 'prior-event'
                           AND ts = '2026-09-04T09:58:00.0000000+00:00';",
                    Is.EqualTo 1,
                    "process-keyed events keep their key and timestamp"
                )
                Assert.That(
                    retained["C:/wt/prior"].SessionId,
                    Is.EqualTo(SessionId "prior-exact-session"),
                    "exact instances remain the representative source"
                )
                Assert.That(
                    store.LoadRecentInstances(ts "2026-09-04T10:30:00Z")
                    |> List.map (fun instance ->
                        SessionId.value instance.SessionId, instance.LifecycleAt.IsNone),
                    Is.EqualTo([ "prior-exact-session", true ]),
                    "instances predating the lifecycle clock stay readable"
                )))

    [<Test>]
    member _.``Partially migrated schema resumes and removes stale rebuild state``() =
        withDbPath (fun path ->
            createLegacyDatabase path
            SqliteTestDatabase.execute
                path
                "CREATE TABLE activity_events_migration (stale TEXT);"

            use _reopened = new SessionActivityStore(path)

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM resume_sessions;", Is.EqualTo 2)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.Zero)
                Assert.That(
                    schemaNames path "table"
                    |> List.contains "activity_events_migration",
                    Is.False
                )))

    [<Test>]
    member _.``A failed migration rolls back and leaves the legacy tables intact for a retry``() =
        withDbPath (fun path ->
            createLegacyDatabase path
            SqliteTestDatabase.execute
                path
                "CREATE TABLE resume_sessions (session_id TEXT PRIMARY KEY);"

            Assert.Throws<SqliteException>(fun () ->
                use _store = new SessionActivityStore(path)
                ())
            |> ignore

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM session_status;", Is.EqualTo 2)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 1)
                Assert.That(primaryKeyColumns path "activity_events", Is.EqualTo([ "event_id" ]))
                Assert.That(
                    schemaNames path "table" |> List.contains "session_instances",
                    Is.False,
                    "schema creation and event replacement share the failed transaction"
                ))

            SqliteTestDatabase.execute path "DROP TABLE resume_sessions;"

            use _reopened = new SessionActivityStore(path)

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM resume_sessions;", Is.EqualTo 2)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.Zero)
                Assert.That(
                    primaryKeyColumns path "activity_events",
                    Is.EqualTo([ "process_id"; "process_start_ticks"; "event_id" ])
                )))

    [<Test>]
    member _.``Migrated rows and resume identity survive repeated store construction``() =
        withDbPath (fun path ->
            createLegacyDatabase path

            (use _store = new SessionActivityStore(path)
             ())

            insertExactInstance path 7200 8200L "exact-session"
            insertExactEvent path 7200 8200L "exact-event"

            (use _reopened = new SessionActivityStore(path)
             ())

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM resume_sessions;", Is.EqualTo 2)
                Assert.That(scalarInt path "SELECT count(*) FROM session_instances;", Is.EqualTo 1)
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM activity_events
                         WHERE process_id = 7200 AND process_start_ticks = 8200
                           AND event_id = 'exact-event';",
                    Is.EqualTo 1
                )))

    [<Test>]
    member _.``Retention removes pre-upgrade resume identity``() =
        withDbPath (fun path ->
            createLegacyDatabase path

            use store = new SessionActivityStore(path)
            store.PruneOld(ts "2026-09-02T00:00:00Z") |> ignore

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM resume_sessions;", Is.Zero)
                Assert.That(
                    store.LatestSessionIdForWorktree(WorktreePath "C:/wt/legacy"),
                    Is.EqualTo None
                )))
