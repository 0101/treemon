module Server.CanvasBridge

open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open FsToolkit.ErrorHandling
open Shared

let private registry = ConcurrentDictionary<string, string>()

let private httpClient = new HttpClient()

let private normalizePath (path: string) =
    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

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

        let content = new StringContent(request.Payload, Encoding.UTF8, "application/json")

        let! response =
            httpClient.PostAsync(injectUrl, content) |> Async.AwaitTask

        if not response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return! Error $"bridge returned {int response.StatusCode}: {body}"
    }
