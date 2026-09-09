module Tests.SessionBridgeTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Server.SessionBridge
open Server.SessionActivity
open Tests.TestUtils

let private clock = DateTime(2042, 7, 23, 12, 0, 0, DateTimeKind.Utc)
let private livenessTtl = TimeSpan.FromSeconds 60.0
let private queueTtl = TimeSpan.FromMinutes 5.0
let private injectUrl = "http://127.0.0.1:1/inject"
let private listenerTimeout = TimeSpan.FromSeconds 5.0

/// A synthetic registration observed `ageSeconds` before the fixed clock snapshot. Only the fields
/// the bridge reasons about (identity, durable session, registration age) vary between scenarios.
let private registrationAged processId ageSeconds sessionId =
    { ProcessIdentity = syntheticProcessIdentityForProcessId processId
      WorktreePath = "worktree"
      InjectUrl = injectUrl
      SessionId = sessionId |> Option.map SessionId
      TerminalSessionId = None
      RegisteredAt = clock - TimeSpan.FromSeconds(float ageSeconds) }

// Tests share the module-level bridge registry, so each synthetic physical process needs a unique
// exact identity even when several tests intentionally reuse one durable SessionId.
let mutable private nextProcessId = 92000

let private nextIdentity () =
    let processId = Interlocked.Increment(&nextProcessId)
    ProcessIdentity.create processId (int64 processId * 1000L + 1L) |> Result.defaultWith invalidOp

let private reusedIdentity identity =
    ProcessIdentity.create
        (ProcessIdentity.processId identity)
        (ProcessIdentity.processStartTimeUtcTicks identity + 1L)
    |> Result.defaultWith invalidOp

let private validRequest identity path sessionId =
    bridgeRegistrationRequest identity path injectUrl sessionId None (fakeShutdownCapability 'A' identity)

let private registerOrFail resolver request =
    registerSession resolver request
    |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}")

/// Prompt and shutdown transport run against real loopback listeners so the trust boundary — the
/// posted URL, body and opaque capability — stays observable rather than stubbed out.
let private withBridges count (run: (HttpListener * string) list -> unit) =
    let bridges =
        getFreeTcpPorts count
        |> List.map (fun port ->
            let listener = new HttpListener()
            let url = $"http://127.0.0.1:{port}/"
            listener.Prefixes.Add url
            listener.Start()
            listener, url)

    try
        run bridges
    finally
        bridges |> List.iter (fun (listener, _) -> (listener :> IDisposable).Dispose())

