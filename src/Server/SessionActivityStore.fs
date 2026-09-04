module Server.SessionActivityStore

open System
open System.Buffers
open System.IO
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite
open Shared
open Server.SessionActivity
open Server.SessionActivityStoreSchema
open Server.SqliteStorage

// SQLite is the durable single-writer mirror behind exact process-instance activity. Legacy
// session_status rows are copied once into migration-only retained_sessions and the legacy table is
// then retired. Runtime writes target only session_instances and process-scoped activity_events.

// --- Row shapes -------------------------------------------------------------------------------

/// Temporary read model for application consumers that still collapse physical instances by durable
/// Copilot SessionId. It is produced only by ExactInstanceProjection and by retained-history reads;
/// no runtime writer persists this shape.
type StoredStatus =
    { SessionId: SessionId
      TerminalSessionId: TerminalSessionId option
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Status: SessionStatus
      UpdatedAt: DateTimeOffset
      LastSeen: DateTimeOffset
      ContextUsageAt: DateTimeOffset option }

module StoredStatus =
    let activityOrderKey (stored: StoredStatus) =
        stored.UpdatedAt, SessionId.value stored.SessionId

    /// LastSeen is liveness-only, so it must never decide which session owns shared content.
    let tryMostRecentActivity sessions =
        sessions
        |> List.sortByDescending activityOrderKey
        |> List.tryHead

/// One exact physical Copilot process. Lifecycle, content, usage, liveness, terminal origin, and
/// closure retain independent clocks/fields on this row.
type StoredInstance =
    { ProcessIdentity: ProcessIdentity
      SessionId: SessionId
      TerminalSessionId: TerminalSessionId option
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Status: SessionStatus
      /// Greatest accepted conversation-activity timestamp. Presence, heartbeat, usage, bootstrap,
      /// and closure do not move it.
      UpdatedAt: DateTimeOffset
      /// Last accepted base-lifecycle event timestamp. Intent/title, ask-user clocks, background
      /// clocks, usage, presence, heartbeat, and closure are ordered independently.
      LifecycleAt: DateTimeOffset option
      /// Server receipt time of the latest acknowledged presence or accepted heartbeat.
      LastSeen: DateTimeOffset
      ContextUsageAt: DateTimeOffset option
      ClosedAt: DateTimeOffset option }

module StoredInstance =
    let activityOrderKey (stored: StoredInstance) =
        stored.UpdatedAt,
        SessionId.value stored.SessionId,
        ProcessIdentity.sortKey stored.ProcessIdentity

    let toStoredStatus (stored: StoredInstance) =
        { SessionId = stored.SessionId
          TerminalSessionId = stored.TerminalSessionId
          WorktreePath = stored.WorktreePath
          Provider = stored.Provider
          Status = stored.Status
          UpdatedAt = stored.UpdatedAt
          LastSeen = stored.LastSeen
          ContextUsageAt = stored.ContextUsageAt }

/// One accepted history-bearing event. Event identity is scoped to the exact producer process.
type ActivityEventRow =
    { ProcessIdentity: ProcessIdentity
      EventId: EventId
      SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Kind: string
      Status: SessionLevelStatus
      Skill: string option
      Ts: DateTimeOffset }

/// The one temporary exact-to-session projection used by untouched application consumers. Closed
/// instances never enter it. Within one durable SessionId an open active instance wins, then the
/// greatest-activity open or otherwise recent nonclosed instance.
module ExactInstanceProjection =
    let private choose (now: DateTimeOffset) (instances: StoredInstance list) =
        let candidates = instances |> List.filter _.ClosedAt.IsNone

        let openInstances =
            candidates
            |> List.filter (fun instance -> now - instance.LastSeen < openWindow)

        openInstances
        |> pickActive _.Status StoredInstance.activityOrderKey
        |> Option.orElseWith (fun () ->
            openInstances
            |> List.sortByDescending StoredInstance.activityOrderKey
            |> List.tryHead)
        |> Option.orElseWith (fun () ->
            candidates
            |> List.sortByDescending StoredInstance.activityOrderKey
            |> List.tryHead)
        |> Option.map StoredInstance.toStoredStatus

    let bySession
        (now: DateTimeOffset)
        (instances: StoredInstance seq)
        =
        instances
        |> Seq.groupBy _.SessionId
        |> Seq.choose (fun (sessionId, grouped) ->
            grouped
            |> List.ofSeq
            |> choose now
            |> Option.map (fun projected -> sessionId, projected))
        |> Map.ofSeq

    let tryForSession
        (now: DateTimeOffset)
        (sessionId: SessionId)
        (instances: StoredInstance seq)
        =
        instances
        |> Seq.filter (fun instance -> instance.SessionId = sessionId)
        |> List.ofSeq
        |> choose now

