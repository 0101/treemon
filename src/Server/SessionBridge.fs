module Server.SessionBridge

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Shared
open Server.SessionActivity

let private normalizePath = Server.PathUtils.normalizePath

[<RequireQualifiedAccess>]
type PromptKind =
    | Canvas
    | AgentPrompt
    | StartupPrompt

[<RequireQualifiedAccess>]
type Prompt =
    | Canvas of CanvasMessageRequest
    | AgentPrompt of text: string
    | StartupPrompt of text: string

    member this.Kind =
        match this with
        | Prompt.Canvas _ -> PromptKind.Canvas
        | Prompt.AgentPrompt _ -> PromptKind.AgentPrompt
        | Prompt.StartupPrompt _ -> PromptKind.StartupPrompt

    member this.Text =
        match this with
        | Prompt.Canvas request -> request.Payload
        | Prompt.AgentPrompt text -> text
        | Prompt.StartupPrompt text -> text

module Prompt =
    let canvasFor worktreePath filename text =
        Prompt.Canvas
            { WorktreePath = WorktreePath worktreePath
              Filename = filename
              Payload = text }

    let agentPrompt text = Prompt.AgentPrompt text

    let startup text = Prompt.StartupPrompt text

[<RequireQualifiedAccess>]
type SendTarget =
    /// One physical Copilot process. A same-SessionId sibling is never eligible.
    | ExactProcess of sessionId: SessionId * processIdentity: ProcessIdentity
    /// One durable conversation's latest bridge.
    | DurableSession of SessionId
    /// No prior identity is known; generic prompt delivery requires one unambiguous live owner.
    | Unspecified

module SendTarget =
    let ofSessionId =
        Option.map SendTarget.DurableSession
        >> Option.defaultValue SendTarget.Unspecified

type SendRequest =
    { WorktreePath: string
      Target: SendTarget
      Prompt: Prompt }

[<RequireQualifiedAccess>]
type DeliveryResult =
    | Delivered
    | NoLiveSession
    | DeliveryFailed

type SessionEntry =
    { ProcessIdentity: ProcessIdentity option
      WorktreePath: string
      InjectUrl: string
      SessionId: SessionId
      TerminalSessionId: TerminalSessionId option
      RegisteredAt: DateTime }

[<RequireQualifiedAccess>]
type RegistrationFailure =
    | InvalidWorktreePath
    | InvalidInjectUrl
    | InvalidShutdownUrl
    | InvalidSessionId
    | InvalidTerminalSessionId
    | InvalidShutdownCapability

type RegistrationRequest =
    { WorktreePath: string
      InjectUrl: string
      ShutdownUrl: string
      ShutdownCapability: string
      SessionId: string option
      ParentProcessId: int option
      TerminalSessionId: string option }

[<RequireQualifiedAccess>]
type SendResult =
    | Delivered
    | Queued

[<RequireQualifiedAccess>]
type internal QueuedTarget =
    | Session of SendTarget
    | LaunchingTerminal of startedAt: DateTime
    | ExactTerminal of TerminalSessionId * resolveSession: (unit -> Async<SessionId option>)

[<RequireQualifiedAccess>]
type internal StartupResult<'started> =
    | Accepted of 'started
    | LaunchFailed of string
    | PromptFailed of 'started * StartupPromptFailure

[<RequireQualifiedAccess>]
type internal PromptDelivery =
    | Ordinary of CancellationToken
    | Startup of
        TaskCompletionSource<Result<unit, StartupPromptFailure>> *
        CancellationToken

type internal QueuedPrompt =
    { Id: Guid
      EnqueuedAt: DateTime
      Target: QueuedTarget
      Prompt: Prompt
      LastFailure: (string * DateTime) option
      Delivery: PromptDelivery }

type private QueueTake =
    | QueueChanged
    | QueueMissing
    | QueueTaken of QueuedPrompt

type private DeliveryAttempt =
    | AttemptDelivered
    | AttemptNoLiveSession
    | AttemptFailed of SessionEntry

type internal DeliveryStatus =
    { QueuedMessages: int
      ActiveDrains: int
      PendingNotifications: int }

type private DrainRun =
    { Completion: TaskCompletionSource<unit>
      Requested: bool }

