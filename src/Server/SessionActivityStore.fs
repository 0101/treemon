module Server.SessionActivityStore

open System
open System.IO
open System.Globalization
open Microsoft.Data.Sqlite
open Shared
open Server.SessionActivity
open Server.OverviewHistoryRollup

// The durable mirror behind the push-model live state. The SessionActivity mailbox (single writer)
// upserts the per-session fold result and appends the raw event to the authoritative source tables:
//
//   session_status  — one row per session: the latest fold state. Read back on restart to rebuild the
//                     live Map before serving (loadLiveStatuses), so cards are correct immediately.
//   activity_events — the append-only raw stream: the substrate the Overview history aggregates on
//                     read (queryWindow), and the source of INSERT OR IGNORE idempotency (event_id PK).
//
// The overview_history_* tables are disposable count-only rollups and reconstruction metadata.
// Construction validates their schema and invariants, replacing only those derived tables when they
// cannot be trusted; the source tables above remain intact for rebuilding.
//
// WAL journalling lets queryWindow / loadLiveStatuses read concurrently with the mailbox writer with
// no lock contention; the writer being single means status upserts never race each other. The SQLite
// file path is instance-specific (keyed by the server's data dir / port) so a side-by-side validation
// instance never collides with main.

// --- Row shapes -------------------------------------------------------------------------------

/// One session_status row: the per-session fold result plus the timestamps the store needs —
/// `UpdatedAt` (the OccurredAt of the last applied STATUS event; drives status last-write-wins) and
/// `LastSeen` (the last heartbeat; drives freshness + the live window on restart). `ContextUsageAt`
/// is the OccurredAt of the last applied `usage_info` gauge — a SEPARATE last-write-wins clock so the
/// context donut is ordered independently of status and never shares the status LWW clock (a usage
/// report must not block a slightly-earlier status transition, nor be discarded by one). It is
/// server-internal ordering state only: never persisted (like `ContextUsage`, it rehydrates as None)
/// and never on the wire.
type StoredStatus =
    { SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Status: SessionStatus
      UpdatedAt: DateTimeOffset
      LastSeen: DateTimeOffset
      ContextUsageAt: DateTimeOffset option }

/// One activity_events row: a single pushed event, already classified. `Status`/`Skill` are the fold
/// result *after* applying this event, so the Overview history can read a bucket's state without
/// re-folding.
type ActivityEventRow =
    { EventId: EventId
      SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Kind: string
      Status: SessionLevelStatus
      Skill: string option
      Ts: DateTimeOffset }

type internal OverviewHistoryInputs =
    { TaskSnapshots: (DateTimeOffset * OverviewData.TaskCount list) list
      Events: ActivityEventRow list
      Liveness: (SessionId * DateTimeOffset) list }

// --- Serialisation helpers --------------------------------------------------------------------

// Timestamps are stored as UTC round-trip ("O") strings. Normalising to UTC gives every value the
// same fixed-width "+00:00" suffix, so lexical string comparison equals chronological order — which
// is what the `ts >= $start` window query and the `last_seen >= $cutoff` live filter rely on.
let internal isoUtc (dto: DateTimeOffset) =
    dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

let internal parseIso (s: string) =
    DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

// Task-snapshot rows persist the count-only `TaskCount list` as a JSON blob, using the SAME
// Fable.Remoting converter the getOverviewHistory wire uses (matching the former JSONL history), so a
// stored snapshot round-trips byte-identically to what the client later receives via OverviewSnapshot.
let private countConverter: Newtonsoft.Json.JsonConverter = Fable.Remoting.Json.FableJsonConverter()

let private serializeTasks (tasks: OverviewData.TaskCount list) : string =
    Newtonsoft.Json.JsonConvert.SerializeObject(tasks, Newtonsoft.Json.Formatting.None, [| countConverter |])

let internal parseTasks (s: string) : OverviewData.TaskCount list =
    Newtonsoft.Json.JsonConvert.DeserializeObject<OverviewData.TaskCount list>(s, [| countConverter |])

let private parseAgents (s: string) : OverviewData.AgentCount list =
    Newtonsoft.Json.JsonConvert.DeserializeObject<OverviewData.AgentCount list>(s, [| countConverter |])

let private statusText =
    function
    | SessionLevelStatus.Working -> "working"
    | SessionLevelStatus.WaitingForUser -> "waiting_for_user"
    | SessionLevelStatus.Idle -> "idle"

let private parseStatus =
    function
    | "working" -> SessionLevelStatus.Working
    | "waiting_for_user" -> SessionLevelStatus.WaitingForUser
    | "idle" -> SessionLevelStatus.Idle
    | other -> failwithf "SessionActivityStore: unknown status text %A" other

let private providerText =
    function
    | CopilotCli -> "copilot_cli"

