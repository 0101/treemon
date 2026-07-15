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

let private graceWindow = TimeSpan.FromSeconds(15.0)
let private stalenessTimeout = TimeSpan.FromMinutes(30.0)
/// Past this age a session is Idle. Also the point at which its cache entry is prunable and — since it
/// can never read as anything but Idle — the point at which getRefreshData stops scanning it at all.
let private idleAgeCutoff = TimeSpan.FromHours(2.0)

/// The mtime freshness wrapper over a raw fold status: a just-finished turn still reads Working during
/// the grace window, a long-quiet Working goes Idle, and anything past the 2 h cutoff is Idle. Shared
/// by getStatusFromEventsFile (path-based, for tests) and getRefreshData (the worktree refresh path).
let private applyStatusFreshness (fileAge: TimeSpan) (rawStatus: CodingToolStatus) : CodingToolStatus =
    if fileAge > idleAgeCutoff then
        Idle
    else
        match rawStatus with
        | Done when fileAge < graceWindow -> Working
        | Working when fileAge > stalenessTimeout -> Idle
        | status -> status

// Copilot CLI has no explicit "skill finished" event. Skill/message/status detection classifies each
// event into a small algebra and folds it forward (see the "Forward fold" section below). The shared
// classifiers here — tryReadSkillArgument / isSkillContextMessage / assistantMessageEvent /
// skillInvokedName — are consumed by that forward fold, so a `skill.invoked` (data.name) or a `skill`
// tool-call (arguments.skill) sets the running skill, a `<skill-context …>` injection stays
// transparent, and an `ask_user` request/reply is distinguished from a genuine new request. The raw
// skill name is surfaced as-is; Shared.Activity.classify normalizes it.

/// One event's bearing on skill detection. Events with no bearing (turn_start/turn_end, tool/hook
/// events, the ask_user tool-execution rows, system messages) parse to None and are skipped.
type private SkillEvent =
    /// `skill.invoked` or a `skill` tool-call — the skill running on the current request.
    | SkillSignal of string
    /// An assistant.message whose toolRequests include `ask_user` (the request the user replies to).
    | AssistantAskUser
    /// Any other assistant.message (ordinary work — decisive when confirming a pending boundary).
    | AssistantWork
    /// A plain user.message: a genuine new request UNLESS it turns out to be an ask_user reply.
    | UserRequest

let private toolRequestName (req: JsonElement) : string option =
    match req.TryGetProperty("name") with
    | true, n when n.ValueKind = JsonValueKind.String -> Some(n.GetString())
    | _ -> None

