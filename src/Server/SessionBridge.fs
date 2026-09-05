module Server.SessionBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open Shared
open Server.SessionActivity

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

[<RequireQualifiedAccess>]
type SendTarget =
    /// One physical Copilot process. A same-SessionId sibling is never eligible.
    | ExactProcess of ProcessIdentity
    /// One durable conversation owner. Duplicate physical registrations collapse to the freshest.
    | DurableSession of string
    /// No prior identity is known; generic prompt delivery requires one unambiguous live owner.
    | Unspecified

module SendTarget =
    let ofSessionId =
        function
        | Some sessionId when not (String.IsNullOrWhiteSpace sessionId) ->
            SendTarget.DurableSession sessionId
        | _ -> SendTarget.Unspecified

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
    { ProcessIdentity: ProcessIdentity
      WorktreePath: string
      InjectUrl: string
      SessionId: string option
      TerminalSessionId: TerminalSessionId option
      RegisteredAt: DateTime }

[<RequireQualifiedAccess>]
type RegistrationFailure =
    | InvalidParentProcessId
    | ParentProcessNotRunning
    | ParentProcessResolutionFailed
    | ParentProcessReused
    | ParentIdentityMismatch
    | InvalidSessionId
    | InvalidTerminalSessionId
    | InvalidShutdownCapability

type RegistrationRequest =
    { WorktreePath: string
      InjectUrl: string
      ShutdownUrl: string
      ShutdownCapability: string
      SessionId: string option
      ParentProcessId: int
      TerminalSessionId: string option }

[<RequireQualifiedAccess>]
type SendResult =
    | Delivered
    | Queued

type internal QueuedPrompt =
    { EnqueuedAt: DateTime
      Target: SendTarget
      Prompt: Prompt }

type private DeliveryAttempt =
    | AttemptDelivered
    | AttemptNoLiveSession
    | AttemptFailed of SessionEntry

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
    | StaleRegistration
    | InvalidCapability
    | NonLoopbackRequest
    | Rejected
    | RequestFailed
    | TimedOut
    | VerificationFailed

type ShutdownTarget =
    { WorktreePath: string
      ProcessIdentity: ProcessIdentity }

type ShutdownWaitOptions =
    { Timeout: TimeSpan
      PollInterval: TimeSpan }

type internal ShutdownDependencies =
    { SendShutdown: string -> string -> Async<ShutdownRequestOutcome>
      IsClosed: ProcessIdentity -> Async<Result<bool, string>>
      ProbeProcess:
        ProcessIdentityResolver
            -> ProcessIdentity
            -> Result<ExactProcessState, string>
      Delay: TimeSpan -> Async<unit>
      UtcNow: unit -> DateTime }

type private RegisteredSession =
    { Entry: SessionEntry
      ShutdownUrl: string
      ShutdownCapability: string
      ProcessIdentityResolver: ProcessIdentityResolver }

// Mutable: ConcurrentDictionary is the thread-safe boundary for bridge registration and queueing.
// Separate session and poll maps prevent canvas-document heartbeats from overwriting live sessions.
let private sessionRegistry = ConcurrentDictionary<ProcessIdentity, RegisteredSession>()
let private pollRegistry = ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
let private promptQueue = ConcurrentDictionary<string, QueuedPrompt list>(StringComparer.OrdinalIgnoreCase)

let private httpClient = new HttpClient()

let private shutdownHttpClient =
    let handler = new HttpClientHandler()
    handler.AllowAutoRedirect <- false
    new HttpClient(handler)

let private maxQueueSize = 10
let private queueTtl = TimeSpan.FromMinutes 5.0
let private livenessTtl = TimeSpan.FromSeconds 60.0
let private shutdownRequestTimeout = TimeSpan.FromSeconds 5.0

let private defaultShutdownWaitOptions =
    { Timeout = TimeSpan.FromSeconds 30.0
      PollInterval = TimeSpan.FromMilliseconds 100.0 }

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

let private enqueue now worktreeKey target prompt =
    let queued =
        { EnqueuedAt = now
          Target = target
          Prompt = prompt }

    promptQueue.AddOrUpdate(
        worktreeKey,
        [ queued ],
        fun _ existing -> cleanExpired now existing @ [ queued ] |> capQueue)
    |> ignore

