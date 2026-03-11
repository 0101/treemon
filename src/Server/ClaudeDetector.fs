module Server.ClaudeDetector

open System
open System.IO
open System.Text.Json
open Shared

type SessionFileKind = Parent | Subagent

type SessionFileData =
    { Kind: SessionFileKind
      LastWriteUtc: DateTimeOffset
      LastLinesReversed: string list }

let private claudeProjectsDir =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects"
    )

let encodeWorktreePath (worktreePath: string) =
    worktreePath.Replace(":", "-").Replace("\\", "-").Replace("/", "-")

let internal findAllJsonlFiles (projectDir: string) =
    try
        if Directory.Exists(projectDir) then
            let topLevel =
                Directory.GetFiles(projectDir, "*.jsonl")
                |> Array.map (fun f -> FileInfo(f), Parent)
            let subagentFiles =
                Directory.GetDirectories(projectDir)
                |> Array.collect (fun sessionDir ->
                    let subagentsDir = Path.Combine(sessionDir, "subagents")
                    if Directory.Exists(subagentsDir) then
                        Directory.GetFiles(subagentsDir, "*.jsonl")
                        |> Array.map (fun f -> FileInfo(f), Subagent)
                    else
                        Array.empty)
            Array.append topLevel subagentFiles
            |> Array.toList
        else
            []
    with ex ->
        Log.log "Claude" $"Failed to list directory {projectDir}: {ex.Message}"
        []

let enumerateFiles (worktreePath: string) =
    let encoded = encodeWorktreePath worktreePath
    let projectDir = Path.Combine(claudeProjectsDir, encoded)
    findAllJsonlFiles projectDir

let private parentFiles (files: (FileInfo * SessionFileKind) list) =
    files |> List.choose (function (fi, Parent) -> Some fi | _ -> None)

let private tryMaxBy projection = function
    | [] -> None
    | items -> items |> List.maxBy projection |> Some

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
    || text = "Warmup"

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
                | Some _ -> Some(UserEntry, timestamp)
                | None -> None
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

let private statusPriority = function
    | Working -> 3
    | WaitingForUser -> 2
    | Done -> 1
    | Idle -> 0

let private getFileStatus (now: DateTimeOffset) (file: SessionFileData) =
    let fileAge = now - file.LastWriteUtc
    if fileAge > TimeSpan.FromHours(2.0) then
        Idle
    else
        let parsed =
            file.LastLinesReversed
            |> List.tryPick tryParseEntryKind
            |> Option.map (fun (kind, timestamp) ->
                let entryAge =
                    timestamp
                    |> Option.map (fun ts -> now - ts)
                    |> Option.defaultValue fileAge
                statusFromEntry kind, entryAge)

        match parsed with
        | Some (Done, _) when fileAge < TimeSpan.FromSeconds(10.0) -> Working
        | Some (Working, entryAge) when entryAge > stalenessTimeout -> Idle
        | Some (status, _) -> status
        | None -> Idle

let private bestStatusByKind (now: DateTimeOffset) (kind: SessionFileKind) (files: SessionFileData list) =
    files
    |> List.filter (fun f -> f.Kind = kind)
    |> List.map (getFileStatus now)
    |> function
       | [] -> None
       | statuses -> statuses |> List.maxBy statusPriority |> Some

let internal getStatusFromFiles (now: DateTimeOffset) (files: SessionFileData list) =
    match files with
    | [] -> Idle
    | _ ->
        let parentStatus = bestStatusByKind now Parent files |> Option.defaultValue Idle
        match parentStatus with
        | Working | WaitingForUser -> parentStatus
        | Done -> Done
        | Idle ->
            let subagentStatus = bestStatusByKind now Subagent files |> Option.defaultValue Idle
            match subagentStatus with
            | Working -> Working
            | _ -> Idle

let getStatusFromEnumeratedFiles (files: (FileInfo * SessionFileKind) list) =
    let sessionFiles =
        files
        |> List.choose (fun (fi, kind) ->
            try
                let lastWrite = DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
                let lines = FileUtils.readLastLines "Claude" fi.FullName 20
                Some { Kind = kind; LastWriteUtc = lastWrite; LastLinesReversed = lines }
            with ex ->
                Log.log "Claude" $"Failed to read status for {fi.FullName}: {ex.Message}"
                None)

    getStatusFromFiles DateTimeOffset.UtcNow sessionFiles

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

let getSessionMtimeFromFiles (files: (FileInfo * SessionFileKind) list) =
    files
    |> List.map (fun (fi, _) -> DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero))
    |> tryMaxBy id

let getLastMessageFromFiles (files: (FileInfo * SessionFileKind) list) =
    files
    |> parentFiles
    |> List.choose (fun fi ->
        FileUtils.readLastLines "Claude" fi.FullName 20
        |> List.tryPick tryParseAssistantText)
    |> tryMaxBy snd
    |> Option.map (fun (text, timestamp) ->
        { Source = "claude"
          Message = FileUtils.truncateMessage 80 text
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

let getLastUserMessageFromFiles (files: (FileInfo * SessionFileKind) list) =
    files
    |> parentFiles
    |> List.choose (fun fi -> scanForUserMessage fi.FullName)
    |> tryMaxBy snd
    |> Option.map (fun (text, ts) -> FileUtils.truncateMessage 120 text, ts)