let private await (task: Task<'a>) = task.WaitAsync(listenerTimeout).GetAwaiter().GetResult()

let private respond status (context: HttpListenerContext) =
    context.Response.StatusCode <- status
    context.Response.Close()

let private readBody (context: HttpListenerContext) =
    use reader = new StreamReader(context.Request.InputStream)
    reader.ReadToEnd()

let private deliverAgentPrompt path target text =
    tryDeliver { WorktreePath = path; Target = target; Prompt = Prompt.agentPrompt text } |> Async.StartAsTask

let private sendPrompt path prompt =
    send { WorktreePath = path; Target = SendTarget.Unspecified; Prompt = prompt }
    |> Async.RunSynchronously

[<RequireQualifiedAccess>]
type ClockProbe =
    | QueueTtl of enqueuedAt: DateTime
    | SessionLiveness of registeredAt: DateTime
    | PollLiveness of heartbeat: DateTime

type ClockScenario = { Name: string; Probe: ClockProbe; Survives: bool }

let private observeClock =
    function
    | ClockProbe.QueueTtl enqueuedAt ->
        let queued = { EnqueuedAt = enqueuedAt; Target = SendTarget.Unspecified; Prompt = Prompt.agentPrompt "q" }
        cleanExpired clock [ queued ] |> List.isEmpty |> not
    | ClockProbe.SessionLiveness registeredAt ->
        isSessionAlive clock { registrationAged 90001 0 (Some "clock") with RegisteredAt = registeredAt }
    | ClockProbe.PollLiveness heartbeat -> isPollAlive clock heartbeat

let private clockScenarios =
    [ ClockProbe.QueueTtl, queueTtl, "a queued prompt"
      ClockProbe.SessionLiveness, livenessTtl, "a session registration"
      ClockProbe.PollLiveness, livenessTtl, "a poll heartbeat" ]
    |> List.collect (fun (probe, ttl, subject) ->
        [ { Name = $"{subject} one tick inside the TTL survives"
            Probe = probe (clock - ttl + TimeSpan.FromTicks 1L)
            Survives = true }
          { Name = $"{subject} exactly at the TTL threshold expires"
            Probe = probe (clock - ttl)
            Survives = false } ])

type LivenessScenario =
    { Name: string
      SessionAge: (int * string option) option
      Poll: bool * int
      Expected: (float * BridgeLiveness) option }

let private livenessScenarios =
    let case name sessionAge poll expected =
        { Name = name; SessionAge = sessionAge; Poll = poll; Expected = expected }

    let liveness isAlive sessionId liveSessionIds =
        { IsAlive = isAlive; SessionId = sessionId; LiveSessionIds = liveSessionIds }

    [ case "no session and no poll registration is unregistered" None (false, 0) None
      case "a poll heartbeat alone reports poll liveness with no session id" None (true, 10) (Some(10.0, liveness true None []))
      case "a live session alone reports its durable session id as live" (Some(10, Some "durable")) (false, 0)
          (Some(10.0, liveness true (Some "durable") [ "durable" ]))
      case "an anonymous live session is alive but reports no session id" (Some(10, None)) (false, 0)
          (Some(10.0, liveness true None []))
      case "a stale session alone is not alive and lists no live session id" (Some(90, Some "durable")) (false, 0)
          (Some(90.0, liveness false (Some "durable") []))
      case "a live poll keeps a stale session alive but not live, on one clock snapshot" (Some(90, Some "durable"))
          (true, 10) (Some(10.0, liveness true (Some "durable") []))
      case "the reported age is the fresher of the session and the poll heartbeat" (Some(5, Some "durable")) (true, 30)
          (Some(5.0, liveness true (Some "durable") [ "durable" ])) ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ClockTests() =

    static member ClockCases: TestCaseData seq =
        clockScenarios |> Seq.map (fun scenario -> TestCaseData(scenario).SetName scenario.Name)

    static member LivenessCases: TestCaseData seq =
        livenessScenarios |> Seq.map (fun scenario -> TestCaseData(scenario).SetName scenario.Name)

    [<TestCaseSource("ClockCases")>]
    member _.``TTL boundaries use the supplied clock snapshot``(scenario: ClockScenario) =
        Assert.That(observeClock scenario.Probe, Is.EqualTo scenario.Survives)

    [<TestCaseSource("LivenessCases")>]
    member _.``combined session and poll liveness``(scenario: LivenessScenario) =
        let session = scenario.SessionAge |> Option.map (fun (age, id) -> registrationAged 90002 age id)
        let registered, pollAge = scenario.Poll
        let poll = registered, clock - TimeSpan.FromSeconds(float pollAge)

        Assert.That(computeLiveness clock session poll, Is.EqualTo scenario.Expected)

/// A rejection scenario owns the whole registration sequence so that two-step rejections (a reused
/// pid, a changed durable identity) share one runner with the single-step validation rejections.
type RegistrationRejection =
    { Name: string
      Reject: ProcessIdentity -> RegistrationRequest -> Result<SessionEntry, RegistrationFailure>
      Expected: RegistrationFailure
      RetainsOriginal: bool }

let private rejectionScenarios =
    let invalid name mutate expected =
        { Name = name
          Reject = fun identity request -> registerSession (exactIdentityResolver identity) (mutate request)
          Expected = expected
          RetainsOriginal = false }

    let unresolvable name resolve expected =
        { Name = name
          Reject = fun _ request -> registerSession (ProcessIdentityResolver.create resolve) request
          Expected = expected
          RetainsOriginal = false }

    let afterRegistering name mutate =
        { Name = name
          Reject =
            fun identity request ->
                registerOrFail (exactIdentityResolver identity) request |> ignore
                registerSession (exactIdentityResolver identity) (mutate request)
          Expected = RegistrationFailure.ParentIdentityMismatch
          RetainsOriginal = true }

    [ invalid "a non-positive parent process id is rejected" (fun request -> { request with ParentProcessId = 0 })
          RegistrationFailure.InvalidParentProcessId
      invalid "a malformed durable session id is rejected"
          (fun request -> { request with SessionId = Some "session with spaces" })
          RegistrationFailure.InvalidSessionId
      invalid "a malformed terminal session id is rejected"
          (fun request -> { request with TerminalSessionId = Some "not-a-terminal-id" })
          RegistrationFailure.InvalidTerminalSessionId
      invalid "a shutdown capability of the wrong shape is rejected"
          (fun request -> { request with ShutdownCapability = "too-short" })
          RegistrationFailure.InvalidShutdownCapability
      unresolvable "a parent process that is no longer running is rejected" (fun _ -> Ok None)
          RegistrationFailure.ParentProcessNotRunning
      unresolvable "a parent process that cannot be probed is rejected" (fun _ -> Error "probe failed")
          RegistrationFailure.ParentProcessResolutionFailed
      { Name = "a reused pid heartbeat cannot re-key one bridge capability to another process"
        Reject =
          fun identity request ->
              // Mutable: the injected resolver models the operating system handing one pid to a
              // different process between the first and the second heartbeat.
              let mutable current = identity

              let resolver =
                  ProcessIdentityResolver.create (fun processId ->
                      if processId = ProcessIdentity.processId identity then Ok(Some current) else Ok None)

              registerOrFail resolver request |> ignore
              current <- reusedIdentity identity
              registerSession resolver request
        Expected = RegistrationFailure.ParentProcessReused
        RetainsOriginal = false }
      afterRegistering "a registered process cannot change its durable session id" (fun request ->
          { request with SessionId = Some "session-other" })
      afterRegistering "a registered process cannot change its worktree" (fun request ->
          { request with WorktreePath = uniquePath "moved-worktree" })
      afterRegistering "a registered process cannot change its terminal origin" (fun request ->
          { request with TerminalSessionId = Some(Guid.NewGuid().ToString "N") }) ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ExactRegistrationTests() =

    static member RejectionCases: TestCaseData seq =
        rejectionScenarios |> Seq.map (fun scenario -> TestCaseData(scenario).SetName scenario.Name)

    [<Test>]
    member _.``Registration stores the resolver's exact identity and normalized session metadata``() =
        let identity = nextIdentity ()
        let path = uniquePath "exact-registration"
        let terminal = "ABCDEF0123456789ABCDEF0123456789"

        let entry =
            { validRequest identity path (Some "session.exact:1") with TerminalSessionId = Some terminal }
            |> registerOrFail (exactIdentityResolver identity)

        Assert.Multiple(fun () ->
            Assert.That(
                (entry.ProcessIdentity, entry.SessionId, entry.TerminalSessionId, entry.InjectUrl),
                Is.EqualTo(
                    (identity, Some(SessionId "session.exact:1"),
                     Some(TerminalSessionId(terminal.ToLowerInvariant())), injectUrl))
            )
            Assert.That(sessionsForWorktree path |> List.map _.ProcessIdentity, Is.EqualTo [ identity ]))

    [<TestCaseSource("RejectionCases")>]
    member _.``Registration rejection leaves the worktree registry unchanged``(scenario: RegistrationRejection) =
        let identity = nextIdentity ()
        let path = uniquePath "rejected-registration"
        let sessionId = $"session-original-{Guid.NewGuid():N}"
        let actual = scenario.Reject identity (validRequest identity path (Some sessionId))
        let survivors = sessionsForWorktree path |> List.choose _.SessionId |> List.map SessionId.value
        let expected: Result<SessionEntry, RegistrationFailure> = Error scenario.Expected

        Assert.Multiple(fun () ->
            Assert.That(actual, Is.EqualTo expected)
            Assert.That(survivors, Is.EqualTo(if scenario.RetainsOriginal then [ sessionId ] else [])))

    [<Test>]
    member _.``Worktree lookup prunes exited and reused exact registrations without re-probing``() =
        let path = uniquePath "registration-pruning"
        let exited, original = nextIdentity (), nextIdentity ()
        let states = ConcurrentDictionary<int, ProcessIdentity option>()
        let probes = ConcurrentDictionary<int, int>()

        let resolver =
            ProcessIdentityResolver.create (fun processId ->
                probes.AddOrUpdate(processId, 1, fun _ count -> count + 1) |> ignore

                match states.TryGetValue processId with
                | true, current -> Ok current
                | false, _ -> Ok None)

        let register identity =
            states[ProcessIdentity.processId identity] <- Some identity

            validRequest identity path None
            |> registerSessionWithDiagnostics ignore resolver
            |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}")
            |> ignore

        register exited
        register original
        states[ProcessIdentity.processId exited] <- None
        states[ProcessIdentity.processId original] <- Some(reusedIdentity original)

        let probeCounts () =
            [ exited; original ] |> List.map (fun identity -> probes[ProcessIdentity.processId identity])

        Assert.That(sessionsForWorktree path, Is.Empty)
        let afterPrune = probeCounts ()

        Assert.Multiple(fun () ->
            Assert.That(sessionsForWorktree path, Is.Empty)
            Assert.That(
                probeCounts (),
                Is.EqualTo afterPrune,
                "A pruned terminal registration must not be retained or probed again"
            ))

    [<Test>]
    member _.``Bridge diagnostics distinguish refreshes normal sessions and duplicate physical registrations``() =
        let path = uniquePath "registration-diagnostics"
        let sharedSession, independentSession = $"shared-{Guid.NewGuid():N}", $"independent-{Guid.NewGuid():N}"
        let firstIdentity, secondIdentity, thirdIdentity = nextIdentity (), nextIdentity (), nextIdentity ()
        let firstTerminal, secondTerminal = Guid.NewGuid().ToString "N", Guid.NewGuid().ToString "N"
        let diagnostics = ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

        let register identity sessionId terminalId =
            { validRequest identity path (Some sessionId) with TerminalSessionId = Some terminalId }
            |> registerSessionWithDiagnostics diagnostics.Enqueue (exactIdentityResolver identity)
            |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}")
            |> ignore

        register firstIdentity sharedSession firstTerminal
        register secondIdentity sharedSession secondTerminal
        register firstIdentity sharedSession firstTerminal
        register thirdIdentity independentSession firstTerminal

        let events = diagnostics.ToArray()
        let bridgeBoundary = LifecycleDiagnostics.ObservationBoundary.Bridge
        let lastOf chooser = events |> Array.choose chooser |> Array.last

        let registrationKinds =
            events
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.BridgeRegistration registration -> Some registration.Kind
                | _ -> None)

        let sameSession =
            lastOf (function
                | LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved observed when
                    observed.Boundary = bridgeBoundary -> Some observed
                | _ -> None)

        let multipleSessions =
            lastOf (function
                | LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved observed when
                    observed.Boundary = bridgeBoundary -> Some observed
                | _ -> None)

        Assert.That(
            (registrationKinds,
             sameSession.ProcessIdentities |> List.sortBy ProcessIdentity.processId,
             sameSession.TerminalSessionIds |> List.map TerminalSessionId.value |> List.sort,
             multipleSessions.SessionIds |> List.map SessionId.value |> List.sort),
            Is.EqualTo(
                ([| LifecycleDiagnostics.BridgeRegistrationKind.Added
                    LifecycleDiagnostics.BridgeRegistrationKind.Added
                    LifecycleDiagnostics.BridgeRegistrationKind.Refreshed
                    LifecycleDiagnostics.BridgeRegistrationKind.Added |],
                 [ firstIdentity; secondIdentity ] |> List.sortBy ProcessIdentity.processId,
                 [ firstTerminal; secondTerminal ] |> List.sort,
                 [ sharedSession; independentSession ] |> List.sort)
            )
        )

