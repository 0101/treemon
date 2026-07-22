module Server.OverviewHistoryRollup

open System
open System.IO
open Microsoft.Data.Sqlite
open OverviewData

[<Literal>]
let schemaVersion = 1

[<Literal>]
let resolutionSeconds = 30

let resolution = TimeSpan.FromSeconds(int64 resolutionSeconds)
let exposedHorizon = HistoryWindow.duration HistoryWindow.Hours72
let predecessorRetention = resolution

type RollupRow =
    { Boundary: DateTimeOffset
      Tasks: TaskCount list
      Agents: AgentCount list }

type StagedRollupRow =
    { Generation: int64
      Row: RollupRow }

type RollupCandidate =
    { Generation: int64
      StartBoundary: DateTimeOffset
      EndBoundary: DateTimeOffset }

type PublicationState =
    { SchemaVersion: int
      ResolutionSeconds: int
      SourceGeneration: int64
      PublishedGeneration: int64
      CompleteThrough: DateTimeOffset option
      EarliestDirty: DateTimeOffset option }

[<RequireQualifiedAccess>]
type StagingResult =
    | Staged
    | SourceGenerationChanged of CurrentGeneration: int64

[<RequireQualifiedAccess>]
type PublicationResult =
    | Published of PublicationState
    | SourceGenerationChanged of CurrentGeneration: int64

let private countConverter: Newtonsoft.Json.JsonConverter =
    Fable.Remoting.Json.FableJsonConverter()

let internal serializeTasks (tasks: TaskCount list) =
    Newtonsoft.Json.JsonConvert.SerializeObject(
        tasks,
        Newtonsoft.Json.Formatting.None,
        [| countConverter |]
    )

let internal parseTasks (value: string) =
    Newtonsoft.Json.JsonConvert.DeserializeObject<TaskCount list>(
        value,
        [| countConverter |]
    )

let private serializeAgents (agents: AgentCount list) =
    Newtonsoft.Json.JsonConvert.SerializeObject(
        agents,
        Newtonsoft.Json.Formatting.None,
        [| countConverter |]
    )

let private parseAgents (value: string) =
    Newtonsoft.Json.JsonConvert.DeserializeObject<AgentCount list>(
        value,
        [| countConverter |]
    )

let private fromUtcTicks (ticks: int64) =
    DateTimeOffset(ticks, TimeSpan.Zero)

let private utcTicks (timestamp: DateTimeOffset) =
    timestamp.UtcDateTime.Ticks

let isBoundary (timestamp: DateTimeOffset) =
    timestamp.Offset = TimeSpan.Zero
    && utcTicks timestamp % resolution.Ticks = 0L

/// The greatest canonical UTC boundary at or before the supplied instant.
let latestCompleteBoundary (timestamp: DateTimeOffset) =
    let ticks = utcTicks timestamp
    fromUtcTicks (ticks - ticks % resolution.Ticks)

/// The least canonical UTC boundary at or after the supplied source timestamp.
let firstBoundaryAtOrAfter (timestamp: DateTimeOffset) =
    let ticks = utcTicks timestamp
    let remainder = ticks % resolution.Ticks

    if remainder = 0L then
        fromUtcTicks ticks
    else
        fromUtcTicks (ticks + resolution.Ticks - remainder)

/// Canonical UTC boundaries intersecting the inclusive input range.
let boundaries (startTime: DateTimeOffset) (endTime: DateTimeOffset) : DateTimeOffset seq =
    let first = firstBoundaryAtOrAfter startTime
    let last = latestCompleteBoundary endTime

    Seq.unfold (fun (boundary: DateTimeOffset) ->
        if boundary > last then None
        else Some(boundary, boundary + resolution)
    ) first

let stride =
    function
    | HistoryWindow.Hours12 -> 5
    | HistoryWindow.Hours24 -> 10
    | HistoryWindow.Hours72 -> 30

let oldestExposedBoundary (anchor: DateTimeOffset) =
    latestCompleteBoundary anchor - exposedHorizon

let oldestRetainedBoundary (anchor: DateTimeOffset) =
    oldestExposedBoundary anchor - predecessorRetention