// --- Serialization ----------------------------------------------------------------------------

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
    | other -> failwith $"SessionActivityStore: unknown status text '{other}'"

let private providerText =
    function
    | CopilotCli -> "copilot_cli"

let private parseProvider =
    function
    | "copilot_cli" -> CopilotCli
    | other -> failwith $"SessionActivityStore: unknown provider text '{other}'"

let private optToDb =
    Option.map box >> Option.defaultValue (box DBNull.Value)

let private timestampToDb =
    Option.map isoUtc >> optToDb

let private msgToDb =
    function
    | Some message -> box message.Text, box (isoUtc message.At)
    | None -> box DBNull.Value, box DBNull.Value

let private contextToDb (stored: StoredInstance) =
    match stored.Status.ContextUsage, stored.ContextUsageAt with
    | None, None -> box DBNull.Value, box DBNull.Value, box DBNull.Value
    | Some usage, Some usageAt ->
        box usage.CurrentTokens, box usage.TokenLimit, box (isoUtc usageAt)
    | _ ->
        invalidArg
            (nameof stored)
            "ContextUsage and ContextUsageAt must both be present or absent"

let private readOptStr (reader: SqliteDataReader) index =
    if reader.IsDBNull index then None else Some(reader.GetString index)

let private readOptTimestamp
    (reader: SqliteDataReader)
    index
    =
    readOptStr reader index |> Option.map parseIso

let private readOptMessage
    (reader: SqliteDataReader)
    textIndex
    timestampIndex
    =
    match readOptStr reader textIndex, readOptStr reader timestampIndex with
    | Some text, Some timestamp ->
        Some
            { Text = text
              At = parseIso timestamp }
    | _ -> None

let private readContextUsage
    (reader: SqliteDataReader)
    currentIndex
    limitIndex
    timestampIndex
    =
    match
        reader.IsDBNull currentIndex,
        reader.IsDBNull limitIndex,
        reader.IsDBNull timestampIndex
    with
    | true, true, true -> None, None
    | false, false, false ->
        Some
            { CurrentTokens = reader.GetInt32 currentIndex
              TokenLimit = reader.GetInt32 limitIndex },
        Some(parseIso (reader.GetString timestampIndex))
    | _ -> failwith $"{nameof StoredInstance}: incomplete persisted context usage"

let private writeTimestampProperty
    (writer: Utf8JsonWriter)
    (name: string)
    (timestamp: DateTimeOffset option)
    =
    match timestamp with
    | Some value -> writer.WriteString(name, isoUtc value)
    | None -> writer.WriteNull name

let private serializeBackgroundAgentClocks
    (clocks: Map<string, BackgroundAgentLifecycle>)
    =
    let buffer = ArrayBufferWriter<byte>()

    use writer =
        new Utf8JsonWriter(
            buffer,
            JsonWriterOptions(Indented = false, SkipValidation = false)
        )

    writer.WriteStartArray()

    clocks
    |> Map.toSeq
    |> Seq.iter (fun (toolCallId, lifecycle) ->
        writer.WriteStartObject()
        writer.WriteString("toolCallId", toolCallId)
        writeTimestampProperty writer "startedAt" lifecycle.StartedAt
        writeTimestampProperty writer "finishedAt" lifecycle.FinishedAt
        writer.WriteEndObject())

    writer.WriteEndArray()
    writer.Flush()
    Encoding.UTF8.GetString(buffer.WrittenSpan)

let private parseOptionalJsonTimestamp
    (element: JsonElement)
    (propertyName: string)
    =
    let property = element.GetProperty propertyName

    match property.ValueKind with
    | JsonValueKind.Null -> None
    | JsonValueKind.String ->
        property.GetString()
        |> Option.ofObj
        |> Option.map parseIso
    | _ ->
        failwith
            $"{nameof StoredInstance}: malformed background-agent timestamp"

