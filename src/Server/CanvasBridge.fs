module Server.CanvasBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open Shared

let private normalizePath = Server.PathUtils.normalizePath
let private normalizeFilename (filename: string) = filename.ToLowerInvariant()

type SessionEntry =
    { WorktreePath: string
      InjectUrl: string
      SessionId: string option
      RegisteredAt: DateTime }

// A queued canvas message re-resolves its persistent target at drain time so ownership changes
// made while it waits are honored before delivery.
type QueuedMessage =
    { EnqueuedAt: DateTime
      Filename: string
      Payload: string }

// Mutable: ConcurrentDictionary used for thread-safe bridge registry;
// simple two-operation access pattern doesn't warrant MailboxProcessor overhead.
// Split into two maps to prevent heartbeat polling from overwriting session registrations.
let private sessionRegistry = ConcurrentDictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase)
let private pollRegistry = ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)

let private messageQueue = ConcurrentDictionary<string, QueuedMessage list>(StringComparer.OrdinalIgnoreCase)

let private httpClient = new HttpClient()

let private maxQueueSize = 10
let private queueTtl = TimeSpan.FromMinutes 5.0

let private cleanExpired (messages: QueuedMessage list) =
    let cutoff = DateTime.UtcNow - queueTtl
    messages |> List.filter (fun m -> m.EnqueuedAt > cutoff)

let private enqueue key filename payload =
    let msg =
        { EnqueuedAt = DateTime.UtcNow
          Filename = filename
          Payload = payload }

    messageQueue.AddOrUpdate(
        key,
        [ msg ],
        fun _ existing ->
            let cleaned = cleanExpired existing
            let appended = cleaned @ [ msg ]

            if List.length appended > maxQueueSize then
                appended |> List.skip (List.length appended - maxQueueSize)
            else
                appended
    )
    |> ignore

/// Re-resolve the current unified target for every queued message. A message without a target is
/// never deliverable, and after reassignment only the current target may receive it.
let private deliverableTo key (sessionId: string option) (msg: QueuedMessage) =
    CanvasDocOwnership.getOwnerSync key msg.Filename = sessionId
    && Option.isSome sessionId

/// Re-queue messages a drain did not deliver so their owner can collect them later. Survivors
/// keep their original EnqueuedAt (TTL preserved) and are placed ahead of anything enqueued
/// during the drain window, then re-capped to maxQueueSize (oldest dropped first, like enqueue).
let private requeue (key: string) (survivors: QueuedMessage list) =
    if not (List.isEmpty survivors) then
        messageQueue.AddOrUpdate(
            key,
            survivors,
            fun _ existing ->
                let merged = survivors @ cleanExpired existing

                if List.length merged > maxQueueSize then
                    merged |> List.skip (List.length merged - maxQueueSize)
                else
                    merged)
        |> ignore

type internal PendingLaunchResult =
    | PendingLaunchStarted
    | PendingLaunchJoined

type private PendingLaunchMsg =
    | BeginPendingLaunch of worktreeKey: string * filename: string * AsyncReplyChannel<PendingLaunchResult>
    | CancelPendingLaunch of worktreeKey: string * AsyncReplyChannel<unit>
    | TakePendingLaunch of worktreeKey: string * AsyncReplyChannel<Set<string>>

let private pendingLaunchAgent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop pending =
            async {
                let! msg = inbox.Receive()

                match msg with
                | BeginPendingLaunch(worktreeKey, filename, reply) ->
                    let existing =
                        pending
                        |> Map.tryFind worktreeKey
                        |> Option.defaultValue Set.empty

                    let result =
                        if Set.isEmpty existing then PendingLaunchStarted
                        else PendingLaunchJoined

                    reply.Reply(result)
                    return! loop (pending |> Map.add worktreeKey (existing |> Set.add filename))

                | CancelPendingLaunch(worktreeKey, reply) ->
                    reply.Reply()
                    return! loop (pending |> Map.remove worktreeKey)

                | TakePendingLaunch(worktreeKey, reply) ->
                    pending
                    |> Map.tryFind worktreeKey
                    |> Option.defaultValue Set.empty
                    |> reply.Reply

                    return! loop (pending |> Map.remove worktreeKey)
            }

        loop Map.empty)

let internal beginPendingLaunch worktreePath filename =
    pendingLaunchAgent.PostAndAsyncReply(fun reply ->
        BeginPendingLaunch(normalizePath worktreePath, normalizeFilename filename, reply))

let internal cancelPendingLaunch worktreePath =
    pendingLaunchAgent.PostAndAsyncReply(fun reply ->
        CancelPendingLaunch(normalizePath worktreePath, reply))

let private takePendingLaunch worktreeKey =
    pendingLaunchAgent.PostAndReply(fun reply -> TakePendingLaunch(worktreeKey, reply))

