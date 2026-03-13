module Server.CopilotDetector

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open Shared

let private copilotSessionDir =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot",
        "session-state"
    )

type private WorkspaceIndex =
    { PathToSessionDirs: Dictionary<string, string list>
      BuiltAt: DateTimeOffset }

let private workspaceIndex = ref { PathToSessionDirs = Dictionary(StringComparer.OrdinalIgnoreCase); BuiltAt = DateTimeOffset.MinValue }

let private parseCwdFromYaml (yamlPath: string) =
    try
        File.ReadAllLines(yamlPath)
        |> Array.tryPick (fun line ->
            if line.StartsWith("cwd:") then
                Some(line.Substring(4).Trim())
            else
                None)
    with _ ->
        None

let private buildWorkspaceIndex () =
    let index = Dictionary<string, string list>(StringComparer.OrdinalIgnoreCase)

    try
        if Directory.Exists(copilotSessionDir) then
            Directory.GetDirectories(copilotSessionDir)
            |> Array.iter (fun sessionDir ->
                let yamlPath = Path.Combine(sessionDir, "workspace.yaml")

                parseCwdFromYaml yamlPath
                |> Option.iter (fun cwd ->
                    let normalized = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    let existing = if index.ContainsKey(normalized) then index[normalized] else []
                    index[normalized] <- sessionDir :: existing))
    with ex ->
        Log.log "Copilot" $"Failed to scan session directories: {ex.Message}"

    { PathToSessionDirs = index; BuiltAt = DateTimeOffset.UtcNow }

let private refreshIndex () =
    FileUtils.refreshIfStale (TimeSpan.FromSeconds(60.0)) workspaceIndex _.BuiltAt buildWorkspaceIndex

let private getSessionDirsForPath (worktreePath: string) =
    let index = refreshIndex ()
    let normalized = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    match index.PathToSessionDirs.TryGetValue(normalized) with
    | true, dirs -> dirs
    | false, _ -> []

type private EventKind =
    | UserMessage
    | AssistantMessage of hasAskUser: bool
    | TurnStart
    | TurnEnd

let private tryParseEventKind (line: string) =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp ->
            match typeProp.GetString() with
            | "user.message" -> Some UserMessage
            | "assistant.turn_start" -> Some TurnStart
            | "assistant.turn_end" -> Some TurnEnd
            | "assistant.message" ->
                let hasAskUser =
                    match root.TryGetProperty("data") with
                    | true, data ->
                        match data.TryGetProperty("toolRequests") with
                        | true, reqs when reqs.ValueKind = JsonValueKind.Array ->
                            reqs.EnumerateArray()
                            |> Seq.exists (fun req ->
                                match req.TryGetProperty("name") with
                                | true, n -> n.GetString() = "ask_user"
                                | _ -> false)
                        | _ -> false
                    | _ -> false

                Some(AssistantMessage hasAskUser)
            | _ -> None
        | _ -> None
    with ex ->
        Log.log "Copilot" $"Failed to parse event kind: {ex.Message}"
        None

let private statusFromEvent eventKind =
    match eventKind with
    | UserMessage -> Working
    | TurnStart -> Working
    | AssistantMessage true -> WaitingForUser
    | AssistantMessage false -> Working
    | TurnEnd -> Done

let private findMostRecentEventsFile (sessionDirs: string list) =
    sessionDirs
    |> List.choose (fun dir ->
        let eventsPath = Path.Combine(dir, "events.jsonl")
        try
            if File.Exists(eventsPath) then
                Some(FileInfo(eventsPath))
            else
                None
        with _ ->
            None)
    |> List.sortByDescending _.LastWriteTimeUtc
    |> List.tryHead

let getSessionMtime (worktreePath: string) =
    let sessionDirs = getSessionDirsForPath worktreePath

    findMostRecentEventsFile sessionDirs
    |> Option.map (fun fi -> DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero))

let getStatus (worktreePath: string) =
    let sessionDirs = getSessionDirsForPath worktreePath

    match findMostRecentEventsFile sessionDirs with
    | Some fi ->
        let age = DateTimeOffset.UtcNow - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
        if age > TimeSpan.FromHours(2.0) then
            Idle
        else
            FileUtils.readLastLines "Copilot" fi.FullName 20
            |> List.tryPick tryParseEventKind
            |> Option.map statusFromEvent
            |> Option.defaultValue Idle
    | None -> Idle

