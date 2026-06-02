module Server.CanvasBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open Shared

let private normalizePath = Server.PathUtils.normalizePath

type BridgeEntry =
    { InjectUrl: string
      SessionId: string option
      LastHeartbeat: DateTime }

type QueuedMessage =
    { EnqueuedAt: DateTime
      Payload: string }

// Mutable: ConcurrentDictionary used for thread-safe bridge registry;
// simple two-operation access pattern doesn't warrant MailboxProcessor overhead.
let private registry = ConcurrentDictionary<string, BridgeEntry>(StringComparer.OrdinalIgnoreCase)

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

let private drainQueue (key: string) (entry: BridgeEntry) =
    match messageQueue.TryRemove(key) with
    | false, _ -> ()
    | true, queued ->
        let valid = cleanExpired queued

        if not (List.isEmpty valid) then
            Log.log "CanvasBridge" $"Draining {List.length valid} queued message(s) for {key}"

            valid
            |> List.iter (fun msg ->
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
                }
                |> Async.Start)

let register (worktreePath: string) (injectUrl: string) (sessionId: string option) =
    let key = normalizePath worktreePath
    let entry = { InjectUrl = injectUrl; SessionId = sessionId; LastHeartbeat = DateTime.UtcNow }

    match registry.TryGetValue(key) with
    | true, oldEntry ->
        Log.log "CanvasBridge" $"Overwriting registration for {key}: {oldEntry.InjectUrl} -> {injectUrl}"
    | false, _ -> ()

    registry[key] <- entry
    Log.log "CanvasBridge" $"Registered {key} -> {injectUrl} (registry size: {registry.Count})"
    drainQueue key entry

let sendMessage (request: CanvasMessageRequest) =
    async {
        let key = normalizePath (WorktreePath.value request.WorktreePath)
        Log.log "CanvasBridge" $"sendMessage: key={key}, payload length={request.Payload.Length}"

        match registry.TryGetValue(key) with
        | false, _ ->
            let registeredKeys = registry.Keys |> Seq.toList |> String.concat "; "
            Log.log "CanvasBridge" $"sendMessage: no bridge for key={key}, message queued. Registered keys: [{registeredKeys}]"
            enqueue key request.Payload
            return CanvasMessageResult.Queued
        | true, entry ->
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
    }

let isAlive (entry: BridgeEntry) =
    (DateTime.UtcNow - entry.LastHeartbeat).TotalSeconds < 60.0

let getStatus (worktreePath: string) =
    let key = normalizePath worktreePath

    match registry.TryGetValue(key) with
    | true, entry ->
        let age = (DateTime.UtcNow - entry.LastHeartbeat).TotalSeconds
        {| Registered = true; LastHeartbeatAge = Some age; IsAlive = isAlive entry; SessionId = entry.SessionId |}
    | false, _ ->
        {| Registered = false; LastHeartbeatAge = None; IsAlive = false; SessionId = None |}

let getAllLiveness (worktreePaths: string list) : Map<string, BridgeLiveness> =
    worktreePaths
    |> List.choose (fun path ->
        let key = normalizePath path
        match registry.TryGetValue(key) with
        | true, entry ->
            Some (path, { IsAlive = isAlive entry; SessionId = entry.SessionId })
        | false, _ -> None)
    |> Map.ofList
