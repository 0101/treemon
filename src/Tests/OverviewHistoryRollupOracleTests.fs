module Tests.OverviewHistoryRollupOracleTests

open System
open System.IO
open Microsoft.Data.Sqlite
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewHistoryReconstruction
open Server.OverviewHistoryRollup
open Server.SessionActivity
open Server.SessionActivityStore
open Shared
open Tests.OverviewTestHelpers

type private Sources =
    { Tasks: (DateTimeOffset * TaskCount list) list
      Events: ActivityEventRow list
      Liveness: (SessionId * DateTimeOffset) list }

let private withStore action =
    let directory = Path.Combine(Path.GetTempPath(), $"treemon-rollup-oracle-{Guid.NewGuid()}")
    Directory.CreateDirectory directory |> ignore
    let path = Path.Combine(directory, "activity.db")

    try
        use store = new SessionActivityStore(path)
        action path store
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

let private insertLiveness path rows =
    // Seed raw liveness directly so reconstruction covers unknown sessions and historical gaps that
    // the forward-only live-status API intentionally rejects.
    use connection = openConnection path
    use tx = connection.BeginTransaction()

    rows
    |> List.iter (fun (sessionId, observedAt) ->
        use command = connection.CreateCommand()
        command.Transaction <- tx
        command.CommandText <-
            "INSERT OR IGNORE INTO session_liveness(session_id, ts) VALUES ($session, $observed);"
        command.Parameters.AddWithValue("$session", SessionId.value sessionId) |> ignore
        command.Parameters.AddWithValue("$observed", isoUtc observedAt) |> ignore
        command.ExecuteNonQuery() |> ignore)

    tx.Commit()

let private tc kind count : TaskCount =
    { Kind = kind
      Count = count }

let private rngModulus = 2147483647L

let private nextRandom (state: int64) =
    (state * 48271L) % rngModulus

let private nextInt (upper: int) (state: int64) =
    let next = nextRandom state
    int (next % int64 upper), next

let private randomTimestamp
    (startBoundary: DateTimeOffset)
    (rowCount: int)
    (state: int64)
    =
    let index, state = nextInt rowCount state
    let exact, state = nextInt 3 state
    let boundary = startBoundary.AddTicks(resolution.Ticks * int64 index)

    if exact = 0 || index = rowCount - 1 then
        boundary, state
    else
        let milliseconds, state = nextInt 29999 state
        boundary.AddMilliseconds(float (milliseconds + 1)), state

let private randomTasks state =
    let kinds =
        [ TaskBucketKind.Planned
          TaskBucketKind.Queued
          TaskBucketKind.InProgress
          TaskBucketKind.Blocked
          TaskBucketKind.Done
          TaskBucketKind.Unattended ]

    let rec build state remaining acc =
        match remaining with
        | [] -> List.rev acc, state
        | kind :: rest ->
            let count, state = nextInt 4 state
            let next = if count = 0 then acc else tc kind count :: acc
            build state rest next

    build state kinds []

let private generateRandomEvents startBoundary rowCount count state =
    let statuses =
        [ SessionLevelStatus.Working
          SessionLevelStatus.WaitingForUser
          SessionLevelStatus.Idle ]

    let skills =
        [ Some "bd-execute"
          Some "bd-plan"
          Some "review"
          Some "pr"
          None
          Some "other" ]

    let rec build index state acc =
        if index = count then
            List.rev acc, state
        else
            let sessionIndex, state = nextInt 10 state
            let statusIndex, state = nextInt statuses.Length state
            let skillIndex, state = nextInt skills.Length state
            let at, state = randomTimestamp startBoundary rowCount state
            let session = $"random-s{sessionIndex}"
            let worktree = $"random-worktree-{sessionIndex % 4}"

            let row =
                evt
                    $"random-event-{index}"
                    session
                    worktree
                    statuses[statusIndex]
                    skills[skillIndex]
                    at

            build (index + 1) state (row :: acc)

    build 0 state []

let private generateRandomLiveness startBoundary rowCount count state =
    let rec build index state acc =
        if index = count then
            List.rev acc, state
        else
            let sessionIndex, state = nextInt 11 state
            let at, state = randomTimestamp startBoundary rowCount state

            let sessionId =
                if sessionIndex = 10 then SessionId "random-unknown"
                else SessionId $"random-s{sessionIndex}"

            build (index + 1) state ((sessionId, at) :: acc)

    build 0 state []

