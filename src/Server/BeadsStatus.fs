module Server.BeadsStatus

open System
open System.IO
open System.Text.Json
open Shared

let private runBd (dbPath: string) =
    ProcessRunner.run "Beads" "bd" $"count --by-status --json --db \"{dbPath}\""

let private parseCountResponse (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let groups =
            match root.TryGetProperty("groups") with
            | true, arr ->
                arr.EnumerateArray()
                |> Seq.map (fun el ->
                    el.GetProperty("group").GetString(),
                    el.GetProperty("count").GetInt32())
                |> Map.ofSeq
            | _ -> Map.empty

        let findCount name =
            Map.tryFind name groups
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

        if File.Exists(dbPath) then
            let! output = runBd dbPath
            return
                output
                |> Option.map parseCountResponse
                |> Option.defaultValue BeadsSummary.zero
        else
            return BeadsSummary.zero
    }