let dirtyBoundary (anchor: DateTimeOffset) (sourceTimestamp: DateTimeOffset) =
    max (oldestExposedBoundary anchor) (firstBoundaryAtOrAfter sourceTimestamp)

let toBucket (boundary: DateTimeOffset) =
    if not (isBoundary boundary) then
        invalidArg (nameof boundary) "Overview rollup buckets must be canonical UTC boundaries."

    boundary.ToUnixTimeSeconds()

let tryFromBucket (bucket: int64) =
    try
        let boundary = DateTimeOffset.FromUnixTimeSeconds bucket
        if isBoundary boundary then Some boundary else None
    with :? ArgumentOutOfRangeException ->
        None

let isSupportedState (state: PublicationState) =
    state.SchemaVersion = schemaVersion
    && state.ResolutionSeconds = resolutionSeconds
    && state.SourceGeneration >= 0L
    && state.PublishedGeneration >= 0L
    && state.PublishedGeneration <= state.SourceGeneration
    && (state.CompleteThrough |> Option.forall isBoundary)
    && (state.EarliestDirty |> Option.forall isBoundary)

let private countsAreValid (tasks: TaskCount list) (agents: AgentCount list) =
    let taskKinds = tasks |> List.map _.Kind
    let agentKinds = agents |> List.map _.Kind

    tasks |> List.forall (fun count -> count.Count > 0)
    && agents |> List.forall (fun count -> count.Count > 0)
    && Set.count (Set.ofList taskKinds) = taskKinds.Length
    && Set.count (Set.ofList agentKinds) = agentKinds.Length

let internal countJsonIsValid tasksJson agentsJson =
    try
        countsAreValid (parseTasks tasksJson) (parseAgents agentsJson)
    with _ ->
        false

let private candidateRangeIsValid (candidate: RollupCandidate) =
    candidate.Generation >= 0L
    && isBoundary candidate.StartBoundary
    && isBoundary candidate.EndBoundary
    && candidate.StartBoundary <= candidate.EndBoundary

let private candidateRowsAreExact
    (candidate: RollupCandidate)
    (rows: StagedRollupRow list)
    =
    let expectedBoundaries =
        boundaries candidate.StartBoundary candidate.EndBoundary |> Seq.toList

    candidateRangeIsValid candidate
    && rows |> List.forall (fun staged -> staged.Generation = candidate.Generation)
    && rows |> List.forall (fun staged -> countsAreValid staged.Row.Tasks staged.Row.Agents)
    && rows |> List.map (fun staged -> staged.Row.Boundary) = expectedBoundaries

