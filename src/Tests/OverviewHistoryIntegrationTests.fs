module Tests.OverviewHistoryIntegrationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open NUnit.Framework
open Shared
open OverviewData
open Server
open Server.OverviewHistoryRollup
open Server.SessionActivity
open Server.SessionActivityStore
open Tests.OverviewTestHelpers

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryIntegrationTests() =

    let anchor = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
    let tc kind count : TaskCount = { Kind = kind; Count = count }
    let ac kind count : AgentCount = { Kind = kind; Count = count }

    let withStorePath (action: string -> SessionActivityStore -> unit) =
        let directory = Path.Combine(Path.GetTempPath(), $"treemon-overview-history-{Guid.NewGuid()}")
        Directory.CreateDirectory directory |> ignore
        let path = Path.Combine(directory, "activity.db")
        let store = new SessionActivityStore(path)

        try
            action path store
        finally
            (store :> IDisposable).Dispose()

            try
                Directory.Delete(directory, true)
            with _ ->
                ()

    let withStore (action: SessionActivityStore -> unit) =
        withStorePath (fun _ store -> action store)

    let execute path sql =
        use connection =
            new SqliteConnection(
                SqliteConnectionStringBuilder(DataSource = path, Pooling = false).ConnectionString
            )
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    let stored sid worktree status updatedAt lastSeen : StoredStatus =
        { SessionId = SessionId sid
          WorktreePath = WorktreePath worktree
          Provider = CopilotCli
          Status =
            { Status = status
              Skill = None
              Intent = None
              Title = None
              LastUserMessage = None
              LastAssistantMessage = None
              ContextUsage = None }
          UpdatedAt = updatedAt
          LastSeen = lastSeen
          ContextUsageAt = None }

    let readHistory (store: SessionActivityStore) (window: HistoryWindow) =
        let duration = HistoryWindow.duration window
        let inputs = store.QueryOverviewHistoryInputs(anchor - duration, anchor)

        { OverviewHistoryResponse.Anchor = anchor
          Snapshots =
            OverviewHistory.sample
                anchor
                duration
                inputs.TaskSnapshots
                inputs.Events
                inputs.Liveness }

    let rollupRow boundary count : RollupRow =
        { Boundary = boundary
          Tasks = [ tc TaskBucketKind.Planned count ]
          Agents = [] }

    let publishRows (store: SessionActivityStore) generation (rows: RollupRow list) =
        let candidate =
            { Generation = generation
              StartBoundary = rows.Head.Boundary
              EndBoundary = (List.last rows).Boundary }

        let stagedRows =
            rows
            |> List.map (fun row ->
                { Generation = generation
                  Row = row })

        match store.StageOverviewRollup(candidate, stagedRows) with
        | StagingResult.Staged -> ()
        | StagingResult.SourceGenerationChanged current ->
            failwith $"Expected generation {generation} to stage, but source generation is {current}."

        match store.PublishOverviewRollup candidate with
        | PublicationResult.Published state -> state
        | PublicationResult.SourceGenerationChanged current ->
            failwith $"Expected generation {generation} to publish, but source generation is {current}."

    let publishDense
        (store: SessionActivityStore)
        startBoundary
        endBoundary
        countAt
        =
        let generation = store.OverviewRollupState().SourceGeneration

        boundaries startBoundary endBoundary
        |> Seq.mapi (fun index boundary -> rollupRow boundary (countAt index))
        |> Seq.toList
        |> publishRows store generation

    [<Test>]
    member _.``task snapshots round trip as count only JSON``() =
        withStore (fun store ->
            let tasks = [ tc TaskBucketKind.Planned 4; tc TaskBucketKind.InProgress 2 ]
            store.AppendTaskSnapshot(anchor.AddMinutes(-10.0), tasks)

            Assert.That(
                store.QueryTaskSnapshots(anchor.AddHours(-1.0), anchor),
                Is.EqualTo [ anchor.AddMinutes(-10.0), tasks ]
            ))

    [<Test>]
    member _.``raw oracle samples store changes at boundaries and returns its supplied anchor``() =
        withStore (fun store ->
            let window = HistoryWindow.duration HistoryWindow.Hours12
            let start = anchor - window
            let first = start.AddMinutes 2.5
            let tasksA = [ tc TaskBucketKind.Planned 2 ]
            let tasksB = [ tc TaskBucketKind.Planned 5 ]
            let executing = [ ac (AgentGroupKind.Activity CurrentActivity.Executing) 1 ]

            store.AppendTaskSnapshot(start.AddHours(-1.0), tasksA)
            store.AppendTaskSnapshot(first, tasksB)
            store.AppendEvent(evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") first) |> ignore

            let response = readHistory store HistoryWindow.Hours12

            Assert.Multiple(fun () ->
                Assert.That(response.Anchor, Is.EqualTo anchor)
                Assert.That(
                    response.Snapshots,
                    Is.EqualTo
                        [ { Timestamp = start; Tasks = tasksA; Agents = [] }
                          { Timestamp = first; Tasks = tasksB; Agents = executing }
                          { Timestamp = start.AddMinutes 7.5; Tasks = tasksB; Agents = [] } ]
                )))

    [<Test>]
    member _.``published API anchors at complete-through and collapses selected values``() =
        withStore (fun store ->
            let cache = OverviewHistoryCache.create ()
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours12

            publishDense store start anchor (fun index ->
                if index < 5 then 1
                elif index = 5 then 2
                else 3)
            |> ignore

            let response =
                WorktreeApi.overviewHistoryCached cache store HistoryWindow.Hours12
                |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(response.Anchor, Is.EqualTo anchor)
                Assert.That(
                    response.Snapshots,
                    Is.EqualTo
                        [ { Timestamp = start
                            Tasks = [ tc TaskBucketKind.Planned 1 ]
                            Agents = [] }
                          { Timestamp = start.AddMinutes 2.5
                            Tasks = [ tc TaskBucketKind.Planned 2 ]
                            Agents = [] }
                          { Timestamp = start.AddMinutes 5.0
                            Tasks = [ tc TaskBucketKind.Planned 3 ]
                            Agents = [] } ]
                )
                Assert.That(response.Snapshots.Length, Is.LessThanOrEqualTo(sampleIntervalCount + 1))))

    [<Test>]
    member _.``published store selects exactly 289 rows at each window stride``() =
        withStore (fun store ->
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours72
            publishDense store start anchor (fun _ -> 1) |> ignore

            [ HistoryWindow.Hours12; HistoryWindow.Hours24; HistoryWindow.Hours72 ]
            |> List.iter (fun window ->
                let rows =
                    store.UsePublishedOverviewRollupSnapshot(
                        window,
                        fun _ _ readRows -> async { return readRows () }
                    )
                    |> Async.RunSynchronously

                let expectedStep =
                    TimeSpan.FromSeconds(int64 (resolutionSeconds * stride window))

                Assert.Multiple(fun () ->
                    Assert.That(rows.Length, Is.EqualTo(sampleIntervalCount + 1))
                    Assert.That(rows.Head.Boundary, Is.EqualTo(anchor - HistoryWindow.duration window))
                    Assert.That((List.last rows).Boundary, Is.EqualTo anchor)
                    Assert.That(
                        rows
                        |> List.pairwise
                        |> List.forall (fun (left, right) -> right.Boundary - left.Boundary = expectedStep),
                        Is.True
                    ))))

    [<Test>]
    member _.``published identity and selected rows share one read snapshot``() =
        withStore (fun store ->
            let currentAnchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let start = currentAnchor - HistoryWindow.duration HistoryWindow.Hours12
            publishDense store start currentAnchor (fun _ -> 1) |> ignore

            store.AppendTaskSnapshot(currentAnchor, [ tc TaskBucketKind.Planned 9 ])
            let repairGeneration = store.OverviewRollupState().SourceGeneration
            let repairCandidate =
                { Generation = repairGeneration
                  StartBoundary = currentAnchor
                  EndBoundary = currentAnchor }
            let repairRows =
                [ { Generation = repairGeneration
                    Row = rollupRow currentAnchor 9 } ]

            match store.StageOverviewRollup(repairCandidate, repairRows) with
            | StagingResult.Staged -> ()
            | StagingResult.SourceGenerationChanged current ->
                failwith $"Expected repair generation {repairGeneration}, but source generation is {current}."

            let oldState, oldAnchor, oldRows =
                store.UsePublishedOverviewRollupSnapshot(
                    HistoryWindow.Hours12,
                    (fun state observedAnchor readRows ->
                        async {
                            return state, observedAnchor, readRows ()
                        }),
                    afterStateRead =
                        (fun () ->
                            match store.PublishOverviewRollup repairCandidate with
                            | PublicationResult.Published _ -> ()
                            | PublicationResult.SourceGenerationChanged current ->
                                failwith $"Expected repair publication, but source generation is {current}.")
                )
                |> Async.RunSynchronously

            let newState, newAnchor, newRows =
                store.UsePublishedOverviewRollupSnapshot(
                    HistoryWindow.Hours12,
                    fun state observedAnchor readRows ->
                        async {
                            return state, observedAnchor, readRows ()
                        }
                )
                |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(oldState.PublishedGeneration, Is.Zero)
                Assert.That(oldAnchor, Is.EqualTo currentAnchor)
                Assert.That((List.last oldRows).Tasks, Is.EqualTo [ tc TaskBucketKind.Planned 1 ])
                Assert.That(newState.PublishedGeneration, Is.EqualTo repairGeneration)
                Assert.That(newAnchor, Is.EqualTo currentAnchor)
                Assert.That((List.last newRows).Tasks, Is.EqualTo [ tc TaskBucketKind.Planned 9 ])))

    [<Test>]
    member _.``published cache hits avoid row reads and publication changes invalidate``() =
        withStore (fun store ->
            let cache = OverviewHistoryCache.create ()
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours12
            publishDense store start anchor (fun _ -> 1) |> ignore
            // The store invokes this seam only when the publication-keyed cache actually reads rows.
            let mutable rowReads = 0
            let beforeRowsRead () = Interlocked.Increment(&rowReads) |> ignore

            let first =
                WorktreeApi.overviewHistoryCachedWith
                    beforeRowsRead
                    cache
                    store
                    HistoryWindow.Hours12
                |> Async.RunSynchronously

            let hit =
                WorktreeApi.overviewHistoryCachedWith
                    beforeRowsRead
                    cache
                    store
                    HistoryWindow.Hours12
                |> Async.RunSynchronously

            let nextAnchor = anchor + resolution
            let publishedGeneration = store.OverviewRollupState().PublishedGeneration
            publishRows store publishedGeneration [ rollupRow nextAnchor 9 ] |> ignore

            let repaired =
                WorktreeApi.overviewHistoryCachedWith
                    beforeRowsRead
                    cache
                    store
                    HistoryWindow.Hours12
                |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(hit, Is.EqualTo first)
                Assert.That(rowReads, Is.EqualTo 2)
                Assert.That(repaired.Anchor, Is.EqualTo nextAnchor)
                Assert.That((List.last repaired.Snapshots).Timestamp, Is.EqualTo nextAnchor)
                Assert.That(
                    (List.last repaired.Snapshots).Tasks,
                    Is.EqualTo [ tc TaskBucketKind.Planned 9 ]
                )))

    [<Test>]
    member _.``simultaneous published API misses perform one row read``() =
        withStore (fun store ->
            let cache = OverviewHistoryCache.create ()
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours12
            publishDense store start anchor (fun _ -> 1) |> ignore
            // Concurrent cache callbacks require one shared atomic read counter.
            let mutable rowReads = 0

            let requests =
                [ 1..8 ]
                |> List.map (fun _ ->
                    WorktreeApi.overviewHistoryCachedWith
                        (fun () -> Interlocked.Increment(&rowReads) |> ignore)
                        cache
                        store
                        HistoryWindow.Hours12
                    |> Async.StartAsTask)

            let responses = Task.WhenAll requests |> Async.AwaitTask |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(rowReads, Is.EqualTo 1)
                Assert.That(responses |> Array.distinct, Has.Length.EqualTo 1)))

    [<Test>]
    member _.``real published API fails when publication is missing``() =
        withStore (fun store ->
            let error =
                Assert.Throws<InvalidDataException>(fun () ->
                    WorktreeApi.overviewHistoryCached
                        (OverviewHistoryCache.create ())
                        store
                        HistoryWindow.Hours12
                    |> Async.RunSynchronously
                    |> ignore)

            Assert.That(error.Message, Is.EqualTo "Overview history has no published rollup."))

    [<Test>]
    member _.``incomplete publication fails instead of returning partial history``() =
        withStore (fun store ->
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours12
            publishDense store (start + resolution) anchor (fun _ -> 1) |> ignore

            let error =
                Assert.Throws<InvalidDataException>(fun () ->
                    WorktreeApi.overviewHistoryCached
                        (OverviewHistoryCache.create ())
                        store
                        HistoryWindow.Hours12
                    |> Async.RunSynchronously
                    |> ignore)

            Assert.That(
                error.Message,
                Is.EqualTo "Overview history publication does not completely cover the requested window."
            ))

    [<Test>]
    member _.``demo history keeps its empty response``() =
        let api =
            WorktreeApi.readOnlyApi
                "demo mode"
                (fun () -> async { return Unchecked.defaultof<DashboardResponse> })
                (fun () -> async { return Map.empty })

        let response =
            api.getOverviewHistory HistoryWindow.Hours72
            |> Async.RunSynchronously

        Assert.That(response.Snapshots, Is.Empty)

    [<Test>]
    member _.``published API reads no raw history tables``() =
        withStorePath (fun path store ->
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours12
            publishDense store start anchor (fun _ -> 1) |> ignore

            execute
                path
                """
DROP TABLE activity_events;
DROP TABLE session_liveness;
DROP TABLE task_snapshots;
"""

            let response =
                WorktreeApi.overviewHistoryCached
                    (OverviewHistoryCache.create ())
                    store
                    HistoryWindow.Hours12
                |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(response.Anchor, Is.EqualTo anchor)
                Assert.That(response.Snapshots.Length, Is.EqualTo 1)))

    [<Test>]
    member _.``raw oracle carries task status and liveness lookbacks to the left edge``() =
        withStore (fun store ->
            let window = HistoryWindow.duration HistoryWindow.Hours12
            let start = anchor - window
            let tasks = [ tc TaskBucketKind.Blocked 3 ]
            let eventAt = start.AddMinutes(-10.0)
            let event = evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Working (Some "pr") eventAt

            store.AppendTaskSnapshot(start.AddDays(-1.0), tasks)
            store.AppendAndUpsert(
                event,
                stored "s1" "C:/wt/a" SessionLevelStatus.Working eventAt eventAt
            )
            |> ignore
            store.AppendEvent(evt "irrelevant" "closed" "C:/wt/closed" SessionLevelStatus.Working None (start.AddDays(-1.0))) |> ignore
            store.RecordLiveness(SessionId "s1", start.AddMinutes(-1.0))

            let response = readHistory store HistoryWindow.Hours12

            Assert.Multiple(fun () ->
                Assert.That(
                    store.QueryHistoryWindow(start, anchor) |> List.map (_.EventId >> EventId.value),
                    Is.EqualTo [ "e1" ]
                )
                Assert.That(response.Snapshots.Head.Timestamp, Is.EqualTo start)
                Assert.That(response.Snapshots.Head.Tasks, Is.EqualTo tasks)
                Assert.That(
                    response.Snapshots.Head.Agents,
                    Is.EqualTo [ ac (AgentGroupKind.Activity CurrentActivity.PR) 1 ]
                )
                Assert.That(response.Snapshots |> List.forall (fun snapshot -> snapshot.Timestamp >= start), Is.True)))

    [<Test>]
    member _.``history inputs stay on one snapshot when liveness commits after the status read``() =
        let directory = Path.Combine(Path.GetTempPath(), $"treemon-overview-history-{Guid.NewGuid()}")
        Directory.CreateDirectory directory |> ignore

        try
            let path = Path.Combine(directory, "activity.db")
            use reader = new SessionActivityStore(path)
            use writer = new SessionActivityStore(path)
            let window = HistoryWindow.duration HistoryWindow.Hours12
            let start = anchor - window
            let sessionId = SessionId "interleaved"
            let baselineAt = start - SessionActivity.openWindow - TimeSpan.FromMinutes 1.0
            let livenessAt = start.AddMinutes(-1.0)

            writer.AppendEvent(
                evt
                    "baseline"
                    (SessionId.value sessionId)
                    "C:/wt/interleaved"
                    SessionLevelStatus.Working
                    (Some "bd-execute")
                    baselineAt
            )
            |> ignore
            writer.UpsertStatus(
                stored
                    (SessionId.value sessionId)
                    "C:/wt/interleaved"
                    SessionLevelStatus.Working
                    baselineAt
                    baselineAt
            )

            let inputs =
                reader.QueryOverviewHistoryInputs(
                    start,
                    anchor,
                    beforeLivenessRead = (fun () -> writer.RecordLiveness(sessionId, livenessAt))
                )

            let response: OverviewHistoryResponse =
                { Anchor = anchor
                  Snapshots =
                    OverviewHistory.sample
                        anchor
                        window
                        inputs.TaskSnapshots
                        inputs.Events
                        inputs.Liveness }

            Assert.Multiple(fun () ->
                Assert.That(inputs.Events, Is.Empty)
                Assert.That(inputs.Liveness, Is.Empty)
                Assert.That(
                    writer.QueryLiveness(start - SessionActivity.openWindow, anchor),
                    Is.EqualTo [ sessionId, livenessAt ]
                )
                Assert.That(
                    response.Snapshots,
                    Is.EqualTo [ { Timestamp = start; Tasks = []; Agents = [] } ]
                ))
        finally
            try
                Directory.Delete(directory, true)
            with _ ->
                ()

    [<Test>]
    member _.``raw oracle honors all supported windows and the hard output bound``() =
        withStore (fun store ->
            store.AppendTaskSnapshot(anchor.AddDays(-4.0), [ tc TaskBucketKind.Planned 1 ])

            [ HistoryWindow.Hours12; HistoryWindow.Hours24; HistoryWindow.Hours72 ]
            |> List.iter (fun window ->
                let response = readHistory store window
                let start = anchor - HistoryWindow.duration window

                Assert.Multiple(fun () ->
                    Assert.That(response.Snapshots.Length, Is.LessThanOrEqualTo(OverviewHistory.sampleBucketCount + 1))
                    Assert.That(response.Snapshots.Head.Timestamp, Is.EqualTo start)
                    Assert.That(response.Snapshots |> List.forall (fun snapshot -> snapshot.Timestamp <= anchor), Is.True))))

    [<Test>]
    member _.``task snapshot capture appends only changes``() =
        withStore (fun store ->
            let first = [ tc TaskBucketKind.Planned 1 ]
            let second = [ tc TaskBucketKind.Planned 2 ]

            Assert.Multiple(fun () ->
                Assert.That(store.AppendTaskSnapshotIfChanged(anchor.AddMinutes(-3.0), first), Is.True)
                Assert.That(store.AppendTaskSnapshotIfChanged(anchor.AddMinutes(-2.0), first), Is.False)
                Assert.That(store.AppendTaskSnapshotIfChanged(anchor.AddMinutes(-1.0), second), Is.True)
                Assert.That(store.QueryTaskSnapshots(anchor.AddHours(-1.0), anchor).Length, Is.EqualTo 2)))

    [<Test>]
    member _.``pruning retains one status baseline per retained session and one task baseline``() =
        withStore (fun store ->
            let cutoff = anchor.AddDays(-60.0)
            let old1 = anchor.AddDays(-90.0)
            let old2 = anchor.AddDays(-80.0)
            let recent = anchor.AddDays(-1.0)

            [ evt "s1-old" "s1" "C:/wt/a" SessionLevelStatus.Idle None old1
              evt "s1-base" "s1" "C:/wt/a" SessionLevelStatus.Working None old2
              evt "s2-old" "s2" "C:/wt/b" SessionLevelStatus.Working None old1
              evt "s2-base" "s2" "C:/wt/b" SessionLevelStatus.WaitingForUser None old2
              evt "expired" "s3" "C:/wt/c" SessionLevelStatus.Working None old2 ]
            |> List.iter (store.AppendEvent >> ignore)

            store.UpsertStatus(stored "s1" "C:/wt/a" SessionLevelStatus.Working old2 old2)
            store.UpsertStatus(stored "s2" "C:/wt/b" SessionLevelStatus.WaitingForUser old2 old2)
            store.UpsertStatus(stored "s3" "C:/wt/c" SessionLevelStatus.Working old2 old2)
            store.RecordLiveness(SessionId "s1", recent)
            store.RecordLiveness(SessionId "s2", recent)

            store.AppendTaskSnapshot(old1, [ tc TaskBucketKind.Planned 1 ])
            store.AppendTaskSnapshot(old2, [ tc TaskBucketKind.Planned 2 ])
            store.AppendTaskSnapshot(recent, [ tc TaskBucketKind.Planned 3 ])

            store.PruneOld cutoff |> ignore

            let retainedEvents =
                store.QueryWindow(anchor.AddDays(-120.0), anchor)
                |> List.map (_.EventId >> EventId.value)

            Assert.Multiple(fun () ->
                Assert.That(retainedEvents, Is.EqualTo [ "s1-base"; "s2-base" ])
                Assert.That(
                    store.QueryTaskSnapshots(anchor.AddDays(-120.0), anchor),
                    Is.EqualTo
                        [ old2, [ tc TaskBucketKind.Planned 2 ]
                          recent, [ tc TaskBucketKind.Planned 3 ] ]
                )
                Assert.That(
                    store.QueryHistoryWindow(anchor.AddHours(-72.0), anchor)
                    |> List.map (_.EventId >> EventId.value),
                    Is.EqualTo [ "s1-base"; "s2-base" ]
                )))
