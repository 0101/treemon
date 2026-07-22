module Tests.OverviewHistoryPerformanceTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Newtonsoft.Json
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewHistoryRollup
open Server.OverviewHistoryRollupWorker
open Server.SessionActivityStore
open Server.SqliteStorage

[<Literal>]
let private sessionCount = 130

[<Literal>]
let private uncachedSampleCount = 7

[<Literal>]
let private cachedSampleCount = 20

[<Literal>]
let private concurrentCallerCount = 16

[<Literal>]
let private maximumUncachedMilliseconds = 1_000.0

[<Literal>]
let private maximumCachedMilliseconds = 100.0

[<Literal>]
let private maximumVolumeElapsedDeltaMilliseconds = 100.0

[<Literal>]
let private maximumVolumeAllocationDeltaBytes = 1_000_000L

[<Literal>]
let private maximumPayloadBytes = 250_000

let private anchor =
    DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)

let private rawEventVolumes = [ 40_000; 400_000 ]

let private converter = Fable.Remoting.Json.FableJsonConverter()

type private SeedEvent =
    { EventId: string
      SessionId: string
      WorktreePath: string
      Status: string
      Skill: string option
      Timestamp: DateTimeOffset }

type RequestMeasurement =
    { ElapsedMilliseconds: float
      AllocatedBytes: int64
      SnapshotCount: int
      SnapshotsOrdered: bool
      PayloadBytes: int }

type TableAccess =
    { Table: string
      StatementCount: int }

type SqlAudit =
    { CacheMissStatements: string list
      CacheMissTableAccess: TableAccess list
      CacheMissRollupReads: int
      CacheHitStatements: string list
      CacheHitTableAccess: TableAccess list
      CacheHitRollupReads: int
      RawSourceRowsRead: int }

type VolumeMeasurements =
    { RawEvents: int
      Sessions: int
      LivenessRows: int
      TaskChanges: int
      PublishedRollupRows: int
      DatabaseBytes: int64
      SeedMilliseconds: float
      BackfillMilliseconds: float
      PublishedGeneration: int64
      CompleteThrough: DateTimeOffset
      UncachedSamples: RequestMeasurement list
      UncachedMedianMilliseconds: float
      UncachedMedianAllocatedBytes: int64
      CachedSamples: RequestMeasurement list
      CachedMaximumMilliseconds: float
      ConcurrentRollupReads: int
      ConcurrentResponsesEqual: bool
      SqlAudit: SqlAudit }

type HarnessConfiguration =
    { BuildConfiguration: string
      Framework: string
      OperatingSystem: string
      ProcessArchitecture: string
      SQLiteVersion: string
      DatabaseMode: string
      Anchor: DateTimeOffset
      Window: string
      ResolutionSeconds: int
      Sessions: int
      RawEventVolumes: int list
      LivenessRows: int
      TaskChanges: int
      UncachedSamples: int
      CachedSamples: int
      ConcurrentCallers: int
      Serialization: string
      AllocationCounter: string
      StatementAudit: string }

type Thresholds =
    { UncachedMilliseconds: float
      CachedMilliseconds: float
      PayloadBytes: int
      MaximumSnapshots: int
      VolumeElapsedDeltaMilliseconds: float
      VolumeAllocationDeltaBytes: int64
      ConcurrentRollupReads: int
      RawSourceRowsRead: int }

type HarnessReport =
    { Configuration: HarnessConfiguration
      Thresholds: Thresholds
      PublishedRollupsEqual: bool
      Volumes: VolumeMeasurements list
      LargeMinusSmallMedianMilliseconds: float
      LargeMinusSmallMedianAllocatedBytes: int64 }

type private PreparedVolume =
    { RawEvents: int
      Path: string
      Store: SessionActivityStore
      Sessions: int
      SeedMilliseconds: float
      BackfillMilliseconds: float
      LivenessRows: int
      TaskChanges: int
      PublishedRollupRows: int
      DatabaseBytes: int64
      PublishedGeneration: int64
      CompleteThrough: DateTimeOffset
      SQLiteVersion: string }

let private selectedBoundaries =
    selectedBoundaries HistoryWindow.Hours72 anchor

let private worktreePath sessionIndex =
    Path.Combine(Path.GetTempPath(), "treemon-overview-performance", $"worktree-{sessionIndex:D3}")

