module Tests.OverviewHistoryPublicationIntegrationTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewHistoryReconstruction
open Server.OverviewHistoryRollup
open Server.OverviewHistoryRollupWorker
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.OverviewTestHelpers
open Tests.SqliteTestDatabase

let private withDbPath =
    SqliteTestDatabase.withDbPath "treemon-rollup-publication"

type private Sources =
    { Tasks: (DateTimeOffset * TaskCount list) list
      Events: ActivityEventRow list
      Liveness: (SessionId * DateTimeOffset) list }

type private TestClock(initial: DateTimeOffset) =
    let gate = obj ()
    // Test-controlled time is mutable only at this clock boundary.
    let mutable now = initial

    member _.Clock: RollupWorkerClock =
        { UtcNow = fun () -> lock gate (fun () -> now)
          WaitUntil = fun _ _ -> async.Return() }

    member _.AdvanceTo(timestamp: DateTimeOffset) =
        lock gate (fun () -> now <- timestamp)

let private tc count : TaskCount list =
    [ { Kind = TaskBucketKind.Planned
        Count = count } ]

let private emptySources =
    { Tasks = []
      Events = []
      Liveness = [] }

let private noHooks : RollupWorkerHooks =
    { BeforeStage = ignore
      BeforePublish = ignore }

let private fixedClock now : RollupWorkerClock =
    { UtcNow = fun () -> now
      WaitUntil = fun _ _ -> async.Return() }

let private createWorker store clock hooks =
    new OverviewHistoryRollupWorker(store, clock, hooks, ignore)

let private rangeLength candidate =
    int (
        (candidate.EndBoundary - candidate.StartBoundary).Ticks
        / resolution.Ticks
    )
    + 1

let private snapshotAt boundary (snapshots: OverviewSnapshot list) =
    snapshots
    |> List.takeWhile (fun snapshot -> snapshot.Timestamp <= boundary)
    |> List.last

let private denseOracle
    startBoundary
    endBoundary
    (sources: Sources)
    =
    let oracleWindow =
        TimeSpan.FromTicks(
            resolution.Ticks
            * int64 OverviewHistory.sampleBucketCount
        )

    boundaries startBoundary endBoundary
    |> Seq.toList
    |> List.chunkBySize (OverviewHistory.sampleBucketCount + 1)
    |> List.collect (fun chunk ->
        let chunkStart = List.head chunk

        let sampled =
            OverviewHistory.sample
                (chunkStart + oracleWindow)
                oracleWindow
                sources.Tasks
                sources.Events
                sources.Liveness

        chunk
        |> List.map (fun boundary ->
            let snapshot = snapshotAt boundary sampled

            { Boundary = boundary
              Tasks = snapshot.Tasks
              Agents = snapshot.Agents }))

let private persistSources
    (store: SessionActivityStore)
    (sources: Sources)
    =
    sources.Tasks
    |> List.iter (fun (timestamp, tasks) ->
        store.AppendTaskSnapshot(timestamp, tasks))

    sources.Events
    |> List.iter (store.AppendEvent >> ignore)

let private readPublished
    (store: SessionActivityStore)
    startBoundary
    endBoundary
    =
    store.ReadPublishedOverviewRollup(startBoundary, endBoundary)

let private assertPublishedMatchesOracle
    label
    (store: SessionActivityStore)
    startBoundary
    endBoundary
    sources
    =
    let _, actual = readPublished store startBoundary endBoundary
    let expected = denseOracle startBoundary endBoundary sources

    Assert.That(
        actual,
        Is.EqualTo expected,
        $"{label}: published rows differ from OverviewHistory.sample."
    )

let private stageRows
    (store: SessionActivityStore)
    generation
    (rows: RollupRow list)
    =
    rows
    |> List.chunkBySize maxBatchBoundaryCount
    |> List.iter (fun batch ->
        let candidate =
            { Generation = generation
              StartBoundary = batch.Head.Boundary
              EndBoundary = (List.last batch).Boundary }

        let staged =
            batch
            |> List.map (fun row ->
                { Generation = generation
                  Row = row })

        match store.StageOverviewRollup(candidate, staged) with
        | StagingResult.Staged -> ()
        | StagingResult.SourceGenerationChanged current ->
            Assert.Fail(
                $"Staging generation {generation} unexpectedly changed to {current}."
            ))

