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
// the timer and drains the mailbox. Program owns and disposes the shared store.

// --- Wire contract DTO ------------------------------------------------------------------------

// The single coupling point with extension.mjs (producer). The POST body accepts a closed set of
// lifecycle/content kinds, the state-only title bootstrap and usage gauge, plus the liveness-only
// heartbeat. Unknown kinds are rejected. `message` is present for user_prompt / assistant_message /
// intent_reported / title_reported / title_bootstrap and optionally awaiting_user_input;
// `skillName` is only for skill_invoked, and usage counters only for usage_info.

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
      skillName: string
      currentTokens: int
      tokenLimit: int }

// --- DTO → domain (pure, HTTP-free so it is unit-testable) -------------------------------------

let private parseProvider (s: string) : Result<CodingToolProvider, string> =
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

/// Map the wire `kind` (+ its optional message / skillName / usage counters) onto a SessionEvent. The
/// The lifecycle/fold kinds plus state-only bootstrap/gauge and liveness heartbeat are the whole
/// contract. message is mandatory for user_prompt / assistant_message / intent_reported /
/// title_reported / title_bootstrap, optional for awaiting_user_input, and absent otherwise.
let internal parseEvent (kind: string) (message: MessageDto) (skillName: string) (currentTokens: int) (tokenLimit: int) : Result<SessionEvent, string> =
    match kind with
    | "turn_started" -> Ok TurnStarted
    | "turn_ended" -> Ok TurnEnded
    | "went_idle" -> Ok WentIdle
    | "heartbeat" -> Ok Heartbeat
    | "user_prompt" -> parseMessage message |> Result.map UserPrompt
    | "assistant_message" -> parseMessage message |> Result.map AssistantMessage
    | "intent_reported" -> parseMessage message |> Result.map IntentReported
    | "title_reported" -> parseMessage message |> Result.map TitleReported
    | "title_bootstrap" -> parseMessage message |> Result.map TitleBootstrap
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
    | "usage_info" ->
        // A pure gauge. A non-positive limit is degenerate (no meaningful fraction), so reject it
        // rather than store a divide-by-zero snapshot; a negative current is clamped to 0.
        if tokenLimit <= 0 then Error "usage_info requires tokenLimit > 0"
        else Ok(UsageInfo(max 0 currentTokens, tokenLimit))
    | other -> Error $"unknown kind '{other}'"

/// The inverse of parseEvent for persistence: the wire `kind` string stored on the raw event row.
let internal kindText =
    function
    | TurnStarted -> "turn_started"
    | UserPrompt _ -> "user_prompt"
    | AssistantMessage _ -> "assistant_message"
    | IntentReported _ -> "intent_reported"
    | TitleReported _ -> "title_reported"
    | TitleBootstrap _ -> "title_bootstrap"
    | SkillInvoked _ -> "skill_invoked"
    | AwaitingUserInput _ -> "awaiting_user_input"
    | TurnEnded -> "turn_ended"
    | WentIdle -> "went_idle"
    | Heartbeat -> "heartbeat"
    | UsageInfo _ -> "usage_info"

let private withMessageTimestamp at =
    function
    | UserPrompt message -> UserPrompt { message with At = at }
    | AssistantMessage message -> AssistantMessage { message with At = at }
    | IntentReported message -> IntentReported { message with At = at }
    | TitleReported message -> TitleReported { message with At = at }
    | TitleBootstrap message -> TitleBootstrap { message with At = at }
    | AwaitingUserInput (Some message) -> AwaitingUserInput(Some { message with At = at })
    | event -> event

/// Validate a wire request and build the domain report, or return a human-readable reason. The
/// worktree path is normalised here so it matches the scheduler's known-path set, and `occurredAt`
/// is clamped against `now` so a future timestamp can't poison freshness or activity ordering.
/// Message-bearing events use that same normalized timestamp. Pure (given `now`), so the whole
/// contract (12 kinds, unknown rejected, per-kind payload rules, future-timestamp clamp) is
/// unit-testable without HTTP plumbing.
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
            |> Result.bind (fun rawOccurredAt ->
                let occurredAt = clampFutureTimestamp now rawOccurredAt
                parseEvent req.kind req.message req.skillName req.currentTokens req.tokenLimit
                |> Result.map (fun ev ->
                    { SessionId = SessionId req.sessionId
                      WorktreePath = WorktreePath(Server.PathUtils.normalizePath req.worktreePath)
                      Provider = provider
                      EventId = EventId req.eventId
                      OccurredAt = occurredAt
                      Event = withMessageTimestamp occurredAt ev })))

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