type SelectionScenario =
    { Name: string
      Entries: SessionEntry list
      Kind: PromptKind
      Target: SendTarget
      Expected: SessionEntry option }

let private freshFirst = registrationAged 7001 1 (Some "shared")
let private freshSecond = registrationAged 7002 2 (Some "shared")
let private otherSession = registrationAged 7003 3 (Some "other")
let private staleSession = registrationAged 7004 60 (Some "stale")
let private anonymous = registrationAged 7005 4 None

let private selectionScenarios =
    let selects name entries target expected =
        { Name = name; Entries = entries; Kind = PromptKind.AgentPrompt; Target = target; Expected = expected }

    let exact (entry: SessionEntry) = SendTarget.ExactProcess entry.ProcessIdentity
    let durable sessionId = SendTarget.DurableSession(SessionId sessionId)

    [ selects "an exact target addresses that physical process" [ freshFirst; freshSecond ] (exact freshFirst)
          (Some freshFirst)
      selects "an exact target never addresses a same-SessionId sibling" [ freshSecond ] (exact freshFirst) None
      selects "an exact target ignores a stale registration" [ staleSession ] (exact staleSession) None
      selects "an exact target addresses a registration without a durable session id" [ anonymous ] (exact anonymous)
          (Some anonymous)
      selects "a durable session target picks the freshest duplicate physical registration"
          [ freshSecond; freshFirst ] (durable "shared") (Some freshFirst)
      selects "a durable session target ignores a stale registration" [ staleSession ] (durable "stale") None
      selects "a durable session target ignores another durable session" [ otherSession ] (durable "shared") None
      selects "an untargeted agent prompt uses the only live session" [ freshFirst ] SendTarget.Unspecified
          (Some freshFirst)
      selects "an untargeted agent prompt collapses duplicate registrations of one session"
          [ freshFirst; freshSecond ] SendTarget.Unspecified (Some freshFirst)
      selects "an untargeted agent prompt stays ambiguous across two live sessions" [ freshFirst; otherSession ]
          SendTarget.Unspecified None
      selects "an untargeted agent prompt ignores stale registrations" [ freshFirst; staleSession ]
          SendTarget.Unspecified (Some freshFirst)
      selects "an untargeted agent prompt has no target without registrations" [] SendTarget.Unspecified None
      { selects "an untargeted canvas prompt has no automatic target" [ freshFirst ] SendTarget.Unspecified None with
          Kind = PromptKind.Canvas } ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TargetSelectionTests() =

    static member SelectionCases: TestCaseData seq =
        selectionScenarios |> Seq.map (fun scenario -> TestCaseData(scenario).SetName scenario.Name)

    [<TestCaseSource("SelectionCases")>]
    member _.``live target selection``(scenario: SelectionScenario) =
        Assert.That(selectLiveTarget clock scenario.Kind scenario.Target scenario.Entries, Is.EqualTo scenario.Expected)

type PromptWireScenario = { Name: string; Prompt: Prompt; Json: string }
type QueueDrainScenario = { Name: string; Prompt: Prompt; Drained: Prompt list }

let private canvasPayload = """{"action":"refresh"}"""

let private promptWireScenarios =
    [ { Name = "a canvas prompt has an explicit canvas kind"
        Prompt = Prompt.canvas canvasPayload
        Json = """{"kind":"canvas","prompt":"{\u0022action\u0022:\u0022refresh\u0022}"}""" }
      { Name = "a generic agent prompt has an explicit agent-prompt kind"
        Prompt = Prompt.agentPrompt "Sync with upstream/main when safe."
        Json = """{"kind":"agent-prompt","prompt":"Sync with upstream/main when safe."}""" } ]

let private queueDrainScenarios =
    [ { Name = "an anonymous canvas prompt queues for canvas polling"
        Prompt = Prompt.canvas canvasPayload
        Drained = [ Prompt.canvas canvasPayload ] }
      { Name = "a canvas heartbeat drain does not consume generic agent prompts"
        Prompt = Prompt.agentPrompt "sync"
        Drained = [] } ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type PromptTransportTests() =

    static member WireCases: TestCaseData seq =
        promptWireScenarios |> Seq.map (fun scenario -> TestCaseData(scenario).SetName scenario.Name)

    static member DrainCases: TestCaseData seq =
        queueDrainScenarios |> Seq.map (fun scenario -> TestCaseData(scenario).SetName scenario.Name)

    [<TestCaseSource("WireCases")>]
    member _.``prompt transport kinds stay explicit on the wire``(scenario: PromptWireScenario) =
        Assert.That(serializePrompt scenario.Prompt, Is.EqualTo scenario.Json)

    [<TestCaseSource("DrainCases")>]
    member _.``queued prompt draining by transport kind``(scenario: QueueDrainScenario) =
        let path = uniquePath "queue-drain"

        Assert.Multiple(fun () ->
            Assert.That(sendPrompt path scenario.Prompt, Is.EqualTo SendResult.Queued)
            Assert.That(drainPendingCanvas path, Is.EqualTo scenario.Drained))

    [<Test>]
    member _.``An untargeted agent prompt is posted to the only live bridge``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = uniquePath "unique-live"
            registerExactSession 'A' (nextIdentity ()) path url (Some $"session-{Guid.NewGuid():N}") None |> ignore

            let received = listener.GetContextAsync()
            let delivery = deliverAgentPrompt path SendTarget.Unspecified "sync"
            let context = await received
            let body = readBody context
            respond 200 context

            Assert.Multiple(fun () ->
                Assert.That(delivery.GetAwaiter().GetResult(), Is.EqualTo DeliveryResult.Delivered)
                Assert.That(body, Is.EqualTo(serializePrompt (Prompt.agentPrompt "sync")))))

    [<Test>]
    member _.``An exact queued prompt cannot drain to a same-SessionId sibling``() =
        withBridges 3 (fun bridges ->
            let (failed, failedUrl), (sibling, siblingUrl), (recovered, recoveredUrl) =
                match bridges with
                | [ first; second; third ] -> first, second, third
                | other -> failwith $"expected three bridges, got {other.Length}"

            let path = uniquePath "exact-queue"
            let sessionId = Some $"shared-{Guid.NewGuid():N}"
            let targetIdentity, siblingIdentity = nextIdentity (), nextIdentity ()
            let register identity url = registerExactSession 'A' identity path url sessionId None |> ignore

            register targetIdentity failedUrl
            register siblingIdentity siblingUrl

            let failedRequest = failed.GetContextAsync()
            let delivery = deliverAgentPrompt path (SendTarget.ExactProcess targetIdentity) "retry-exact"
            await failedRequest |> respond 503

            Assert.That(delivery.GetAwaiter().GetResult(), Is.EqualTo DeliveryResult.DeliveryFailed)

            let siblingRequest = sibling.GetContextAsync()
            let recoveredRequest = recovered.GetContextAsync()
            register siblingIdentity siblingUrl
            register targetIdentity recoveredUrl

            Assert.That(
                Object.ReferenceEquals(await (Task.WhenAny(recoveredRequest, siblingRequest)), recoveredRequest),
                Is.True,
                "Re-registering the sibling must leave the exact-target queue untouched"
            )

            await recoveredRequest |> respond 200)

    [<Test>]
    member _.``The prompt queue is capped at the most recent prompts``() =
        let path = uniquePath "queue-cap"
        let prompts = [ 1..12 ] |> List.map (fun index -> Prompt.canvas $"""{{"n":{index}}}""")
        prompts |> List.iter (sendPrompt path >> ignore)

        Assert.That(drainPendingCanvas path, Is.EqualTo(prompts |> List.skip 2))

    [<Test>]
    member _.``Bridge failure formatting excludes the response body``() =
        let secretBody = $"first line{Environment.NewLine}secret-token=abc123"
        let failure = formatPostFailure 503 secretBody

        Assert.Multiple(fun () ->
            Assert.That(failure, Does.Contain "status=503")
            Assert.That(failure, Does.Contain $"bodyLength={secretBody.Length}")
            Assert.That(failure, Does.Not.Contain "first line")
            Assert.That(failure, Does.Not.Contain "secret-token"))

[<RequireQualifiedAccess>]
type ClosureObservation =
    | NoneClosed
    | TargetClosed
    | Unavailable

type ShutdownObservation =
    { Outcome: Result<ShutdownCompletion, ShutdownFailure>
      Stages: string list
      Requests: int }

type ShutdownScenario =
    { Name: string
      Registered: bool
      SessionExpired: bool
      /// Probe answers consumed in order; the last answer repeats for every later poll.
      Probes: Result<ExactProcessState, string> list
      Closure: ClosureObservation
      Request: ShutdownRequestOutcome
      Expected: ShutdownObservation }

let private stageName =
    let rejectionName =
        function
        | LifecycleDiagnostics.ShutdownRejection.MissingRegistration -> "missing-registration"
        | LifecycleDiagnostics.ShutdownRejection.StaleRegistration -> "stale-registration"
        | LifecycleDiagnostics.ShutdownRejection.InvalidCapability -> "invalid-capability"
        | LifecycleDiagnostics.ShutdownRejection.NonLoopbackRequest -> "non-loopback"
        | LifecycleDiagnostics.ShutdownRejection.Rejected -> "rejected"
        | LifecycleDiagnostics.ShutdownRejection.RequestFailed -> "request-failed"
        | LifecycleDiagnostics.ShutdownRejection.VerificationFailed -> "verification-failed"

    function
    | LifecycleDiagnostics.ShutdownStage.Requested -> "requested"
    | LifecycleDiagnostics.ShutdownStage.RequestAccepted -> "accepted"
    | LifecycleDiagnostics.ShutdownStage.CompletedExactClosure -> "closed"
    | LifecycleDiagnostics.ShutdownStage.CompletedProcessExit -> "exited"
    | LifecycleDiagnostics.ShutdownStage.TimedOut -> "timed-out"
    | LifecycleDiagnostics.ShutdownStage.Rejected rejection -> $"rejected:{rejectionName rejection}"

let private runShutdownScenario (scenario: ShutdownScenario) =
    let identity = nextIdentity ()
    let path = uniquePath "exact-shutdown"

    let registeredAt =
        if scenario.Registered then
            (registerExactSession 'A' identity path injectUrl None None).RegisteredAt
        else
            DateTime.UtcNow

    // Mutable: the injected clock, probe sequence and request counter are the impure operating
    // system and transport boundaries this scenario table drives.
    let mutable now = if scenario.SessionExpired then registeredAt + livenessTtl else registeredAt
    let mutable probes = scenario.Probes
    let mutable requests = 0
    let diagnostics = ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

    let closure =
        match scenario.Closure with
        | ClosureObservation.NoneClosed -> Ok Set.empty
        | ClosureObservation.TargetClosed -> Ok(Set.singleton identity)
        | ClosureObservation.Unavailable -> Error "closure snapshot unavailable"

    let nextProbe () =
        match probes with
        | [] -> Ok ExactProcessState.Running
        | [ last ] -> last
        | next :: rest ->
            probes <- rest
            next

    let dependencies: ShutdownDependencies =
        { SendShutdown =
            fun _ _ ->
                async {
                    requests <- requests + 1
                    return scenario.Request
                }
          ClosureSnapshot = fun () -> async { return closure }
          ProbeProcess = fun _ _ -> async { return nextProbe () }
          Delay = fun interval -> async { now <- now + interval }
          UtcNow = fun () -> now }

    let waitOptions: ShutdownWaitOptions =
        { Timeout = TimeSpan.FromSeconds 2.0; PollInterval = TimeSpan.FromSeconds 1.0 }

    let attempts =
        shutdownExactBatchWithDiagnostics
            diagnostics.Enqueue
            dependencies
            waitOptions
            [ { WorktreePath = path; ProcessIdentity = identity } ]
        |> Async.RunSynchronously

    { Outcome = attempts |> List.exactlyOne |> _.Outcome
      Requests = requests
      Stages =
        diagnostics.ToArray()
        |> Array.toList
        |> List.choose (function
            | LifecycleDiagnostics.Diagnostic.ShutdownTransition shutdown -> Some(stageName shutdown.Stage)
            | _ -> None) }

let private acceptedRunning =
    { Name = ""
      Registered = true
      SessionExpired = false
      Probes = [ Ok ExactProcessState.Running ]
      Closure = ClosureObservation.NoneClosed
      Request = ShutdownRequestOutcome.Accepted
      Expected =
        { Outcome = Error ShutdownFailure.TimedOut
          Stages = [ "requested"; "accepted"; "timed-out" ]
          Requests = 1 } }

let private shutdownScenarios =
    let scenario name (change: ShutdownScenario -> ShutdownScenario) stages requests outcome =
        let expected: ShutdownObservation = { Outcome = outcome; Stages = "requested" :: stages; Requests = requests }
        { change acceptedRunning with Name = name; Expected = expected }

    let beforeDelivery name change outcome stage = scenario name change [ stage ] 0 outcome
    let afterAccepting name change outcome stage = scenario name change [ "accepted"; stage ] 1 outcome
    let unregistered basis = { basis with Registered = false }
    let expired basis = { basis with SessionExpired = true }
    let probing probes basis = { basis with Probes = probes }
    let closure value basis = { basis with Closure = value }
    let running = Ok ExactProcessState.Running
    let staleRegistration = Error ShutdownFailure.StaleRegistration
    let verificationFailed = Error ShutdownFailure.VerificationFailed

    [ beforeDelivery "a missing exact registration is explicit and sends nothing" unregistered
          (Error ShutdownFailure.MissingRegistration) "rejected:missing-registration"
      beforeDelivery "a registration past the liveness TTL is stale and sends nothing" expired staleRegistration
          "rejected:stale-registration"
      beforeDelivery "a reused process identity is stale and sends nothing" (probing [ Ok ExactProcessState.Reused ])
          staleRegistration "rejected:stale-registration"
      beforeDelivery "an unverifiable process fails verification and sends nothing" (probing [ Error "probe failed" ])
          verificationFailed "rejected:verification-failed"
      beforeDelivery "an already exited process completes without a request" (probing [ Ok ExactProcessState.Exited ])
          (Ok ShutdownCompletion.ProcessExit) "exited"
      afterAccepting "an accepted shutdown completes from exact closure" (closure ClosureObservation.TargetClosed)
          (Ok ShutdownCompletion.ExactClosure) "closed"
      afterAccepting "an accepted shutdown completes when the exact process exits"
          (probing [ running; Ok ExactProcessState.Exited ]) (Ok ShutdownCompletion.ProcessExit) "exited"
      afterAccepting "an accepted shutdown fails verification when the process stops answering"
          (probing [ running; Error "probe failed" ]) verificationFailed "rejected:verification-failed"
      afterAccepting "an unavailable closure snapshot fails verification" (closure ClosureObservation.Unavailable)
          verificationFailed "rejected:verification-failed"
      { acceptedRunning with
          Name = "an accepted shutdown times out while closure and process exit remain absent" } ]
    @ ([ ShutdownRequestOutcome.InvalidCapability, ShutdownFailure.InvalidCapability, "invalid-capability"
         ShutdownRequestOutcome.NonLoopbackRequest, ShutdownFailure.NonLoopbackRequest, "non-loopback"
         ShutdownRequestOutcome.Rejected, ShutdownFailure.Rejected, "rejected"
         ShutdownRequestOutcome.TransportFailed, ShutdownFailure.RequestFailed, "request-failed" ]
       |> List.map (fun (request, failure, rejection) ->
           scenario $"the endpoint outcome {rejection} stays typed" (fun basis -> { basis with Request = request })
               [ $"rejected:{rejection}" ] 1 (Error failure)))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type ExactShutdownTests() =

    static member ShutdownCases: TestCaseData seq =
        shutdownScenarios |> Seq.map (fun scenario -> TestCaseData(scenario).SetName scenario.Name)

    [<TestCaseSource("ShutdownCases")>]
    member _.``exact shutdown outcome and diagnostic trace``(scenario: ShutdownScenario) =
        Assert.That(runShutdownScenario scenario, Is.EqualTo scenario.Expected)

    [<Test>]
    member _.``Production shutdown transport posts the opaque capability and then observes closure``() =
        withBridges 1 (fun bridges ->
            let listener, shutdownUrl = List.exactlyOne bridges
            let identity = nextIdentity ()
            let capability = fakeShutdownCapability 'A' identity

            let entry =
                { validRequest identity (uniquePath "shutdown-http") (Some "session-http-shutdown") with
                    ShutdownUrl = shutdownUrl }
                |> registerOrFail (exactIdentityResolver identity)

            let received = listener.GetContextAsync()

            let shutdown =
                shutdownExactBatch
                    (fun () -> async { return Ok(Set.singleton identity) })
                    [ { WorktreePath = entry.WorktreePath; ProcessIdentity = entry.ProcessIdentity } ]
                |> Async.StartAsTask

            let context = await received
            use body = JsonDocument.Parse(readBody context)
            respond 202 context
            let expected: Result<ShutdownCompletion, ShutdownFailure> list = [ Ok ShutdownCompletion.ExactClosure ]

            Assert.Multiple(fun () ->
                Assert.That(body.RootElement.GetProperty("capability").GetString(), Is.EqualTo capability)
                Assert.That(shutdown.GetAwaiter().GetResult() |> List.map _.Outcome, Is.EqualTo expected)))

    [<Test>]
    member _.``Bulk shutdown shares polling and bounds request and process operations``() =
        let path = uniquePath "bulk-shutdown"

        let entries =
            [ 0..99 ]
            |> List.map (fun index ->
                let identity = nextIdentity ()

                validRequest identity path (Some $"batch-session-{index}")
                |> registerSessionWithDiagnostics ignore (exactIdentityResolver identity)
                |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}"))

        let startedAt = entries |> List.maxBy _.RegisteredAt |> _.RegisteredAt

        let targets =
            entries
            |> List.map (fun entry -> { WorktreePath = entry.WorktreePath; ProcessIdentity = entry.ProcessIdentity })

        let indexed projection =
            entries |> List.indexed |> List.map (fun (index, entry) -> projection entry, index) |> Map.ofList

        let indexByIdentity = indexed _.ProcessIdentity
        let indexByCapability = indexed (fun entry -> fakeShutdownCapability 'A' entry.ProcessIdentity)
        let closedProcesses = entries |> List.take 30 |> List.map _.ProcessIdentity |> Set.ofList
        let probeCounts = ConcurrentDictionary<ProcessIdentity, int>()
        let release () = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let probeRelease, requestRelease = release (), release ()
        let probeGate, requestGate, clockGate = obj (), obj (), obj ()

        // Mutable counters and clock expose the injected concurrency and timing boundaries.
        let mutable activeProbes = 0
        let mutable maxProbes = 0
        let mutable activeRequests = 0
        let mutable maxRequests = 0
        let mutable closureSnapshots = 0
        let mutable delays = 0
        let mutable now = startedAt

        /// Hold every concurrent operation until the bound is reached, so the observed maximum is
        /// exactly the concurrency limit rather than a scheduling artefact.
        let track (gate: obj) (release: TaskCompletionSource<unit>) enter leave result =
            async {
                if lock gate enter = maxConcurrentShutdownOperations then
                    release.TrySetResult() |> ignore

                do! release.Task.WaitAsync(TimeSpan.FromSeconds 5.0) |> Async.AwaitTask
                let completed = result ()
                lock gate leave
                return completed
            }

        let runtime: ShutdownDependencies =
            { SendShutdown =
                fun _ capability ->
                    track requestGate requestRelease
                        (fun () ->
                            activeRequests <- activeRequests + 1
                            maxRequests <- max maxRequests activeRequests
                            activeRequests)
                        (fun () -> activeRequests <- activeRequests - 1)
                        (fun () ->
                            match indexByCapability[capability] with
                            | index when index < 80 -> ShutdownRequestOutcome.Accepted
                            | index when index < 90 -> ShutdownRequestOutcome.Rejected
                            | _ -> ShutdownRequestOutcome.TransportFailed)
              ClosureSnapshot =
                fun () ->
                    async {
                        lock clockGate (fun () -> closureSnapshots <- closureSnapshots + 1)
                        return Ok closedProcesses
                    }
              ProbeProcess =
                fun _ identity ->
                    track probeGate probeRelease
                        (fun () ->
                            activeProbes <- activeProbes + 1
                            maxProbes <- max maxProbes activeProbes
                            activeProbes)
                        (fun () -> activeProbes <- activeProbes - 1)
                        (fun () ->
                            let count = probeCounts.AddOrUpdate(identity, 1, fun _ current -> current + 1)
                            let index = indexByIdentity[identity]

                            if count > 1 && index >= 30 && index < 60 then
                                Ok ExactProcessState.Exited
                            else
                                Ok ExactProcessState.Running)
              Delay =
                fun interval ->
                    async {
                        Assert.That(interval, Is.EqualTo(TimeSpan.FromMilliseconds 100.0))

                        lock clockGate (fun () ->
                            delays <- delays + 1
                            now <- startedAt + TimeSpan.FromSeconds 30.0)
                    }
              UtcNow = fun () -> lock clockGate (fun () -> now) }

        let attempts =
            let waitOptions: ShutdownWaitOptions =
                { Timeout = TimeSpan.FromSeconds 30.0; PollInterval = TimeSpan.FromMilliseconds 100.0 }

            shutdownExactBatchWithDiagnostics ignore runtime waitOptions targets |> Async.RunSynchronously

        let expected =
            [ 0..99 ]
            |> List.map (function
                | index when index < 30 -> Ok ShutdownCompletion.ExactClosure
                | index when index < 60 -> Ok ShutdownCompletion.ProcessExit
                | index when index < 80 -> Error ShutdownFailure.TimedOut
                | index when index < 90 -> Error ShutdownFailure.Rejected
                | _ -> Error ShutdownFailure.RequestFailed)

        Assert.Multiple(fun () ->
            Assert.That(attempts |> List.map _.Outcome, Is.EqualTo expected)
            Assert.That(
                (closureSnapshots, delays, maxProbes, maxRequests, now),
                Is.EqualTo(
                    (2, 1, maxConcurrentShutdownOperations, maxConcurrentShutdownOperations,
                     startedAt + TimeSpan.FromSeconds 30.0))
            ))
