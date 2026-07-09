module Server.CopilotDetector

open System
open System.Collections.Concurrent
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

// The running skill rides a backward events scan with *freshness*: Copilot CLI has no explicit
// "skill finished" event, so a skill counts as running-now only while its invocation is the most
// recent thing the agent did on the CURRENT request. Scanning backward from EOF, the first decisive
// event wins:
//   * a `skill.invoked` event (data.name), or the `skill` tool-call that requested it
//     (arguments.skill) -> that skill is running now; or
//   * a genuine `user.message` (a new top-level or scheduled request) -> the prior skill's run is
//     over, so report nothing. This is what stops a finished skill from lingering across turns
//     (the v1.1 (i) correction) — a bd-plan that finished before the user moved on no longer shows.
// Two `user.message` shapes are NOT request boundaries and stay transparent:
//   * a skill's own context injection (Copilot tags it `source: "skill-<name>"` AND a
//     `<skill-context …>` content preamble, written right after skill.invoked) — part of the skill
//     *starting* (see isSkillContextMessage); and
//   * an `ask_user` reply — the agent asked a question mid-skill (an assistant.message requesting
//     the `ask_user` tool → WaitingForUser), the user answered, and the SAME skill resumes on the
//     next turn. A per-line pick cannot tell an ask_user reply from a new request (both are a plain
//     `source:""` user.message), so the scan is *stateful*: a candidate boundary is only confirmed
//     once the first assistant.message older than it is seen — if that older assistant.message was
//     the outstanding ask_user request, the candidate was its reply and is discarded (the skill is
//     still running). This needs consecutive events, so it reads the whole bounded tail rather than
//     riding scanBackward's overlapping (boundary-line-duplicating) chunks.
// Because skill.invoked is written after its tool-call, recency prefers skill.invoked and falls back
// to the tool-call for a skill that is still starting. The raw name is surfaced; Shared.Activity
// .classify normalizes it.

/// One event's bearing on skill freshness, scanning newest→oldest. Events with no bearing
/// (turn_start/turn_end, tool/hook events, the ask_user tool-execution rows, system messages) parse
/// to None and are skipped.
type private SkillEvent =
    /// `skill.invoked` or a `skill` tool-call — the skill running on the current request.
    | SkillSignal of string
    /// An assistant.message whose toolRequests include `ask_user` (the request the user replies to).
    | AssistantAskUser
    /// Any other assistant.message (ordinary work — decisive when confirming a pending boundary).
    | AssistantWork
    /// A plain user.message: a genuine new request UNLESS it turns out to be an ask_user reply.
    | UserRequest

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
/// rather than a genuine new request. Copilot CLI tags these with BOTH a `source` of `skill-<name>`
/// and a `<skill-context …>` content preamble. Both markers are required (focused-review F4): the
/// `source` alone is system-controlled and trustworthy, but a normal user message could legitimately
/// begin with the literal text "<skill-context", so requiring the content check without the
/// system-set source would let such a message masquerade as an injection, skip the request boundary,
/// and resurrect a stale/finished skill. Requiring both keeps the real injection transparent while a
/// user-typed lookalike stays a genuine boundary.
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

    sourceIsSkill && contentIsSkillContext

let private assistantMessageEvent (data: JsonElement) : SkillEvent =
    match data.TryGetProperty("toolRequests") with
    | true, reqs when reqs.ValueKind = JsonValueKind.Array ->
        // A single assistant.message can carry several tool calls; a `skill` call wins (the skill is
        // starting), otherwise an `ask_user` call marks the request the user will reply to.
        let toolNamed (name: string) (req: JsonElement) =
            match req.TryGetProperty("name") with
            | true, n when n.ValueKind = JsonValueKind.String -> n.GetString() = name
            | _ -> false

        let skill =
            reqs.EnumerateArray()
            |> Seq.tryPick (fun req -> if toolNamed "skill" req then tryReadSkillArgument req else None)

        match skill with
        | Some name -> SkillSignal name
        | None ->
            let hasAskUser = reqs.EnumerateArray() |> Seq.exists (toolNamed "ask_user")
            if hasAskUser then AssistantAskUser else AssistantWork
    | _ -> AssistantWork

