module Server.ClaudeStatus

open System
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
        Log.log "Claude" $"Failed to list directory {projectDir}: {ex.Message}"
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
            Log.log "Claude" $"Failed to read mtime for {fi.FullName}: {ex.Message}"
            Unknown
    | None -> Unknown

let private truncateMessage (maxLen: int) (text: string) =
    let singleLine = text.Replace("\r", "").Replace("\n", " ").Trim()

    match singleLine.Length <= maxLen with
    | true -> singleLine
    | false -> singleLine.[..maxLen-1].TrimEnd() + "..."

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
          Status = None
          Duration = None })
