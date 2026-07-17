module Server.SessionActivity

open System
open Shared

// The push-model status domain. The server owns this domain; the Copilot CLI extension is a thin
// forwarder that maps SDK events onto the wire contract, and the handler maps that onto SessionEvent.
// Everything here is pure — no IO, no mutation — so it can be unit-tested in isolation and folded
// incrementally (a later batch onto an earlier result == the whole stream at once).
//
// This is the SAME state machine as the old CopilotDetector.foldForwardEvent, MINUS the sub-agent
// depth gating and the <skill-context> injection handling. Those two sources of complexity are
// eliminated at the SOURCE (the extension drops any event carrying an agentId and any skill-context
// injection), so the server never sees them and the fold has no branch for them.

// --- Value types ------------------------------------------------------------------------------

type SessionId = SessionId of string

module SessionId =
    let value (SessionId id) = id

type EventId = EventId of string

module EventId =
    let value (EventId id) = id

/// A message's text plus the time it occurred. Raw (untruncated) text, exactly as pushed.
type Message = { Text: string; At: DateTimeOffset }

// --- Events -----------------------------------------------------------------------------------

/// The events that bear on status (plus the liveness-only `Heartbeat`). Anything else the extension
/// never sends, so the server has no "irrelevant event" branch to carry. These map 1:1 onto the wire
/// `kind` values (see the handler).
type SessionEvent =
    | TurnStarted
    /// A genuine user prompt (never a skill-context injection — those are dropped at the source).
    | UserPrompt of Message
    | AssistantMessage of Message
    | SkillInvoked of name: string
    /// ask_user — carries the question text to surface as the last assistant message.
    | AwaitingUserInput of question: Message option
    | TurnEnded
    | WentIdle
    /// A liveness-only heartbeat: re-asserts the CLI is still open WITHOUT bearing on status. Handled
    /// specially by the ingestion service — it only bumps the session's `last_seen` (openness), never
    /// folds into status and never appends to the event history. Timer-generated (no SDK event source).
    | Heartbeat

/// One pushed report: a single event for one session in one worktree.
type SessionActivityReport =
    { SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: CodingToolProvider
      EventId: EventId
      OccurredAt: DateTimeOffset
      Event: SessionEvent }

// --- Per-session fold -------------------------------------------------------------------------

/// A single push session's own status. `NoSession` is a *worktree-level* collapse result
/// (`CodingToolStatus`), never a per-session value — so it is intentionally absent here, making that
/// illegal state unrepresentable rather than guarding it with a runtime failwith. It is widened to the
/// four-case `CodingToolStatus` only at the worktree-collapse boundary (`CodingToolStatus.fromPushSessions`).
[<RequireQualifiedAccess>]
type SessionLevelStatus =
    | Working
    | WaitingForUser
    | Idle

/// Widen a per-session status to the card/worktree `CodingToolStatus`. Total — `NoSession` is only
/// ever produced by the collapse, never by the per-session fold.
let toCodingToolStatus =
    function
    | SessionLevelStatus.Working -> Working
    | SessionLevelStatus.WaitingForUser -> WaitingForUser
    | SessionLevelStatus.Idle -> Idle

/// The running per-session state produced by folding events oldest→newest. Deliberately smaller than
/// the old SessionScanCache: no SubagentDepth, no LastAssistantWasAskUser, no separate raw-status
/// type — the fold sets Status directly (incl. Idle from WentIdle).
type SessionStatus =
    { Status: SessionLevelStatus
      Skill: string option
      LastUserMessage: Message option
      LastAssistantMessage: Message option }

/// The starting state for a session with no events yet.
let emptyStatus =
    { Status = SessionLevelStatus.Idle
      Skill = None
      LastUserMessage = None
      LastAssistantMessage = None }

/// Pure, append-friendly fold. Folding a later batch onto an earlier result equals folding the whole
/// stream, which is what the durable-mirror + live-Map ingestion relies on.
let fold (s: SessionStatus) (e: SessionEvent) : SessionStatus =
    match e with
    | TurnStarted -> { s with Status = SessionLevelStatus.Working }
    | AssistantMessage m -> { s with Status = SessionLevelStatus.Working; LastAssistantMessage = Some m }
    | SkillInvoked name -> { s with Skill = Some name }
    | AwaitingUserInput q ->
        // The ask_user question is surfaced as the last assistant message; keep the prior one if the
        // question carries no text.
        { s with
            Status = SessionLevelStatus.WaitingForUser
            LastAssistantMessage = (q |> Option.orElse s.LastAssistantMessage) }
    | TurnEnded -> { s with Status = SessionLevelStatus.Idle }
    | WentIdle -> { s with Status = SessionLevelStatus.Idle }
    | Heartbeat -> s
    | UserPrompt m ->
        // A reply to an ask_user keeps the running skill; any other prompt is a new request that ends
        // the prior skill's run.
        let keepSkill = s.Status = SessionLevelStatus.WaitingForUser
        { s with
            Status = SessionLevelStatus.Working
            Skill = (if keepSkill then s.Skill else None)
            LastUserMessage = Some m }