let private tryParseAssistantContent (line: string) =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp when typeProp.GetString() = "assistant.message" ->
            let timestamp =
                match root.TryGetProperty("timestamp") with
                | true, ts -> ts.GetString() |> DateTimeOffset.Parse |> Some
                | _ -> None

            let textContent =
                match root.TryGetProperty("data") with
                | true, data ->
                    match data.TryGetProperty("content") with
                    | true, content when content.ValueKind = JsonValueKind.String ->
                        let text = content.GetString()
                        if String.IsNullOrWhiteSpace(text) then None else Some text
                    | _ -> None
                | _ -> None

            match textContent, timestamp with
            | Some text, Some ts -> Some(text, ts)
            | _ -> None
        | _ -> None
    with ex ->
        Log.log "Copilot" $"Failed to parse assistant content: {ex.Message}"
        None

let getLastMessage (worktreePath: string) =
    let sessionDirs = getSessionDirsForPath worktreePath

    findMostRecentEventsFile sessionDirs
    |> Option.bind (fun fi ->
        FileUtils.scanBackward "Copilot" fi.FullName tryParseAssistantContent)
    |> Option.map (fun (text, timestamp) ->
        { Source = "copilot"
          Message = FileUtils.truncateMessage 80 text
          Timestamp = timestamp
          Status = None
          Duration = None })

let private tryParseUserContent (line: string) =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp when typeProp.GetString() = "user.message" ->
            let timestamp =
                match root.TryGetProperty("timestamp") with
                | true, ts -> ts.GetString() |> DateTimeOffset.Parse |> Some
                | _ -> None

            let textContent =
                match root.TryGetProperty("data") with
                | true, data ->
                    match data.TryGetProperty("content") with
                    | true, content when content.ValueKind = JsonValueKind.String ->
                        let text = content.GetString()
                        if String.IsNullOrWhiteSpace(text) then None else Some text
                    | _ -> None
                | _ -> None

            match textContent with
            | Some text -> Some(text, timestamp |> Option.defaultValue DateTimeOffset.MinValue)
            | None -> None
        | _ -> None
    with ex ->
        Log.log "Copilot" $"Failed to parse user content: {ex.Message}"
        None

let internal scanForUserMessage (eventsPath: string) =
    FileUtils.scanBackward "Copilot" eventsPath tryParseUserContent

let getLastUserMessage (worktreePath: string) =
    let sessionDirs = getSessionDirsForPath worktreePath

    findMostRecentEventsFile sessionDirs
    |> Option.bind (fun fi -> scanForUserMessage fi.FullName)
    |> Option.map (fun (text, ts) -> FileUtils.truncateMessage 120 text, ts)

let internal getStatusFromEventsFile (eventsPath: string) (now: DateTimeOffset) =
    try
        let fi = FileInfo(eventsPath)
        if not fi.Exists then Idle
        else
            let age = now - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
            if age > TimeSpan.FromHours(2.0) then
                Idle
            else
                FileUtils.readLastLines "Copilot" eventsPath 20
                |> List.tryPick tryParseEventKind
                |> Option.map statusFromEvent
                |> Option.defaultValue Idle
    with ex ->
        Log.log "Copilot" $"Failed to read status from {eventsPath}: {ex.Message}"
        Idle

let internal getLastMessageFromEventsFile (eventsPath: string) =
    try
        if not (File.Exists(eventsPath)) then None
        else
            FileUtils.scanBackward "Copilot" eventsPath tryParseAssistantContent
            |> Option.map (fun (text, timestamp) ->
                { Source = "copilot"
                  Message = FileUtils.truncateMessage 80 text
                  Timestamp = timestamp
                  Status = None
                  Duration = None })
    with ex ->
        Log.log "Copilot" $"Failed to read last message from {eventsPath}: {ex.Message}"
        None

let internal parseCwd (yamlPath: string) = parseCwdFromYaml yamlPath
