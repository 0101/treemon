module Server.CanvasBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open FsToolkit.ErrorHandling
open Shared

let private normalizePath = Server.PathUtils.normalizePath

// Mutable: ConcurrentDictionary used for thread-safe bridge registry;
// simple two-operation access pattern doesn't warrant MailboxProcessor overhead.
let private registry = ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)

let private httpClient = new HttpClient()

let register (worktreePath: string) (injectUrl: string) =
    let key = normalizePath worktreePath
    registry[key] <- injectUrl

let sendMessage (request: CanvasMessageRequest) =
    asyncResult {
        let key = normalizePath (WorktreePath.value request.WorktreePath)

        let! injectUrl =
            match registry.TryGetValue(key) with
            | true, url -> Ok url
            | false, _ -> Error "no bridge registered for this worktree"

        use content = new StringContent(request.Payload, Encoding.UTF8, "application/json")

        let! response =
            httpClient.PostAsync(injectUrl, content) |> Async.AwaitTask

        use _ = response

        if not response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return! Error $"bridge returned {int response.StatusCode}: {body}"
        else
            Log.log "CanvasBridge" $"Message forwarded to {Path.GetFileName(key)}"
    }
