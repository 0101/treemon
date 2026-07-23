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
//   background_agent_lifecycle — independent per-tool start/finish clocks used to reconstruct active
//                     agents without trusting report arrival order. Completed clocks are bounded
//                     tombstones and never enter the live SessionStatus projection.
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
/// server-internal ordering state persisted alongside `ContextUsage`, but never sent on the wire.
/// Ask-user request/completion clocks live in `SessionStatus` and are persisted independently too.
type StoredStatus =
    { SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Status: SessionStatus
      UpdatedAt: DateTimeOffset
      LastSeen: DateTimeOffset
      ContextUsageAt: DateTimeOffset option }

module StoredStatus =
    let activityOrderKey (stored: StoredStatus) =
        stored.UpdatedAt, SessionId.value stored.SessionId

    /// `LastSeen` is liveness-only, so it must never decide which session owns shared content.
    let tryMostRecentActivity sessions =
        sessions
        |> List.sortByDescending activityOrderKey
        |> List.tryHead

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

let private timestampToDb =
    Option.map isoUtc >> optToDb

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

let private readOptTimestamp (r: SqliteDataReader) i =
    readOptStr r i |> Option.map parseIso

let private readActiveBackgroundAgent (r: SqliteDataReader) =
    r.GetString 0, parseIso (r.GetString 1)

let private readSessionActiveBackgroundAgent (r: SqliteDataReader) =
    SessionId(r.GetString 0), (r.GetString 1, parseIso (r.GetString 2))

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
          ContextUsage = contextUsage
          AwaitingUserSince = readOptTimestamp r 18
          UserInputCompletedAt = readOptTimestamp r 19
          BackgroundAgents = Map.empty }
      UpdatedAt = parseIso (r.GetString 13)
      LastSeen = parseIso (r.GetString 14)
      ContextUsageAt = contextUsageAt }

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
    last_seen     TEXT NOT NULL,
    context_current_tokens INTEGER,
    context_token_limit     INTEGER,
    context_usage_at        TEXT,
    awaiting_user_since     TEXT,
    user_input_completed_at TEXT,
    background_agent_replay_after TEXT
);
CREATE INDEX IF NOT EXISTS ix_status_worktree ON session_status(worktree_path);
CREATE INDEX IF NOT EXISTS ix_status_worktree_activity
ON session_status(worktree_path, updated_at DESC, session_id DESC);

CREATE TABLE IF NOT EXISTS background_agent_lifecycle (
    session_id   TEXT NOT NULL,
    tool_call_id TEXT NOT NULL,
    started_at   TEXT,
    finished_at  TEXT,
    PRIMARY KEY(session_id, tool_call_id)
);
CREATE INDEX IF NOT EXISTS ix_background_agent_finished
ON background_agent_lifecycle(finished_at)
WHERE finished_at IS NOT NULL;

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

let private additiveColumnMigrations =
    [ "intent_text", "TEXT"
      "intent_ts", "TEXT"
      "title_text", "TEXT"
      "title_ts", "TEXT"
      "context_current_tokens", "INTEGER"
      "context_token_limit", "INTEGER"
      "context_usage_at", "TEXT"
      "awaiting_user_since", "TEXT"
      "user_input_completed_at", "TEXT"
      "background_agent_replay_after", "TEXT" ]

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

// Bounded normalisation of legacy rows. Retired "done" values become idle; pre-clock waiting rows
// become an idle base plus an open request at their lifecycle timestamp. Both updates are idempotent.
let private migrateSql =
    """
UPDATE session_status SET status = 'idle' WHERE status = 'done';
UPDATE activity_events SET status = 'idle' WHERE status = 'done';
UPDATE session_status
SET status = 'idle', awaiting_user_since = updated_at
WHERE status = 'waiting_for_user' AND awaiting_user_since IS NULL;
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
     context_current_tokens, context_token_limit, context_usage_at,
     awaiting_user_since, user_input_completed_at)
VALUES ($sid, $wt, $prov, $status, $skill, $um, $uts, $am, $ats,
        $it, $its, $tt, $tts, $upd, $seen,
        $contextCurrent, $contextLimit, $contextAt, $awaitingUserSince, $userInputCompletedAt)
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
    last_seen     = excluded.last_seen,
    awaiting_user_since = excluded.awaiting_user_since,
    user_input_completed_at = excluded.user_input_completed_at
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

let private upsertBackgroundAgentSql =
    """
INSERT INTO background_agent_lifecycle
    (session_id, tool_call_id, started_at, finished_at)
