module Server.SessionActivityStore

open System
open System.IO
open Microsoft.Data.Sqlite
open Shared
open Server.SessionActivity
open Server.SqliteStorage

// The durable mirror behind the push-model live state. The SessionActivity mailbox (single writer)
// upserts the per-session fold result and appends accepted lifecycle events:
//
//   session_status  — one row per session: the latest fold state. Read back on restart to rebuild the
//                     live Map before serving (loadLiveStatuses), so cards are correct immediately.
//   activity_events — accepted lifecycle events keyed by event_id. INSERT OR IGNORE makes a replay a
//                     full no-op while retention bounds the durable idempotency window.
//
// WAL journalling lets restart/resume reads run concurrently with the mailbox writer with no lock
// contention; the writer being single means status upserts never race each other. The SQLite file
// path is instance-specific (keyed by the server's data dir / port) so a side-by-side validation
// instance never collides with main. Overview history is stored independently in overview_snapshots.

// --- Row shapes -------------------------------------------------------------------------------

/// One session_status row: the per-session fold result plus the timestamps the store needs —
/// `UpdatedAt` (the OccurredAt of the last applied STATUS event; drives status last-write-wins) and
/// `LastSeen` (the last heartbeat; drives freshness + the live window on restart). `ContextUsageAt`
/// is the OccurredAt of the last applied `usage_info` gauge — a SEPARATE last-write-wins clock so the
/// context donut is ordered independently of status and never shares the status LWW clock (a usage
/// report must not block a slightly-earlier status transition, nor be discarded by one). It is
/// server-internal ordering state persisted alongside `ContextUsage`, but never sent on the wire.
type StoredStatus =
    { SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Status: SessionStatus
      UpdatedAt: DateTimeOffset
      LastSeen: DateTimeOffset
      ContextUsageAt: DateTimeOffset option }

/// One activity_events row: a single accepted event. `Status`/`Skill` retain the fold result after
/// applying it, preserving the existing durable event shape while event_id supplies idempotency.
type ActivityEventRow =
    { EventId: EventId
      SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Kind: string
      Status: SessionLevelStatus
      Skill: string option
      Ts: DateTimeOffset }

// --- Serialisation helpers --------------------------------------------------------------------

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

let private contextToDb (stored: StoredStatus) : obj * obj * obj =
    match stored.Status.ContextUsage, stored.ContextUsageAt with
    | None, None -> box DBNull.Value, box DBNull.Value, box DBNull.Value
    | Some usage, Some usageAt -> box usage.CurrentTokens, box usage.TokenLimit, box (isoUtc usageAt)
    | _ -> invalidArg (nameof stored) "ContextUsage and ContextUsageAt must both be present or absent"

let private readOptStr (r: SqliteDataReader) (i: int) =
    if r.IsDBNull i then None else Some(r.GetString i)

let private readContextUsage (r: SqliteDataReader) currentTokensIndex tokenLimitIndex usageAtIndex =
    match r.IsDBNull currentTokensIndex, r.IsDBNull tokenLimitIndex, r.IsDBNull usageAtIndex with
    | true, true, true -> None, None
    | false, false, false ->
        let usage =
            { CurrentTokens = r.GetInt32 currentTokensIndex
              TokenLimit = r.GetInt32 tokenLimitIndex }

        Some usage, Some(parseIso (r.GetString usageAtIndex))
    | _ -> failwith $"{nameof StoredStatus}: incomplete persisted context usage"

/// Reconstruct a `Message option` from a text column + a timestamp column; present only when both
/// are non-NULL (they are written together, so this is really an all-or-nothing pair).
let private readOptMsg (r: SqliteDataReader) (iText: int) (iTs: int) : Message option =
    match readOptStr r iText, readOptStr r iTs with
    | Some t, Some ts -> Some { Text = t; At = parseIso ts }
    | _ -> None