let private parseBackgroundAgentClocks (json: string) =
    use document = JsonDocument.Parse json

    if document.RootElement.ValueKind <> JsonValueKind.Array then
        failwith $"{nameof StoredInstance}: malformed background-agent clocks"

    document.RootElement.EnumerateArray()
    |> Seq.map (fun element ->
        let toolCallId =
            element.GetProperty("toolCallId").GetString()
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () ->
                failwith
                    $"{nameof StoredInstance}: malformed background-agent tool-call identity")

        toolCallId,
        { StartedAt = parseOptionalJsonTimestamp element "startedAt"
          FinishedAt = parseOptionalJsonTimestamp element "finishedAt" })
    |> Map.ofSeq

let private readInstance (reader: SqliteDataReader) =
    let identity =
        ProcessIdentity.create (reader.GetInt32 0) (reader.GetInt64 1)
        |> Result.defaultWith invalidOp

    let contextUsage, contextUsageAt =
        readContextUsage reader 18 19 20

    { ProcessIdentity = identity
      SessionId = SessionId(reader.GetString 2)
      TerminalSessionId =
        readOptStr reader 23 |> Option.map TerminalSessionId
      WorktreePath = WorktreePath(reader.GetString 3)
      Provider = parseProvider (reader.GetString 4)
      Status =
        { Status = parseStatus (reader.GetString 5)
          Skill = readOptStr reader 6
          LastUserMessage = readOptMessage reader 7 8
          LastAssistantMessage = readOptMessage reader 9 10
          Intent = readOptMessage reader 11 12
          Title = readOptMessage reader 13 14
          ContextUsage = contextUsage
          AwaitingUserSince = readOptTimestamp reader 21
          UserInputCompletedAt = readOptTimestamp reader 22
          BackgroundAgentClocks =
            reader.GetString 24 |> parseBackgroundAgentClocks }
      UpdatedAt = parseIso (reader.GetString 15)
      LifecycleAt = readOptTimestamp reader 16
      LastSeen = parseIso (reader.GetString 17)
      ContextUsageAt = contextUsageAt
      ClosedAt = readOptTimestamp reader 25 }

let private readProjectedStatus (reader: SqliteDataReader) =
    let contextUsage, contextUsageAt =
        readContextUsage reader 15 16 17

    { SessionId = SessionId(reader.GetString 0)
      TerminalSessionId =
        readOptStr reader 20 |> Option.map TerminalSessionId
      WorktreePath = WorktreePath(reader.GetString 1)
      Provider = parseProvider (reader.GetString 2)
      Status =
        { Status = parseStatus (reader.GetString 3)
          Skill = readOptStr reader 4
          LastUserMessage = readOptMessage reader 5 6
          LastAssistantMessage = readOptMessage reader 7 8
          Intent = readOptMessage reader 9 10
          Title = readOptMessage reader 11 12
          ContextUsage = contextUsage
          AwaitingUserSince = readOptTimestamp reader 18
          UserInputCompletedAt = readOptTimestamp reader 19
          BackgroundAgentClocks = Map.empty }
      UpdatedAt = parseIso (reader.GetString 13)
      LastSeen = parseIso (reader.GetString 14)
      ContextUsageAt = contextUsageAt }

// --- SQL --------------------------------------------------------------------------------------

let private instanceColumns =
    """
process_id, process_start_ticks, session_id, worktree_path, provider, status,
current_skill, last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
intent_text, intent_ts, title_text, title_ts, updated_at, lifecycle_at, last_seen,
context_current_tokens, context_token_limit, context_usage_at,
awaiting_user_since, user_input_completed_at, terminal_session_id,
background_agent_clocks, closed_at
"""