VALUES ($sid, $toolCallId, $startedAt, $finishedAt)
ON CONFLICT(session_id, tool_call_id) DO UPDATE SET
    started_at = CASE
        WHEN excluded.started_at IS NULL THEN background_agent_lifecycle.started_at
        WHEN background_agent_lifecycle.started_at IS NULL
          OR background_agent_lifecycle.started_at < excluded.started_at
            THEN excluded.started_at
        ELSE background_agent_lifecycle.started_at
    END,
    finished_at = CASE
        WHEN excluded.finished_at IS NULL THEN background_agent_lifecycle.finished_at
        WHEN background_agent_lifecycle.finished_at IS NULL
          OR background_agent_lifecycle.finished_at < excluded.finished_at
            THEN excluded.finished_at
        ELSE background_agent_lifecycle.finished_at
    END;
"""

let private backgroundAgentsBySessionSql =
    """
SELECT tool_call_id, started_at
FROM background_agent_lifecycle
WHERE session_id = $sid
  AND started_at IS NOT NULL
  AND (finished_at IS NULL OR started_at > finished_at)
ORDER BY tool_call_id;
"""

let private closeActiveBackgroundAgentsSql =
    """
UPDATE background_agent_lifecycle
SET finished_at = $closedAt
WHERE session_id = $sid
  AND started_at IS NOT NULL
  AND started_at < $closedAt
  AND (finished_at IS NULL OR started_at > finished_at);
"""

let private liveBackgroundAgentsSql =
    """
SELECT lifecycle.session_id, lifecycle.tool_call_id, lifecycle.started_at
FROM background_agent_lifecycle AS lifecycle
INNER JOIN session_status AS status ON status.session_id = lifecycle.session_id
WHERE status.last_seen >= $cutoff
  AND lifecycle.started_at IS NOT NULL
  AND (lifecycle.finished_at IS NULL OR lifecycle.started_at > lifecycle.finished_at)
ORDER BY lifecycle.session_id, lifecycle.tool_call_id;
"""

let private backgroundAgentReplayAfterSql =
    """
SELECT background_agent_replay_after
FROM session_status
WHERE session_id = $sid
LIMIT 1;
"""

let private advanceSessionBackgroundAgentReplaySql =
    """
UPDATE session_status
SET background_agent_replay_after = $replayAfter
WHERE session_id = $sid
  AND (background_agent_replay_after IS NULL
       OR background_agent_replay_after < $replayAfter);
"""

let private upsertContextUsageSql =
    """
INSERT INTO session_status
    (session_id, worktree_path, provider, status, current_skill,
     last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
     intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
     context_current_tokens, context_token_limit, context_usage_at,
     awaiting_user_since, user_input_completed_at)
VALUES ($sid, $wt, $prov, $status, $skill, $um, $uts, $am, $ats,
        $it, $its, $tt, $tts, $upd, $seen,
        $contextCurrent, $contextLimit, $contextAt, $awaitingUserSince, $userInputCompletedAt)
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
       context_current_tokens, context_token_limit, context_usage_at,
       awaiting_user_since, user_input_completed_at
FROM session_status
WHERE last_seen >= $cutoff
ORDER BY last_seen;
"""

// Resume only needs the durable identity, not the full status aggregate or lifecycle projection.
let private latestSessionIdForWorktreeSql =
    """
SELECT session_id
FROM session_status
WHERE worktree_path = $wt
ORDER BY updated_at DESC, session_id DESC
LIMIT 1;
"""

let private queryWindowSql =
    """
SELECT event_id, session_id, worktree_path, provider, kind, status, skill, ts
FROM activity_events
WHERE ts >= $start AND ts <= $end
ORDER BY ts;
"""

// The replay floor and terminal tombstone deletion move together in one transaction. Once a
// completed clock leaves the bounded replay window, an event at or before the floor can still be
// appended to history but can no longer recreate current lifecycle state.
let private advanceBackgroundAgentReplaySql =
    """
UPDATE session_status
SET background_agent_replay_after = $cutoff
WHERE background_agent_replay_after IS NULL
   OR background_agent_replay_after < $cutoff;
"""

let private pruneCompletedBackgroundAgentsSql =
    """
DELETE FROM background_agent_lifecycle
WHERE finished_at IS NOT NULL
  AND (started_at IS NULL OR started_at <= finished_at)
  AND finished_at <= $cutoff;
"""

let private pruneOldSessionBackgroundAgentsSql =
    """
