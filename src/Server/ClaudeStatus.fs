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

type private EntryKind =
    | UserEntry
    | AssistantToolUse of hasAskUserQuestion: bool
    | AssistantDone

let private tryParseEntryKind (line: string) =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp ->
            match typeProp.GetString() with
            | "user" -> Some UserEntry
            | "assistant" ->
                match root.TryGetProperty("message") with
                | true, msg ->
                    let stopReason =
                        match msg.TryGetProperty("stop_reason") with
                        | true, sr when sr.ValueKind <> JsonValueKind.Null -> Some(sr.GetString())
                        | _ -> None

                    let toolUseBlocks =
                        match msg.TryGetProperty("content") with
                        | true, contentArr ->
                            contentArr.EnumerateArray()
                            |> Seq.filter (fun block ->
                                match block.TryGetProperty("type") with
                                | true, t -> t.GetString() = "tool_use"
                                | _ -> false)
                            |> Seq.toList
                        | _ -> []

                    match stopReason, toolUseBlocks with
                    | Some "tool_use", _
                    | _, _ :: _ ->
                        let hasAskUser =
                            toolUseBlocks
                            |> List.exists (fun block ->
                                match block.TryGetProperty("name") with
                                | true, n -> n.GetString() = "AskUserQuestion"
                                | _ -> false)

                        Some(AssistantToolUse hasAskUser)
                    | _ -> Some AssistantDone
                | _ -> Some AssistantDone
            | _ -> None
        | _ -> None
    with _ -> None

let private statusFromEntry entryKind =
    match entryKind with
    | UserEntry -> Working
    | AssistantToolUse true -> WaitingForUser
    | AssistantToolUse false -> Working
    | AssistantDone -> Done

let getClaudeStatus (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    match findLatestJsonl projectDir with
    | Some fi ->
        try
            let age = DateTimeOffset.UtcNow - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)

            match age > TimeSpan.FromHours(2.0) with
            | true -> Idle
            | false ->
                let jsonlStatus =
                    readLinesReverse fi.FullName
                    |> List.tryPick tryParseEntryKind
                    |> Option.map statusFromEntry
                    |> Option.defaultValue Idle

                match jsonlStatus, age < TimeSpan.FromMinutes(1.0) with
                | Done, true -> Working
                | status, _ -> status
        with ex ->
            Log.log "Claude" $"Failed to read status for {fi.FullName}: {ex.Message}"
            Idle
    | None -> Idle

let private truncateMessage (maxLen: int) (text: string) =
    let singleLine = text.Replace("\r", "").Replace("\n", " ").Trim()

    match singleLine.Length <= maxLen with
    | true -> singleLine
    | false -> singleLine.[..maxLen-1].TrimEnd() + "..."

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