let private taskCounts index : TaskCount list =
    [ { Kind = TaskBucketKind.Planned
        Count = index % 17 + 1 }
      { Kind = TaskBucketKind.Queued
        Count = index % 13 + 2 }
      { Kind = TaskBucketKind.InProgress
        Count = index % 11 + 3 }
      { Kind = TaskBucketKind.Blocked
        Count = index % 7 + 1 }
      { Kind = TaskBucketKind.Done
        Count = index % 23 + 5 }
      { Kind = TaskBucketKind.Unattended
        Count = index % 5 + 1 } ]

let private statusAndSkill index =
    let skills =
        [| "investigate"
           "bd-plan"
           "bd-execute"
           "review"
           "pr"
           "unrecognized-work" |]

    match index % 3 with
    | 0 -> "working", Some skills[index % skills.Length]
    | 1 -> "waiting_for_user", None
    | _ -> "idle", None

let private visibleEvents =
    let baselines =
        Seq.init sessionCount (fun sessionIndex ->
            { EventId = $"visible-baseline-{sessionIndex:D3}"
              SessionId = $"session-{sessionIndex:D3}"
              WorktreePath = worktreePath sessionIndex
              Status = "idle"
              Skill = None
              Timestamp = oldestRetainedBoundary anchor })

    let changes =
        selectedBoundaries
        |> Seq.mapi (fun index boundary ->
            let sessionIndex = index % sessionCount
            let status, skill = statusAndSkill index

            { EventId = $"visible-change-{index:D3}"
              SessionId = $"session-{sessionIndex:D3}"
              WorktreePath = worktreePath sessionIndex
              Status = status
              Skill = skill
              Timestamp = boundary })

    Seq.append baselines changes

let private fillerEvents rawEventCount =
    let visibleCount = sessionCount + selectedBoundaries.Length
    let fillerCount = rawEventCount - visibleCount
    let oldestRetained = oldestRetainedBoundary anchor
    let fillerStart = oldestRetained - TimeSpan.FromDays 30.0
    let fillerEnd = oldestRetained - TimeSpan.FromHours 1.0
    let stepTicks = (fillerEnd - fillerStart).Ticks / int64 fillerCount

    Seq.init fillerCount (fun index ->
        let sessionIndex = index % sessionCount

        { EventId = $"filler-{rawEventCount}-{index:D6}"
          SessionId = $"session-{sessionIndex:D3}"
          WorktreePath = worktreePath sessionIndex
          Status = if index % 2 = 0 then "working" else "idle"
          Skill = if index % 2 = 0 then Some "bd-execute" else None
          Timestamp = fillerStart + TimeSpan.FromTicks(stepTicks * int64 index) })

let private openConnection path =
    let connection =
        new SqliteConnection(
            SqliteConnectionStringBuilder(DataSource = path, Pooling = false).ConnectionString
        )

    connection.Open()
    connection

let private insertEvents
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    rawEventCount
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <-
        """
INSERT INTO activity_events
    (event_id, session_id, worktree_path, provider, kind, status, skill, ts)
VALUES
    ($eventId, $sessionId, $worktreePath, 'copilot_cli', 'status', $status, $skill, $ts);
"""

    let eventId = command.Parameters.Add("$eventId", SqliteType.Text)
    let sessionId = command.Parameters.Add("$sessionId", SqliteType.Text)
    let path = command.Parameters.Add("$worktreePath", SqliteType.Text)
    let status = command.Parameters.Add("$status", SqliteType.Text)
    let skill = command.Parameters.Add("$skill", SqliteType.Text)
    let timestamp = command.Parameters.Add("$ts", SqliteType.Text)
    command.Prepare()

    let insert row =
        eventId.Value <- row.EventId
        sessionId.Value <- row.SessionId
        path.Value <- row.WorktreePath
        status.Value <- row.Status
        skill.Value <- row.Skill |> Option.map box |> Option.defaultValue (box DBNull.Value)
        timestamp.Value <- isoUtc row.Timestamp
        command.ExecuteNonQuery() |> ignore

    Seq.append (fillerEvents rawEventCount) visibleEvents
    |> Seq.iter insert

