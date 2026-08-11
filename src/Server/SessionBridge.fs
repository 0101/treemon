module Server.SessionBridge

open System
open System.Collections.Concurrent
open System.Collections.Generic
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
      Text: string
      Filename: string option }

module Prompt =
    let canvas text =
        { Kind = PromptKind.Canvas
          Text = text
          Filename = None }

    let canvasFor filename text =
        { Kind = PromptKind.Canvas
          Text = text
          Filename = Some filename }

    let agentPrompt text =
        { Kind = PromptKind.AgentPrompt
          Text = text
          Filename = None }

type SendRequest =
    { WorktreePath: string
      SessionId: string option
      Prompt: Prompt }

[<RequireQualifiedAccess>]
type DeliveryResult =
    | Delivered
    | NoLiveSession
    | DeliveryFailed

type SessionEntry =
    { WorktreePath: string
      InjectUrl: string
      SessionId: string option
      RegisteredAt: DateTime }

[<RequireQualifiedAccess>]
type SendResult =
    | Delivered
    | Queued

type internal QueuedPrompt =
    { EnqueuedAt: DateTime
      TargetSessionId: string option
      Prompt: Prompt }

type private DeliveryAttempt =
    | AttemptDelivered
    | AttemptNoLiveSession
    | AttemptFailed of SessionEntry

// Mutable: ConcurrentDictionary is the thread-safe boundary for bridge registration and queueing.
// Separate session and poll maps prevent canvas-document heartbeats from overwriting live sessions.
let private sessionRegistry = ConcurrentDictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase)
let private pollRegistry = ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
let private promptQueue = ConcurrentDictionary<string, QueuedPrompt list>(StringComparer.OrdinalIgnoreCase)

let private httpClient = new HttpClient()

let private maxQueueSize = 10
let private queueTtl = TimeSpan.FromMinutes 5.0
let private livenessTtl = TimeSpan.FromSeconds 60.0

let private promptKindName =
    function
    | PromptKind.Canvas -> "canvas"
    | PromptKind.AgentPrompt -> "agent-prompt"

let internal serializePrompt (prompt: Prompt) =
    JsonSerializer.Serialize(
        {| kind = promptKindName prompt.Kind
           prompt = prompt.Text |})

let internal cleanExpired (now: DateTime) (prompts: QueuedPrompt list) =
    let cutoff = now - queueTtl
    prompts |> List.filter (fun prompt -> prompt.EnqueuedAt > cutoff)

let internal formatPostFailure statusCode (body: string) =
    $"bridge returned status={statusCode}, bodyLength={body.Length}"

let private capQueue prompts =
    let excess = List.length prompts - maxQueueSize
    if excess > 0 then prompts |> List.skip excess else prompts

let private enqueue now worktreeKey targetSessionId prompt =
    let queued =
        { EnqueuedAt = now
          TargetSessionId = targetSessionId
          Prompt = prompt }

    promptQueue.AddOrUpdate(
        worktreeKey,
        [ queued ],
        fun _ existing -> cleanExpired now existing @ [ queued ] |> capQueue)
    |> ignore

/// Which registering session a queued prompt may drain to.
///
/// A canvas prompt for an AgentDoc waits for that document's recorded author. A SystemView has no
/// stored owner: if resolution picked a target before the send failed, the queued copy stays bound
/// to that session; if nothing was reachable, it drains to the next identified session — the one
/// the queue caused to launch.
let private deliverableTo worktreeKey (sessionId: string option) (queued: QueuedPrompt) =
    match queued.Prompt.Kind, queued.Prompt.Filename with
    | PromptKind.Canvas, Some filename ->
        match CanvasDocKinds.classify filename with
        | SystemView ->
            match queued.TargetSessionId with
            | Some target -> sessionId = Some target
            | None -> Option.isSome sessionId
        | AgentDoc ->
            let owner = CanvasDocOwnership.getOwnerSync worktreeKey filename
            Option.isSome sessionId && owner = sessionId
    | _ ->
        match queued.TargetSessionId with
        | None -> true
        | Some targetSessionId -> sessionId = Some targetSessionId

let private requeue now (worktreeKey: string) (survivors: QueuedPrompt list) =
    if not (List.isEmpty survivors) then
        promptQueue.AddOrUpdate(
            worktreeKey,
            survivors,
            fun _ existing -> survivors @ cleanExpired now existing |> capQueue)
        |> ignore

let private postPrompt
    (entry: SessionEntry)
    (prompt: Prompt)
    (worktreeKey: string)
    : Async<Result<unit, unit>> =
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
                let failure = formatPostFailure (int response.StatusCode) body
                Log.log "SessionBridge" $"Prompt forward failed: {failure}"
                return Error()
        with ex ->
            Log.log "SessionBridge" $"Prompt forward error: {ex.Message}"
            return Error()
    }

let private drainQueue now (worktreeKey: string) (entry: SessionEntry) =
    match promptQueue.TryRemove(worktreeKey) with
    | false, _ -> ()
    | true, queued ->
        let deliver, survivors =
            queued
            |> cleanExpired now
            |> List.partition (deliverableTo worktreeKey entry.SessionId)

        requeue now worktreeKey survivors

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

