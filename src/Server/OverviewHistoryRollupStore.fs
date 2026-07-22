module Server.OverviewHistoryRollupStore

open System
open System.IO
open Microsoft.Data.Sqlite
open Server.SessionActivity
open Server.OverviewHistoryRollup
open Server.SqliteStorage
open Shared

let private derivedSchemaSql =
    $"""
CREATE TABLE IF NOT EXISTS overview_history_rows (
    bucket INTEGER PRIMARY KEY CHECK (bucket %% {resolutionSeconds} = 0),
    tasks  TEXT NOT NULL,
    agents TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS overview_history_state (
    id                   INTEGER PRIMARY KEY CHECK (id = 1),
    schema_version       INTEGER NOT NULL CHECK (schema_version = {schemaVersion}),
    resolution_seconds   INTEGER NOT NULL CHECK (resolution_seconds = {resolutionSeconds}),
    source_generation    INTEGER NOT NULL CHECK (source_generation >= 0),
    published_generation INTEGER NOT NULL CHECK (
        published_generation >= 0 AND published_generation <= source_generation
    ),
    complete_through     INTEGER CHECK (
        complete_through IS NULL OR complete_through %% resolution_seconds = 0
    ),
    earliest_dirty       INTEGER CHECK (
        earliest_dirty IS NULL OR earliest_dirty %% resolution_seconds = 0
    )
);

INSERT OR IGNORE INTO overview_history_state
    (id, schema_version, resolution_seconds, source_generation, published_generation)
VALUES (1, {schemaVersion}, {resolutionSeconds}, 0, 0);

CREATE TABLE IF NOT EXISTS overview_history_staging (
    generation INTEGER NOT NULL CHECK (generation >= 0),
    bucket     INTEGER NOT NULL CHECK (bucket %% {resolutionSeconds} = 0),
    tasks      TEXT NOT NULL,
    agents     TEXT NOT NULL,
    PRIMARY KEY (generation, bucket)
);
CREATE INDEX IF NOT EXISTS ix_overview_staging_bucket
    ON overview_history_staging(bucket, generation);

CREATE TABLE IF NOT EXISTS overview_history_session_bounds (
    session_id        TEXT PRIMARY KEY,
    first_observed_at TEXT NOT NULL,
    last_observed_at  TEXT NOT NULL,
    CHECK (first_observed_at <= last_observed_at)
);
CREATE INDEX IF NOT EXISTS ix_overview_bounds_last_first
    ON overview_history_session_bounds(last_observed_at, first_observed_at);
CREATE INDEX IF NOT EXISTS ix_overview_bounds_first_last
    ON overview_history_session_bounds(first_observed_at, last_observed_at);
"""

let private dropDerivedSchemaSql =
    """
DROP TABLE IF EXISTS overview_history_staging;
DROP TABLE IF EXISTS overview_history_rows;
DROP TABLE IF EXISTS overview_history_session_bounds;
DROP TABLE IF EXISTS overview_history_state;
"""

let private updateObservationBoundsSql =
    """
INSERT INTO overview_history_session_bounds
    (session_id, first_observed_at, last_observed_at)
VALUES ($sid, $observed, $observed)
ON CONFLICT(session_id) DO UPDATE SET
    first_observed_at = min(first_observed_at, excluded.first_observed_at),
    last_observed_at  = max(last_observed_at, excluded.last_observed_at);
"""

let private invalidateOverviewRollupSql =
    """
UPDATE overview_history_state
SET source_generation = source_generation + 1,
    earliest_dirty = CASE
        WHEN earliest_dirty IS NULL OR $dirty < earliest_dirty THEN $dirty
        ELSE earliest_dirty
    END
WHERE id = 1;
"""

let private derivedTableShapes =
    [ "overview_history_rows", [ "bucket"; "tasks"; "agents" ]
      "overview_history_state",
      [ "id"
        "schema_version"
        "resolution_seconds"
        "source_generation"
        "published_generation"
        "complete_through"
        "earliest_dirty" ]
      "overview_history_staging", [ "generation"; "bucket"; "tasks"; "agents" ]
      "overview_history_session_bounds", [ "session_id"; "first_observed_at"; "last_observed_at" ] ]

let private tableColumns (conn: SqliteConnection) table =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"PRAGMA table_info(%s{table});"
    use reader = cmd.ExecuteReader()
    readRows reader (fun row -> row.GetString 1) []

let private derivedShapesAreCurrent conn =
    derivedTableShapes
    |> List.forall (fun (table, expected) -> tableColumns conn table = expected)

let private executeSql (conn: SqliteConnection) sql =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

let private readPublicationState
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

let private publishedRowsAreValid (conn: SqliteConnection) state =
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

let private stagingRowsAreValid (conn: SqliteConnection) sourceGeneration =
    try
        match tryReadStagingSummary conn None with
        | None -> true
        | Some candidate ->
            candidate.Generation <= sourceGeneration
            && stagingPayloadsAreValid conn
    with _ ->
        false

