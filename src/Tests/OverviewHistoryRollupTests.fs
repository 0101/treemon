module Tests.OverviewHistoryRollupTests

open System
open System.IO
open Microsoft.Data.Sqlite
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewHistoryRollup
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.OverviewTestHelpers

let private withDbPath action =
    let directory = Path.Combine(Path.GetTempPath(), $"treemon-rollup-schema-{Guid.NewGuid()}")
    Directory.CreateDirectory directory |> ignore
    let path = Path.Combine(directory, "activity.db")

    try
        action path
    finally
        try
            Directory.Delete(directory, true)
        with _ ->
            ()

let private openConnection path =
    let connection =
        new SqliteConnection(
            SqliteConnectionStringBuilder(DataSource = path, Pooling = false).ConnectionString
        )

    connection.Open()
    connection

let private execute path sql =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

let private scalarInt path sql =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- sql
    Convert.ToInt32(command.ExecuteScalar())

let private observationBounds path sessionId =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
SELECT first_observed_at, last_observed_at
FROM overview_history_session_bounds
WHERE session_id = $session;
"""
    command.Parameters.AddWithValue("$session", SessionId.value sessionId) |> ignore
    use reader = command.ExecuteReader()

    if reader.Read() then Some(parseIso (reader.GetString 0), parseIso (reader.GetString 1))
    else None

let private failInvalidation path =
    execute
        path
        """
CREATE TRIGGER fail_overview_invalidation
BEFORE UPDATE OF source_generation ON overview_history_state
BEGIN
    SELECT RAISE(ABORT, 'forced overview invalidation failure');
END;
"""

let private stored sessionId observedAt =
    { SessionId = sessionId
      WorktreePath = WorktreePath "worktree-a"
      Provider = CopilotCli
      Status = { emptyStatus with Status = SessionLevelStatus.Working }
      UpdatedAt = observedAt
      LastSeen = observedAt
      ContextUsageAt = None }

let private columns path table =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info(%s{table});"
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then read (reader.GetString 1 :: acc)
        else List.rev acc

    read []

let private replaceStateTable path version resolution =
    execute
        path
        $"""
DROP TABLE overview_history_state;
CREATE TABLE overview_history_state (
    id INTEGER PRIMARY KEY,
    schema_version INTEGER NOT NULL,
    resolution_seconds INTEGER NOT NULL,
    source_generation INTEGER NOT NULL,
    published_generation INTEGER NOT NULL,
    complete_through INTEGER,
    earliest_dirty INTEGER
);
INSERT INTO overview_history_state
    (id, schema_version, resolution_seconds, source_generation, published_generation)