/// Apply an extractor to a tool call's arguments, whether they are a nested object (real
/// events.jsonl encodes `arguments` as `{"skill":"..."}`) or a JSON string (the session-store schema
/// names it `arguments_json`). The extractor must return a value, not a `JsonElement`: a parsed
/// `arguments_json` document is disposed on return, so any element read from it would dangle.
let private withToolArguments (extract: JsonElement -> 'a option) (req: JsonElement) : 'a option =
    let argsElement =
        match req.TryGetProperty("arguments"), req.TryGetProperty("arguments_json") with
        | (true, a), _ -> Some a
        | _, (true, a) -> Some a
        | _ -> None

    argsElement
    |> Option.bind (fun args ->
        match args.ValueKind with
        | JsonValueKind.Object -> extract args
        | JsonValueKind.String ->
            try
                use argsDoc = JsonDocument.Parse(args.GetString())
                extract argsDoc.RootElement
            with _ -> None
        | _ -> None)

let private tryReadSkillArgument (req: JsonElement) =
    req
    |> withToolArguments (fun obj ->
        match obj.TryGetProperty("skill") with
        | true, s when s.ValueKind = JsonValueKind.String ->
            let name = s.GetString()
            if String.IsNullOrWhiteSpace(name) then None else Some name
        | _ -> None)

/// The agent id of a `task` tool call launched with `mode: "background"` (a fire-and-forget agent the
/// session keeps working alongside and is later woken about via a `system.notification`), or None for
/// any other tool call. A SYNC task agent blocks the turn, so only background launches leave work
/// outstanding once the turn ends — the signal that separates a "waiting on background agents"
/// (Working) session from a genuinely finished (Done) one.
let private backgroundAgentName (req: JsonElement) : string option =
    if toolRequestName req <> Some "task" then
        None
    else
        req
        |> withToolArguments (fun args ->
            let isBackground =
                match args.TryGetProperty("mode") with
                | true, m when m.ValueKind = JsonValueKind.String ->
                    String.Equals(m.GetString(), "background", StringComparison.OrdinalIgnoreCase)
                | _ -> false

            if not isBackground then
                None
            else
                match args.TryGetProperty("name") with
                | true, n when n.ValueKind = JsonValueKind.String ->
                    let s = n.GetString()
                    if String.IsNullOrWhiteSpace s then None else Some s
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
        let toolNamed (name: string) (req: JsonElement) = toolRequestName req = Some name

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

/// The background-agent ids launched by one assistant.message — its `task` tool calls with
/// `mode: "background"`. Empty when the message launches no background agents.
let private backgroundTaskLaunches (data: JsonElement) : string list =
    match data.TryGetProperty("toolRequests") with
    | true, reqs when reqs.ValueKind = JsonValueKind.Array ->
        reqs.EnumerateArray() |> Seq.choose backgroundAgentName |> Seq.toList
    | _ -> []

/// The agent id a `system.notification` reports as having gone idle (its background run finished),
/// read from the structured `data.kind` (`{ type = "agent_idle"; agentId = … }`) rather than the
/// prose content. None for any other notification kind.
let private agentIdleId (data: JsonElement) : string option =
    match data.TryGetProperty("kind") with
    | true, kind when kind.ValueKind = JsonValueKind.Object ->
        let isIdle =
            match kind.TryGetProperty("type") with
            | true, t when t.ValueKind = JsonValueKind.String -> t.GetString() = "agent_idle"
            | _ -> false

        if not isIdle then
            None
        else
            match kind.TryGetProperty("agentId") with
            | true, a when a.ValueKind = JsonValueKind.String ->
                let s = a.GetString()
                if String.IsNullOrWhiteSpace s then None else Some s
            | _ -> None
    | _ -> None

// --- Forward fold (oldest→newest) -------------------------------------------------------------
// events.jsonl is append-only, so the same skill/message/status determination the backward scans do
// can be expressed as a FORWARD fold that carries state and is fed each new line as the file grows —
// which is what an incremental, append-aware cache needs (wired up separately). The fold is pure; it
// reuses the very classifiers the backward path uses (skillInvokedName / assistantMessageEvent /
// isSkillContextMessage) so `CurrentSkill` stays equivalent to `scanSkill` on any session that has no
// sub-agent nesting. It ADDS two things the ~1 MB backward scans could not do:
//   * `SubagentDepth` gating — a `skill.invoked` emitted inside a subagent.started/…completed bracket
//     is the SUB-agent's, not the user's, and must not overwrite the top-level skill (see the
//     depth-gating note on foldForwardEvent below);
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
      SubagentDepth: int
      /// Background agents (`task`, `mode: "background"`) launched but not yet reported idle. While
      /// this is non-empty a turn that ends is still Working — the session is waiting on background
      /// work, not the user — so it keeps its red dot instead of decaying to a blue Done dot.
      RunningBackgroundAgents: Set<string> }

let internal emptySessionScan =
    { CurrentSkill = None
      LastUserMessage = None
      LastAssistantMessage = None
      RawStatus = Idle
      LastAssistantWasAskUser = false
      SubagentDepth = 0
      RunningBackgroundAgents = Set.empty }

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
    /// depth-0 only), its text content when non-empty, and any background agents it launched (`task`
    /// calls with `mode: "background"`, depth-0 only).
    | AssistantMessage of
        isAskUser: bool *
        skill: string option *
        content: (string * DateTimeOffset) option *
        backgroundLaunches: string list
    /// A `system.notification` reporting a launched background agent has gone idle (its run finished),
    /// carrying that agent's id — the signal that clears it from the outstanding set.
    | AgentIdle of string
    | TurnStarted
    | TurnEnded

let private readMessageTimestamp (root: JsonElement) : DateTimeOffset =
    JsonHelpers.tryTimestamp root |> Option.defaultValue DateTimeOffset.MinValue

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
                let d = data ()
                let content = d |> Option.bind (fun d -> readMessageContent d (readMessageTimestamp root))
                let kind = d |> Option.map assistantMessageEvent |> Option.defaultValue AssistantWork
                let launches = d |> Option.map backgroundTaskLaunches |> Option.defaultValue []
                match kind with
                | SkillSignal name -> Some(AssistantMessage(false, Some name, content, launches))
                | AssistantAskUser -> Some(AssistantMessage(true, None, content, launches))
                | AssistantWork
                | UserRequest -> Some(AssistantMessage(false, None, content, launches))
            | "system.notification" -> data () |> Option.bind agentIdleId |> Option.map AgentIdle
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
    // Sub-agent skill gating rests on one observed fact about the parent events.jsonl: a sub-agent's
    // own `skill.invoked` is written into the PARENT file, nested between the matching
    // `subagent.started`/`subagent.completed` (which share a toolCallId), and carries no depth/parent
    // marker of its own — the enclosing bracket is the only signal it belongs to a sub-agent. So
    // without depth tracking a newest-wins fold would let a deep sub-agent skill (megabytes in)
    // overwrite the top-level skill the user actually invoked. `SubagentDepth` counts the brackets and
    // `SkillInvoked` only sets CurrentSkill at depth 0. (Confirmed on a real 20.7 MB bd-execute
    // session: top-level `bd-execute` at ~85 KB; a sub-agent's `vs-local-development` at ~8.8 MB sat
    // inside a subagent bracket and would otherwise have won.)
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
    | AssistantMessage(isAskUser, skill, content, backgroundLaunches) ->
        let withSkill =
            match skill with
            | Some name when state.SubagentDepth = 0 -> { state with CurrentSkill = Some name }
            | _ -> state
        // Background launches inside a sub-agent bracket belong to the sub-agent, not the top-level
        // session, so only depth-0 launches count toward the outstanding set (mirrors skill gating).
        let running =
            if state.SubagentDepth = 0 then
                backgroundLaunches |> List.fold (fun acc name -> Set.add name acc) withSkill.RunningBackgroundAgents
            else
                withSkill.RunningBackgroundAgents
        { withSkill with
            LastAssistantMessage = (match content with Some c -> Some c | None -> withSkill.LastAssistantMessage)
            LastAssistantWasAskUser = isAskUser
            RunningBackgroundAgents = running
            RawStatus = (if isAskUser then WaitingForUser else Working) }
    | AgentIdle agentId ->
        // A launched background agent went idle (its run finished); drop it from the outstanding set.
        // A no-op when the id was never tracked (e.g. launched before the scan window).
        { state with RunningBackgroundAgents = Set.remove agentId state.RunningBackgroundAgents }
    | TurnStarted -> { state with RawStatus = Working }
    | TurnEnded ->
        // A finished turn is Done — UNLESS background agents are still running. The session launched
        // them and ended its turn to wait for their idle notifications, so it is genuinely Working
        // (red dot), not a blue Done dot, until they all report back (or the file goes stale).
        { state with RawStatus = (if state.RunningBackgroundAgents.IsEmpty then Done else Working) }

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
            || (now - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)) > idleAgeCutoff
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

