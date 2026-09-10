module Server.SessionActivityStore

open System
open System.Buffers
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open FsToolkit.ErrorHandling
open Microsoft.Data.Sqlite
open Shared
open Server.SessionActivity
open Server.SessionActivityStoreSchema
open Server.SqliteStorage

// SQLite is the durable single-writer mirror behind exact process-instance activity. Pre-upgrade
// session rows contribute only their durable identity to resume_sessions so explicit Resume keeps
// working; runtime writes target session_instances and dedupe-only activity_events.

// --- Row shapes -------------------------------------------------------------------------------

/// The greatest-activity exact instance of one worktree, projected for card/footer history and for
/// automatic fallback identity when no physical process is open. It deliberately carries no
/// liveness, terminal origin, or process identity, so it cannot be mistaken for a live address.
type RetainedSession =
    { SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      Status: SessionStatus
      UpdatedAt: DateTimeOffset
      ContextUsageAt: DateTimeOffset option }

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
        stored.SessionId,
        ProcessIdentity.sortKey stored.ProcessIdentity

    let isOpenAt (now: DateTimeOffset) (stored: StoredInstance) =
        stored.ClosedAt.IsNone
        && now - stored.LastSeen < openWindow

    /// LastSeen is liveness-only, so it must never decide which instance owns shared content.
    let tryMostRecentActivity instances =
        instances
        |> List.sortByDescending activityOrderKey
        |> List.tryHead

/// One accepted history-bearing event, reduced to its deduplication key. Event identity is scoped
/// to the exact producer process; folded state lives on `session_instances`.
type ActivityEventRow =
    { ProcessIdentity: ProcessIdentity
      EventId: EventId
      Ts: DateTimeOffset }

[<RequireQualifiedAccess>]
type private PersistedDataError =
    | InvalidProcessIdentity of reason: string
    | InvalidSessionId of reason: string
    | InvalidTerminalSessionId of reason: string
    | UnknownStatus of value: string
    | UnknownProvider of value: string
    | InvalidTimestamp of field: string
    | IncompleteMessage of field: string
    | IncompleteContextUsage
    | MalformedBackgroundAgentClocks of reason: string
    | MissingPersistedInstance

let private persistedDataErrorMessage =
    function
    | PersistedDataError.InvalidProcessIdentity reason ->
        $"invalid persisted process identity: {reason}"
    | PersistedDataError.InvalidSessionId reason ->
        $"invalid persisted session id: {reason}"
    | PersistedDataError.InvalidTerminalSessionId reason ->
        $"invalid persisted terminal session id: {reason}"
    | PersistedDataError.UnknownStatus value ->
        $"{nameof SessionLevelStatus}: unknown status text '{value}'"
    | PersistedDataError.UnknownProvider value ->
        $"{nameof CodingToolProvider}: unknown provider text '{value}'"
    | PersistedDataError.InvalidTimestamp field ->
        $"invalid persisted {field} timestamp"
    | PersistedDataError.IncompleteMessage field ->
        $"incomplete persisted {field}"
    | PersistedDataError.IncompleteContextUsage ->
        $"incomplete persisted {nameof ContextUsage}"
    | PersistedDataError.MalformedBackgroundAgentClocks reason ->
        $"malformed background-agent clocks: {reason}"
    | PersistedDataError.MissingPersistedInstance ->
        $"{nameof StoredInstance}: persisted instance row missing"

let private raisePersistedDataError error =
    error
    |> persistedDataErrorMessage
    |> fun message ->
        raise (InvalidDataException($"SessionActivityStore: {message}"))

let private persistedValue result =
    result
    |> Result.defaultWith raisePersistedDataError

// --- Serialization ----------------------------------------------------------------------------

let private statusText =
    function
    | SessionLevelStatus.Working -> "working"
    | SessionLevelStatus.WaitingForUser -> "waiting_for_user"
    | SessionLevelStatus.Idle -> "idle"

let private parseStatus =
    function
    | "working" -> Ok SessionLevelStatus.Working
    | "waiting_for_user" -> Ok SessionLevelStatus.WaitingForUser
    | "idle" -> Ok SessionLevelStatus.Idle
    | other -> Error(PersistedDataError.UnknownStatus other)

