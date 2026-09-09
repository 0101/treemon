/// Shared legacy-schema builders and SQLite metadata helpers for the construction-time migration
/// tests in `SessionActivityMigrationTests` and `SessionActivityStoreTests`. Each builder recreates
/// a distinct historical schema snapshot; keep them separate rather than merging them.
module Tests.SessionActivityMigrationFixture

open Microsoft.Data.Sqlite
open Tests.SqliteTestDatabase

let private readStrings (reader: SqliteDataReader) =
    let rec read acc =
        if reader.Read() then read (reader.GetString 0 :: acc) else List.rev acc

    read []

let private columnsOf path tableName index =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{tableName}');"
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then read (reader.GetString index :: acc) else List.rev acc

    read []

let tableColumns path tableName = columnsOf path tableName 1

let primaryKeyColumns path tableName =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{tableName}');"
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then
            let primaryKeyOrder = reader.GetInt32 5

            let next =
                if primaryKeyOrder = 0 then
                    acc
                else
                    (primaryKeyOrder, reader.GetString 1) :: acc

            read next
        else
            acc |> List.sortBy fst |> List.map snd

    read []

let indexColumns path indexName =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA index_info('{indexName}');"
    use reader = command.ExecuteReader()

    let rec read acc =
        if reader.Read() then read (reader.GetString 2 :: acc) else List.rev acc

    read []

let schemaNames path objectType =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
SELECT name
FROM sqlite_master
WHERE type = $type
ORDER BY name;
"""
    command.Parameters.AddWithValue("$type", objectType) |> ignore
    use reader = command.ExecuteReader()
    readStrings reader

/// The retained pre-upgrade resume identity of one session: worktree and activity clock only.
let resumeIdentity path sessionId =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
SELECT worktree_path, updated_at
FROM resume_sessions
WHERE session_id = $sessionId;
"""
    command.Parameters.AddWithValue("$sessionId", (sessionId: string)) |> ignore
    use reader = command.ExecuteReader()

    if reader.Read() then
        Some(reader.GetString 0, reader.GetString 1)
    else
        None

/// Main's durable schema: one `session_status` row per durable session plus `activity_events`
/// keyed by `event_id` alone. Two sessions share a worktree so Resume selection has to rank them.
let createLegacyDatabase path =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
CREATE TABLE session_status (
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
    terminal_session_id     TEXT
);

CREATE TABLE activity_events (
    event_id      TEXT PRIMARY KEY,
    session_id    TEXT NOT NULL,
    worktree_path TEXT NOT NULL,
    provider      TEXT NOT NULL,
    kind          TEXT NOT NULL,
    status        TEXT NOT NULL,
    skill         TEXT,
    ts            TEXT NOT NULL
);

INSERT INTO session_status
    (session_id, worktree_path, provider, status, current_skill,
     last_user_msg, title_text, updated_at, last_seen)
VALUES
    ('legacy-session', 'C:/wt/legacy', 'copilot_cli', 'waiting_for_user', 'review',
     'please review', 'Review storage',
     '2026-09-01T10:04:00.0000000+00:00', '2026-09-04T10:04:00.0000000+00:00'),
    ('older-legacy-session', 'C:/wt/legacy', 'copilot_cli', 'idle', NULL,
     NULL, NULL,
     '2026-08-30T09:00:00.0000000+00:00', '2026-08-30T09:00:00.0000000+00:00');

INSERT INTO activity_events
    (event_id, session_id, worktree_path, provider, kind, status, skill, ts)
VALUES
    ('legacy-event', 'legacy-session', 'C:/wt/legacy', 'copilot_cli',
     'awaiting_user_input', 'waiting_for_user', 'review',
     '2026-09-01T10:04:00.0000000+00:00');
