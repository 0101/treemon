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
                | "copilot" -> Some CopilotCli
                | other ->
                    Log.log "CodingTool" $"Unknown/unsupported codingTool value '{other}' in {configPath} — using the default"
                    None
            | false, _ -> None
        with ex ->
            Log.log "CodingTool" $"Failed to read .treemon.json: {ex.Message}"
            None

type CodingToolResult =
    { Status: CodingToolStatus
      Provider: CodingToolProvider option
      CurrentSkill: string option
      /// The agent's current intent (SDK `assistant.intent`) with the time it last changed — the card's
      /// live "what it's doing" line. Sourced from the same footer session as the other footer fields.
      AgentIntent: (string * DateTimeOffset) option
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

/// Wraps an arbitrary argument in a provider-aware skill invocation. The Copilot CLI uses the
/// natural-language "use {skill} skill with {arg}" form. Shared by actionPrompt (FixPr/FixBuild) and
/// the worktree-create auto-launch flow so both stay byte-identical. Provider-matched so a future
/// provider must supply its own form.
let skillInvocation (provider: CodingToolProvider option) (skill: string) (arg: string) =
    match provider |> Option.defaultValue CodingToolProvider.Default with
    | CopilotCli -> $"use {skill} skill with {arg}"

let actionPrompt (provider: CodingToolProvider option) (action: ActionKind) =
    match action with
    | FixPr url -> skillInvocation provider "pr" url
    | FixBuild url -> skillInvocation provider "fix-build" url
    | FixTests ->
        $"Please fix the failing tests. See the test failure report in {TestFailureLog.relPath} for details."
    | ConfigureTests -> configureTestsPrompt "the repo root"
    | CreatePr -> "Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"
    | CanvasSession prompt -> prompt

// Push-model live-state sourcing.
//
// The card's coding-tool fields come from the push model's live per-session state, not the
// log-parsing detectors. A worktree's live sessions are collapsed via `fromPushSessions`, which now
// makes TWO decoupled picks:
//   * the STATUS dot is driven by OPENNESS (only sessions still heartbeating count): open-active →
//     Working/WaitingForUser, open-but-idle → Idle (blue), no open session → NoSession (grey);
//   * the FOOTER (skill / last-user / last-assistant) comes from the active winner when one runs,
//     else the most-recent session of ANY status, so it survives Idle / NoSession.
// Resume is a THIRD, distinct pick (getLastSessionId): the most-recent session regardless of
// active/idle (the session the user last touched).

/// The blank grey card a worktree shows when it has NO push session at all (never reported, or its
/// rows pruned). The `fromPushSessions` collapse below reproduces this exact value for an empty
/// session list, and `WorktreeApi` falls back to it for a worktree absent from the collapse map.
/// A worktree with an OPEN-but-idle session collapses to blue `Idle` (not here), and one whose
/// sessions have all gone stale collapses to `NoSession` but KEEPS its retained footer.
let noSessionPushResult: CodingToolResult =
    { Status = NoSession
      Provider = None
      CurrentSkill = None
      AgentIntent = None
      LastUserMessage = None
      LastAssistantMessage = None
      LastMessageProvider = None
      LastActivity = None }

/// The last assistant message as the card's `CardEvent` (the exact shape the detectors produced): a
/// single line truncated to 80 chars, tagged with the push provider's source string.
let private toLastAssistantEvent (m: Message) : CardEvent =
    { Source = "copilot"
      Message = FileUtils.truncateMessage 80 m.Text
      Timestamp = m.At
      Status = None
      Duration = None }

/// Collapse a worktree's live push sessions into the card's coding-tool fields. Two DECOUPLED picks:
///
/// * **Status dot** — driven by OPENNESS. Only sessions seen within `openWindow` (a live CLI keeps
///   heartbeating, even while idle) count: among the open sessions `pickActive` picks the most-recent
///   ACTIVE winner (Working/WaitingForUser); open-but-all-idle collapses to `Idle` (blue); NO open
///   session collapses to `NoSession` (grey). `openWindow` (~3 min) is smaller than
///   `stalenessTimeout`, so a dead Working session drops out of openness (→ grey) before the crash-net
///   would rewrite it to Idle — it never lingers blue.
/// * **Footer** (skill / last user / last assistant) — DECOUPLED from the dot: the active winner when
///   one is running, otherwise the MOST-RECENT session of ANY status (the same pick
///   `getLastSessionId` uses for resume). Going Idle or losing the open session therefore does NOT
///   blank the footer: it stays populated while any session for the worktree remains in the store
///   (retention / `idleWindow`). This is the fix for the idle-only worktree whose footer/event-log
///   used to vanish because the old `pickActive`-only collapse dropped every Idle session.
let fromPushSessions (now: DateTimeOffset) (sessions: StoredStatus list) : CodingToolResult =
    // OPENNESS: only sessions seen within openWindow drive the status dot. A closed/crashed session's
    // last_seen goes stale and drops out here.
    let openSessions =
        sessions |> List.filter (fun s -> now - s.LastSeen < SessionActivity.openWindow)

    // Freshness crash-net (defensive): with openness applied first it rarely fires, but a
    // Working/WaitingForUser open session past the staleness timeout still reads as Idle.
    let adjustedOpen =
        openSessions
        |> List.map (fun s -> SessionActivity.freshnessAdjusted now s.LastSeen s.Status, s.LastSeen)

    let activeWinner = SessionActivity.pickActive adjustedOpen

    let status =
        match openSessions with
        | [] -> NoSession
        | _ ->
            activeWinner
            |> Option.map (fun w -> SessionActivity.toCodingToolStatus w.Status)
            |> Option.defaultValue Idle

    // Footer source: the active winner if running, else the most-recent session of ANY status so the
    // footer survives Idle / NoSession. Reads the raw fold state (idle sessions retain their last
    // messages + skill), NOT a freshness-adjusted one — freshness only rewrites the status dot.
    let footer =
        activeWinner
        |> Option.orElse (
            sessions
            |> List.sortByDescending _.LastSeen
            |> List.tryHead
            |> Option.map _.Status)

    // "Last did something" time: the newest active open last_seen when an active session is displayed,
    // else None (an idle/no-session worktree has no active surface).
    let lastActivity =
        if activeWinner.IsSome then
            adjustedOpen
            |> List.filter (fun (st, _) -> st.Status <> SessionLevelStatus.Idle)
            |> List.map snd
            |> List.max
            |> Some
        else
            None

    { Status = status
      // Single push provider today (Copilot CLI); a future provider threads its own value here.
      Provider = footer |> Option.map (fun _ -> CopilotCli)
      CurrentSkill = footer |> Option.bind _.Skill
      AgentIntent = footer |> Option.bind _.Intent |> Option.map (fun m -> FileUtils.truncateMessage 120 m.Text, m.At)
      LastUserMessage =
        footer
        |> Option.bind _.LastUserMessage
        |> Option.map (fun m -> FileUtils.truncateMessage 120 m.Text, m.At)
      LastAssistantMessage = footer |> Option.bind _.LastAssistantMessage |> Option.map toLastAssistantEvent
      LastMessageProvider = footer |> Option.bind _.LastAssistantMessage |> Option.map (fun _ -> CopilotCli)
      LastActivity = lastActivity }

/// Group a flat set of live push session-statuses by worktree path and collapse each group into the
/// card's coding-tool fields (the openness-driven status dot + the decoupled footer). Keyed by the
/// normalised worktree path stored on each session, so callers look it up by the (already-normalised)
/// `WorktreeInfo.Path`. The single place the push live state becomes card fields — both the worktree
/// assembly and the recent-messages endpoint read from the result.
let collapseByWorktree (now: DateTimeOffset) (sessions: StoredStatus seq) : Map<string, CodingToolResult> =
    sessions
    |> Seq.groupBy (_.WorktreePath >> WorktreePath.value)
    |> Seq.map (fun (path, group) -> path, fromPushSessions now (List.ofSeq group))
    |> Map.ofSeq

/// A durable-store fallback card for a worktree whose sessions have ALL aged out of the live idle
/// window (e.g. after a restart, last active >2h ago). The status dot stays `NoSession` (grey) — no
/// OPEN session exists — but the retained footer/resume metadata (skill, last user/assistant message,
/// provider) is surfaced from its most-recent stored session, so the footer still renders and the
/// resume button stays reachable (`CardViews.canResumeSession` requires a `LastUserMessage`). Without
/// this the durable `--resume <id>` path is UI-unreachable for exactly the sessions it was built for.
let retainedFooterResult (stored: StoredStatus) : CodingToolResult =
    let s = stored.Status
    let hasFooter = s.Skill.IsSome || s.LastUserMessage.IsSome || s.LastAssistantMessage.IsSome

    { Status = NoSession
      Provider = if hasFooter then Some CopilotCli else None
      CurrentSkill = s.Skill
      AgentIntent = s.Intent |> Option.map (fun m -> FileUtils.truncateMessage 120 m.Text, m.At)
      LastUserMessage = s.LastUserMessage |> Option.map (fun m -> FileUtils.truncateMessage 120 m.Text, m.At)
      LastAssistantMessage = s.LastAssistantMessage |> Option.map toLastAssistantEvent
      LastMessageProvider = s.LastAssistantMessage |> Option.map (fun _ -> CopilotCli)
      LastActivity = None }

/// Fill gaps in the live collapse with the durable retained fallback: a worktree present in `live`
/// keeps its live result (openness dot + footer); one absent from it (all its sessions aged out of the
/// idle window) takes the retained `NoSession`-with-footer card, so its footer and resume button
/// survive a restart. `retained` is keyed by worktree path (from `RetainedByWorktree`).
let withRetainedFallback (retained: Map<string, StoredStatus>) (live: Map<string, CodingToolResult>) : Map<string, CodingToolResult> =
    retained
    |> Map.fold
        (fun acc path stored ->
            if Map.containsKey path acc then acc
            else Map.add path (retainedFooterResult stored) acc)
        live

/// Resume pick — DISTINCT from the display (`pickActive`) pick: the most-recent session for the
/// worktree regardless of active/idle (the session the user last touched). Reads the id from the
/// push live state (the store's in-memory reflection) instead of scanning log directories. `None`
/// when the worktree has never reported (→ the CLI `--continue` fallback in CodingToolCli).
let getLastSessionId (sessions: StoredStatus list) : string option =
    sessions
    |> List.sortByDescending _.LastSeen
    |> List.tryHead
    |> Option.map (_.SessionId >> SessionId.value)

