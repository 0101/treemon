module Server.SessionActivityStoreSchema

open System
open Microsoft.Data.Sqlite

let private activityEventsTableSql createClause tableName =
    $"""
{createClause} {tableName} (
    process_id          INTEGER NOT NULL CHECK (process_id > 0),
    process_start_ticks INTEGER NOT NULL CHECK (process_start_ticks > 0),
    event_id            TEXT NOT NULL,
    session_id          TEXT NOT NULL,
    worktree_path       TEXT NOT NULL,
    provider            TEXT NOT NULL,
    kind                TEXT NOT NULL,
    status              TEXT NOT NULL,
    skill               TEXT,
    ts                  TEXT NOT NULL,
    PRIMARY KEY (process_id, process_start_ticks, event_id)
);
"""

let private schemaSql =
    $"""
CREATE TABLE IF NOT EXISTS session_instances (
    process_id                 INTEGER NOT NULL CHECK (process_id > 0),
    process_start_ticks        INTEGER NOT NULL CHECK (process_start_ticks > 0),
    session_id                 TEXT NOT NULL,
    worktree_path              TEXT NOT NULL,
    provider                   TEXT NOT NULL,
    status                     TEXT NOT NULL,
    current_skill              TEXT,
    last_user_msg              TEXT,
    last_user_ts               TEXT,
    last_asst_msg              TEXT,
    last_asst_ts               TEXT,
    intent_text                TEXT,
    intent_ts                  TEXT,
    title_text                 TEXT,
    title_ts                   TEXT,
    updated_at                 TEXT NOT NULL,
    lifecycle_at               TEXT,
    last_seen                  TEXT NOT NULL,
    context_current_tokens     INTEGER,
    context_token_limit        INTEGER,
    context_usage_at           TEXT,
    awaiting_user_since        TEXT,
    user_input_completed_at    TEXT,
    terminal_session_id        TEXT,
    background_agent_clocks    TEXT NOT NULL DEFAULT '[]',
    closed_at                  TEXT,
    PRIMARY KEY (process_id, process_start_ticks)
);

CREATE TABLE IF NOT EXISTS retained_sessions (
    session_id                 TEXT PRIMARY KEY,
    worktree_path              TEXT NOT NULL,
    provider                   TEXT NOT NULL,
    status                     TEXT NOT NULL,
    current_skill              TEXT,
    last_user_msg              TEXT,
    last_user_ts               TEXT,
    last_asst_msg              TEXT,
    last_asst_ts               TEXT,
    intent_text                TEXT,
    intent_ts                  TEXT,
    title_text                 TEXT,
    title_ts                   TEXT,
    updated_at                 TEXT NOT NULL,
    context_current_tokens     INTEGER,
    context_token_limit        INTEGER,
    context_usage_at           TEXT,
    awaiting_user_since        TEXT,
    user_input_completed_at    TEXT
);

CREATE TABLE IF NOT EXISTS worktree_representatives (
    worktree_path              TEXT PRIMARY KEY,
    session_id                 TEXT NOT NULL,
    provider                   TEXT NOT NULL,
    status                     TEXT NOT NULL,
    current_skill              TEXT,
    last_user_msg              TEXT,
    last_user_ts               TEXT,
    last_asst_msg              TEXT,
    last_asst_ts               TEXT,
    intent_text                TEXT,
    intent_ts                  TEXT,
    title_text                 TEXT,
    title_ts                   TEXT,
    updated_at                 TEXT NOT NULL,
    context_current_tokens     INTEGER,
    context_token_limit        INTEGER,
    context_usage_at           TEXT,
    awaiting_user_since        TEXT,
    user_input_completed_at    TEXT,
    process_id                 INTEGER NOT NULL,
    process_start_ticks        INTEGER NOT NULL
);

{activityEventsTableSql "CREATE TABLE IF NOT EXISTS" "activity_events"}
"""