type private DeliveryLane(utcNow: unit -> DateTime) =
    let gate = obj ()
    // One snapshot is confined to this thread-safe queue/notification boundary; HTTP runs outside it.
    let mutable state: QueuedPrompt list * DrainRun option = [], None
    // Queue receipt order shares the same private gate, independently of bridge registration clocks.
    let mutable lastEnqueuedAt = DateTime.MinValue

    member _.UtcNow() = utcNow ()

    member _.NextEnqueuedAt() =
        lock gate (fun () ->
            let now = utcNow ()
            let timestamp = if now > lastEnqueuedAt then now else lastEnqueuedAt.AddTicks 1L
            lastEnqueuedAt <- timestamp
            timestamp)

    member _.ChangeQueue(change: QueuedPrompt list -> QueuedPrompt list * 'result) =
        lock gate (fun () ->
            let queue, run = state
            let next, result = change queue
            state <- next, run
            result)

    member _.TakeQueued(observed: QueuedPrompt, prune, canPrecede: QueuedPrompt -> bool) =
        lock gate (fun () ->
            let queue, run = state
            let live = prune queue
            match run with
            | Some active when active.Requested ->
                state <- live, Some { active with Requested = false }
                QueueChanged
            | _ ->
                let current = live |> List.tryFind (fun item -> item.Id = observed.Id)
                let hasEarlierTerminalMessage =
                    live
                    |> List.takeWhile (fun item -> item.Id <> observed.Id)
                    |> List.exists canPrecede
                match current with
                | Some item when Object.ReferenceEquals(item, observed) && not hasEarlierTerminalMessage ->
                    state <- (live |> List.filter (fun queued -> queued.Id <> observed.Id)), run
                    QueueTaken item
                | Some _ ->
                    state <- live, run
                    QueueChanged
                | None ->
                    state <- live, run
                    QueueMissing)

    member _.RequestDrain() =
        lock gate (fun () ->
            let queue, current = state
            match current with
            | Some run ->
                state <- queue, Some { run with Requested = true }
                false, run.Completion
            | None ->
                let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                state <- queue, Some { Completion = completion; Requested = false }
                true, completion)

    member _.FinishPass() =
        lock gate (fun () ->
            let queue, current = state
            match current with
            | Some run when run.Requested ->
                state <- queue, Some { run with Requested = false }
                true
            | Some run ->
                state <- queue, None
                run.Completion.TrySetResult() |> ignore
                false
            | None -> false)

    member _.FailDrain(error: exn) =
        lock gate (fun () ->
            let queue, current = state
            state <- queue, None
            current |> Option.iter (fun run -> run.Completion.TrySetException error |> ignore))

    member _.Status =
        lock gate (fun () ->
            let queue, run = state
            { QueuedMessages = queue.Length
              ActiveDrains = if run.IsSome then 1 else 0
              PendingNotifications = if run |> Option.exists _.Requested then 1 else 0 })

[<RequireQualifiedAccess>]
type ShutdownRequestOutcome =
    | Accepted
    | InvalidCapability
    | NonLoopbackRequest
    | Rejected
    | TransportFailed

[<RequireQualifiedAccess>]
type ExactProcessState =
    | Running
    | Exited
    | Reused

[<RequireQualifiedAccess>]
type ShutdownCompletion =
    | ExactClosure
    | ProcessExit

[<RequireQualifiedAccess>]
type ShutdownFailure =
    | MissingRegistration
    | LocationUnavailable
    | StaleRegistration
    | InvalidCapability
    | NonLoopbackRequest
    | Rejected
    | RequestFailed
    | TimedOut
    | VerificationFailed

type ShutdownTarget =
    { WorktreePath: string
      SessionId: SessionId
      ProcessIdentity: ProcessIdentity }

type ShutdownWaitOptions =
    { Timeout: TimeSpan
      PollInterval: TimeSpan }

type ShutdownAttempt =
    { Target: ShutdownTarget
      Outcome: Result<ShutdownCompletion, ShutdownFailure> }

type internal ShutdownDependencies =
    { SendShutdown: string -> string -> Async<ShutdownRequestOutcome>
      ClosureSnapshot: unit -> Async<Result<Set<ProcessIdentity>, string>>
      ProbeProcess:
        ProcessIdentityResolver
            -> ProcessIdentity
            -> Async<Result<ExactProcessState, string>>
      Delay: TimeSpan -> Async<unit>
      UtcNow: unit -> DateTime }

type private RegisteredSession =
    { Entry: SessionEntry
      ShutdownUrl: string
      ShutdownCapability: string
      ProcessIdentityResolver: ProcessIdentityResolver }

// Mutable: ConcurrentDictionary is the thread-safe boundary for bridge registration and queueing.
// Separate session and poll maps prevent canvas-document heartbeats from overwriting live sessions.
let private sessionRegistry = ConcurrentDictionary<SessionId, RegisteredSession>()
let private pollRegistry = ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal)
let private deliveryLanes = ConcurrentDictionary<string, DeliveryLane>(StringComparer.Ordinal)
let private registrationClockLock = obj ()
// Mutable only under registrationClockLock; receipts order bridge installation and queued messages.
let mutable private lastReceivedAt = DateTime.MinValue

let private httpClient =
    let handler = new HttpClientHandler()
    handler.AllowAutoRedirect <- false
    new HttpClient(handler, Timeout = TimeSpan.FromSeconds 5.0)

let private maxQueueSize = 10
let private queueTtl = TimeSpan.FromMinutes 5.0
let private livenessTtl = TimeSpan.FromSeconds 60.0
let private shutdownRequestTimeout = TimeSpan.FromSeconds 5.0
let internal maxConcurrentShutdownOperations = 8

let private defaultShutdownWaitOptions =
    { Timeout = TimeSpan.FromSeconds 30.0
      PollInterval = TimeSpan.FromMilliseconds 100.0 }

let private promptKindName =
    function
    | PromptKind.Canvas -> "canvas"
    | PromptKind.AgentPrompt -> "agent-prompt"
    | PromptKind.StartupPrompt -> "startup-prompt"

let internal serializePrompt (prompt: Prompt) =
    match prompt with
    | Prompt.AgentPrompt text ->
        JsonSerializer.Serialize({| kind = "agent-prompt"; prompt = text |})
    | Prompt.StartupPrompt text ->
        JsonSerializer.Serialize({| kind = "startup-prompt"; prompt = text |})
    | Prompt.Canvas request ->
        let source =
            CanvasPrompt.documentIdentityJson
                (WorktreePath.value request.WorktreePath)
                request.Filename
        let payload = JsonSerializer.Serialize request.Payload
        $"{{\"kind\":\"canvas\",\"prompt\":{payload},\"source\":{source}}}"

let internal cleanExpired (now: DateTime) (prompts: QueuedPrompt list) =
    let cutoff = now - queueTtl
    prompts
    |> List.filter (fun prompt ->
        match prompt.Delivery with
        | PromptDelivery.Ordinary token ->
            prompt.EnqueuedAt > cutoff && not token.IsCancellationRequested
        | PromptDelivery.Startup(_, token) -> not token.IsCancellationRequested)

let internal formatPostFailure statusCode (body: string) =
    $"bridge returned status={statusCode}, bodyLength={body.Length}"

let private capQueue prompts =
    let startup, ordinary =
        prompts
        |> List.partition (fun prompt ->
            match prompt.Delivery with
            | PromptDelivery.Startup _ -> true
            | PromptDelivery.Ordinary _ -> false)

    let excess = ordinary.Length - maxQueueSize
    startup @ (if excess > 0 then ordinary |> List.skip excess else ordinary)

let private deliveryLaneUsing utcNow worktreeKey =
    deliveryLanes.GetOrAdd(worktreeKey, fun _ -> DeliveryLane utcNow)

let private deliveryLane worktreeKey =
    deliveryLaneUsing (fun () -> DateTime.UtcNow) worktreeKey

let internal initializeDeliveryClock worktreePath utcNow =
    deliveryLaneUsing utcNow (normalizePath worktreePath) |> ignore

let private enqueue worktreeKey queued =
    let lane = deliveryLane worktreeKey
    lane.ChangeQueue(fun existing ->
        let next =
            queued :: existing
            |> cleanExpired (lane.UtcNow())
            |> List.distinctBy _.Id
            |> List.sortBy _.EnqueuedAt
            |> capQueue
        next, ())

let private queuedPrompts worktreeKey =
    let lane = deliveryLane worktreeKey
    lane.ChangeQueue(fun queue ->
        let live = cleanExpired (lane.UtcNow()) queue
        live, live)

let internal deliveryStatus worktreePath =
    (deliveryLane (normalizePath worktreePath)).Status