let private observationBoundsAreValid (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "SELECT first_observed_at, last_observed_at FROM overview_history_session_bounds;"
    use reader = cmd.ExecuteReader()

    readRows reader (fun row -> row.GetString 0, row.GetString 1) []
    |> List.forall (fun (firstText, lastText) ->
        try
            let first = parseIso firstText
            let last = parseIso lastText
            isoUtc first = firstText && isoUtc last = lastText && first <= last
        with _ ->
            false)

let private derivedDataIsValid conn =
    try
        match readPublicationState conn None with
        | Some state when isSupportedState state ->
            publishedRowsAreValid conn state
            && stagingRowsAreValid conn state.SourceGeneration
            && observationBoundsAreValid conn
        | _ -> false
    with _ ->
        false

let private resetDerivedSchema (conn: SqliteConnection) =
    use tx = conn.BeginTransaction()
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- dropDerivedSchemaSql + derivedSchemaSql
    cmd.ExecuteNonQuery() |> ignore
    tx.Commit()

let internal ensureSchema conn =
    if derivedShapesAreCurrent conn then
        executeSql conn derivedSchemaSql

        if not (derivedDataIsValid conn) then
            resetDerivedSchema conn
    else
        resetDerivedSchema conn

let internal publicationState (openConnection: unit -> SqliteConnection) =
    use conn = openConnection ()

    match readPublicationState conn None with
    | Some state when isSupportedState state -> state
    | _ ->
        raise (
            InvalidDataException(
                "Overview history publication state has an unsupported schema or resolution."
            )
        )

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
    match state.CompleteThrough, state.EarliestDirty with
    | None, _ -> true
    | Some completeThrough, earliestDirty
        when candidate.StartBoundary = completeThrough + resolution
             && (earliestDirty |> Option.forall (fun dirty -> dirty > completeThrough)) ->
        true
    | Some completeThrough, Some dirty ->
        dirty <= completeThrough
        && candidate.StartBoundary = dirty
        && candidate.EndBoundary >= completeThrough
    | Some _, None -> false

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

let private readSelectedPublishedRows
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    window
    anchor
    =
    let expected = selectedBoundaries window anchor
    let startBoundary = List.head expected
    let stepSeconds = int64 (resolutionSeconds * stride window)

    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        """
SELECT bucket, tasks, agents
FROM overview_history_rows
WHERE bucket >= $start
  AND bucket <= $end
  AND ((bucket - $start) % $step) = 0
ORDER BY bucket
LIMIT $limit;
"""
    cmd.Parameters.AddWithValue("$start", toBucket startBoundary) |> ignore
    cmd.Parameters.AddWithValue("$end", toBucket anchor) |> ignore
    cmd.Parameters.AddWithValue("$step", stepSeconds) |> ignore
    cmd.Parameters.AddWithValue("$limit", sampleIntervalCount + 1) |> ignore
    use reader = cmd.ExecuteReader()

    let rows =
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

    if rows |> List.map _.Boundary <> expected then
        raise (
            InvalidDataException(
                "Overview history publication does not completely cover the requested window."
            )
        )

    rows

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

let internal usePublishedSnapshot
    (openConnection: unit -> SqliteConnection)
    window
    afterStateRead
    beforeRowsRead
    useSnapshot
    =
    async {
        use conn = openConnection ()
        use tx = conn.BeginTransaction(deferred = true)
        let state = requirePublicationState conn tx
        afterStateRead ()

        let anchor =
            state.CompleteThrough
            |> Option.defaultWith (fun () ->
                raise (
                    InvalidDataException(
                        "Overview history has no published rollup."
                    )
                ))

        let readRows () =
            beforeRowsRead ()
            readSelectedPublishedRows conn tx window anchor

        let! result = useSnapshot state anchor readRows
        tx.Commit()
        return result
    }

let internal upsertObservationBounds
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    sessionId
    observedAt
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- updateObservationBoundsSql
    cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
    cmd.Parameters.AddWithValue("$observed", isoUtc observedAt) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let internal invalidateOverviewRollup
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    sourceTimestamp
    =
    let clampAnchor =
        (requirePublicationState conn tx).CompleteThrough
        |> Option.defaultWith (fun () ->
            latestCompleteBoundary DateTimeOffset.UtcNow)

    let dirty = dirtyBoundary clampAnchor sourceTimestamp
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- invalidateOverviewRollupSql
    cmd.Parameters.AddWithValue("$dirty", toBucket dirty) |> ignore

    if cmd.ExecuteNonQuery() <> 1 then
        raise (InvalidDataException("Overview history publication state is missing."))

let internal rebuildObservationBounds
    (openConnection: unit -> SqliteConnection)
    =
    use conn = openConnection ()
    use tx = conn.BeginTransaction(deferred = false)
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <-
        """
DELETE FROM overview_history_session_bounds;
INSERT INTO overview_history_session_bounds
    (session_id, first_observed_at, last_observed_at)
SELECT session_id, min(ts), max(ts)
FROM (
    SELECT session_id, ts FROM activity_events
    UNION ALL
    SELECT session_id, ts FROM session_liveness
)
GROUP BY session_id;
"""
    cmd.ExecuteNonQuery() |> ignore
    tx.Commit()
