module Server.SessionActivityService

open System
open System.Globalization
open System.Threading
open Giraffe
open Microsoft.AspNetCore.Http
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

// Ingestion for the push status model: the POST /api/session/activity endpoint plus the single
// mutable boundary behind it. A dedicated MailboxProcessor is the ONLY writer of live status and
// of the SQLite mirror, so status upserts never race. Per report it: folds the event onto that
// session's prior state → updates the in-memory Map → persists (append raw event + upsert status)
// → feeds RefreshScheduler (UpdateSessionStatus) so the card path can read it. The handler mirrors
// CanvasDocServer.canvasRegisterHandler (JSON DTO → domain, validate, known-worktree guard); the
// csrfGuard is composed in front of it in the route (Program.fs). The service owns its lifecycle:
// on Start it rebuilds live state from the store and arms a retention timer; on Dispose it stops
// the timer, the mailbox, and the store.

// --- Wire contract DTO ------------------------------------------------------------------------

// The single coupling point with extension.mjs (producer). The POST body is one report; `kind` is
// exactly one of the seven the fold consumes and maps 1:1 onto SessionEvent (no catch-all). An
// unknown `kind` is a validation error (rejected), never silently dropped. `message` is present for
// user_prompt / assistant_message / awaiting_user_input (the ask_user question); `skillName` only
// for skill_invoked; turn_started / turn_ended / went_idle carry neither.

[<CLIMutable>]
type MessageDto = { text: string; at: string }

[<CLIMutable>]
type SessionActivityRequest =
    { sessionId: string
      worktreePath: string
      provider: string
      eventId: string
      occurredAt: string
      kind: string
      message: MessageDto
      skillName: string }

// --- DTO → domain (pure, HTTP-free so it is unit-testable) -------------------------------------

let private parseProvider (s: string) : Result<PushProvider, string> =
    match s with
    | "copilot_cli" -> Ok CopilotCli
    | other -> Error $"unknown provider '{other}'"

let private tryParseTimestamp (s: string) : Result<DateTimeOffset, string> =
    match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
    | true, dto -> Ok dto
    | false, _ -> Error $"malformed timestamp '{s}'"

/// Server-side hard cap on any free-text field (message text, the ask_user question, skillName),
/// enforced independently of the client. extension.mjs caps at 2000 chars, but this loopback +
/// csrfGuard endpoint must not trust the producer's bound — a runaway/malicious multi-KB (or larger)
/// payload would otherwise be persisted to SQLite and held in memory verbatim. Set well above the
/// client's 2000-char cap so legitimate traffic is never touched; this is purely a defence-in-depth
/// storage/memory bound (downstream display truncation to 80/120 chars still happens in
/// CodingToolStatus). Truncate (not reject) so an over-long field never drops the whole event and
/// regresses the live fold.
let internal maxTextLength = 8192

/// Truncate a free-text field to the server-side cap (see maxTextLength). Null-safe: a null string
/// passes through unchanged (field presence is validated separately).
let internal capText (s: string) : string =
    if isNull s || s.Length <= maxTextLength then s else s.Substring(0, maxTextLength)

/// Clock-skew allowance for the producer's `occurredAt`. Minor skew between the reporting client's
/// clock and the server's is tolerated as-is; anything further ahead is implausible.
let internal futureSkewAllowance = TimeSpan.FromMinutes 5.0

/// Guard the freshness/staleness net against a future `occurredAt`. `last_seen` is set from
/// `occurredAt`, and `freshnessAdjusted` decays a session once `now - last_seen` exceeds the
/// staleness timeout — but a future `last_seen` makes that difference negative, so the session
/// reads perpetually fresh and never decays to Idle (stuck non-Idle). Clamp any timestamp beyond
/// `now + skew` down to `now` so decay always proceeds; timestamps at/before that bound pass through.
let internal clampFutureTimestamp (now: DateTimeOffset) (ts: DateTimeOffset) : DateTimeOffset =
    if ts > now + futureSkewAllowance then now else ts