let private postPrompt
    cancellationToken
    (entry: SessionEntry)
    (prompt: Prompt)
    (worktreeKey: string)
    : Async<Result<unit, unit>> =
    async {
        try
            use content = new StringContent(serializePrompt prompt, Encoding.UTF8, "application/json")
            let! response =
                httpClient.PostAsync(entry.InjectUrl, content, cancellationToken)
                |> Async.AwaitTask
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
            Log.logException "SessionBridge" "Prompt forward failed" ex
            return Error()
    }

let private nextReceiptAt now =
    let timestamp = if now > lastReceivedAt then now else lastReceivedAt.AddTicks 1L
    lastReceivedAt <- timestamp
    timestamp

let private normalizeSessionId =
    function
    | Some sessionId when not (String.IsNullOrWhiteSpace sessionId) ->
        SessionId.create sessionId
        |> Result.mapError (fun _ -> RegistrationFailure.InvalidSessionId)
    | _ -> Error RegistrationFailure.InvalidSessionId

let private normalizeTerminalSessionId =
    function
    | Some terminalSessionId ->
        TerminalSessionId.create terminalSessionId
        |> Result.map Some
        |> Result.mapError (fun _ -> RegistrationFailure.InvalidTerminalSessionId)
    | _ -> Ok None

let internal isValidShutdownCapability (capability: string) =
    not (isNull capability)
    && capability.Length = 43
    && capability
       |> Seq.forall (fun character ->
           Char.IsAsciiLetterOrDigit character
           || character = '-'
           || character = '_')

let internal validateRegistrationRequest (request: RegistrationRequest) =
    if String.IsNullOrWhiteSpace request.WorktreePath || not (Path.IsPathFullyQualified request.WorktreePath) then
        Error RegistrationFailure.InvalidWorktreePath
    elif not (HttpSecurity.isLoopbackEndpoint request.InjectUrl) then
        Error RegistrationFailure.InvalidInjectUrl
    elif not (HttpSecurity.isLoopbackEndpoint request.ShutdownUrl) then
        Error RegistrationFailure.InvalidShutdownUrl
    elif not (isValidShutdownCapability request.ShutdownCapability) then
        Error RegistrationFailure.InvalidShutdownCapability
    else
        match normalizeSessionId request.SessionId, normalizeTerminalSessionId request.TerminalSessionId, PathUtils.tryNormalizePath request.WorktreePath with
        | Error failure, _, _
        | _, Error failure, _ -> Error failure
        | _, _, None -> Error RegistrationFailure.InvalidWorktreePath
        | Ok sessionId, Ok terminalSessionId, Some worktreeKey ->
            Ok(worktreeKey, sessionId, terminalSessionId)

let private probeExactProcess resolver identity =
    identity
    |> ProcessIdentity.processId
    |> fun processId -> ProcessIdentityResolver.resolve processId resolver
    |> Result.map (fun current ->
        match current with
        | None -> ExactProcessState.Exited
        | Some resolved when resolved = identity -> ExactProcessState.Running
        | Some _ -> ExactProcessState.Reused)

let private removeObservedRegistration
    identity
    (registration: RegisteredSession)
    =
    KeyValuePair(identity, registration)
    |> sessionRegistry.TryRemove
    |> ignore

let private isCurrentRegistration (registration: RegisteredSession) =
    match sessionRegistry.TryGetValue registration.Entry.SessionId with
    | true, current -> current.Entry = registration.Entry
    | false, _ -> false

let registerPoll (worktreePath: string) =
    let now = DateTime.UtcNow
    let key = normalizePath worktreePath
    pollRegistry[key] <- now

let sessionsForWorktree (worktreePath: string) : SessionEntry list =
    let worktreeKey = normalizePath worktreePath

    sessionRegistry
    |> Seq.filter (fun observed -> observed.Value.Entry.WorktreePath = worktreeKey)
    |> Seq.map _.Value.Entry
    |> Seq.toList

let internal isSessionAlive now (entry: SessionEntry) =
    now - entry.RegisteredAt < livenessTtl

let internal isPollAlive now (lastHeartbeat: DateTime) =
    now - lastHeartbeat < livenessTtl

let internal expireObservedAt now (entry: SessionEntry) =
    if not (isSessionAlive now entry) then
        match sessionRegistry.TryGetValue entry.SessionId with
        | true, registration when registration.Entry = entry ->
            removeObservedRegistration entry.SessionId registration
        | _ -> ()

let internal collapseLiveRegistrations now entries =
    entries
    |> List.filter (isSessionAlive now)
    |> List.groupBy _.SessionId
    |> List.map (fun (_, registrations) ->
        registrations
        |> List.sortByDescending _.RegisteredAt
        |> List.head)
    |> List.sortByDescending _.RegisteredAt

let canvasSessionsForWorktreeAt now worktreePath =
    let observed = sessionsForWorktree worktreePath
    observed |> List.iter (expireObservedAt now)
    collapseLiveRegistrations now observed

let canvasSessionsForWorktree worktreePath =
    canvasSessionsForWorktreeAt DateTime.UtcNow worktreePath

let internal selectLiveTarget now promptKind target entries =
    let live = collapseLiveRegistrations now entries

    match target, promptKind with
    | SendTarget.ExactProcess(sessionId, processIdentity), _ ->
        live
        |> List.tryFind (fun entry ->
            entry.SessionId = sessionId && entry.ProcessIdentity = Some processIdentity)
    | SendTarget.DurableSession sessionId, _ ->
        live
        |> List.tryFind (fun entry -> entry.SessionId = sessionId)
    | SendTarget.Unspecified, PromptKind.AgentPrompt ->
        match live with
        | [ entry ] -> Some entry
        | _ -> None
    | SendTarget.Unspecified, PromptKind.Canvas
    | SendTarget.Unspecified, PromptKind.StartupPrompt -> None