let private providerText =
    function
    | CopilotCli -> "copilot_cli"

let private parseProvider =
    function
    | "copilot_cli" -> Ok CopilotCli
    | other -> Error(PersistedDataError.UnknownProvider other)

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

let private persistedSessionId value =
    SessionId.create value
    |> Result.mapError PersistedDataError.InvalidSessionId

let private persistedTerminalSessionId value =
    TerminalSessionId.create value
    |> Result.mapError PersistedDataError.InvalidTerminalSessionId

let private parseTimestamp (field: string) (value: string) =
    match
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        )
    with
    | true, timestamp -> Ok timestamp
    | false, _ -> Error(PersistedDataError.InvalidTimestamp field)

let private readOptTimestamp
    field
    (reader: SqliteDataReader)
    index
    =
    match readOptStr reader index with
    | None -> Ok None
    | Some value ->
        parseTimestamp field value
        |> Result.map Some

let private readOptMessage
    field
    (reader: SqliteDataReader)
    textIndex
    timestampIndex
    =
    match readOptStr reader textIndex, readOptStr reader timestampIndex with
    | None, None -> Ok None
    | Some text, Some timestamp ->
        parseTimestamp $"{field}_at" timestamp
        |> Result.map (fun at ->
            Some
                { Text = text
                  At = at })
    | _ -> Error(PersistedDataError.IncompleteMessage field)

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
    | true, true, true -> Ok(None, None)
    | false, false, false ->
        parseTimestamp "context_usage_at" (reader.GetString timestampIndex)
        |> Result.map (fun timestamp ->
            Some
                { CurrentTokens = reader.GetInt32 currentIndex
                  TokenLimit = reader.GetInt32 limitIndex },
            Some timestamp)
    | _ -> Error PersistedDataError.IncompleteContextUsage

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
    match element.TryGetProperty propertyName with
    | false, _ ->
        Error(
            PersistedDataError.MalformedBackgroundAgentClocks(
                $"missing {propertyName}"
            )
        )
    | true, property ->
        match property.ValueKind with
        | JsonValueKind.Null -> Ok None
        | JsonValueKind.String ->
            match property.GetString() |> Option.ofObj with
            | None ->
                Error(
                    PersistedDataError.MalformedBackgroundAgentClocks(
                        $"null {propertyName}"
                    )
                )
            | Some value ->
                parseTimestamp $"background_agent_clocks.{propertyName}" value
                |> Result.mapError (fun _ ->
                    PersistedDataError.MalformedBackgroundAgentClocks(
                        $"invalid {propertyName}"
                    ))
                |> Result.map Some
        | _ ->
            Error(
                PersistedDataError.MalformedBackgroundAgentClocks(
                    $"invalid {propertyName}"
                )
            )

let private parseBackgroundAgentClock (element: JsonElement) =
    result {
        if element.ValueKind <> JsonValueKind.Object then
            return!
                Error(
                    PersistedDataError.MalformedBackgroundAgentClocks(
                        "entry is not an object"
                    )
                )

        let! toolCallId =
            match element.TryGetProperty "toolCallId" with
            | true, property when property.ValueKind = JsonValueKind.String ->
                property.GetString()
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Result.requireSome (
                    PersistedDataError.MalformedBackgroundAgentClocks(
                        "missing toolCallId"
                    )
                )
            | _ ->
                Error(
                    PersistedDataError.MalformedBackgroundAgentClocks(
                        "missing toolCallId"
                    )
                )

        let! startedAt =
            parseOptionalJsonTimestamp element "startedAt"

        let! finishedAt =
            parseOptionalJsonTimestamp element "finishedAt"

        return
            toolCallId,
            { StartedAt = startedAt
              FinishedAt = finishedAt }
    }

let private parseBackgroundAgentClocks (json: string) =
    try
        use document = JsonDocument.Parse json

        if document.RootElement.ValueKind <> JsonValueKind.Array then
            Error(
                PersistedDataError.MalformedBackgroundAgentClocks(
                    "root is not an array"
                )
            )
        else
            document.RootElement.EnumerateArray()
            |> Seq.toList
            |> List.traverseResultM parseBackgroundAgentClock
            |> Result.map Map.ofList
    with :? JsonException ->
        Error(
            PersistedDataError.MalformedBackgroundAgentClocks(
                "invalid JSON"
            )
        )

