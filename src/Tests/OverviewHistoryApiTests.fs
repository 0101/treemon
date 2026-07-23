module Tests.OverviewHistoryApiTests

open System
open Microsoft.Data.Sqlite
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewSnapshotStore

let private anchor = DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)

let private snapshot timestamp count : OverviewSnapshot =
    { Timestamp = timestamp
      Tasks =
        [ { Kind = TaskBucketKind.Planned
            Count = count } ]
      Agents = [] }

let private createApi store =
    WorktreeApi.worktreeApi
        (RefreshScheduler.createAgent ())
        (SyncEngine.createSyncAgent ())
        (CardEventLog.createAgent ())
        (SessionManager.createAgent ())
        None
        (Some store)
        []
        None
        "test"
        None

let private readHistory store window =
    (createApi store).getOverviewHistory window
    |> Async.RunSynchronously

let private hasTable path tableName =
    use connection =
        new SqliteConnection(
            SqliteConnectionStringBuilder(DataSource = path, Pooling = false).ConnectionString
        )

    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        """
SELECT count(*)
FROM sqlite_master
WHERE type = 'table' AND name = $name;
"""
    command.Parameters.AddWithValue("$name", tableName) |> ignore
    Convert.ToInt32(command.ExecuteScalar()) > 0

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryApiTests() =

    [<Test>]
    member _.``empty snapshot store returns a successful current-grid anchor``() =
        Tests.SqliteTestDatabase.withDbPath "treemon-overview-api-empty" (fun path ->
            let store = OverviewSnapshotStore path
            let before = floorBoundary DateTimeOffset.UtcNow
            let response = readHistory store HistoryWindow.Hours12
            let after = floorBoundary DateTimeOffset.UtcNow

            Assert.Multiple(fun () ->
                Assert.That(response.Snapshots, Is.Empty)
                Assert.That(response.Anchor, Is.GreaterThanOrEqualTo before)
                Assert.That(response.Anchor, Is.LessThanOrEqualTo after)
                Assert.That(response.Anchor.ToUnixTimeSeconds() % 30L, Is.EqualTo 0L)))

    [<Test>]
    member _.``all windows return partial gapped rows on their anchor-aligned grids``() =
        [ HistoryWindow.Hours12, 5
          HistoryWindow.Hours24, 10
          HistoryWindow.Hours72, 30 ]
        |> List.iteri (fun index (window, stride) ->
            Tests.SqliteTestDatabase.withDbPath $"treemon-overview-api-grid-{index}" (fun path ->
                let store = OverviewSnapshotStore path
                let totalBuckets =
                    int (HistoryWindow.duration window).TotalSeconds / 30
                let atDifference bucketCount =
                    anchor.AddSeconds(float (-30 * bucketCount))
                let expected =
                    [ snapshot (atDifference totalBuckets) 1
                      snapshot (atDifference (2 * stride)) 2
                      snapshot anchor 3 ]

                [ snapshot (atDifference (totalBuckets + stride)) 10
                  snapshot (atDifference (stride + 1)) 11 ]
                @ expected
                |> List.sortBy _.Timestamp
                |> List.iter (store.Insert >> ignore)

                let response = readHistory store window

                Assert.Multiple(fun () ->
                    Assert.That(response.Anchor, Is.EqualTo anchor)
                    Assert.That(response.Snapshots, Is.EqualTo expected))))

    [<Test>]
    member _.``full 72 hour response is bounded to 289 snapshots in oldest-first order``() =
        Tests.SqliteTestDatabase.withDbPath "treemon-overview-api-limit" (fun path ->
            let store = OverviewSnapshotStore path
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours72
            let expected =
                [ 0..288 ]
                |> List.map (fun index ->
                    snapshot (start.AddMinutes(float (index * 15))) (index + 1))

            expected |> List.iter (store.Insert >> ignore)
            let response = readHistory store HistoryWindow.Hours72

            Assert.Multiple(fun () ->
                Assert.That(response.Anchor, Is.EqualTo anchor)
                Assert.That(response.Snapshots.Length, Is.EqualTo 289)
                Assert.That(response.Snapshots, Is.EqualTo expected)))

    [<Test>]
    member _.``equal runs collapse to their earliest timestamps``() =
        Tests.SqliteTestDatabase.withDbPath "treemon-overview-api-collapse" (fun path ->
            let store = OverviewSnapshotStore path
            let start = anchor - HistoryWindow.duration HistoryWindow.Hours12

            [ snapshot start 1
              snapshot (start.AddMinutes 2.5) 1
              snapshot (start.AddMinutes 5.0) 2
              snapshot (start.AddMinutes 7.5) 2
              snapshot anchor 3 ]
            |> List.iter (store.Insert >> ignore)

            let response = readHistory store HistoryWindow.Hours12

            Assert.That(
                response.Snapshots,
                Is.EqualTo
                    [ snapshot start 1
                      snapshot (start.AddMinutes 5.0) 2
                      snapshot anchor 3 ]
            ))

    [<Test>]
    member _.``snapshot-only database serves history without activity events``() =
        Tests.SqliteTestDatabase.withDbPath "treemon-overview-api-snapshot-only" (fun path ->
            let store = OverviewSnapshotStore path
            let expected = snapshot anchor 7
            store.Insert expected |> ignore

            Assert.That(hasTable path "activity_events", Is.False)

            let response = readHistory store HistoryWindow.Hours24

            Assert.Multiple(fun () ->
                Assert.That(response.Anchor, Is.EqualTo anchor)
                Assert.That(response.Snapshots, Is.EqualTo [ expected ])))