let private readStored (r: SqliteDataReader) : StoredStatus =
    let contextUsage, contextUsageAt = readContextUsage r 15 16 17

    { SessionId = SessionId(r.GetString 0)
      WorktreePath = WorktreePath(r.GetString 1)
      Provider = parseProvider (r.GetString 2)
      Status =
        { Status = parseStatus (r.GetString 3)
          Skill = readOptStr r 4
          Intent = readOptMsg r 9 10
          Title = readOptMsg r 11 12
          LastUserMessage = readOptMsg r 5 6
          LastAssistantMessage = readOptMsg r 7 8
          ContextUsage = contextUsage }
      UpdatedAt = parseIso (r.GetString 13)
      LastSeen = parseIso (r.GetString 14)
      ContextUsageAt = contextUsageAt }

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
    last_seen     TEXT NOT NULL,
    context_current_tokens INTEGER,
    context_token_limit     INTEGER,
    context_usage_at        TEXT
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
"""

let private additiveColumnMigrations =
    [ "intent_text", "TEXT"
      "intent_ts", "TEXT"
      "title_text", "TEXT"
      "title_ts", "TEXT"
      "context_current_tokens", "INTEGER"
      "context_token_limit", "INTEGER"
      "context_usage_at", "TEXT" ]

let rec private readColumnNames (reader: SqliteDataReader) names =
    if reader.Read() then
        readColumnNames reader (Set.add (reader.GetString 1) names)
    else
        names

let private ensureAdditiveColumns (conn: SqliteConnection) =
    let existingColumns =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA table_info(session_status);"
        use reader = cmd.ExecuteReader()
        readColumnNames reader Set.empty

    let migrationSql =
        additiveColumnMigrations
        |> List.choose (fun (columnName, declaration) ->
            if Set.contains columnName existingColumns then
                None
            else
                Some $"ALTER TABLE session_status ADD COLUMN %s{columnName} %s{declaration};")
        |> String.concat Environment.NewLine

    if migrationSql <> "" then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- migrationSql
        cmd.ExecuteNonQuery() |> ignore

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
     last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
     intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
     context_current_tokens, context_token_limit, context_usage_at)
VALUES ($sid, $wt, $prov, $status, $skill, $um, $uts, $am, $ats,
        $it, $its, $tt, $tts, $upd, $seen,
        $contextCurrent, $contextLimit, $contextAt)
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
// upsert+append, so they refresh openness without moving the last-write-wins clock.
let private touchSql =
    """
UPDATE session_status SET last_seen = $seen WHERE session_id = $sid AND last_seen < $seen;
"""

let private upsertContextUsageSql =
    """
INSERT INTO session_status
    (session_id, worktree_path, provider, status, current_skill,
     last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
     intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
     context_current_tokens, context_token_limit, context_usage_at)
VALUES ($sid, $wt, $prov, $status, $skill, $um, $uts, $am, $ats,
        $it, $its, $tt, $tts, $upd, $seen,
        $contextCurrent, $contextLimit, $contextAt)
ON CONFLICT(session_id) DO UPDATE SET
    context_current_tokens = excluded.context_current_tokens,
    context_token_limit = excluded.context_token_limit,
    context_usage_at = excluded.context_usage_at,
    last_seen = CASE
        WHEN session_status.last_seen < excluded.last_seen THEN excluded.last_seen
        ELSE session_status.last_seen
    END
WHERE session_status.context_usage_at IS NULL
   OR session_status.context_usage_at <= excluded.context_usage_at;
"""

let private loadSql =
    """
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
       intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
       context_current_tokens, context_token_limit, context_usage_at
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
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
       intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
       context_current_tokens, context_token_limit, context_usage_at
FROM session_status
WHERE worktree_path = $wt
ORDER BY last_seen DESC;
"""

// Preserve the established retention behavior: events older than the cutoff are deleted except for
// the latest old event belonging to a session row that is itself still retained.
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

DELETE FROM session_status WHERE last_seen < $cutoff;
"""

// Every stored session across all worktrees, oldest `last_seen` first (so a fold keyed by
// worktree_path keeps the newest per worktree). NO idle-window filter — the durable footer/resume
// substrate for cards whose sessions have aged out of the live map after a restart.
let private allStatusesSql =
    """
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
       intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
       context_current_tokens, context_token_limit, context_usage_at
FROM session_status
ORDER BY last_seen;
"""

let private statusBySessionSql =
    """
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
       intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
       context_current_tokens, context_token_limit, context_usage_at
FROM session_status
WHERE session_id = $sid
LIMIT 1;
"""

// --- Reader / binder helpers ------------------------------------------------------------------

