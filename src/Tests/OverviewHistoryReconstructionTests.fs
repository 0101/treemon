module Tests.OverviewHistoryReconstructionTests

open System
open Microsoft.Data.Sqlite
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewHistoryReconstruction
open Server.OverviewHistoryRollup
open Server.SessionActivity
open Server.SessionActivityStore
open Server.SqliteStorage
open Shared
open Tests.OverviewTestHelpers
open Tests.SqliteTestDatabase

let private withStore =
    SqliteTestDatabase.withStore "treemon-reconstruction"

let private setObservationBounds path observations =
    let bounds =
        observations
        |> List.groupBy fst
        |> List.map (fun (sessionId, rows) ->
            let timestamps = rows |> List.map snd
            sessionId, List.min timestamps, List.max timestamps)

    use connection = openConnection path
    use tx = connection.BeginTransaction()

    bounds
    |> List.iter (fun (sessionId, firstObserved, lastObserved) ->
        use command = connection.CreateCommand()
        command.Transaction <- tx
        command.CommandText <-
            """
INSERT OR REPLACE INTO overview_history_session_bounds
    (session_id, first_observed_at, last_observed_at)
VALUES ($session, $first, $last);
"""
        command.Parameters.AddWithValue("$session", SessionId.value sessionId) |> ignore
        command.Parameters.AddWithValue("$first", isoUtc firstObserved) |> ignore
        command.Parameters.AddWithValue("$last", isoUtc lastObserved) |> ignore
        command.ExecuteNonQuery() |> ignore)

    tx.Commit()

let private appendLivenessAndBound path sessionId observedAt =
    use connection = openConnection path
    use tx = connection.BeginTransaction()

    use liveness = connection.CreateCommand()
    liveness.Transaction <- tx
    liveness.CommandText <-
        "INSERT INTO session_liveness (session_id, ts) VALUES ($session, $observed);"
    liveness.Parameters.AddWithValue("$session", SessionId.value sessionId) |> ignore
    liveness.Parameters.AddWithValue("$observed", isoUtc observedAt) |> ignore
    liveness.ExecuteNonQuery() |> ignore

    use bounds = connection.CreateCommand()
    bounds.Transaction <- tx
    bounds.CommandText <-
        """
UPDATE overview_history_session_bounds
SET first_observed_at = min(first_observed_at, $observed),
    last_observed_at = max(last_observed_at, $observed)
WHERE session_id = $session;
"""
    bounds.Parameters.AddWithValue("$session", SessionId.value sessionId) |> ignore
    bounds.Parameters.AddWithValue("$observed", isoUtc observedAt) |> ignore
    bounds.ExecuteNonQuery() |> ignore
    tx.Commit()

let private plan path sql bind =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- "EXPLAIN QUERY PLAN " + sql
    bind command
    use reader = command.ExecuteReader()
    readRows reader (fun row -> row.GetString 3) []

let private tc kind count : TaskCount = { Kind = kind; Count = count }