let private parseProvider =
    function
    | "copilot_cli" -> CopilotCli
    | other -> failwithf "SessionActivityStore: unknown provider text %A" other

/// A `string option` as a parameter value: `Some s` binds the text, `None` binds SQL NULL.
let private optToDb (o: string option) : obj =
    match o with
    | Some s -> box s
    | None -> box DBNull.Value

/// A `Message option` as two parameter values (text, iso-ts); `None` binds NULL for both.
let private msgToDb (m: Message option) : obj * obj =
    match m with
    | Some x -> box x.Text, box (isoUtc x.At)
    | None -> box DBNull.Value, box DBNull.Value

let private readOptStr (r: SqliteDataReader) (i: int) =
    if r.IsDBNull i then None else Some(r.GetString i)

/// Reconstruct a `Message option` from a text column + a timestamp column; present only when both
/// are non-NULL (they are written together, so this is really an all-or-nothing pair).
let private readOptMsg (r: SqliteDataReader) (iText: int) (iTs: int) : Message option =
    match readOptStr r iText, readOptStr r iTs with
    | Some t, Some ts -> Some { Text = t; At = parseIso ts }
    | _ -> None

let private readStored (r: SqliteDataReader) : StoredStatus =
    { SessionId = SessionId(r.GetString 0)
      WorktreePath = WorktreePath(r.GetString 1)
      Provider = parseProvider (r.GetString 2)
      Status =
        { Status = parseStatus (r.GetString 3)
          Skill = readOptStr r 4
          Intent = readOptMsg r 9 10
          Title = readOptMsg r 13 14
          LastUserMessage = readOptMsg r 5 6
          LastAssistantMessage = readOptMsg r 7 8
          ContextUsage = None }
      UpdatedAt = parseIso (r.GetString 11)
      LastSeen = parseIso (r.GetString 12)
      ContextUsageAt = None }

let internal readEventRow (r: SqliteDataReader) : ActivityEventRow =
    { EventId = EventId(r.GetString 0)
      SessionId = SessionId(r.GetString 1)
      WorktreePath = WorktreePath(r.GetString 2)
      Provider = parseProvider (r.GetString 3)
      Kind = r.GetString 4
      Status = parseStatus (r.GetString 5)
      Skill = readOptStr r 6
      Ts = parseIso (r.GetString 7) }

// --- SQL --------------------------------------------------------------------------------------

let private schemaSql =
    """
CREATE TABLE IF NOT EXISTS session_status (
    session_id    TEXT PRIMARY KEY,
    worktree_path TEXT NOT NULL,
    provider      TEXT NOT NULL,
    status        TEXT NOT NULL,
    current_skill TEXT,
    last_user_msg TEXT,
    last_user_ts  TEXT,
    last_asst_msg TEXT,
    last_asst_ts  TEXT,
    intent_text   TEXT,
    intent_ts     TEXT,
    title_text    TEXT,
    title_ts      TEXT,
    updated_at    TEXT NOT NULL,
    last_seen     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_status_worktree ON session_status(worktree_path);

CREATE TABLE IF NOT EXISTS activity_events (
    event_id      TEXT PRIMARY KEY,
    session_id    TEXT NOT NULL,
    worktree_path TEXT NOT NULL,
    provider      TEXT NOT NULL,
    kind          TEXT NOT NULL,
    status        TEXT NOT NULL,
    skill         TEXT,
    ts            TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_events_ts ON activity_events(ts);
CREATE INDEX IF NOT EXISTS ix_events_session_ts ON activity_events(session_id, ts);

CREATE TABLE IF NOT EXISTS session_liveness (
    session_id TEXT NOT NULL,
    ts         TEXT NOT NULL,
    PRIMARY KEY (session_id, ts)
);
CREATE INDEX IF NOT EXISTS ix_liveness_ts ON session_liveness(ts);

CREATE TABLE IF NOT EXISTS task_snapshots (
    id    INTEGER PRIMARY KEY AUTOINCREMENT,
    ts    TEXT NOT NULL,
    tasks TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_task_snapshots_ts ON task_snapshots(ts);
"""

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

// One-time normalisation of legacy rows: pre-idle-only builds persisted the retired "done" status, so
// existing DBs still carry status='done' rows. Rewrite them to 'idle' (the value 'done' now folds to)
// so stored data matches the current vocabulary. Idempotent — a no-op once no 'done' rows remain — so
// it is safe to re-run on every store construction.
let private migrateSql =
    """
UPDATE session_status SET status = 'idle' WHERE status = 'done';
UPDATE activity_events SET status = 'idle' WHERE status = 'done';
"""