let private targetMatches (entry: SessionEntry option) =
    function
    | SendTarget.ExactProcess target ->
        entry |> Option.exists (fun session -> session.ProcessIdentity = target)
    | SendTarget.DurableSession target ->
        entry |> Option.exists (fun session -> session.SessionId = Some target)
    | SendTarget.Unspecified -> true

/// Which registering session a queued prompt may drain to.
///
/// A canvas prompt for an AgentDoc waits for that document's recorded author. A SystemView has no
/// stored owner: if resolution picked a target before the send failed, the queued copy stays bound
/// to that session; if nothing was reachable, it drains to the next identified session — the one
/// the queue caused to launch.
let private deliverableTo worktreeKey (entry: SessionEntry option) (queued: QueuedPrompt) =
    match queued.Prompt.Kind, queued.Prompt.Filename with
    | PromptKind.Canvas, Some filename ->
        match CanvasDocKinds.classify filename with
        | SystemView ->
            match queued.Target with
            | SendTarget.Unspecified ->
                entry |> Option.bind _.SessionId |> Option.isSome
            | target -> targetMatches entry target
        | AgentDoc ->
            let owner = CanvasDocOwnership.getOwnerSync worktreeKey filename
            entry |> Option.bind _.SessionId |> Option.exists (fun sessionId -> owner = Some sessionId)
    | _ -> targetMatches entry queued.Target

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
            |> List.partition (deliverableTo worktreeKey (Some entry))

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
    | Some sessionId when not (String.IsNullOrWhiteSpace sessionId) ->
        SessionId.create sessionId
        |> Result.map (SessionId.value >> Some)
        |> Result.mapError (fun _ -> RegistrationFailure.InvalidSessionId)
    | _ -> Ok None

let private normalizeTerminalSessionId =
    function
    | Some terminalSessionId when not (String.IsNullOrWhiteSpace terminalSessionId) ->
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

let private probeExactProcess resolver identity =
    identity
    |> ProcessIdentity.processId
    |> fun processId -> ProcessIdentityResolver.resolve processId resolver
    |> Result.map (fun current ->
        match current with
        | None -> ExactProcessState.Exited
        | Some resolved when resolved = identity -> ExactProcessState.Running
        | Some _ -> ExactProcessState.Reused)

let private sameBridgeSource
    (request: RegistrationRequest)
    (registration: RegisteredSession)
    =
    String.Equals(
        registration.ShutdownCapability,
        request.ShutdownCapability,
        StringComparison.Ordinal
    )

let private sameRegistrationMetadata
    worktreeKey
    sessionId
    terminalSessionId
    (registration: RegisteredSession)
    =
    String.Equals(
        registration.Entry.WorktreePath,
        worktreeKey,
        StringComparison.OrdinalIgnoreCase
    )
    && registration.Entry.SessionId = sessionId
    && registration.Entry.TerminalSessionId = terminalSessionId

let private recordRegistration
    (diagnostics: LifecycleDiagnostics.Sink)
    now
    kind
    (entry: SessionEntry)
    =
    let sessionId =
        entry.SessionId
        |> Option.bind LifecycleDiagnostics.trySessionId

    diagnostics (
        LifecycleDiagnostics.Diagnostic.BridgeRegistration
            { Kind = kind
              ProcessIdentity = entry.ProcessIdentity
              SessionId = sessionId
              TerminalSessionId = entry.TerminalSessionId }
    )

    let liveRegistrations =
        sessionRegistry.Values
        |> Seq.filter (fun registration ->
            String.Equals(
                registration.Entry.WorktreePath,
                entry.WorktreePath,
                StringComparison.OrdinalIgnoreCase
            )
            && now - registration.Entry.RegisteredAt < livenessTtl
            && probeExactProcess
                registration.ProcessIdentityResolver
                registration.Entry.ProcessIdentity
               = Ok ExactProcessState.Running)
        |> Seq.toList

    let sessionIds =
        liveRegistrations
        |> List.choose (fun registration ->
            registration.Entry.SessionId
            |> Option.bind LifecycleDiagnostics.trySessionId)
        |> List.distinct

    if sessionIds.Length > 1 then
        diagnostics (
            LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved
                { Boundary =
                    LifecycleDiagnostics.ObservationBoundary.Bridge
                  ProcessCount = liveRegistrations.Length
                  SessionIds = sessionIds }
        )

    match sessionId with
    | None -> ()
    | Some durableSessionId ->
        let sameSession =
            liveRegistrations
            |> List.filter (fun registration ->
                registration.Entry.SessionId = entry.SessionId)

        if sameSession.Length > 1 then
            diagnostics (
                LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved
                    { Boundary =
                        LifecycleDiagnostics.ObservationBoundary.Bridge
                      SessionId = durableSessionId
                      ProcessIdentities =
                        sameSession
                        |> List.map _.Entry.ProcessIdentity
                      TerminalSessionIds =
                        sameSession
                        |> List.choose _.Entry.TerminalSessionId
                        |> List.distinct
                      UnattributedProcessCount =
                        sameSession
                        |> List.filter _.Entry.TerminalSessionId.IsNone
                        |> List.length }
            )