/// Fold a batch of events (oldest→newest) onto an existing state.
let foldMany (initial: SessionStatus) (events: SessionEvent seq) : SessionStatus =
    Seq.fold fold initial events

// --- Freshness (crash safety-net) -------------------------------------------------------------

/// A session that dies without emitting `session.idle` (crash, closed laptop) is treated as Idle once
/// its `last_seen` is older than this. The extension heartbeats every ~30–120s, so this is a few
/// missed heartbeats — much faster than the old 30-min mtime staleness.
let stalenessTimeout = TimeSpan.FromMinutes 5.0

/// The idle window for `pickActive` display collapse and restart `loadLiveStatuses` (reuses the
/// existing idle cutoff): sessions quiet longer than this are not considered live.
let idleWindow = TimeSpan.FromHours 2.0

/// The OPENNESS window — how recently a session must have been seen to count as an "open" (live) CLI
/// for the worktree's status dot. The extension heartbeats every ~60s (HEARTBEAT_INTERVAL_MS),
/// re-asserting even an idle session, so an open CLI keeps `last_seen` fresh while a closed/crashed
/// one goes stale within a few missed beats. Deliberately a SMALL multiple of the heartbeat (~3
/// beats) — DISTINCT from the 2 h `idleWindow` (memory eviction / resume) and, per Decision 2,
/// SMALLER than `stalenessTimeout` (5 min): openness filters a dead Working session out (→ grey /
/// NoSession) before the crash-net would rewrite it to Idle, so a dead agent goes straight to grey
/// rather than lingering blue.
let openWindow = TimeSpan.FromMinutes 3.0

/// Crash net ONLY: a Working/WaitingForUser status whose `last_seen` is older than the staleness
/// timeout reads as Idle. `session.idle` (WentIdle) already sets Idle directly, so an explicitly-idle
/// session is unaffected. `last_seen` is the direct analogue of the old file mtime.
let freshnessAdjusted (now: DateTimeOffset) (lastSeen: DateTimeOffset) (s: SessionStatus) : SessionStatus =
    if s.Status <> SessionLevelStatus.Idle && now - lastSeen > stalenessTimeout then
        { s with Status = SessionLevelStatus.Idle }
    else
        s

// --- Idle-display debounce --------------------------------------------------------------------

/// How long a worktree must stay Idle before the dot is allowed to turn blue. Between turns of one
/// continuous task the agent emits `turn_ended` (→ Idle) then the next `turn_started` (→ Working)
/// with a ~1–2s gap; because the dashboard polls ~every second, a poll lands in that gap and
/// flickers the dot blue and back. Holding Working for this window swallows the inter-turn blink
/// while still surfacing a genuine idle a moment after it settles.
let idleDebounceWindow = TimeSpan.FromSeconds 10.0

/// Display-smoothing for the Working→Idle edge, applied on the card read path. Hold the DISPLAYED
/// status at Working until the worktree has been Idle for at least `graceWindow`, so a brief
/// inter-turn idle never surfaces as a blue flicker. Only the Idle edge is debounced —
/// Working / WaitingForUser / NoSession pass through unchanged (WaitingForUser can't flap to Idle:
/// the extension suppresses `went_idle` while a prompt is pending). `idleSince` is the frozen
/// "entered Idle" stamp (`CodingToolSinceByWorktree`), which the scheduler resets on every new
/// Working turn — so each turn restarts the window. With no stamp there is no reference instant, so
/// the real Idle status falls through. The classified activity (Reviewing/Investigating/…) is
/// unaffected: it is derived from the retained skill, so a held-Working worktree keeps its group.
let debounceIdle
    (graceWindow: TimeSpan)
    (now: DateTimeOffset)
    (idleSince: DateTimeOffset option)
    (status: CodingToolStatus)
    : CodingToolStatus =
    match status, idleSince with
    | Idle, Some since when now - since < graceWindow -> Working
    | _ -> status

// --- Multi-session collapse -------------------------------------------------------------------

/// Collapse a worktree's live sessions to one winning record: drop Idle, then the most-recent (by
/// `last_seen`) active session wins; all Idle → None. NOT raw latest-update — a session that just went
/// Idle must not hide an actively-Working sibling. Every displayed field (status, skill, last user,
/// last assistant) is read from that one record, so per-field cherry-picking is unrepresentable.
///
/// This is `CodingToolStatus.mostRecentActive` reused across a worktree's sessions rather than across
/// three detector surfaces. Callers freshness-adjust each session first (so stale ones read as Idle
/// and drop out here).
let pickActive (sessions: (SessionStatus * DateTimeOffset) list) : SessionStatus option =
    sessions
    |> List.filter (fun (s, _) -> s.Status <> SessionLevelStatus.Idle)
    |> List.sortByDescending snd
    |> List.tryHead
    |> Option.map fst