/// The skill name carried by a `skill.invoked` event's `data.name` (blank/absent → None). Shared by
/// the backward `classifySkillEvent` and the forward `classifyForwardEvent` so both read it identically.
let private skillInvokedName (data: JsonElement) : string option =
    match data.TryGetProperty("name") with
    | true, n when n.ValueKind = JsonValueKind.String ->
        let name = n.GetString()
        if String.IsNullOrWhiteSpace(name) then None else Some name
    | _ -> None

let private classifySkillEvent (line: string) : SkillEvent option =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp ->
            match typeProp.GetString() with
            | "skill.invoked" ->
                match root.TryGetProperty("data") with
                | true, data -> skillInvokedName data |> Option.map SkillSignal
                | _ -> None
            | "assistant.message" ->
                match root.TryGetProperty("data") with
                | true, data -> Some(assistantMessageEvent data)
                | _ -> Some AssistantWork
            | "user.message" ->
                // A skill's own context injection is transparent (part of the skill starting); every
                // other user.message is a candidate request boundary — the stateful scan below then
                // decides whether it is a genuine new request or an ask_user reply.
                match root.TryGetProperty("data") with
                | true, data when isSkillContextMessage data -> None
                | _ -> Some UserRequest
            | _ -> None
        | _ -> None
    with ex ->
        Log.log "Copilot" $"Failed to parse skill event: {ex.Message}"
        None

/// Where the newest→oldest scan stands: normally searching for the skill signal, or holding a
/// candidate request boundary until the next older classified event resolves it. The candidate is
/// discarded (skill still running) only if that older event is the outstanding ask_user request;
/// any other older event — ordinary assistant work, a skill signal, or another user request —
/// confirms it as a genuine boundary.
type private ScanState =
    | Searching
    | PendingBoundary

let private scanSkill (events: SkillEvent seq) : string option =
    // Pulls events lazily so classifySkillEvent's JSON parse stops as soon as a decision is reached
    // (the skill signal usually sits within the newest handful of events); the loop is tail-recursive.
    use e = events.GetEnumerator()

    let rec loop state =
        if not (e.MoveNext()) then
            None // reached the tail horizon / file start without a live skill signal
        else
            match state, e.Current with
            | Searching, SkillSignal name -> Some name
            | Searching, UserRequest -> loop PendingBoundary
            | Searching, (AssistantAskUser | AssistantWork) -> loop Searching
            // The candidate boundary is an ask_user reply iff the assistant.message just before it
            // requested ask_user; then the same skill resumes, so drop the boundary and keep looking.
            | PendingBoundary, AssistantAskUser -> loop Searching
            // Any other decisive event older than the candidate confirms it as a genuine boundary.
            | PendingBoundary, (SkillSignal _ | AssistantWork | UserRequest) -> None

    loop Searching

let internal getCurrentSkillFromEventsFile (eventsPath: string) : string option =
    // Bounded to a ~1 MB tail (matching scanBackward's horizon); a skill whose start scrolled past it
    // degrades to None → Working, an accepted graceful degradation.
    FileUtils.readTailLines "Copilot" eventsPath (1024 * 1024)
    |> List.rev // newest → oldest
    |> Seq.choose classifySkillEvent // lazy: parsed on demand, stops when scanSkill decides
    |> scanSkill

let getCurrentSkill (worktreePath: string) : string option =
    getSessionDirsForPath worktreePath
    |> findMostRecentEventsFile
    |> Option.bind (fun fi -> getCurrentSkillFromEventsFile fi.FullName)