let private readInstance (reader: SqliteDataReader) =
    result {
        let! identity =
            ProcessIdentity.create
                (reader.GetInt32 0)
                (reader.GetInt64 1)
            |> Result.mapError PersistedDataError.InvalidProcessIdentity

        let! sessionId =
            reader.GetString 2
            |> persistedSessionId

        let! terminalSessionId =
            match readOptStr reader 23 with
            | None -> Ok None
            | Some value ->
                persistedTerminalSessionId value
                |> Result.map Some

        let! provider = parseProvider (reader.GetString 4)
        let! status = parseStatus (reader.GetString 5)
        let! lastUserMessage = readOptMessage "last_user_message" reader 7 8
        let! lastAssistantMessage =
            readOptMessage "last_assistant_message" reader 9 10
        let! intent = readOptMessage "intent" reader 11 12
        let! title = readOptMessage "title" reader 13 14
        let! contextUsage, contextUsageAt =
            readContextUsage reader 18 19 20
        let! awaitingUserSince =
            readOptTimestamp "awaiting_user_since" reader 21
        let! userInputCompletedAt =
            readOptTimestamp "user_input_completed_at" reader 22
        let! backgroundAgentClocks =
            reader.GetString 24
            |> parseBackgroundAgentClocks
        let! updatedAt = parseTimestamp "updated_at" (reader.GetString 15)
        let! lifecycleAt = readOptTimestamp "lifecycle_at" reader 16
        let! lastSeen = parseTimestamp "last_seen" (reader.GetString 17)
        let! closedAt = readOptTimestamp "closed_at" reader 25

        return
            { ProcessIdentity = identity
              SessionId = sessionId
              TerminalSessionId = terminalSessionId
              WorktreePath = WorktreePath(reader.GetString 3)
              Provider = provider
              Status =
                { Status = status
                  Skill = readOptStr reader 6
                  LastUserMessage = lastUserMessage
                  LastAssistantMessage = lastAssistantMessage
                  Intent = intent
                  Title = title
                  ContextUsage = contextUsage
                  AwaitingUserSince = awaitingUserSince
                  UserInputCompletedAt = userInputCompletedAt
                  BackgroundAgentClocks = backgroundAgentClocks }
              UpdatedAt = updatedAt
              LifecycleAt = lifecycleAt
              LastSeen = lastSeen
              ContextUsageAt = contextUsageAt
              ClosedAt = closedAt }
    }

let private readRetainedSession (reader: SqliteDataReader) =
    result {
        let! sessionId =
            reader.GetString 0
            |> persistedSessionId

        let! provider = parseProvider (reader.GetString 2)
        let! status = parseStatus (reader.GetString 3)
        let! lastUserMessage = readOptMessage "last_user_message" reader 5 6
        let! lastAssistantMessage =
            readOptMessage "last_assistant_message" reader 7 8
        let! intent = readOptMessage "intent" reader 9 10
        let! title = readOptMessage "title" reader 11 12
        let! contextUsage, contextUsageAt =
            readContextUsage reader 14 15 16
        let! awaitingUserSince =
            readOptTimestamp "awaiting_user_since" reader 17
        let! userInputCompletedAt =
            readOptTimestamp "user_input_completed_at" reader 18
        let! updatedAt = parseTimestamp "updated_at" (reader.GetString 13)

        return
            { SessionId = sessionId
              WorktreePath = WorktreePath(reader.GetString 1)
              Provider = provider
              Status =
                { Status = status
                  Skill = readOptStr reader 4
                  LastUserMessage = lastUserMessage
                  LastAssistantMessage = lastAssistantMessage
                  Intent = intent
                  Title = title
                  ContextUsage = contextUsage
                  AwaitingUserSince = awaitingUserSince
                  UserInputCompletedAt = userInputCompletedAt
                  BackgroundAgentClocks = Map.empty }
              UpdatedAt = updatedAt
              ContextUsageAt = contextUsageAt }
    }