let internal registerSessionWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    (processIdentityResolver: ProcessIdentityResolver)
    (request: RegistrationRequest)
    : Result<SessionEntry, RegistrationFailure> =
    if request.ParentProcessId <= 0 then
        Error RegistrationFailure.InvalidParentProcessId
    elif
        isNull request.ShutdownCapability
        || not (isValidShutdownCapability request.ShutdownCapability)
    then
        Error RegistrationFailure.InvalidShutdownCapability
    else
        match normalizeSessionId request.SessionId, normalizeTerminalSessionId request.TerminalSessionId with
        | Error failure, _
        | _, Error failure -> Error failure
        | Ok sessionId, Ok terminalSessionId ->
            match ProcessIdentityResolver.resolve request.ParentProcessId processIdentityResolver with
            | Error _ ->
                Error RegistrationFailure.ParentProcessResolutionFailed
            | Ok None ->
                Error RegistrationFailure.ParentProcessNotRunning
            | Ok(Some processIdentity) ->
                let reusedSource =
                    sessionRegistry.Values
                    |> Seq.exists (fun registration ->
                        registration.Entry.ProcessIdentity <> processIdentity
                        && sameBridgeSource request registration)

                if reusedSource then
                    Error RegistrationFailure.ParentProcessReused
                else
                    let worktreeKey = normalizePath request.WorktreePath

                    match sessionRegistry.TryGetValue processIdentity with
                    | true, existing
                        when not (
                            sameRegistrationMetadata
                                worktreeKey
                                sessionId
                                terminalSessionId
                                existing
                        ) ->
                        Error RegistrationFailure.ParentIdentityMismatch
                    | hadExisting, _ ->
                        let now = DateTime.UtcNow

                        let entry =
                            { ProcessIdentity = processIdentity
                              WorktreePath = worktreeKey
                              InjectUrl = request.InjectUrl
                              SessionId = sessionId
                              TerminalSessionId = terminalSessionId
                              RegisteredAt = nextRegisteredAt now }

                        sessionRegistry[processIdentity] <-
                            { Entry = entry
                              ShutdownUrl = request.ShutdownUrl
                              ShutdownCapability = request.ShutdownCapability
                              ProcessIdentityResolver = processIdentityResolver }

                        recordRegistration
                            diagnostics
                            now
                            (if hadExisting then
                                 LifecycleDiagnostics.BridgeRegistrationKind.Refreshed
                             else
                                 LifecycleDiagnostics.BridgeRegistrationKind.Added)
                            entry

                        drainQueue now worktreeKey entry
                        Ok entry

let registerSession =
    registerSessionWithDiagnostics LifecycleDiagnostics.write

let registerPoll (worktreePath: string) =
    let now = DateTime.UtcNow
    let key = normalizePath worktreePath
    pollRegistry[key] <- now

let sessionsForWorktree (worktreePath: string) : SessionEntry list =
    let worktreeKey = normalizePath worktreePath

    sessionRegistry.Values
    |> Seq.filter (fun registration ->
        String.Equals(
            registration.Entry.WorktreePath,
            worktreeKey,
            StringComparison.OrdinalIgnoreCase
        )
        && probeExactProcess
            registration.ProcessIdentityResolver
            registration.Entry.ProcessIdentity
           = Ok ExactProcessState.Running)
    |> Seq.map _.Entry
    |> Seq.toList

let internal isSessionAlive now (entry: SessionEntry) =
    now - entry.RegisteredAt < livenessTtl

let internal isPollAlive now (lastHeartbeat: DateTime) =
    now - lastHeartbeat < livenessTtl

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
    sessionsForWorktree worktreePath
    |> collapseLiveRegistrations now

let canvasSessionsForWorktree worktreePath =
    canvasSessionsForWorktreeAt DateTime.UtcNow worktreePath