VALUES (1, {version}, {resolution}, 0, 0);
"""

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryGridTests() =

    [<Test>]
    member _.``latest complete boundary floors in UTC and keeps an exact boundary``() =
        let between =
            DateTimeOffset(2026, 7, 22, 12, 0, 44, 987, TimeSpan.FromHours 2.0)

        let exact =
            DateTimeOffset(2026, 7, 22, 12, 0, 30, TimeSpan.FromHours 2.0)

        Assert.Multiple(fun () ->
            Assert.That(
                latestCompleteBoundary between,
                Is.EqualTo(DateTimeOffset(2026, 7, 22, 10, 0, 30, TimeSpan.Zero))
            )
            Assert.That(
                latestCompleteBoundary exact,
                Is.EqualTo(DateTimeOffset(2026, 7, 22, 10, 0, 30, TimeSpan.Zero))
            ))

    [<Test>]
    member _.``first affected boundary includes exact timestamps and ceilings later timestamps``() =
        let exact = DateTimeOffset(2026, 7, 22, 10, 0, 30, TimeSpan.Zero)
        let later = exact.AddTicks 1L

        Assert.Multiple(fun () ->
            Assert.That(firstBoundaryAtOrAfter exact, Is.EqualTo exact)
            Assert.That(firstBoundaryAtOrAfter later, Is.EqualTo(exact.AddSeconds 30.0)))

    [<Test>]
    member _.``boundary ranges are canonical inclusive and empty when no boundary intersects``() =
        let start = DateTimeOffset(2026, 7, 22, 10, 0, 0, 1, TimeSpan.Zero)
        let ending = DateTimeOffset(2026, 7, 22, 10, 1, 0, TimeSpan.Zero)

        Assert.Multiple(fun () ->
            Assert.That(
                boundaries start ending |> Seq.toList,
                Is.EqualTo
                    [ DateTimeOffset(2026, 7, 22, 10, 0, 30, TimeSpan.Zero)
                      ending ]
            )
            Assert.That(
                boundaries start (start.AddMilliseconds 28.0) |> Seq.toList,
                Is.Empty
            ))

    [<TestCase(12, 5)>]
    [<TestCase(24, 10)>]
    [<TestCase(72, 30)>]
    member _.``supported windows use fixed canonical row strides``(hours, expected) =
        let window =
            match hours with
            | 12 -> HistoryWindow.Hours12
            | 24 -> HistoryWindow.Hours24
            | _ -> HistoryWindow.Hours72

        Assert.That(stride window, Is.EqualTo expected)

    [<Test>]
    member _.``retention keeps the 72 hour horizon plus one predecessor``() =
        let anchor = DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)
        let exposed = oldestExposedBoundary anchor
        let retained = oldestRetainedBoundary anchor
        let retainedRows = boundaries retained anchor |> Seq.length

        Assert.Multiple(fun () ->
            Assert.That(anchor - exposed, Is.EqualTo(TimeSpan.FromHours 72.0))
            Assert.That(exposed - retained, Is.EqualTo resolution)
            Assert.That(retainedRows, Is.EqualTo(72 * 60 * 2 + 2)))

    [<Test>]
    member _.``dirty boundaries include exact writes, ceil later writes, and clamp old writes``() =
        let anchor = DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)
        let exact = anchor.AddMinutes(-1.0)

        Assert.Multiple(fun () ->
            Assert.That(dirtyBoundary anchor exact, Is.EqualTo exact)
            Assert.That(
                dirtyBoundary anchor (exact.AddTicks 1L),
                Is.EqualTo(exact.AddSeconds 30.0)
            )
            Assert.That(
                dirtyBoundary anchor (anchor.AddDays(-30.0)),
                Is.EqualTo(oldestExposedBoundary anchor)
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryDerivedSchemaTests() =

    [<Test>]
    member _.``fresh schema is count only and initializes one supported state row``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let state = store.OverviewRollupState()

            Assert.Multiple(fun () ->
                Assert.That(state.SchemaVersion, Is.EqualTo schemaVersion)
                Assert.That(state.ResolutionSeconds, Is.EqualTo resolutionSeconds)
                Assert.That(state.SourceGeneration, Is.Zero)
                Assert.That(state.PublishedGeneration, Is.Zero)
                Assert.That(state.CompleteThrough, Is.EqualTo None)
                Assert.That(state.EarliestDirty, Is.EqualTo None)
                Assert.That(
                    columns path "overview_history_rows",
                    Is.EqualTo [ "bucket"; "tasks"; "agents" ]
                )
                Assert.That(
                    columns path "overview_history_staging",
                    Is.EqualTo [ "generation"; "bucket"; "tasks"; "agents" ]
                )
                Assert.That(
                    columns path "overview_history_session_bounds",
                    Is.EqualTo [ "session_id"; "first_observed_at"; "last_observed_at" ]
                )
                Assert.That(
                    scalarInt path
                        "SELECT count(*) FROM sqlite_master
                         WHERE type = 'index'
                           AND name IN ('ix_overview_staging_bucket',
                                        'ix_overview_bounds_last_first',
                                        'ix_overview_bounds_first_last');",
                    Is.EqualTo 3
                )))

    [<Test>]
    member _.``reopening a valid derived schema is idempotent and preserves rows``() =
        withDbPath (fun path ->
            let boundary = DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero) |> toBucket

            (use _ = new SessionActivityStore(path)
             ())

            execute
                path
                $"""
