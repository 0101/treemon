module Server.SessionActivityStore

open System
open System.IO
open System.Globalization
open Microsoft.Data.Sqlite
open Shared
open Server.SessionActivity

// The durable mirror behind the push-model live state. The SessionActivity mailbox (single writer)
// upserts the per-session fold result and appends the raw event to two tables:
//
//   session_status  — one row per session: the latest fold state. Read back on restart to rebuild the
//                     live Map before serving (loadLiveStatuses), so cards are correct immediately.
//   activity_events — the append-only raw stream: the substrate the Overview history aggregates on
//                     read (queryWindow), and the source of INSERT OR IGNORE idempotency (event_id PK).
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

// --- Serialisation helpers --------------------------------------------------------------------

// Timestamps are stored as UTC round-trip ("O") strings. Normalising to UTC gives every value the
// same fixed-width "+00:00" suffix, so lexical string comparison equals chronological order — which
// is what the `ts >= $start` window query and the `last_seen >= $cutoff` live filter rely on.
let private isoUtc (dto: DateTimeOffset) =
    dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

let private parseIso (s: string) =
    DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

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

let private readEventRow (r: SqliteDataReader) : ActivityEventRow =
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
ORDER BY ts;
"""

// pruneOld trims both tables past the retention cutoff: the append-only event stream (the unbounded
// one) plus long-dead session rows well outside any live window.
let private pruneSql =
    """
DELETE FROM activity_events WHERE ts < $cutoff;
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
let rec private readRows (reader: SqliteDataReader) (map: SqliteDataReader -> 'T) (acc: 'T list) : 'T list =
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
        c

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
        use cmd = conn.CreateCommand()
        cmd.CommandText <- appendSql
        bindAppend cmd row
        cmd.ExecuteNonQuery() = 1

    /// Atomically append the raw event AND upsert the session's live row in ONE transaction on ONE
    /// connection, so the durable status can never diverge from the appended history. With the two on
    /// separate connections a failed upsert AFTER a committed append left the event_id permanently
    /// deduped on replay while the status never recovered; here a mid-pair failure rolls both back.
    /// Returns true when the event was newly inserted (upsert applied), false when the event_id
    /// already existed (a full idempotent no-op — nothing appended, nothing upserted).
    member _.AppendAndUpsert(row: ActivityEventRow, stored: StoredStatus) : bool =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        use appendCmd = conn.CreateCommand()
        appendCmd.Transaction <- tx
        appendCmd.CommandText <- appendSql
        bindAppend appendCmd row
        let inserted = appendCmd.ExecuteNonQuery() = 1

        if inserted then
            use upsertCmd = conn.CreateCommand()
            upsertCmd.Transaction <- tx
            upsertCmd.CommandText <- upsertSql
            bindUpsert upsertCmd stored
            upsertCmd.ExecuteNonQuery() |> ignore

        tx.Commit()
        inserted

    /// Advance a session's `last_seen` (openness heartbeat) without touching status/updated_at or the
    /// message fields. Only moves it forward; a no-op if the row is absent or already fresher.
    member _.TouchLastSeen(sessionId: SessionId, lastSeen: DateTimeOffset) : unit =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- touchSql
        cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
        cmd.Parameters.AddWithValue("$seen", isoUtc lastSeen) |> ignore
        cmd.ExecuteNonQuery() |> ignore

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
        use cmd = conn.CreateCommand()
        cmd.CommandText <- pruneSql
        cmd.Parameters.AddWithValue("$cutoff", isoUtc cutoff) |> ignore
        cmd.ExecuteNonQuery()

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

    interface IDisposable with
        member _.Dispose() = keepAlive.Dispose()