/// Status for a single events file: the forward-fold RawStatus under the mtime grace/staleness
/// wrapper. Path-based (drives the CopilotDetector status unit tests); the worktree refresh path uses
/// getRefreshData. Reads the whole file, so a static file's final event is decisive even when it lacks
/// a trailing newline — unlike the incremental cache, which defers a partial, still-being-written line.
let internal getStatusFromEventsFile (eventsPath: string) (now: DateTimeOffset) =
    try
        let fi = FileInfo(eventsPath)
        if not fi.Exists then
            Idle
        else
            let fileAge = now - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
            if fileAge > idleAgeCutoff then
                Idle
            else
                (scanSessionEvents (File.ReadLines eventsPath)).RawStatus
                |> applyStatusFreshness fileAge
    with ex ->
        Log.log "Copilot" $"Failed to read status from {eventsPath}: {ex.Message}"
        Idle

/// Everything CodingToolStatus needs for a worktree's Copilot CLI surface, assembled from ONE
/// incremental session scan (replacing the four separate ~1 MB backward scans). Status carries the
/// mtime grace/staleness wrapper; the message fields are truncated for card display.
type CopilotRefreshData =
    { Status: CodingToolStatus
      Mtime: DateTimeOffset option
      CurrentSkill: string option
      LastUserMessage: (string * DateTimeOffset) option
      LastMessage: CardEvent option }

let private emptyRefreshData =
    { Status = Idle
      Mtime = None
      CurrentSkill = None
      LastUserMessage = None
      LastMessage = None }

/// Resolve the worktree's most-recent Copilot session, fold in whatever bytes have been appended since
/// the last refresh (O(new bytes) via the incremental cache), and derive every field from that single
/// scan. Replaces the previous four calls (getStatus / getCurrentSkill / getLastUserMessage /
/// getLastMessage), which each re-resolved the session and re-scanned ~1 MB independently.
let getRefreshData (worktreePath: string) : CopilotRefreshData =
    let now = DateTimeOffset.UtcNow

    if now - lastPrune.Value > prunePeriod then
        lastPrune.Value <- now
        pruneSessionScanCache now

    match getSessionDirsForPath worktreePath |> findMostRecentEventsFile with
    | None -> emptyRefreshData
    | Some fi ->
        let mtime = DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)

        // Past the Idle cutoff the file can only read as Idle, so skip the scan entirely. This also
        // stops the prune/rescan thrash the cache would otherwise cause: pruning evicts a stale entry
        // every 5 min, and without this guard the very next refresh would full-rescan the (possibly
        // 200 MB+) file just to discard the result as Idle.
        if now - mtime > idleAgeCutoff then
            { emptyRefreshData with Mtime = Some mtime }
        else

        match getSessionScanForFile fi.FullName with
        | None -> { emptyRefreshData with Mtime = Some mtime }
        | Some scan ->
            { Status = applyStatusFreshness (now - mtime) scan.RawStatus
              Mtime = Some mtime
              CurrentSkill = scan.CurrentSkill
              LastUserMessage =
                scan.LastUserMessage
                |> Option.map (fun (text, ts) -> FileUtils.truncateMessage 120 text, ts)
              LastMessage =
                scan.LastAssistantMessage
                |> Option.map (fun (text, ts) ->
                    { Source = "copilot"
                      Message = FileUtils.truncateMessage 80 text
                      Timestamp = ts
                      Status = None
                      Duration = None }) }

let internal parseCwd (yamlPath: string) = parseCwdFromYaml yamlPath