let internal selectLiveTarget now promptKind target entries =
    let live = entries |> List.filter (isSessionAlive now)

    match target, promptKind with
    | SendTarget.ExactProcess processIdentity, _ ->
        live
        |> List.tryFind (fun entry -> entry.ProcessIdentity = processIdentity)
    | SendTarget.DurableSession sessionId, _ ->
        live
        |> List.filter (fun entry -> entry.SessionId = Some sessionId)
        |> List.sortByDescending _.RegisteredAt
        |> List.tryHead
    | SendTarget.Unspecified, PromptKind.AgentPrompt ->
        match collapseLiveRegistrations now entries with
        | [ entry ] -> Some entry
        | _ -> None
    | _ -> None

/// Attempt immediate delivery to the selected live session. A failed POST is queued for that
/// session, while an absent live target remains distinct so auto-sync can apply its fallback policy.
let private tryDeliverAt now (request: SendRequest) =
    async {
        let worktreeKey = normalizePath request.WorktreePath
        let target =
            sessionsForWorktree request.WorktreePath
            |> selectLiveTarget now request.Prompt.Kind request.Target

        match target with
        | None -> return AttemptNoLiveSession
        | Some entry ->
            match! postPrompt entry request.Prompt worktreeKey with
            | Ok () -> return AttemptDelivered
            | Error () ->
                let queuedTarget =
                    match request.Target with
                    | SendTarget.Unspecified ->
                        SendTarget.ExactProcess entry.ProcessIdentity
                    | target -> target

                enqueue now worktreeKey queuedTarget request.Prompt
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

        match! tryDeliverAt now request with
        | AttemptDelivered -> return SendResult.Delivered
        | AttemptFailed _ -> return SendResult.Queued
        | AttemptNoLiveSession ->
            enqueue now worktreeKey request.Target request.Prompt
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
                shutdownHttpClient.PostAsync(
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

let private defaultShutdownDependencies isClosed =
    { SendShutdown = sendShutdownRequest
      IsClosed = isClosed
      ProbeProcess = fun resolver identity -> probeExactProcess resolver identity
      Delay =
        fun interval ->
            let milliseconds =
                interval.TotalMilliseconds
                |> max 0.0
                |> int

            Async.Sleep milliseconds
      UtcNow = fun () -> DateTime.UtcNow }

let private shutdownRegistration target =
    match sessionRegistry.TryGetValue target.ProcessIdentity with
    | true, registration
        when String.Equals(
            registration.Entry.WorktreePath,
            normalizePath target.WorktreePath,
            StringComparison.OrdinalIgnoreCase
        ) ->
        Some registration
    | _ -> None

let private shutdownDiagnostic target registration stage =
    LifecycleDiagnostics.Diagnostic.ShutdownTransition
        { ProcessIdentity = target.ProcessIdentity
          SessionId =
            registration
            |> Option.bind _.Entry.SessionId
            |> Option.bind LifecycleDiagnostics.trySessionId
          TerminalSessionId =
            registration
            |> Option.bind _.Entry.TerminalSessionId
          Stage = stage }

let internal shutdownExactWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    (dependencies: ShutdownDependencies)
    (options: ShutdownWaitOptions)
    (target: ShutdownTarget)
    : Async<Result<ShutdownCompletion, ShutdownFailure>> =
    async {
        diagnostics (
            shutdownDiagnostic
                target
                None
                LifecycleDiagnostics.ShutdownStage.Requested
        )

        match shutdownRegistration target with
        | None ->
            diagnostics (
                shutdownDiagnostic
                    target
                    None
                    (LifecycleDiagnostics.ShutdownStage.Rejected
                        LifecycleDiagnostics.ShutdownRejection.MissingRegistration)
            )

            return Error ShutdownFailure.MissingRegistration
        | Some registration ->
            let record stage =
                diagnostics (
                    shutdownDiagnostic
                        target
                        (Some registration)
                        stage
                )

            match
                dependencies.ProbeProcess
                    registration.ProcessIdentityResolver
                    target.ProcessIdentity
            with
            | Error _ ->
                record (
                    LifecycleDiagnostics.ShutdownStage.Rejected
                        LifecycleDiagnostics.ShutdownRejection.VerificationFailed
                )

                return Error ShutdownFailure.VerificationFailed
            | Ok ExactProcessState.Exited ->
                record LifecycleDiagnostics.ShutdownStage.CompletedProcessExit
                return Ok ShutdownCompletion.ProcessExit
            | Ok ExactProcessState.Reused ->
                record (
                    LifecycleDiagnostics.ShutdownStage.Rejected
                        LifecycleDiagnostics.ShutdownRejection.StaleRegistration
                )

                return Error ShutdownFailure.StaleRegistration
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

                return Error ShutdownFailure.StaleRegistration
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

                    return Error ShutdownFailure.InvalidCapability
                | ShutdownRequestOutcome.NonLoopbackRequest ->
                    record (
                        LifecycleDiagnostics.ShutdownStage.Rejected
                            LifecycleDiagnostics.ShutdownRejection.NonLoopbackRequest
                    )

                    return Error ShutdownFailure.NonLoopbackRequest
                | ShutdownRequestOutcome.Rejected ->
                    record (
                        LifecycleDiagnostics.ShutdownStage.Rejected
                            LifecycleDiagnostics.ShutdownRejection.Rejected
                    )

                    return Error ShutdownFailure.Rejected
                | ShutdownRequestOutcome.TransportFailed ->
                    record (
                        LifecycleDiagnostics.ShutdownStage.Rejected
                            LifecycleDiagnostics.ShutdownRejection.RequestFailed
                    )

                    return Error ShutdownFailure.RequestFailed
                | ShutdownRequestOutcome.Accepted ->
                    record LifecycleDiagnostics.ShutdownStage.RequestAccepted

                    let deadline =
                        dependencies.UtcNow () + options.Timeout

                    let rec waitForCompletion () =
                        async {
                            match! dependencies.IsClosed target.ProcessIdentity with
                            | Error _ ->
                                record (
                                    LifecycleDiagnostics.ShutdownStage.Rejected
                                        LifecycleDiagnostics.ShutdownRejection.VerificationFailed
                                )

                                return Error ShutdownFailure.VerificationFailed
                            | Ok true ->
                                record LifecycleDiagnostics.ShutdownStage.CompletedExactClosure
                                return Ok ShutdownCompletion.ExactClosure
                            | Ok false ->
                                match
                                    dependencies.ProbeProcess
                                        registration.ProcessIdentityResolver
                                        target.ProcessIdentity
                                with
                                | Error _ ->
                                    record (
                                        LifecycleDiagnostics.ShutdownStage.Rejected
                                            LifecycleDiagnostics.ShutdownRejection.VerificationFailed
                                    )

                                    return Error ShutdownFailure.VerificationFailed
                                | Ok ExactProcessState.Exited
                                | Ok ExactProcessState.Reused ->
                                    record LifecycleDiagnostics.ShutdownStage.CompletedProcessExit
                                    return Ok ShutdownCompletion.ProcessExit
                                | Ok ExactProcessState.Running
                                    when dependencies.UtcNow () >= deadline ->
                                    record LifecycleDiagnostics.ShutdownStage.TimedOut
                                    return Error ShutdownFailure.TimedOut
                                | Ok ExactProcessState.Running ->
                                    do! dependencies.Delay options.PollInterval
                                    return! waitForCompletion ()
                        }

                    return! waitForCompletion ()
    }

let internal shutdownExactWith =
    shutdownExactWithDiagnostics LifecycleDiagnostics.write

/// Request routine SDK shutdown for one exact registered process. Endpoint acceptance only starts
/// the wait; success requires the activity owner to report exact closure or the shared process
/// resolver to prove that exact PID/start identity exited.
let internal shutdownExactUsing diagnostics isClosed target =
    shutdownExactWithDiagnostics
        diagnostics
        (defaultShutdownDependencies isClosed)
        defaultShutdownWaitOptions
        target

let shutdownExact =
    shutdownExactUsing LifecycleDiagnostics.write

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
    |> Option.bind _.SessionId

let getAllLiveness (worktreePaths: string list) : Map<string, BridgeLiveness> =
    let now = DateTime.UtcNow

    worktreePaths
    |> List.choose (fun path ->
        let key = normalizePath path
        let sessions = canvasSessionsForWorktreeAt now path
        let session = sessions |> List.tryHead
        let poll = pollRegistry.TryGetValue(key)
        let liveSessionIds =
            sessions
            |> List.choose _.SessionId
            |> List.sort

        computeLiveness now session poll
        |> Option.map (fun (_, liveness) ->
            path, { liveness with LiveSessionIds = liveSessionIds }))
    |> Map.ofList