// --- Forward fold (oldest→newest) -------------------------------------------------------------
// events.jsonl is append-only, so the same skill/message/status determination the backward scans do
// can be expressed as a FORWARD fold that carries state and is fed each new line as the file grows —
// which is what an incremental, append-aware cache needs (wired up separately). The fold is pure; it
// reuses the very classifiers the backward path uses (skillInvokedName / assistantMessageEvent /
// isSkillContextMessage) so `CurrentSkill` stays equivalent to `scanSkill` on any session that has no
// sub-agent nesting. It ADDS two things the ~1 MB backward scans could not do:
//   * `SubagentDepth` gating — a `skill.invoked` emitted inside a subagent.started/…completed bracket
//     is the SUB-agent's, not the user's, and must not overwrite the top-level skill (see spec Step 0);
//   * a genuine-only `LastUserMessage` — a `<skill-context>` injection is never recorded as the last
//     user message (it stays transparent, exactly as it is for skill detection).
// Because the fold sees the WHOLE stream (not a tail window), a skill.invoked megabytes back is still
// detected.

/// The running per-session state produced by folding events oldest→newest. This is the "cache" the
/// incremental scanner keeps per session (byte-offset bookkeeping is layered on separately).
type SessionScanCache =
    { CurrentSkill: string option
      /// Genuine user prompts only — never a `<skill-context>` injection. Raw (untruncated) text.
      LastUserMessage: (string * DateTimeOffset) option
      /// Last non-empty assistant message. Raw (untruncated) text.
      LastAssistantMessage: (string * DateTimeOffset) option
      /// Status implied by the newest decisive event, before the mtime grace/staleness wrapper.
      RawStatus: CodingToolStatus
      /// Was the most recent assistant.message an `ask_user` request? Distinguishes a user reply
      /// (skill resumes) from a genuine new request (skill's run is over).
      LastAssistantWasAskUser: bool
      SubagentDepth: int }

let internal emptySessionScan =
    { CurrentSkill = None
      LastUserMessage = None
      LastAssistantMessage = None
      RawStatus = Idle
      LastAssistantWasAskUser = false
      SubagentDepth = 0 }

/// One event's bearing on the forward fold. Reuses the same classification as the backward
/// `classifySkillEvent`, and adds the events that scan ignored but the fold needs: sub-agent brackets,
/// turn boundaries, and the message text/timestamp recorded for LastUserMessage / LastAssistantMessage.
type private ForwardEvent =
    /// subagent.started — a sub-agent's events (incl. its own skill.invoked) follow until its
    /// matching subagent.completed; depth gating stops them from being read as the parent's.
    | SubagentStarted
    | SubagentCompleted
    /// A `skill.invoked` event — sets the running skill (only at depth 0). Status is unchanged, as the
    /// backward status scan also ignores skill.invoked.
    | SkillInvoked of string
    /// A genuine (non-injection) user.message — a request boundary; content is None when the message
    /// carries no text (still a boundary, just nothing to record as LastUserMessage).
    | GenuineUserMessage of (string * DateTimeOffset) option
    /// A `<skill-context>` injection user.message — transparent to skill/LastUserMessage, but still
    /// marks the agent as Working like any user.message (matches the backward status scan).
    | InjectionUserMessage
    /// An assistant.message: whether it requested `ask_user`, the skill it started (a `skill` tool-call,
    /// depth-0 only), and its text content when non-empty.
    | AssistantMessage of isAskUser: bool * skill: string option * content: (string * DateTimeOffset) option
    | TurnStarted
    | TurnEnded

let private readMessageTimestamp (root: JsonElement) : DateTimeOffset =
    match root.TryGetProperty("timestamp") with
    | true, ts when ts.ValueKind = JsonValueKind.String ->
        match DateTimeOffset.TryParse(ts.GetString()) with
        | true, v -> v
        | _ -> DateTimeOffset.MinValue
    | _ -> DateTimeOffset.MinValue

/// A message's text + timestamp, or None when the content is absent/blank (matching the backward
/// tryParse*Content helpers, which skip empty-content messages).
let private readMessageContent (data: JsonElement) (ts: DateTimeOffset) : (string * DateTimeOffset) option =
    match data.TryGetProperty("content") with
    | true, c when c.ValueKind = JsonValueKind.String ->
        let text = c.GetString()
        if String.IsNullOrWhiteSpace(text) then None else Some(text, ts)
    | _ -> None

