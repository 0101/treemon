module Server.SqliteStorage

open System
open System.Globalization
open Microsoft.Data.Sqlite

// UTC round-trip strings have a fixed-width "+00:00" suffix, so SQLite lexical comparisons preserve
// timestamp order.
let internal isoUtc (timestamp: DateTimeOffset) =
    timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

let internal parseIso (value: string) =
    DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

// Drain readers through an immutable accumulator, then restore source order.
let rec internal readRows
    (reader: SqliteDataReader)
    (map: SqliteDataReader -> 'T)
    (acc: 'T list)
    : 'T list =
    if reader.Read() then readRows reader map (map reader :: acc)
    else List.rev acc
