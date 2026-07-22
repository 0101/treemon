module Server.OverviewHistoryReconstruction

open System
open Microsoft.Data.Sqlite
open Server.OverviewHistoryRollup
open Server.SessionActivity
open Server.SessionActivityStore
open Shared

[<Literal>]
let maxBatchBoundaryCount = 512

let private relevantSessionsCte =
    """
WITH relevant_sessions AS MATERIALIZED (
    SELECT session_id
    FROM overview_history_session_bounds INDEXED BY ix_overview_bounds_last_first
    WHERE last_observed_at >= $lookback AND first_observed_at <= $end
)
"""

let internal taskPredecessorSql =
    """
SELECT ts, tasks
FROM task_snapshots INDEXED BY ix_task_snapshots_ts
WHERE ts < $start
ORDER BY ts DESC, id DESC
LIMIT 1;
"""

let internal taskChangesSql =
    """
SELECT ts, tasks
FROM task_snapshots INDEXED BY ix_task_snapshots_ts
WHERE ts >= $start AND ts <= $end
ORDER BY ts, id;
"""

let internal eventRowsSql =
    relevantSessionsCte
    + """
, baseline_rows AS (
    SELECT (
        SELECT baseline.rowid
        FROM activity_events AS baseline INDEXED BY ix_events_session_ts
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
SELECT event.event_id, event.session_id, event.worktree_path, event.provider, event.kind,
       event.status, event.skill, event.ts, event.rowid
FROM relevant_sessions AS relevant
JOIN activity_events AS event INDEXED BY ix_events_session_ts
  ON event.session_id = relevant.session_id
WHERE event.ts >= $start AND event.ts <= $end
ORDER BY ts, rowid;
"""

let internal livenessRowsSql =
    relevantSessionsCte
    + """
SELECT live.session_id, live.ts
FROM relevant_sessions AS relevant
CROSS JOIN session_liveness AS live
WHERE live.session_id = relevant.session_id
  AND live.ts >= $lookback AND live.ts <= $end
ORDER BY live.ts, live.session_id;
"""

let private requestedBoundaries startBoundary endBoundary =
    if not (isBoundary startBoundary) then
        invalidArg (nameof startBoundary) "The reconstruction start must be a canonical UTC boundary."

    if not (isBoundary endBoundary) then
        invalidArg (nameof endBoundary) "The reconstruction end must be a canonical UTC boundary."

    if endBoundary < startBoundary then
        invalidArg (nameof endBoundary) "The reconstruction end must not precede its start."

    let requested =
        boundaries startBoundary endBoundary
        |> Seq.truncate (maxBatchBoundaryCount + 1)
        |> Seq.toList

    if requested.Length > maxBatchBoundaryCount then
        invalidArg (nameof endBoundary) $"A reconstruction batch may contain at most {maxBatchBoundaryCount} boundaries."

    requested

let private queryRows
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    sql
    bind
    map
    =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- sql
    bind cmd
    use reader = cmd.ExecuteReader()
    readRows reader map []

let private queryRow conn tx sql bind map =
    queryRows conn tx sql bind map |> List.tryHead

let internal reconstructRangeWith
    beforeLivenessRead
    (store: SessionActivityStore)
    startBoundary
    endBoundary
    : RollupRow list =
    let requested = requestedBoundaries startBoundary endBoundary
    let lookback = startBoundary - openWindow

    store.ReadSnapshot(fun conn tx ->
        let bindStart (cmd: SqliteCommand) =
            cmd.Parameters.AddWithValue("$start", isoUtc startBoundary) |> ignore

        let bindRange (cmd: SqliteCommand) =
            bindStart cmd
            cmd.Parameters.AddWithValue("$end", isoUtc endBoundary) |> ignore

        let bindRelevantRange (cmd: SqliteCommand) =
            bindRange cmd
            cmd.Parameters.AddWithValue("$lookback", isoUtc lookback) |> ignore

        let tasks =
            (queryRow
                conn
                tx
                taskPredecessorSql
                bindStart
                (fun row -> parseIso (row.GetString 0), parseTasks (row.GetString 1))
             |> Option.toList)
            @ queryRows
                conn
                tx
                taskChangesSql
                bindRange
                (fun row -> parseIso (row.GetString 0), parseTasks (row.GetString 1))

        let events =
            queryRows conn tx eventRowsSql bindRelevantRange readEventRow

        beforeLivenessRead ()

        let liveness =
            queryRows
                conn
                tx
                livenessRowsSql
                bindRelevantRange
                (fun row -> SessionId(row.GetString 0), parseIso (row.GetString 1))

        OverviewHistory.reconstructAt requested tasks events liveness
        |> List.map (fun snapshot ->
            { Boundary = snapshot.Timestamp
              Tasks = snapshot.Tasks
              Agents = snapshot.Agents }))

let reconstructRange store startBoundary endBoundary =
    reconstructRangeWith ignore store startBoundary endBoundary