DELETE FROM background_agent_lifecycle
WHERE session_id IN (
    SELECT session_id FROM session_status WHERE last_seen < $cutoff
);
"""

let private pruneOldEventsSql = "DELETE FROM activity_events WHERE ts < $cutoff;"

let private pruneOldStatusesSql = "DELETE FROM session_status WHERE last_seen < $cutoff;"

// One durable footer representative per worktree, selected before rows cross the SQLite boundary.
// Lifecycle hydration is intentionally absent: retained rows supply footer metadata, while any live
// copy of the same session is merged separately with its authoritative active-agent projection.
let private retainedByWorktreeSql =
    """
WITH ranked AS (
    SELECT session_id, worktree_path, provider, status, current_skill,
           last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
           intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
           context_current_tokens, context_token_limit, context_usage_at,
           awaiting_user_since, user_input_completed_at,
           ROW_NUMBER() OVER (
               PARTITION BY worktree_path
               ORDER BY updated_at DESC, session_id DESC
           ) AS activity_rank
    FROM session_status
)
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
       intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
       context_current_tokens, context_token_limit, context_usage_at,
       awaiting_user_since, user_input_completed_at
FROM ranked
WHERE activity_rank = 1;
"""

let private statusBySessionSql =
    """
SELECT session_id, worktree_path, provider, status, current_skill,
       last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
       intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
       context_current_tokens, context_token_limit, context_usage_at,
       awaiting_user_since, user_input_completed_at
FROM session_status
WHERE session_id = $sid
LIMIT 1;
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
    cmd.Parameters.AddWithValue("$awaitingUserSince", timestampToDb s.AwaitingUserSince) |> ignore
    cmd.Parameters.AddWithValue("$userInputCompletedAt", timestampToDb s.UserInputCompletedAt) |> ignore

let private readBackgroundAgents
    (observeBackgroundAgentRead: unit -> unit)
    (conn: SqliteConnection)
    (tx: SqliteTransaction option)
    (sessionId: SessionId)
    : Map<string, DateTimeOffset>
    =
    observeBackgroundAgentRead ()
    use cmd = conn.CreateCommand()
    tx |> Option.iter (fun transaction -> cmd.Transaction <- transaction)
    cmd.CommandText <- backgroundAgentsBySessionSql
    cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
    use reader = cmd.ExecuteReader()

    readRows reader readActiveBackgroundAgent [] |> Map.ofList

let private readLiveBackgroundAgents
    (observeBackgroundAgentRead: unit -> unit)
    (conn: SqliteConnection)
    (cutoff: DateTimeOffset)
    : Map<SessionId, Map<string, DateTimeOffset>>
    =
    observeBackgroundAgentRead ()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- liveBackgroundAgentsSql
    cmd.Parameters.AddWithValue("$cutoff", isoUtc cutoff) |> ignore
    use reader = cmd.ExecuteReader()

    readRows reader readSessionActiveBackgroundAgent []
    |> List.groupBy fst
    |> List.map (fun (sessionId, agents) -> sessionId, agents |> List.map snd |> Map.ofList)
    |> Map.ofList

let private readBackgroundAgentReplayAfter
    (conn: SqliteConnection)
    (tx: SqliteTransaction option)
    (sessionId: SessionId)
    : DateTimeOffset option
    =
    use cmd = conn.CreateCommand()
    tx |> Option.iter (fun transaction -> cmd.Transaction <- transaction)
    cmd.CommandText <- backgroundAgentReplayAfterSql
    cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
    use reader = cmd.ExecuteReader()

    if reader.Read() then readOptTimestamp reader 0 else None

let private startAfterReplayFloor replayAfter startedAt =
    match replayAfter, startedAt with
    | Some floor, Some at when at <= floor -> None
    | _, value -> value

let private lifecycleAfterReplayFloor replayAfter lifecycle =
    let eligible =
        { StartedAt = startAfterReplayFloor replayAfter lifecycle.StartedAt
          // Terminal clocks only deactivate state, so even a delayed terminal outside the start
          // replay window is safe and may close an old still-active row.
          FinishedAt = lifecycle.FinishedAt }

    match eligible.StartedAt, eligible.FinishedAt with
    | None, None -> None
    | _ -> Some eligible

let private attachBackgroundAgents
    (observeBackgroundAgentRead: unit -> unit)
    (conn: SqliteConnection)
    (stored: StoredStatus)
    =
    { stored with
        Status.BackgroundAgents =
            readBackgroundAgents observeBackgroundAgentRead conn None stored.SessionId }

