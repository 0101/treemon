module Server.ClaudeStatus

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open Shared

let private claudeProjectsDir =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects"
    )

let encodeWorktreePath (worktreePath: string) =
    worktreePath.Replace(":", "-").Replace("\\", "-").Replace("/", "-")

let private findLatestJsonl (projectDir: string) =
    try
        if Directory.Exists(projectDir) then
            Directory.GetFiles(projectDir, "*.jsonl")
            |> Array.map (fun f -> FileInfo(f))
            |> Array.sortByDescending (fun fi -> fi.LastWriteTimeUtc)
            |> Array.tryHead
        else
            None
    with ex ->
        Log.log "Claude" (sprintf "Failed to list directory %s: %s" projectDir ex.Message)
        None

let private statusFromAge (age: TimeSpan) =
    match age with
    | a when a < TimeSpan.FromMinutes(2.0) -> Active
    | a when a < TimeSpan.FromMinutes(30.0) -> Recent
    | _ -> Idle

let getClaudeStatus (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    match findLatestJsonl projectDir with
    | Some fi ->
        try
            let age = DateTimeOffset.UtcNow - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
            statusFromAge age
        with ex ->
            Log.log "Claude" (sprintf "Failed to read mtime for %s: %s" fi.FullName ex.Message)
            Unknown
    | None -> Unknown

let private truncateMessage (maxLen: int) (text: string) =
    let singleLine = text.Replace("\r", "").Replace("\n", " ").Trim()

    match singleLine.Length <= maxLen with
    | true -> singleLine
    | false -> singleLine.Substring(0, maxLen).TrimEnd() + "..."

let private readLinesReverse (filePath: string) =
    try
        use stream =
            new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

        use reader = new StreamReader(stream)
        let allLines = reader.ReadToEnd().Split('\n')

        allLines
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s.Length > 0)
        |> Array.rev
        |> Array.toList
    with ex ->
        Log.log "Claude" $"Failed to read JSONL {filePath}: {ex.Message}"
        []

let private tryParseAssistantText (line: string) =
    try
        let doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp when typeProp.GetString() = "assistant" ->
            let timestamp =
                match root.TryGetProperty("timestamp") with
                | true, ts -> ts.GetString() |> DateTimeOffset.Parse |> Some
                | _ -> None

            let textContent =
                match root.TryGetProperty("message") with
                | true, msg ->
                    match msg.TryGetProperty("content") with
                    | true, contentArr ->
                        contentArr.EnumerateArray()
                        |> Seq.tryFind (fun block ->
                            match block.TryGetProperty("type") with
                            | true, t -> t.GetString() = "text"
                            | _ -> false)
                        |> Option.bind (fun block ->
                            match block.TryGetProperty("text") with
                            | true, t -> Some(t.GetString())
                            | _ -> None)
                    | _ -> None
                | _ -> None

            match textContent, timestamp with
            | Some text, Some ts -> Some(text, ts)
            | _ -> None
        | _ -> None
    with _ ->
        None

let getLastClaudeMessage (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    findLatestJsonl projectDir
    |> Option.bind (fun fi ->
        readLinesReverse fi.FullName
        |> List.tryPick tryParseAssistantText)
    |> Option.map (fun (text, timestamp) ->
        { Source = "claude"
          Message = truncateMessage 80 text
          Timestamp = timestamp
          Status = None })

module Cache =
    type CacheEntry<'T> =
        { Value: 'T
          CachedAt: DateTimeOffset }

    let private statusCache = ConcurrentDictionary<string, CacheEntry<ClaudeCodeStatus>>()
    let private messageCache = ConcurrentDictionary<string, CacheEntry<CardEvent option>>()
    let private ttl = TimeSpan.FromSeconds(15.0)

    let private getOrRefresh (cache: ConcurrentDictionary<string, CacheEntry<'T>>) key (compute: unit -> 'T) =
        let now = DateTimeOffset.UtcNow

        match cache.TryGetValue(key) with
        | true, entry when now - entry.CachedAt < ttl -> entry.Value
        | _ ->
            let value = compute ()
            cache.[key] <- { Value = value; CachedAt = now }
            value

    let getCachedClaudeStatus (worktreePath: string) =
        getOrRefresh statusCache worktreePath (fun () -> getClaudeStatus worktreePath)

    let getCachedLastMessage (worktreePath: string) =
        getOrRefresh messageCache worktreePath (fun () -> getLastClaudeMessage worktreePath)

    let getOldestCachedAt () =
        statusCache.Values
        |> Seq.map (fun entry -> entry.CachedAt)
        |> Seq.sortBy id
        |> Seq.tryHead