let rec private readPersistedRows
    (reader: SqliteDataReader)
    read
    accumulated
    =
    if reader.Read() then
        match read reader with
        | Ok row ->
            readPersistedRows
                reader
                read
                (row :: accumulated)
        | Error error -> Error error
    else
        Ok(List.rev accumulated)

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

let private retainedColumns =
    """
session_id, worktree_path, provider, status, current_skill,
last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
intent_text, intent_ts, title_text, title_ts, updated_at,
context_current_tokens, context_token_limit, context_usage_at,
awaiting_user_since, user_input_completed_at
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
    (process_id, process_start_ticks, event_id, ts)
VALUES
    ($processId, $processStartTicks, $eventId, $timestamp);
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
SELECT {retainedColumns}
FROM (
    SELECT {retainedColumns},
           ROW_NUMBER() OVER (
               PARTITION BY worktree_path
               ORDER BY updated_at DESC, session_id DESC,
                        process_id DESC, process_start_ticks DESC
           ) AS activity_rank
    FROM session_instances
)
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
    FROM resume_sessions
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
DELETE FROM activity_events
WHERE ts < $cutoff;

DELETE FROM session_instances
WHERE updated_at < $cutoff
  AND last_seen < $cutoff
  AND (closed_at IS NULL OR closed_at < $cutoff);

DELETE FROM resume_sessions
WHERE updated_at < $cutoff;
"""

// --- Bind/read helpers ------------------------------------------------------------------------

// Microsoft.Data.Sqlite binds values through the mutable Parameters collection; this binder confines that interop mutation.
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

// Microsoft.Data.Sqlite exposes only mutable parameter binding; this helper confines it to a fresh, single-use command.
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

// Microsoft.Data.Sqlite requires imperative parameter population on the caller's locally scoped, disposable command.
let private bindEvent (command: SqliteCommand) (row: ActivityEventRow) =
    bindIdentity command row.ProcessIdentity
    command.Parameters.AddWithValue("$eventId", EventId.value row.EventId) |> ignore
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
    if reader.Read() then
        readInstance reader
        |> Result.map Some
    else
        Ok None

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
        use transaction = connection.BeginTransaction()
        upsertInstance connection (Some transaction) stored

        let persisted =
            readInstanceByIdentity
                connection
                (Some transaction)
                stored.ProcessIdentity
            |> persistedValue
            |> Option.defaultWith (fun () ->
                raisePersistedDataError
                    PersistedDataError.MissingPersistedInstance)

        transaction.Commit()
        persisted

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
                |> persistedValue
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
                |> persistedValue
            else
                None

        transaction.Commit()
        persisted

    member _.InstanceByIdentity(identity: ProcessIdentity) =
        use connection = openConnection ()
        readInstanceByIdentity connection None identity
        |> persistedValue

    member _.InstancesBySession(sessionId: SessionId) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- instancesBySessionSql
        command.Parameters.AddWithValue("$sessionId", SessionId.value sessionId) |> ignore
        use reader = command.ExecuteReader()
        readPersistedRows reader readInstance []
        |> persistedValue

    /// Restart rebuild: exact rows whose receipt-time liveness is still within the in-memory window.
    member _.LoadRecentInstances(now: DateTimeOffset) =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- loadRecentInstancesSql
        command.Parameters.AddWithValue("$cutoff", isoUtc (now - idleWindow)) |> ignore
        use reader = command.ExecuteReader()
        readPersistedRows reader readInstance []
        |> persistedValue

    /// One durable footer representative per worktree, ranked directly from exact instances. The
    /// read returns at most one row per worktree instead of the 60-day process-instance history.
    member _.RetainedByWorktree() =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- retainedByWorktreeSql
        use reader = command.ExecuteReader()

        readPersistedRows reader readRetainedSession []
        |> persistedValue
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
        if reader.Read() then
            reader.GetString 0
            |> persistedSessionId
            |> Result.map Some
            |> persistedValue
        else
            None

    member internal _.RetainedTerminalSessionIds() =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- retainedTerminalSessionIdsSql
        use reader = command.ExecuteReader()

        readPersistedRows
            reader
            (fun row ->
                row.GetString 0
                |> persistedTerminalSessionId)
            []
        |> persistedValue
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