let private generateRandomTaskChanges startBoundary rowCount count state =
    let rec build index state acc =
        if index = count then
            List.rev acc, state
        else
            let at, state = randomTimestamp startBoundary rowCount state
            let tasks, state = randomTasks state
            build (index + 1) state ((at, tasks) :: acc)

    build 0 state []

let private fixtureSources (startBoundary: DateTimeOffset) ordinal =
    let prefix = $"window-{ordinal}"
    let boundary offset = startBoundary.AddTicks(resolution.Ticks * int64 offset)
    let between offset = (boundary offset).AddTicks 1L
    let sharedWorktree = $"{prefix}-shared"
    let gapSession = $"{prefix}-gap"
    let transitionSession = $"{prefix}-transition"

    { Tasks =
        [ startBoundary, [ tc TaskBucketKind.Planned (ordinal + 1) ]
          between 1, [ tc TaskBucketKind.Blocked (ordinal + 2) ]
          boundary 20, [ tc TaskBucketKind.InProgress (ordinal + 3) ] ]
      Events =
        [ evt $"{prefix}-exact" $"{prefix}-exact" sharedWorktree SessionLevelStatus.Working (Some "bd-execute") startBoundary
          evt $"{prefix}-between" $"{prefix}-between" sharedWorktree SessionLevelStatus.Idle None (between 1)
          evt $"{prefix}-gap-start" gapSession $"{prefix}-gap-worktree" SessionLevelStatus.Working (Some "pr") (startBoundary.AddMinutes(-1.0))
          evt $"{prefix}-transition-newer" transitionSession $"{prefix}-transition-worktree" SessionLevelStatus.WaitingForUser None (boundary 20)
          evt $"{prefix}-transition-older" transitionSession $"{prefix}-transition-worktree" SessionLevelStatus.Working (Some "bd-plan") (boundary 18)
          evt $"{prefix}-transition-skill" transitionSession $"{prefix}-transition-worktree" SessionLevelStatus.Working (Some "pr") (boundary 22)
          evt $"{prefix}-transition-idle" transitionSession $"{prefix}-transition-worktree" SessionLevelStatus.Idle None (boundary 24)
          evt $"{prefix}-multi-a" $"{prefix}-multi-a" sharedWorktree SessionLevelStatus.Working (Some "review") (startBoundary - resolution)
          evt $"{prefix}-multi-b" $"{prefix}-multi-b" sharedWorktree SessionLevelStatus.Idle None (startBoundary - resolution) ]
      Liveness =
        [ SessionId gapSession, boundary 2
          SessionId gapSession, boundary 2
          SessionId gapSession, boundary 12
          SessionId $"{prefix}-unknown", boundary 4 ] }

let private normalizeTaskChanges rows =
    rows
    |> List.indexed
    |> List.sortBy (fun (index, (at, _)) -> at, index)
    |> List.fold (fun (previous, accepted) (_, row) ->
        let _, tasks = row

        if Some tasks = previous then
            previous, accepted
        else
            Some tasks, row :: accepted
    ) (None, [])
    |> snd
    |> List.rev

let private scenario seed anchor =
    let retainedStart = oldestRetainedBoundary anchor
    let rowCount = boundaries retainedStart anchor |> Seq.length
    let randomEvents, state = generateRandomEvents retainedStart rowCount 48 (int64 seed)
    let randomLiveness, state = generateRandomLiveness retainedStart rowCount 32 state
    let randomTaskChanges, _ = generateRandomTaskChanges retainedStart rowCount 18 state

    let fixtures =
        [ fixtureSources (anchor - HistoryWindow.duration HistoryWindow.Hours12) 12
          fixtureSources (anchor - HistoryWindow.duration HistoryWindow.Hours24) 24
          fixtureSources (anchor - HistoryWindow.duration HistoryWindow.Hours72) 72 ]

    { Tasks =
        ((anchor.AddHours(-100.0), [ tc TaskBucketKind.Planned 1; tc TaskBucketKind.Done 1 ])
         :: randomTaskChanges
         @ (fixtures |> List.collect _.Tasks))
        |> normalizeTaskChanges
      Events = randomEvents @ (fixtures |> List.collect _.Events)
      Liveness = randomLiveness @ (fixtures |> List.collect _.Liveness) }

let private formatTaskCounts (tasks: TaskCount list) =
    tasks
    |> List.map (fun count -> $"{count.Kind}:{count.Count}")
    |> String.concat ","

let private formatAgentCounts (agents: AgentCount list) =
    agents
    |> List.map (fun count -> $"{count.Kind}:{count.Count}")
    |> String.concat ","