let private assignPendingLaunch worktreeKey sessionId =
    let filenames = takePendingLaunch worktreeKey

    if not (Set.isEmpty filenames) then
        filenames
        |> Set.toList
        |> List.map (fun filename -> CanvasDocOwnership.assign worktreeKey filename sessionId)
        |> Async.Sequential
        |> Async.Ignore
        |> Async.RunSynchronously

        Log.log
            "CanvasBridge"
            $"Session {sessionId} assigned {Set.count filenames} pending canvas target(s) for {worktreeKey}"

let private drainQueue (key: string) (entry: SessionEntry) =
    match messageQueue.TryRemove(key) with
    | false, _ -> ()
    | true, queued ->
        let valid = cleanExpired queued
        // Forward only what this session may receive and re-queue the rest for the rightful
        // target. An untargeted document always stays queued.
        let deliver, requeued = valid |> List.partition (deliverableTo key entry.SessionId)
        requeue key requeued

        if not (List.isEmpty deliver) then
            Log.log "CanvasBridge" $"Draining {List.length deliver} queued message(s) for {key}"

            deliver
            |> List.map (fun msg ->
                async {
                    try
                        use content = new StringContent(msg.Payload, Encoding.UTF8, "application/json")
                        let! response = httpClient.PostAsync(entry.InjectUrl, content) |> Async.AwaitTask
                        use _ = response

                        if response.IsSuccessStatusCode then
                            Log.log "CanvasBridge" $"Drained message for '{msg.Filename}' forwarded to {Path.GetFileName(key)}"
                        else
                            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                            Log.log "CanvasBridge" $"Drain forward failed for {key} ('{msg.Filename}'): {int response.StatusCode} {body}"
                    with ex ->
                        Log.log "CanvasBridge" $"Drain forward error for {key} ('{msg.Filename}'): {ex.Message}"
                })
            |> Async.Sequential
            |> Async.Ignore
            |> Async.Start

// Monotonic registration clock: guarantees each registration receives a strictly
// increasing RegisteredAt, so "most recently registered" is deterministic even when
// several registrations land within the same system-clock tick (e.g. in tests). In
// production, registrations are seconds apart so this is just wall-clock time.
let private monoLock = obj ()
let private lastIssuedAt = ref DateTime.MinValue

let private nextRegisteredAt () =
    lock monoLock (fun () ->
        let now = DateTime.UtcNow
        let t = if now > lastIssuedAt.Value then now else lastIssuedAt.Value.AddTicks 1L
        lastIssuedAt.Value <- t
        t)

/// Collapse a blank/whitespace sessionId to None so a registry entry's SessionId can never be
/// Some "". That value is the key for owner-based delivery (sendMessage) and the source of the
/// scanner's fallback attribution (RefreshScheduler.fallbackOwner -> CanvasDocOwnership.attribute):
/// a Some "" owner is unroutable (no real Some "real-id" session ever equals it) yet sticks
/// permanently because the scanner never overwrites an existing owner — so a blank registration
/// would blackhole the doc's messages. A registrant that omits or blanks its sessionId is
/// anonymous (None), exactly like a missing field.
let private normalizeSessionId (sessionId: string option) : string option =
    match sessionId with
    | Some sid when not (String.IsNullOrWhiteSpace sid) -> Some sid
    | _ -> None

// The registry is keyed by sessionId so multiple sessions in one worktree coexist.
// sessionId=None entries fall back to a per-worktree slot (namespaced so it can never
// collide with a real sessionId). This preserves single-session back-compat and makes
// two None registrations for one worktree collapse to that single slot, while two
// distinct sessionIds for one worktree keep separate slots (no clobber).
// Assumes sessionIds are globally unique (they are: provider session UUIDs); the same
// sessionId is never registered against two different worktrees, so the WorktreePath
// carried in the value is not part of the key.
let private registryKeyFor (normalizedWorktree: string) (sessionId: string option) =
    match normalizeSessionId sessionId with
    | Some sid -> "sid:" + sid
    | None -> "wt:" + normalizedWorktree