let private attachBackgroundAgentsFrom
    (agentsBySession: Map<SessionId, Map<string, DateTimeOffset>>)
    (stored: StoredStatus)
    =
    { stored with
        Status.BackgroundAgents =
            agentsBySession
            |> Map.tryFind stored.SessionId
            |> Option.defaultValue Map.empty }

let private readStoredBySession
    (observeBackgroundAgentRead: unit -> unit)
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    (sessionId: SessionId)
    : StoredStatus
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- statusBySessionSql
    cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
    let stored =
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(readStored reader) else None

    match stored with
    | Some row ->
        { row with
            Status.BackgroundAgents =
                readBackgroundAgents observeBackgroundAgentRead conn (Some tx) row.SessionId }
    | None -> failwith $"{nameof StoredStatus}: persisted session row missing"

let private upsertBackgroundAgent
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    (sessionId: SessionId)
    (toolCallId: string)
    (lifecycle: BackgroundAgentLifecycle)
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- upsertBackgroundAgentSql
    cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
    cmd.Parameters.AddWithValue("$toolCallId", toolCallId) |> ignore
    cmd.Parameters.AddWithValue("$startedAt", timestampToDb lifecycle.StartedAt) |> ignore
    cmd.Parameters.AddWithValue("$finishedAt", timestampToDb lifecycle.FinishedAt) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private advanceSessionBackgroundAgentReplay
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    (sessionId: SessionId)
    (replayAfter: DateTimeOffset)
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- advanceSessionBackgroundAgentReplaySql
    cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
    cmd.Parameters.AddWithValue("$replayAfter", isoUtc replayAfter) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private closeActiveBackgroundAgents
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    (sessionId: SessionId)
    (closedAt: DateTimeOffset)
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- closeActiveBackgroundAgentsSql
    cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
    cmd.Parameters.AddWithValue("$closedAt", isoUtc closedAt) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// --- Store ------------------------------------------------------------------------------------