let private formatRow =
    function
    | None -> "<missing>"
    | Some(row: RollupRow) ->
        $"{row.Boundary:O} tasks=[{formatTaskCounts row.Tasks}] agents=[{formatAgentCounts row.Agents}]"

let private sourceContext sources boundary =
    let radius = openWindow + resolution
    let latestTasks =
        sources.Tasks
        |> List.filter (fun (at, _) -> at <= boundary)
        |> List.tryLast
        |> Option.map (fun (at, tasks) -> $"{at:O}=[{formatTaskCounts tasks}]")
        |> Option.defaultValue "<none>"

    let events =
        sources.Events
        |> List.filter (fun row -> row.Ts >= boundary - radius && row.Ts <= boundary + resolution)
        |> List.truncate 8
        |> List.map (fun row ->
            let skill = row.Skill |> Option.defaultValue "-"
            $"{row.Ts:O}:{SessionId.value row.SessionId}:{row.Status}:{skill}")
        |> String.concat "|"

    let liveness =
        sources.Liveness
        |> List.filter (fun (_, at) -> at >= boundary - radius && at <= boundary + resolution)
        |> List.truncate 8
        |> List.map (fun (sessionId, at) -> $"{at:O}:{SessionId.value sessionId}")
        |> String.concat "|"

    $"latestTasks={latestTasks}; events={events}; liveness={liveness}"

let private firstMismatch expected actual =
    let rec find index expected actual =
        match expected, actual with
        | [], [] -> None
        | expectedRow :: expectedRest, actualRow :: actualRest when expectedRow = actualRow ->
            find (index + 1) expectedRest actualRest
        | expectedRow :: _, actualRow :: _ ->
            Some(index, Some expectedRow, Some actualRow)
        | expectedRow :: _, [] ->
            Some(index, Some expectedRow, None)
        | [], actualRow :: _ ->
            Some(index, None, Some actualRow)

    find 0 expected actual

let private assertRows seed label partitions sources expected actual =
    match firstMismatch expected actual with
    | None -> ()
    | Some(index, expectedRow, actualRow) ->
        let boundary =
            expectedRow
            |> Option.orElse actualRow
            |> Option.map _.Boundary
            |> Option.defaultValue DateTimeOffset.MinValue

        let partitionText = partitions |> List.map string |> String.concat ","

        Assert.Fail(
            $"seed={seed}; {label}; partitions=[{partitionText}]; row={index}; boundary={boundary:O}; expected={formatRow expectedRow}; actual={formatRow actualRow}; {sourceContext sources boundary}"
        )

let private snapshotAt
    (boundary: DateTimeOffset)
    (snapshots: OverviewSnapshot list)
    =
    snapshots
    |> List.takeWhile (fun snapshot -> snapshot.Timestamp <= boundary)
    |> List.last

let private denseOracle
    (startBoundary: DateTimeOffset)
    (endBoundary: DateTimeOffset)
    (sources: Sources)
    : RollupRow list =
    // The sampler's 288 intervals are exactly 30 seconds across 144 minutes, making each call a
    // dense oracle chunk while preserving its independent correctness path.
    let oracleWindow =
        TimeSpan.FromTicks(resolution.Ticks * int64 OverviewHistory.sampleBucketCount)

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

let private partitionSizes (seed: int) (total: int) =
    let rec build state index remaining acc =
        if remaining = 0 then
            List.rev acc
        else
            let drawn, state = nextInt maxBatchBoundaryCount state

            let requested =
                match index % 4 with
                | 0 -> maxBatchBoundaryCount
                | 1 -> maxBatchBoundaryCount - 1
                | _ -> drawn + 1

            let size = min remaining requested
            build state (index + 1) (remaining - size) (size :: acc)

    build (int64 seed + 1L) 0 total []

let private requireStaged seed (candidate: RollupCandidate) =
    function
    | StagingResult.Staged -> ()
    | StagingResult.SourceGenerationChanged current ->
        raise (
            AssertionException(
                $"seed={seed}; staging generation {candidate.Generation} changed unexpectedly to {current}."
            )
        )

let private requirePublished seed (candidate: RollupCandidate) =
    function
    | PublicationResult.Published state -> state
    | PublicationResult.SourceGenerationChanged current ->
        raise (
            AssertionException(
                $"seed={seed}; publication generation {candidate.Generation} changed unexpectedly to {current}."
            )
        )