UPDATE overview_history_state
SET source_generation = 3, published_generation = 3, complete_through = {boundary};
INSERT INTO overview_history_rows(bucket, tasks, agents) VALUES ({boundary}, '[]', '[]');
INSERT INTO overview_history_staging(generation, bucket, tasks, agents)
VALUES (3, {boundary}, '[]', '[]');
INSERT INTO overview_history_session_bounds(session_id, first_observed_at, last_observed_at)
VALUES ('s1', '2026-07-22T11:00:00.0000000+00:00', '2026-07-22T12:00:00.0000000+00:00');
"""

            (use reopened = new SessionActivityStore(path)
             Assert.That(
                 reopened.OverviewRollupState().CompleteThrough,
                 Is.EqualTo(Some(DateTimeOffset.FromUnixTimeSeconds boundary))
             ))

            use reopenedAgain = new SessionActivityStore(path)

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_rows;", Is.EqualTo 1)
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_staging;", Is.EqualTo 1)
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_session_bounds;", Is.EqualTo 1)
                Assert.That(reopenedAgain.OverviewRollupState().PublishedGeneration, Is.EqualTo 3L)))

    [<TestCase(999, 30)>]
    [<TestCase(1, 60)>]
    member _.``unsupported schema or resolution rebuilds only derived state``(version, seconds) =
        withDbPath (fun path ->
            let eventAt = DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero)

            (use store = new SessionActivityStore(path)
             store.AppendEvent(
                 evt "raw-event" "s1" "C:/wt/a" SessionLevelStatus.Working None eventAt
             )
             |> ignore
             store.AppendTaskSnapshot(eventAt, []))

            replaceStateTable path version seconds
            execute
                path
                """
INSERT INTO overview_history_rows(bucket, tasks, agents) VALUES (1784721600, '[]', '[]');
INSERT INTO overview_history_staging(generation, bucket, tasks, agents)
VALUES (0, 1784721600, '[]', '[]');
INSERT OR REPLACE INTO overview_history_session_bounds(session_id, first_observed_at, last_observed_at)
VALUES ('s1', '2026-07-22T11:00:00.0000000+00:00', '2026-07-22T11:00:00.0000000+00:00');
"""

            use rebuilt = new SessionActivityStore(path)
            let state = rebuilt.OverviewRollupState()

            Assert.Multiple(fun () ->
                Assert.That(state.SchemaVersion, Is.EqualTo schemaVersion)
                Assert.That(state.ResolutionSeconds, Is.EqualTo resolutionSeconds)
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_rows;", Is.Zero)
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_staging;", Is.Zero)
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_session_bounds;", Is.Zero)
                Assert.That(
                    rebuilt.QueryWindow(eventAt, eventAt) |> List.map (_.EventId >> EventId.value),
                    Is.EqualTo [ "raw-event" ]
                )
                Assert.That(
                    rebuilt.QueryTaskSnapshots(eventAt, eventAt),
                    Is.EqualTo [ eventAt, ([]: TaskCount list) ]
                )))

    [<Test>]
    member _.``invalid derived row rebuilds without touching raw sources``() =
        withDbPath (fun path ->
            let at = DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)
            let bucket = toBucket at

            (use store = new SessionActivityStore(path)
             store.AppendEvent(evt "raw" "s1" "C:/wt/a" SessionLevelStatus.Idle None at) |> ignore)

            execute
                path
                $"""