let private insertLiveness
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <-
        """
INSERT INTO session_liveness (session_id, ts)
VALUES ($sessionId, $ts);
"""

    let sessionId = command.Parameters.Add("$sessionId", SqliteType.Text)
    let timestamp = command.Parameters.Add("$ts", SqliteType.Text)
    command.Prepare()

    selectedBoundaries
    |> List.mapi (fun index boundary ->
        let observedAt =
            if boundary = anchor then anchor
            else boundary + TimeSpan.FromMinutes 1.0

        $"session-{index % sessionCount:D3}", observedAt)
    |> List.iter (fun (session, observedAt) ->
        sessionId.Value <- session
        timestamp.Value <- isoUtc observedAt
        command.ExecuteNonQuery() |> ignore)

let private insertTaskChanges
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <-
        """
INSERT INTO task_snapshots (ts, tasks)
VALUES ($ts, $tasks);
"""

    let timestamp = command.Parameters.Add("$ts", SqliteType.Text)
    let tasks = command.Parameters.Add("$tasks", SqliteType.Text)
    command.Prepare()

    selectedBoundaries
    |> List.mapi (fun index boundary ->
        boundary, OverviewHistoryRollup.serializeTasks (taskCounts index))
    |> List.iter (fun (boundary, serializedTasks) ->
        timestamp.Value <- isoUtc boundary
        tasks.Value <- serializedTasks
        command.ExecuteNonQuery() |> ignore)

let private seedDatabase path rawEventCount =
    use connection = openConnection path
    use pragma = connection.CreateCommand()
    pragma.CommandText <- "PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY;"
    pragma.ExecuteNonQuery() |> ignore
    use transaction = connection.BeginTransaction()
    insertEvents connection transaction rawEventCount
    insertLiveness connection transaction
    insertTaskChanges connection transaction

    use generation = connection.CreateCommand()
    generation.Transaction <- transaction
    generation.CommandText <-
        """
UPDATE overview_history_state
SET source_generation = $generation,
    earliest_dirty = $dirty
WHERE id = 1;
"""
    generation.Parameters.AddWithValue(
        "$generation",
        int64 rawEventCount + int64 selectedBoundaries.Length * 2L
    )
    |> ignore
    generation.Parameters.AddWithValue(
        "$dirty",
        toBucket (oldestExposedBoundary anchor)
    )
    |> ignore
    generation.ExecuteNonQuery() |> ignore
    transaction.Commit()

let private fixedClock : RollupWorkerClock =
    { UtcNow = fun () -> anchor
      WaitUntil = fun _ _ -> async.Return() }

let private noHooks : RollupWorkerHooks =
    { BeforeStage = ignore
      BeforePublish = ignore }

let private scalar<'T> path sql =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteScalar() |> unbox<'T>

let private checkpoint path =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- "PRAGMA wal_checkpoint(TRUNCATE);"
    command.ExecuteNonQuery() |> ignore

let private fileBytes path =
    [ path; path + "-wal"; path + "-shm" ]
    |> List.sumBy (fun file ->
        if File.Exists file then FileInfo(file).Length else 0L)

let private prepareVolume root rawEventCount =
    let path = Path.Combine(root, $"overview-{rawEventCount}.db")
    let store = new SessionActivityStore(path)

    try
        let seedWatch = Stopwatch.StartNew()
        seedDatabase path rawEventCount
        seedWatch.Stop()

        let backfillWatch = Stopwatch.StartNew()

        use worker =
            new OverviewHistoryRollupWorker(
                store,
                fixedClock,
                noHooks,
                fun ex -> raise ex
            )

        let state = worker.Backfill CancellationToken.None
        backfillWatch.Stop()
        checkpoint path

        { RawEvents = scalar<int64> path "SELECT count(*) FROM activity_events;" |> int
          Path = path
          Store = store
          Sessions =
            scalar<int64> path "SELECT count(DISTINCT session_id) FROM activity_events;"
            |> int
          SeedMilliseconds = seedWatch.Elapsed.TotalMilliseconds
          BackfillMilliseconds = backfillWatch.Elapsed.TotalMilliseconds
          LivenessRows = scalar<int64> path "SELECT count(*) FROM session_liveness;" |> int
          TaskChanges = scalar<int64> path "SELECT count(*) FROM task_snapshots;" |> int
          PublishedRollupRows =
            scalar<int64> path "SELECT count(*) FROM overview_history_rows;" |> int
          DatabaseBytes = fileBytes path
          PublishedGeneration = state.PublishedGeneration
          CompleteThrough =
            state.CompleteThrough
            |> Option.defaultWith (fun () ->
                failwith "Performance harness backfill did not publish a complete-through boundary.")
          SQLiteVersion = scalar<string> path "SELECT sqlite_version();" }
    with _ ->
        (store :> IDisposable).Dispose()
        reraise ()

