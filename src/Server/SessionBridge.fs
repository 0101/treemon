module Server.SessionBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open Shared

let private normalizePath = Server.PathUtils.normalizePath

[<RequireQualifiedAccess>]
type PromptKind =
    | Canvas
    | AgentPrompt

type Prompt =
    { Kind: PromptKind
      Text: string }

module Prompt =
    let canvas text = { Kind = PromptKind.Canvas; Text = text }
    let agentPrompt text = { Kind = PromptKind.AgentPrompt; Text = text }

type SendRequest =
    { WorktreePath: string
      SessionId: string option
      Prompt: Prompt }

[<RequireQualifiedAccess>]
type SendResult =
    | Delivered
    | Queued

type SessionEntry =
    { WorktreePath: string
      InjectUrl: string
      SessionId: string option
      RegisteredAt: DateTime }

type private QueuedPrompt =
    { EnqueuedAt: DateTime
      TargetSessionId: string option
      Prompt: Prompt }

// Mutable: ConcurrentDictionary is the thread-safe boundary for bridge registration and queueing.
// Separate session and poll maps prevent canvas-document heartbeats from overwriting live sessions.
let private sessionRegistry = ConcurrentDictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase)
let private pollRegistry = ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
let private promptQueue = ConcurrentDictionary<string, QueuedPrompt list>(StringComparer.OrdinalIgnoreCase)

let private httpClient = new HttpClient()

let private maxQueueSize = 10
let private queueTtl = TimeSpan.FromMinutes 5.0

let private promptKindName =
    function
    | PromptKind.Canvas -> "canvas"
    | PromptKind.AgentPrompt -> "agent-prompt"

let internal serializePrompt (prompt: Prompt) =
    JsonSerializer.Serialize(
        {| kind = promptKindName prompt.Kind
           prompt = prompt.Text |})

let private cleanExpired (prompts: QueuedPrompt list) =
    let cutoff = DateTime.UtcNow - queueTtl
    prompts |> List.filter (fun prompt -> prompt.EnqueuedAt > cutoff)

let private capQueue prompts =
    let excess = List.length prompts - maxQueueSize
    if excess > 0 then prompts |> List.skip excess else prompts

let private enqueue worktreeKey targetSessionId prompt =
    let queued =
        { EnqueuedAt = DateTime.UtcNow
          TargetSessionId = targetSessionId
          Prompt = prompt }

    promptQueue.AddOrUpdate(
        worktreeKey,
        [ queued ],
        fun _ existing -> cleanExpired existing @ [ queued ] |> capQueue)
    |> ignore

let private deliverableTo (sessionId: string option) (queued: QueuedPrompt) =
    match queued.TargetSessionId with
    | None -> true
    | Some target -> sessionId = Some target

let private requeue (worktreeKey: string) (survivors: QueuedPrompt list) =
    if not (List.isEmpty survivors) then
        promptQueue.AddOrUpdate(
            worktreeKey,
            survivors,
            fun _ existing -> survivors @ cleanExpired existing |> capQueue)
        |> ignore

let private postPrompt (entry: SessionEntry) (prompt: Prompt) (worktreeKey: string) : Async<Result<unit, string>> =
    async {
        try
            use content = new StringContent(serializePrompt prompt, Encoding.UTF8, "application/json")
            let! response = httpClient.PostAsync(entry.InjectUrl, content) |> Async.AwaitTask
            use _ = response

            if response.IsSuccessStatusCode then
                Log.log "SessionBridge" $"{promptKindName prompt.Kind} prompt forwarded to {Path.GetFileName(worktreeKey)}"
                return Ok()
            else
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                Log.log "SessionBridge" $"Prompt forward failed: status={int response.StatusCode}, body={body}"
                return Error $"bridge returned {int response.StatusCode}: {body}"
        with ex ->
            Log.log "SessionBridge" $"Prompt forward error: {ex.Message}"
            return Error ex.Message
    }

let private drainQueue (worktreeKey: string) (entry: SessionEntry) =
    match promptQueue.TryRemove(worktreeKey) with
    | false, _ -> ()
    | true, queued ->
        let deliver, survivors =
            queued
            |> cleanExpired
            |> List.partition (deliverableTo entry.SessionId)

        requeue worktreeKey survivors

        if not (List.isEmpty deliver) then
            Log.log "SessionBridge" $"Draining {List.length deliver} queued prompt(s) for {worktreeKey}"

            deliver
            |> List.map (fun queued ->
                async {
                    match! postPrompt entry queued.Prompt worktreeKey with
                    | Ok() -> ()
                    | Error error ->
                        Log.log "SessionBridge" $"Queued {promptKindName queued.Prompt.Kind} prompt delivery failed for {worktreeKey}: {error}"
                })
            |> Async.Sequential
            |> Async.Ignore
            |> Async.Start

let private registrationClockLock = obj ()
// Mutable under registrationClockLock so registrations receive a strictly monotonic timestamp.
let mutable private lastRegisteredAt = DateTime.MinValue

let private nextRegisteredAt () =
    lock registrationClockLock (fun () ->
        let now = DateTime.UtcNow
        let timestamp = if now > lastRegisteredAt then now else lastRegisteredAt.AddTicks 1L
        lastRegisteredAt <- timestamp
        timestamp)

let private normalizeSessionId =
    function
    | Some sessionId when not (String.IsNullOrWhiteSpace sessionId) -> Some sessionId
    | _ -> None

