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

let private findMostRecentSessionDir (sessionDirs: string list) =
    sessionDirs
    |> List.choose (fun dir ->
        let eventsPath = Path.Combine(dir, "events.jsonl")
        try
            if File.Exists(eventsPath) then
                Some(dir, FileInfo(eventsPath))
            else
                None
        with _ ->
            None)
    |> List.sortByDescending (fun (_, fi) -> fi.LastWriteTimeUtc)
    |> List.tryHead

let private findMostRecentEventsFile (sessionDirs: string list) =
    findMostRecentSessionDir sessionDirs |> Option.map snd

let getLastSessionId (worktreePath: string) =
    getSessionDirsForPath worktreePath
    |> findMostRecentSessionDir
    |> Option.map (fun (dir, _) -> Path.GetFileName(dir))

let getSessionMtime (worktreePath: string) =
    let sessionDirs = getSessionDirsForPath worktreePath

    findMostRecentEventsFile sessionDirs
    |> Option.map (fun fi -> DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero))

let private graceWindow = TimeSpan.FromSeconds(15.0)
let private stalenessTimeout = TimeSpan.FromMinutes(30.0)

let internal getStatusFromEventsFile (eventsPath: string) (now: DateTimeOffset) =
    try
        let fi = FileInfo(eventsPath)
        if not fi.Exists then Idle
        else
            let fileAge = now - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
            if fileAge > TimeSpan.FromHours(2.0) then
                Idle
            else
                let rawStatus =
                    FileUtils.scanBackward "Copilot" eventsPath tryParseEventKind
                    |> Option.map statusFromEvent
                    |> Option.defaultValue Idle

                match rawStatus with
                | Done when fileAge < graceWindow -> Working
                | Working when fileAge > stalenessTimeout -> Idle
                | status -> status
    with ex ->
        Log.log "Copilot" $"Failed to read status from {eventsPath}: {ex.Message}"
        Idle

let getStatus (worktreePath: string) =
    let sessionDirs = getSessionDirsForPath worktreePath

    match findMostRecentEventsFile sessionDirs with
    | Some fi -> getStatusFromEventsFile fi.FullName DateTimeOffset.UtcNow
    | None -> Idle

// The running skill rides the same backward events scan as the red dot, but with *freshness*:
// Copilot CLI has no explicit "skill finished" event, so a skill counts as running-now only while
// its invocation is the most recent thing the agent did on the CURRENT request. Scanning backward
// from EOF, the first of these decides:
//   * a `skill.invoked` event (data.name), or the `skill` tool-call that requested it
//     (arguments.skill) -> that skill is running now; or
//   * a genuine `user.message` (a new top-level or scheduled request) -> the prior skill's run is
//     over, so report nothing. This is what stops a finished skill from lingering across turns
//     (the v1.1 (i) correction) — a bd-plan that finished before the user moved on no longer shows.
// A skill's own context-injection `user.message` (Copilot tags it `source: "skill-<name>"` with a
// `<skill-context …>` content preamble, written right after skill.invoked) is part of the skill
// starting, NOT a new request, so the scan steps past it. Because skill.invoked is written after its
// tool-call, recency prefers skill.invoked and falls back to the tool-call for a skill that is still
// starting. The raw name is surfaced; Shared.Activity.classify normalizes it.
type private SkillScan =
    | SkillRunning of string
    | RequestBoundary

let private tryReadSkillArgument (req: JsonElement) =
    let skillFromObject (obj: JsonElement) =
        match obj.TryGetProperty("skill") with
        | true, s when s.ValueKind = JsonValueKind.String ->
            let name = s.GetString()
            if String.IsNullOrWhiteSpace(name) then None else Some name
        | _ -> None

    // Real events.jsonl encodes arguments as a nested object ({"skill":"..."}); the session-store
    // schema names it arguments_json as a JSON string. Handle both so the source doesn't matter.
    let argsElement =
        match req.TryGetProperty("arguments"), req.TryGetProperty("arguments_json") with
        | (true, a), _ -> Some a
        | _, (true, a) -> Some a
        | _ -> None

    argsElement
    |> Option.bind (fun args ->
        match args.ValueKind with
        | JsonValueKind.Object -> skillFromObject args
        | JsonValueKind.String ->
            try
                use argsDoc = JsonDocument.Parse(args.GetString())
                skillFromObject argsDoc.RootElement
            with _ -> None
        | _ -> None)

/// A user.message that is a skill's own context injection (written immediately after skill.invoked)
/// rather than a genuine new request. Copilot CLI tags these with a `source` of `skill-<name>` and a
/// `<skill-context …>` content preamble; either marker identifies one, so the freshness scan treats
/// it as transparent and keeps looking back for the skill signal it belongs to.
let private isSkillContextMessage (data: JsonElement) =
    let sourceIsSkill =
        match data.TryGetProperty("source") with
        | true, s when s.ValueKind = JsonValueKind.String ->
            s.GetString().StartsWith("skill-", StringComparison.OrdinalIgnoreCase)
        | _ -> false

    let contentIsSkillContext =
        match data.TryGetProperty("content") with
        | true, c when c.ValueKind = JsonValueKind.String ->
            c.GetString().TrimStart().StartsWith("<skill-context", StringComparison.OrdinalIgnoreCase)
        | _ -> false

    sourceIsSkill || contentIsSkillContext

let private tryParseSkill (line: string) : SkillScan option =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp ->
            match typeProp.GetString() with
            | "skill.invoked" ->
                match root.TryGetProperty("data") with
                | true, data ->
                    match data.TryGetProperty("name") with
                    | true, n when n.ValueKind = JsonValueKind.String ->
                        let name = n.GetString()
                        if String.IsNullOrWhiteSpace(name) then None else Some(SkillRunning name)
                    | _ -> None
                | _ -> None
            | "assistant.message" ->
                match root.TryGetProperty("data") with
                | true, data ->
                    match data.TryGetProperty("toolRequests") with
                    | true, reqs when reqs.ValueKind = JsonValueKind.Array ->
                        reqs.EnumerateArray()
                        |> Seq.tryPick (fun req ->
                            let isSkillCall =
                                match req.TryGetProperty("name") with
                                | true, n -> n.GetString() = "skill"
                                | _ -> false
                            if isSkillCall then tryReadSkillArgument req |> Option.map SkillRunning else None)
                    | _ -> None
                | _ -> None
            | "user.message" ->
                // A genuine new request ends the prior skill's run; the skill's own context
                // injection does not (the scan steps past it, staying on the skill it belongs to).
                match root.TryGetProperty("data") with
                | true, data when isSkillContextMessage data -> None
                | _ -> Some RequestBoundary
            | _ -> None
        | _ -> None
    with ex ->
        Log.log "Copilot" $"Failed to parse skill event: {ex.Message}"
        None

let internal getCurrentSkillFromEventsFile (eventsPath: string) : string option =
    match FileUtils.scanBackward "Copilot" eventsPath tryParseSkill with
    | Some(SkillRunning name) -> Some name
    | Some RequestBoundary
    | None -> None

let getCurrentSkill (worktreePath: string) : string option =
    getSessionDirsForPath worktreePath
    |> findMostRecentEventsFile
    |> Option.bind (fun fi -> getCurrentSkillFromEventsFile fi.FullName)

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
