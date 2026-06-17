module Server.CanvasBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open Shared

let private normalizePath = Server.PathUtils.normalizePath

type SessionEntry =
    { WorktreePath: string
      InjectUrl: string
      SessionId: string option
      RegisteredAt: DateTime }

type QueuedMessage =
    { EnqueuedAt: DateTime
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

let private enqueue key payload =
    let msg = { EnqueuedAt = DateTime.UtcNow; Payload = payload }

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

let private drainQueue (key: string) (entry: SessionEntry) =
    match messageQueue.TryRemove(key) with
    | false, _ -> ()
    | true, queued ->
        let valid = cleanExpired queued

        if not (List.isEmpty valid) then
            Log.log "CanvasBridge" $"Draining {List.length valid} queued message(s) for {key}"

            valid
            |> List.map (fun msg ->
                async {
                    try
                        use content = new StringContent(msg.Payload, Encoding.UTF8, "application/json")
                        let! response = httpClient.PostAsync(entry.InjectUrl, content) |> Async.AwaitTask
                        use _ = response

                        if response.IsSuccessStatusCode then
                            Log.log "CanvasBridge" $"Drained message forwarded to {Path.GetFileName(key)}"
                        else
                            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                            Log.log "CanvasBridge" $"Drain forward failed for {key}: {int response.StatusCode} {body}"
                    with ex ->
                        Log.log "CanvasBridge" $"Drain forward error for {key}: {ex.Message}"
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

// The registry is keyed by sessionId so multiple sessions in one worktree coexist.
// sessionId=None entries fall back to a per-worktree slot (namespaced so it can never
// collide with a real sessionId). This preserves single-session back-compat and makes
// two None registrations for one worktree collapse to that single slot, while two
// distinct sessionIds for one worktree keep separate slots (no clobber).
// Assumes sessionIds are globally unique (they are: provider session UUIDs); the same
// sessionId is never registered against two different worktrees, so the WorktreePath
// carried in the value is not part of the key.
let private registryKeyFor (normalizedWorktree: string) (sessionId: string option) =
    match sessionId with
    | Some sid when not (String.IsNullOrWhiteSpace sid) -> "sid:" + sid
    | _ -> "wt:" + normalizedWorktree

let registerSession (worktreePath: string) (injectUrl: string) (sessionId: string option) =
    let worktreeKey = normalizePath worktreePath
    let key = registryKeyFor worktreeKey sessionId

    let entry =
        { WorktreePath = worktreeKey
          InjectUrl = injectUrl
          SessionId = sessionId
          RegisteredAt = nextRegisteredAt () }

    match sessionRegistry.TryGetValue(key) with
    | true, oldEntry ->
        Log.log "CanvasBridge" $"Updating session registration {key} for {worktreeKey}: {oldEntry.InjectUrl} -> {injectUrl}"
    | false, _ -> ()

    sessionRegistry[key] <- entry
    Log.log "CanvasBridge" $"Session registered {worktreeKey} (key={key}) -> {injectUrl} (session registry size: {sessionRegistry.Count})"
    // The message queue stays keyed by worktree path, so drain to the new entry by worktree key.
    drainQueue worktreeKey entry

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

/// The most recently registered session for a worktree. Deterministic because
/// RegisteredAt is issued from a monotonic clock. Preserves the prior "last
/// registered wins" semantics for the single-status / single-session views.
let private freshestSession (worktreePath: string) : SessionEntry option =
    sessionsForWorktree worktreePath
    |> List.sortByDescending (fun e -> e.RegisteredAt)
    |> List.tryHead

let private isSessionAlive (entry: SessionEntry) =
    (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds < 60.0

let private isPollAlive (lastHeartbeat: DateTime) =
    (DateTime.UtcNow - lastHeartbeat).TotalSeconds < 60.0

let sendMessage (request: CanvasMessageRequest) =
    async {
        let key = normalizePath (WorktreePath.value request.WorktreePath)
        Log.log "CanvasBridge" $"sendMessage: key={key}, payload length={request.Payload.Length}"

        // Owner-aware routing (deliver to the doc's owning session) is a follow-up
        // task; here we preserve single-session back-compat by delivering to the
        // worktree's most-recently-registered live session, otherwise queue.
        let sessions = sessionsForWorktree (WorktreePath.value request.WorktreePath)

        let activeEntry =
            sessions
            |> List.filter isSessionAlive
            |> List.sortByDescending (fun e -> e.RegisteredAt)
            |> List.tryHead

        match activeEntry with
        | Some entry ->
            try
                use content = new StringContent(request.Payload, Encoding.UTF8, "application/json")
                let! response = httpClient.PostAsync(entry.InjectUrl, content) |> Async.AwaitTask
                use _ = response

                if not response.IsSuccessStatusCode then
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    Log.log "CanvasBridge" $"sendMessage HTTP failure: status={int response.StatusCode}, body={body}"
                    return CanvasMessageResult.Error $"bridge returned {int response.StatusCode}: {body}"
                else
                    Log.log "CanvasBridge" $"Message forwarded to {Path.GetFileName(key)}"
                    return CanvasMessageResult.Ok
            with ex ->
                Log.log "CanvasBridge" $"sendMessage exception: {ex.Message}"
                return CanvasMessageResult.Error ex.Message
        | None ->
            // No alive session-backed bridge — queue for poll drain (or future session)
            if not (List.isEmpty sessions) then
                Log.log "CanvasBridge" $"sendMessage: registered session(s) for {Path.GetFileName(key)} all stale, queuing"

            let hasPolling = pollRegistry.ContainsKey(key)
            let reason = if hasPolling then "poll-based bridge" else "no bridge"
            Log.log "CanvasBridge" $"sendMessage: {reason} for {Path.GetFileName(key)}, message queued"
            enqueue key request.Payload
            return CanvasMessageResult.Queued
    }

/// Atomically drain pending messages for a worktree (used by heartbeat polling).
let drainPending (worktreePath: string) : string list =
    let key = normalizePath worktreePath
    match messageQueue.TryRemove(key) with
    | true, queued ->
        let valid = cleanExpired queued
        if not (List.isEmpty valid) then
            Log.log "CanvasBridge" $"Drained {List.length valid} pending message(s) for {Path.GetFileName(key)} via poll"
        valid |> List.map _.Payload
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
    freshestSession worktreePath |> Option.bind (fun e -> e.SessionId)

let getAllLiveness (worktreePaths: string list) : Map<string, BridgeLiveness> =
    worktreePaths
    |> List.choose (fun path ->
        let key = normalizePath path
        let session = freshestSession path
        let poll = pollRegistry.TryGetValue(key)
        computeLiveness session poll |> Option.map (fun (_, liveness) -> path, liveness))
    |> Map.ofList