let private resolveRegistration worktreeKey (queued: QueuedPrompt) =
    async {
        let! target =
            match queued.Prompt with
            | Prompt.Canvas request when CanvasDocKinds.classify request.Filename = AgentDoc ->
                async {
                    let! owner = CanvasDocOwnership.getOwner worktreeKey request.Filename
                    return SendTarget.ofSessionId owner
                }
            | Prompt.Canvas _
            | Prompt.AgentPrompt _
            | Prompt.StartupPrompt _ ->
                match queued.Target with
                | QueuedTarget.ExactTerminal(_, resolveSession) ->
                    async {
                        let! sessionId = resolveSession ()
                        return SendTarget.ofSessionId sessionId
                    }
                | QueuedTarget.Session target -> async.Return target
                | QueuedTarget.LaunchingTerminal _ -> async.Return SendTarget.Unspecified

        let sourceMatches =
            match queued.Prompt with
            | Prompt.Canvas request ->
                CanvasFilename.isValid request.Filename
                && normalizePath (WorktreePath.value request.WorktreePath) = worktreeKey
            | Prompt.AgentPrompt _
            | Prompt.StartupPrompt _ -> true

        let now = DateTime.UtcNow
        let selected =
            if sourceMatches then
                canvasSessionsForWorktreeAt now worktreeKey
                |> selectLiveTarget now queued.Prompt.Kind target
            else None

        return
            selected
            |> Option.bind (fun entry ->
                match sessionRegistry.TryGetValue entry.SessionId with
                | true, registration when registration.Entry = entry ->
                    match queued.Prompt, target with
                    | Prompt.AgentPrompt _, _
                    | _, SendTarget.ExactProcess _ ->
                        let verified =
                            entry.ProcessIdentity
                            |> Option.exists (fun identity ->
                                probeExactProcess registration.ProcessIdentityResolver identity = Ok ExactProcessState.Running)
                        if verified && isCurrentRegistration registration then
                            Some registration
                        else
                            Log.log "SessionBridge" "Exact-process prompt unavailable: current location could not be verified"
                            None
                    | Prompt.Canvas _, SendTarget.DurableSession _
                    | Prompt.Canvas _, SendTarget.Unspecified
                    | Prompt.StartupPrompt _, SendTarget.DurableSession _ -> Some registration
                    | Prompt.StartupPrompt _, SendTarget.Unspecified -> None
                | _ -> None)
    }

let private failureBoundary () =
    lock registrationClockLock (fun () -> lastReceivedAt)

let private dispatch worktreeKey (registration: RegisteredSession) queued =
    async {
        let cancellationToken =
            match queued.Delivery with
            | PromptDelivery.Ordinary token -> token
            | PromptDelivery.Startup(_, token) -> token

        match! postPrompt cancellationToken registration.Entry queued.Prompt worktreeKey with
        | Ok() ->
            match queued.Delivery with
            | PromptDelivery.Ordinary _ -> ()
            | PromptDelivery.Startup(completion, _) ->
                completion.TrySetResult(Ok()) |> ignore

            return None
        | Error() ->
            removeObservedRegistration registration.Entry.SessionId registration

            match queued.Delivery with
            | PromptDelivery.Ordinary _ ->
                return
                    Some
                        { queued with
                            LastFailure = Some(registration.Entry.InjectUrl, failureBoundary ()) }
            | PromptDelivery.Startup(completion, token) ->
                let failure =
                    if token.IsCancellationRequested then StartupPromptFailure.TimedOut
                    else StartupPromptFailure.Rejected

                completion.TrySetResult(Error failure) |> ignore
                return None
    }

let private failedEndpoint (entry: SessionEntry) failures =
    failures
    |> List.tryPick (fun (url, receipt) ->
        if url = entry.InjectUrl && entry.RegisteredAt <= receipt then Some(url, receipt)
        else None)

let private takeQueued worktreeKey entry (observed: QueuedPrompt) =
    let lane = deliveryLane worktreeKey
    let canPrecede (earlier: QueuedPrompt) =
        match earlier.Target, observed.Target with
        | QueuedTarget.ExactTerminal(first, _), QueuedTarget.ExactTerminal(second, _) ->
            first = second && (failedEndpoint entry (Option.toList earlier.LastFailure)).IsNone
        | _ -> false
    lane.TakeQueued(observed, (fun pending -> cleanExpired (lane.UtcNow()) pending), canPrecede)

let private drainQueue watchedId (worktreeKey: string) =
    let outcomeFor id attempt previous =
        if watchedId = Some id then Some attempt else previous

    let rec drain outcome attempted failures remaining =
        async {
            match remaining with
            | [] ->
                let pendingIds = queuedPrompts worktreeKey |> List.map _.Id |> Set.ofList
                let newIds = Set.difference pendingIds attempted
                if Set.isEmpty newIds then return outcome
                else
                    let ordered =
                        queuedPrompts worktreeKey
                        |> List.filter (fun item -> newIds.Contains item.Id)
                        |> List.map _.Id
                    return! drain outcome (Set.intersect attempted pendingIds) failures ordered
            | id :: rest ->
                match queuedPrompts worktreeKey |> List.tryFind (fun item -> item.Id = id) with
                | None -> return! drain outcome (Set.add id attempted) failures rest
                | Some queued ->
                    match! resolveRegistration worktreeKey queued with
                    | None -> return! drain outcome (Set.add id attempted) failures rest
                    | Some registration ->
                        let blocked =
                            failures
                            |> List.tryFind (fun (url, _) -> url = registration.Entry.InjectUrl)
                            |> Option.orElseWith (fun () ->
                                failedEndpoint registration.Entry (Option.toList queued.LastFailure))
                        match blocked with
                        | Some failure ->
                            (deliveryLane worktreeKey).ChangeQueue(fun existing ->
                                existing |> List.map (fun item ->
                                    if item.Id = id then { item with LastFailure = Some failure } else item), ())
                            return! drain outcome (Set.add id attempted) failures rest
                        | None ->
                            match takeQueued worktreeKey registration.Entry queued with
                            | QueueChanged ->
                                let ordered = queuedPrompts worktreeKey |> List.map _.Id
                                return! drain outcome Set.empty failures ordered
                            | QueueMissing -> return! drain outcome (Set.add id attempted) failures rest
                            | QueueTaken current ->
                                match! dispatch worktreeKey registration current with
                                | None ->
                                    return! drain (outcomeFor id AttemptDelivered outcome) (Set.add id attempted) failures rest
                                | Some failed ->
                                    enqueue worktreeKey failed
                                    return!
                                        drain
                                            (outcomeFor id (AttemptFailed registration.Entry) outcome)
                                            attempted
                                            (Option.toList failed.LastFailure @ failures |> List.truncate maxQueueSize)
                                            (id :: rest)
        }

    drain None Set.empty [] (queuedPrompts worktreeKey |> List.map _.Id)

let private runDrain watchedId worktreeKey =
    let lane = deliveryLane worktreeKey
    let rec drain previous =
        async {
            let! outcome = drainQueue watchedId worktreeKey
            let current = outcome |> Option.orElse previous
            if lane.FinishPass() then return! drain current
            else return current
        }
    async {
        try
            return! drain None
        with error ->
            lane.FailDrain error
            return raise error
    }

let private startDrain worktreeKey =
    Async.StartWithContinuations(
        runDrain None worktreeKey |> Async.Ignore,
        ignore,
        Log.logException "SessionBridge" "Pending prompt delivery failed",
        Log.logException "SessionBridge" "Pending prompt delivery cancelled")