let private upsertInstanceSql =
    """
INSERT INTO session_instances
    (process_id, process_start_ticks, session_id, worktree_path, provider, status,
     current_skill, last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
     intent_text, intent_ts, title_text, title_ts, updated_at, lifecycle_at, last_seen,
     context_current_tokens, context_token_limit, context_usage_at,
     awaiting_user_since, user_input_completed_at, terminal_session_id,
     background_agent_clocks, closed_at)
VALUES
    ($processId, $processStartTicks, $sessionId, $worktreePath, $provider, $status,
     $skill, $userMessage, $userMessageAt, $assistantMessage, $assistantMessageAt,
     $intent, $intentAt, $title, $titleAt, $updatedAt, $lifecycleAt, $lastSeen,
     $contextCurrent, $contextLimit, $contextAt,
     $awaitingUserSince, $userInputCompletedAt, $terminalSessionId,
     $backgroundAgentClocks, $closedAt)
ON CONFLICT(process_id, process_start_ticks) DO UPDATE SET
    session_id = excluded.session_id,
    worktree_path = excluded.worktree_path,
    provider = excluded.provider,
    status = excluded.status,
    current_skill = excluded.current_skill,
    last_user_msg = excluded.last_user_msg,
    last_user_ts = excluded.last_user_ts,
    last_asst_msg = excluded.last_asst_msg,
    last_asst_ts = excluded.last_asst_ts,
    intent_text = excluded.intent_text,
    intent_ts = excluded.intent_ts,
    title_text = excluded.title_text,
    title_ts = excluded.title_ts,
    updated_at = excluded.updated_at,
    lifecycle_at = excluded.lifecycle_at,
    last_seen = excluded.last_seen,
    context_current_tokens = excluded.context_current_tokens,
    context_token_limit = excluded.context_token_limit,
    context_usage_at = excluded.context_usage_at,
    awaiting_user_since = excluded.awaiting_user_since,
    user_input_completed_at = excluded.user_input_completed_at,
    terminal_session_id = excluded.terminal_session_id,
    background_agent_clocks = excluded.background_agent_clocks,
    closed_at = COALESCE(session_instances.closed_at, excluded.closed_at);
"""

let private appendSql =
    """
INSERT OR IGNORE INTO activity_events
    (process_id, process_start_ticks, event_id, session_id, worktree_path,
     provider, kind, status, skill, ts)
VALUES
    ($processId, $processStartTicks, $eventId, $sessionId, $worktreePath,
     $provider, $kind, $status, $skill, $timestamp);
"""

let private heartbeatSql =
    """
UPDATE session_instances
SET last_seen =
        CASE WHEN last_seen < $lastSeen THEN $lastSeen ELSE last_seen END,
    terminal_session_id = COALESCE($terminalSessionId, terminal_session_id)
WHERE process_id = $processId
  AND process_start_ticks = $processStartTicks
  AND closed_at IS NULL;
"""

let private closeSql =
    """
UPDATE session_instances
SET closed_at = COALESCE(closed_at, $closedAt),
    terminal_session_id = COALESCE($terminalSessionId, terminal_session_id)
WHERE process_id = $processId
  AND process_start_ticks = $processStartTicks;
"""

let private instanceByIdentitySql =
    $"""
SELECT {instanceColumns}
FROM session_instances
WHERE process_id = $processId
  AND process_start_ticks = $processStartTicks
LIMIT 1;
"""

let private instancesBySessionSql =
    $"""
SELECT {instanceColumns}
FROM session_instances
WHERE session_id = $sessionId
ORDER BY updated_at DESC, process_id DESC, process_start_ticks DESC;
"""

let private loadRecentInstancesSql =
    $"""
SELECT {instanceColumns}
FROM session_instances
WHERE last_seen >= $cutoff
ORDER BY last_seen, process_id, process_start_ticks;
"""

let private retainedByWorktreeSql =
    $"""
WITH candidates AS (
    SELECT
        session_id, worktree_path, provider, status, current_skill,
        last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
        intent_text, intent_ts, title_text, title_ts, updated_at,
        context_current_tokens, context_token_limit, context_usage_at,
        awaiting_user_since, user_input_completed_at,
        process_id, process_start_ticks
    FROM session_instances

    UNION ALL

    SELECT
        session_id, worktree_path, provider, status, current_skill,
        last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
        intent_text, intent_ts, title_text, title_ts, updated_at,
        context_current_tokens, context_token_limit, context_usage_at,
        awaiting_user_since, user_input_completed_at,
        0 AS process_id, 0 AS process_start_ticks
    FROM retained_sessions
),
ranked AS (
    SELECT *,
           ROW_NUMBER() OVER (
               PARTITION BY worktree_path
               ORDER BY updated_at DESC, session_id DESC,
                        process_id DESC, process_start_ticks DESC
           ) AS activity_rank
    FROM candidates
)
SELECT
    session_id, worktree_path, provider, status, current_skill,
    last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
    intent_text, intent_ts, title_text, title_ts, updated_at,
    $notLive AS last_seen,
    context_current_tokens, context_token_limit, context_usage_at,
    awaiting_user_since, user_input_completed_at,
    NULL AS terminal_session_id
FROM ranked
WHERE activity_rank = 1;
"""

