module Tests.SessionActivityMigrationTests

open System
open Microsoft.Data.Sqlite
open NUnit.Framework
open Server
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.TestUtils

type private RetainedSnapshot =
    { SessionId: string
      WorktreePath: string
      Status: string
      Skill: string option
      LastUserMessage: string option
      LastAssistantMessage: string option
      Intent: string option
      Title: string option
      UpdatedAt: string
      ContextCurrent: int option
      ContextLimit: int option
      ContextUsageAt: string option
      AwaitingUserSince: string option
      UserInputCompletedAt: string option }

let private withDbPath action =
    SqliteTestDatabase.withDbPath "treemon-session-migration" action

let private readStrings (reader: SqliteDataReader) =
    let rec read acc =
        if reader.Read() then read (reader.GetString 0 :: acc)
        else List.rev acc

    read []

let private tableColumns path tableName =
    use connection = SqliteTestDatabase.openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{tableName}');"
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then read (reader.GetString 1 :: acc)
        else List.rev acc

    read []

let private primaryKeyColumns path tableName =
    use connection = SqliteTestDatabase.openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{tableName}');"
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then
            let primaryKeyOrder = reader.GetInt32 5

            let next =
                if primaryKeyOrder = 0 then
                    acc
                else
                    (primaryKeyOrder, reader.GetString 1) :: acc

            read next
        else
            acc
            |> List.sortBy fst
            |> List.map snd

    read []

let private indexColumns path indexName =
    use connection = SqliteTestDatabase.openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA index_info('{indexName}');"
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then read (reader.GetString 2 :: acc)
        else List.rev acc

    read []

let private schemaNames path objectType =
    use connection = SqliteTestDatabase.openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
SELECT name
FROM sqlite_master
WHERE type = $type
ORDER BY name;
"""
    command.Parameters.AddWithValue("$type", objectType) |> ignore
    use reader = command.ExecuteReader()
    readStrings reader

let private scalarInt path sql =
    SqliteTestDatabase.scalarInt path sql

let private readOptionalString (reader: SqliteDataReader) index =
    if reader.IsDBNull index then None else Some(reader.GetString index)

let private readOptionalInt (reader: SqliteDataReader) index =
    if reader.IsDBNull index then None else Some(reader.GetInt32 index)

let private retainedSnapshot path sessionId =
    use connection = SqliteTestDatabase.openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
SELECT session_id, worktree_path, status, current_skill,
       last_user_msg, last_asst_msg, intent_text, title_text, updated_at,
       context_current_tokens, context_token_limit, context_usage_at,
       awaiting_user_since, user_input_completed_at
FROM retained_sessions
WHERE session_id = $sessionId;
"""
    command.Parameters.AddWithValue("$sessionId", sessionId) |> ignore
    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failwith $"Missing retained session {sessionId}"

    { SessionId = reader.GetString 0
      WorktreePath = reader.GetString 1
      Status = reader.GetString 2
      Skill = readOptionalString reader 3
      LastUserMessage = readOptionalString reader 4
      LastAssistantMessage = readOptionalString reader 5
      Intent = readOptionalString reader 6
      Title = readOptionalString reader 7
      UpdatedAt = reader.GetString 8
      ContextCurrent = readOptionalInt reader 9
      ContextLimit = readOptionalInt reader 10
      ContextUsageAt = readOptionalString reader 11
      AwaitingUserSince = readOptionalString reader 12
      UserInputCompletedAt = readOptionalString reader 13 }

let private createLegacyDatabase path =
    use connection = SqliteTestDatabase.openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
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
    intent_text   TEXT,
    intent_ts     TEXT,
    title_text    TEXT,
    title_ts      TEXT,
    updated_at    TEXT NOT NULL,
    last_seen     TEXT NOT NULL,
    context_current_tokens INTEGER,
    context_token_limit     INTEGER,
    context_usage_at        TEXT,
    awaiting_user_since     TEXT,
    user_input_completed_at TEXT,
    terminal_session_id     TEXT
);

CREATE TABLE activity_events (
    event_id      TEXT PRIMARY KEY,
    session_id    TEXT NOT NULL,
    worktree_path TEXT NOT NULL,
    provider      TEXT NOT NULL,
    kind          TEXT NOT NULL,
    status        TEXT NOT NULL,
    skill         TEXT,
    ts            TEXT NOT NULL
);