let retryPending worktreePath =
    let key = normalizePath worktreePath
    let started, _ = (deliveryLane key).RequestDrain()
    if started then startDrain key

let internal flushPending worktreePath =
    async {
        let key = normalizePath worktreePath
        let started, completion = (deliveryLane key).RequestDrain()
        if started then startDrain key
        do! completion.Task |> Async.AwaitTask
    }

let internal pendingPrompts worktreePath =
    let key = normalizePath worktreePath
    async.Return(queuedPrompts key)

let private removeQueued worktreeKey id =
    (deliveryLane worktreeKey).ChangeQueue(fun existing ->
        existing |> List.filter (fun queued -> queued.Id <> id), ())

/// Reserve the first prompt for one new durable session before its terminal starts.
let internal startWithPrompt timeout worktreePath sessionId prompt launch =
    async {
        let worktreeKey = normalizePath worktreePath
        let completion =
            TaskCompletionSource<Result<unit, StartupPromptFailure>>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        use deadline = new CancellationTokenSource()
        let queued =
            { Id = Guid.NewGuid()
              EnqueuedAt = (deliveryLane worktreeKey).NextEnqueuedAt()
              Target = QueuedTarget.Session(SendTarget.DurableSession sessionId)
              Prompt = Prompt.startup prompt
              LastFailure = None
              Delivery = PromptDelivery.Startup(completion, deadline.Token) }

        enqueue worktreeKey queued
        retryPending worktreeKey

        try
            match! launch () with
            | Error error -> return StartupResult.LaunchFailed error
            | Ok started ->
                deadline.CancelAfter(timeout: TimeSpan)

                try
                    let! accepted =
                        completion.Task.WaitAsync(deadline.Token) |> Async.AwaitTask

                    return
                        match accepted with
                        | Ok() -> StartupResult.Accepted started
                        | Error error -> StartupResult.PromptFailed(started, error)
                with :? OperationCanceledException when deadline.IsCancellationRequested ->
                    return StartupResult.PromptFailed(started, StartupPromptFailure.TimedOut)
        finally
            deadline.Cancel()
            removeQueued worktreeKey queued.Id
    }

let private deliver queueWhenAbsent queueCancellationToken (request: SendRequest) =
    async {
        let worktreeKey = normalizePath request.WorktreePath
        let queued =
            { Id = Guid.NewGuid()
              EnqueuedAt = (deliveryLane worktreeKey).NextEnqueuedAt()
              Target = QueuedTarget.Session request.Target
              Prompt = request.Prompt
              LastFailure = None
              Delivery = PromptDelivery.Ordinary queueCancellationToken }
        let! registration = resolveRegistration worktreeKey queued
        if registration.IsNone && not queueWhenAbsent then
            return AttemptNoLiveSession
        else
            let addressed =
                match queued.Target, queued.Prompt, registration with
                | QueuedTarget.Session SendTarget.Unspecified, Prompt.AgentPrompt _, Some registration ->
                    { queued with
                        Target = QueuedTarget.Session(
                            SendTarget.ExactProcess(registration.Entry.SessionId, registration.Entry.ProcessIdentity.Value)) }
                | _ -> queued
            enqueue worktreeKey addressed
            let started, _ = (deliveryLane worktreeKey).RequestDrain()
            if not started then
                return
                    registration
                    |> Option.map (_.Entry >> AttemptFailed)
                    |> Option.defaultValue AttemptNoLiveSession
            else
                let! outcome = runDrain (Some queued.Id) worktreeKey
                return outcome |> Option.defaultValue AttemptNoLiveSession
    }

let tryDeliver request =
    async {
        match! deliver false CancellationToken.None request with
        | AttemptDelivered -> return DeliveryResult.Delivered
        | AttemptNoLiveSession -> return DeliveryResult.NoLiveSession
        | AttemptFailed _ -> return DeliveryResult.DeliveryFailed
    }

let send queueCancellationToken request =
    async {
        match! deliver true queueCancellationToken request with
        | AttemptDelivered -> return SendResult.Delivered
        | AttemptFailed _
        | AttemptNoLiveSession -> return SendResult.Queued
    }

let private isPendingSystemView target (queued: QueuedPrompt) =
    match queued.Prompt, queued.Target, target with
    | Prompt.Canvas request, QueuedTarget.Session(SendTarget.DurableSession pending), Some expected ->
        CanvasDocKinds.classify request.Filename = SystemView && pending = expected
    | Prompt.Canvas request, QueuedTarget.Session SendTarget.Unspecified, None ->
        CanvasDocKinds.classify request.Filename = SystemView
    | _ -> false

let internal pendingSystemViewWork worktreePath =
    queuedPrompts (normalizePath worktreePath)
    |> List.choose (fun queued ->
        match queued.Prompt, queued.Target with
        | Prompt.Canvas request, QueuedTarget.Session target when CanvasDocKinds.classify request.Filename = SystemView ->
            match target with
            | SendTarget.DurableSession sessionId -> Some(queued.Id, Some sessionId)
            | SendTarget.Unspecified -> Some(queued.Id, None)
            | SendTarget.ExactProcess _ -> None
        | _ -> None)

/// Once the bridge grace ends, unresolved view interactions wait for Treemon's exact launch result.
let internal prepareSystemViewFallback worktreePath sessionId =
    let key = normalizePath worktreePath
    async {
        let entry =
            sessionId
            |> Option.bind (fun id ->
                canvasSessionsForWorktree key
                |> List.tryFind (fun bridge -> bridge.SessionId = id))
        let lane = deliveryLane key
        let prepared =
            lane.ChangeQueue(fun existing ->
                let live = cleanExpired (lane.UtcNow()) existing
                let unavailable queued =
                    isPendingSystemView sessionId queued
                    && not (entry |> Option.exists (fun bridge ->
                        failedEndpoint bridge (Option.toList queued.LastFailure) |> Option.isNone))
                let pending = live |> List.exists unavailable
                let updated =
                    live |> List.map (fun queued ->
                        if unavailable queued then { queued with Target = QueuedTarget.Session SendTarget.Unspecified }
                        else queued)
                let handled =
                    live
                    |> List.filter (isPendingSystemView sessionId)
                    |> List.map _.Id
                    |> Set.ofList
                updated, (pending, handled))
        retryPending key
        return prepared
    }

