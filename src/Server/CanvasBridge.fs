module Server.CanvasBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open FsToolkit.ErrorHandling
open Shared

let private normalizePath = Server.PathUtils.normalizePath

type BridgeEntry =
    { InjectUrl: string
      SessionId: string option
      LastHeartbeat: DateTime }

// Mutable: ConcurrentDictionary used for thread-safe bridge registry;
// simple two-operation access pattern doesn't warrant MailboxProcessor overhead.
let private registry = ConcurrentDictionary<string, BridgeEntry>(StringComparer.OrdinalIgnoreCase)

let private httpClient = new HttpClient()

let register (worktreePath: string) (injectUrl: string) (sessionId: string option) =
    let key = normalizePath worktreePath
    let entry = { InjectUrl = injectUrl; SessionId = sessionId; LastHeartbeat = DateTime.UtcNow }

    match registry.TryGetValue(key) with
    | true, oldEntry ->
        Log.log "CanvasBridge" $"Overwriting registration for {key}: {oldEntry.InjectUrl} -> {injectUrl}"
    | false, _ -> ()

    registry[key] <- entry
    Log.log "CanvasBridge" $"Registered {key} -> {injectUrl} (registry size: {registry.Count})"

let sendMessage (request: CanvasMessageRequest) =
    asyncResult {
        let key = normalizePath (WorktreePath.value request.WorktreePath)
        Log.log "CanvasBridge" $"sendMessage: key={key}, payload length={request.Payload.Length}"

        let! injectUrl =
            match registry.TryGetValue(key) with
            | true, entry -> Ok entry.InjectUrl
            | false, _ ->
                let registeredKeys = registry.Keys |> Seq.toList |> String.concat "; "
                Log.log "CanvasBridge" $"sendMessage FAILED: no bridge for key={key}. Registered keys: [{registeredKeys}]"
                Error "no bridge registered for this worktree"

        use content = new StringContent(request.Payload, Encoding.UTF8, "application/json")

        let! response =
            httpClient.PostAsync(injectUrl, content) |> Async.AwaitTask

        use _ = response

        if not response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            Log.log "CanvasBridge" $"sendMessage HTTP failure: status={int response.StatusCode}, body={body}"
            return! Error $"bridge returned {int response.StatusCode}: {body}"
        else
            Log.log "CanvasBridge" $"Message forwarded to {Path.GetFileName(key)}"
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