INSERT INTO session_status
    (session_id, worktree_path, provider, status, current_skill,
     last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
     intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
     context_current_tokens, context_token_limit, context_usage_at,
     awaiting_user_since, user_input_completed_at, terminal_session_id)
VALUES
    ('legacy-session', 'C:/wt/legacy', 'copilot_cli', 'waiting_for_user', 'review',
     'please review', '2026-09-01T10:00:00.0000000+00:00',
     'working on it', '2026-09-01T10:01:00.0000000+00:00',
     'reviewing storage', '2026-09-01T10:02:00.0000000+00:00',
     'Review storage', '2026-09-01T10:03:00.0000000+00:00',
     '2026-09-01T10:04:00.0000000+00:00',
     '2026-09-04T10:04:00.0000000+00:00',
     120000, 200000, '2026-09-01T10:03:30.0000000+00:00',
     NULL, '2026-09-01T09:59:00.0000000+00:00',
     '0123456789abcdef0123456789abcdef');

INSERT INTO activity_events
    (event_id, session_id, worktree_path, provider, kind, status, skill, ts)
VALUES
    ('legacy-event', 'legacy-session', 'C:/wt/legacy', 'copilot_cli',
     'awaiting_user_input', 'waiting_for_user', 'review',
     '2026-09-01T10:04:00.0000000+00:00');
"""
    command.ExecuteNonQuery() |> ignore

let private insertExactInstance path processId processStartTicks sessionId =
    use connection = SqliteTestDatabase.openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
INSERT INTO session_instances
    (process_id, process_start_ticks, session_id, worktree_path, provider, status,
     updated_at, last_seen)
VALUES
    ($processId, $processStartTicks, $sessionId, 'C:/wt/exact', 'copilot_cli', 'idle',
     '2026-09-04T10:00:00.0000000+00:00', '2026-09-04T10:00:00.0000000+00:00');
"""
    command.Parameters.AddWithValue("$processId", processId) |> ignore
    command.Parameters.AddWithValue("$processStartTicks", processStartTicks) |> ignore
    command.Parameters.AddWithValue("$sessionId", sessionId) |> ignore
    command.ExecuteNonQuery() |> ignore

let private insertExactEvent path processId processStartTicks eventId =
    use connection = SqliteTestDatabase.openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
INSERT INTO activity_events
    (process_id, process_start_ticks, event_id, session_id, worktree_path,
     provider, kind, status, skill, ts)
VALUES
    ($processId, $processStartTicks, $eventId, 'shared-session', 'C:/wt/exact',
     'copilot_cli', 'turn_started', 'working', NULL,
     '2026-09-04T10:00:00.0000000+00:00');
"""
    command.Parameters.AddWithValue("$processId", processId) |> ignore
    command.Parameters.AddWithValue("$processStartTicks", processStartTicks) |> ignore
    command.Parameters.AddWithValue("$eventId", eventId) |> ignore
    command.ExecuteNonQuery() |> ignore

let private currentWriterIdentity =
    ProcessIdentity.create 7300 8300L
    |> Result.defaultWith invalidOp

let private currentWriterStatus updatedAt =
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
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 2)))

    [<Test>]
    member _.``Intermediate sentinel event lane is retired while exact rows survive``() =
        withDbPath (fun path ->
            SqliteTestDatabase.execute
                path
                """
CREATE TABLE activity_events (
    process_id          INTEGER NOT NULL DEFAULT 0,
    process_start_ticks INTEGER NOT NULL DEFAULT 0,
    event_id            TEXT NOT NULL,
    session_id          TEXT NOT NULL,
    worktree_path       TEXT NOT NULL,
    provider            TEXT NOT NULL,
    kind                TEXT NOT NULL,
    status              TEXT NOT NULL,
    skill               TEXT,
    ts                  TEXT NOT NULL,
    PRIMARY KEY (process_id, process_start_ticks, event_id),
    CHECK (
        (process_id = 0 AND process_start_ticks = 0)
        OR (process_id > 0 AND process_start_ticks > 0)
    )
);

INSERT INTO activity_events
    (process_id, process_start_ticks, event_id, session_id, worktree_path,
     provider, kind, status, skill, ts)