let private changePendingSystemViews worktreePath (matches: QueuedPrompt -> bool) (target: QueuedTarget) =
    let key = normalizePath worktreePath
    async {
        let lane = deliveryLane key
        let handled =
            lane.ChangeQueue(fun existing ->
                let live = cleanExpired (lane.UtcNow()) existing
                let matched = live |> List.filter matches |> List.map _.Id |> Set.ofList
                live
                |> List.map (fun item ->
                    if matches item then { item with Target = target }
                    else item), matched)
        retryPending key
        return handled
    }

let private belongsToLaunch startedAt (queued: QueuedPrompt) =
    match queued.Target with
    | QueuedTarget.LaunchingTerminal expected -> expected = startedAt
    | _ -> false

let internal reservePendingSystemViews worktreePath startedAt =
    changePendingSystemViews worktreePath (isPendingSystemView None) (QueuedTarget.LaunchingTerminal startedAt)

let internal releasePendingSystemViews worktreePath startedAt =
    changePendingSystemViews worktreePath (belongsToLaunch startedAt) (QueuedTarget.Session SendTarget.Unspecified)
    |> Async.Ignore

let internal targetPendingSystemViews worktreePath startedAt terminalId resolveSession =
    changePendingSystemViews
        worktreePath
        (belongsToLaunch startedAt)
        (QueuedTarget.ExactTerminal(terminalId, resolveSession))
    |> Async.Ignore

let private recordRegistration (diagnostics: LifecycleDiagnostics.Sink) now kind (entry: SessionEntry) =
    diagnostics (
        LifecycleDiagnostics.Diagnostic.BridgeRegistration
            { Kind = kind
              ProcessIdentity = entry.ProcessIdentity
              SessionId = entry.SessionId
              TerminalSessionId = entry.TerminalSessionId })

    let live = canvasSessionsForWorktreeAt now entry.WorktreePath
    if live.Length > 1 then
        diagnostics (
            LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved
                { Boundary = LifecycleDiagnostics.ObservationBoundary.Bridge
                  ProcessCount = live |> List.choose _.ProcessIdentity |> List.distinct |> List.length
                  SessionIds = live |> List.map _.SessionId })

let internal registerSessionWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    (processIdentityResolver: ProcessIdentityResolver)
    (request: RegistrationRequest)
    : Result<SessionEntry, RegistrationFailure> =
    match validateRegistrationRequest request with
    | Error failure -> Error failure
    | Ok(worktreeKey, sessionId, terminalSessionId) ->
        let entry, now, kind =
            lock registrationClockLock (fun () ->
                let now = DateTime.UtcNow
                let registeredAt = nextReceiptAt now
                let processIdentity =
                    request.ParentProcessId
                    |> Option.filter (fun processId -> processId > 0)
                    |> Option.bind (fun processId ->
                        match ProcessIdentityResolver.resolve processId processIdentityResolver with
                        | Ok identity -> identity
                        | Error _ ->
                            Log.log "SessionBridge" "Bridge location hint unavailable: parent identity could not be resolved"
                            None)
                let hadExisting = sessionRegistry.ContainsKey sessionId
                let entry =
                    { ProcessIdentity = processIdentity
                      WorktreePath = worktreeKey
                      InjectUrl = request.InjectUrl
                      SessionId = sessionId
                      TerminalSessionId = terminalSessionId
                      RegisteredAt = registeredAt }
                sessionRegistry[sessionId] <-
                    { Entry = entry
                      ShutdownUrl = request.ShutdownUrl
                      ShutdownCapability = request.ShutdownCapability
                      ProcessIdentityResolver = processIdentityResolver }
                entry, now,
                if hadExisting then LifecycleDiagnostics.BridgeRegistrationKind.Refreshed
                else LifecycleDiagnostics.BridgeRegistrationKind.Added)

        recordRegistration diagnostics now kind entry
        retryPending worktreeKey
        Ok entry

let registerSession =
    registerSessionWithDiagnostics LifecycleDiagnostics.write

let private sendShutdownRequest
    (shutdownUrl: string)
    (shutdownCapability: string)
    =
    async {
        try
            use timeout = new Threading.CancellationTokenSource(shutdownRequestTimeout)
            use content =
                new StringContent(
                    JsonSerializer.Serialize(
                        {| capability = shutdownCapability |}
                    ),
                    Encoding.UTF8,
                    "application/json"
                )

            let! response =
                httpClient.PostAsync(
                    shutdownUrl,
                    content,
                    timeout.Token
                )
                |> Async.AwaitTask

            use _ = response

            return
                match int response.StatusCode with
                | 202 -> ShutdownRequestOutcome.Accepted
                | 401 -> ShutdownRequestOutcome.InvalidCapability
                | 403 -> ShutdownRequestOutcome.NonLoopbackRequest
                | 409 -> ShutdownRequestOutcome.Rejected
                | _ -> ShutdownRequestOutcome.Rejected
        with _ ->
            return ShutdownRequestOutcome.TransportFailed
    }

let private defaultShutdownDependencies closureSnapshot =
    { SendShutdown = sendShutdownRequest
      ClosureSnapshot = closureSnapshot
      ProbeProcess =
        fun resolver identity ->
            async {
                return probeExactProcess resolver identity
            }
      Delay =
        fun interval ->
            let milliseconds =
                interval.TotalMilliseconds
                |> max 0.0
                |> int

            Async.Sleep milliseconds
      UtcNow = fun () -> DateTime.UtcNow }

let private shutdownRegistration target =
    match sessionRegistry.TryGetValue target.SessionId with
    | true, registration
        when registration.Entry.WorktreePath = normalizePath target.WorktreePath
             && registration.Entry.ProcessIdentity = Some target.ProcessIdentity ->
        Ok registration
    | true, _ -> Error ShutdownFailure.LocationUnavailable
    | false, _ -> Error ShutdownFailure.MissingRegistration

let private shutdownDiagnostic target registration stage =
    LifecycleDiagnostics.Diagnostic.ShutdownTransition
        { ProcessIdentity = target.ProcessIdentity
          SessionId =
            Some target.SessionId
          TerminalSessionId =
            registration
            |> Option.bind _.Entry.TerminalSessionId
          Stage = stage }

type private PendingShutdown =
    { Index: int
      Target: ShutdownTarget
      Registration: RegisteredSession }

type private ShutdownPreparation =
    | Completed of int * ShutdownAttempt
    | Accepted of PendingShutdown

type private ShutdownProbe =
    | ProbeCompleted of int * ShutdownAttempt
    | ProbePending of PendingShutdown

