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
      /// One SessionDot per open (live) session, ordered Working→Waiting→Idle then by stable session
      /// id — the per-session status donuts, each carrying its own context usage. Empty ⇔ Status =
      /// NoSession.
      SessionStatuses: SessionDot list
      Provider: CodingToolProvider option
      CurrentSkill: string option
      /// The freshest source-tagged activity value from the same footer session as the other fields.
      AgentActivity: AgentActivity option
      LastUserMessage: UserFooterMessage option
      LastAssistantMessage: (string * DateTimeOffset) option
      /// `LastSeen` of the active session that won status resolution. None when every session is Idle.
      LastActivity: DateTimeOffset option }

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
    | CreatePr -> "Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"
    | CanvasSession prompt -> prompt

// Push-model live-state sourcing.
//
// The card's coding-tool fields come from the push model's live per-session state, not the
// log-parsing detectors. A worktree's live sessions are collapsed via `fromPushSessions`, which now
// makes TWO decoupled picks:
//   * the STATUS dot is driven by OPENNESS (only sessions still heartbeating count): open-active →
//     Working/WaitingForUser, open-but-idle → Idle (blue), no open session → NoSession (grey);
//   * the FOOTER (activity / skill / last-user / last-assistant) comes from the active winner when
//     one runs, else the session with the most-recent activity, so it survives Idle / NoSession.
// Resume is a THIRD, distinct durable-store scalar pick: the most-recently-active session regardless
// of active/idle (the session the user last touched).

/// The blank grey card a worktree shows when it has NO push session at all (never reported, or its
/// rows pruned). The `fromPushSessions` collapse below reproduces this exact value for an empty
/// session list, and `WorktreeApi` falls back to it for a worktree absent from the collapse map.
/// A worktree with an OPEN-but-idle session collapses to blue `Idle` (not here), and one whose
/// sessions have all gone stale collapses to `NoSession` but KEEPS its retained footer.
let noSessionPushResult: CodingToolResult =
    { Status = NoSession
      SessionStatuses = []
      Provider = None
      CurrentSkill = None
      AgentActivity = None
      LastUserMessage = None
      LastAssistantMessage = None
      LastActivity = None }

let private toFooterMessage maxLength (message: Message) =
    FileUtils.truncateMessage maxLength message.Text, message.At

let private tryFormatActivityMessage (message: Message) =
    match UserMessageFormatting.classify message.Text with
    | UserMessageFormatting.UserMessageClassification.SystemReminder -> None
    | UserMessageFormatting.UserMessageClassification.Display(_, displayText) ->
        Some { message with Text = FileUtils.truncateMessage 120 displayText }

let private effectiveDisplayActivity (status: SessionStatus) =
    { status with
        Intent = status.Intent |> Option.bind tryFormatActivityMessage
        Title = status.Title |> Option.bind tryFormatActivityMessage }
    |> SessionActivity.effectiveActivity

let private toUserFooterMessage (message: Message) =
    match UserMessageFormatting.classify message.Text with
    | UserMessageFormatting.UserMessageClassification.SystemReminder -> None
    | UserMessageFormatting.UserMessageClassification.Display(glyph, text) ->
        Some
            { Glyph = glyph
              Text = FileUtils.truncateMessage 120 text
              Timestamp = message.At }

/// Collapse a worktree's live push sessions into the card's coding-tool fields. Two DECOUPLED picks:
///
/// * **Status dot** — driven by OPENNESS. Only sessions seen within `openWindow` (a live CLI keeps
///   heartbeating, even while idle) count: among the open sessions `pickActive` picks the most-recent
///   ACTIVE winner (Working/WaitingForUser); open-but-all-idle collapses to `Idle` (blue); NO open
///   session collapses to `NoSession` (grey). `openWindow` (~3 min) is smaller than
///   `stalenessTimeout`, so a dead Working session drops out of openness (→ grey) before the crash-net
///   would rewrite it to Idle — it never lingers blue.
/// * **Footer** (activity / skill / last user / last assistant) — DECOUPLED from the dot: the active
///   winner when one is running, otherwise the session with the MOST-RECENT ACTIVITY of ANY status
///   (the same activity ordering the durable resume query uses). Going Idle or losing the open
///   session does NOT blank the footer: it stays populated while any session for the worktree remains
///   in the store (retention / `idleWindow`).
/// Render order for the per-session dots: Working first, then WaitingForUser, then Idle. NoSession is
/// never a per-session status (it is the worktree-level collapse of an empty session set).
let private sessionStatusOrder =
    function
    | Working -> 0
    | WaitingForUser -> 1
    | Idle -> 2
    | NoSession -> 3