/// A message DTO → domain Message: both text and a parseable timestamp are required (used for
/// user_prompt / assistant_message, where the message is mandatory).
let private parseMessage (dto: MessageDto) : Result<Message, string> =
    if obj.ReferenceEquals(dto, null) then Error "missing message"
    elif String.IsNullOrWhiteSpace dto.text then Error "missing message text"
    else tryParseTimestamp dto.at |> Result.map (fun at -> { Text = capText dto.text; At = at })

/// Map the wire `kind` (+ its optional message / skillName) onto a SessionEvent. The seven cases are
/// the whole contract; anything else is rejected. message is mandatory for user_prompt /
/// assistant_message, optional for awaiting_user_input (the ask_user question), absent otherwise.
let internal parseEvent (kind: string) (message: MessageDto) (skillName: string) : Result<SessionEvent, string> =
    match kind with
    | "turn_started" -> Ok TurnStarted
    | "turn_ended" -> Ok TurnEnded
    | "went_idle" -> Ok WentIdle
    | "user_prompt" -> parseMessage message |> Result.map UserPrompt
    | "assistant_message" -> parseMessage message |> Result.map AssistantMessage
    | "skill_invoked" ->
        if String.IsNullOrWhiteSpace skillName then Error "skill_invoked requires skillName"
        else Ok(SkillInvoked(capText skillName))
    | "awaiting_user_input" ->
        // The question text is optional; a blank/absent message just means "no question to surface".
        if obj.ReferenceEquals(message, null) || String.IsNullOrWhiteSpace message.text then
            Ok(AwaitingUserInput None)
        else
            tryParseTimestamp message.at
            |> Result.map (fun at -> AwaitingUserInput(Some { Text = capText message.text; At = at }))
    | other -> Error $"unknown kind '{other}'"

/// The inverse of parseEvent for persistence: the wire `kind` string stored on the raw event row.
let internal kindText =
    function
    | TurnStarted -> "turn_started"
    | UserPrompt _ -> "user_prompt"
    | AssistantMessage _ -> "assistant_message"
    | SkillInvoked _ -> "skill_invoked"
    | AwaitingUserInput _ -> "awaiting_user_input"
    | TurnEnded -> "turn_ended"
    | WentIdle -> "went_idle"

/// Validate a wire request and build the domain report, or return a human-readable reason. The
/// worktree path is normalised here so it matches the scheduler's known-path set, and `occurredAt`
/// is clamped against `now` so a future timestamp can't poison the freshness/staleness net. Pure
/// (given `now`), so the whole contract (7 kinds, unknown rejected, per-kind message/skill rules,
/// future-timestamp clamp) is unit-testable without HTTP plumbing.
let parseReport (now: DateTimeOffset) (req: SessionActivityRequest) : Result<SessionActivityReport, string> =
    if obj.ReferenceEquals(box req, null) then Error "missing body"
    elif String.IsNullOrWhiteSpace req.sessionId then Error "missing sessionId"
    elif String.IsNullOrWhiteSpace req.worktreePath then Error "missing worktreePath"
    elif String.IsNullOrWhiteSpace req.eventId then Error "missing eventId"
    elif String.IsNullOrWhiteSpace req.occurredAt then Error "missing occurredAt"
    elif String.IsNullOrWhiteSpace req.kind then Error "missing kind"
    else
        parseProvider req.provider
        |> Result.bind (fun provider ->
            tryParseTimestamp req.occurredAt
            |> Result.bind (fun occurredAt ->
                parseEvent req.kind req.message req.skillName
                |> Result.map (fun ev ->
                    { SessionId = SessionId req.sessionId
                      WorktreePath = WorktreePath(Server.PathUtils.normalizePath req.worktreePath)
                      Provider = provider
                      EventId = EventId req.eventId
                      OccurredAt = clampFutureTimestamp now occurredAt
                      Event = ev })))

// --- Known-worktree guard (mirrors CanvasDocServer) --------------------------------------------

let private allKnownPaths (agent: MailboxProcessor<RefreshScheduler.StateMsg>) = async {
    let! state = agent.PostAndAsyncReply RefreshScheduler.GetState
    return state.Repos |> Map.values |> Seq.collect _.KnownPaths |> Set.ofSeq
}

let private isKnownWorktree agent path = async {
    let! paths = allKnownPaths agent
    return paths |> Set.contains path
}

