module Server.ClaudeDetector

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
            |> Array.sortByDescending _.LastWriteTimeUtc
            |> Array.tryHead
        else
            None
    with ex ->
        Log.log "Claude" $"Failed to list directory {projectDir}: {ex.Message}"
        None

let private readLastLines (filePath: string) (maxLines: int) =
    try
        use stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        if stream.Length = 0L then []
        else
            // Read last 64KB or full file if smaller
            let bufferSize = 64 * 1024
            let length = stream.Length
            let start = Math.Max(0L, length - int64 bufferSize)
            stream.Seek(start, SeekOrigin.Begin) |> ignore

            use reader = new StreamReader(stream)
            let content = reader.ReadToEnd()
            let lines = content.Split([| '\r'; '\n' |], StringSplitOptions.None)

            // If we didn't read the whole file, the first line might be partial
            let linesToProcess =
                if start > 0L && lines.Length > 0 then
                    lines[1..]
                else
                    lines

            linesToProcess
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s.Length > 0)
            |> Array.rev
            |> Array.truncate maxLines
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
    with ex ->
        Log.log "Claude" $"Failed to parse entry kind: {ex.Message}"
        None

let private statusFromEntry entryKind =
    match entryKind with
    | UserEntry -> Working
    | AssistantToolUse true -> WaitingForUser
    | AssistantToolUse false -> Working
    | AssistantDone -> Done

let getStatus (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    match findLatestJsonl projectDir with
    | Some fi ->
        try
            let age = DateTimeOffset.UtcNow - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
            if age > TimeSpan.FromHours(2.0) then
                Idle
            else
                readLastLines fi.FullName 20
                |> List.tryPick tryParseEntryKind
                |> Option.map statusFromEntry
                |> Option.defaultValue Idle
                |> fun status ->
                    if status = Done && age < TimeSpan.FromMinutes(1.0) then Working else status
        with ex ->
            Log.log "Claude" $"Failed to read status for {fi.FullName}: {ex.Message}"
            Idle
    | None -> Idle

let private truncateMessage (maxLen: int) (text: string) =
    let singleLine = text.Replace("\r", "").Replace("\n", " ").Trim()
    if singleLine.Length <= maxLen then singleLine
    else singleLine[..maxLen-1].TrimEnd() + "..."

let private tryParseAssistantText (line: string) =
    try
        use doc = JsonDocument.Parse(line)
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
    with ex ->
        Log.log "Claude" $"Failed to parse assistant text: {ex.Message}"
        None

let getSessionMtime (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    findLatestJsonl projectDir
    |> Option.map (fun fi -> DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero))

let getLastMessage (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    findLatestJsonl projectDir
    |> Option.bind (fun fi ->
        readLastLines fi.FullName 20
        |> List.tryPick tryParseAssistantText)
    |> Option.map (fun (text, timestamp) ->
        { Source = "claude"
          Message = truncateMessage 80 text
          Timestamp = timestamp
          Status = None
          Duration = None })