let private latestSessionIdForWorktreeSql =
    """
SELECT session_id
FROM (
    SELECT session_id, updated_at, process_id, process_start_ticks
    FROM session_instances
    WHERE worktree_path = $worktreePath

    UNION ALL

    SELECT session_id, updated_at, 0 AS process_id, 0 AS process_start_ticks
    FROM retained_sessions
    WHERE worktree_path = $worktreePath
)
ORDER BY updated_at DESC, session_id DESC, process_id DESC, process_start_ticks DESC
LIMIT 1;
"""

let private retainedTerminalSessionIdsSql =
    """
SELECT DISTINCT terminal_session_id
FROM session_instances
WHERE terminal_session_id IS NOT NULL;
"""

let private pruneSql =
    """
WITH retained_event_baselines AS (
    SELECT event.rowid
    FROM activity_events AS event
    JOIN session_instances AS instance
      ON instance.process_id = event.process_id
     AND instance.process_start_ticks = event.process_start_ticks
    WHERE event.ts < $cutoff
      AND (
          instance.updated_at >= $cutoff
          OR instance.last_seen >= $cutoff
          OR instance.closed_at >= $cutoff
      )
      AND event.rowid = (
          SELECT baseline.rowid
          FROM activity_events AS baseline
          WHERE baseline.process_id = event.process_id
            AND baseline.process_start_ticks = event.process_start_ticks
            AND baseline.ts < $cutoff
          ORDER BY baseline.ts DESC, baseline.rowid DESC
          LIMIT 1
      )
)
DELETE FROM activity_events
WHERE ts < $cutoff
  AND rowid NOT IN (SELECT rowid FROM retained_event_baselines);

DELETE FROM session_instances
WHERE updated_at < $cutoff
  AND last_seen < $cutoff
  AND (closed_at IS NULL OR closed_at < $cutoff);

DELETE FROM retained_sessions
WHERE updated_at < $cutoff;
"""

// --- Bind/read helpers ------------------------------------------------------------------------

let private bindIdentity (command: SqliteCommand) identity =
    command.Parameters.AddWithValue(
        "$processId",
        ProcessIdentity.processId identity
    )
    |> ignore

    command.Parameters.AddWithValue(
        "$processStartTicks",
        ProcessIdentity.processStartTimeUtcTicks identity
    )
    |> ignore

let private bindInstance (command: SqliteCommand) (stored: StoredInstance) =
    let status = stored.Status
    let userMessage, userMessageAt = msgToDb status.LastUserMessage
    let assistantMessage, assistantMessageAt =
        msgToDb status.LastAssistantMessage
    let intent, intentAt = msgToDb status.Intent
    let title, titleAt = msgToDb status.Title
    let contextCurrent, contextLimit, contextAt = contextToDb stored

    bindIdentity command stored.ProcessIdentity
    command.Parameters.AddWithValue("$sessionId", SessionId.value stored.SessionId) |> ignore
    command.Parameters.AddWithValue("$worktreePath", WorktreePath.value stored.WorktreePath) |> ignore
    command.Parameters.AddWithValue("$provider", providerText stored.Provider) |> ignore
    command.Parameters.AddWithValue("$status", statusText status.Status) |> ignore
    command.Parameters.AddWithValue("$skill", optToDb status.Skill) |> ignore
    command.Parameters.AddWithValue("$userMessage", userMessage) |> ignore
    command.Parameters.AddWithValue("$userMessageAt", userMessageAt) |> ignore
    command.Parameters.AddWithValue("$assistantMessage", assistantMessage) |> ignore
    command.Parameters.AddWithValue("$assistantMessageAt", assistantMessageAt) |> ignore
    command.Parameters.AddWithValue("$intent", intent) |> ignore
    command.Parameters.AddWithValue("$intentAt", intentAt) |> ignore
    command.Parameters.AddWithValue("$title", title) |> ignore
    command.Parameters.AddWithValue("$titleAt", titleAt) |> ignore
    command.Parameters.AddWithValue("$updatedAt", isoUtc stored.UpdatedAt) |> ignore
    command.Parameters.AddWithValue("$lifecycleAt", timestampToDb stored.LifecycleAt) |> ignore
    command.Parameters.AddWithValue("$lastSeen", isoUtc stored.LastSeen) |> ignore
    command.Parameters.AddWithValue("$contextCurrent", contextCurrent) |> ignore
    command.Parameters.AddWithValue("$contextLimit", contextLimit) |> ignore
    command.Parameters.AddWithValue("$contextAt", contextAt) |> ignore
    command.Parameters.AddWithValue("$awaitingUserSince", timestampToDb status.AwaitingUserSince) |> ignore
    command.Parameters.AddWithValue("$userInputCompletedAt", timestampToDb status.UserInputCompletedAt) |> ignore
    command.Parameters.AddWithValue(
        "$terminalSessionId",
        stored.TerminalSessionId
        |> Option.map TerminalSessionId.value
        |> optToDb
    )
    |> ignore
    command.Parameters.AddWithValue(
        "$backgroundAgentClocks",
        serializeBackgroundAgentClocks status.BackgroundAgentClocks
    )
    |> ignore
    command.Parameters.AddWithValue("$closedAt", timestampToDb stored.ClosedAt) |> ignore

