module Tests.OverviewHistoryRollupWorkerTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open NUnit.Framework
open OverviewData
open Server.OverviewHistoryRollup
open Server.OverviewHistoryRollupWorker
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.OverviewTestHelpers

let private withDbPath action =
    let directory = Path.Combine(Path.GetTempPath(), $"treemon-rollup-worker-{Guid.NewGuid()}")
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

let private publishedBounds path =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        "SELECT count(*), min(bucket), max(bucket) FROM overview_history_rows;"
    use reader = command.ExecuteReader()
    reader.Read() |> ignore

    reader.GetInt32 0,
    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64 1),
    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64 2)

let private taskCount count : TaskCount list =
    [ { Kind = TaskBucketKind.Planned
        Count = count } ]

let private fixedClock now : RollupWorkerClock =
    { UtcNow = fun () -> now
      WaitUntil = fun _ _ -> async.Return() }

let private noHooks : RollupWorkerHooks =
    { BeforeStage = ignore
      BeforePublish = ignore }

let private rangeLength candidate =
    int ((candidate.EndBoundary - candidate.StartBoundary).Ticks / resolution.Ticks) + 1

type private ClockWait =
    { Target: DateTimeOffset
      Release: TaskCompletionSource<unit> }

type private ControllableClock(initial: DateTimeOffset) =
    let gate = obj ()
    let requests = Channel.CreateUnbounded<ClockWait>()
    // Test time is an intentionally mutable clock boundary controlled only by AdvanceTo.
    let mutable now = initial

    member _.Clock: RollupWorkerClock =
        { UtcNow = fun () -> lock gate (fun () -> now)
          WaitUntil =
            fun target cancellationToken -> async {
                let release =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                do!
                    requests.Writer.WriteAsync(
                        { Target = target
                          Release = release },
                        cancellationToken
                    ).AsTask()
                    |> Async.AwaitTask

                do! release.Task.WaitAsync(cancellationToken) |> Async.AwaitTask
            } }

    member _.AdvanceTo(timestamp) =
        lock gate (fun () -> now <- timestamp)

    member _.NextWait() =
        requests.Reader.ReadAsync().AsTask()