// Last-write-wins: on a session_id conflict the incoming row overwrites only when its updated_at is
// at least as new (>= so an idempotent replay with the same timestamp still lands identically). A
// stale/out-of-order report is a no-op.
let private upsertSql =
    """
INSERT INTO session_status
    (session_id, worktree_path, provider, status, current_skill,
     last_user_msg, last_user_ts, last_asst_msg, last_asst_ts, intent_text, intent_ts, updated_at, last_seen, title_text, title_ts)
VALUES ($sid, $wt, $prov, $status, $skill, $um, $uts, $am, $ats, $it, $its, $upd, $seen, $tt, $tts)
ON CONFLICT(session_id) DO UPDATE SET
    worktree_path = excluded.worktree_path,
    provider      = excluded.provider,
    status        = excluded.status,
    current_skill = excluded.current_skill,
    last_user_msg = excluded.last_user_msg,
    last_user_ts  = excluded.last_user_ts,
    last_asst_msg = excluded.last_asst_msg,
    last_asst_ts  = excluded.last_asst_ts,
    intent_text   = excluded.intent_text,
    intent_ts     = excluded.intent_ts,
    title_text    = excluded.title_text,
    title_ts      = excluded.title_ts,
    updated_at    = excluded.updated_at,
    last_seen     = excluded.last_seen
WHERE excluded.updated_at >= session_status.updated_at;
"""

// event_id is the PK; OR IGNORE makes a duplicate POST (same event_id) a silent no-op — the
// idempotency guarantee for the raw stream.
let private appendSql =
    """
INSERT OR IGNORE INTO activity_events
    (event_id, session_id, worktree_path, provider, kind, status, skill, ts)
VALUES ($eid, $sid, $wt, $prov, $kind, $status, $skill, $ts);
"""

// Liveness-only bump: advance a session's last_seen (openness) without touching updated_at, status,
// or any message/skill field, and only ever forward. Heartbeats take this path instead of
// upsert+append, so they refresh openness without moving the last-write-wins clock or polluting the
// event history.
let private touchSql =
    """
UPDATE session_status SET last_seen = $seen WHERE session_id = $sid AND last_seen < $seen;
"""

let private appendLivenessSql =
    """
INSERT OR IGNORE INTO session_liveness (session_id, ts) VALUES ($sid, $seen);
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

let private loadSql =
    """
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts, intent_text, intent_ts, updated_at, last_seen, title_text, title_ts
FROM session_status
WHERE last_seen >= $cutoff
ORDER BY last_seen;
"""

// Every stored session for one worktree, newest first, with NO idle-window filter (unlike loadSql).
// The resume path reads this: after a restart the idle-window live cache drops sessions last active
// >2h ago, so a resume pick over that cache returns None (→ wrong `--continue` fallback); this keeps
// the durable identity available until the 14d retention prune. Uses the ix_status_worktree index.
let private worktreeStatusesSql =
    """
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts, intent_text, intent_ts, updated_at, last_seen, title_text, title_ts
FROM session_status
WHERE worktree_path = $wt
ORDER BY last_seen DESC;
"""

let private statusBySessionSql =
    """
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts, intent_text, intent_ts, updated_at, last_seen, title_text, title_ts
FROM session_status
WHERE session_id = $sid
LIMIT 1;
"""

let private queryWindowSql =
    """
SELECT event_id, session_id, worktree_path, provider, kind, status, skill, ts
FROM activity_events
WHERE ts >= $start AND ts <= $end
ORDER BY ts, rowid;
"""

let private queryHistoryWindowSql =
    """
WITH relevant_sessions AS (
    SELECT session_id
    FROM activity_events
    WHERE ts >= $lookback AND ts <= $end
    UNION
    SELECT session_id
    FROM session_liveness
    WHERE ts >= $lookback AND ts <= $end
),
baseline_rows AS (
    SELECT (
        SELECT baseline.rowid
        FROM activity_events AS baseline
        WHERE baseline.session_id = relevant.session_id AND baseline.ts < $start
        ORDER BY baseline.ts DESC, baseline.rowid DESC
        LIMIT 1
    ) AS rowid
    FROM relevant_sessions AS relevant
)
SELECT event_id, session_id, worktree_path, provider, kind, status, skill, ts, event.rowid
FROM activity_events AS event
WHERE event.rowid IN (SELECT rowid FROM baseline_rows WHERE rowid IS NOT NULL)
UNION ALL
SELECT event_id, session_id, worktree_path, provider, kind, status, skill, ts, rowid
FROM activity_events
WHERE ts >= $start AND ts <= $end
ORDER BY ts, rowid;
"""

let private queryLivenessSql =
    """
SELECT session_id, ts
FROM session_liveness
WHERE ts >= $start AND ts <= $end
ORDER BY ts;
"""

let private appendTaskSnapshotSql =
    """
INSERT INTO task_snapshots (ts, tasks) VALUES ($ts, $tasks);
"""

let private queryTaskSnapshotsSql =
    """