let rec private readRows
    (reader: SqliteDataReader)
    (map: SqliteDataReader -> 'T)
    (acc: 'T list)
    =
    if reader.Read() then readRows reader map (map reader :: acc)
    else List.rev acc

let internal readPublicationState
    (conn: SqliteConnection)
    (tx: SqliteTransaction option)
    : PublicationState option
    =
    use cmd = conn.CreateCommand()
    tx |> Option.iter (fun transaction -> cmd.Transaction <- transaction)
    cmd.CommandText <-
        """
SELECT id, schema_version, resolution_seconds, source_generation, published_generation,
       complete_through, earliest_dirty
FROM overview_history_state
ORDER BY id;
"""
    use reader = cmd.ExecuteReader()

    if not (reader.Read()) then
        None
    else
        let boundaryAt index =
            if reader.IsDBNull index then None
            else tryFromBucket (reader.GetInt64 index)

        let id = reader.GetInt64 0
        let completeThrough = boundaryAt 5
        let earliestDirty = boundaryAt 6
        let completeThroughWasNull = reader.IsDBNull 5
        let earliestDirtyWasNull = reader.IsDBNull 6
        let state =
            { SchemaVersion = reader.GetInt32 1
              ResolutionSeconds = reader.GetInt32 2
              SourceGeneration = reader.GetInt64 3
              PublishedGeneration = reader.GetInt64 4
              CompleteThrough = completeThrough
              EarliestDirty = earliestDirty }
        let hasExtraRow = reader.Read()

        if id = 1L
           && not hasExtraRow
           && (completeThroughWasNull || completeThrough.IsSome)
           && (earliestDirtyWasNull || earliestDirty.IsSome) then
            Some state
        else
            None

let private requirePublicationState conn tx =
    match readPublicationState conn (Some tx) with
    | Some state when isSupportedState state -> state
    | _ ->
        raise (
            InvalidDataException(
                "Overview history publication state has an unsupported schema or resolution."
            )
        )

let internal publishedRowsAreValid (conn: SqliteConnection) state =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT bucket, tasks, agents FROM overview_history_rows ORDER BY bucket;"
    use reader = cmd.ExecuteReader()

    let rows =
        readRows reader (fun row -> row.GetInt64 0, row.GetString 1, row.GetString 2) []

    let rowsValid =
        rows
        |> List.forall (fun (bucket, tasks, agents) ->
            tryFromBucket bucket |> Option.isSome
            && countJsonIsValid tasks agents)

    let dense =
        rows
        |> List.map (fun (bucket, _, _) -> bucket)
        |> List.pairwise
        |> List.forall (fun (left, right) -> right - left = int64 resolutionSeconds)

    match state.CompleteThrough, rows with
    | None, [] -> rowsValid && state.PublishedGeneration = 0L
    | Some completeThrough, _ :: _ ->
        rowsValid
        && dense
        && rows |> List.last |> fun (bucket, _, _) -> bucket = toBucket completeThrough
    | _ -> false

let private tryReadStagingSummary
    (conn: SqliteConnection)
    (tx: SqliteTransaction option)
    =
    use cmd = conn.CreateCommand()
    tx |> Option.iter (fun transaction -> cmd.Transaction <- transaction)
    cmd.CommandText <-
        """
SELECT min(generation), max(generation), min(bucket), max(bucket), count(*)
FROM overview_history_staging;
"""
    use reader = cmd.ExecuteReader()
    reader.Read() |> ignore
    let count = reader.GetInt64 4

    if count = 0L then
        None
    else
        let minGeneration = reader.GetInt64 0
        let maxGeneration = reader.GetInt64 1
        let minBucket = reader.GetInt64 2
        let maxBucket = reader.GetInt64 3

        match tryFromBucket minBucket, tryFromBucket maxBucket with
        | Some startBoundary, Some endBoundary
            when minGeneration = maxGeneration
                 && maxBucket - minBucket = (count - 1L) * int64 resolutionSeconds ->
            Some
                { Generation = minGeneration
                  StartBoundary = startBoundary
                  EndBoundary = endBoundary }
        | _ ->
            raise (
                InvalidDataException(
                    "Overview history staging rows contain gaps or mixed generations."
                )
            )

let private stagingPayloadsAreValid (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "SELECT tasks, agents FROM overview_history_staging ORDER BY bucket;"
    use reader = cmd.ExecuteReader()

    readRows reader (fun row -> row.GetString 0, row.GetString 1) []
    |> List.forall (fun (tasks, agents) -> countJsonIsValid tasks agents)

let internal stagingRowsAreValid (conn: SqliteConnection) sourceGeneration =
    try
        match tryReadStagingSummary conn None with
        | None -> true
        | Some candidate ->
            candidate.Generation <= sourceGeneration
            && stagingPayloadsAreValid conn
    with _ ->
        false

let private insertStagedRow
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    (staged: StagedRollupRow)
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        """
INSERT INTO overview_history_staging(generation, bucket, tasks, agents)
VALUES ($generation, $bucket, $tasks, $agents);
"""
    cmd.Parameters.AddWithValue("$generation", staged.Generation) |> ignore
    cmd.Parameters.AddWithValue("$bucket", toBucket staged.Row.Boundary) |> ignore
    cmd.Parameters.AddWithValue("$tasks", serializeTasks staged.Row.Tasks) |> ignore
    cmd.Parameters.AddWithValue("$agents", serializeAgents staged.Row.Agents) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let internal stageCandidate
    (openConnection: unit -> SqliteConnection)
    (candidate: RollupCandidate)
    (rows: StagedRollupRow list)
    =
    if not (candidateRowsAreExact candidate rows) then
        invalidArg
            (nameof rows)
            "Overview history staging rows must exactly cover one dense candidate range and generation."

    use conn = openConnection ()
    use tx = conn.BeginTransaction(deferred = false)
    let state = requirePublicationState conn tx

    if state.SourceGeneration <> candidate.Generation then
        tx.Rollback()
        StagingResult.SourceGenerationChanged state.SourceGeneration
    else
        match tryReadStagingSummary conn (Some tx) with
        | Some existing when existing.Generation <> candidate.Generation ->
            invalidOp "Overview history staging already contains a different generation."
        | Some existing when candidate.StartBoundary <> existing.EndBoundary + resolution ->
            invalidOp "Overview history staging batches must be contiguous and non-overlapping."
        | _ ->
            rows |> List.iter (insertStagedRow conn tx)
            tx.Commit()
            StagingResult.Staged

let private readStagedRows
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        """
SELECT generation, bucket, tasks, agents
FROM overview_history_staging
ORDER BY bucket;
"""
    use reader = cmd.ExecuteReader()

    readRows
        reader
        (fun row ->
            let boundary =
                tryFromBucket (row.GetInt64 1)
                |> Option.defaultWith (fun () ->
                    raise (InvalidDataException("Overview history staging contains an invalid bucket.")))

            { Generation = row.GetInt64 0
              Row =
                { Boundary = boundary
                  Tasks = parseTasks (row.GetString 2)
                  Agents = parseAgents (row.GetString 3) } })
        []

let private publicationRangeIsValid state candidate =
    match state.CompleteThrough with
    | None -> true
    | Some completeThrough
        when candidate.StartBoundary = completeThrough + resolution
             && (state.EarliestDirty |> Option.forall (fun dirty -> dirty > completeThrough)) ->
        true
    | Some completeThrough ->
        match state.EarliestDirty with
        | Some dirty ->
            dirty <= completeThrough
            && candidate.StartBoundary = dirty
            && candidate.EndBoundary >= completeThrough
        | None -> false

type private PublicationMode =
    | Incremental
    | ReplaceAll

let private deletePublishedRange
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    candidate
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        "DELETE FROM overview_history_rows WHERE bucket >= $start AND bucket <= $end;"
    cmd.Parameters.AddWithValue("$start", toBucket candidate.StartBoundary) |> ignore
    cmd.Parameters.AddWithValue("$end", toBucket candidate.EndBoundary) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private deleteAllPublishedRows
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- "DELETE FROM overview_history_rows;"
    cmd.ExecuteNonQuery() |> ignore

let private insertPublishedRange
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    candidate
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        """
INSERT INTO overview_history_rows(bucket, tasks, agents)
SELECT bucket, tasks, agents
FROM overview_history_staging
WHERE generation = $generation AND bucket >= $start AND bucket <= $end
ORDER BY bucket;
"""
    cmd.Parameters.AddWithValue("$generation", candidate.Generation) |> ignore
    cmd.Parameters.AddWithValue("$start", toBucket candidate.StartBoundary) |> ignore
    cmd.Parameters.AddWithValue("$end", toBucket candidate.EndBoundary) |> ignore
    cmd.ExecuteNonQuery()

let private advancePublication
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    candidate
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        """
UPDATE overview_history_state
SET published_generation = $generation,
    complete_through = $end,
    earliest_dirty = CASE
        WHEN earliest_dirty >= $start AND earliest_dirty <= $end THEN NULL
        ELSE earliest_dirty
    END
WHERE id = 1 AND source_generation = $generation;
"""
    cmd.Parameters.AddWithValue("$generation", candidate.Generation) |> ignore
    cmd.Parameters.AddWithValue("$start", toBucket candidate.StartBoundary) |> ignore
    cmd.Parameters.AddWithValue("$end", toBucket candidate.EndBoundary) |> ignore
    cmd.ExecuteNonQuery() = 1

let private advanceReplacement
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    candidate
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        """
UPDATE overview_history_state
SET published_generation = $generation,
    complete_through = $end,
    earliest_dirty = CASE
        WHEN earliest_dirty <= $end THEN NULL
        ELSE earliest_dirty
    END
WHERE id = 1 AND source_generation = $generation;
"""
    cmd.Parameters.AddWithValue("$generation", candidate.Generation) |> ignore
    cmd.Parameters.AddWithValue("$end", toBucket candidate.EndBoundary) |> ignore
    cmd.ExecuteNonQuery() = 1

let private clearStaging
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- "DELETE FROM overview_history_staging;"
    cmd.ExecuteNonQuery() |> ignore

let private publish
    mode
    (openConnection: unit -> SqliteConnection)
    (candidate: RollupCandidate)
    =
    if not (candidateRangeIsValid candidate) then
        invalidArg (nameof candidate) "Overview history publication requires a canonical candidate range."

    use conn = openConnection ()
    use tx = conn.BeginTransaction(deferred = false)
    let state = requirePublicationState conn tx

    if state.SourceGeneration <> candidate.Generation then
        tx.Rollback()
        PublicationResult.SourceGenerationChanged state.SourceGeneration
    else
        let stagedRows = readStagedRows conn tx

        if not (candidateRowsAreExact candidate stagedRows) then
            invalidOp "Overview history staging does not exactly match the requested publication range."

        if mode = Incremental && not (publicationRangeIsValid state candidate) then
            invalidOp "Overview history publication must be contiguous or fully repair the published dirty range."

        match mode with
        | Incremental -> deletePublishedRange conn tx candidate
        | ReplaceAll -> deleteAllPublishedRows conn tx

        let inserted = insertPublishedRange conn tx candidate

        if inserted <> stagedRows.Length then
            raise (InvalidDataException("Overview history publication did not insert the complete staged range."))

        let advanced =
            match mode with
            | Incremental -> advancePublication conn tx candidate
            | ReplaceAll -> advanceReplacement conn tx candidate

        if not advanced then
            tx.Rollback()
            PublicationResult.SourceGenerationChanged state.SourceGeneration
        else
            clearStaging conn tx
            let publishedState = requirePublicationState conn tx
            tx.Commit()
            PublicationResult.Published publishedState

let internal publishCandidate openConnection candidate =
    publish Incremental openConnection candidate

let internal replacePublishedCandidate openConnection candidate =
    publish ReplaceAll openConnection candidate

let internal discardStaging (openConnection: unit -> SqliteConnection) =
    use conn = openConnection ()
    use tx = conn.BeginTransaction(deferred = false)
    clearStaging conn tx
    tx.Commit()

let internal prunePublishedRows
    (openConnection: unit -> SqliteConnection)
    oldestRetained
    =
    if not (isBoundary oldestRetained) then
        invalidArg (nameof oldestRetained) "Overview history retention requires a canonical boundary."

    use conn = openConnection ()
    use tx = conn.BeginTransaction(deferred = false)
    let state = requirePublicationState conn tx

    let deleted =
        match state.CompleteThrough with
        | Some completeThrough when oldestRetained <= completeThrough ->
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- "DELETE FROM overview_history_rows WHERE bucket < $oldest;"
            cmd.Parameters.AddWithValue("$oldest", toBucket oldestRetained) |> ignore
            cmd.ExecuteNonQuery()
        | _ -> 0

    tx.Commit()
    deleted

let private readPublishedRows
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    startBoundary
    endBoundary
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        """
SELECT bucket, tasks, agents
FROM overview_history_rows
WHERE bucket >= $start AND bucket <= $end
ORDER BY bucket;
"""
    cmd.Parameters.AddWithValue("$start", toBucket startBoundary) |> ignore
    cmd.Parameters.AddWithValue("$end", toBucket endBoundary) |> ignore
    use reader = cmd.ExecuteReader()

    readRows
        reader
        (fun row ->
            let boundary =
                tryFromBucket (row.GetInt64 0)
                |> Option.defaultWith (fun () ->
                    raise (InvalidDataException("Overview history contains an invalid published bucket.")))

            { Boundary = boundary
              Tasks = parseTasks (row.GetString 1)
              Agents = parseAgents (row.GetString 2) })
        []

let internal readPublishedSnapshot
    (openConnection: unit -> SqliteConnection)
    startBoundary
    endBoundary
    afterStateRead
    =
    if not (isBoundary startBoundary)
       || not (isBoundary endBoundary)
       || startBoundary > endBoundary then
        invalidArg (nameof startBoundary) "Overview history reads require a canonical ordered range."

    use conn = openConnection ()
    use tx = conn.BeginTransaction(deferred = true)
    let state = requirePublicationState conn tx
    afterStateRead ()
    let rows = readPublishedRows conn tx startBoundary endBoundary
    tx.Commit()
    state, rows