let private classifyForwardEvent (line: string) : ForwardEvent option =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("type") with
        | true, typeProp ->
            let data () =
                match root.TryGetProperty("data") with
                | true, d -> Some d
                | _ -> None

            match typeProp.GetString() with
            | "subagent.started" -> Some SubagentStarted
            | "subagent.completed" -> Some SubagentCompleted
            | "assistant.turn_start" -> Some TurnStarted
            | "assistant.turn_end" -> Some TurnEnded
            | "skill.invoked" -> data () |> Option.bind skillInvokedName |> Option.map SkillInvoked
            | "assistant.message" ->
                let content = data () |> Option.bind (fun d -> readMessageContent d (readMessageTimestamp root))
                let kind = data () |> Option.map assistantMessageEvent |> Option.defaultValue AssistantWork
                match kind with
                | SkillSignal name -> Some(AssistantMessage(false, Some name, content))
                | AssistantAskUser -> Some(AssistantMessage(true, None, content))
                | AssistantWork
                | UserRequest -> Some(AssistantMessage(false, None, content))
            | "user.message" ->
                match data () with
                | Some d when isSkillContextMessage d -> Some InjectionUserMessage
                | Some d -> Some(GenuineUserMessage(readMessageContent d (readMessageTimestamp root)))
                // No `data` → not an injection → a genuine (content-less) boundary, matching the
                // backward classifySkillEvent's `| _ -> Some UserRequest` fall-through.
                | None -> Some(GenuineUserMessage None)
            | _ -> None
        | _ -> None
    with ex ->
        Log.log "Copilot" $"Failed to classify forward event: {ex.Message}"
        None

let private foldForwardEvent (state: SessionScanCache) (ev: ForwardEvent) : SessionScanCache =
    match ev with
    | SubagentStarted -> { state with SubagentDepth = state.SubagentDepth + 1 }
    | SubagentCompleted -> { state with SubagentDepth = max 0 (state.SubagentDepth - 1) }
    | SkillInvoked name ->
        // Gate on depth 0: a sub-agent's skill.invoked must not overwrite the user's top-level skill.
        if state.SubagentDepth = 0 then
            { state with CurrentSkill = Some name; LastAssistantWasAskUser = false }
        else
            state
    | InjectionUserMessage ->
        // Transparent to skill + LastUserMessage; still Working like any user.message.
        { state with RawStatus = Working }
    | GenuineUserMessage content ->
        // An ask_user reply resumes the same skill; any other genuine request ends the prior skill.
        let skill = if state.LastAssistantWasAskUser then state.CurrentSkill else None
        { state with
            CurrentSkill = skill
            LastUserMessage = (match content with Some c -> Some c | None -> state.LastUserMessage)
            LastAssistantWasAskUser = false
            RawStatus = Working }
    | AssistantMessage(isAskUser, skill, content) ->
        let withSkill =
            match skill with
            | Some name when state.SubagentDepth = 0 -> { state with CurrentSkill = Some name }
            | _ -> state
        { withSkill with
            LastAssistantMessage = (match content with Some c -> Some c | None -> withSkill.LastAssistantMessage)
            LastAssistantWasAskUser = isAskUser
            RawStatus = (if isAskUser then WaitingForUser else Working) }
    | TurnStarted -> { state with RawStatus = Working }
    | TurnEnded -> { state with RawStatus = Done }

/// Fold event lines (oldest→newest) onto an existing state. Unknown/irrelevant lines are skipped.
/// Pure and append-friendly: folding a later batch onto the state from an earlier batch yields the
/// same result as folding the whole stream at once, which is what the incremental cache relies on.
let internal foldSessionEvents (initial: SessionScanCache) (lines: string seq) : SessionScanCache =
    lines
    |> Seq.choose classifyForwardEvent
    |> Seq.fold foldForwardEvent initial