let private mapAsyncBounded operation items =
    let rec run completed remaining =
        async {
            match remaining with
            | [] ->
                return
                    completed
                    |> List.rev
                    |> List.collect Array.toList
            | _ ->
                let batchSize =
                    min
                        maxConcurrentShutdownOperations
                        (List.length remaining)

                let batch, rest =
                    remaining |> List.splitAt batchSize

                let! results =
                    batch
                    |> List.map operation
                    |> Async.Parallel

                return! run (results :: completed) rest
        }

    run [] items

let private attempt target outcome =
    { Target = target
      Outcome = outcome }

let private recordShutdown
    (diagnostics: LifecycleDiagnostics.Sink)
    target
    registration
    stage
    =
    diagnostics (
        shutdownDiagnostic
            target
            registration
            stage
    )

let private prepareShutdown
    (diagnostics: LifecycleDiagnostics.Sink)
    (dependencies: ShutdownDependencies)
    (index, target)
    =
    async {
        let complete outcome =
            Completed(index, attempt target outcome)

        recordShutdown
            diagnostics
            target
            None
            LifecycleDiagnostics.ShutdownStage.Requested

        match shutdownRegistration target with
        | Error failure ->
            let reason =
                match failure with
                | ShutdownFailure.LocationUnavailable -> LifecycleDiagnostics.ShutdownRejection.LocationUnavailable
                | _ -> LifecycleDiagnostics.ShutdownRejection.MissingRegistration
            recordShutdown
                diagnostics
                target
                None
                (LifecycleDiagnostics.ShutdownStage.Rejected
                    reason)

            return
                complete (
                    Error failure
                )
        | Ok registration ->
            let record =
                recordShutdown
                    diagnostics
                    target
                    (Some registration)

            match!
                dependencies.ProbeProcess
                    registration.ProcessIdentityResolver
                    target.ProcessIdentity
            with
            | Error _ ->
                record (
                    LifecycleDiagnostics.ShutdownStage.Rejected
                        LifecycleDiagnostics.ShutdownRejection.VerificationFailed
                )

                return
                    complete (
                        Error ShutdownFailure.VerificationFailed
                    )
            | Ok ExactProcessState.Exited ->
                removeObservedRegistration
                    target.SessionId
                    registration

                record LifecycleDiagnostics.ShutdownStage.CompletedProcessExit
                return complete (Ok ShutdownCompletion.ProcessExit)
            | Ok ExactProcessState.Reused ->
                removeObservedRegistration
                    target.SessionId
                    registration

                record (
                    LifecycleDiagnostics.ShutdownStage.Rejected
                        LifecycleDiagnostics.ShutdownRejection.StaleRegistration
                )

                return
                    complete (
                        Error ShutdownFailure.StaleRegistration
                    )
            | Ok ExactProcessState.Running
                when not (
                    isSessionAlive
                        (dependencies.UtcNow ())
                        registration.Entry
                ) ->
                record (
                    LifecycleDiagnostics.ShutdownStage.Rejected
                        LifecycleDiagnostics.ShutdownRejection.StaleRegistration
                )

                return
                    complete (
                        Error ShutdownFailure.StaleRegistration
                    )
            | Ok ExactProcessState.Running when not (isCurrentRegistration registration) ->
                record (
                    LifecycleDiagnostics.ShutdownStage.Rejected
                        LifecycleDiagnostics.ShutdownRejection.LocationUnavailable)
                return complete (Error ShutdownFailure.LocationUnavailable)
            | Ok ExactProcessState.Running ->
                match!
                    dependencies.SendShutdown
                        registration.ShutdownUrl
                        registration.ShutdownCapability
                with
                | ShutdownRequestOutcome.InvalidCapability ->
                    record (
                        LifecycleDiagnostics.ShutdownStage.Rejected
                            LifecycleDiagnostics.ShutdownRejection.InvalidCapability
                    )

                    return
                        complete (
                            Error ShutdownFailure.InvalidCapability
                        )
                | ShutdownRequestOutcome.NonLoopbackRequest ->
                    record (
                        LifecycleDiagnostics.ShutdownStage.Rejected
                            LifecycleDiagnostics.ShutdownRejection.NonLoopbackRequest
                    )

                    return
                        complete (
                            Error ShutdownFailure.NonLoopbackRequest
                        )
                | ShutdownRequestOutcome.Rejected ->
                    record (
                        LifecycleDiagnostics.ShutdownStage.Rejected
                            LifecycleDiagnostics.ShutdownRejection.Rejected
                    )

                    return
                        complete (
                            Error ShutdownFailure.Rejected
                        )
                | ShutdownRequestOutcome.TransportFailed ->
                    record (
                        LifecycleDiagnostics.ShutdownStage.Rejected
                            LifecycleDiagnostics.ShutdownRejection.RequestFailed
                    )

                    return
                        complete (
                            Error ShutdownFailure.RequestFailed
                        )
                | ShutdownRequestOutcome.Accepted ->
                    record LifecycleDiagnostics.ShutdownStage.RequestAccepted

                    return
                        Accepted
                            { Index = index
                              Target = target
                              Registration = registration }
    }

let private completeClosed
    (diagnostics: LifecycleDiagnostics.Sink)
    (pending: PendingShutdown)
    =
    recordShutdown
        diagnostics
        pending.Target
        (Some pending.Registration)
        LifecycleDiagnostics.ShutdownStage.CompletedExactClosure

    pending.Index,
    attempt
        pending.Target
        (Ok ShutdownCompletion.ExactClosure)

let private completeVerificationFailure
    (diagnostics: LifecycleDiagnostics.Sink)
    (pending: PendingShutdown)
    =
    recordShutdown
        diagnostics
        pending.Target
        (Some pending.Registration)
        (LifecycleDiagnostics.ShutdownStage.Rejected
            LifecycleDiagnostics.ShutdownRejection.VerificationFailed)

    pending.Index,
    attempt
        pending.Target
        (Error ShutdownFailure.VerificationFailed)

let private probePending
    (diagnostics: LifecycleDiagnostics.Sink)
    (dependencies: ShutdownDependencies)
    deadline
    now
    (pending: PendingShutdown)
    =
    async {
        let complete stage outcome =
            recordShutdown
                diagnostics
                pending.Target
                (Some pending.Registration)
                stage

            ProbeCompleted(
                pending.Index,
                attempt pending.Target outcome
            )

        match!
            dependencies.ProbeProcess
                pending.Registration.ProcessIdentityResolver
                pending.Target.ProcessIdentity
        with
        | Error _ ->
            return
                complete
                    (LifecycleDiagnostics.ShutdownStage.Rejected
                        LifecycleDiagnostics.ShutdownRejection.VerificationFailed)
                    (Error ShutdownFailure.VerificationFailed)
        | Ok ExactProcessState.Exited
        | Ok ExactProcessState.Reused ->
            removeObservedRegistration
                pending.Target.SessionId
                pending.Registration

            return
                complete
                    LifecycleDiagnostics.ShutdownStage.CompletedProcessExit
                    (Ok ShutdownCompletion.ProcessExit)
        | Ok ExactProcessState.Running when now >= deadline ->
            return
                complete
                    LifecycleDiagnostics.ShutdownStage.TimedOut
                    (Error ShutdownFailure.TimedOut)
        | Ok ExactProcessState.Running ->
            return ProbePending pending
    }

