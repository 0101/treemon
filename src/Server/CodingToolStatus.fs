module Server.CodingToolStatus

open System
open System.IO
open System.Text.Json
open Shared
open Server.SessionActivity
open Server.SessionActivityStore


let internal readConfiguredProvider (worktreePath: string) : CodingToolProvider option =
    let configPath = Path.Combine(worktreePath, ".treemon.json")

    if not (File.Exists(configPath)) then
        None
    else
        try
            let json = File.ReadAllText(configPath)
            use doc = JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty("codingTool") with
            | true, elem ->
                match elem.GetString().ToLowerInvariant() with
                | "claude" -> Some Claude
                | "copilot" -> Some Copilot
                | other ->
                    Log.log "CodingTool" $"Unknown codingTool value '{other}' in {configPath}"
                    None
            | false, _ -> None
        with ex ->
            Log.log "CodingTool" $"Failed to read .treemon.json: {ex.Message}"
            None

type CodingToolResult =
    { Status: CodingToolStatus
      Provider: CodingToolProvider option
      CurrentSkill: string option
      LastUserMessage: (string * DateTimeOffset) option
      LastAssistantMessage: CardEvent option
      LastMessageProvider: CodingToolProvider option }

type internal ProviderResult =
    { Provider: CodingToolProvider
      Status: CodingToolStatus
      Mtime: DateTimeOffset option }