SELECT ts, tasks
FROM task_snapshots
WHERE ts >= $start AND ts <= $end
ORDER BY ts, id;
"""

let private queryLatestTaskSnapshotSql =
    """
SELECT ts, tasks
FROM task_snapshots
ORDER BY ts DESC, id DESC
LIMIT 1;
"""

let private queryTaskSnapshotBeforeSql =
    """
SELECT ts, tasks
FROM task_snapshots
WHERE ts < $start
ORDER BY ts DESC, id DESC
LIMIT 1;
"""

// Prune redundant history before the cutoff while retaining the one baseline needed to carry state
// into later windows: the latest old event for each still-retained session and the latest old task
// snapshot. Liveness needs no old baseline beyond openWindow.
let private pruneSql =
    """
WITH retained_event_baselines AS (
    SELECT event.rowid
    FROM activity_events AS event
    JOIN session_status AS status
      ON status.session_id = event.session_id
     AND status.last_seen >= $cutoff
    WHERE event.ts < $cutoff
      AND event.rowid = (
          SELECT baseline.rowid
          FROM activity_events AS baseline
          WHERE baseline.session_id = event.session_id
            AND baseline.ts < $cutoff
          ORDER BY baseline.ts DESC, baseline.rowid DESC
          LIMIT 1
      )
)
DELETE FROM activity_events
WHERE ts < $cutoff
  AND rowid NOT IN (SELECT rowid FROM retained_event_baselines);

DELETE FROM session_liveness WHERE ts < $cutoff;
DELETE FROM task_snapshots
WHERE ts < $cutoff
  AND id <> (
      SELECT baseline.id
      FROM task_snapshots AS baseline
      WHERE baseline.ts < $cutoff
      ORDER BY baseline.ts DESC, baseline.id DESC
      LIMIT 1
  );