/// Full forward scan of a complete event stream from the empty state.
let internal scanSessionEvents (lines: string seq) : SessionScanCache =
    foldSessionEvents emptySessionScan lines

// --- Incremental per-session cache ------------------------------------------------------------
// Because the forward fold is append-friendly, we cache each session's fold state alongside the byte
// offset consumed so far and, on each query, fold only the bytes appended since — O(new bytes) rather
// than re-reading up to a 200 MB file every refresh. The ConcurrentDictionary is the sole mutable
// boundary (mirroring the workspaceIndex ref+Dictionary pattern); the fold stays pure. Re-reading
// appended bytes is idempotent, so a racing double-read is harmless (last-write-wins).

type private SessionScanEntry =
    { State: SessionScanCache
      /// Absolute byte offset of the end of the last complete (newline-terminated) line folded.
      /// A partial trailing line beyond this offset stays unconsumed until its newline arrives.
      Length: int64 }

let private sessionScanCache =
    ConcurrentDictionary<string, SessionScanEntry>(StringComparer.OrdinalIgnoreCase)

/// Fold the bytes appended to a single events file since its cached offset, or full-rescan from zero
/// when there is no cache or the file shrank (rotation/truncation makes the cached offset meaningless).
let internal getSessionScanForFile (eventsPath: string) : SessionScanCache option =
    try
        let fi = FileInfo(eventsPath)
        if not fi.Exists then None
        else
            let fileLength = fi.Length
            // Reuse the cached state only while the file has not shrunk (append-only); otherwise the
            // offsets no longer line up (rotation) so fold the whole file from the empty state.
            let baseState, startOffset =
                match sessionScanCache.TryGetValue(eventsPath) with
                | true, entry when entry.Length <= fileLength -> entry.State, entry.Length
                | _ -> emptySessionScan, 0L

            let lines, newLength =
                FileUtils.readByteRangeLines "Copilot" eventsPath startOffset fileLength

            let newState = foldSessionEvents baseState lines
            sessionScanCache[eventsPath] <- { State = newState; Length = newLength }
            Some newState
    with ex ->
        Log.log "Copilot" $"Failed incremental session scan of {eventsPath}: {ex.Message}"
        None

/// Drop cache entries whose events file has vanished or gone idle (mtime older than the 2 h Idle
/// cutoff) so the dictionary only tracks live sessions.
let internal pruneSessionScanCache (now: DateTimeOffset) =
    sessionScanCache.Keys
    |> Seq.toList
    |> List.filter (fun path ->
        try
            let fi = FileInfo(path)
            not fi.Exists
            || (now - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)) > TimeSpan.FromHours(2.0)
        with _ -> true)
    |> List.iter (fun path -> sessionScanCache.TryRemove(path) |> ignore)

/// The cached byte offset for an events file, if any — exposed for tests to assert the incremental
/// offset bookkeeping (partial-line handling, truncation resets, pruning).
let internal peekSessionScanCacheLength (eventsPath: string) : int64 option =
    match sessionScanCache.TryGetValue(eventsPath) with
    | true, entry -> Some entry.Length
    | false, _ -> None

let private prunePeriod = TimeSpan.FromMinutes(5.0)
let private lastPrune = ref DateTimeOffset.MinValue

/// The forward-fold state for a worktree's most-recent Copilot session, updated incrementally from the
/// events file so CurrentSkill / LastUserMessage / LastAssistantMessage / RawStatus are correct at any
/// session size. Replaces the four separate ~1 MB backward scans (wired up by a later task).
let getSessionScan (worktreePath: string) : SessionScanCache option =
    let now = DateTimeOffset.UtcNow
    if now - lastPrune.Value > prunePeriod then
        lastPrune.Value <- now
        pruneSessionScanCache now

    getSessionDirsForPath worktreePath
    |> findMostRecentEventsFile
    |> Option.bind (fun fi -> getSessionScanForFile fi.FullName)

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