// Shared "active surface wins" rule: drop Idle sources, then the most recent (by mtime) wins.
// Both provider-status resolution and running-skill selection go through this so they can never
// disagree about which surface is active.
let private mostRecentActive
    (statusOf: 'a -> CodingToolStatus)
    (mtimeOf: 'a -> DateTimeOffset option)
    (items: 'a list)
    : 'a option =
    items
    |> List.filter (fun x -> statusOf x <> Idle)
    |> List.sortByDescending (fun x -> mtimeOf x |> Option.defaultValue DateTimeOffset.MinValue)
    |> List.tryHead

let internal pickActiveProvider (results: ProviderResult list) : ProviderResult option =
    results |> mostRecentActive _.Status _.Mtime

// The running skill must come from the SAME surface that wins the status resolution, not from
// whichever session file merely has the newest mtime. Mirrors pickActiveProvider (drop Idle, most
// recent wins), then scans only the winning surface for its skill via a lazy getter, so idle/losing
// surfaces are never read. All surfaces Idle -> None (-> Working via Activity.classify downstream).
let internal pickActiveSkill (surfaces: (ProviderResult * (unit -> string option)) list) : string option =
    surfaces
    |> mostRecentActive (fun (r, _) -> r.Status) (fun (r, _) -> r.Mtime)
    |> Option.bind (fun (_, getSkill) -> getSkill ())

// Uses pickActiveProvider (filter non-Idle, pick most recent) instead of tryFind
// so that when multiple providers report status, the most recently active wins.
let internal resolveStatus
    (configuredProvider: CodingToolProvider option)
    (providerResults: ProviderResult list)
    : CodingToolStatus * CodingToolProvider option =

    match configuredProvider with
    | Some provider ->
        let matching = providerResults |> List.filter (fun r -> r.Provider = provider)

        match pickActiveProvider matching with
        | Some r -> r.Status, Some provider
        | None -> Idle, Some provider
    | None ->
        match pickActiveProvider providerResults with
        | Some r -> r.Status, Some r.Provider
        | None -> Idle, None

// The three session surfaces scanned per refresh: Claude, Copilot CLI, and VS Code Copilot. Kept as
// named results so status resolution and running-skill selection reuse the SAME status/mtime reads.
type internal SessionResults =
    { Claude: ProviderResult
      CopilotCli: ProviderResult
      VsCodeCopilot: ProviderResult }

let private getClaudeResult (files: (FileInfo * ClaudeDetector.SessionFileKind) list) =
    { Provider = Claude
      Status = ClaudeDetector.getStatusFromEnumeratedFiles files
      Mtime = ClaudeDetector.getSessionMtimeFromFiles files }

let private gatherResultsFromFiles (worktreePath: string) (claudeFiles: (FileInfo * ClaudeDetector.SessionFileKind) list) (copilot: CopilotDetector.CopilotRefreshData) : SessionResults =
    { Claude = getClaudeResult claudeFiles
      CopilotCli =
        { Provider = Copilot
          Status = copilot.Status
          Mtime = copilot.Mtime }
      VsCodeCopilot =
        { Provider = Copilot
          Status = VsCodeCopilotDetector.getStatus worktreePath
          Mtime = VsCodeCopilotDetector.getSessionMtime worktreePath } }

let getRefreshData (worktreePath: string) : CodingToolResult =
    let configured = readConfiguredProvider worktreePath
    let claudeFiles = ClaudeDetector.enumerateFiles worktreePath
    // ONE incremental Copilot CLI session scan per refresh; every Copilot field below is derived from
    // it, instead of the four separate ~1 MB backward scans (status / skill / last user / last message).
    let copilot = CopilotDetector.getRefreshData worktreePath
    let results = gatherResultsFromFiles worktreePath claudeFiles copilot

    let status, provider =
        resolveStatus configured [ results.Claude; results.CopilotCli; results.VsCodeCopilot ]

    let target = configured |> Option.orElse provider

    let lastUserMsg, lastMsgProvider =
        match target with
        | Some Claude ->
            let msg = ClaudeDetector.getLastUserMessageFromFiles claudeFiles
            msg, msg |> Option.map (fun _ -> Claude)
        | Some Copilot ->
            let cliMsg = copilot.LastUserMessage
            let vsCodeMsg = VsCodeCopilotDetector.getLastUserMessage worktreePath

            let msg =
                [ cliMsg; vsCodeMsg ]
                |> List.choose id
                |> List.sortByDescending snd
                |> List.tryHead
            msg, msg |> Option.map (fun _ -> Copilot)
        | None ->
            let winner =
                [ ClaudeDetector.getLastUserMessageFromFiles claudeFiles |> Option.map (fun m -> Claude, m)
                  copilot.LastUserMessage |> Option.map (fun m -> Copilot, m)
                  VsCodeCopilotDetector.getLastUserMessage worktreePath |> Option.map (fun m -> Copilot, m) ]
                |> List.choose id
                |> List.sortByDescending (fun (_, (_, ts)) -> ts)
                |> List.tryHead
            winner |> Option.map snd, winner |> Option.map fst

    let lastAssistantMsg =
        match target with
        | Some Claude ->
            ClaudeDetector.getLastMessageFromFiles claudeFiles
        | Some Copilot ->
            [ copilot.LastMessage
              VsCodeCopilotDetector.getLastMessage worktreePath ]
            |> List.choose id
            |> List.sortByDescending _.Timestamp
            |> List.tryHead
        | None ->
            [ ClaudeDetector.getLastMessageFromFiles claudeFiles
              copilot.LastMessage
              VsCodeCopilotDetector.getLastMessage worktreePath ]
            |> List.choose id
            |> List.sortByDescending _.Timestamp
            |> List.tryHead

    let currentSkill =
        match target with
        | Some Claude -> ClaudeDetector.getCurrentSkillFromFiles claudeFiles
        | Some Copilot ->
            pickActiveSkill
                [ results.CopilotCli, (fun () -> copilot.CurrentSkill)
                  results.VsCodeCopilot, (fun () -> VsCodeCopilotDetector.getCurrentSkill worktreePath) ]
        | None -> None

    { Status = status; Provider = provider; CurrentSkill = currentSkill; LastUserMessage = lastUserMsg; LastAssistantMessage = lastAssistantMsg; LastMessageProvider = lastMsgProvider }

let configureTestsPrompt (repoRoot: string) =
    "Look at this project and determine the appropriate test command to run (e.g. 'dotnet test', 'npm test', 'pytest', etc). "
    + $"Then create or update .treemon.json at '{repoRoot}' with a \"testCommand\" field set to the full test command string. "
    + $"IMPORTANT: The config file MUST be at '{repoRoot}\\.treemon.json', not in the current directory. "
    + "For example: {\"testCommand\": \"dotnet test src/Tests/Tests.fsproj\"}"

/// Wraps an arbitrary argument in a provider-aware skill invocation.
/// Copilot uses the natural-language "use {skill} skill with {arg}" form;
/// Claude uses the "/{skill} {arg}" slash-command form. Shared by actionPrompt
/// (FixPr/FixBuild) and the worktree-create auto-launch flow so both stay byte-identical.
let skillInvocation (provider: CodingToolProvider option) (skill: string) (arg: string) =
    match provider |> Option.defaultValue CodingToolProvider.Default with
    | Copilot -> $"use {skill} skill with {arg}"
    | Claude -> $"/{skill} {arg}"

let actionPrompt (provider: CodingToolProvider option) (action: ActionKind) =
    match action with
    | FixPr url -> skillInvocation provider "pr" url
    | FixBuild url -> skillInvocation provider "fix-build" url
    | FixTests ->
        $"Please fix the failing tests. See the test failure report in {TestFailureLog.relPath} for details."
    | ConfigureTests -> configureTestsPrompt "the repo root"
    | CreatePr -> "Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"
    | CanvasSession prompt -> prompt

// --- Push-model live-state sourcing -----------------------------------------------------------
//
// The card's coding-tool fields (status / skill / last-user / last-assistant) come from the push
// model's live per-session state, not the log-parsing detectors. A worktree's live sessions are
// collapsed to ONE winner via SessionActivity.pickActive (drop Idle, most-recent active wins) and
// EVERY displayed field is read from that one winning session — per-field cherry-picking across
// sessions is unrepresentable. Resume is a DISTINCT pick (getLastSessionId): the most-recent
// session regardless of active/idle (the session the user last touched).

/// The Idle default a worktree card shows when it has no live/active push session (all Idle, all
/// stale, or none reported) — the same blank card an unmonitored worktree shows.
let idlePushResult: CodingToolResult =
    { Status = Idle
      Provider = None
      CurrentSkill = None
      LastUserMessage = None
      LastAssistantMessage = None
      LastMessageProvider = None }

/// The push model has a single provider today (Copilot CLI); `pickActive` collapses to a bare
/// `SessionStatus` (provider-free), so an active push session always reads as Copilot on the card.
let private pushCardProvider (p: PushProvider) : CodingToolProvider =
    match p with
    | CopilotCli -> Copilot

/// The last assistant message as the card's `CardEvent` (the exact shape the detectors produced): a
/// single line truncated to 80 chars, tagged with the push provider's source string.
let private toLastAssistantEvent (m: Message) : CardEvent =
    { Source = "copilot"
      Message = FileUtils.truncateMessage 80 m.Text
      Timestamp = m.At
      Status = None
      Duration = None }

/// Collapse a worktree's live push sessions into the card's coding-tool fields. Each session is
/// freshness-adjusted first (a Working/WaitingForUser/Done whose `last_seen` is older than the
/// staleness timeout reads as Idle — the crash safety-net — so it drops out of the pick), then
/// `pickActive` picks the most-recent ACTIVE winner and ALL displayed fields are read from that ONE
/// session. No live/active session → the Idle default (blank fields).
let fromPushSessions (now: DateTimeOffset) (sessions: StoredStatus list) : CodingToolResult =
    let winner =
        sessions
        |> List.map (fun s -> SessionActivity.freshnessAdjusted now s.LastSeen s.Status, s.LastSeen)
        |> SessionActivity.pickActive

    match winner with
    | None -> idlePushResult
    | Some s ->
        { Status = s.Status
          Provider = Some(pushCardProvider CopilotCli)
          CurrentSkill = s.Skill
          LastUserMessage = s.LastUserMessage |> Option.map (fun m -> FileUtils.truncateMessage 120 m.Text, m.At)
          LastAssistantMessage = s.LastAssistantMessage |> Option.map toLastAssistantEvent
          LastMessageProvider = s.LastAssistantMessage |> Option.map (fun _ -> pushCardProvider CopilotCli) }

/// Group a flat set of live push session-statuses by worktree path and collapse each group into the
/// card's coding-tool fields (the `pickActive` winner). Keyed by the normalised worktree path stored
/// on each session, so callers look it up by the (already-normalised) `WorktreeInfo.Path`. The single
/// place the push live state becomes card fields — both the worktree assembly and the recent-messages
/// endpoint read from the result.
let collapseByWorktree (now: DateTimeOffset) (sessions: StoredStatus seq) : Map<string, CodingToolResult> =
    sessions
    |> Seq.groupBy (fun s -> WorktreePath.value s.WorktreePath)
    |> Seq.map (fun (path, group) -> path, fromPushSessions now (List.ofSeq group))
    |> Map.ofSeq

/// Resume pick — DISTINCT from the display (`pickActive`) pick: the most-recent session for the
/// worktree regardless of active/idle (the session the user last touched). Reads the id from the
/// push live state (the store's in-memory reflection) instead of scanning log directories. `None`
/// when the worktree has never reported (→ the CLI `--continue` fallback in CodingToolCli).
let getLastSessionId (sessions: StoredStatus list) : string option =
    sessions
    |> List.sortByDescending _.LastSeen
    |> List.tryHead
    |> Option.map (fun s -> SessionId.value s.SessionId)