let private legacyAdditiveColumns =
    [ "intent_text", "TEXT"
      "intent_ts", "TEXT"
      "title_text", "TEXT"
      "title_ts", "TEXT"
      "context_current_tokens", "INTEGER"
      "context_token_limit", "INTEGER"
      "context_usage_at", "TEXT"
      "awaiting_user_since", "TEXT"
      "user_input_completed_at", "TEXT"
      "terminal_session_id", "TEXT" ]

let private exactAdditiveColumns =
    [ "lifecycle_at", "TEXT" ]

let private indexSql =
    """
CREATE INDEX IF NOT EXISTS ix_instances_worktree_activity
ON session_instances(worktree_path, updated_at DESC, session_id DESC);
CREATE INDEX IF NOT EXISTS ix_instances_session_activity
ON session_instances(session_id, updated_at DESC, process_id, process_start_ticks);
CREATE INDEX IF NOT EXISTS ix_instances_terminal_activity
ON session_instances(terminal_session_id, updated_at DESC, session_id DESC);
CREATE INDEX IF NOT EXISTS ix_instances_last_seen
ON session_instances(last_seen);

CREATE INDEX IF NOT EXISTS ix_retained_worktree_activity
ON retained_sessions(worktree_path, updated_at DESC, session_id DESC);

CREATE INDEX IF NOT EXISTS ix_events_ts ON activity_events(ts);
CREATE INDEX IF NOT EXISTS ix_events_session_ts ON activity_events(session_id, ts);
CREATE INDEX IF NOT EXISTS ix_events_instance_ts
ON activity_events(process_id, process_start_ticks, ts);
"""

let private tableExists
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    tableName
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <-
        "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name;"
    command.Parameters.AddWithValue("$name", tableName) |> ignore
    Convert.ToInt32(command.ExecuteScalar()) = 1

let rec private readColumnNames (reader: SqliteDataReader) names =
    if reader.Read() then
        readColumnNames reader (Set.add (reader.GetString 1) names)
    else
        names

let private ensureColumns
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    (tableName: string)
    (columns: (string * string) list)
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <- $"PRAGMA table_info({tableName});"
    use reader = command.ExecuteReader()
    let existing = readColumnNames reader Set.empty

    columns
    |> List.filter (fst >> existing.Contains >> not)
    |> List.map (fun (name, declaration) ->
        $"ALTER TABLE {tableName} ADD COLUMN {name} {declaration};")
    |> String.concat Environment.NewLine
    |> fun migration ->
        if migration <> "" then
            use alter = connection.CreateCommand()
            alter.Transaction <- transaction
            alter.CommandText <- migration
            alter.ExecuteNonQuery() |> ignore

let rec private readPrimaryKeyColumns (reader: SqliteDataReader) columns =
    if reader.Read() then
        let order = reader.GetInt32 5
        let next =
            if order = 0 then columns
            else (order, reader.GetString 1) :: columns

        readPrimaryKeyColumns reader next
    else
        columns |> List.sortBy fst |> List.map snd

let private activityEventsUsesProcessKey
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <- "PRAGMA table_info(activity_events);"
    use reader = command.ExecuteReader()

    readPrimaryKeyColumns reader []
    = [ "process_id"; "process_start_ticks"; "event_id" ]

let private activityEventsRequiresExactRebuild
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    use definition = connection.CreateCommand()
    definition.Transaction <- transaction
    definition.CommandText <-
        "SELECT sql FROM sqlite_master
         WHERE type = 'table' AND name = 'activity_events';"

    let tableSql =
        definition.ExecuteScalar()
        |> string

    use invalidRows = connection.CreateCommand()
    invalidRows.Transaction <- transaction
    invalidRows.CommandText <-
        "SELECT count(*) FROM activity_events
         WHERE process_id <= 0 OR process_start_ticks <= 0;"

    let hasStrictIdentityChecks =
        tableSql.Contains(
            "CHECK (process_id > 0)",
            StringComparison.OrdinalIgnoreCase
        )
        && tableSql.Contains(
            "CHECK (process_start_ticks > 0)",
            StringComparison.OrdinalIgnoreCase
        )

    not hasStrictIdentityChecks
    || tableSql.Contains("DEFAULT 0", StringComparison.OrdinalIgnoreCase)
    || tableSql.Contains(
        "process_id = 0",
        StringComparison.OrdinalIgnoreCase
    )
    || Convert.ToInt32(invalidRows.ExecuteScalar()) > 0