let private registerSessionCore
    (worktreePath: string)
    (injectUrl: string)
    (sessionId: string option)
    =
    // Defense-in-depth: a blank/whitespace sessionId from any caller collapses to None, so
    // entry.SessionId is never Some "" (see normalizeSessionId). The HTTP boundary normalizes
    // too, but every registration funnels through here.
    let sessionId = normalizeSessionId sessionId
    let worktreeKey = normalizePath worktreePath
    let key = registryKeyFor worktreeKey sessionId
    let existing = sessionRegistry.TryGetValue(key)

    let entry =
        { WorktreePath = worktreeKey
          InjectUrl = injectUrl
          SessionId = sessionId
          RegisteredAt = nextRegisteredAt () }

    match existing with
    | true, oldEntry ->
        Log.log "CanvasBridge" $"Updating session registration {key} for {worktreeKey}: {oldEntry.InjectUrl} -> {injectUrl}"
    | false, _ -> ()

    sessionRegistry[key] <- entry
    Log.log "CanvasBridge" $"Session registered {worktreeKey} (key={key}) -> {injectUrl} (session registry size: {sessionRegistry.Count})"

    sessionId
    |> Option.iter (assignPendingLaunch worktreeKey)

    // The message queue stays keyed by worktree path, so drain to the new entry by worktree key.
    drainQueue worktreeKey entry

let registerSession (worktreePath: string) (injectUrl: string) (sessionId: string option) =
    registerSessionCore worktreePath injectUrl sessionId

let registerPoll (worktreePath: string) =
    let key = normalizePath worktreePath
    pollRegistry[key] <- DateTime.UtcNow
    Log.log "CanvasBridge" $"Poll heartbeat for {key} (poll registry size: {pollRegistry.Count})"

/// All sessions currently registered for a worktree. The registry is sessionId-keyed,
/// so this is the worktree-level view that backs fallbacks, liveness and (later)
/// owner-aware routing now that multiple sessions can share one worktree.
let sessionsForWorktree (worktreePath: string) : SessionEntry list =
    let worktreeKey = normalizePath worktreePath

    sessionRegistry.Values
    |> Seq.filter (fun e -> String.Equals(e.WorktreePath, worktreeKey, StringComparison.OrdinalIgnoreCase))
    |> Seq.toList

let internal registrationStamp (worktreePath: string) (sessionId: string) =
    sessionsForWorktree worktreePath
    |> List.tryFind (fun entry -> entry.SessionId = Some sessionId)
    |> Option.map _.RegisteredAt

let internal waitForRegistrationAfter
    (timeout: TimeSpan)
    (worktreePath: string)
    (sessionId: string)
    (previous: DateTime option)
    =
    let deadline = DateTime.UtcNow + timeout

    let rec wait () =
        async {
            let registered =
                registrationStamp worktreePath sessionId
                |> Option.exists (fun registeredAt ->
                    previous |> Option.forall (fun previousAt -> registeredAt > previousAt))

            if registered then
                return true
            elif DateTime.UtcNow >= deadline then
                return false
            else
                do! Async.Sleep 50
                return! wait ()
        }

    wait ()

/// The most recently registered session for a worktree. Deterministic because
/// RegisteredAt is issued from a monotonic clock. Preserves the prior "last
/// registered wins" semantics for the single-status / single-session views.
let private freshestSession (worktreePath: string) : SessionEntry option =
    sessionsForWorktree worktreePath
    |> List.sortByDescending _.RegisteredAt
    |> List.tryHead