let private reconstructStageAndPublish
    seed
    (store: SessionActivityStore)
    (sources: Sources)
    (startBoundary: DateTimeOffset)
    (endBoundary: DateTimeOffset)
    =
    let generation = store.OverviewRollupState().SourceGeneration
    let expected = denseOracle startBoundary endBoundary sources
    let partitions = partitionSizes seed expected.Length

    let rec stage offset (startBoundary: DateTimeOffset) remaining =
        match remaining with
        | [] -> ()
        | size :: rest ->
            let endBoundary =
                startBoundary.AddTicks(resolution.Ticks * int64 (size - 1))

            let actual = reconstructRange store startBoundary endBoundary
            let expectedBatch = expected[offset .. offset + size - 1]
            assertRows seed $"reconstructed batch {offset}" partitions sources expectedBatch actual

            let candidate =
                { Generation = generation
                  StartBoundary = startBoundary
                  EndBoundary = endBoundary }

            actual
            |> List.map (fun row ->
                { Generation = generation
                  Row = row })
            |> fun rows -> store.StageOverviewRollup(candidate, rows)
            |> requireStaged seed candidate

            stage (offset + size) (endBoundary + resolution) rest

    stage 0 startBoundary partitions

    let candidate =
        { Generation = generation
          StartBoundary = startBoundary
          EndBoundary = endBoundary }

    let state =
        store.PublishOverviewRollup candidate
        |> requirePublished seed candidate

    let _, published =
        store.ReadPublishedOverviewRollup(startBoundary, endBoundary)

    assertRows seed "published dense range" partitions sources expected published
    state, published, partitions

let private persistScenario
    seed
    path
    (store: SessionActivityStore)
    (sources: Sources)
    (anchor: DateTimeOffset)
    =
    sources.Tasks
    |> List.iter (fun (at, tasks) ->
        Assert.That(
            store.AppendTaskSnapshotIfChanged(at, tasks),
            Is.True,
            $"seed={seed}; task change at {at:O} was not appended."
        )

        let afterInsert = store.OverviewRollupState()

        Assert.That(
            store.AppendTaskSnapshotIfChanged(at, tasks),
            Is.False,
            $"seed={seed}; duplicate task change at {at:O} was not a no-op."
        )

        Assert.That(
            store.OverviewRollupState(),
            Is.EqualTo afterInsert,
            $"seed={seed}; duplicate task change at {at:O} changed rollup metadata."
        ))

    sources.Events
    |> List.iter (fun row ->
        Assert.That(
            store.AppendEvent row,
            Is.True,
            $"seed={seed}; event {EventId.value row.EventId} was not appended."
        )

        let afterInsert = store.OverviewRollupState()

        Assert.That(
            store.AppendEvent row,
            Is.False,
            $"seed={seed}; duplicate event {EventId.value row.EventId} was not a no-op."
        )

        Assert.That(
            store.OverviewRollupState(),
            Is.EqualTo afterInsert,
            $"seed={seed}; duplicate event {EventId.value row.EventId} changed rollup metadata."
        ))

    let beforeUnknownLiveness = store.OverviewRollupState()
    store.RecordLiveness(SessionId $"api-unknown-{seed}", anchor)

    Assert.That(
        store.OverviewRollupState(),
        Is.EqualTo beforeUnknownLiveness,
        $"seed={seed}; unknown liveness changed rollup metadata."
    )

    insertLiveness path sources.Liveness
    store.RebuildOverviewRollupObservationBounds()

let private sampledBoundaries
    (anchor: DateTimeOffset)
    (window: HistoryWindow)
    =
    let duration = HistoryWindow.duration window
    let start = anchor - duration

    [ 0 .. OverviewHistory.sampleBucketCount ]
    |> List.map (fun bucket ->
        if bucket = OverviewHistory.sampleBucketCount then anchor
        else
            start.AddTicks(
                duration.Ticks
                * int64 bucket
                / int64 OverviewHistory.sampleBucketCount
            ))