let private executeMigrationSql
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    sql
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

let private rebuildActivityEventsIfNeeded
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    let migrationTable = "activity_events_migration"

    if
        activityEventsUsesProcessKey connection transaction
        && not (activityEventsRequiresExactRebuild connection transaction)
    then
        executeMigrationSql
            connection
            transaction
            $"DROP TABLE IF EXISTS {migrationTable};"
    else
        let preserveExactRows =
            if activityEventsUsesProcessKey connection transaction then
                """
INSERT INTO activity_events_migration
    (process_id, process_start_ticks, event_id, session_id, worktree_path,
     provider, kind, status, skill, ts)
SELECT
    process_id, process_start_ticks, event_id, session_id, worktree_path,
    provider, kind, status, skill, ts
FROM activity_events
WHERE process_id > 0 AND process_start_ticks > 0;
"""
            else
                ""

        executeMigrationSql
            connection
            transaction
            $"""
DROP TABLE IF EXISTS {migrationTable};
{activityEventsTableSql "CREATE TABLE" migrationTable}
{preserveExactRows}
DROP TABLE activity_events;
ALTER TABLE {migrationTable} RENAME TO activity_events;
"""

let private normalizeCurrentTables
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    executeMigrationSql
        connection
        transaction
        """
UPDATE session_instances SET status = 'idle' WHERE status = 'done';
UPDATE retained_sessions SET status = 'idle' WHERE status = 'done';
UPDATE activity_events SET status = 'idle' WHERE status = 'done';
"""

let private migrateLegacySessionStatus
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    if tableExists connection transaction "session_status" then
        ensureColumns
            connection
            transaction
            "session_status"
            legacyAdditiveColumns

        executeMigrationSql
            connection
            transaction
            """
UPDATE session_status SET status = 'idle' WHERE status = 'done';
UPDATE session_status
SET status = 'idle', awaiting_user_since = updated_at
WHERE status = 'waiting_for_user' AND awaiting_user_since IS NULL;

INSERT INTO retained_sessions
    (session_id, worktree_path, provider, status, current_skill,
     last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
     intent_text, intent_ts, title_text, title_ts, updated_at,
     context_current_tokens, context_token_limit, context_usage_at,
     awaiting_user_since, user_input_completed_at)
SELECT
    session_id, worktree_path, provider, status, current_skill,
    last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
    intent_text, intent_ts, title_text, title_ts, updated_at,
    context_current_tokens, context_token_limit, context_usage_at,
    awaiting_user_since, user_input_completed_at
FROM session_status
WHERE true
ON CONFLICT(session_id) DO UPDATE SET
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
    context_current_tokens = excluded.context_current_tokens,
    context_token_limit = excluded.context_token_limit,
    context_usage_at = excluded.context_usage_at,
    awaiting_user_since = excluded.awaiting_user_since,
    user_input_completed_at = excluded.user_input_completed_at
WHERE excluded.updated_at >= retained_sessions.updated_at;
"""

let internal initializeSchema (connection: SqliteConnection) =
    use transaction = connection.BeginTransaction()
    executeMigrationSql connection transaction schemaSql
    ensureColumns
        connection
        transaction
        "session_instances"
        exactAdditiveColumns
    normalizeCurrentTables connection transaction
    migrateLegacySessionStatus connection transaction
    rebuildActivityEventsIfNeeded connection transaction
    executeMigrationSql connection transaction "DROP TABLE IF EXISTS session_status;"
    executeMigrationSql connection transaction indexSql
    transaction.Commit()