let private isSessionAlive (entry: SessionEntry) =
    (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds < 60.0

let private isPollAlive (lastHeartbeat: DateTime) =
    (DateTime.UtcNow - lastHeartbeat).TotalSeconds < 60.0

/// POST a payload to one session's inject URL. Ok on a 2xx response; Error (with a
/// reason) on a non-success status or a transport-level exception. Callers decide
/// whether a failure surfaces to the client or falls through to the queue.
let private postPayload (entry: SessionEntry) (payload: string) (key: string) : Async<Result<unit, string>> =
    async {
        try
            use content = new StringContent(payload, Encoding.UTF8, "application/json")
            let! response = httpClient.PostAsync(entry.InjectUrl, content) |> Async.AwaitTask
            use _ = response

            if not response.IsSuccessStatusCode then
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                Log.log "CanvasBridge" $"sendMessage HTTP failure: status={int response.StatusCode}, body={body}"
                return Error $"bridge returned {int response.StatusCode}: {body}"
            else
                Log.log "CanvasBridge" $"Message forwarded to {Path.GetFileName(key)}"
                return Ok()
        with ex ->
            Log.log "CanvasBridge" $"sendMessage exception: {ex.Message}"
            return Error ex.Message
    }

let getTargetOwner (worktreePath: string) (filename: string) =
    CanvasDocOwnership.getOwner worktreePath filename

/// Route a canvas-doc message through the unified persistent target store.
///
/// The registry is sessionId-keyed, so two sessions can share one worktree; this
/// resolves the appropriate target and delivers only to it. Untargeted messages remain queued
/// until an identified registration consumes the worktree's pending launch.
///
/// 1. Owner has a live registry entry -> POST to it (HTTP failure -> queue for redelivery).
/// 2. Owner offline/gone              -> queue (never fall back to a non-owner).
/// 3. No declared owner               -> queue (never deliver to a co-located non-author).
///    Authoring sessions declare ownership on every canvas write, so an unowned doc has no
///    identifiable recipient; the send/resume flow brings up a session to drain it. The old
///    "exactly one live session" single-session fallback is gone — it misrouted unowned docs
///    (e.g. a focused-review reply into an unrelated co-located session).
let sendMessage (request: CanvasMessageRequest) =
    async {
        let worktree = WorktreePath.value request.WorktreePath
        let key = normalizePath worktree
        Log.log "CanvasBridge" $"sendMessage: key={key}, filename={request.Filename}, payload length={request.Payload.Length}"

        // Live sessions for this worktree, freshest first.
        let liveSessions =
            sessionsForWorktree worktree
            |> List.filter isSessionAlive
            |> List.sortByDescending _.RegisteredAt

        let! owner = getTargetOwner worktree request.Filename

        let queueWith reason =
            Log.log "CanvasBridge" $"sendMessage: {reason} for {Path.GetFileName(key)}, message queued"
            enqueue key request.Filename request.Payload
            CanvasMessageResult.Queued

        match owner with
        | Some ownerId ->
            match liveSessions |> List.tryFind (fun e -> e.SessionId = Some ownerId) with
            | Some entry ->
                // Owner is live — deliver to it. A transient HTTP failure falls through to
                // the queue so the message is redelivered when the owner re-registers.
                match! postPayload entry request.Payload key with
                | Ok() ->
                    Log.log "CanvasBridge" $"sendMessage: delivered to owner {ownerId} for {Path.GetFileName(key)}"
                    return CanvasMessageResult.Ok
                | Error _ -> return queueWith $"owner {ownerId} unreachable"
            | None ->
                // Owner offline or not registered — queue. Never deliver to a non-owner,
                // even if another session for the worktree is live.
                return queueWith $"owner {ownerId} offline"
        | None ->
            // An unowned document has no deterministic recipient. Queue it; the send/resume flow
            // records the filename in the worktree launch coordinator before starting a session.
            match liveSessions with
            | [] ->
                let reason = if pollRegistry.ContainsKey(key) then "no owner, poll-based bridge" else "no owner, no bridge"
                return queueWith reason
            | sessions ->
                return queueWith $"no owner with {List.length sessions} live session(s) — not delivering to a non-author"
    }

/// Atomically inspect pending messages for heartbeat polling. Unified targets always require an
/// identified push bridge, so an anonymous poll cannot claim or cross-route them.
let drainPending (worktreePath: string) : string list =
    let key = normalizePath worktreePath
    match messageQueue.TryRemove(key) with
    | true, queued ->
        let valid = cleanExpired queued
        let deliver, requeued = valid |> List.partition (deliverableTo key None)
        requeue key requeued

        if not (List.isEmpty deliver) then
            Log.log "CanvasBridge" $"Drained {List.length deliver} pending message(s) for {Path.GetFileName(key)} via poll"

        deliver |> List.map _.Payload
    | false, _ -> []

let private computeLiveness (session: SessionEntry option) (poll: (bool * DateTime)) =
    match session, poll with
    | Some entry, (true, hb) ->
        let age = min (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds (DateTime.UtcNow - hb).TotalSeconds
        Some (age, { IsAlive = isSessionAlive entry || isPollAlive hb; SessionId = entry.SessionId })
    | Some entry, (false, _) ->
        let age = (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds
        Some (age, { IsAlive = isSessionAlive entry; SessionId = entry.SessionId })
    | None, (true, hb) ->
        let age = (DateTime.UtcNow - hb).TotalSeconds
        Some (age, { IsAlive = isPollAlive hb; SessionId = None })
    | None, (false, _) -> None

let getStatus (worktreePath: string) =
    let key = normalizePath worktreePath
    let session = freshestSession worktreePath
    let poll = pollRegistry.TryGetValue(key)

    match computeLiveness session poll with
    | Some (age, liveness) ->
        {| Registered = true; LastHeartbeatAge = Some age; IsAlive = liveness.IsAlive; SessionId = liveness.SessionId |}
    | None ->
        {| Registered = false; LastHeartbeatAge = None; IsAlive = false; SessionId = None |}

let getSessionForWorktree (worktreePath: string) : string option =
    // Most-recently-registered session for the worktree. Preserves the prior
    // last-registered semantics now that the registry is sessionId-keyed.
    freshestSession worktreePath |> Option.bind _.SessionId

let getAllLiveness (worktreePaths: string list) : Map<string, BridgeLiveness> =
    worktreePaths
    |> List.choose (fun path ->
        let key = normalizePath path
        let session = freshestSession path
        let poll = pollRegistry.TryGetValue(key)
        computeLiveness session poll |> Option.map (fun (_, liveness) -> path, liveness))
    |> Map.ofList
