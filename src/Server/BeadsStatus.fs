module Server.BeadsStatus

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open Shared

let private runBd (dbPath: string) =
    async {
        let args = $"count --by-status --json --db \"{dbPath}\""

        try
            let psi =
                ProcessStartInfo(
                    "bd",
                    args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            use proc = Process.Start(psi)
            let! output = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
            let! stderr = proc.StandardError.ReadToEndAsync() |> Async.AwaitTask
            do! proc.WaitForExitAsync() |> Async.AwaitTask

            match proc.ExitCode with
            | 0 ->
                let trimmed = output.TrimEnd()
                Log.log "Beads" $"bd {args} -> exit 0, stdout: {trimmed}"
                return Some trimmed
            | code ->
                Log.log "Beads" $"bd {args} -> exit {code}, stderr: {stderr.TrimEnd()}"
                return None
        with
        | :? System.ComponentModel.Win32Exception as ex ->
            Log.log "Beads" $"bd {args} -> failed to start: {ex.Message}"
            return None
    }

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
        { Open = 0; InProgress = 0; Closed = 0 }

let private zeroCounts =
    { Open = 0; InProgress = 0; Closed = 0 }

let getBeadsSummary (worktreePath: string) =
    async {
        let dbPath = Path.Combine(worktreePath, ".beads", "beads.db")

        if not (File.Exists(dbPath)) then
            return zeroCounts
        else
            let! output = runBd dbPath

            return
                output
                |> Option.map parseCountResponse
                |> Option.defaultValue zeroCounts
    }

module Cache =
    type CacheEntry<'T> =
        { Value: 'T
          CachedAt: DateTimeOffset }

    let private cache = ConcurrentDictionary<string, CacheEntry<BeadsSummary>>()
    let private ttl = TimeSpan.FromSeconds(15.0)

    let getCachedBeadsSummary (worktreePath: string) =
        async {
            let now = DateTimeOffset.UtcNow

            match cache.TryGetValue(worktreePath) with
            | true, entry when now - entry.CachedAt < ttl -> return entry.Value
            | _ ->
                let! summary = getBeadsSummary worktreePath
                cache.[worktreePath] <- { Value = summary; CachedAt = now }
                return summary
        }
