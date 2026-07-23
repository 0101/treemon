module Server.OverviewSnapshotStore

open System
open System.IO
open Microsoft.Data.Sqlite
open Shared
open Server.SqliteStorage

[<Literal>]
let private resolutionSeconds = 30L

[<Literal>]
let private retentionSeconds = 72L * 60L * 60L

[<Literal>]
let private maxRows = 289

let private countConverter: Newtonsoft.Json.JsonConverter =
    Fable.Remoting.Json.FableJsonConverter()

let private serialize value =
    Newtonsoft.Json.JsonConvert.SerializeObject(
        value,
        Newtonsoft.Json.Formatting.None,
        [| countConverter |]
    )

let private deserialize<'T> value =
    Newtonsoft.Json.JsonConvert.DeserializeObject<'T>(
        value,
        [| countConverter |]
    )

let private bucketOf parameterName (timestamp: DateTimeOffset) =
    let bucket = timestamp.ToUnixTimeSeconds()

    if bucket % resolutionSeconds <> 0L
       || timestamp.ToUniversalTime() <> DateTimeOffset.FromUnixTimeSeconds bucket then
        invalidArg parameterName "Overview snapshot timestamps must be exact 30-second boundaries."

    bucket

let private timestampOf bucket =
    DateTimeOffset.FromUnixTimeSeconds bucket

let private stride =
    function
    | OverviewData.HistoryWindow.Hours12 -> 5L
    | OverviewData.HistoryWindow.Hours24 -> 10L
    | OverviewData.HistoryWindow.Hours72 -> 30L

let private migrationSql =
    """
DROP TABLE IF EXISTS overview_history_staging;
DROP TABLE IF EXISTS overview_history_rows;
DROP TABLE IF EXISTS overview_history_session_bounds;
DROP TABLE IF EXISTS overview_history_state;
DROP TABLE IF EXISTS session_liveness;
DROP TABLE IF EXISTS task_snapshots;

CREATE TABLE IF NOT EXISTS overview_snapshots (
    bucket INTEGER PRIMARY KEY CHECK (bucket % 30 = 0),
    tasks  TEXT NOT NULL,
    agents TEXT NOT NULL
);
"""

type OverviewSnapshotStore(dbPath: string) =

    do
        let directory = Path.GetDirectoryName dbPath

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

    let connectionString =
        SqliteConnectionStringBuilder(DataSource = dbPath, Pooling = false).ConnectionString

    let openConnection () =
        let connection = new SqliteConnection(connectionString)
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;"
        command.ExecuteNonQuery() |> ignore
        connection

    do
        use connection = openConnection ()
        use transaction = connection.BeginTransaction()
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- migrationSql
        command.ExecuteNonQuery() |> ignore
        transaction.Commit()

    /// Insert one canonical snapshot and prune only rows strictly older than its 72-hour cutoff.
    /// A duplicate bucket is a no-op and never overwrites the first committed observation.
    member _.Insert(snapshot: OverviewData.OverviewSnapshot) : bool =
        let bucket = bucketOf (nameof snapshot.Timestamp) snapshot.Timestamp
        let tasks = serialize snapshot.Tasks
        let agents = serialize snapshot.Agents

        use connection = openConnection ()
        use transaction = connection.BeginTransaction(deferred = false)
        use insert = connection.CreateCommand()
        insert.Transaction <- transaction
        insert.CommandText <-
            """
INSERT OR IGNORE INTO overview_snapshots(bucket, tasks, agents)
VALUES ($bucket, $tasks, $agents);
"""
        insert.Parameters.AddWithValue("$bucket", bucket) |> ignore
        insert.Parameters.AddWithValue("$tasks", tasks) |> ignore
        insert.Parameters.AddWithValue("$agents", agents) |> ignore
        let inserted = insert.ExecuteNonQuery() = 1

        if inserted then
            use prune = connection.CreateCommand()
            prune.Transaction <- transaction
            prune.CommandText <- "DELETE FROM overview_snapshots WHERE bucket < $cutoff;"
            prune.Parameters.AddWithValue("$cutoff", bucket - retentionSeconds) |> ignore
            prune.ExecuteNonQuery() |> ignore

        transaction.Commit()
        inserted

    member _.LatestAnchor() : DateTimeOffset option =
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT max(bucket) FROM overview_snapshots;"
        let value = command.ExecuteScalar()

        if isNull value || Convert.IsDBNull value then
            None
        else
            Some(timestampOf (Convert.ToInt64 value))

    /// Read one inclusive, anchor-aligned history window. Gaps stay absent and rows are oldest first.
    member _.ReadWindow
        (
            anchor: DateTimeOffset,
            window: OverviewData.HistoryWindow
        ) : OverviewData.OverviewSnapshot list =
        let anchorBucket = bucketOf (nameof anchor) anchor
        let startBucket =
            anchorBucket - int64 (OverviewData.HistoryWindow.duration window).TotalSeconds
        let stepSeconds = resolutionSeconds * stride window

        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <-
            """
SELECT bucket, tasks, agents
FROM overview_snapshots
WHERE bucket >= $start
  AND bucket <= $anchor
  AND (($anchor - bucket) % $step) = 0
ORDER BY bucket
LIMIT $limit;
"""
        command.Parameters.AddWithValue("$start", startBucket) |> ignore
        command.Parameters.AddWithValue("$anchor", anchorBucket) |> ignore
        command.Parameters.AddWithValue("$step", stepSeconds) |> ignore
        command.Parameters.AddWithValue("$limit", maxRows) |> ignore
        use reader = command.ExecuteReader()

        readRows
            reader
            (fun row ->
                { OverviewData.OverviewSnapshot.Timestamp = timestampOf (row.GetInt64 0)
                  Tasks = deserialize<OverviewData.TaskCount list> (row.GetString 1)
                  Agents = deserialize<OverviewData.AgentCount list> (row.GetString 2) })
            []