type private SessionSelection =
    { OpenSessions: StoredStatus list
      AdjustedOpen: StoredStatus list
      ActiveWinner: StoredStatus option
      Footer: StoredStatus option }

let private selectSessions (now: DateTimeOffset) (sessions: StoredStatus list) =
    let openSessions =
        sessions |> List.filter (fun s -> now - s.LastSeen < SessionActivity.openWindow)

    let adjustedOpen =
        openSessions
        |> List.map (fun s ->
            { s with Status = SessionActivity.freshnessAdjusted now s.LastSeen s.Status })

    let activeWinner =
        adjustedOpen
        |> SessionActivity.pickActive _.Status StoredStatus.activityOrderKey

    { OpenSessions = openSessions
      AdjustedOpen = adjustedOpen
      ActiveWinner = activeWinner
      Footer =
        activeWinner
        |> Option.orElse (sessions |> StoredStatus.tryMostRecentActivity) }

let internal representativeActivityText now sessions =
    (selectSessions now sessions).Footer
    |> Option.map _.Status
    |> Option.bind effectiveDisplayActivity
    |> Option.map (AgentActivity.textAndTimestamp >> fst >> _.Trim())

let fromPushSessions (now: DateTimeOffset) (sessions: StoredStatus list) : CodingToolResult =
    let selection = selectSessions now sessions

    let status =
        match selection.OpenSessions with
        | [] -> NoSession
        | _ ->
            selection.ActiveWinner
            |> Option.map (fun winner ->
                winner.Status
                |> SessionActivity.effectiveStatus
                |> SessionActivity.toCodingToolStatus)
            |> Option.defaultValue Idle

    // Per-session dots: every open session's freshness-adjusted status paired with its own running
    // skill and context usage, ordered Working→Waiting→Idle then by session id. Each session keeps
    // its OWN skill + ContextUsage — no footer collapse — so the Overview band can classify each
    // session's activity independently and a session that has reported usage renders a donut
    // regardless of which session currently wins status. Empty ⇔ status = NoSession, so the client
    // reproduces the single grey dot from an empty list.
    let sessionStatuses =
        selection.AdjustedOpen
        |> List.map (fun s ->
            { Status =
                s.Status
                |> SessionActivity.effectiveStatus
                |> SessionActivity.toCodingToolStatus
              Skill = s.Status.Skill
              ContextUsage = s.Status.ContextUsage },
            SessionId.value s.SessionId)
        |> List.sortBy (fun (dot, sessionId) -> sessionStatusOrder dot.Status, sessionId)
        |> List.map fst

    // Footer source: the active winner if running, else the most-recently-active session of ANY
    // status so the footer survives Idle / NoSession. Reads the raw fold state (idle sessions retain
    // their last messages + skill), NOT a freshness-adjusted one — freshness only rewrites the dot.
    let footer = selection.Footer |> Option.map _.Status

    { Status = status
      SessionStatuses = sessionStatuses
      // Single push provider today (Copilot CLI); a future provider threads its own value here.
      Provider = footer |> Option.map (fun _ -> CopilotCli)
      CurrentSkill = footer |> Option.bind _.Skill
      AgentActivity =
        footer
        |> Option.bind effectiveDisplayActivity
      LastUserMessage =
        footer
        |> Option.bind _.LastUserMessage
        |> Option.bind toUserFooterMessage
      LastAssistantMessage =
        footer
        |> Option.bind _.LastAssistantMessage
        |> Option.map (toFooterMessage 80)
      LastActivity = selection.ActiveWinner |> Option.map _.LastSeen }

/// Add each worktree's durable representative to the live candidate set. Live rows win duplicate
/// session ids; retained rows with distinct ids remain available for footer and auto-sync fallback
/// selection, while their own `LastSeen` still independently determines whether they contribute an
/// open status dot.
let includeRetainedSessions (retained: Map<string, StoredStatus>) (live: StoredStatus seq) : StoredStatus seq =
    let addSession (sessions: Map<SessionId, StoredStatus>) (session: StoredStatus) =
        Map.add session.SessionId session sessions
    let retainedBySession = retained |> Map.values |> Seq.fold addSession Map.empty
    live
    |> Seq.fold addSession retainedBySession
    |> Map.toSeq
    |> Seq.map snd

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