let private assertSupportedWindows
    seed
    partitions
    (anchor: DateTimeOffset)
    (sources: Sources)
    (published: RollupRow list)
    =
    let publishedByBoundary =
        published
        |> List.map (fun row -> row.Boundary, row)
        |> Map.ofList

    [ HistoryWindow.Hours12
      HistoryWindow.Hours24
      HistoryWindow.Hours72 ]
    |> List.iter (fun window ->
        let duration = HistoryWindow.duration window

        let oracle =
            OverviewHistory.sample
                anchor
                duration
                sources.Tasks
                sources.Events
                sources.Liveness

        let expected =
            sampledBoundaries anchor window
            |> List.map (fun boundary ->
                let snapshot = snapshotAt boundary oracle

                { Boundary = boundary
                  Tasks = snapshot.Tasks
                  Agents = snapshot.Agents })

        let actual =
            sampledBoundaries anchor window
            |> List.map (fun boundary -> Map.find boundary publishedByBoundary)

        assertRows seed $"supported window {window}" partitions sources expected actual)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryRollupOracleTests() =

    [<TestCase(104729)>]
    [<TestCase(130363)>]
    member _.``randomized reconstructed and published rows match the pure sampler``(seed: int) =
        withStore (fun path store ->
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let startBoundary = oldestRetainedBoundary anchor
            let sources = scenario seed anchor
            persistScenario seed path store sources anchor

            let state, published, partitions =
                reconstructStageAndPublish seed store sources startBoundary anchor

            Assert.Multiple(fun () ->
                Assert.That(partitions |> List.max, Is.EqualTo maxBatchBoundaryCount)
                Assert.That(partitions |> List.forall (fun size -> size <= maxBatchBoundaryCount), Is.True)
                Assert.That(
                    partitionSizes 104729 published.Length,
                    Is.Not.EqualTo(partitionSizes 130363 published.Length)
                )
                Assert.That(state.CompleteThrough, Is.EqualTo(Some anchor))
                Assert.That(state.PublishedGeneration, Is.EqualTo state.SourceGeneration)
                Assert.That(state.EarliestDirty, Is.EqualTo None))

            assertSupportedWindows seed partitions anchor sources published)

    [<Test>]
    member _.``exact ceiling and clamped repairs match the oracle at every exposed boundary``() =
        withStore (fun _ store ->
            let seed = 8675309
            let anchor = latestCompleteBoundary DateTimeOffset.UtcNow
            let retainedStart = oldestRetainedBoundary anchor
            let emptySources =
                { Tasks = []
                  Events = []
                  Liveness = [] }

            reconstructStageAndPublish seed store emptySources retainedStart anchor
            |> ignore

            let exactAt = anchor.AddMinutes(-1.0)
            let exactEvent =
                evt "dirty-exact" "dirty-exact" "dirty-worktree-a" SessionLevelStatus.Working (Some "bd-execute") exactAt

            Assert.That(store.AppendEvent exactEvent, Is.True)
            let exactState = store.OverviewRollupState()
            Assert.That(exactState.EarliestDirty, Is.EqualTo(Some exactAt))
            Assert.That(store.AppendEvent exactEvent, Is.False)
            Assert.That(store.OverviewRollupState(), Is.EqualTo exactState)

            let exactSources =
                { emptySources with
                    Events = [ exactEvent ] }

            reconstructStageAndPublish seed store exactSources exactAt anchor
            |> ignore

            let betweenAt = anchor.AddSeconds(-30.0).AddTicks 1L
            let betweenEvent =
                evt "dirty-between" "dirty-between" "dirty-worktree-b" SessionLevelStatus.Idle None betweenAt

            Assert.That(store.AppendEvent betweenEvent, Is.True)
            Assert.That(store.OverviewRollupState().EarliestDirty, Is.EqualTo(Some anchor))

            let betweenSources =
                { exactSources with
                    Events = [ exactEvent; betweenEvent ] }

            reconstructStageAndPublish seed store betweenSources anchor anchor
            |> ignore

            let oldAt = anchor.AddHours(-100.0)
            let lateTasks = [ tc TaskBucketKind.Blocked 7 ]
            let beforeWrite = latestCompleteBoundary DateTimeOffset.UtcNow
            Assert.That(store.AppendTaskSnapshotIfChanged(oldAt, lateTasks), Is.True)
            let afterWrite = latestCompleteBoundary DateTimeOffset.UtcNow
            let lateState = store.OverviewRollupState()
            let dirty = lateState.EarliestDirty |> Option.get

            Assert.That(
                [ oldestExposedBoundary beforeWrite
                  oldestExposedBoundary afterWrite ],
                Does.Contain dirty
            )

            Assert.That(store.AppendTaskSnapshotIfChanged(oldAt, lateTasks), Is.False)
            Assert.That(store.OverviewRollupState(), Is.EqualTo lateState)

            let repairAnchor = max anchor afterWrite

            let lateSources =
                { betweenSources with
                    Tasks = [ oldAt, lateTasks ] }

            reconstructStageAndPublish seed store lateSources dirty repairAnchor
            |> ignore

            let _, predecessor =
                store.ReadPublishedOverviewRollup(dirty - resolution, dirty - resolution)

            Assert.That(
                predecessor |> List.tryHead |> Option.map _.Tasks |> Option.defaultValue [],
                Is.Empty,
                "The retained predecessor is outside the clamped exposed repair."
            ))