let private bindEvent (command: SqliteCommand) (row: ActivityEventRow) =
    bindIdentity command row.ProcessIdentity
    command.Parameters.AddWithValue("$eventId", EventId.value row.EventId) |> ignore
    command.Parameters.AddWithValue("$sessionId", SessionId.value row.SessionId) |> ignore
    command.Parameters.AddWithValue("$worktreePath", WorktreePath.value row.WorktreePath) |> ignore
    command.Parameters.AddWithValue("$provider", providerText row.Provider) |> ignore
    command.Parameters.AddWithValue("$kind", row.Kind) |> ignore
    command.Parameters.AddWithValue("$status", statusText row.Status) |> ignore
    command.Parameters.AddWithValue("$skill", optToDb row.Skill) |> ignore
    command.Parameters.AddWithValue("$timestamp", isoUtc row.Ts) |> ignore

let private readInstanceByIdentity
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    identity
    =
    use command = connection.CreateCommand()
    transaction |> Option.iter (fun value -> command.Transaction <- value)
    command.CommandText <- instanceByIdentitySql
    bindIdentity command identity
    use reader = command.ExecuteReader()
    if reader.Read() then Some(readInstance reader) else None

let private upsertInstance
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    stored
    =
    use command = connection.CreateCommand()
    transaction |> Option.iter (fun value -> command.Transaction <- value)
    command.CommandText <- upsertInstanceSql
    bindInstance command stored
    command.ExecuteNonQuery() |> ignore

// --- Store ------------------------------------------------------------------------------------

