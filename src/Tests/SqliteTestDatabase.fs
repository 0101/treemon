module Tests.SqliteTestDatabase

open System
open System.IO
open Microsoft.Data.Sqlite
open Server.SessionActivityStore

let withDbPath prefix action =
    let directory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid()}")
    Directory.CreateDirectory directory |> ignore
    let path = Path.Combine(directory, "activity.db")

    try
        action path
    finally
        try
            Directory.Delete(directory, true)
        with _ ->
            ()

let withStore prefix action =
    withDbPath prefix (fun path ->
        use store = new SessionActivityStore(path)
        action path store)

let openConnection path =
    let connection =
        new SqliteConnection(
            SqliteConnectionStringBuilder(DataSource = path, Pooling = false).ConnectionString
        )

    connection.Open()
    connection

let execute path sql =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

let scalarInt path sql =
    use connection = openConnection path
    use command = connection.CreateCommand()
    command.CommandText <- sql
    Convert.ToInt32(command.ExecuteScalar())