"""
    command.ExecuteNonQuery() |> ignore

/// This branch's prior durable schema: exact instances without the per-instance lifecycle clock,
/// full-payload process-keyed events, the materialized representative table, and the wide
/// `retained_sessions` copy of pre-exact history.
let createPriorBranchDatabase path =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
CREATE TABLE session_instances (
    process_id                 INTEGER NOT NULL,
    process_start_ticks        INTEGER NOT NULL,
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

CREATE TABLE retained_sessions (
    session_id                 TEXT PRIMARY KEY,
    worktree_path              TEXT NOT NULL,
    provider                   TEXT NOT NULL,
    status                     TEXT NOT NULL,
    current_skill              TEXT,
    title_text                 TEXT,
    title_ts                   TEXT,
    updated_at                 TEXT NOT NULL
);

CREATE TABLE worktree_representatives (
    worktree_path              TEXT PRIMARY KEY,
    session_id                 TEXT NOT NULL,
    provider                   TEXT NOT NULL,
    status                     TEXT NOT NULL,
    updated_at                 TEXT NOT NULL,
    process_id                 INTEGER NOT NULL,
    process_start_ticks        INTEGER NOT NULL
);

CREATE TABLE activity_events (
    process_id          INTEGER NOT NULL,
    process_start_ticks INTEGER NOT NULL,
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

INSERT INTO session_instances
    (process_id, process_start_ticks, session_id, worktree_path, provider, status,
     title_text, title_ts, updated_at, last_seen)
VALUES
    (9100, 9200, 'prior-exact-session', 'C:/wt/prior', 'copilot_cli', 'idle',
     'Prior title', '2026-09-04T09:59:00.0000000+00:00',
     '2026-09-04T10:00:00.0000000+00:00', '2026-09-04T10:00:00.0000000+00:00');

INSERT INTO retained_sessions
    (session_id, worktree_path, provider, status, current_skill, title_text, title_ts, updated_at)
VALUES
    ('prior-retained-session', 'C:/wt/prior-retained', 'copilot_cli', 'idle', 'review',
     'Retained title', '2026-09-01T10:03:00.0000000+00:00',
     '2026-09-01T10:04:00.0000000+00:00');

INSERT INTO worktree_representatives
    (worktree_path, session_id, provider, status, updated_at, process_id, process_start_ticks)
VALUES
    ('C:/wt/prior', 'prior-exact-session', 'copilot_cli', 'idle',
     '2026-09-04T10:00:00.0000000+00:00', 9100, 9200);

INSERT INTO activity_events
    (process_id, process_start_ticks, event_id, session_id, worktree_path,
     provider, kind, status, skill, ts)
VALUES
    (9100, 9200, 'prior-event', 'prior-exact-session', 'C:/wt/prior',
     'copilot_cli', 'turn_ended', 'idle', NULL,
     '2026-09-04T09:58:00.0000000+00:00');
"""
    command.ExecuteNonQuery() |> ignore

/// A `session_instances` row keyed by exact process identity, the shape used to prove that
/// reused PIDs are keyed by their distinct start ticks.
let insertExactInstance path processId processStartTicks sessionId =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
INSERT INTO session_instances
    (process_id, process_start_ticks, session_id, worktree_path, provider, status,
     updated_at, last_seen)
VALUES
    ($processId, $processStartTicks, $sessionId, 'C:/wt/exact', 'copilot_cli', 'idle',
     '2026-09-04T10:00:00.0000000+00:00', '2026-09-04T10:00:00.0000000+00:00');
"""
    command.Parameters.AddWithValue("$processId", (processId: int)) |> ignore
    command.Parameters.AddWithValue("$processStartTicks", (processStartTicks: int64)) |> ignore
    command.Parameters.AddWithValue("$sessionId", (sessionId: string)) |> ignore
    command.ExecuteNonQuery() |> ignore

/// An `activity_events` row keyed by exact process identity, the shape used to prove that event
/// idempotency is scoped to one exact process identity rather than the durable session id.
let insertExactEvent path processId processStartTicks eventId =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <-
        """
INSERT INTO activity_events (process_id, process_start_ticks, event_id, ts)
VALUES ($processId, $processStartTicks, $eventId, '2026-09-04T10:00:00.0000000+00:00');
"""
    command.Parameters.AddWithValue("$processId", (processId: int)) |> ignore
    command.Parameters.AddWithValue("$processStartTicks", (processStartTicks: int64)) |> ignore
    command.Parameters.AddWithValue("$eventId", (eventId: string)) |> ignore
    command.ExecuteNonQuery() |> ignore
