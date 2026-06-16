module Server.CanvasBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open Shared

let private normalizePath = Server.PathUtils.normalizePath

type SessionEntry =
    { InjectUrl: string
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

let registerSession (worktreePath: string) (injectUrl: string) (sessionId: string option) =
    let key = normalizePath worktreePath
    let entry = { InjectUrl = injectUrl; SessionId = sessionId; RegisteredAt = DateTime.UtcNow }

    match sessionRegistry.TryGetValue(key) with
    | true, oldEntry ->
        Log.log "CanvasBridge" $"Overwriting session registration for {key}: {oldEntry.InjectUrl} -> {injectUrl}"
    | false, _ -> ()

    sessionRegistry[key] <- entry
    Log.log "CanvasBridge" $"Session registered {key} -> {injectUrl} (session registry size: {sessionRegistry.Count})"
    drainQueue key entry

let registerPoll (worktreePath: string) =
    let key = normalizePath worktreePath
    pollRegistry[key] <- DateTime.UtcNow
    Log.log "CanvasBridge" $"Poll heartbeat for {key} (poll registry size: {pollRegistry.Count})"

let private isSessionAlive (entry: SessionEntry) =
    (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds < 60.0

let private isPollAlive (lastHeartbeat: DateTime) =
    (DateTime.UtcNow - lastHeartbeat).TotalSeconds < 60.0

let sendMessage (request: CanvasMessageRequest) =
    async {
        let key = normalizePath (WorktreePath.value request.WorktreePath)
        Log.log "CanvasBridge" $"sendMessage: key={key}, payload length={request.Payload.Length}"

        let activeEntry =
            match sessionRegistry.TryGetValue(key) with
            | true, entry when isSessionAlive entry -> Some entry
            | true, entry ->
                Log.log "CanvasBridge" $"sendMessage: stale session for {Path.GetFileName(key)} (age={(DateTime.UtcNow - entry.RegisteredAt).TotalSeconds:F0}s), ignoring"
                None
            | false, _ -> None

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

let private computeLiveness (session: (bool * SessionEntry)) (poll: (bool * DateTime)) =
    match session, poll with
    | (true, entry), (true, hb) ->
        let age = min (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds (DateTime.UtcNow - hb).TotalSeconds
        Some (age, { IsAlive = isSessionAlive entry || isPollAlive hb; SessionId = entry.SessionId })
    | (true, entry), (false, _) ->
        let age = (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds
        Some (age, { IsAlive = isSessionAlive entry; SessionId = entry.SessionId })
    | (false, _), (true, hb) ->
        let age = (DateTime.UtcNow - hb).TotalSeconds
        Some (age, { IsAlive = isPollAlive hb; SessionId = None })
    | (false, _), (false, _) -> None

let getStatus (worktreePath: string) =
    let key = normalizePath worktreePath
    let session = sessionRegistry.TryGetValue(key)
    let poll = pollRegistry.TryGetValue(key)

    match computeLiveness session poll with
    | Some (age, liveness) ->
        {| Registered = true; LastHeartbeatAge = Some age; IsAlive = liveness.IsAlive; SessionId = liveness.SessionId |}
    | None ->
        {| Registered = false; LastHeartbeatAge = None; IsAlive = false; SessionId = None |}

let getSessionForWorktree (worktreePath: string) : string option =
    let key = normalizePath worktreePath

    match sessionRegistry.TryGetValue(key) with
    | true, entry -> entry.SessionId
    | false, _ -> None

let getAllLiveness (worktreePaths: string list) : Map<string, BridgeLiveness> =
    worktreePaths
    |> List.choose (fun path ->
        let key = normalizePath path
        let session = sessionRegistry.TryGetValue(key)
        let poll = pollRegistry.TryGetValue(key)
        computeLiveness session poll |> Option.map (fun (_, liveness) -> path, liveness))
    |> Map.ofList