VALUES
    (0, 0, 'legacy-event', 'legacy-session', 'C:/wt/legacy',
     'copilot_cli', 'turn_started', 'working', NULL,
     '2026-09-04T09:00:00.0000000+00:00'),
    (7400, 8400, 'exact-event', 'exact-session', 'C:/wt/exact',
     'copilot_cli', 'turn_started', 'working', NULL,
     '2026-09-04T10:00:00.0000000+00:00');
"""

            use _store = new SessionActivityStore(path)

            Assert.Multiple(fun () ->
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM activity_events
                         WHERE process_id = 7400 AND process_start_ticks = 8400;",
                    Is.EqualTo 1
                )
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM activity_events
                         WHERE process_id <= 0 OR process_start_ticks <= 0;",
                    Is.Zero
                ))

            Assert.Throws<SqliteException>(
                TestDelegate(fun () ->
                    SqliteTestDatabase.execute
                        path
                        """
INSERT INTO activity_events
    (process_id, process_start_ticks, event_id, session_id, worktree_path,
     provider, kind, status, skill, ts)
VALUES
    (0, 0, 'invalid', 'invalid', 'C:/wt/invalid',
     'copilot_cli', 'turn_started', 'working', NULL,
     '2026-09-04T10:00:00.0000000+00:00');
""")
            )
            |> ignore)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SessionHistoryMigrationTests() =

    [<Test>]
    member _.``Legacy history is retained before unrecoverable event rows are discarded``() =
        withDbPath (fun path ->
            createLegacyDatabase path

            use store = new SessionActivityStore(path)
            let retained = retainedSnapshot path "legacy-session"
            let columns = tableColumns path "retained_sessions" |> Set.ofList

            Assert.Multiple(fun () ->
                Assert.That(retained.SessionId, Is.EqualTo "legacy-session")
                Assert.That(retained.WorktreePath, Is.EqualTo "C:/wt/legacy")
                Assert.That(retained.Status, Is.EqualTo "idle")
                Assert.That(retained.Skill, Is.EqualTo(Some "review"))
                Assert.That(retained.LastUserMessage, Is.EqualTo(Some "please review"))
                Assert.That(retained.LastAssistantMessage, Is.EqualTo(Some "working on it"))
                Assert.That(retained.Intent, Is.EqualTo(Some "reviewing storage"))
                Assert.That(retained.Title, Is.EqualTo(Some "Review storage"))
                Assert.That(
                    retained.UpdatedAt,
                    Is.EqualTo "2026-09-01T10:04:00.0000000+00:00"
                )
                Assert.That(retained.ContextCurrent, Is.EqualTo(Some 120000))
                Assert.That(retained.ContextLimit, Is.EqualTo(Some 200000))
                Assert.That(
                    retained.ContextUsageAt,
                    Is.EqualTo(Some "2026-09-01T10:03:30.0000000+00:00")
                )
                Assert.That(
                    retained.AwaitingUserSince,
                    Is.EqualTo(Some "2026-09-01T10:04:00.0000000+00:00")
                )
                Assert.That(
                    retained.UserInputCompletedAt,
                    Is.EqualTo(Some "2026-09-01T09:59:00.0000000+00:00")
                )
                Assert.That(Set.contains "process_id" columns, Is.False)
                Assert.That(Set.contains "process_start_ticks" columns, Is.False)
                Assert.That(Set.contains "last_seen" columns, Is.False)
                Assert.That(Set.contains "terminal_session_id" columns, Is.False)
                Assert.That(Set.contains "closed_at" columns, Is.False)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 0)
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM sqlite_master
                         WHERE type = 'table' AND name = 'session_status';",
                    Is.Zero
                )
                Assert.That(
                    primaryKeyColumns path "activity_events",
                    Is.EqualTo([ "process_id"; "process_start_ticks"; "event_id" ])
                ))

            let eventRow =
                { ProcessIdentity = currentWriterIdentity
                  EventId = EventId "current-writer-event"
                  SessionId = SessionId "legacy-session"
                  WorktreePath = WorktreePath "C:/wt/legacy"
                  Provider = CopilotCli
                  Kind = "turn_ended"
                  Status = SessionLevelStatus.Idle
                  Skill = Some "review"
                  Ts = ts "2026-09-04T10:00:00Z" }

            let stored = currentWriterStatus "2026-09-04T10:00:00Z"
            Assert.That(store.AppendAndUpsert(eventRow, stored) |> Option.isSome, Is.True)
            Assert.That(store.AppendAndUpsert(eventRow, stored), Is.EqualTo None)
            Assert.That(
                scalarInt
                    path
                    "SELECT count(*) FROM activity_events
                     WHERE process_id = 7300 AND process_start_ticks = 8300;",
                Is.EqualTo 1
            ))

    [<Test>]
    member _.``Retained copy remains migration-only and excludes exact sessions``() =
        withDbPath (fun path ->
            createLegacyDatabase path

            (use store = new SessionActivityStore(path)
             store.UpsertInstance(currentWriterStatus "2026-09-04T10:00:00Z")
             |> ignore)

            insertExactInstance path 7100 8100L "exact-only-session"

            (use _reopened = new SessionActivityStore(path)
             let retained = retainedSnapshot path "legacy-session"

             Assert.Multiple(fun () ->
                 Assert.That(retained.Title, Is.EqualTo(Some "Review storage"))
                 Assert.That(
                     retained.UpdatedAt,
                     Is.EqualTo "2026-09-01T10:04:00.0000000+00:00"
                 )
                 Assert.That(scalarInt path "SELECT count(*) FROM retained_sessions;", Is.EqualTo 1)
                 Assert.That(
                     scalarInt
                         path
                         "SELECT count(*) FROM retained_sessions
                          WHERE session_id = 'exact-only-session';",
                     Is.EqualTo 0
                 ))))

    [<Test>]
    member _.``Retained history survives source retirement and shrinks only through retention``() =
        withDbPath (fun path ->
            createLegacyDatabase path

            (use _store = new SessionActivityStore(path)
             ())

            use reopened = new SessionActivityStore(path)
            Assert.That(scalarInt path "SELECT count(*) FROM retained_sessions;", Is.EqualTo 1)

            reopened.PruneOld(ts "2026-09-02T00:00:00Z") |> ignore
            Assert.That(scalarInt path "SELECT count(*) FROM retained_sessions;", Is.EqualTo 0))

    [<Test>]
    member _.``Partially migrated schema resumes and removes stale rebuild state``() =
        withDbPath (fun path ->
            createLegacyDatabase path

            use connection = SqliteTestDatabase.openConnection path
            use command = connection.CreateCommand()
            command.CommandText <-
                """