/// How long the append-only streams (activity_events + task_snapshots) and any long-dead session rows
/// are kept before the retention timer trims them. Well beyond the 2h idle window, so a live session is
/// never pruned, while still bounding the unbounded tables. Comfortably covers the 72h Overview-history
/// window that reads these streams; adjustable as we learn how fast the tables grow.
let internal retentionPeriod = TimeSpan.FromDays 60.0

/// How often the retention timer fires.
let internal pruneInterval = TimeSpan.FromHours 1.0

// --- Service -----------------------------------------------------------------------------------

type private ServiceMsg =
    | Ingest of SessionActivityReport
    | Seed of StoredStatus list
    | Snapshot of AsyncReplyChannel<Map<SessionId, StoredStatus>>
    | Stop of AsyncReplyChannel<unit>

/// The SessionActivity ingestion service: the single-writer mailbox, the POST handler, and the
/// start/stop lifecycle (restart rebuild + retention timer). Construct with an instance-specific
/// store (its dbPath keyed to the server's port/data dir so a side-by-side validation instance
/// never collides) and the scheduler agent it feeds. Call Start once before serving; Dispose on
/// shutdown. The store is borrowed from Program; Dispose stops only this service.
type SessionActivityService(store: SessionActivityStore, scheduler: MailboxProcessor<RefreshScheduler.StateMsg>) =

    let dispositionGate = obj ()
    // Service disposal is a lifetime boundary: mark it once so no work can be queued behind Stop.
    let mutable disposed = false

    let ensureActive () =
        lock dispositionGate (fun () ->
            if disposed then
                raise (ObjectDisposedException(nameof SessionActivityService)))

    // Apply one report on the single writer. State-only reports have independent persistence/order
    // paths; lifecycle events fold → append (dedupe on event_id) → upsert (last-write-wins) → feed
    // the scheduler. Returns the new in-memory live map.
    let apply (live: Map<SessionId, StoredStatus>) (report: SessionActivityReport) : Map<SessionId, StoredStatus> =
        let foldReportState () =
            let prior =
                live
                |> Map.tryFind report.SessionId
                |> Option.orElseWith (fun () -> store.StatusBySession report.SessionId)
            let status =
                prior
                |> Option.map _.Status
                |> Option.defaultValue emptyStatus
                |> fun current -> SessionActivity.fold current report.Event
            let stored =
                match prior with
                | Some existing ->
                    { existing with
                        Status = status
                        LastSeen = max existing.LastSeen report.OccurredAt }
                | None ->
                    { SessionId = report.SessionId
                      WorktreePath = report.WorktreePath
                      Provider = report.Provider
                      Status = status
                      UpdatedAt = DateTimeOffset.MinValue
                      LastSeen = report.OccurredAt
                      ContextUsageAt = None }
            status, stored

        match report.Event with
        | Heartbeat ->
            // Liveness-only: bump the session's last_seen (the openness signal that keeps an idle-but-open
            // CLI blue) WITHOUT moving updated_at, re-folding status, or appending a history row. Keeping
            // it off the ordering/append path is what stops a heartbeat from overtaking a slightly-earlier
            // real event and dropping it (F20), and from inflating activity_events with synthetic rows
            // (F14). After a restart, an older but still-open session can be absent from the idle-window
            // live rebuild; lazily rehydrate its durable status when its next heartbeat proves it is open.
            // A heartbeat with no prior durable event is still ignored.
            let prior =
                live
                |> Map.tryFind report.SessionId
                |> Option.orElseWith (fun () -> store.StatusBySession report.SessionId)

            match prior with
            | None -> live
            | Some prior ->
                let bumped = { prior with LastSeen = max prior.LastSeen report.OccurredAt }
                store.RecordLiveness(report.SessionId, bumped.LastSeen)
                scheduler.Post(RefreshScheduler.UpdateSessionStatus bumped)
                live |> Map.add report.SessionId bumped
        | UsageInfo(currentTokens, tokenLimit) ->
            // A pure context-window gauge on its OWN order path, DECOUPLED from the status
            // last-write-wins clock (UpdatedAt). Sharing that clock let a usage report's timestamp
            // block a slightly-earlier status transition (turn stuck Working), and in the reverse
            // arrival order let the status out-of-order guard discard the usage snapshot. So, like a
            // heartbeat, it never moves UpdatedAt and never appends a history row; it is ordered only
            // against prior usage via its own ContextUsageAt clock. It needs a live session to attach
            // to — a usage report for a session with no prior status is dropped (nothing to gauge).
            match live |> Map.tryFind report.SessionId with
            | None -> live
            | Some prior ->
                // Usage LWW: a snapshot older than the one already held is ignored, so an out-of-order
                // (delayed) older gauge can never clobber a fresher reading.
                let isStaleUsage =
                    match prior.ContextUsageAt with
                    | Some at -> report.OccurredAt < at
                    | None -> false

                if isStaleUsage then
                    live
                else
                    let usage = { CurrentTokens = currentTokens; TokenLimit = tokenLimit }
                    let bumped =
                        { prior with
                            Status.ContextUsage = Some usage
                            ContextUsageAt = Some report.OccurredAt
                            LastSeen = max prior.LastSeen report.OccurredAt }
                    store.RecordLiveness(report.SessionId, bumped.LastSeen)
                    scheduler.Post(RefreshScheduler.UpdateSessionStatus bumped)
                    live |> Map.add report.SessionId bumped
        | TitleBootstrap _ ->
            // Metadata hydration is current state, not a source event. Persist the title without
            // appending history or advancing the lifecycle UpdatedAt clock. A bootstrap that arrives
            // before delayed replay creates an Idle shell with the minimum ordering timestamp, so
            // every real SDK event can still fold onto it; its join timestamp seeds LastSeen only
            // until a real event/heartbeat takes over.
            let _, stored = foldReportState ()
            store.UpsertStatus stored
            scheduler.Post(RefreshScheduler.UpdateSessionStatus stored)
            live |> Map.add report.SessionId stored
        | IntentReported _
        | TitleReported _ ->
            // Activity fields are source events, but they are ordered independently from lifecycle
            // status. Preserve UpdatedAt so a fire-and-forget report cannot block an older lifecycle
            // transition or be discarded merely because a newer lifecycle event arrived first.
            let status, stored = foldReportState ()
            let eventRow =
                { EventId = report.EventId
                  SessionId = report.SessionId
                  WorktreePath = report.WorktreePath
                  Provider = report.Provider
                  Kind = kindText report.Event
                  Status = status.Status
                  Skill = status.Skill
                  Ts = report.OccurredAt }

            if not (store.AppendAndUpsert(eventRow, stored)) then
                live
            else
                scheduler.Post(RefreshScheduler.UpdateSessionStatus stored)
                live |> Map.add report.SessionId stored
        | _ ->
            let prior = live |> Map.tryFind report.SessionId
            let priorStatus = prior |> Option.map _.Status |> Option.defaultValue emptyStatus
            let newStatus = SessionActivity.fold priorStatus report.Event

            let isOutOfOrder =
                match prior with
                | Some p -> report.OccurredAt < p.UpdatedAt
                | None -> false

            // The history row records the fold state AFTER this event. For an out-of-order (older) event
            // that must be the event's OWN direct effect (fold onto empty), never the current newest live
            // status — which never held at this event's point in history (F19). In-order events fold onto
            // the running state as usual.
            let rowState = if isOutOfOrder then SessionActivity.fold emptyStatus report.Event else newStatus

            let eventRow =
                { EventId = report.EventId
                  SessionId = report.SessionId
                  WorktreePath = report.WorktreePath
                  Provider = report.Provider
                  Kind = kindText report.Event
                  Status = rowState.Status
                  Skill = rowState.Skill
                  Ts = report.OccurredAt }

            // Idempotency: a duplicate POST (same event_id) is a full no-op — nothing appended, no
            // upsert, no scheduler feed, live map unchanged.
            //
            // LastSeen must never regress: a heartbeat may already have advanced it past this event's
            // OccurredAt (a delayed real event landing after a fresher heartbeat), and resetting it
            // backwards could drop the session out of the openness window and grey an open card early.
            let lastSeen =
                match prior with
                | Some p -> max p.LastSeen report.OccurredAt
                | None -> report.OccurredAt

            let stored =
                { SessionId = report.SessionId
                  WorktreePath = report.WorktreePath
                  Provider = report.Provider
                  Status = newStatus
                  UpdatedAt = report.OccurredAt
                  LastSeen = lastSeen
                  // Carry the usage LWW clock forward: a status event neither carries nor reorders the
                  // gauge, and `fold` already preserves ContextUsage, so its ordering stamp survives too.
                  ContextUsageAt = prior |> Option.bind _.ContextUsageAt }

            // Append + durable upsert (last-write-wins) in ONE transaction so the history and the
            // status can never diverge on a mid-pair failure. Returns false for a duplicate event_id
            // (nothing appended or upserted) → live map unchanged.
            if not (store.AppendAndUpsert(eventRow, stored)) then
                live
            elif isOutOfOrder then
                // Mirror the ordering guard in memory: an out-of-order (older) event is recorded in the
                // history substrate but must not regress the live fold state or the shown card.
                live
            else
                scheduler.Post(RefreshScheduler.UpdateSessionStatus stored)
                live |> Map.add report.SessionId stored

    let mailbox =
        MailboxProcessor<ServiceMsg>.Start(fun inbox ->
            let rec loop (live: Map<SessionId, StoredStatus>) = async {
                let! msg = inbox.Receive()

                match msg with
                | Ingest report ->
                    // A store failure must never kill the single writer: catch, log, and keep the loop
                    // alive with the unchanged live map (the report is dropped, best-effort like the wire).
                    let next =
                        try
                            apply live report
                        with ex ->
                            Log.log "Activity" $"Ingest failed (report dropped, mailbox kept alive): {ex.Message}"
                            live
                    return! loop next
                | Seed loaded ->
                    let seeded = loaded |> List.fold (fun m s -> Map.add s.SessionId s m) live
                    return! loop seeded
                | Snapshot reply ->
                    reply.Reply live
                    return! loop live
                | Stop reply ->
                    reply.Reply()
            }

            loop Map.empty)

    let prune _ =
        try
            let deleted = store.PruneOld(DateTimeOffset.UtcNow - retentionPeriod)
            if deleted > 0 then Log.log "Activity" $"Retention: pruned {deleted} old activity row(s)"
        with ex ->
            Log.log "Activity" $"Retention prune failed: {ex.Message}"

    // Retention timer, created DISABLED (infinite due/period) at construction so the handle is a fixed
    // immutable binding; Start() arms it with Change once the store is seeded, Dispose() releases this
    // same handle.
    let pruneTimer =
        new Timer(TimerCallback(prune), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)

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
    member internal _.Submit(report: SessionActivityReport) =
        ensureActive ()
        mailbox.Post(Ingest report)

    /// Restart rebuild + retention timer. Loads every still-live session (last_seen within the idle
    /// window) from the store, feeds it to the scheduler and primes the in-memory fold map, so cards
    /// are correct before any new event arrives; then arms the retention timer.
    member _.Start() =
        ensureActive ()
        let loaded = store.LoadLiveStatuses DateTimeOffset.UtcNow
        // Seed the scheduler in ONE batch so the time-since-idle chip is stamped from each worktree's
        // NEWEST session rather than the oldest-replayed row (LoadLiveStatuses is ordered oldest-first);
        // feeding rows individually would freeze a stale idle stamp and overstate the chip (F11/C-14).
        scheduler.Post(RefreshScheduler.SeedSessionStatuses loaded)
        mailbox.Post(Seed loaded)
        Log.log "Activity" $"Rebuilt {List.length loaded} live session status(es) from store"
        pruneTimer.Change(pruneInterval, pruneInterval) |> ignore

    /// The current in-memory live map (test seam; live reads for cards go via the scheduler).
    member _.LiveSnapshot() : Map<SessionId, StoredStatus> =
        ensureActive ()
        mailbox.PostAndReply Snapshot

    member internal _.Store = store

    interface IDisposable with
        member _.Dispose() =
            let shouldStop =
                lock dispositionGate (fun () ->
                    if disposed then
                        false
                    else
                        disposed <- true
                        true)

            if shouldStop then
                pruneTimer.DisposeAsync().AsTask().GetAwaiter().GetResult()
                mailbox.PostAndReply Stop
                (mailbox :> IDisposable).Dispose()