DELETE FROM session_status WHERE last_seen < $cutoff;
"""

// Every stored session across all worktrees, oldest `last_seen` first (so a fold keyed by
// worktree_path keeps the newest per worktree). NO idle-window filter — the durable footer/resume
// substrate for cards whose sessions have aged out of the live map after a restart.
let private allStatusesSql =
    """
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts, intent_text, intent_ts, updated_at, last_seen, title_text, title_ts
FROM session_status
ORDER BY last_seen;
"""

// --- Reader / binder helpers ------------------------------------------------------------------

// Drain the reader through an immutable recursive accumulator instead of a mutable list-building
// loop, then restore source order.
let rec internal readRows (reader: SqliteDataReader) (map: SqliteDataReader -> 'T) (acc: 'T list) : 'T list =
    if reader.Read() then readRows reader map (map reader :: acc) else List.rev acc

// Bind an activity_events row's parameters onto a prepared command — shared by AppendEvent and the
// transactional AppendAndUpsert so the two paths can never drift.
let private bindAppend (cmd: SqliteCommand) (row: ActivityEventRow) =
    cmd.Parameters.AddWithValue("$eid", EventId.value row.EventId) |> ignore
    cmd.Parameters.AddWithValue("$sid", SessionId.value row.SessionId) |> ignore
    cmd.Parameters.AddWithValue("$wt", WorktreePath.value row.WorktreePath) |> ignore
    cmd.Parameters.AddWithValue("$prov", providerText row.Provider) |> ignore
    cmd.Parameters.AddWithValue("$kind", row.Kind) |> ignore
    cmd.Parameters.AddWithValue("$status", statusText row.Status) |> ignore
    cmd.Parameters.AddWithValue("$skill", optToDb row.Skill) |> ignore
    cmd.Parameters.AddWithValue("$ts", isoUtc row.Ts) |> ignore

// Bind a session_status row's parameters onto a prepared command — shared by UpsertStatus and the
// transactional AppendAndUpsert.
let private bindUpsert (cmd: SqliteCommand) (stored: StoredStatus) =
    let s = stored.Status
    let umText, umTs = msgToDb s.LastUserMessage
    let amText, amTs = msgToDb s.LastAssistantMessage
    let itText, itTs = msgToDb s.Intent
    let ttText, ttTs = msgToDb s.Title
    cmd.Parameters.AddWithValue("$sid", SessionId.value stored.SessionId) |> ignore
    cmd.Parameters.AddWithValue("$wt", WorktreePath.value stored.WorktreePath) |> ignore
    cmd.Parameters.AddWithValue("$prov", providerText stored.Provider) |> ignore
    cmd.Parameters.AddWithValue("$status", statusText s.Status) |> ignore
    cmd.Parameters.AddWithValue("$skill", optToDb s.Skill) |> ignore
    cmd.Parameters.AddWithValue("$um", umText) |> ignore
    cmd.Parameters.AddWithValue("$uts", umTs) |> ignore
    cmd.Parameters.AddWithValue("$am", amText) |> ignore
    cmd.Parameters.AddWithValue("$ats", amTs) |> ignore
    cmd.Parameters.AddWithValue("$it", itText) |> ignore
    cmd.Parameters.AddWithValue("$its", itTs) |> ignore
    cmd.Parameters.AddWithValue("$tt", ttText) |> ignore
    cmd.Parameters.AddWithValue("$tts", ttTs) |> ignore
    cmd.Parameters.AddWithValue("$upd", isoUtc stored.UpdatedAt) |> ignore
    cmd.Parameters.AddWithValue("$seen", isoUtc stored.LastSeen) |> ignore

let private appendEvent (conn: SqliteConnection) (tx: SqliteTransaction) row =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- appendSql
    bindAppend cmd row
    cmd.ExecuteNonQuery() = 1

let private upsertObservationBounds
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

let private invalidateOverviewRollup
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    sourceTimestamp
    =
    let dirty = dirtyBoundary DateTimeOffset.UtcNow sourceTimestamp
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- invalidateOverviewRollupSql
    cmd.Parameters.AddWithValue("$dirty", toBucket dirty) |> ignore

    if cmd.ExecuteNonQuery() <> 1 then
        raise (InvalidDataException("Overview history publication state is missing."))

let private appendTaskSnapshot
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    ts
    tasks
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- appendTaskSnapshotSql
    cmd.Parameters.AddWithValue("$ts", isoUtc ts) |> ignore
    cmd.Parameters.AddWithValue("$tasks", serializeTasks tasks) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private queryLatestTaskSnapshot
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- queryLatestTaskSnapshotSql
    use reader = cmd.ExecuteReader()

    if reader.Read() then Some(parseIso (reader.GetString 0), parseTasks (reader.GetString 1))
    else None

let private appendTaskSnapshotIfChanged
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    ts
    tasks
    =
    match queryLatestTaskSnapshot conn tx with
    | Some (_, previous) when previous = tasks -> false
    | _ ->
        appendTaskSnapshot conn tx ts tasks
        invalidateOverviewRollup conn tx ts
        true

// --- Store ------------------------------------------------------------------------------------

/// True when `table` already has a column named `col`. Read via PRAGMA table_info (column index 1 is
/// the name); keeps the additive intent migration idempotent across restarts.
let private columnExists (conn: SqliteConnection) (table: string) (col: string) : bool =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"PRAGMA table_info(%s{table});"
    use reader = cmd.ExecuteReader()
    let rec scan () = reader.Read() && (reader.GetString 1 = col || scan ())
    scan ()

/// Idempotently add a nullable column to an existing table. SQLite has no `ADD COLUMN IF NOT EXISTS`,
/// so guard on table_info: a fresh DB already has the column from schemaSql (no-op), an upgraded DB
/// gets it added once. The column is nullable, so pre-existing rows simply read it as NULL.
let private addColumnIfMissing (conn: SqliteConnection) (table: string) (col: string) (decl: string) : unit =
    if not (columnExists conn table col) then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"ALTER TABLE %s{table} ADD COLUMN %s{col} %s{decl};"
        cmd.ExecuteNonQuery() |> ignore

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

let private readPublicationState (conn: SqliteConnection) : PublicationState option =
    use cmd = conn.CreateCommand()
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

let private countJsonIsValid tasksJson agentsJson =
    try
        let tasks = parseTasks tasksJson
        let agents = parseAgents agentsJson

        let taskKinds = tasks |> List.map _.Kind
        let agentKinds = agents |> List.map _.Kind

        tasks |> List.forall (fun count -> count.Count > 0)
        && agents |> List.forall (fun count -> count.Count > 0)
        && Set.count (Set.ofList taskKinds) = taskKinds.Length
        && Set.count (Set.ofList agentKinds) = agentKinds.Length
    with _ ->
        false

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

let private stagingRowsAreValid (conn: SqliteConnection) sourceGeneration =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT generation, bucket, tasks, agents FROM overview_history_staging;"
    use reader = cmd.ExecuteReader()

    readRows reader (fun row -> row.GetInt64 0, row.GetInt64 1, row.GetString 2, row.GetString 3) []
    |> List.forall (fun (generation, bucket, tasks, agents) ->
        generation >= 0L
        && generation <= sourceGeneration
        && tryFromBucket bucket |> Option.isSome
        && countJsonIsValid tasks agents)

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
        match readPublicationState conn with
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

let private ensureDerivedSchema conn =
    if derivedShapesAreCurrent conn then
        executeSql conn derivedSchemaSql

        if not (derivedDataIsValid conn) then
            resetDerivedSchema conn
    else
        resetDerivedSchema conn

/// SQLite (WAL) persistence for push-model session activity. Construct once per Treemon instance with
/// an instance-specific `dbPath` (created if its directory is missing). Thread-safe: every operation
/// runs on its own short-lived connection, so the single-writer mailbox and concurrent WAL readers
/// (restart rebuild, Overview history, prune timer) never share a connection. Dispose on shutdown.
type SessionActivityStore(dbPath: string) =

    do
        let dir = Path.GetDirectoryName dbPath

        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore

    // Pooling is off so each connection fully releases its file handle on close — reliable teardown on
    // Windows (which locks open DB files) and no pooled-connection surprises. The keep-alive below
    // keeps the file open (and WAL active) for the store's lifetime instead.
    let connString =
        SqliteConnectionStringBuilder(DataSource = dbPath, Pooling = false).ConnectionString

    // journal_mode=WAL is persisted in the DB header (set once, survives reopen); synchronous and
    // busy_timeout are per-connection, so they are (re)applied on every open. Re-asserting WAL each
    // time is a cheap no-op once the header says WAL.
    let openConn () =
        let c = new SqliteConnection(connString)
        c.Open()
        use cmd = c.CreateCommand()
        cmd.CommandText <- "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;"
        cmd.ExecuteNonQuery() |> ignore
        c

    // Held open for the store's lifetime: keeps the DB file (and its WAL) live between operations and
    // owns schema creation. Never used for queries (that would share one connection across threads).
    let keepAlive =
        let c = openConn ()
        (use cmd = c.CreateCommand()
         cmd.CommandText <- schemaSql + migrateSql
         cmd.ExecuteNonQuery() |> ignore)
        // Additive migration for DBs created before the intent/title columns existed (schemaSql adds
        // them only to a fresh table). Idempotent — a no-op once the columns are present.
        addColumnIfMissing c "session_status" "intent_text" "TEXT"
        addColumnIfMissing c "session_status" "intent_ts" "TEXT"
        addColumnIfMissing c "session_status" "title_text" "TEXT"
        addColumnIfMissing c "session_status" "title_ts" "TEXT"
        ensureDerivedSchema c
        c

    /// Run a related group of internal reads against one stable deferred WAL snapshot.
    member internal _.ReadSnapshot(read: SqliteConnection -> SqliteTransaction -> 'T) : 'T =
        use conn = openConn ()
        use tx = conn.BeginTransaction(deferred = true)
        let result = read conn tx
        tx.Commit()
        result

    member internal _.OverviewRollupState() : PublicationState =
        use conn = openConn ()

        match readPublicationState conn with
        | Some state when isSupportedState state -> state
        | _ ->
            raise (
                InvalidDataException(
                    "Overview history publication state has an unsupported schema or resolution."
                )
            )

    /// Insert-or-update a session's live row. Last-write-wins on `UpdatedAt`: a stale (older) report
    /// for an existing session is silently ignored (see upsertSql).
    member _.UpsertStatus(stored: StoredStatus) : unit =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- upsertSql
        bindUpsert cmd stored
        cmd.ExecuteNonQuery() |> ignore

    /// Append a raw event. Returns true if inserted, false if the event_id already existed
    /// (INSERT OR IGNORE dedupe).
    member _.AppendEvent(row: ActivityEventRow) : bool =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        let inserted = appendEvent conn tx row

        if inserted then
            upsertObservationBounds conn tx row.SessionId row.Ts
            invalidateOverviewRollup conn tx row.Ts

        tx.Commit()
        inserted

    /// Atomically append the raw event AND upsert the session's live row in ONE transaction on ONE
    /// connection, so the durable status can never diverge from the appended history. With the two on
    /// separate connections a failed upsert AFTER a committed append left the event_id permanently
    /// deduped on replay while the status never recovered; here a mid-pair failure rolls both back.
    /// Returns true when the event was newly inserted (upsert applied), false when the event_id
    /// already existed (a full idempotent no-op — nothing appended, nothing upserted).
    member _.AppendAndUpsert(row: ActivityEventRow, stored: StoredStatus) : bool =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        let inserted = appendEvent conn tx row

        if inserted then
            use upsertCmd = conn.CreateCommand()
            upsertCmd.Transaction <- tx
            upsertCmd.CommandText <- upsertSql
            bindUpsert upsertCmd stored
            upsertCmd.ExecuteNonQuery() |> ignore
            upsertObservationBounds conn tx row.SessionId row.Ts
            invalidateOverviewRollup conn tx row.Ts

        tx.Commit()
        inserted

    /// Advance `last_seen` and append the compact liveness point used by historical openness
    /// reconstruction. Both writes share one transaction so live state and history cannot diverge;
    /// stale or equal observations are a full no-op.
    member _.RecordLiveness(sessionId: SessionId, lastSeen: DateTimeOffset) : unit =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        let bind (cmd: SqliteCommand) =
            cmd.Transaction <- tx
            cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
            cmd.Parameters.AddWithValue("$seen", isoUtc lastSeen) |> ignore

        use touchCmd = conn.CreateCommand()
        touchCmd.CommandText <- touchSql
        bind touchCmd
        let advanced = touchCmd.ExecuteNonQuery() = 1

        if advanced then
            use livenessCmd = conn.CreateCommand()
            livenessCmd.CommandText <- appendLivenessSql
            bind livenessCmd
            let inserted = livenessCmd.ExecuteNonQuery() = 1

            if inserted then
                upsertObservationBounds conn tx sessionId lastSeen
                invalidateOverviewRollup conn tx lastSeen

        tx.Commit()

    /// Read one durable session row regardless of the live idle-window cutoff.
    member _.StatusBySession(sessionId: SessionId) : StoredStatus option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- statusBySessionSql
        cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(readStored reader) else None

    /// Restart rebuild: every session whose `last_seen` is within the idle window (i.e. still live),
    /// so cards are correct before any new event arrives.
    member _.LoadLiveStatuses(now: DateTimeOffset) : StoredStatus list =
        let cutoff = now - idleWindow
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- loadSql
        cmd.Parameters.AddWithValue("$cutoff", isoUtc cutoff) |> ignore
        use reader = cmd.ExecuteReader()

        readRows reader readStored []

    /// The newest stored session per worktree across ALL rows, IGNORING the idle window (unlike
    /// LoadLiveStatuses). The durable footer/resume substrate for cards: after a restart a worktree
    /// whose sessions last ran outside the idle window is absent from the live map, so its card would
    /// collapse to a blank NoSession and the resume button (which needs a retained LastUserMessage)
    /// would be UI-unreachable. Keyed by worktree_path; each value is that worktree's most-recent
    /// session (allStatusesSql is oldest-first, so the fold keeps the newest per key).
    member _.RetainedByWorktree() : Map<string, StoredStatus> =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- allStatusesSql
        use reader = cmd.ExecuteReader()

        readRows reader readStored []
        |> List.fold (fun acc s -> Map.add (WorktreePath.value s.WorktreePath) s acc) Map.empty

    /// Every stored session for a worktree, newest `last_seen` first, INDEPENDENT of the idle window
    /// (unlike LoadLiveStatuses). The RESUME substrate: after a restart a session last active >2h ago
    /// is absent from the idle-window live cache, so a resume pick over that cache returned None and
    /// resume wrongly fell back to `--continue` instead of `--resume <id>` (F10/C-02). Reading
    /// session_status directly by worktree_path returns those older sessions too (kept until the 14d
    /// retention prune), so `getLastSessionId` still finds the most-recent one across a restart.
    member _.StatusesForWorktree(worktreePath: WorktreePath) : StoredStatus list =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- worktreeStatusesSql
        cmd.Parameters.AddWithValue("$wt", WorktreePath.value worktreePath) |> ignore
        use reader = cmd.ExecuteReader()

        readRows reader readStored []

    /// Retention: drop events older than `cutoff` and session rows last seen before it. Returns the
    /// total number of rows deleted across both tables.
    member _.PruneOld(cutoff: DateTimeOffset) : int =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- pruneSql
        cmd.Parameters.AddWithValue("$cutoff", isoUtc cutoff) |> ignore
        let deleted = cmd.ExecuteNonQuery()
        tx.Commit()
        deleted

    /// History substrate: raw events with `ts` in [startTime, endTime], oldest first. WAL lets this
    /// run concurrently with the mailbox writer.
    member _.QueryWindow(startTime: DateTimeOffset, endTime: DateTimeOffset) : ActivityEventRow list =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- queryWindowSql
        cmd.Parameters.AddWithValue("$start", isoUtc startTime) |> ignore
        cmd.Parameters.AddWithValue("$end", isoUtc endTime) |> ignore
        use reader = cmd.ExecuteReader()

        readRows reader readEventRow []

    /// History events in [startTime, endTime], plus the latest pre-window status only for sessions
    /// observed during the window or its openness lookback.
    member _.QueryHistoryWindow(startTime: DateTimeOffset, endTime: DateTimeOffset) : ActivityEventRow list =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- queryHistoryWindowSql
        cmd.Parameters.AddWithValue("$lookback", isoUtc (startTime - openWindow)) |> ignore
        cmd.Parameters.AddWithValue("$start", isoUtc startTime) |> ignore
        cmd.Parameters.AddWithValue("$end", isoUtc endTime) |> ignore
        use reader = cmd.ExecuteReader()

        readRows reader readEventRow []

    /// Liveness-only points in [startTime, endTime], oldest first. These remain separate from
    /// activity_events because they extend openness without changing status or skill.
    member _.QueryLiveness(startTime: DateTimeOffset, endTime: DateTimeOffset) : (SessionId * DateTimeOffset) list =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- queryLivenessSql
        cmd.Parameters.AddWithValue("$start", isoUtc startTime) |> ignore
        cmd.Parameters.AddWithValue("$end", isoUtc endTime) |> ignore
        use reader = cmd.ExecuteReader()

        readRows reader (fun row -> SessionId(row.GetString 0), parseIso (row.GetString 1)) []

    /// Read every durable input for one Overview history response from one WAL snapshot. The
    /// optional boundary callback is an internal deterministic test seam for committing a writer
    /// after the status read but before the liveness read.
    member internal _.QueryOverviewHistoryInputs
        (
            startTime: DateTimeOffset,
            endTime: DateTimeOffset,
            ?beforeLivenessRead: unit -> unit
        ) : OverviewHistoryInputs =
        use conn = openConn ()
        use tx = conn.BeginTransaction(deferred = true)

        let queryRows sql bind map =
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- sql
            bind cmd
            use reader = cmd.ExecuteReader()
            readRows reader map []

        let queryRow sql bind map =
            queryRows sql bind map |> List.tryHead

        let bindStart (cmd: SqliteCommand) =
            cmd.Parameters.AddWithValue("$start", isoUtc startTime) |> ignore

        let bindRange (cmd: SqliteCommand) =
            bindStart cmd
            cmd.Parameters.AddWithValue("$end", isoUtc endTime) |> ignore

        let taskSnapshots =
            (queryRow
                queryTaskSnapshotBeforeSql
                bindStart
                (fun row -> parseIso (row.GetString 0), parseTasks (row.GetString 1))
             |> Option.toList)
            @ queryRows
                queryTaskSnapshotsSql
                bindRange
                (fun row -> parseIso (row.GetString 0), parseTasks (row.GetString 1))

        let events =
            queryRows
                queryHistoryWindowSql
                (fun cmd ->
                    cmd.Parameters.AddWithValue("$lookback", isoUtc (startTime - openWindow)) |> ignore
                    bindRange cmd)
                readEventRow

        defaultArg beforeLivenessRead ignore ()

        let liveness =
            queryRows
                queryLivenessSql
                (fun cmd ->
                    cmd.Parameters.AddWithValue("$start", isoUtc (startTime - openWindow)) |> ignore
                    cmd.Parameters.AddWithValue("$end", isoUtc endTime) |> ignore)
                (fun row -> SessionId(row.GetString 0), parseIso (row.GetString 1))

        tx.Commit()

        { TaskSnapshots = taskSnapshots
          Events = events
          Liveness = liveness }

    /// Append one Tasks (beads) history snapshot — the count-only projection, logged only on change by
    /// the scheduler. The Agents dimension is NOT stored here: it is derived on read from
    /// `activity_events` (the push event stream), so only Tasks are snapshot-based.
    member _.AppendTaskSnapshot(ts: DateTimeOffset, tasks: OverviewData.TaskCount list) : unit =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        appendTaskSnapshot conn tx ts tasks
        invalidateOverviewRollup conn tx ts
        tx.Commit()

    member _.AppendTaskSnapshotIfChanged(ts: DateTimeOffset, tasks: OverviewData.TaskCount list) : bool =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        let appended = appendTaskSnapshotIfChanged conn tx ts tasks
        tx.Commit()
        appended

    /// The Tasks history substrate: task snapshots with `ts` in [startTime, endTime], oldest first.
    /// Merged with the event-derived Agents history into the OverviewSnapshot stream on read.
    member _.QueryTaskSnapshots(startTime: DateTimeOffset, endTime: DateTimeOffset) : (DateTimeOffset * OverviewData.TaskCount list) list =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- queryTaskSnapshotsSql
        cmd.Parameters.AddWithValue("$start", isoUtc startTime) |> ignore
        cmd.Parameters.AddWithValue("$end", isoUtc endTime) |> ignore
        use reader = cmd.ExecuteReader()

        readRows reader (fun row -> parseIso (row.GetString 0), parseTasks (row.GetString 1)) []

    member _.QueryLatestTaskSnapshot() : (DateTimeOffset * OverviewData.TaskCount list) option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- queryLatestTaskSnapshotSql
        use reader = cmd.ExecuteReader()

        if reader.Read() then Some(parseIso (reader.GetString 0), parseTasks (reader.GetString 1))
        else None

    member _.QueryTaskSnapshotBefore(startTime: DateTimeOffset) : (DateTimeOffset * OverviewData.TaskCount list) option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- queryTaskSnapshotBeforeSql
        cmd.Parameters.AddWithValue("$start", isoUtc startTime) |> ignore
        use reader = cmd.ExecuteReader()

        if reader.Read() then Some(parseIso (reader.GetString 0), parseTasks (reader.GetString 1))
        else None

    interface IDisposable with
        member _.Dispose() = keepAlive.Dispose()
