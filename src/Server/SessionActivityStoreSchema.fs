module Server.SessionActivityStoreSchema

open System
open Microsoft.Data.Sqlite

let private activityEventsTableSql createClause tableName =
    $"""
{createClause} {tableName} (
    process_id          INTEGER NOT NULL CHECK (process_id > 0),
    process_start_ticks INTEGER NOT NULL CHECK (process_start_ticks > 0),
    event_id            TEXT NOT NULL,
    ts                  TEXT NOT NULL,
    PRIMARY KEY (process_id, process_start_ticks, event_id)
);
"""

let private minimalEventColumns =
    Set.ofList [ "process_id"; "process_start_ticks"; "event_id"; "ts" ]

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

CREATE TABLE IF NOT EXISTS resume_sessions (
    session_id                 TEXT PRIMARY KEY,
    worktree_path              TEXT NOT NULL,
    updated_at                 TEXT NOT NULL
);

{activityEventsTableSql "CREATE TABLE IF NOT EXISTS" "activity_events"}
"""

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

CREATE INDEX IF NOT EXISTS ix_resume_worktree_activity
ON resume_sessions(worktree_path, updated_at DESC, session_id DESC);

CREATE INDEX IF NOT EXISTS ix_events_ts ON activity_events(ts);
"""

/// Copies the durable identity of a pre-upgrade session row into `resume_sessions`, keeping the
/// greatest `updated_at` per session so repeated startup and several sources converge.
let private retainResumeIdentitySql sourceTable =
    $"""
INSERT INTO resume_sessions (session_id, worktree_path, updated_at)
SELECT session_id, worktree_path, MAX(updated_at)
FROM {sourceTable}
GROUP BY session_id
ON CONFLICT(session_id) DO UPDATE SET
    worktree_path = excluded.worktree_path,
    updated_at = excluded.updated_at
WHERE excluded.updated_at >= resume_sessions.updated_at;
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

let private columnNames
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    (tableName: string)
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <- $"PRAGMA table_info({tableName});"
    use reader = command.ExecuteReader()
    readColumnNames reader Set.empty

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

let private executeMigrationSql
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    sql
    =
    use command = connection.CreateCommand()
    command.Transaction <- transaction
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

/// Branch databases created before per-instance lifecycle ordering lack `lifecycle_at`; adding it
/// keeps those exact rows readable instead of failing startup.
let private ensureLifecycleColumn
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    if
        columnNames connection transaction "session_instances"
        |> Set.contains "lifecycle_at"
        |> not
    then
        executeMigrationSql
            connection
            transaction
            "ALTER TABLE session_instances ADD COLUMN lifecycle_at TEXT;"

/// Rebuilds `activity_events` down to its dedupe key. Rows already keyed by exact process identity
/// keep their key and timestamp; rows keyed by event ID alone cannot prove which process produced
/// them, so they are discarded rather than folded under a foreign identity.
let private rebuildActivityEventsIfNeeded
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    =
    let migrationTable = "activity_events_migration"

    executeMigrationSql
        connection
        transaction
        $"DROP TABLE IF EXISTS {migrationTable};"

    if columnNames connection transaction "activity_events" <> minimalEventColumns then
        let copyKeys =
            if activityEventsUsesProcessKey connection transaction then
                $"""
INSERT INTO {migrationTable} (process_id, process_start_ticks, event_id, ts)
SELECT process_id, process_start_ticks, event_id, ts FROM activity_events;
"""
            else
                ""

        executeMigrationSql
            connection
            transaction
            $"""
{activityEventsTableSql "CREATE TABLE" migrationTable}
{copyKeys}
DROP TABLE activity_events;
ALTER TABLE {migrationTable} RENAME TO activity_events;
"""

let private retainResumeIdentity
    (connection: SqliteConnection)
    (transaction: SqliteTransaction)
    sourceTable
    =
    if tableExists connection transaction sourceTable then
        executeMigrationSql
            connection
            transaction
            (retainResumeIdentitySql sourceTable)

let internal initializeSchema (connection: SqliteConnection) =
    use transaction = connection.BeginTransaction()
    executeMigrationSql connection transaction schemaSql
    ensureLifecycleColumn connection transaction
    retainResumeIdentity connection transaction "session_status"
    retainResumeIdentity connection transaction "retained_sessions"
    rebuildActivityEventsIfNeeded connection transaction
    executeMigrationSql
        connection
        transaction
        """
DROP TABLE IF EXISTS session_status;
DROP TABLE IF EXISTS retained_sessions;
DROP TABLE IF EXISTS worktree_representatives;
"""
    executeMigrationSql connection transaction indexSql
    transaction.Commit()