let private createWorker store clock hooks =
    new OverviewHistoryRollupWorker(store, clock, hooks, ignore)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryRollupWorkerTests() =

    [<Test>]
    member _.``startup backfills the retained horizon in bounded batches``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let staged = ConcurrentQueue<RollupCandidate>()

            let hooks =
                { noHooks with
                    BeforeStage = staged.Enqueue }

            use worker = createWorker store (fixedClock anchor) hooks
            let state = worker.Backfill CancellationToken.None
            let _, rows =
                store.ReadPublishedOverviewRollup(oldestRetainedBoundary anchor, anchor)

            let batchSizes =
                staged.ToArray() |> Array.map rangeLength

            Assert.Multiple(fun () ->
                Assert.That(state.CompleteThrough, Is.EqualTo(Some anchor))
                Assert.That(state.EarliestDirty, Is.EqualTo None)
                Assert.That(rows.Length, Is.EqualTo(72 * 60 * 2 + 2))
                Assert.That(rows.Head.Boundary, Is.EqualTo(oldestRetainedBoundary anchor))
                Assert.That(rows |> List.last |> _.Boundary, Is.EqualTo anchor)
                Assert.That(batchSizes.Length, Is.EqualTo 17)
                Assert.That(batchSizes |> Array.max, Is.EqualTo 512)
                Assert.That(batchSizes |> Array.sum, Is.EqualTo rows.Length)))

    [<Test>]
    member _.``running worker wakes on the next boundary and retains a bounded row set``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let nextAnchor = anchor + resolution
            let caughtUpAnchor = nextAnchor + resolution
            let clock = ControllableClock(anchor)

            let hooks =
                { noHooks with
                    BeforePublish =
                        fun candidate ->
                            if candidate.EndBoundary = nextAnchor then
                                clock.AdvanceTo caughtUpAnchor }

            use worker = createWorker store clock.Clock hooks
            worker.Backfill CancellationToken.None |> ignore
            use cancellation = new CancellationTokenSource()

            let running =
                worker.Run cancellation.Token |> Async.StartAsTask

            let timeout = TimeSpan.FromSeconds 10.0
            let firstWait =
                clock.NextWait().WaitAsync(timeout).GetAwaiter().GetResult()

            Assert.That(firstWait.Target, Is.EqualTo nextAnchor)
            clock.AdvanceTo nextAnchor
            firstWait.Release.SetResult()

            let secondWait =
                clock.NextWait().WaitAsync(timeout).GetAwaiter().GetResult()
            let state = store.OverviewRollupState()
            let count, oldest, newest = publishedBounds path

            cancellation.Cancel()
            running.WaitAsync(timeout).GetAwaiter().GetResult()

            Assert.Multiple(fun () ->
                Assert.That(secondWait.Target, Is.EqualTo(caughtUpAnchor + resolution))
                Assert.That(state.CompleteThrough, Is.EqualTo(Some caughtUpAnchor))
                Assert.That(count, Is.EqualTo(72 * 60 * 2 + 2))
                Assert.That(oldest, Is.EqualTo(oldestRetainedBoundary caughtUpAnchor))
                Assert.That(newest, Is.EqualTo caughtUpAnchor)))

    [<Test>]
    member _.``late writes stage the full repair before replacing the published range``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let dirty = anchor.AddMinutes(-1.0)
            let observations =
                ConcurrentQueue<PublicationState * RollupRow list * int>()

            let hooks =
                { noHooks with
                    BeforePublish =
                        fun candidate ->
                            if candidate.Generation > 0L then
                                let state, rows =
                                    store.ReadPublishedOverviewRollup(dirty, anchor)

                                observations.Enqueue(
                                    state,
                                    rows,
                                    scalarInt path
                                        "SELECT count(*) FROM overview_history_staging;"
                                ) }

            use worker = createWorker store (fixedClock anchor) hooks
            worker.Backfill CancellationToken.None |> ignore
            let oldState, oldRows =
                store.ReadPublishedOverviewRollup(dirty, anchor)

            store.AppendTaskSnapshot(dirty, taskCount 2)
            let repaired = worker.Backfill CancellationToken.None
            let _, repairedRows =
                store.ReadPublishedOverviewRollup(dirty, anchor)

            let observedState, visibleRows, stagedCount =
                observations.ToArray() |> Array.exactlyOne

            let expectedCandidate =
                { Generation = repaired.SourceGeneration
                  StartBoundary = dirty
                  EndBoundary = anchor }

            Assert.Multiple(fun () ->
                Assert.That(observedState.PublishedGeneration, Is.EqualTo oldState.PublishedGeneration)
                Assert.That(visibleRows, Is.EqualTo oldRows)
                Assert.That(stagedCount, Is.EqualTo(rangeLength expectedCandidate))
                Assert.That(repaired.PublishedGeneration, Is.EqualTo repaired.SourceGeneration)
                Assert.That(repaired.EarliestDirty, Is.EqualTo None)
                Assert.That(repairedRows |> List.forall (fun row -> row.Tasks = taskCount 2), Is.True)))

    [<Test>]
    member _.``source generation conflicts discard staging and retry``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            store.AppendTaskSnapshot(anchor, taskCount 1)
            // The test hook injects exactly one concurrent source write across retry callbacks.
            let mutable injected = 0

            let hooks =
                { noHooks with
                    BeforeStage =
                        fun _ ->
                            if Interlocked.Exchange(&injected, 1) = 0 then
                                store.AppendTaskSnapshot(anchor, taskCount 2) }

            use worker = createWorker store (fixedClock anchor) hooks
            let state = worker.Backfill CancellationToken.None
            let _, rows =
                store.ReadPublishedOverviewRollup(anchor, anchor)

            Assert.Multiple(fun () ->
                Assert.That(state.SourceGeneration, Is.EqualTo 2L)
                Assert.That(state.PublishedGeneration, Is.EqualTo 2L)
                Assert.That(rows |> List.exactlyOne |> _.Tasks, Is.EqualTo(taskCount 2))
                Assert.That(
                    scalarInt path "SELECT count(*) FROM overview_history_staging;",
                    Is.Zero
                )))

    [<Test>]
    member _.``restart discards orphaned staging and continues from durable metadata``() =
        withDbPath (fun path ->
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let nextAnchor = anchor + resolution

            (use store = new SessionActivityStore(path)
             use worker = createWorker store (fixedClock anchor) noHooks
             worker.Backfill CancellationToken.None |> ignore
             let generation = store.OverviewRollupState().SourceGeneration
             let candidate =
                 { Generation = generation
                   StartBoundary = nextAnchor
                   EndBoundary = nextAnchor }

             store.StageOverviewRollup(
                 candidate,
                 [ { Generation = generation
                     Row =
                       { Boundary = nextAnchor
                         Tasks = []
                         Agents = [] } } ]
             )
             |> ignore

             store.AppendTaskSnapshot(nextAnchor, taskCount 3))

            Assert.That(
                scalarInt path "SELECT count(*) FROM overview_history_staging;",
                Is.EqualTo 1
            )

            use reopened = new SessionActivityStore(path)
            use restarted = createWorker reopened (fixedClock nextAnchor) noHooks
            let state = restarted.Backfill CancellationToken.None
            let _, rows =
                reopened.ReadPublishedOverviewRollup(nextAnchor, nextAnchor)

            Assert.Multiple(fun () ->
                Assert.That(state.CompleteThrough, Is.EqualTo(Some nextAnchor))
                Assert.That(state.PublishedGeneration, Is.EqualTo state.SourceGeneration)
                Assert.That(rows |> List.exactlyOne |> _.Tasks, Is.EqualTo(taskCount 3))
                Assert.That(
                    scalarInt path "SELECT count(*) FROM overview_history_staging;",
                    Is.Zero
                )))

    [<Test>]
    member _.``stale publication rebases only the current retained horizon``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let rebasedAnchor = anchor.AddHours 73.0
            let clock = ControllableClock(anchor)
            let visibleBeforeReplacement =
                ConcurrentQueue<int * DateTimeOffset * DateTimeOffset>()

            let hooks =
                { noHooks with
                    BeforePublish =
                        fun candidate ->
                            if candidate.EndBoundary = rebasedAnchor then
                                visibleBeforeReplacement.Enqueue(publishedBounds path) }

            use worker = createWorker store clock.Clock hooks
            worker.Backfill CancellationToken.None |> ignore
            let oldBounds = publishedBounds path
            clock.AdvanceTo rebasedAnchor

            let state = worker.Backfill CancellationToken.None
            let count, oldest, newest = publishedBounds path

            Assert.Multiple(fun () ->
                Assert.That(
                    visibleBeforeReplacement.ToArray() |> Array.exactlyOne,
                    Is.EqualTo oldBounds
                )
                Assert.That(state.CompleteThrough, Is.EqualTo(Some rebasedAnchor))
                Assert.That(count, Is.EqualTo(72 * 60 * 2 + 2))
                Assert.That(oldest, Is.EqualTo(oldestRetainedBoundary rebasedAnchor))
                Assert.That(newest, Is.EqualTo rebasedAnchor)))

    [<Test>]
    member _.``derived schema reset rebuilds observation bounds and publication``() =
        withDbPath (fun path ->
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let eventAt = anchor.AddMinutes(-1.0)

            (use store = new SessionActivityStore(path)
             store.AppendEvent(
                 evt "working" "s1" "worktree-a" SessionLevelStatus.Working None eventAt
             )
             |> ignore)

            execute
                path
                """
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
VALUES (1, 999, 30, 0, 0);
"""

            use rebuilt = new SessionActivityStore(path)
            use worker = createWorker rebuilt (fixedClock anchor) noHooks
            let state = worker.Backfill CancellationToken.None
            let _, rows =
                rebuilt.ReadPublishedOverviewRollup(anchor, anchor)

            Assert.Multiple(fun () ->
                Assert.That(state.CompleteThrough, Is.EqualTo(Some anchor))
                Assert.That(rows |> List.exactlyOne |> _.Agents, Is.Not.Empty)
                Assert.That(
                    scalarInt path "SELECT count(*) FROM overview_history_session_bounds;",
                    Is.EqualTo 1
                )))

    [<Test>]
    member _.``publication failure preserves the prior coherent generation for retry``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let dirty = anchor.AddMinutes(-1.0)
            use worker = createWorker store (fixedClock anchor) noHooks
            worker.Backfill CancellationToken.None |> ignore
            store.AppendTaskSnapshot(dirty, taskCount 4)
            let beforeState, beforeRows =
                store.ReadPublishedOverviewRollup(dirty, anchor)
            let beforeBounds = publishedBounds path

            execute
                path
                """
CREATE TRIGGER fail_worker_publication
BEFORE INSERT ON overview_history_rows
BEGIN
    SELECT RAISE(ABORT, 'forced worker publication failure');
END;
"""

            Assert.Throws<SqliteException>(fun () ->
                worker.Backfill CancellationToken.None |> ignore)
            |> ignore

            let failedState, failedRows =
                store.ReadPublishedOverviewRollup(dirty, anchor)

            Assert.Multiple(fun () ->
                Assert.That(failedState.CompleteThrough, Is.EqualTo beforeState.CompleteThrough)
                Assert.That(failedState.PublishedGeneration, Is.EqualTo beforeState.PublishedGeneration)
                Assert.That(failedRows, Is.EqualTo beforeRows)
                Assert.That(publishedBounds path, Is.EqualTo beforeBounds)
                Assert.That(
                    scalarInt path "SELECT count(*) FROM overview_history_staging;",
                    Is.GreaterThan 0
                ))

            execute path "DROP TRIGGER fail_worker_publication;"
            let repaired = worker.Backfill CancellationToken.None
            let _, repairedRows =
                store.ReadPublishedOverviewRollup(dirty, anchor)

            Assert.Multiple(fun () ->
                Assert.That(repaired.EarliestDirty, Is.EqualTo None)
                Assert.That(repairedRows |> List.forall (fun row -> row.Tasks = taskCount 4), Is.True)))

    [<Test>]
    member _.``only one rollup worker can claim a store``() =
        withDbPath (fun path ->
            use store = new SessionActivityStore(path)
            use worker = new OverviewHistoryRollupWorker(store)

            Assert.Throws<InvalidOperationException>(fun () ->
                new OverviewHistoryRollupWorker(store) |> ignore)
            |> ignore)