let private nextRegisteredAt now =
    lock registrationClockLock (fun () ->
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
    let now = DateTime.UtcNow
    let sessionId = normalizeSessionId sessionId
    let worktreeKey = normalizePath worktreePath
    let registryKey = registryKeyFor worktreeKey sessionId

    let entry =
        { WorktreePath = worktreeKey
          InjectUrl = injectUrl
          SessionId = sessionId
          RegisteredAt = nextRegisteredAt now }

    sessionRegistry[registryKey] <- entry
    Log.log "SessionBridge" $"Session registered {worktreeKey} (key={registryKey}) -> {injectUrl}"
    drainQueue now worktreeKey entry

let registerPoll (worktreePath: string) =
    let now = DateTime.UtcNow
    let key = normalizePath worktreePath
    pollRegistry[key] <- now
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

let internal isSessionAlive now (entry: SessionEntry) =
    now - entry.RegisteredAt < livenessTtl

let internal isPollAlive now (lastHeartbeat: DateTime) =
    now - lastHeartbeat < livenessTtl

let internal selectLiveTarget now promptKind targetSessionId entries =
    let live = entries |> List.filter (isSessionAlive now)

    match targetSessionId, promptKind, live with
    | Some target, _, _ -> live |> List.tryFind (fun entry -> entry.SessionId = Some target)
    | None, PromptKind.AgentPrompt, [ entry ] -> Some entry
    | _ -> None

/// Attempt immediate delivery to the selected live session. A failed POST is queued for that
/// session, while an absent live target remains distinct so auto-sync can apply its fallback policy.
let private tryDeliverAt now (request: SendRequest) =
    async {
        let worktreeKey = normalizePath request.WorktreePath
        let targetSessionId = normalizeSessionId request.SessionId
        let target =
            sessionsForWorktree request.WorktreePath
            |> selectLiveTarget now request.Prompt.Kind targetSessionId

        match target with
        | None -> return AttemptNoLiveSession
        | Some entry ->
            match! postPrompt entry request.Prompt worktreeKey with
            | Ok () -> return AttemptDelivered
            | Error () ->
                enqueue now worktreeKey entry.SessionId request.Prompt
                return AttemptFailed entry
    }

let tryDeliver (request: SendRequest) =
    async {
        let now = DateTime.UtcNow

        match! tryDeliverAt now request with
        | AttemptDelivered -> return DeliveryResult.Delivered
        | AttemptNoLiveSession -> return DeliveryResult.NoLiveSession
        | AttemptFailed _ -> return DeliveryResult.DeliveryFailed
    }

let send (request: SendRequest) =
    async {
        let now = DateTime.UtcNow
        let worktreeKey = normalizePath request.WorktreePath
        let targetSessionId = normalizeSessionId request.SessionId

        match! tryDeliverAt now request with
        | AttemptDelivered -> return SendResult.Delivered
        | AttemptFailed _ -> return SendResult.Queued
        | AttemptNoLiveSession ->
            enqueue now worktreeKey targetSessionId request.Prompt
            return SendResult.Queued
    }

/// Atomically drain anonymous pending prompts of one transport kind. Canvas iframe heartbeats use
/// this for legacy owner-unknown canvas messages; owner-bound and agent prompts stay queued for a
/// matching live session registration.
let private drainPending now (kind: PromptKind) (worktreePath: string) : Prompt list =
    let key = normalizePath worktreePath

    match promptQueue.TryRemove(key) with
    | false, _ -> []
    | true, queued ->
        let deliver, survivors =
            queued
            |> cleanExpired now
            |> List.partition (fun prompt ->
                deliverableTo key None prompt && prompt.Prompt.Kind = kind)

        requeue now key survivors

        if not (List.isEmpty deliver) then
            Log.log "SessionBridge" $"Drained {List.length deliver} pending {promptKindName kind} prompt(s) for {Path.GetFileName(key)} via poll"

        deliver |> List.map _.Prompt

let drainPendingCanvas worktreePath =
    drainPending DateTime.UtcNow PromptKind.Canvas worktreePath

let internal computeLiveness now (session: SessionEntry option) (poll: bool * DateTime) =
    match session, poll with
    | Some entry, (true, heartbeat) ->
        let age =
            min
                (now - entry.RegisteredAt).TotalSeconds
                (now - heartbeat).TotalSeconds
        let liveSessionIds =
            if isSessionAlive now entry then entry.SessionId |> Option.toList else []
        Some (
            age,
            { IsAlive = isSessionAlive now entry || isPollAlive now heartbeat
              SessionId = entry.SessionId
              LiveSessionIds = liveSessionIds })
    | Some entry, (false, _) ->
        let age = (now - entry.RegisteredAt).TotalSeconds
        let liveSessionIds =
            if isSessionAlive now entry then entry.SessionId |> Option.toList else []
        Some (
            age,
            { IsAlive = isSessionAlive now entry
              SessionId = entry.SessionId
              LiveSessionIds = liveSessionIds })
    | None, (true, heartbeat) ->
        let age = (now - heartbeat).TotalSeconds
        Some (
            age,
            { IsAlive = isPollAlive now heartbeat
              SessionId = None
              LiveSessionIds = [] })
    | None, (false, _) -> None

let getStatus (worktreePath: string) =
    let now = DateTime.UtcNow
    let key = normalizePath worktreePath
    let session = freshestSession worktreePath
    let poll = pollRegistry.TryGetValue(key)

    match computeLiveness now session poll with
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
    let now = DateTime.UtcNow

    worktreePaths
    |> List.choose (fun path ->
        let key = normalizePath path
        let sessions = sessionsForWorktree path
        let session = sessions |> List.sortByDescending _.RegisteredAt |> List.tryHead
        let poll = pollRegistry.TryGetValue(key)
        let liveSessionIds =
            sessions
            |> List.filter (isSessionAlive now)
            |> List.choose _.SessionId
            |> List.distinct
            |> List.sort

        computeLiveness now session poll
        |> Option.map (fun (_, liveness) ->
            path, { liveness with LiveSessionIds = liveSessionIds }))
    |> Map.ofList