let private forceFullCollection () =
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true)
    GC.WaitForPendingFinalizers()
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true)

let private snapshotsOrdered response =
    response.Snapshots
    |> List.pairwise
    |> List.forall (fun (left, right) -> left.Timestamp < right.Timestamp)

let private runSerializedRequest cache store =
    let response =
        WorktreeApi.overviewHistoryCached
            cache
            store
            HistoryWindow.Hours72
        |> Async.RunSynchronously

    let payload =
        JsonConvert.SerializeObject(response, Formatting.None, converter)

    response, Encoding.UTF8.GetByteCount payload

let private measureRequest cache store =
    forceFullCollection ()
    let allocatedBefore = GC.GetTotalAllocatedBytes true
    let watch = Stopwatch.StartNew()
    let response, payloadBytes = runSerializedRequest cache store
    watch.Stop()
    let allocatedAfter = GC.GetTotalAllocatedBytes true

    { ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
      AllocatedBytes = allocatedAfter - allocatedBefore
      SnapshotCount = response.Snapshots.Length
      SnapshotsOrdered = snapshotsOrdered response
      PayloadBytes = payloadBytes }

let private median values =
    values |> List.sort |> fun sorted -> sorted[sorted.Length / 2]

let private normalizeSql (statement: string) =
    Regex.Replace(statement.Trim(), @"\s+", " ")

let private tableAccess (statements: string list) =
    [ "overview_history_state"
      "overview_history_rows"
      "overview_history_staging"
      "overview_history_session_bounds"
      "activity_events"
      "session_liveness"
      "task_snapshots"
      "session_status" ]
    |> List.choose (fun table ->
        let count =
            statements
            |> List.sumBy (fun statement ->
                if statement.Contains(table, StringComparison.OrdinalIgnoreCase) then 1 else 0)

        if count = 0 then None
        else
            Some
                { Table = table
                  StatementCount = count })

let private auditSql path =
    let statements = ConcurrentQueue<string>()

    let trace =
        SQLitePCL.strdelegate_trace(fun _ statement ->
            if not (String.IsNullOrWhiteSpace statement) then
                statements.Enqueue(normalizeSql statement))

    let connectionOpened (connection: SqliteConnection) =
        SQLitePCL.raw.sqlite3_trace(connection.Handle, trace, box statements)

    use store = new SessionActivityStore(path, connectionOpened = connectionOpened)
    let cache = OverviewHistoryCache.create ()
    // The callback crosses concurrent cache tasks, so the counter must use atomic mutation.
    let mutable rollupReads = 0
    statements.Clear()

    WorktreeApi.overviewHistoryCachedWith
        (fun () -> Interlocked.Increment(&rollupReads) |> ignore)
        cache
        store
        HistoryWindow.Hours72
    |> Async.RunSynchronously
    |> ignore

    let missStatements = statements.ToArray() |> Array.toList
    let readsAfterMiss = rollupReads
    statements.Clear()

    WorktreeApi.overviewHistoryCachedWith
        (fun () -> Interlocked.Increment(&rollupReads) |> ignore)
        cache
        store
        HistoryWindow.Hours72
    |> Async.RunSynchronously
    |> ignore

    let hitStatements = statements.ToArray() |> Array.toList
    let sourceTables =
        Set.ofList
            [ "activity_events"
              "session_liveness"
              "task_snapshots" ]

    let rawAccesses =
        tableAccess (missStatements @ hitStatements)
        |> List.filter (fun access -> Set.contains access.Table sourceTables)

    { CacheMissStatements = missStatements
      CacheMissTableAccess = tableAccess missStatements
      CacheMissRollupReads = readsAfterMiss
      CacheHitStatements = hitStatements
      CacheHitTableAccess = tableAccess hitStatements
      CacheHitRollupReads = rollupReads - readsAfterMiss
      RawSourceRowsRead = if rawAccesses.IsEmpty then 0 else -1 }