let private valueAt boundary (snapshots: OverviewSnapshot list) =
    snapshots
    |> List.takeWhile (fun snapshot -> snapshot.Timestamp <= boundary)
    |> List.last

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryReconstructionTests() =

    [<Test>]
    member _.``indexed reconstruction matches the sampler at every requested boundary``() =
        withStore (fun path store ->
            let start = DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero)
            let ending = start.AddMinutes 144.0
            let s1 = SessionId "s1"
            let unknown = SessionId "unknown"
            let tasks =
                [ start.AddHours(-1.0), [ tc TaskBucketKind.Planned 1 ]
                  start, [ tc TaskBucketKind.Planned 2 ]
                  start.AddMinutes 1.0, [ tc TaskBucketKind.Blocked 1 ] ]
            let events =
                [ evt "s1-working" "s1" "worktree-a" SessionLevelStatus.Working (Some "bd-execute") (start.AddMinutes(-1.0))
                  evt "s2-idle" "s2" "worktree-a" SessionLevelStatus.Idle None (start.AddMinutes(-1.0))
                  evt "s1-waiting" "s1" "worktree-a" SessionLevelStatus.WaitingForUser None (start.AddMinutes 2.0)
                  evt "s3-newer" "s3" "worktree-b" SessionLevelStatus.Working (Some "pr") (start.AddMinutes 5.0)
                  evt "s3-older" "s3" "worktree-b" SessionLevelStatus.Idle None (start.AddMinutes 4.0)
                  evt "old" "old" "worktree-old" SessionLevelStatus.Working None (start.AddDays(-1.0)) ]
            let liveness =
                [ s1, start.AddMinutes 2.5
                  unknown, start.AddMinutes 1.0
                  s1, start.AddMinutes 7.0 ]
            let observations =
                (events |> List.map (fun row -> row.SessionId, row.Ts)) @ liveness

            tasks |> List.iter store.AppendTaskSnapshot
            events |> List.iter (store.AppendEvent >> ignore)
            liveness
            |> List.iter (fun (sessionId, observedAt) ->
                appendLivenessAndBound path sessionId observedAt)
            setObservationBounds path observations

            let actual = reconstructRange store start ending
            let oracle = OverviewHistory.sample ending (ending - start) tasks events liveness

            let expected =
                boundaries start ending
                |> Seq.map (fun boundary ->
                    let snapshot = valueAt boundary oracle
                    { Boundary = boundary
                      Tasks = snapshot.Tasks
                      Agents = snapshot.Agents })
                |> Seq.toList

            Assert.Multiple(fun () ->
                Assert.That(actual.Length, Is.EqualTo 289)
                Assert.That(actual, Is.EqualTo expected)))

    [<Test>]
    member _.``one batch reads task status and liveness from one stable snapshot``() =
        withStore (fun path store ->
            let start = DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero)
            let ending = start.AddMinutes 3.0
            let sessionId = SessionId "s1"
            let row =
                evt "working" "s1" "worktree-a" SessionLevelStatus.Working (Some "bd-execute") (start.AddMinutes(-1.0))

            store.AppendEvent row |> ignore
            setObservationBounds path [ sessionId, row.Ts ]

            let during =
                reconstructRangeWith
                    (fun () -> appendLivenessAndBound path sessionId ending)
                    store
                    start
                    ending

            let after = reconstructRange store start ending

            Assert.Multiple(fun () ->
                Assert.That(during |> List.last |> _.Agents, Is.Empty)
                Assert.That(after |> List.last |> _.Agents, Is.Not.Empty)))

    [<Test>]
    member _.``reconstruction enforces canonical batches of at most 512 boundaries``() =
        withStore (fun _ store ->
            let start = DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero)
            let lastAllowed = start.AddSeconds(float (maxBatchBoundaryCount - 1) * 30.0)
            let tooMany = lastAllowed.AddSeconds 30.0

            Assert.Multiple(fun () ->
                Assert.That(reconstructRange store start lastAllowed |> List.length, Is.EqualTo maxBatchBoundaryCount)
                Assert.Throws<ArgumentException>(fun () -> reconstructRange store start tooMany |> ignore)
                |> ignore
                Assert.Throws<ArgumentException>(fun () -> reconstructRange store (start.AddSeconds 1.0) start |> ignore)
                |> ignore))

    [<Test>]
    member _.``raw reconstruction statements seek indexed predecessors and ranges``() =
        withStore (fun path _ ->
            let start = DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero)
            let ending = start.AddMinutes 1.0

            let bindStart (command: SqliteCommand) =
                command.Parameters.AddWithValue("$start", isoUtc start) |> ignore

            let bindRange (command: SqliteCommand) =
                bindStart command
                command.Parameters.AddWithValue("$end", isoUtc ending) |> ignore

            let bindRelevantRange (command: SqliteCommand) =
                bindRange command
                command.Parameters.AddWithValue("$lookback", isoUtc (start - openWindow)) |> ignore

            let taskPredecessorPlan = plan path taskPredecessorSql bindStart
            let taskChangesPlan = plan path taskChangesSql bindRange
            let eventPlan = plan path eventRowsSql bindRelevantRange
            let livenessPlan = plan path livenessRowsSql bindRelevantRange
            let has (text: string) (rows: string list) = rows |> List.exists _.Contains(text)

            Assert.Multiple(fun () ->
                Assert.That(has "ix_task_snapshots_ts" taskPredecessorPlan, Is.True)
                Assert.That(has "ix_task_snapshots_ts" taskChangesPlan, Is.True)
                Assert.That(has "ix_overview_bounds_last_first" eventPlan, Is.True)
                Assert.That(has "ix_events_session_ts" eventPlan, Is.True)
                Assert.That(has "ix_overview_bounds_last_first" livenessPlan, Is.True)
                Assert.That(has "session_id=? AND ts>? AND ts<?" livenessPlan, Is.True)
                Assert.That(has "SCAN task_snapshots" (taskPredecessorPlan @ taskChangesPlan), Is.False)
                Assert.That(has "SCAN activity_events" eventPlan, Is.False)
                Assert.That(has "SCAN session_liveness" livenessPlan, Is.False)))