/// SQLite (WAL) persistence for push-model session activity. Construct once per Treemon instance with
/// an instance-specific `dbPath` (created if its directory is missing). Thread-safe: every operation
/// runs on its own short-lived connection, so the single-writer mailbox and concurrent WAL readers
/// (restart rebuild, Overview history, prune timer) never share a connection. Dispose on shutdown.
type SessionActivityStore private (dbPath: string, observeBackgroundAgentRead: unit -> unit) =

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
        use cmd = c.CreateCommand()
        cmd.CommandText <- schemaSql
        cmd.ExecuteNonQuery() |> ignore
        ensureAdditiveColumns c
        cmd.CommandText <- migrateSql
        cmd.ExecuteNonQuery() |> ignore
        c

    let updateAfterStale
        (sessionId: SessionId)
        (closedAt: DateTimeOffset)
        (write: SqliteConnection -> SqliteTransaction -> unit)
        =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        closeActiveBackgroundAgents conn tx sessionId closedAt
        write conn tx
        let persisted = readStoredBySession observeBackgroundAgentRead conn tx sessionId
        tx.Commit()
        persisted

    new(dbPath: string) = new SessionActivityStore(dbPath, ignore)

    static member internal CreateWithBackgroundAgentReadObserver(
        dbPath: string,
        observeBackgroundAgentRead: unit -> unit
    ) =
        new SessionActivityStore(dbPath, observeBackgroundAgentRead)

    /// Insert-or-update a session's live row. Last-write-wins on `UpdatedAt`: a stale (older) report
    /// for an existing session is silently ignored (see upsertSql).
    member _.UpsertStatus(stored: StoredStatus) : unit =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- upsertSql
        bindUpsert cmd stored
        cmd.ExecuteNonQuery() |> ignore

    /// Close stale active lifecycle and upsert a state-only report in the same transaction.
    member _.UpsertStatusAfterStale(stored: StoredStatus, closedAt: DateTimeOffset) : StoredStatus =
        updateAfterStale stored.SessionId closedAt (fun conn tx ->
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- upsertSql
            bindUpsert cmd stored
            cmd.ExecuteNonQuery() |> ignore)

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
    /// Returns the authoritative persisted status when the event was newly inserted, or None when
    /// the event_id already existed (a full idempotent no-op — nothing appended or upserted).
    member _.AppendAndUpsert(
        row: ActivityEventRow,
        stored: StoredStatus,
        closeActiveAt: DateTimeOffset option
    ) : StoredStatus option =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        use appendCmd = conn.CreateCommand()
        appendCmd.Transaction <- tx
        appendCmd.CommandText <- appendSql
        bindAppend appendCmd row
        let inserted = appendCmd.ExecuteNonQuery() = 1

        let persisted =
            if inserted then
                closeActiveAt
                |> Option.iter (closeActiveBackgroundAgents conn tx stored.SessionId)
                use upsertCmd = conn.CreateCommand()
                upsertCmd.Transaction <- tx
                upsertCmd.CommandText <- upsertSql
                bindUpsert upsertCmd stored
                upsertCmd.ExecuteNonQuery() |> ignore
                Some(readStoredBySession observeBackgroundAgentRead conn tx stored.SessionId)
            else
                None

        tx.Commit()
        persisted

    /// Advance a session's `last_seen` (openness heartbeat) without touching status/updated_at or the
    /// message fields. Only moves it forward; a no-op if the row is absent or already fresher.
    member _.TouchLastSeen(sessionId: SessionId, lastSeen: DateTimeOffset) : unit =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- touchSql
        cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
        cmd.Parameters.AddWithValue("$seen", isoUtc lastSeen) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Close stale active lifecycle and advance liveness in the same transaction.
    member _.TouchLastSeenAfterStale(
        sessionId: SessionId,
        lastSeen: DateTimeOffset,
        closedAt: DateTimeOffset
    ) : StoredStatus =
        updateAfterStale sessionId closedAt (fun conn tx ->
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- touchSql
            cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
            cmd.Parameters.AddWithValue("$seen", isoUtc lastSeen) |> ignore
            cmd.ExecuteNonQuery() |> ignore)

    /// Merge one background agent's independent start/terminal clocks by event time. Each clock only
    /// moves forward, so duplicates and out-of-order delivery are idempotent. Returns active starts
    /// only; completed clocks remain durable tombstones until the replay floor passes them.
    member _.UpsertBackgroundAgentLifecycle(
        sessionId: SessionId,
        toolCallId: string,
        lifecycle: BackgroundAgentLifecycle
    ) : Map<string, DateTimeOffset> =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        let replayAfter = readBackgroundAgentReplayAfter conn (Some tx) sessionId
        lifecycleAfterReplayFloor replayAfter lifecycle
        |> Option.iter (upsertBackgroundAgent conn tx sessionId toolCallId)
        let persisted = readBackgroundAgents observeBackgroundAgentRead conn (Some tx) sessionId
        tx.Commit()
        persisted

    /// Atomically dedupe and append a background-agent event, merge that tool call's independent
    /// lifecycle clocks, upsert the session shell/aggregate, and return the authoritative persisted
    /// row. The event is inserted first inside the transaction, so a duplicate event_id skips both
    /// lifecycle and status changes. The supplied event-time history row remains unchanged; merged
    /// lifecycle clocks are used only for the authoritative current session aggregate.
    member _.AppendBackgroundAgentAndUpsert(
        row: ActivityEventRow,
        stored: StoredStatus,
        toolCallId: string,
        lifecycle: BackgroundAgentLifecycle,
        replayAfter: DateTimeOffset,
        closeActiveAt: DateTimeOffset option
    ) : StoredStatus option =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        use appendCmd = conn.CreateCommand()
        appendCmd.Transaction <- tx
        appendCmd.CommandText <- appendSql
        bindAppend appendCmd row
        let inserted = appendCmd.ExecuteNonQuery() = 1

        let persisted =
            if inserted then
                closeActiveAt
                |> Option.iter (closeActiveBackgroundAgents conn tx stored.SessionId)
                let effectiveReplayAfter =
                    readBackgroundAgentReplayAfter conn (Some tx) stored.SessionId
                    |> Option.fold max replayAfter

                lifecycleAfterReplayFloor (Some effectiveReplayAfter) lifecycle
                |> Option.iter (upsertBackgroundAgent conn tx stored.SessionId toolCallId)
                let authoritativeAgents =
                    readBackgroundAgents observeBackgroundAgentRead conn (Some tx) stored.SessionId
                let authoritativeInput =
                    { stored with
                        Status.BackgroundAgents = authoritativeAgents }

                use upsertCmd = conn.CreateCommand()
                upsertCmd.Transaction <- tx
                upsertCmd.CommandText <- upsertSql
                bindUpsert upsertCmd authoritativeInput
                upsertCmd.ExecuteNonQuery() |> ignore
                advanceSessionBackgroundAgentReplay conn tx stored.SessionId effectiveReplayAfter
                let authoritative =
                    readStoredBySession observeBackgroundAgentRead conn tx stored.SessionId
                Some authoritative
            else
                None

        tx.Commit()
        persisted

    /// Persist the latest accepted context-window gauge, inserting the full session snapshot when a
    /// retained in-memory session outlives its pruned row. Returns the authoritative persisted state,
    /// including a newer gauge that may already have won the independent usage clock.
    member _.UpsertContextUsage(stored: StoredStatus) : StoredStatus =
        use conn = openConn ()
        use tx = conn.BeginTransaction()
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- upsertContextUsageSql
        bindUpsert cmd stored
        cmd.ExecuteNonQuery() |> ignore
        let persisted = readStoredBySession observeBackgroundAgentRead conn tx stored.SessionId
        tx.Commit()
        persisted

    /// Close stale active lifecycle and persist the latest context gauge in the same transaction.
    member _.UpsertContextUsageAfterStale(
        stored: StoredStatus,
        closedAt: DateTimeOffset
    ) : StoredStatus =
        updateAfterStale stored.SessionId closedAt (fun conn tx ->
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- upsertContextUsageSql
            bindUpsert cmd stored
            cmd.ExecuteNonQuery() |> ignore)

    /// Read one durable session row regardless of the live idle-window cutoff.
    member _.StatusBySession(sessionId: SessionId) : StoredStatus option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- statusBySessionSql
        cmd.Parameters.AddWithValue("$sid", SessionId.value sessionId) |> ignore
        let stored =
            use reader = cmd.ExecuteReader()
            if reader.Read() then Some(readStored reader) else None

        stored |> Option.map (attachBackgroundAgents observeBackgroundAgentRead conn)

    /// Restart rebuild: every session whose `last_seen` is within the idle window (i.e. still live),
    /// so cards are correct before any new event arrives.
    member _.LoadLiveStatuses(now: DateTimeOffset) : StoredStatus list =
        let cutoff = now - idleWindow
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- loadSql
        cmd.Parameters.AddWithValue("$cutoff", isoUtc cutoff) |> ignore
        let rows =
            use reader = cmd.ExecuteReader()
            readRows reader readStored []

        let agentsBySession = readLiveBackgroundAgents observeBackgroundAgentRead conn cutoff
        rows |> List.map (attachBackgroundAgentsFrom agentsBySession)

    /// The most recently active stored session per worktree across ALL rows, IGNORING the idle
    /// window (unlike LoadLiveStatuses). This is the durable footer and resume-button-visibility
    /// substrate for cards whose sessions have aged out of the live map. Keyed by worktree_path.
    member _.RetainedByWorktree() : Map<string, StoredStatus> =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- retainedByWorktreeSql
        let rows =
            use reader = cmd.ExecuteReader()
            readRows reader readStored []

        rows
        |> List.map (fun session -> WorktreePath.value session.WorktreePath, session)
        |> Map.ofList

    /// Resume identity for a worktree, independent of the idle window and retained until pruning.
    /// This scalar path deliberately avoids loading status content or background-agent lifecycle.
    member _.LatestSessionIdForWorktree(worktreePath: WorktreePath) : string option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- latestSessionIdForWorktreeSql
        cmd.Parameters.AddWithValue("$wt", WorktreePath.value worktreePath) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(reader.GetString 0) else None

    /// Retention: atomically advance every session's lifecycle replay floor, remove completed
    /// tombstones at/before it, then drop old events, dead sessions, and their remaining lifecycle
    /// rows. Returns deleted rows only; replay-floor updates are not included.
    member _.PruneOld(cutoff: DateTimeOffset) : int =
        use conn = openConn ()
        use tx = conn.BeginTransaction()

        let execute sql =
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- sql
            cmd.Parameters.AddWithValue("$cutoff", isoUtc cutoff) |> ignore
            cmd.ExecuteNonQuery()

        execute advanceBackgroundAgentReplaySql |> ignore

        let completedAgentsDeleted = execute pruneCompletedBackgroundAgentsSql
        let oldSessionAgentsDeleted = execute pruneOldSessionBackgroundAgentsSql
        let oldEventsDeleted = execute pruneOldEventsSql
        let oldStatusesDeleted = execute pruneOldStatusesSql
        let deleted = completedAgentsDeleted + oldSessionAgentsDeleted + oldEventsDeleted + oldStatusesDeleted

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

    interface IDisposable with
        member _.Dispose() = keepAlive.Dispose()