let private concurrentRollupReadCount store =
    let cache = OverviewHistoryCache.create ()
    // Concurrent callbacks require one shared atomic invocation counter.
    let mutable rollupReads = 0

    let requests =
        [ 1..concurrentCallerCount ]
        |> List.map (fun _ ->
            WorktreeApi.overviewHistoryCachedWith
                (fun () -> Interlocked.Increment(&rollupReads) |> ignore)
                cache
                store
                HistoryWindow.Hours72
            |> Async.StartAsTask)

    let responses = Task.WhenAll requests |> Async.AwaitTask |> Async.RunSynchronously
    rollupReads, (responses |> Array.distinct |> Array.length = 1)

let private measureVolume (prepared: PreparedVolume) : VolumeMeasurements =
    runSerializedRequest
        (OverviewHistoryCache.create ())
        prepared.Store
    |> ignore

    let uncached =
        List.init uncachedSampleCount (fun _ ->
            measureRequest
                (OverviewHistoryCache.create ())
                prepared.Store)

    let cachedCache = OverviewHistoryCache.create ()
    runSerializedRequest cachedCache prepared.Store |> ignore

    let cached =
        List.init cachedSampleCount (fun _ ->
            measureRequest cachedCache prepared.Store)

    let concurrentReads, responsesEqual =
        concurrentRollupReadCount prepared.Store

    { RawEvents = prepared.RawEvents
      Sessions = prepared.Sessions
      LivenessRows = prepared.LivenessRows
      TaskChanges = prepared.TaskChanges
      PublishedRollupRows = prepared.PublishedRollupRows
      DatabaseBytes = prepared.DatabaseBytes
      SeedMilliseconds = prepared.SeedMilliseconds
      BackfillMilliseconds = prepared.BackfillMilliseconds
      PublishedGeneration = prepared.PublishedGeneration
      CompleteThrough = prepared.CompleteThrough
      UncachedSamples = uncached
      UncachedMedianMilliseconds =
        uncached |> List.map _.ElapsedMilliseconds |> median
      UncachedMedianAllocatedBytes =
        uncached |> List.map _.AllocatedBytes |> median
      CachedSamples = cached
      CachedMaximumMilliseconds =
        cached |> List.map _.ElapsedMilliseconds |> List.max
      ConcurrentRollupReads = concurrentReads
      ConcurrentResponsesEqual = responsesEqual
      SqlAudit = auditSql prepared.Path }

let private assertVolume expectedRawEvents (volume: VolumeMeasurements) =
    Assert.Multiple(fun () ->
        Assert.That(volume.RawEvents, Is.EqualTo expectedRawEvents)
        Assert.That(volume.Sessions, Is.GreaterThanOrEqualTo 130)
        Assert.That(volume.LivenessRows, Is.GreaterThan 0)
        Assert.That(volume.TaskChanges, Is.GreaterThan 0)
        Assert.That(volume.PublishedRollupRows, Is.EqualTo(72 * 60 * 2 + 2))
        Assert.That(volume.CompleteThrough, Is.EqualTo anchor)
        Assert.That(volume.UncachedMedianMilliseconds, Is.LessThan maximumUncachedMilliseconds)
        Assert.That(
            volume.UncachedSamples
            |> List.forall (fun sample ->
                sample.ElapsedMilliseconds < maximumUncachedMilliseconds),
            Is.True
        )
        Assert.That(volume.CachedMaximumMilliseconds, Is.LessThan maximumCachedMilliseconds)
        Assert.That(
            volume.UncachedSamples |> List.forall _.SnapshotsOrdered,
            Is.True
        )
        Assert.That(
            volume.UncachedSamples
            |> List.forall (fun sample -> sample.SnapshotCount <= sampleIntervalCount + 1),
            Is.True
        )
        Assert.That(
            volume.UncachedSamples
            |> List.forall (fun sample -> sample.PayloadBytes < maximumPayloadBytes),
            Is.True
        )
        Assert.That(volume.SqlAudit.CacheMissRollupReads, Is.EqualTo 1)
        Assert.That(volume.SqlAudit.CacheHitRollupReads, Is.Zero)
        Assert.That(volume.SqlAudit.RawSourceRowsRead, Is.Zero)
        Assert.That(volume.ConcurrentRollupReads, Is.EqualTo 1)
        Assert.That(volume.ConcurrentResponsesEqual, Is.True))

