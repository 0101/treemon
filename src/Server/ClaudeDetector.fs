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

let isSystemNoise (text: string) =
    text.Contains("PRESERVE ON CONTEXT COMPACTION")
    || text.StartsWith("<local-command-")
    || text.StartsWith("<system-reminder>")
    || text.StartsWith("<task-notification>")
    || text.Contains("<command-name>")
    || text.Contains("[Request interrupted by user]")
    || (text.StartsWith("# ") && text.Length > 200)
    || (text.StartsWith("**") && text.Length > 200)

let private extractTextFromMessage (root: JsonElement) =
    match root.TryGetProperty("message") with
    | true, msg ->
        match msg.TryGetProperty("content") with
        | true, c when c.ValueKind = JsonValueKind.String -> Some(c.GetString())
        | true, c when c.ValueKind = JsonValueKind.Array ->
            c.EnumerateArray()
            |> Seq.tryFind (fun b ->
                match b.TryGetProperty("type") with
                | true, t -> t.GetString() = "text"
                | _ -> false)
            |> Option.bind (fun b ->
                match b.TryGetProperty("text") with
                | true, t -> Some(t.GetString())
                | _ -> None)
        | _ -> None
    | _ -> None

let private tryParseTimestamp (root: JsonElement) =
    match root.TryGetProperty("timestamp") with
    | true, ts ->
        match DateTimeOffset.TryParse(ts.GetString()) with
        | true, dto -> Some dto
        | _ -> None
    | _ -> None

let private tryParseEntryKind (line: string) =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement
        let timestamp = tryParseTimestamp root

        match root.TryGetProperty("type") with
        | true, typeProp ->
            match typeProp.GetString() with
            | "user" ->
                let text = extractTextFromMessage root
                match text with
                | Some t when isSystemNoise t -> None
                | _ -> Some(UserEntry, timestamp)
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

                        Some(AssistantToolUse hasAskUser, timestamp)
                    | _ -> Some(AssistantDone, timestamp)
                | _ -> Some(AssistantDone, timestamp)
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

let private stalenessTimeout = TimeSpan.FromMinutes(30.0)

let getStatus (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    match findLatestJsonl projectDir with
    | Some fi ->
        try
            let fileAge = DateTimeOffset.UtcNow - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
            if fileAge > TimeSpan.FromHours(2.0) then
                Idle
            else
                let entry =
                    readLastLines fi.FullName 20
                    |> List.tryPick tryParseEntryKind

                match entry with
                | Some(kind, timestamp) ->
                    let status = statusFromEntry kind
                    let entryAge =
                        timestamp
                        |> Option.map (fun ts -> DateTimeOffset.UtcNow - ts)
                        |> Option.defaultValue fileAge

                    match status with
                    | Done when fileAge < TimeSpan.FromSeconds(10.0) -> Working
                    | Working when entryAge > stalenessTimeout -> Idle
                    | other -> other
                | None -> Idle
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

let private tryExtractSlashCommand (text: string) =
    let extractTag (tag: string) (s: string) =
        let openTag = $"<{tag}>"
        let closeTag = $"</{tag}>"
        match s.IndexOf(openTag), s.IndexOf(closeTag) with
        | start, finish when start >= 0 && finish > start ->
            Some(s.Substring(start + openTag.Length, finish - start - openTag.Length).Trim())
        | _ -> None

    extractTag "command-name" text
    |> Option.map (fun cmd ->
        match extractTag "command-args" text with
        | Some args when args.Length > 0 -> $"{cmd} {args}"
        | _ -> cmd)

let private extractUserContent (text: string) =
    match tryExtractSlashCommand text with
    | Some cmd -> Some cmd
    | None when isSystemNoise text -> None
    | None -> Some text

let private tryParseUserText (line: string) =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp when typeProp.GetString() = "user" ->
            let timestamp =
                match root.TryGetProperty("timestamp") with
                | true, ts -> ts.GetString() |> DateTimeOffset.Parse |> Some
                | _ -> None

            let rawContent =
                match root.TryGetProperty("message") with
                | true, msg ->
                    match msg.TryGetProperty("content") with
                    | true, content when content.ValueKind = JsonValueKind.String ->
                        Some(content.GetString())
                    | true, contentArr when contentArr.ValueKind = JsonValueKind.Array ->
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

            match rawContent |> Option.bind extractUserContent, timestamp with
            | Some text, Some ts when not (String.IsNullOrWhiteSpace(text)) ->
                Some(text, ts)
            | _ -> None
        | _ -> None
    with ex ->
        Log.log "Claude" $"Failed to parse user text: {ex.Message}"
        None

let private findUserMessageInLines (lines: string array) =
    lines
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.rev
    |> Array.tryPick tryParseUserText

let scanForUserMessage (filePath: string) =
    let chunkSize = 64L * 1024L
    let overlap = 1024L
    let stepSize = chunkSize - overlap
    let maxChunks = 16

    try
        use stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let fileLength = stream.Length
        if fileLength = 0L then None
        else
            let rec scanChunk chunkIndex =
                if chunkIndex >= maxChunks then None
                else
                    let rawStart = fileLength - chunkSize - (int64 chunkIndex) * stepSize
                    let chunkStart = Math.Max(0L, rawStart)
                    let readLength = int (Math.Min(chunkSize, fileLength - chunkStart))
                    if readLength <= 0 then None
                    else
                        let isAtFileStart = chunkStart = 0L
                        stream.Seek(chunkStart, SeekOrigin.Begin) |> ignore
                        let buffer = Array.zeroCreate readLength
                        let bytesRead = stream.Read(buffer, 0, readLength)
                        let content = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead)
                        let lines = content.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

                        let trimmedLines =
                            if isAtFileStart || lines.Length = 0 then lines
                            else lines[1..]

                        match findUserMessageInLines trimmedLines with
                        | Some _ as result -> result
                        | None when isAtFileStart -> None
                        | None -> scanChunk (chunkIndex + 1)

            scanChunk 0
    with ex ->
        Log.log "Claude" $"Failed to scan JSONL {filePath}: {ex.Message}"
        None

let getLastUserMessage (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)

    findLatestJsonl projectDir
    |> Option.bind (fun fi -> scanForUserMessage fi.FullName)
    |> Option.map (fun (text, ts) -> truncateMessage 120 text, ts)