/// The decision for one incoming request, decoupled from HTTP so it is unit-testable exactly like
/// CanvasDocServer.attributeOwnership: a malformed/invalid body is rejected, a well-formed body for
/// an unmonitored worktree records nothing (soft accept), and a well-formed body for a monitored
/// worktree yields the domain report ready for the single writer.
type AcceptOutcome =
    | Accepted of SessionActivityReport   // validated + monitored — hand to the mailbox
    | Unmonitored of worktreePath: string // well-formed but unmonitored — nothing recorded
    | Rejected of reason: string          // invalid body — nothing recorded

/// Validate + guard a request without touching HTTP or the mailbox: parse the DTO to a domain
/// report, then apply the known-worktree guard against the scheduler's monitored paths. Returns the
/// outcome so the handler can map it to a response and tests can assert it directly.
let tryAcceptReport (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (req: SessionActivityRequest) : Async<AcceptOutcome> =
    async {
        match parseReport DateTimeOffset.UtcNow req with
        | Error reason -> return Rejected reason
        | Ok report ->
            let path = WorktreePath.value report.WorktreePath
            let! known = isKnownWorktree agent path
            return (if known then Accepted report else Unmonitored path)
    }

// --- Retention ---------------------------------------------------------------------------------

/// How long the append-only event stream (and any long-dead session rows) is kept before the
/// retention timer trims it. Well beyond the 2h idle window, so a live session is never pruned,
/// while still bounding the unbounded activity_events table. The Overview history reads events
/// within this window (see the overview-history unification task).
let internal retentionPeriod = TimeSpan.FromDays 14.0

/// How often the retention timer fires.
let internal pruneInterval = TimeSpan.FromHours 1.0

// --- Service -----------------------------------------------------------------------------------

type private ServiceMsg =
    | Ingest of SessionActivityReport
    | Seed of StoredStatus list
    | Snapshot of AsyncReplyChannel<Map<SessionId, StoredStatus>>

/// The SessionActivity ingestion service: the single-writer mailbox, the POST handler, and the
/// start/stop lifecycle (restart rebuild + retention timer). Construct with an instance-specific
/// store (its dbPath keyed to the server's port/data dir so a side-by-side validation instance
/// never collides) and the scheduler agent it feeds. Call Start once before serving; Dispose on
/// shutdown. Owns the store's lifetime — Dispose disposes it.
type SessionActivityService(store: SessionActivityStore, scheduler: MailboxProcessor<RefreshScheduler.StateMsg>) =

    // Apply one report on the single writer: fold → append (dedupe on event_id) → upsert
    // (last-write-wins) → feed the scheduler. Returns the new in-memory live map.
    let apply (live: Map<SessionId, StoredStatus>) (report: SessionActivityReport) : Map<SessionId, StoredStatus> =
        let prior = live |> Map.tryFind report.SessionId
        let priorStatus = prior |> Option.map _.Status |> Option.defaultValue emptyStatus
        let newStatus = SessionActivity.fold priorStatus report.Event

        let eventRow =
            { EventId = report.EventId
              SessionId = report.SessionId
              WorktreePath = report.WorktreePath
              Provider = report.Provider
              Kind = kindText report.Event
              Status = newStatus.Status
              Skill = newStatus.Skill
              Ts = report.OccurredAt }

        // Idempotency: a duplicate POST (same event_id) is a full no-op — nothing appended, no
        // upsert, no scheduler feed, live map unchanged.
        if not (store.AppendEvent eventRow) then
            live
        else
            let stored =
                { SessionId = report.SessionId
                  WorktreePath = report.WorktreePath
                  Provider = report.Provider
                  Status = newStatus
                  UpdatedAt = report.OccurredAt
                  LastSeen = report.OccurredAt }

            // Durable mirror is last-write-wins (a stale/out-of-order report is a no-op in the store).
            store.UpsertStatus stored

            // Mirror the same ordering guard in memory: an out-of-order (older) event is recorded in
            // the history substrate but must not regress the live fold state or the shown card.
            match prior with
            | Some p when report.OccurredAt < p.UpdatedAt -> live
            | _ ->
                scheduler.Post(RefreshScheduler.UpdateSessionStatus stored)
                live |> Map.add report.SessionId stored

    let mailbox =
        MailboxProcessor<ServiceMsg>.Start(fun inbox ->
            let rec loop (live: Map<SessionId, StoredStatus>) = async {
                let! msg = inbox.Receive()

                match msg with
                | Ingest report -> return! loop (apply live report)
                | Seed loaded ->
                    let seeded = loaded |> List.fold (fun m s -> Map.add s.SessionId s m) live
                    return! loop seeded
                | Snapshot reply ->
                    reply.Reply live
                    return! loop live
            }

            loop Map.empty)

    // Retention Timer lifecycle field: created lazily in Start() (after construction, once the store
    // is seeded) and released in Dispose(); the handle must survive between those two calls, so it
    // cannot be an immutable let.
    let mutable pruneTimer: Timer option = None

    let prune _ =
        try
            let deleted = store.PruneOld(DateTimeOffset.UtcNow - retentionPeriod)
            if deleted > 0 then Log.log "Activity" $"Retention: pruned {deleted} old activity row(s)"
        with ex ->
            Log.log "Activity" $"Retention prune failed: {ex.Message}"

    /// POST /api/session/activity. Mirrors canvasRegisterHandler: bind the JSON DTO, validate + map
    /// to the domain report, apply the known-worktree guard, then hand the report to the single
    /// writer. An unmonitored worktree is a soft 200 (recorded=false) exactly like canvas register,
    /// so the reporting extension's fan-out to a non-owning instance is harmless.
    member this.Handler: HttpHandler =
        fun next ctx -> task {
            try
                let! body = ctx.BindJsonAsync<SessionActivityRequest>()
                let! outcome = tryAcceptReport scheduler body |> Async.StartAsTask

                match outcome with
                | Rejected reason ->
                    Log.log "Activity" $"Rejected report: {reason}"
                    return! RequestErrors.BAD_REQUEST reason next ctx
                | Unmonitored path ->
                    Log.log "Activity" $"Report for unmonitored worktree — {path} (ignored)"
                    return! Successful.ok (json {| recorded = false; monitored = false |}) next ctx
                | Accepted report ->
                    this.Submit report
                    return! Successful.ok (json {| recorded = true; monitored = true |}) next ctx
            with ex ->
                Log.log "Activity" $"Report failed: malformed JSON — {ex.Message}"
                return! RequestErrors.BAD_REQUEST $"malformed JSON: {ex.Message}" next ctx
        }

    /// Hand an already-validated + monitored report to the single-writer mailbox. Used by the
    /// handler and by tests (which drive the fold/persist/feed path without HTTP plumbing).
    member internal _.Submit(report: SessionActivityReport) = mailbox.Post(Ingest report)

    /// Restart rebuild + retention timer. Loads every still-live session (last_seen within the idle
    /// window) from the store, feeds it to the scheduler and primes the in-memory fold map, so cards
    /// are correct before any new event arrives; then arms the retention timer.
    member _.Start() =
        let loaded = store.LoadLiveStatuses DateTimeOffset.UtcNow
        // Seed the scheduler in ONE batch so the time-since-idle chip is stamped from each worktree's
        // NEWEST session rather than the oldest-replayed row (LoadLiveStatuses is ordered oldest-first);
        // feeding rows individually would freeze a stale idle stamp and overstate the chip (F11/C-14).
        scheduler.Post(RefreshScheduler.SeedSessionStatuses loaded)
        mailbox.Post(Seed loaded)
        Log.log "Activity" $"Rebuilt {List.length loaded} live session status(es) from store"
        pruneTimer <- Some(new Timer(TimerCallback(prune), null, pruneInterval, pruneInterval))

    /// The current in-memory live map (test seam; live reads for cards go via the scheduler).
    member _.LiveSnapshot() : Map<SessionId, StoredStatus> = mailbox.PostAndReply Snapshot

    interface IDisposable with
        member _.Dispose() =
            pruneTimer |> Option.iter _.Dispose()
            (mailbox :> IDisposable).Dispose()
            (store :> IDisposable).Dispose()