// Bind an activity_events row's parameters for the transactional AppendAndUpsert path.
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
    let contextCurrent, contextLimit, contextAt = contextToDb stored
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
    cmd.Parameters.AddWithValue("$contextCurrent", contextCurrent) |> ignore
    cmd.Parameters.AddWithValue("$contextLimit", contextLimit) |> ignore
    cmd.Parameters.AddWithValue("$contextAt", contextAt) |> ignore

let private readStoredBySession
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    (sessionId: SessionId)
    : StoredStatus
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- statusBySessionSql
    cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
    use reader = cmd.ExecuteReader()

    if reader.Read() then
        readStored reader
    else
        failwith $"{nameof StoredStatus}: persisted session row missing"

let private appendEvent (conn: SqliteConnection) (tx: SqliteTransaction) row =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- appendSql
    bindAppend cmd row
    cmd.ExecuteNonQuery() = 1

// --- Store ------------------------------------------------------------------------------------

/// SQLite (WAL) persistence for push-model session activity. Construct once per Treemon instance with
/// an instance-specific `dbPath` (created if its directory is missing). Thread-safe: every operation
/// runs on its own short-lived connection, so the single-writer mailbox and concurrent WAL readers
/// (restart rebuild, resume lookup, prune timer) never share a connection. The optional observer runs
/// after connection-local PRAGMAs and before store SQL so diagnostics can attach per connection
/// without changing production callers. Dispose on shutdown.
type SessionActivityStore
    (
        dbPath: string,
        ?connectionOpened: SqliteConnection -> unit
    ) =

    do
        let dir = Path.GetDirectoryName dbPath

        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore

    let connectionOpened = defaultArg connectionOpened ignore

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

        try
            connectionOpened c
            c
        with _ ->
            c.Dispose()
            reraise ()

    // Held open for the store's lifetime: keeps the DB file (and its WAL) live between operations and
    // owns schema creation. Never used for queries (that would share one connection across threads).
    let keepAlive =
        let c = openConn ()
        (use cmd = c.CreateCommand()
         cmd.CommandText <- schemaSql + migrateSql
         cmd.ExecuteNonQuery() |> ignore)
        ensureAdditiveColumns c
        c

    /// Insert-or-update a session's live row. Last-write-wins on `UpdatedAt`: a stale (older) report
    /// for an existing session is silently ignored (see upsertSql).
    member _.UpsertStatus(stored: StoredStatus) : unit =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- upsertSql
        bindUpsert cmd stored
        cmd.ExecuteNonQuery() |> ignore

    /// Atomically append the accepted event AND upsert the session's live row in ONE transaction on
    /// ONE connection, so the durable status can never diverge from the idempotency record. With the
    /// two on separate connections a failed upsert AFTER a committed append left the event_id
    /// permanently deduped on replay while the status never recovered; here a mid-pair failure rolls
    /// both back.
    /// Returns the authoritative persisted status when the event was newly inserted, or None when
    /// the event_id already existed (a full idempotent no-op — nothing appended or upserted).
    member _.AppendAndUpsert(row: ActivityEventRow, stored: StoredStatus) : StoredStatus option =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        let inserted = appendEvent conn tx row

        let persisted =
            if inserted then
                use upsertCmd = conn.CreateCommand()
                upsertCmd.Transaction <- tx
                upsertCmd.CommandText <- upsertSql
                bindUpsert upsertCmd stored
                upsertCmd.ExecuteNonQuery() |> ignore
                Some(readStoredBySession conn tx stored.SessionId)
            else
                None

        tx.Commit()
        persisted

    /// Advance `last_seen` for openness without moving the lifecycle ordering clock. Stale or equal
    /// observations are a full no-op.
    member _.RecordLiveness(sessionId: SessionId, lastSeen: DateTimeOffset) : unit =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- touchSql
        cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
        cmd.Parameters.AddWithValue("$seen", isoUtc lastSeen) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Persist the latest accepted context-window gauge and last_seen, inserting the full session
    /// snapshot when a retained in-memory session outlives its pruned row. Returns the authoritative
    /// persisted state, including a newer gauge that may already have won the independent usage clock.
    member _.UpsertContextUsage(stored: StoredStatus) : StoredStatus =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- upsertContextUsageSql
        bindUpsert cmd stored
        cmd.ExecuteNonQuery() |> ignore

        let persisted = readStoredBySession conn tx stored.SessionId
        tx.Commit()
        persisted

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

    interface IDisposable with
        member _.Dispose() = keepAlive.Dispose()