type SessionActivityStore
    (
        dbPath: string,
        ?connectionOpened: SqliteConnection -> unit
    ) =

    do
        let directory = Path.GetDirectoryName dbPath

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

    let connectionOpened = defaultArg connectionOpened ignore

    let connectionString =
        SqliteConnectionStringBuilder(
            DataSource = dbPath,
            Pooling = false
        ).ConnectionString

    let openConnection () =
        let connection = new SqliteConnection(connectionString)
        connection.Open()

        use command = connection.CreateCommand()
        command.CommandText <-
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;"
        command.ExecuteNonQuery() |> ignore

        try
            connectionOpened connection
            connection
        with _ ->
            connection.Dispose()
            reraise ()

    let keepAlive =
        let connection = openConnection ()

        try
            initializeSchema connection
            connection
        with _ ->
            connection.Dispose()
            reraise ()

    /// Insert or replace one exact process-instance snapshot. Closure is monotonic at the SQL
    /// boundary even if a caller accidentally supplies ClosedAt=None after the row was closed.
    member _.UpsertInstance(stored: StoredInstance) =
        use connection = openConnection ()
        upsertInstance connection None stored

        readInstanceByIdentity connection None stored.ProcessIdentity
        |> Option.defaultWith (fun () ->
            failwith $"{nameof StoredInstance}: persisted instance row missing")

    /// Atomically append one process-scoped event and persist its folded exact-instance state.
    /// A duplicate event ID for the same process is a complete no-op.
    member _.AppendAndUpsert(row: ActivityEventRow, stored: StoredInstance) =
        use connection = openConnection ()
        use transaction = connection.BeginTransaction()

        let inserted =
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            command.CommandText <- appendSql
            bindEvent command row
            command.ExecuteNonQuery() = 1

        let persisted =
            if inserted then
                upsertInstance connection (Some transaction) stored

                readInstanceByIdentity
                    connection
                    (Some transaction)
                    stored.ProcessIdentity
            else
                None

        transaction.Commit()
        persisted

    /// Refresh liveness only for an already-known, still-open exact identity.
    member _.RecordHeartbeat
        (
            identity: ProcessIdentity,
            lastSeen: DateTimeOffset,
            terminalSessionId: TerminalSessionId option
        ) =
        use connection = openConnection ()
        use transaction = connection.BeginTransaction()
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- heartbeatSql
        bindIdentity command identity
        command.Parameters.AddWithValue("$lastSeen", isoUtc lastSeen) |> ignore
        command.Parameters.AddWithValue(
            "$terminalSessionId",
            terminalSessionId
            |> Option.map TerminalSessionId.value
            |> optToDb
        )
        |> ignore

        let updated = command.ExecuteNonQuery() = 1

        let persisted =
            if updated then
                readInstanceByIdentity connection (Some transaction) identity
            else
                None

        transaction.Commit()
        persisted

    /// Monotonically close one known exact identity. Repeated closes retain the first closure time.
    member _.CloseInstance
        (
            identity: ProcessIdentity,
            closedAt: DateTimeOffset,
            terminalSessionId: TerminalSessionId option
        ) =
        use connection = openConnection ()
        use transaction = connection.BeginTransaction()
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- closeSql
        bindIdentity command identity
        command.Parameters.AddWithValue("$closedAt", isoUtc closedAt) |> ignore
        command.Parameters.AddWithValue(
            "$terminalSessionId",
            terminalSessionId
            |> Option.map TerminalSessionId.value
            |> optToDb
        )
        |> ignore

        let updated = command.ExecuteNonQuery() = 1

        let persisted =
            if updated then
                readInstanceByIdentity connection (Some transaction) identity
            else
                None

        transaction.Commit()
        persisted

    member _.InstanceByIdentity(identity: ProcessIdentity) =
        use connection = openConnection ()
        readInstanceByIdentity connection None identity

    member _.InstancesBySession(sessionId: SessionId) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- instancesBySessionSql
        command.Parameters.AddWithValue("$sessionId", SessionId.value sessionId) |> ignore
        use reader = command.ExecuteReader()
        readRows reader readInstance []

    /// Restart rebuild: exact rows whose receipt-time liveness is still within the in-memory window.
    member _.LoadRecentInstances(now: DateTimeOffset) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- loadRecentInstancesSql
        command.Parameters.AddWithValue("$cutoff", isoUtc (now - idleWindow)) |> ignore
        use reader = command.ExecuteReader()
        readRows reader readInstance []

    /// One durable footer representative per worktree across exact and migration-only history. The
    /// result is deliberately non-live; the scheduler's exact projection supplies openness.
    member _.RetainedByWorktree() =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- retainedByWorktreeSql
        command.Parameters.AddWithValue("$notLive", isoUtc DateTimeOffset.MinValue) |> ignore
        use reader = command.ExecuteReader()

        readRows reader readProjectedStatus []
        |> List.map (fun status ->
            WorktreePath.value status.WorktreePath, status)
        |> Map.ofList

    member _.LatestSessionIdForWorktree(worktreePath: WorktreePath) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- latestSessionIdForWorktreeSql
        command.Parameters.AddWithValue(
            "$worktreePath",
            WorktreePath.value worktreePath
        )
        |> ignore
        use reader = command.ExecuteReader()
        if reader.Read() then Some(reader.GetString 0) else None

    member internal _.RetainedTerminalSessionIds() =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- retainedTerminalSessionIdsSql
        use reader = command.ExecuteReader()

        readRows reader (fun row -> TerminalSessionId(row.GetString 0)) []
        |> Set.ofList

    member _.PruneOld(cutoff: DateTimeOffset) =
        use connection = openConnection ()
        use transaction = connection.BeginTransaction()
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- pruneSql
        command.Parameters.AddWithValue("$cutoff", isoUtc cutoff) |> ignore
        let deleted = command.ExecuteNonQuery()
        transaction.Commit()
        deleted

    interface IDisposable with
        member _.Dispose() = keepAlive.Dispose()