let rec private waitForShutdowns
    (diagnostics: LifecycleDiagnostics.Sink)
    (dependencies: ShutdownDependencies)
    (options: ShutdownWaitOptions)
    deadline
    pending
    completed
    =
    async {
        match pending with
        | [] -> return completed
        | _ ->
            match! dependencies.ClosureSnapshot () with
            | Error _ ->
                return
                    pending
                    |> List.map (completeVerificationFailure diagnostics)
                    |> fun failures -> failures @ completed
            | Ok closedProcesses ->
                let closed, unresolved =
                    pending
                    |> List.partition (fun current ->
                        closedProcesses.Contains current.Target.ProcessIdentity)

                let closedResults =
                    closed
                    |> List.map (completeClosed diagnostics)

                let now = dependencies.UtcNow ()

                let! probes =
                    unresolved
                    |> mapAsyncBounded (
                        probePending
                            diagnostics
                            dependencies
                            deadline
                            now
                    )

                let probeResults =
                    probes
                    |> List.choose (function
                        | ProbeCompleted(index, completed) ->
                            Some(index, completed)
                        | ProbePending _ -> None)

                let stillPending =
                    probes
                    |> List.choose (function
                        | ProbeCompleted _ -> None
                        | ProbePending current -> Some current)

                let nextCompleted =
                    probeResults @ closedResults @ completed

                match stillPending with
                | [] -> return nextCompleted
                | _ ->
                    do! dependencies.Delay options.PollInterval

                    return!
                        waitForShutdowns
                            diagnostics
                            dependencies
                            options
                            deadline
                            stillPending
                            nextCompleted
    }

let internal shutdownExactBatchWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    (dependencies: ShutdownDependencies)
    (options: ShutdownWaitOptions)
    (targets: ShutdownTarget list)
    : Async<ShutdownAttempt list> =
    async {
        let! preparations =
            targets
            |> List.indexed
            |> mapAsyncBounded (
                prepareShutdown
                    diagnostics
                    dependencies
            )

        let completed =
            preparations
            |> List.choose (function
                | Completed(index, completed) ->
                    Some(index, completed)
                | Accepted _ -> None)

        let pending =
            preparations
            |> List.choose (function
                | Completed _ -> None
                | Accepted current -> Some current)

        let! outcomes =
            match pending with
            | [] -> async.Return completed
            | _ ->
                let deadline =
                    dependencies.UtcNow () + options.Timeout

                waitForShutdowns
                    diagnostics
                    dependencies
                    options
                    deadline
                    pending
                    completed

        return
            outcomes
            |> List.sortBy fst
            |> List.map snd
    }

let internal shutdownExactBatchUsing diagnostics closureSnapshot targets =
    shutdownExactBatchWithDiagnostics
        diagnostics
        (defaultShutdownDependencies closureSnapshot)
        defaultShutdownWaitOptions
        targets

let shutdownExactBatch =
    shutdownExactBatchUsing LifecycleDiagnostics.write

let internal computeLiveness now (session: SessionEntry option) (poll: bool * DateTime) =
    match session, poll with
    | Some entry, (true, heartbeat) ->
        let age =
            min
                (now - entry.RegisteredAt).TotalSeconds
                (now - heartbeat).TotalSeconds
        let liveSessionIds =
            if isSessionAlive now entry then
                [ SessionId.value entry.SessionId ]
            else
                []
        Some (
            age,
            { IsAlive = isSessionAlive now entry || isPollAlive now heartbeat
              SessionId = Some(SessionId.value entry.SessionId)
              LiveSessionIds = liveSessionIds
              SystemViewTargetSessionId = None })
    | Some entry, (false, _) ->
        let age = (now - entry.RegisteredAt).TotalSeconds
        let liveSessionIds =
            if isSessionAlive now entry then
                [ SessionId.value entry.SessionId ]
            else
                []
        Some (
            age,
            { IsAlive = isSessionAlive now entry
              SessionId = Some(SessionId.value entry.SessionId)
              LiveSessionIds = liveSessionIds
              SystemViewTargetSessionId = None })
    | None, (true, heartbeat) ->
        let age = (now - heartbeat).TotalSeconds
        Some (
            age,
            { IsAlive = isPollAlive now heartbeat
              SessionId = None
              LiveSessionIds = []
              SystemViewTargetSessionId = None })
    | None, (false, _) -> None

let getStatus (worktreePath: string) =
    let now = DateTime.UtcNow
    let key = normalizePath worktreePath
    let session =
        canvasSessionsForWorktreeAt now worktreePath
        |> List.tryHead
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
    canvasSessionsForWorktree worktreePath
    |> List.tryHead
    |> Option.map _.SessionId

let internal getAllLivenessAt
    (systemViewTarget: string -> SessionEntry list -> string option)
    now
    (worktreePaths: string list)
    : Map<string, BridgeLiveness> =
    worktreePaths
    |> List.choose (fun path ->
        let key = normalizePath path
        let sessions = canvasSessionsForWorktreeAt now path
        let session = sessions |> List.tryHead
        let poll = pollRegistry.TryGetValue(key)
        let liveSessionIds =
            sessions
            |> List.map _.SessionId
            |> List.map SessionId.value
            |> List.sort

        let target = systemViewTarget path sessions
        let liveness =
            match computeLiveness now session poll with
            | Some(_, liveness) -> Some liveness
            | None when target.IsSome ->
                Some
                    { IsAlive = false
                      SessionId = None
                      LiveSessionIds = []
                      SystemViewTargetSessionId = target }
            | None -> None

        liveness
        |> Option.map (fun liveness ->
            path,
            { liveness with
                LiveSessionIds = liveSessionIds
                SystemViewTargetSessionId = target }))
    |> Map.ofList

let getAllLiveness (worktreePaths: string list) : Map<string, BridgeLiveness> =
    getAllLivenessAt
        (fun _ _ -> None)
        DateTime.UtcNow
        worktreePaths
