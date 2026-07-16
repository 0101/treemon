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
      LastMessageProvider: CodingToolProvider option
      /// Mtime of the active session surface that won status resolution (its last write) — the best
      /// available "the agent last did something" time, and the value the scheduler freezes as the
      /// state-transition timestamp. None when every surface is Idle.
      LastActivity: DateTimeOffset option }

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

/// The default a worktree card shows when it has no live/active push session (all Idle, all
/// stale, or none reported) — the same blank GREY card an unmonitored worktree shows. Until the
/// server-openness follow-up lights up live blue Idle, the push path yields Working | WaitingForUser
/// | NoSession, so a quiet worktree collapses here to NoSession (grey), matching today's blank card.
let noSessionPushResult: CodingToolResult =
    { Status = NoSession
      Provider = None
      CurrentSkill = None
      LastUserMessage = None
      LastAssistantMessage = None
      LastMessageProvider = None
      LastActivity = None }

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
/// freshness-adjusted first (a Working/WaitingForUser whose `last_seen` is older than the
/// staleness timeout reads as Idle — the crash safety-net — so it drops out of the pick), then
/// `pickActive` picks the most-recent ACTIVE winner and ALL displayed fields are read from that ONE
/// session. No live/active session → the NoSession default (blank grey fields).
let fromPushSessions (now: DateTimeOffset) (sessions: StoredStatus list) : CodingToolResult =
    let adjusted =
        sessions
        |> List.map (fun s -> SessionActivity.freshnessAdjusted now s.LastSeen s.Status, s.LastSeen)

    match SessionActivity.pickActive adjusted with
    | None -> noSessionPushResult
    | Some s ->
        // The winner is the most-recent ACTIVE session, so its last-seen (the newest among the
        // non-Idle sessions) is the card's "last did something" time.
        let lastActivity =
            adjusted
            |> List.filter (fun (st, _) -> st.Status <> Idle)
            |> List.map snd
            |> List.max
        { Status = s.Status
          Provider = Some(pushCardProvider CopilotCli)
          CurrentSkill = s.Skill
          LastUserMessage = s.LastUserMessage |> Option.map (fun m -> FileUtils.truncateMessage 120 m.Text, m.At)
          LastAssistantMessage = s.LastAssistantMessage |> Option.map toLastAssistantEvent
          LastMessageProvider = s.LastAssistantMessage |> Option.map (fun _ -> pushCardProvider CopilotCli)
          LastActivity = Some lastActivity }

/// Group a flat set of live push session-statuses by worktree path and collapse each group into the
/// card's coding-tool fields (the `pickActive` winner). Keyed by the normalised worktree path stored
/// on each session, so callers look it up by the (already-normalised) `WorktreeInfo.Path`. The single
/// place the push live state becomes card fields — both the worktree assembly and the recent-messages
/// endpoint read from the result.
let collapseByWorktree (now: DateTimeOffset) (sessions: StoredStatus seq) : Map<string, CodingToolResult> =
    sessions
    |> Seq.groupBy (_.WorktreePath >> WorktreePath.value)
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
    |> Option.map (_.SessionId >> SessionId.value)

