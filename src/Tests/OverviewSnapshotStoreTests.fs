module Tests.OverviewSnapshotStoreTests

open System
open Microsoft.Data.Sqlite
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewSnapshotStore
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.SqliteTestDatabase

let private anchor = DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)

let private task count : TaskCount =
    { Kind = TaskBucketKind.Planned
      Count = count }

let private agent count : AgentCount =
    { Kind = AgentGroupKind.Activity CurrentActivity.Executing
      Count = count }

let private snapshot timestamp taskCount agentCount : OverviewSnapshot =
    { Timestamp = timestamp
      Tasks = if taskCount = 0 then [] else [ task taskCount ]
      Agents = if agentCount = 0 then [] else [ agent agentCount ] }

let private buckets path =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT bucket FROM overview_snapshots ORDER BY bucket;"
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then read (reader.GetInt64 0 :: acc)
        else List.rev acc

    read []

let private schemaNames path objectType names =
    use connection = openConnection path
    use command = connection.CreateCommand()
    let parameters =
        names
        |> List.mapi (fun index name ->
            let parameterName = $"$name{index}"
            command.Parameters.AddWithValue(parameterName, name) |> ignore
            parameterName)

    command.CommandText <-
        $"""
SELECT name
FROM sqlite_master
WHERE type = $type AND name IN ({String.concat ", " parameters})
ORDER BY name;
"""
    command.Parameters.AddWithValue("$type", objectType) |> ignore
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then read (reader.GetString 0 :: acc)
        else List.rev acc

    read []

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewSnapshotStoreTests() =

    [<Test>]
    member _.``empty store has no latest anchor and returns an empty window``() =
        withDbPath "treemon-overview-snapshot-empty" (fun path ->
            let store = OverviewSnapshotStore path
            Assert.That(store.LatestAnchor(), Is.EqualTo None)
            Assert.That(store.ReadWindow(anchor, HistoryWindow.Hours12), Is.Empty))

    [<Test>]
    member _.``insert and retention roll back together when pruning fails``() =
        withDbPath "treemon-overview-snapshot-atomic" (fun path ->
            let store = OverviewSnapshotStore path
            let oldTimestamp = anchor.AddHours(-72.0).AddSeconds(-30.0)
            store.Insert(snapshot oldTimestamp 1 1) |> ignore

            execute
                path
                """
CREATE TRIGGER fail_overview_snapshot_prune
BEFORE DELETE ON overview_snapshots
BEGIN
    SELECT RAISE(ABORT, 'forced overview snapshot prune failure');
END;
"""

            Assert.Throws<SqliteException>(fun () -> store.Insert(snapshot anchor 2 2) |> ignore)
            |> ignore

            Assert.That(
                buckets path,
                Is.EqualTo [ oldTimestamp.ToUnixTimeSeconds() ],
                "the failed prune must also roll back the new snapshot"
            ))

    [<Test>]
    member _.``duplicate bucket keeps the first committed snapshot``() =
        withDbPath "treemon-overview-snapshot-duplicate" (fun path ->
            let store = OverviewSnapshotStore path
            let original = snapshot anchor 2 1
            let replacement = snapshot anchor 99 88

            Assert.That(store.Insert original, Is.True)
            Assert.That(store.Insert replacement, Is.False)
            Assert.That(store.ReadWindow(anchor, HistoryWindow.Hours12), Is.EqualTo [ original ]))

    [<Test>]
    member _.``retention removes only rows strictly before the 72 hour cutoff``() =
        withDbPath "treemon-overview-snapshot-retention" (fun path ->
            let store = OverviewSnapshotStore path
            let cutoff = anchor.AddHours(-72.0)
            let expired = cutoff.AddSeconds(-30.0)

            [ snapshot expired 1 0; snapshot cutoff 2 0; snapshot anchor 3 0 ]
            |> List.iter (store.Insert >> ignore)

            Assert.That(
                buckets path,
                Is.EqualTo [ cutoff.ToUnixTimeSeconds(); anchor.ToUnixTimeSeconds() ]
            ))

    [<Test>]
    member _.``snapshots survive reopening the store``() =
        withDbPath "treemon-overview-snapshot-restart" (fun path ->
            let expected = snapshot anchor 4 2
            let writer = OverviewSnapshotStore path
            writer.Insert expected |> ignore

            let reader = OverviewSnapshotStore path
            Assert.That(reader.LatestAnchor(), Is.EqualTo(Some anchor))
            Assert.That(reader.ReadWindow(anchor, HistoryWindow.Hours24), Is.EqualTo [ expected ]))

    [<Test>]
    member _.``window reads use each anchor-aligned grid and preserve gaps``() =
        [ HistoryWindow.Hours12, 5
          HistoryWindow.Hours24, 10
          HistoryWindow.Hours72, 30 ]
        |> List.iteri (fun index (window, stride) ->
            withDbPath $"treemon-overview-snapshot-grid-{index}" (fun path ->
                let store = OverviewSnapshotStore path
                let totalBuckets =
                    int (HistoryWindow.duration window).TotalSeconds / 30
                let atDifference bucketCount =
                    anchor.AddSeconds(float (-30 * bucketCount))
                let expectedTimestamps =
                    [ totalBuckets; 2 * stride; stride; 0 ]
                    |> List.map atDifference

                [ anchor.AddSeconds 30.0
                  atDifference (totalBuckets + stride)
                  atDifference (totalBuckets - 1)
                  atDifference (stride + 1) ]
                @ expectedTimestamps
                |> List.distinct
                |> List.map (fun timestamp -> snapshot timestamp (timestamp.Second + 1) 0)
                |> List.iter (store.Insert >> ignore)

                Assert.That(
                    store.ReadWindow(anchor, window) |> List.map _.Timestamp,
                    Is.EqualTo expectedTimestamps
                )))

    [<Test>]
    member _.``a fully populated window returns exactly 289 oldest-first rows``() =
        withDbPath "treemon-overview-snapshot-bound" (fun path ->
            let store = OverviewSnapshotStore path
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours12
            let expected =
                [ 0..288 ]
                |> List.map (fun index ->
                    snapshot (start.AddSeconds(float (index * 5 * 30))) (index + 1) 0)

            expected |> List.iter (store.Insert >> ignore)

            Assert.That(
                store.ReadWindow(anchor, HistoryWindow.Hours12),
                Is.EqualTo expected
            ))

    [<Test>]
    member _.``task and agent count unions round trip through SQLite JSON``() =
        withDbPath "treemon-overview-snapshot-serialization" (fun path ->
            let expected =
                { Timestamp = anchor
                  Tasks =
                    [ { Kind = TaskBucketKind.Planned; Count = 1 }
                      { Kind = TaskBucketKind.Queued; Count = 2 }
                      { Kind = TaskBucketKind.InProgress; Count = 3 }
                      { Kind = TaskBucketKind.Blocked; Count = 4 }
                      { Kind = TaskBucketKind.Done; Count = 5 }
                      { Kind = TaskBucketKind.Unattended; Count = 6 } ]
                  Agents =
                    [ { Kind = AgentGroupKind.Activity CurrentActivity.Investigating; Count = 1 }
                      { Kind = AgentGroupKind.Activity CurrentActivity.Planning; Count = 2 }
                      { Kind = AgentGroupKind.Activity CurrentActivity.Executing; Count = 3 }
                      { Kind = AgentGroupKind.Activity CurrentActivity.Reviewing; Count = 4 }
                      { Kind = AgentGroupKind.Activity CurrentActivity.PR; Count = 5 }
                      { Kind = AgentGroupKind.Activity CurrentActivity.Working; Count = 6 }
                      { Kind = AgentGroupKind.Waiting; Count = 7 }
                      { Kind = AgentGroupKind.Idle; Count = 8 } ] }
            let store = OverviewSnapshotStore path
            store.Insert expected |> ignore

            Assert.That(store.ReadWindow(anchor, HistoryWindow.Hours72), Is.EqualTo [ expected ]))

    [<Test>]
    member _.``migration repeatedly drops only legacy overview tables and keeps direct snapshots``() =
        withDbPath "treemon-overview-snapshot-migration" (fun path ->
            (use legacy = new SessionActivityStore(path)
             let stored =
                 { SessionId = SessionId "session-1"
                   WorktreePath = WorktreePath "worktree-1"
                   Provider = CopilotCli
                   Status = { emptyStatus with Status = SessionLevelStatus.Working }
                   UpdatedAt = anchor
                   LastSeen = anchor
                   ContextUsageAt = None }

             legacy.UpsertStatus stored
             legacy.AppendEvent(
                 { EventId = EventId "event-1"
                   SessionId = stored.SessionId
                   WorktreePath = stored.WorktreePath
                   Provider = stored.Provider
                   Kind = "turn_start"
                   Status = SessionLevelStatus.Working
                   Skill = None
                   Ts = anchor }
             )
             |> ignore
             legacy.RecordLiveness(stored.SessionId, anchor)
             legacy.AppendTaskSnapshot(anchor, [ task 42 ]))

            execute
                path
                $"""
INSERT INTO overview_history_rows(bucket, tasks, agents)
VALUES ({anchor.ToUnixTimeSeconds()}, '[]', '[]');
INSERT INTO overview_history_staging(generation, bucket, tasks, agents)
VALUES (0, {anchor.ToUnixTimeSeconds()}, '[]', '[]');
INSERT OR REPLACE INTO overview_history_session_bounds(session_id, first_observed_at, last_observed_at)
VALUES ('session-1', '{anchor:O}', '{anchor:O}');
"""

            let first = OverviewSnapshotStore path
            Assert.That(
                first.LatestAnchor(),
                Is.EqualTo None,
                "legacy reconstructed rows must not migrate"
            )
            Assert.That(first.Insert(snapshot anchor 7 3), Is.True)

            let second = OverviewSnapshotStore path
            Assert.That(second.ReadWindow(anchor, HistoryWindow.Hours12), Is.EqualTo [ snapshot anchor 7 3 ])

            let legacyTables =
                [ "session_liveness"
                  "task_snapshots"
                  "overview_history_rows"
                  "overview_history_state"
                  "overview_history_staging"
                  "overview_history_session_bounds" ]

            Assert.Multiple(fun () ->
                Assert.That(schemaNames path "table" legacyTables, Is.Empty)
                Assert.That(
                    schemaNames path "table" [ "session_status"; "activity_events"; "overview_snapshots" ],
                    Is.EqualTo [ "activity_events"; "overview_snapshots"; "session_status" ]
                )
                Assert.That(scalarInt path "SELECT count(*) FROM session_status;", Is.EqualTo 1)
                Assert.That(scalarInt path "SELECT count(*) FROM activity_events;", Is.EqualTo 1)
                Assert.That(
                    schemaNames
                        path
                        "index"
                        [ "ix_status_worktree"; "ix_events_ts"; "ix_events_session_ts" ],
                    Is.EqualTo [ "ix_events_session_ts"; "ix_events_ts"; "ix_status_worktree" ]
                )))
