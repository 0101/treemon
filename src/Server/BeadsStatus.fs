module Server.BeadsStatus

open System
open System.IO
open System.Text.Json
open Shared

let private runBd (dbPath: string) =
    ProcessRunner.run "Beads" "bd" $"count --by-status --json --db \"{dbPath}\""

let private parseCountResponse (json: string) =
    try
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let groups =
            match root.TryGetProperty("groups") with
            | true, arr ->
                arr.EnumerateArray()
                |> Seq.map (fun el ->
                    let group = el.GetProperty("group").GetString()
                    let count = el.GetProperty("count").GetInt32()
                    group, count)
                |> Seq.toList
            | _ -> []

        let findCount name =
            groups
            |> List.tryFind (fun (g, _) -> g = name)
            |> Option.map snd
            |> Option.defaultValue 0

        { Open = findCount "open"
          InProgress = findCount "in_progress"
          Closed = findCount "closed" }
    with ex ->
        Log.log "Beads" $"Failed to parse bd JSON: {ex.Message}, raw input: {json}"
        BeadsSummary.zero

let getBeadsSummary (worktreePath: string) =
    async {
        let dbPath = Path.Combine(worktreePath, ".beads", "beads.db")

        if not (File.Exists(dbPath)) then
            return BeadsSummary.zero
        else
            let! output = runBd dbPath

            return
                output
                |> Option.map parseCountResponse
                |> Option.defaultValue BeadsSummary.zero
    }

module Cache =
    let private cache = Cache.TtlCache<BeadsSummary>(TimeSpan.FromSeconds(15.0))

    let getCachedBeadsSummary (worktreePath: string) =
        cache.GetOrRefreshAsync worktreePath (fun key -> getBeadsSummary key)

    let getOldestCachedAt () = cache.GetOldestCachedAt()