[<TestFixture>]
[<Category("Performance")>]
[<NonParallelizable>]
type OverviewHistoryPerformanceTests() =

    [<Test>]
    member _.``release SQLite API serialization stays bounded across raw volumes``() =
#if DEBUG
        Assert.Ignore(
            "Performance harness requires Release mode: dotnet test src/Tests/Tests.fsproj -c Release --filter Category=Performance"
        )
#else
        let root =
            Path.Combine(
                Path.GetTempPath(),
                $"treemon-overview-performance-{Guid.NewGuid()}"
            )

        Directory.CreateDirectory root |> ignore

        try
            let prepared =
                rawEventVolumes
                |> List.map (prepareVolume root)

            try
                let smallRows =
                    prepared[0].Store.ReadPublishedOverviewRollup(
                        oldestRetainedBoundary anchor,
                        anchor
                    )
                    |> snd

                let largeRows =
                    prepared[1].Store.ReadPublishedOverviewRollup(
                        oldestRetainedBoundary anchor,
                        anchor
                    )
                    |> snd

                let publishedRollupsEqual = smallRows = largeRows
                let volumes = prepared |> List.map measureVolume
                let small = volumes[0]
                let large = volumes[1]
                let elapsedDelta =
                    large.UncachedMedianMilliseconds
                    - small.UncachedMedianMilliseconds
                let allocationDelta =
                    large.UncachedMedianAllocatedBytes
                    - small.UncachedMedianAllocatedBytes

                let report =
                    { Configuration =
                        { BuildConfiguration = "Release"
                          Framework = RuntimeInformation.FrameworkDescription
                          OperatingSystem = RuntimeInformation.OSDescription
                          ProcessArchitecture = string RuntimeInformation.ProcessArchitecture
                          SQLiteVersion = prepared.Head.SQLiteVersion
                          DatabaseMode =
                            "on-disk SQLite WAL; pooling=false; warm OS cache; fresh API cache per uncached sample"
                          Anchor = anchor
                          Window = "Hours72"
                          ResolutionSeconds = resolutionSeconds
                          Sessions = sessionCount
                          RawEventVolumes = rawEventVolumes
                          LivenessRows = selectedBoundaries.Length
                          TaskChanges = selectedBoundaries.Length
                          UncachedSamples = uncachedSampleCount
                          CachedSamples = cachedSampleCount
                          ConcurrentCallers = concurrentCallerCount
                          Serialization =
                            "Newtonsoft.Json + FableJsonConverter; UTF-8 payload byte count"
                          AllocationCounter =
                            "GC.GetTotalAllocatedBytes(precise=true), full GC before each sample"
                          StatementAudit =
                            "separate sqlite3_trace cache miss/hit; timed samples have no trace or rollup-read callback" }
                      Thresholds =
                        { UncachedMilliseconds = maximumUncachedMilliseconds
                          CachedMilliseconds = maximumCachedMilliseconds
                          PayloadBytes = maximumPayloadBytes
                          MaximumSnapshots = sampleIntervalCount + 1
                          VolumeElapsedDeltaMilliseconds =
                            maximumVolumeElapsedDeltaMilliseconds
                          VolumeAllocationDeltaBytes = maximumVolumeAllocationDeltaBytes
                          ConcurrentRollupReads = 1
                          RawSourceRowsRead = 0 }
                      PublishedRollupsEqual = publishedRollupsEqual
                      Volumes = volumes
                      LargeMinusSmallMedianMilliseconds = elapsedDelta
                      LargeMinusSmallMedianAllocatedBytes = allocationDelta }

                TestContext.Progress.WriteLine(
                    JsonConvert.SerializeObject(
                        report,
                        Formatting.Indented
                    )
                )

                List.zip rawEventVolumes volumes
                |> List.iter (fun (expectedRawEvents, volume) ->
                    assertVolume expectedRawEvents volume)

                Assert.Multiple(fun () ->
                    Assert.That(publishedRollupsEqual, Is.True)
                    Assert.That(
                        elapsedDelta,
                        Is.LessThanOrEqualTo maximumVolumeElapsedDeltaMilliseconds
                    )
                    Assert.That(
                        allocationDelta,
                        Is.LessThanOrEqualTo maximumVolumeAllocationDeltaBytes
                    ))
            finally
                prepared
                |> List.iter (fun volume ->
                    (volume.Store :> IDisposable).Dispose())
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
#endif