let private registryKeyFor normalizedWorktree sessionId =
    match normalizeSessionId sessionId with
    | Some value -> "sid:" + value
    | None -> "wt:" + normalizedWorktree

let registerSession (worktreePath: string) (injectUrl: string) (sessionId: string option) =
    let sessionId = normalizeSessionId sessionId
    let worktreeKey = normalizePath worktreePath
    let registryKey = registryKeyFor worktreeKey sessionId

    let entry =
        { WorktreePath = worktreeKey
          InjectUrl = injectUrl
          SessionId = sessionId
          RegisteredAt = nextRegisteredAt () }

    sessionRegistry[registryKey] <- entry
    Log.log "SessionBridge" $"Session registered {worktreeKey} (key={registryKey}) -> {injectUrl}"
    drainQueue worktreeKey entry

let registerPoll (worktreePath: string) =
    let key = normalizePath worktreePath
    pollRegistry[key] <- DateTime.UtcNow
    Log.log "SessionBridge" $"Poll heartbeat for {key}"

let sessionsForWorktree (worktreePath: string) : SessionEntry list =
    let worktreeKey = normalizePath worktreePath

    sessionRegistry.Values
    |> Seq.filter (fun entry ->
        String.Equals(entry.WorktreePath, worktreeKey, StringComparison.OrdinalIgnoreCase))
    |> Seq.toList

let private freshestSession (worktreePath: string) =
    sessionsForWorktree worktreePath
    |> List.sortByDescending _.RegisteredAt
    |> List.tryHead

let private isSessionAlive (entry: SessionEntry) =
    DateTime.UtcNow - entry.RegisteredAt < TimeSpan.FromSeconds 60.0

let private isPollAlive (lastHeartbeat: DateTime) =
    DateTime.UtcNow - lastHeartbeat < TimeSpan.FromSeconds 60.0

let send (request: SendRequest) =
    async {
        let worktreeKey = normalizePath request.WorktreePath
        let targetSessionId = normalizeSessionId request.SessionId

        let liveTarget =
            targetSessionId
            |> Option.bind (fun target ->
                sessionsForWorktree request.WorktreePath
                |> List.filter isSessionAlive
                |> List.tryFind (fun entry -> entry.SessionId = Some target))

        match liveTarget with
        | Some entry ->
            match! postPrompt entry request.Prompt worktreeKey with
            | Ok() -> return SendResult.Delivered
            | Error _ ->
                enqueue worktreeKey targetSessionId request.Prompt
                return SendResult.Queued
        | None ->
            enqueue worktreeKey targetSessionId request.Prompt
            return SendResult.Queued
    }

/// Atomically drain anonymous pending prompts of one transport kind. Canvas iframe heartbeats use
/// this for legacy owner-unknown canvas messages; owner-bound and agent prompts stay queued for a
/// matching live session registration.
let private drainPending (kind: PromptKind) (worktreePath: string) : Prompt list =
    let key = normalizePath worktreePath

    match promptQueue.TryRemove(key) with
    | false, _ -> []
    | true, queued ->
        let deliver, survivors =
            queued
            |> cleanExpired
            |> List.partition (fun prompt ->
                deliverableTo None prompt && prompt.Prompt.Kind = kind)

        requeue key survivors

        if not (List.isEmpty deliver) then
            Log.log "SessionBridge" $"Drained {List.length deliver} pending {promptKindName kind} prompt(s) for {Path.GetFileName(key)} via poll"

        deliver |> List.map _.Prompt

let drainPendingCanvas worktreePath =
    drainPending PromptKind.Canvas worktreePath

let private computeLiveness (session: SessionEntry option) (poll: bool * DateTime) =
    match session, poll with
    | Some entry, (true, heartbeat) ->
        let age =
            min
                (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds
                (DateTime.UtcNow - heartbeat).TotalSeconds
        Some (age, { IsAlive = isSessionAlive entry || isPollAlive heartbeat; SessionId = entry.SessionId })
    | Some entry, (false, _) ->
        let age = (DateTime.UtcNow - entry.RegisteredAt).TotalSeconds
        Some (age, { IsAlive = isSessionAlive entry; SessionId = entry.SessionId })
    | None, (true, heartbeat) ->
        let age = (DateTime.UtcNow - heartbeat).TotalSeconds
        Some (age, { IsAlive = isPollAlive heartbeat; SessionId = None })
    | None, (false, _) -> None

let getStatus (worktreePath: string) =
    let key = normalizePath worktreePath
    let session = freshestSession worktreePath
    let poll = pollRegistry.TryGetValue(key)

    match computeLiveness session poll with
    | Some (age, liveness) ->
        {| Registered = true
           LastHeartbeatAge = Some age
           IsAlive = liveness.IsAlive
           SessionId = liveness.SessionId |}
    | None ->
        {| Registered = false
           LastHeartbeatAge = None
           IsAlive = false
           SessionId = None |}

let getSessionForWorktree worktreePath =
    freshestSession worktreePath |> Option.bind _.SessionId

let getAllLiveness (worktreePaths: string list) : Map<string, BridgeLiveness> =
    worktreePaths
    |> List.choose (fun path ->
        let key = normalizePath path
        let session = freshestSession path
        let poll = pollRegistry.TryGetValue(key)
        computeLiveness session poll |> Option.map (fun (_, liveness) -> path, liveness))
    |> Map.ofList