CREATE TABLE activity_events_migration (stale TEXT);
"""
            command.ExecuteNonQuery() |> ignore
            connection.Close()

            use _reopened = new SessionActivityStore(path)

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM retained_sessions;", Is.EqualTo 1)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 0)
                Assert.That(
                    schemaNames path "table"
                    |> List.contains "activity_events_migration",
                    Is.False
                )
                Assert.That(
                    primaryKeyColumns path "activity_events",
                    Is.EqualTo([ "process_id"; "process_start_ticks"; "event_id" ])
                )))

    [<Test>]
    member _.``Failed retained copy leaves the legacy key and rows intact for a later retry``() =
        withDbPath (fun path ->
            createLegacyDatabase path
            SqliteTestDatabase.execute
                path
                "CREATE TABLE retained_sessions (session_id TEXT PRIMARY KEY);"

            Assert.Throws<SqliteException>(fun () ->
                use _store = new SessionActivityStore(path)
                ())
            |> ignore

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 1)
                Assert.That(primaryKeyColumns path "activity_events", Is.EqualTo([ "event_id" ]))
                Assert.That(
                    schemaNames path "table" |> List.contains "session_instances",
                    Is.False,
                    "schema creation and event replacement share the failed transaction"
                ))

            SqliteTestDatabase.execute path "DROP TABLE retained_sessions;"

            use _reopened = new SessionActivityStore(path)

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM retained_sessions;", Is.EqualTo 1)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 0)
                Assert.That(
                    primaryKeyColumns path "activity_events",
                    Is.EqualTo([ "process_id"; "process_start_ticks"; "event_id" ])
                )))

    [<Test>]
    member _.``Already migrated exact rows survive repeated store construction``() =
        withDbPath (fun path ->
            (use _store = new SessionActivityStore(path)
             ())

            insertExactInstance path 7200 8200L "exact-session"
            insertExactEvent path 7200 8200L "exact-event"

            (use _reopened = new SessionActivityStore(path)
             ())

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM session_instances;", Is.EqualTo 1)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 1)
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM activity_events
                         WHERE process_id = 7200 AND process_start_ticks = 8200
                           AND event_id = 'exact-event';",
                    Is.EqualTo 1
                )))