let private publish
    (store: SessionActivityStore)
    candidate
    =
    match store.PublishOverviewRollup candidate with
    | PublicationResult.Published state -> state
    | PublicationResult.SourceGenerationChanged current ->
        raise (
            AssertionException(
                $"Publication generation {candidate.Generation} unexpectedly changed to {current}."
            )
        )

let private readApi
    (store: SessionActivityStore)
    window
    =
    WorktreeApi.overviewHistoryCached
        (OverviewHistoryCache.create ())
        store
        window
    |> Async.RunSynchronously

let private assertMissingPublication (store: SessionActivityStore) =
    let error =
        Assert.Throws<InvalidDataException>(fun () ->
            readApi store HistoryWindow.Hours72 |> ignore)

    Assert.That(
        error.Message,
        Is.EqualTo "Overview history has no published rollup."
    )

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryPublicationIntegrationTests() =

    [<Test>]
    member _.``exact and clamped late writes publish one complete multi-batch replacement``() =
        withDbPath (fun path ->
            let baselineAnchor =
                latestCompleteBoundary DateTimeOffset.UtcNow

            let clock = TestClock(baselineAnchor)
            let baselineAt = baselineAnchor.AddHours(-100.0)
            let olderLateAt = baselineAnchor.AddHours(-90.0)
            let exactLateAt = baselineAnchor.AddHours(-6.0)
            let baselineSources =
                { emptySources with
                    Tasks = [ baselineAt, tc 1 ] }

            use store = new SessionActivityStore(path)
            persistSources store baselineSources

            let stagedBatches = ConcurrentQueue<RollupCandidate>()
            let observations =
                ConcurrentQueue<PublicationState * RollupRow list * int>()

            // Hooks are enabled only after the baseline publication.
            let mutable captureRepair = false

            let hooks =
                { BeforeStage =
                    fun candidate ->
                        if captureRepair then
                            stagedBatches.Enqueue candidate
                  BeforePublish =
                    fun candidate ->
                        if captureRepair then
                            let state, rows =
                                readPublished
                                    store
                                    candidate.StartBoundary
                                    candidate.EndBoundary

                            observations.Enqueue(
                                state,
                                rows,
                                scalarInt
                                    path
                                    "SELECT count(*) FROM overview_history_staging;"
                            ) }

            use worker = createWorker store clock.Clock hooks
            let baselineState =
                worker.Backfill TestContext.CurrentContext.CancellationToken

            let beforeWrite =
                latestCompleteBoundary DateTimeOffset.UtcNow

            store.AppendTaskSnapshot(olderLateAt, tc 2)
            store.AppendTaskSnapshot(exactLateAt, tc 3)

            let afterWrite =
                latestCompleteBoundary DateTimeOffset.UtcNow

            let dirty =
                store.OverviewRollupState().EarliestDirty
                |> Option.get

            let repairAnchor =
                latestCompleteBoundary DateTimeOffset.UtcNow

            clock.AdvanceTo repairAnchor
            captureRepair <- true
            let repaired =
                worker.Backfill TestContext.CurrentContext.CancellationToken
            let candidate =
                { Generation = repaired.SourceGeneration
                  StartBoundary = dirty
                  EndBoundary = repairAnchor }

            let observedState, visibleRows, stagedCount =
                observations.ToArray() |> Array.exactlyOne

            let finalSources =
                { baselineSources with
                    Tasks =
                        [ baselineAt, tc 1
                          olderLateAt, tc 2
                          exactLateAt, tc 3 ] }

            Assert.Multiple(fun () ->
                Assert.That(
                    [ oldestExposedBoundary beforeWrite
                      oldestExposedBoundary afterWrite ],
                    Does.Contain dirty
                )
                Assert.That(firstBoundaryAtOrAfter exactLateAt, Is.EqualTo exactLateAt)
                Assert.That(stagedBatches.Count, Is.GreaterThan 1)
                Assert.That(
                    stagedBatches.ToArray()
                    |> Array.forall (fun batch ->
                        rangeLength batch <= maxBatchBoundaryCount),
                    Is.True
                )
                Assert.That(
                    stagedBatches.ToArray()
                    |> Array.sumBy rangeLength,
                    Is.EqualTo(rangeLength candidate)
                )
                Assert.That(
                    observedState.PublishedGeneration,
                    Is.EqualTo baselineState.PublishedGeneration
                )
                Assert.That(
                    visibleRows
                    |> List.forall (fun row -> row.Tasks = tc 1),
                    Is.True
                )
                Assert.That(stagedCount, Is.EqualTo(rangeLength candidate))
                Assert.That(repaired.CompleteThrough, Is.EqualTo(Some repairAnchor))
                Assert.That(
                    repaired.PublishedGeneration,
                    Is.EqualTo repaired.SourceGeneration
                )
                Assert.That(repaired.EarliestDirty, Is.EqualTo None))

            assertPublishedMatchesOracle
                "late-write repair"
                store
                candidate.StartBoundary
                candidate.EndBoundary
                finalSources)

    [<Test>]
    member _.``lagging published anchor repairs concurrent late write at the visible 72-hour left edge``() =
        withDbPath (fun path ->
            let publishedAnchor =
                latestCompleteBoundary(
                    DateTimeOffset.UtcNow.AddHours(-2.0)
                )

            let repairAnchor = publishedAnchor + resolution
            let publishedLeftEdge = oldestExposedBoundary publishedAnchor

            use store = new SessionActivityStore(path)
            store.AppendTaskSnapshot(publishedAnchor.AddHours(-80.0), tc 1)
            // The worker hook arms one source write between reconstruction and staging.
            let mutable injectLateWrite = false

            let hooks =
                { noHooks with
                    BeforeStage =
                        fun _ ->
                            if injectLateWrite then
                                injectLateWrite <- false
                                store.AppendTaskSnapshot(publishedLeftEdge, tc 2) }

            let clock = TestClock(publishedAnchor)
            use worker = createWorker store clock.Clock hooks
            worker.Backfill TestContext.CurrentContext.CancellationToken
            |> ignore

            clock.AdvanceTo repairAnchor
            injectLateWrite <- true
            let repaired =
                worker.Backfill TestContext.CurrentContext.CancellationToken

            let response = readApi store HistoryWindow.Hours72
            let snapshot = response.Snapshots |> List.exactlyOne

            Assert.Multiple(fun () ->
                Assert.That(injectLateWrite, Is.False)
                Assert.That(repaired.EarliestDirty, Is.EqualTo None)
                Assert.That(response.Anchor, Is.EqualTo repairAnchor)
                Assert.That(
                    snapshot.Timestamp,
                    Is.EqualTo(oldestExposedBoundary repairAnchor)
                )
                Assert.That(snapshot.Tasks, Is.EqualTo(tc 2))))

    [<Test>]
    member _.``generation change after partial staging retries without losing dirty work``() =
        withDbPath (fun path ->
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let baselineAt = anchor.AddHours(-80.0)
            let firstLateAt = anchor.AddHours(-20.0)
            let concurrentAt = anchor.AddHours(-10.0)
            let baselineSources =
                { emptySources with
                    Tasks = [ baselineAt, tc 1 ] }

            use store = new SessionActivityStore(path)
            persistSources store baselineSources
            // Hook state is confined to this deterministic injection seam.
            let mutable captureRepair = false
            let mutable batchNumber = 0
            let mutable injected = 0
            let partialStagingCounts = ConcurrentQueue<int>()
            let dirtyAfterConflict =
                ConcurrentQueue<DateTimeOffset option>()
            let publishedCandidates =
                ConcurrentQueue<RollupCandidate>()

            let hooks =
                { BeforeStage =
                    fun _ ->
                        if captureRepair then
                            let current =
                                Interlocked.Increment(&batchNumber)

                            if current = 2
                               && Interlocked.Exchange(&injected, 1) = 0 then
                                partialStagingCounts.Enqueue(
                                    scalarInt
                                        path
                                        "SELECT count(*) FROM overview_history_staging;"
                                )

                                store.AppendTaskSnapshot(
                                    concurrentAt,
                                    tc 3
                                )

                                dirtyAfterConflict.Enqueue(
                                    store.OverviewRollupState().EarliestDirty
                                )
                  BeforePublish =
                    fun candidate ->
                        if captureRepair then
                            publishedCandidates.Enqueue candidate }

            use worker =
                createWorker store (fixedClock anchor) hooks

            worker.Backfill TestContext.CurrentContext.CancellationToken
            |> ignore
            store.AppendTaskSnapshot(firstLateAt, tc 2)
            captureRepair <- true
            let repaired =
                worker.Backfill TestContext.CurrentContext.CancellationToken
            let published =
                publishedCandidates.ToArray()
                |> Array.exactlyOne

            let finalSources =
                { baselineSources with
                    Tasks =
                        [ baselineAt, tc 1
                          firstLateAt, tc 2
                          concurrentAt, tc 3 ] }

            Assert.Multiple(fun () ->
                Assert.That(
                    partialStagingCounts.ToArray()
                    |> Array.exactlyOne,
                    Is.EqualTo maxBatchBoundaryCount
                )
                Assert.That(
                    dirtyAfterConflict.ToArray()
                    |> Array.exactlyOne,
                    Is.EqualTo(Some firstLateAt)
                )
                Assert.That(
                    published.Generation,
                    Is.EqualTo repaired.SourceGeneration
                )
                Assert.That(published.StartBoundary, Is.EqualTo firstLateAt)
                Assert.That(repaired.SourceGeneration, Is.EqualTo 3L)
                Assert.That(
                    repaired.PublishedGeneration,
                    Is.EqualTo repaired.SourceGeneration
                )
                Assert.That(repaired.EarliestDirty, Is.EqualTo None)
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM overview_history_staging;",
                    Is.Zero
                ))

            assertPublishedMatchesOracle
                "generation-conflict retry"
                store
                firstLateAt
                anchor
                finalSources)

    [<Test>]
    member _.``reader snapshot observes the old generation while a complete replacement commits``() =
        withDbPath (fun path ->
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let baselineAt = anchor.AddHours(-80.0)
            let dirty = anchor.AddHours(-1.0)
            let baselineSources =
                { emptySources with
                    Tasks = [ baselineAt, tc 1 ] }

            use store = new SessionActivityStore(path)
            persistSources store baselineSources

            use worker =
                createWorker store (fixedClock anchor) noHooks

            let baselineState =
                worker.Backfill TestContext.CurrentContext.CancellationToken

            store.AppendTaskSnapshot(dirty, tc 9)
            let generation =
                store.OverviewRollupState().SourceGeneration

            let replacement =
                reconstructRange store dirty anchor

            stageRows store generation replacement

            let candidate =
                { Generation = generation
                  StartBoundary = dirty
                  EndBoundary = anchor }

            let oldState, oldRows =
                store.ReadPublishedOverviewRollup(
                    dirty,
                    anchor,
                    afterStateRead = (fun () ->
                        publish store candidate |> ignore)
                )

            let newState, newRows =
                readPublished store dirty anchor

            let finalSources =
                { baselineSources with
                    Tasks =
                        [ baselineAt, tc 1
                          dirty, tc 9 ] }

            let oldExpected =
                denseOracle dirty anchor baselineSources

            let newExpected =
                denseOracle dirty anchor finalSources

            Assert.Multiple(fun () ->
                Assert.That(
                    oldState.PublishedGeneration,
                    Is.EqualTo baselineState.PublishedGeneration
                )
                Assert.That(oldRows, Is.EqualTo oldExpected)
                Assert.That(
                    newState.PublishedGeneration,
                    Is.EqualTo generation
                )
                Assert.That(newRows, Is.EqualTo newExpected)
                Assert.That(
                    oldRows
                    |> List.map _.Tasks
                    |> List.distinct,
                    Is.EqualTo [ tc 1 ]
                )
                Assert.That(
                    newRows
                    |> List.map _.Tasks
                    |> List.distinct,
                    Is.EqualTo [ tc 9 ]
                )))

    [<Test>]
    member _.``restart discards stale partial staging and rebuilds the current generation``() =
        withDbPath (fun path ->
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let baselineAt = anchor.AddHours(-80.0)
            let dirty = anchor.AddHours(-12.0)
            let concurrentAt = anchor.AddHours(-6.0)
            let baselineSources =
                { emptySources with
                    Tasks = [ baselineAt, tc 1 ] }

            (use store = new SessionActivityStore(path)
             persistSources store baselineSources

             use worker =
                 createWorker store (fixedClock anchor) noHooks

             worker.Backfill TestContext.CurrentContext.CancellationToken
             |> ignore
             store.AppendTaskSnapshot(dirty, tc 2)
             let generation =
                 store.OverviewRollupState().SourceGeneration

             let firstEnd =
                 dirty.AddTicks(
                     resolution.Ticks
                     * int64 (maxBatchBoundaryCount - 1)
                 )

             reconstructRange store dirty firstEnd
             |> stageRows store generation

             store.AppendTaskSnapshot(concurrentAt, tc 3))

            Assert.That(
                scalarInt
                    path
                    "SELECT count(*) FROM overview_history_staging;",
                Is.EqualTo maxBatchBoundaryCount
            )

            use reopened = new SessionActivityStore(path)
            let beforeState, beforeRows =
                readPublished reopened dirty anchor

            use restarted =
                createWorker reopened (fixedClock anchor) noHooks

            let repaired =
                restarted.Backfill
                    TestContext.CurrentContext.CancellationToken

            let finalSources =
                { baselineSources with
                    Tasks =
                        [ baselineAt, tc 1
                          dirty, tc 2
                          concurrentAt, tc 3 ] }

            Assert.Multiple(fun () ->
                Assert.That(
                    beforeState.PublishedGeneration,
                    Is.LessThan repaired.PublishedGeneration
                )
                Assert.That(
                    beforeRows
                    |> List.forall (fun row -> row.Tasks = tc 1),
                    Is.True
                )
                Assert.That(
                    repaired.PublishedGeneration,
                    Is.EqualTo repaired.SourceGeneration
                )
                Assert.That(repaired.EarliestDirty, Is.EqualTo None)
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM overview_history_staging;",
                    Is.Zero
                ))

            assertPublishedMatchesOracle
                "restart recovery"
                reopened
                dirty
                anchor
                finalSources)

    [<Test>]
    member _.``runtime reconstruction and publication failures preserve the last generation``() =
        withDbPath (fun path ->
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let baselineAt = anchor.AddHours(-80.0)
            let firstDirty = anchor.AddHours(-1.0)
            let secondDirty = anchor.AddMinutes(-30.0)
            let baselineSources =
                { emptySources with
                    Tasks = [ baselineAt, tc 1 ] }

            use store = new SessionActivityStore(path)
            persistSources store baselineSources
            // One-shot failure injection lives only at the worker hook boundary.
            let mutable failReconstruction = false

            let hooks =
                { noHooks with
                    BeforeStage =
                        fun _ ->
                            if failReconstruction then
                                failReconstruction <- false
                                raise (
                                    InvalidOperationException(
                                        "forced runtime reconstruction failure"
                                    )
                                ) }

            use worker =
                createWorker store (fixedClock anchor) hooks

            worker.Backfill TestContext.CurrentContext.CancellationToken
            |> ignore
            let stableState, stableRows =
                readPublished
                    store
                    (oldestExposedBoundary anchor)
                    anchor

            store.AppendTaskSnapshot(firstDirty, tc 4)
            failReconstruction <- true

            let reconstructionError =
                Assert.Throws<InvalidOperationException>(fun () ->
                    worker.Backfill
                        TestContext.CurrentContext.CancellationToken
                    |> ignore)

            let afterReconstructionState, afterReconstructionRows =
                readPublished
                    store
                    (oldestExposedBoundary anchor)
                    anchor

            Assert.Multiple(fun () ->
                Assert.That(
                    reconstructionError.Message,
                    Is.EqualTo "forced runtime reconstruction failure"
                )
                Assert.That(
                    afterReconstructionState.PublishedGeneration,
                    Is.EqualTo stableState.PublishedGeneration
                )
                Assert.That(
                    afterReconstructionState.CompleteThrough,
                    Is.EqualTo stableState.CompleteThrough
                )
                Assert.That(afterReconstructionRows, Is.EqualTo stableRows)
                Assert.That(
                    store.OverviewRollupState().EarliestDirty,
                    Is.EqualTo(Some firstDirty)
                )
                Assert.That(
                    (readApi store HistoryWindow.Hours72).Snapshots
                    |> List.forall (fun snapshot ->
                        snapshot.Tasks = tc 1),
                    Is.True
                ))

            worker.Backfill TestContext.CurrentContext.CancellationToken
            |> ignore

            let firstRepairSources =
                { baselineSources with
                    Tasks =
                        [ baselineAt, tc 1
                          firstDirty, tc 4 ] }

            assertPublishedMatchesOracle
                "reconstruction retry"
                store
                (oldestExposedBoundary anchor)
                anchor
                firstRepairSources

            store.AppendTaskSnapshot(secondDirty, tc 7)
            let beforePublicationFailure =
                readPublished
                    store
                    (oldestExposedBoundary anchor)
                    anchor

            execute
                path
                """
CREATE TRIGGER fail_publication_integration
BEFORE INSERT ON overview_history_rows
BEGIN
    SELECT RAISE(ABORT, 'forced publication failure');
END;
"""

            Assert.Throws<SqliteException>(fun () ->
                worker.Backfill TestContext.CurrentContext.CancellationToken
                |> ignore)
            |> ignore

            let afterPublicationFailure =
                readPublished
                    store
                    (oldestExposedBoundary anchor)
                    anchor

            Assert.Multiple(fun () ->
                Assert.That(
                    afterPublicationFailure,
                    Is.EqualTo beforePublicationFailure
                )
                Assert.That(
                    store.OverviewRollupState().EarliestDirty,
                    Is.EqualTo(Some secondDirty)
                )
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM overview_history_staging;",
                    Is.GreaterThan 0
                ))

            execute path "DROP TRIGGER fail_publication_integration;"
            let repaired =
                worker.Backfill TestContext.CurrentContext.CancellationToken

            let finalSources =
                { baselineSources with
                    Tasks =
                        [ baselineAt, tc 1
                          firstDirty, tc 4
                          secondDirty, tc 7 ] }

            Assert.Multiple(fun () ->
                Assert.That(
                    repaired.PublishedGeneration,
                    Is.EqualTo repaired.SourceGeneration
                )
                Assert.That(repaired.EarliestDirty, Is.EqualTo None)
                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM overview_history_staging;",
                    Is.Zero
                ))

            assertPublishedMatchesOracle
                "publication retry"
                store
                (oldestExposedBoundary anchor)
                anchor
                finalSources)

    [<Test>]
    member _.``raw retention waits for multi-batch repair and cannot change its source snapshot``() =
        withDbPath (fun path ->
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let baselineAt = anchor.AddHours(-80.0)
            let dirty = anchor.AddHours(-70.0)
            let exactAt = anchor.AddHours(-6.0)
            let baselineSources =
                { emptySources with
                    Tasks = [ baselineAt, tc 1 ] }

            use store = new SessionActivityStore(path)
            persistSources store baselineSources
            // Events coordinate the deliberately paused repair without sleeps.
            use stagePaused = new ManualResetEventSlim(false)
            use releaseRepair = new ManualResetEventSlim(false)
            use retentionStarted = new ManualResetEventSlim(false)
            // Hook state is confined to this one pause injection.
            let mutable captureRepair = false
            let mutable paused = 0

            let hooks =
                { noHooks with
                    BeforeStage =
                        fun _ ->
                            if captureRepair
                               && Interlocked.Exchange(&paused, 1) = 0 then
                                stagePaused.Set()
                                releaseRepair.Wait() }

            use worker =
                createWorker store (fixedClock anchor) hooks

            worker.Backfill TestContext.CurrentContext.CancellationToken
            |> ignore
            store.AppendTaskSnapshot(dirty, tc 2)
            store.AppendTaskSnapshot(exactAt, tc 3)
            captureRepair <- true

            let repair =
                Task.Run(fun () ->
                    worker.Backfill
                        TestContext.CurrentContext.CancellationToken)

            Assert.That(
                stagePaused.Wait(TimeSpan.FromSeconds 10.0),
                Is.True,
                "The repair did not reach its first staged batch."
            )

            let retention =
                Task.Run(fun () ->
                    retentionStarted.Set()
                    store.PruneOld(anchor + resolution))

            Assert.That(
                retentionStarted.Wait(TimeSpan.FromSeconds 10.0),
                Is.True,
                "The retention task did not start."
            )

            try
                Assert.That(
                    retention.Wait(TimeSpan.FromSeconds 1.0),
                    Is.False,
                    "Raw retention completed while the multi-batch repair held the maintenance lease."
                )
            finally
                releaseRepair.Set()

            let repaired =
                repair.WaitAsync(TimeSpan.FromSeconds 30.0)
                    .GetAwaiter()
                    .GetResult()

            retention.WaitAsync(TimeSpan.FromSeconds 30.0)
                .GetAwaiter()
                .GetResult()
            |> ignore

            let finalSources =
                { baselineSources with
                    Tasks =
                        [ baselineAt, tc 1
                          dirty, tc 2
                          exactAt, tc 3 ] }

            Assert.Multiple(fun () ->
                Assert.That(
                    repaired.PublishedGeneration,
                    Is.EqualTo repaired.SourceGeneration
                )
                Assert.That(repaired.EarliestDirty, Is.EqualTo None)
                Assert.That(
                    store.QueryTaskSnapshots(
                        baselineAt.AddHours(-1.0),
                        anchor + resolution
                    ),
                    Is.EqualTo [ exactAt, tc 3 ]
                ))

            assertPublishedMatchesOracle
                "retention-serialized repair"
                store
                dirty
                anchor
                finalSources)