UPDATE overview_history_state
SET source_generation = 1, published_generation = 1, complete_through = {bucket};
INSERT INTO overview_history_rows(bucket, tasks, agents)
VALUES ({bucket}, '{{not-json', '[]');
"""

            use rebuilt = new SessionActivityStore(path)

            Assert.Multiple(fun () ->
                Assert.That(rebuilt.OverviewRollupState().CompleteThrough, Is.EqualTo None)
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_rows;", Is.Zero)
                Assert.That(
                    rebuilt.QueryWindow(at, at) |> List.map (_.EventId >> EventId.value),
                    Is.EqualTo [ "raw" ]
                )))

    [<Test>]
    member _.``state reader never exposes an unsupported schema or resolution``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            replaceStateTable path 42 60

            Assert.Throws<InvalidDataException>(fun () -> store.OverviewRollupState() |> ignore)
            |> ignore)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistorySourceInvalidationTests() =

    [<Test>]
    member _.``successful source writes advance generation, lower dirty time, and maintain bounds``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let eventAt = anchor.AddMinutes(-1.0)
            let livenessAt = eventAt.AddSeconds 1.0
            let taskAt = eventAt.AddMinutes(-1.0).AddTicks 1L
            let sessionId = SessionId "s1"
            let event = evt "event-1" "s1" "worktree-a" SessionLevelStatus.Working None eventAt

            Assert.That(store.AppendAndUpsert(event, stored sessionId eventAt), Is.True)
            store.RecordLiveness(sessionId, livenessAt)
            Assert.That(
                store.AppendTaskSnapshotIfChanged(taskAt, [ { Kind = TaskBucketKind.Planned; Count = 1 } ]),
                Is.True
            )

            let expectedDirty = firstBoundaryAtOrAfter taskAt
            let stateAfterWrites = store.OverviewRollupState()

            Assert.Multiple(fun () ->
                Assert.That(stateAfterWrites.SourceGeneration, Is.EqualTo 3L)
                Assert.That(stateAfterWrites.EarliestDirty, Is.EqualTo(Some expectedDirty))
                Assert.That(
                    observationBounds path sessionId,
                    Is.EqualTo(Some(eventAt, livenessAt))
                ))

            Assert.That(store.AppendAndUpsert(event, stored sessionId eventAt), Is.False)
            store.RecordLiveness(sessionId, livenessAt)
            store.RecordLiveness(sessionId, eventAt)
            Assert.That(
                store.AppendTaskSnapshotIfChanged(anchor, [ { Kind = TaskBucketKind.Planned; Count = 1 } ]),
                Is.False
            )

            let stateAfterNoOps = store.OverviewRollupState()

            Assert.Multiple(fun () ->
                Assert.That(stateAfterNoOps, Is.EqualTo stateAfterWrites)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 1)
                Assert.That(scalarInt path "SELECT count(*) FROM session_liveness;", Is.EqualTo 1)
                Assert.That(scalarInt path "SELECT count(*) FROM task_snapshots;", Is.EqualTo 1)
                Assert.That(
                    observationBounds path sessionId,
                    Is.EqualTo(Some(eventAt, livenessAt))
                )))

    [<Test>]
    member _.``event source and invalidation roll back together``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let at = latestCompleteBoundary DateTimeOffset.UtcNow
            failInvalidation path

            Assert.Throws<SqliteException>(fun () ->
                store.AppendEvent(
                    evt "event-1" "s1" "worktree-a" SessionLevelStatus.Working None at
                )
                |> ignore)
            |> ignore

            let state = store.OverviewRollupState()

            Assert.Multiple(fun () ->
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.Zero)
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_session_bounds;", Is.Zero)
                Assert.That(state.SourceGeneration, Is.Zero)
                Assert.That(state.EarliestDirty, Is.EqualTo None)))

    [<Test>]
    member _.``liveness source, live status, and invalidation roll back together``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let before = latestCompleteBoundary DateTimeOffset.UtcNow
            let after = before.AddSeconds 1.0
            let sessionId = SessionId "s1"
            store.UpsertStatus(stored sessionId before)
            failInvalidation path

            Assert.Throws<SqliteException>(fun () -> store.RecordLiveness(sessionId, after))
            |> ignore

            let state = store.OverviewRollupState()

            Assert.Multiple(fun () ->
                Assert.That(store.StatusBySession(sessionId).Value.LastSeen, Is.EqualTo before)
                Assert.That(scalarInt path "SELECT count(*) FROM session_liveness;", Is.Zero)
                Assert.That(scalarInt path "SELECT count(*) FROM overview_history_session_bounds;", Is.Zero)
                Assert.That(state.SourceGeneration, Is.Zero)
                Assert.That(state.EarliestDirty, Is.EqualTo None)))

    [<Test>]
    member _.``task source and invalidation roll back together``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let at = latestCompleteBoundary DateTimeOffset.UtcNow
            failInvalidation path

            Assert.Throws<SqliteException>(fun () ->
                store.AppendTaskSnapshotIfChanged(
                    at,
                    [ { Kind = TaskBucketKind.Planned; Count = 1 } ]
                )
                |> ignore)
            |> ignore

            let state = store.OverviewRollupState()

            Assert.Multiple(fun () ->
                Assert.That(store.QueryLatestTaskSnapshot(), Is.EqualTo None)
                Assert.That(state.SourceGeneration, Is.Zero)
                Assert.That(state.EarliestDirty, Is.EqualTo None)))
