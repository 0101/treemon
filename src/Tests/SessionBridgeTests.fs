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
open Tests.BridgeFixture
open Tests.TestUtils

let private clock = DateTime(2042, 7, 23, 12, 0, 0, DateTimeKind.Utc)
let private livenessTtl = TimeSpan.FromSeconds 60.0
let private queueTtl = TimeSpan.FromMinutes 5.0
let private injectUrl = "http://127.0.0.1:1/inject"
let private listenerTimeout = TimeSpan.FromSeconds 5.0

/// A synthetic registration observed `ageSeconds` before the fixed clock snapshot. Only the fields
/// the bridge reasons about (identity, durable session, registration age) vary between scenarios.
let private registrationAged processId ageSeconds sessionId =
    { ProcessIdentity = Some(syntheticProcessIdentityForProcessId processId)
      WorktreePath = "worktree"
      InjectUrl = injectUrl
      SessionId = SessionId sessionId
      TerminalSessionId = None
      RegisteredAt = clock - TimeSpan.FromSeconds(float ageSeconds) }

let private nextIdentity () =
    Guid.NewGuid().ToString("N")
    |> collisionResistantProcessIdentityForSessionId

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
    send CancellationToken.None { WorktreePath = path; Target = SendTarget.Unspecified; Prompt = prompt }
    |> Async.RunSynchronously

let private queue request =
    send CancellationToken.None request

[<RequireQualifiedAccess>]
type ClockProbe =
    | QueueTtl of enqueuedAt: DateTime
    | SessionLiveness of registeredAt: DateTime
    | PollLiveness of heartbeat: DateTime

type ClockScenario = { Name: string; Probe: ClockProbe; Survives: bool }

let private observeClock =
    function
    | ClockProbe.QueueTtl enqueuedAt ->
        let queued =
            { Id = Guid.Empty
              EnqueuedAt = enqueuedAt
              Target = QueuedTarget.Session SendTarget.Unspecified
              Prompt = Prompt.agentPrompt "q"
              LastFailure = None
              Delivery = PromptDelivery.Ordinary CancellationToken.None }
        cleanExpired clock [ queued ] |> List.isEmpty |> not
    | ClockProbe.SessionLiveness registeredAt ->
        isSessionAlive clock { registrationAged 90001 0 "clock" with RegisteredAt = registeredAt }
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
      SessionAge: (int * string) option
      Poll: bool * int
      Expected: (float * BridgeLiveness) option }

let private livenessScenarios =
    let case name sessionAge poll expected =
        { Name = name; SessionAge = sessionAge; Poll = poll; Expected = expected }

    let liveness isAlive sessionId liveSessionIds =
        { IsAlive = isAlive
          SessionId = sessionId
          LiveSessionIds = liveSessionIds
          SystemViewTargetSessionId = None }

    [ case "no session and no poll registration is unregistered" None (false, 0) None
      case "a poll heartbeat alone reports poll liveness with no session id" None (true, 10) (Some(10.0, liveness true None []))
      case "a live session alone reports its durable session id as live" (Some(10, "durable")) (false, 0)
          (Some(10.0, liveness true (Some "durable") [ "durable" ]))
      case "a stale session alone is not alive and lists no live session id" (Some(90, "durable")) (false, 0)
          (Some(90.0, liveness false (Some "durable") []))
      case "a live poll keeps a stale session alive but not live, on one clock snapshot" (Some(90, "durable"))
          (true, 10) (Some(10.0, liveness true (Some "durable") []))
      case "the reported age is the fresher of the session and the poll heartbeat" (Some(5, "durable")) (true, 30)
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

    [<Test>]
    member _.``startup reservation lifetime belongs to its launch deadline rather than queue TTL``() =
        use deadline = new CancellationTokenSource()
        let completion =
            TaskCompletionSource<Result<unit, StartupPromptFailure>>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let reserved =
            { Id = Guid.NewGuid()
              EnqueuedAt = clock - queueTtl
              Target = QueuedTarget.Session(SendTarget.DurableSession(SessionId "startup-deadline"))
              Prompt = Prompt.startup "Initial task"
              LastFailure = None
              Delivery = PromptDelivery.Startup(completion, deadline.Token) }

        Assert.That(cleanExpired clock [ reserved ], Is.EqualTo([ reserved ]))
        deadline.Cancel()
        Assert.That(cleanExpired clock [ reserved ], Is.Empty)

/// A rejection scenario owns the whole registration sequence so that two-step rejections (a reused
/// pid, a changed durable identity) share one runner with the single-step validation rejections.
type RegistrationRejection =
    { Name: string
      Reject: ProcessIdentity -> RegistrationRequest -> Result<SessionEntry, RegistrationFailure>
      Expected: RegistrationFailure }

let private rejectionScenarios =
    let invalid name mutate expected =
        { Name = name
          Reject = fun identity request -> registerSession (exactIdentityResolver identity) (mutate request)
          Expected = expected }

    [ invalid "a missing durable session id is rejected" (fun request -> { request with SessionId = None })
          RegistrationFailure.InvalidSessionId
      invalid "a blank durable session id is rejected" (fun request -> { request with SessionId = Some " " })
          RegistrationFailure.InvalidSessionId
      invalid "a malformed durable session id is rejected"
          (fun request -> { request with SessionId = Some "session with spaces" })
          RegistrationFailure.InvalidSessionId
      invalid "a malformed terminal session id is rejected"
          (fun request -> { request with TerminalSessionId = Some "not-a-terminal-id" })
          RegistrationFailure.InvalidTerminalSessionId
      invalid "a shutdown capability of the wrong shape is rejected"
          (fun request -> { request with ShutdownCapability = "too-short" })
          RegistrationFailure.InvalidShutdownCapability
      invalid "a relative worktree path is rejected" (fun request -> { request with WorktreePath = "relative" })
          RegistrationFailure.InvalidWorktreePath
      invalid "a non-loopback injection endpoint is rejected" (fun request -> { request with InjectUrl = "https://example.com/inject" })
          RegistrationFailure.InvalidInjectUrl
      invalid "a non-loopback shutdown endpoint is rejected" (fun request -> { request with ShutdownUrl = "https://example.com/shutdown" })
          RegistrationFailure.InvalidShutdownUrl ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
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
                    (Some identity, SessionId "session.exact:1",
                     Some(TerminalSessionId(terminal.ToLowerInvariant())), injectUrl))
            )
            Assert.That(sessionsForWorktree path |> List.map _.ProcessIdentity, Is.EqualTo [ Some identity ]))

    [<TestCaseSource("RejectionCases")>]
    member _.``Registration rejection leaves the worktree registry unchanged``(scenario: RegistrationRejection) =
        let identity = nextIdentity ()
        let path = uniquePath "rejected-registration"
        let sessionId = $"session-original-{Guid.NewGuid():N}"
        let request = validRequest identity path (Some sessionId)
        registerOrFail (exactIdentityResolver identity) request |> ignore
        let actual = scenario.Reject identity request
        let survivors = sessionsForWorktree path |> List.map (_.SessionId >> SessionId.value)
        let expected: Result<SessionEntry, RegistrationFailure> = Error scenario.Expected

        Assert.Multiple(fun () ->
            Assert.That(actual, Is.EqualTo expected)
            Assert.That(survivors, Is.EqualTo [ sessionId ]))

    [<Test>]
    member _.``Sequential conversations in one process do not transfer document owners``() =
        withTempCwd (fun () ->
            let path = uniquePath "sequential-conversations"
            let identity = nextIdentity ()
            let first, second = $"first-{Guid.NewGuid():N}", $"second-{Guid.NewGuid():N}"
            let register sid =
                validRequest identity path (Some sid)
                |> registerOrFail (exactIdentityResolver identity)
            register first |> ignore
            runAsync (CanvasDocOwnership.assign path "report.html" first) |> Result.defaultWith (fun _ -> failwith "owner save failed")
            register second |> ignore
            Assert.Multiple(fun () ->
                Assert.That(sessionsForWorktree path |> List.map (_.SessionId >> SessionId.value), Is.EquivalentTo [ first; second ])
                Assert.That(runAsync (CanvasDocOwnership.getOwner path "report.html"), Is.EqualTo(Some first))
                Assert.That(getSessionForWorktree path, Is.EqualTo(Some(SessionId second)))))

    [<Test>]
    member _.``Newest receipt wins including the older physical endpoint returning last``() =
        let path = uniquePath "last-arrival"
        let sid = Some $"resumed-{Guid.NewGuid():N}"
        let first, second = nextIdentity (), nextIdentity ()
        let register identity url terminal =
            { validRequest identity path sid with InjectUrl = url; TerminalSessionId = terminal }
            |> registerOrFail (exactIdentityResolver identity)
        let old = register first "http://127.0.0.1:1/inject" None
        let newer = register second "http://127.0.0.1:2/inject" (Some(Guid.NewGuid().ToString "N"))
        let returned = register first old.InjectUrl None
        Assert.Multiple(fun () ->
            Assert.That(returned.RegisteredAt, Is.GreaterThan newer.RegisteredAt)
            Assert.That(newer.RegisteredAt, Is.GreaterThan old.RegisteredAt)
            Assert.That(sessionsForWorktree path, Is.EqualTo [ returned ]))

    [<Test>]
    member _.``Receipt installation is atomic across a blocked optional location lookup``() =
        let path = uniquePath "concurrent-arrivals"
        let identity = nextIdentity ()
        let request = validRequest identity path (Some $"concurrent-{Guid.NewGuid():N}")
        use entered = new ManualResetEventSlim()
        use release = new ManualResetEventSlim()
        let resolver =
            ProcessIdentityResolver.create (fun _ ->
                entered.Set()
                Assert.That(release.Wait listenerTimeout, Is.True)
                Ok(Some identity))
        let first = Task.Run(fun () -> registerOrFail resolver request)
        Assert.That(entered.Wait listenerTimeout, Is.True)
        let next = Task.Run(fun () ->
            { request with InjectUrl = "http://127.0.0.1:2/inject" }
            |> registerOrFail (exactIdentityResolver identity))
        release.Set()
        let old, latest = await first, await next
        Assert.That(latest.RegisteredAt, Is.GreaterThan old.RegisteredAt)
        Assert.That(sessionsForWorktree path, Is.EqualTo [ latest ])

    [<Test>]
    member _.``Missing and unverified hints remain valid scoped canvas bridges``() =
        let path = uniquePath "optional-hints"
        let identity = nextIdentity ()
        let request = validRequest identity path (Some $"unverified-{Guid.NewGuid():N}")
        let unavailable = ProcessIdentityResolver.create (fun _ -> Error "probe unavailable")
        let entry = registerOrFail unavailable request
        let missing = registerOrFail unavailable { request with ParentProcessId = None }
        Assert.Multiple(fun () ->
            Assert.That(entry.ProcessIdentity, Is.EqualTo(None: ProcessIdentity option))
            Assert.That(missing.ProcessIdentity, Is.EqualTo(None: ProcessIdentity option))
            Assert.That(canvasSessionsForWorktree path, Is.EqualTo [ missing ])
            Assert.That((getStatus path).IsAlive, Is.True))

    [<Test>]
    member _.``Changed terminal and parent hints replace metadata without rejecting the conversation``() =
        let path = uniquePath "changed-hints"
        let identity = nextIdentity ()
        let request = validRequest identity path (Some $"hints-{Guid.NewGuid():N}")
        let firstTerminal, nextTerminal = Guid.NewGuid().ToString "N", Guid.NewGuid().ToString "N"
        registerOrFail (exactIdentityResolver identity) { request with TerminalSessionId = Some firstTerminal } |> ignore
        let changed =
            registerOrFail (exactIdentityResolver identity)
                { request with TerminalSessionId = Some nextTerminal; ParentProcessId = Some -1 }
        Assert.Multiple(fun () ->
            Assert.That(changed.ProcessIdentity, Is.EqualTo(None: ProcessIdentity option))
            Assert.That(changed.TerminalSessionId, Is.EqualTo(Some(TerminalSessionId nextTerminal)))
            Assert.That(canvasSessionsForWorktree path, Is.EqualTo [ changed ]))

    [<Test>]
    member _.``Old observed expiry cannot erase a replacement and expired sessions can return``() =
        let path = uniquePath "expiry-recovery"
        let identity = nextIdentity ()
        let request = validRequest identity path (Some $"expiry-{Guid.NewGuid():N}")
        let old = registerOrFail (exactIdentityResolver identity) request
        let replacement = registerOrFail (exactIdentityResolver identity) { request with InjectUrl = "http://127.0.0.1:2/inject" }
        expireObservedAt (old.RegisteredAt + livenessTtl) old
        Assert.That(sessionsForWorktree path, Is.EqualTo [ replacement ])
        Assert.That(canvasSessionsForWorktreeAt (replacement.RegisteredAt + livenessTtl) path, Is.Empty)
        let recovered = registerOrFail (exactIdentityResolver identity) request
        Assert.That(canvasSessionsForWorktree path, Is.EqualTo [ recovered ])

    [<Test>]
    member _.``Bridge diagnostics distinguish durable replacements and independent sessions``() =
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

        let multipleSessions =
            lastOf (function
                | LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved observed when
                    observed.Boundary = bridgeBoundary -> Some observed
                | _ -> None)

        Assert.That(
            (registrationKinds,
             multipleSessions.SessionIds |> List.map SessionId.value |> List.sort),
            Is.EqualTo(
                ([| LifecycleDiagnostics.BridgeRegistrationKind.Added
                    LifecycleDiagnostics.BridgeRegistrationKind.Refreshed
                    LifecycleDiagnostics.BridgeRegistrationKind.Refreshed
                    LifecycleDiagnostics.BridgeRegistrationKind.Added |],
                 [ sharedSession; independentSession ] |> List.sort)
            )
        )
        Assert.That(events |> Array.exists (function LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved _ -> true | _ -> false), Is.False)

type SelectionScenario =
    { Name: string
      Entries: SessionEntry list
      Kind: PromptKind
      Target: SendTarget
      Expected: SessionEntry option }

let private freshFirst = registrationAged 7001 1 "shared"
let private freshSecond = registrationAged 7002 2 "shared"
let private otherSession = registrationAged 7003 3 "other"
let private staleSession = registrationAged 7004 60 "stale"

let private selectionScenarios =
    let selects name entries target expected =
        { Name = name; Entries = entries; Kind = PromptKind.AgentPrompt; Target = target; Expected = expected }

    let exact (entry: SessionEntry) = SendTarget.ExactProcess(entry.SessionId, entry.ProcessIdentity.Value)
    let durable sessionId = SendTarget.DurableSession(SessionId sessionId)

    [ selects "an exact target addresses that physical process" [ freshFirst; freshSecond ] (exact freshFirst)
          (Some freshFirst)
      selects "an exact target never addresses a same-SessionId sibling" [ freshSecond ] (exact freshFirst) None
      selects "an exact target ignores a stale registration" [ staleSession ] (exact staleSession) None
      selects "an exact target cannot reuse the process for a different conversation"
          [ { freshFirst with SessionId = SessionId "replacement" } ] (exact freshFirst) None
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

let private canvasPayload = """{"action":"refresh"}"""

let private promptWireScenarios =
    [ { Name = "a canvas prompt has an explicit canvas kind"
        Prompt = Prompt.canvasFor @"Q:\repo" "report.html" canvasPayload
        Json = """{"kind":"canvas","prompt":"{\u0022action\u0022:\u0022refresh\u0022}","source":{"worktreePath":"Q:\\repo","filename":"report.html"}}""" }
      { Name = "a generic agent prompt has an explicit agent-prompt kind"
        Prompt = Prompt.agentPrompt "Sync with upstream/main when safe."
        Json = """{"kind":"agent-prompt","prompt":"Sync with upstream/main when safe."}""" } ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("BridgeTransport")>]
[<NonParallelizable>]
type PromptTransportTests() =

    static member WireCases: TestCaseData seq =
        promptWireScenarios |> Seq.map (fun scenario -> TestCaseData(scenario).SetName scenario.Name)

    [<TestCaseSource("WireCases")>]
    member _.``prompt transport kinds stay explicit on the wire``(scenario: PromptWireScenario) =
        Assert.That(serializePrompt scenario.Prompt, Is.EqualTo scenario.Json)

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
            let sessionId = SessionId $"shared-{Guid.NewGuid():N}"
            let targetIdentity, siblingIdentity = nextIdentity (), nextIdentity ()
            let register identity url = registerExactSession 'A' identity path url (Some(SessionId.value sessionId)) None |> ignore

            register targetIdentity failedUrl

            let failedRequest = failed.GetContextAsync()
            let delivery = deliverAgentPrompt path (SendTarget.ExactProcess(sessionId, targetIdentity)) "retry-exact"
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
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = uniquePath "queue-cap"
            let prompts = [ 1..12 ] |> List.map (fun index -> Prompt.agentPrompt $"message-{index}")
            prompts |> List.iter (sendPrompt path >> ignore)
            registerExactSession 'A' (nextIdentity ()) path url (Some $"cap-{Guid.NewGuid():N}") None |> ignore
            let bodies =
                [ for _ in 1..10 do
                    let context = await (listener.GetContextAsync())
                    let body = readBody context
                    respond 200 context
                    yield body ]
            runAsync (flushPending path)
            Assert.That(bodies, Is.EqualTo(prompts |> List.skip 2 |> List.map serializePrompt)))

    [<TestCase("owner")>]
    [<TestCase("endpoint")>]
    [<TestCase("worktree")>]
    member _.``Every unsent dispatch resolves authority after an in-flight request``(change: string) =
        withTempCwd (fun () ->
            withBridges 2 (fun bridges ->
                let oldListener, oldUrl = bridges[0]
                let nextListener, nextUrl = bridges[1]
                let path, away = uniquePath "dispatch-current", uniquePath "dispatch-away"
                let owner, nextOwner = $"owner-{Guid.NewGuid():N}", $"claimed-{Guid.NewGuid():N}"
                let identity = nextIdentity ()
                runAsync (CanvasDocOwnership.assign path "report.html" owner)
                |> Result.defaultWith (fun _ -> failwith "owner save failed")
                let message text =
                    { WorktreePath = path
                      Target = SendTarget.DurableSession(SessionId owner)
                      Prompt = Prompt.canvasFor (PathUtils.normalizePath path) "report.html" text }
                [ "first"; "second" ] |> List.iter (fun text ->
                    Assert.That(runAsync (queue (message text)), Is.EqualTo SendResult.Queued))
                let firstRequest = oldListener.GetContextAsync()
                registerExactSession 'A' identity path oldUrl (Some owner) None |> ignore
                let first = await firstRequest
                let firstBody = readBody first
                let nextRequest = nextListener.GetContextAsync()
                match change with
                | "owner" ->
                    runAsync (CanvasDocOwnership.assign path "report.html" nextOwner)
                    |> Result.defaultWith (fun _ -> failwith "claim save failed")
                    registerExactSession 'A' (nextIdentity ()) path nextUrl (Some nextOwner) None |> ignore
                | "endpoint" ->
                    registerExactSession 'A' (nextIdentity ()) path nextUrl (Some owner) None |> ignore
                | "worktree" ->
                    registerExactSession 'A' identity away nextUrl (Some owner) None |> ignore
                | _ -> failwith "unknown routing scenario"
                respond 200 first
                if change = "worktree" then
                    runAsync (flushPending path)
                    Assert.That(nextRequest.IsCompleted, Is.False, "A bridge in another worktree cannot receive the second message")
                    registerExactSession 'A' identity path nextUrl (Some owner) None |> ignore
                let second = await nextRequest
                let secondBody = readBody second
                respond 200 second
                runAsync (flushPending path)
                Assert.Multiple(fun () ->
                    Assert.That(firstBody, Is.EqualTo(serializePrompt (message "first").Prompt))
                    Assert.That(secondBody, Is.EqualTo(serializePrompt (message "second").Prompt))
                    Assert.That(runAsync (pendingPrompts path), Is.Empty))))

    [<Test>]
    member _.``A failed old send cannot remove a replacement and recovery preserves queue order``() =
        withTempCwd (fun () ->
            withBridges 2 (fun bridges ->
                let failedListener, failedUrl = bridges[0]
                let recoveredListener, recoveredUrl = bridges[1]
                let path = uniquePath "failed-replacement"
                let owner = $"owner-{Guid.NewGuid():N}"
                let identity = nextIdentity ()
                runAsync (CanvasDocOwnership.assign path "report.html" owner)
                |> Result.defaultWith (fun _ -> failwith "owner save failed")
                let message text =
                    { WorktreePath = path
                      Target = SendTarget.DurableSession(SessionId owner)
                      Prompt = Prompt.canvasFor (PathUtils.normalizePath path) "report.html" text }
                [ "first"; "second" ] |> List.iter (message >> queue >> runAsync >> ignore)
                let failedRequest = failedListener.GetContextAsync()
                registerExactSession 'A' identity path failedUrl (Some owner) None |> ignore
                let first = await failedRequest
                let replacement = registerExactSession 'A' (nextIdentity ()) path recoveredUrl (Some owner) None
                respond 503 first
                let recoveredBodies =
                    [ for _ in 1..2 do
                        let context = await (recoveredListener.GetContextAsync())
                        let body = readBody context
                        respond 200 context
                        yield body ]
                runAsync (flushPending path)
                Assert.Multiple(fun () ->
                    Assert.That(sessionsForWorktree path, Is.EqualTo [ replacement ])
                    Assert.That(recoveredBodies, Is.EqualTo([ "first"; "second" ] |> List.map (message >> _.Prompt >> serializePrompt))))))

    [<Test>]
    member _.``Failed queued messages retain age and cannot loop on an unchanged failed endpoint``() =
        withTempCwd (fun () ->
            withBridges 1 (fun bridges ->
                let listener, url = List.exactlyOne bridges
                let path = uniquePath "failed-queue-age"
                let owner = $"owner-{Guid.NewGuid():N}"
                let identity = nextIdentity ()
                runAsync (CanvasDocOwnership.assign path "report.html" owner)
                |> Result.defaultWith (fun _ -> failwith "owner save failed")
                let message text =
                    { WorktreePath = path
                      Target = SendTarget.DurableSession(SessionId owner)
                      Prompt = Prompt.canvasFor (PathUtils.normalizePath path) "report.html" text }
                [ "first"; "second" ] |> List.iter (message >> queue >> runAsync >> ignore)
                let original = runAsync (pendingPrompts path)
                let failedRequest = listener.GetContextAsync()
                registerExactSession 'A' identity path url (Some owner) None |> ignore
                let first = await failedRequest
                registerExactSession 'A' identity path url (Some owner) None |> ignore
                let unexpectedRetry = listener.GetContextAsync()
                respond 503 first
                runAsync (flushPending path)
                let failed = runAsync (pendingPrompts path)
                Assert.Multiple(fun () ->
                    Assert.That(unexpectedRetry.IsCompleted, Is.False)
                    Assert.That(failed |> List.map _.EnqueuedAt, Is.EqualTo(original |> List.map _.EnqueuedAt))
                    Assert.That(failed |> List.map _.Prompt.Text, Is.EqualTo [ "first"; "second" ])
                    Assert.That(cleanExpired (original[1].EnqueuedAt + queueTtl) failed, Is.Empty))
                registerExactSession 'A' identity path url (Some owner) None |> ignore
                let recoveredFirst = await unexpectedRetry
                let firstBody = readBody recoveredFirst
                respond 200 recoveredFirst
                let recoveredSecond = await (listener.GetContextAsync())
                let secondBody = readBody recoveredSecond
                respond 200 recoveredSecond
                runAsync (flushPending path)
                Assert.That([ firstBody; secondBody ], Is.EqualTo([ "first"; "second" ] |> List.map (message >> _.Prompt >> serializePrompt)))))

    [<Test>]
    member _.``Busy delivery accepts only a bounded ordered queue instead of waiting request bodies``() =
        withTempCwd (fun () ->
            withBridges 1 (fun bridges ->
                let listener, url = List.exactlyOne bridges
                let path = uniquePath "busy-delivery-cap"
                let owner = $"owner-{Guid.NewGuid():N}"
                runAsync (CanvasDocOwnership.assign path "report.html" owner)
                |> Result.defaultWith (fun _ -> failwith "owner save failed")
                registerExactSession 'A' (nextIdentity ()) path url (Some owner) None |> ignore
                let message text =
                    { WorktreePath = path
                      Target = SendTarget.DurableSession(SessionId owner)
                      Prompt = Prompt.canvasFor (PathUtils.normalizePath path) "report.html" text }
                let held = listener.GetContextAsync()
                let first = message "held" |> queue |> Async.StartAsTask
                let context = await held
                [ 1..12 ] |> List.iter (fun index ->
                    Assert.That(runAsync (queue (message $"queued-{index}")), Is.EqualTo SendResult.Queued))
                respond 200 context
                let delivered =
                    [ for _ in 1..10 do
                        let queued = await (listener.GetContextAsync())
                        let body = readBody queued
                        respond 200 queued
                        yield body ]
                Assert.That(await first, Is.EqualTo SendResult.Delivered)
                Assert.That(delivered, Is.EqualTo([ 3..12 ] |> List.map (fun index -> serializePrompt (message $"queued-{index}").Prompt)))
                Assert.That(runAsync (pendingPrompts path), Is.Empty)))

    [<TestCase("expired")>]
    [<TestCase("evicted")>]
    [<Category("CanvasRoutingRaces")>]
    member _.``Async target lookup cannot dispatch an expired or evicted item``(change: string) =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = uniquePath "take-after-lookup"
            let owner = SessionId $"owner-{Guid.NewGuid():N}"
            let terminal = TerminalSessionId(Guid.NewGuid().ToString "N")
            // The injected lane clock and resolver expose expiry/eviction during the async lookup.
            let mutable now = DateTime.UtcNow
            let mutable lookupBlocked = false
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            initializeDeliveryClock path (fun () -> now)
            registerExactSession 'A' (nextIdentity ()) path url (Some(SessionId.value owner)) None |> ignore
            runAsync (flushPending path)
            let message text target =
                { WorktreePath = path
                  Target = target
                  Prompt = Prompt.canvasFor (PathUtils.normalizePath path) "diff.html" text }
            Assert.That(runAsync (queue (message "stale" SendTarget.Unspecified)), Is.EqualTo SendResult.Queued)
            let original = runAsync (pendingPrompts path) |> List.exactlyOne
            let launchAt = DateTime.UtcNow
            runAsync (reservePendingSystemViews path launchAt) |> ignore
            let resolve () =
                async {
                    if lookupBlocked then
                        entered.TrySetResult() |> ignore
                        do! release.Task |> Async.AwaitTask
                        return Some owner
                    else return None
                }
            runAsync (targetPendingSystemViews path launchAt terminal resolve)
            runAsync (flushPending path)
            lookupBlocked <- true
            let draining = flushPending path |> Async.StartAsTask
            await entered.Task
            let count = if change = "expired" then 2 else 12
            let next = listener.GetContextAsync()
            let firstIndex = if change = "expired" then 1 else 3
            let bodies =
                if change = "expired" then
                    now <- original.EnqueuedAt + queueTtl
                    Assert.That((deliveryStatus path).QueuedMessages, Is.EqualTo 1, "Expiry must be checked by TAKE, not a prior queue cleanup")
                    release.TrySetResult() |> ignore
                    await draining
                    Assert.That(next.IsCompleted, Is.False, "An item expiring during target lookup must issue no HTTP")
                    let first = queue (message "live-1" (SendTarget.DurableSession owner)) |> Async.StartAsTask
                    let context = await next
                    let firstBody = readBody context
                    Assert.That(runAsync (queue (message "live-2" (SendTarget.DurableSession owner))), Is.EqualTo SendResult.Queued)
                    respond 200 context
                    let second = await (listener.GetContextAsync())
                    let secondBody = readBody second
                    respond 200 second
                    Assert.That(await first, Is.EqualTo SendResult.Delivered)
                    [ firstBody; secondBody ]
                else
                    [ 1..count ]
                    |> List.iter (fun index ->
                        Assert.That(
                            runAsync (queue (message $"live-{index}" (SendTarget.DurableSession owner))),
                            Is.EqualTo SendResult.Queued))
                    release.TrySetResult() |> ignore
                    let delivered =
                        [ for index in firstIndex..count do
                            let context = await (if index = firstIndex then next else listener.GetContextAsync())
                            let body = readBody context
                            respond 200 context
                            yield body ]
                    await draining
                    delivered
            Assert.That(
                bodies,
                Is.EqualTo([ firstIndex..count ] |> List.map (fun index ->
                    serializePrompt (message $"live-{index}" (SendTarget.DurableSession owner)).Prompt)))
            Assert.That(runAsync (pendingPrompts path), Is.Empty))

    [<Test>]
    [<Category("CanvasRoutingFollowers")>]
    member _.``Exact terminal successors cannot overtake an earlier stale activity lookup``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = uniquePath "coalesced-pass-order"
            let owner = SessionId $"launched-{Guid.NewGuid():N}"
            let terminal = TerminalSessionId(Guid.NewGuid().ToString "N")
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            // The first resolver captures absent activity before the exact terminal becomes active.
            let mutable blockNext = false
            let mutable reachable = false
            let message payload =
                { WorktreePath = path
                  Target = SendTarget.Unspecified
                  Prompt = Prompt.canvasFor (PathUtils.normalizePath path) "diff.html" payload }
            [ "first"; "second" ] |> List.iter (fun payload ->
                Assert.That(runAsync (queue (message payload)), Is.EqualTo SendResult.Queued))
            let launchAt = DateTime.UtcNow
            runAsync (reservePendingSystemViews path launchAt) |> ignore
            let resolve () =
                async {
                    let observed = if reachable then Some owner else None
                    if blockNext then
                        blockNext <- false
                        entered.TrySetResult() |> ignore
                        do! release.Task |> Async.AwaitTask
                    return observed
                }
            runAsync (targetPendingSystemViews path launchAt terminal resolve)
            runAsync (flushPending path)
            blockNext <- true
            registerExactSession 'A' (nextIdentity ()) path url (Some(SessionId.value owner)) None |> ignore
            await entered.Task
            reachable <- true
            release.TrySetResult() |> ignore
            let bodies =
                [ for _ in 1..2 do
                    let context = await (listener.GetContextAsync())
                    let body = readBody context
                    respond 200 context
                    yield body ]
            runAsync (flushPending path)
            Assert.That(bodies, Is.EqualTo(
                [ message "first"; message "second" ]
                |> List.map (_.Prompt >> serializePrompt))))

    [<Test>]
    [<Category("CanvasRoutingFollowers")>]
    member _.``An unchanged failed predecessor does not spin or block fresh exact terminal work``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = uniquePath "failed-terminal-predecessor"
            let owner = SessionId $"launched-{Guid.NewGuid():N}"
            let identity = nextIdentity ()
            let terminal = TerminalSessionId(Guid.NewGuid().ToString "N")
            let message payload =
                { WorktreePath = path
                  Target = SendTarget.Unspecified
                  Prompt = Prompt.canvasFor (PathUtils.normalizePath path) "diff.html" payload }
            [ "first"; "second" ] |> List.iter (message >> queue >> runAsync >> ignore)
            let original = runAsync (pendingPrompts path)
            let launchAt = DateTime.UtcNow
            let bind at =
                runAsync (reservePendingSystemViews path at) |> ignore
                runAsync (targetPendingSystemViews path at terminal (fun () -> async.Return(Some owner)))
            bind launchAt
            let failedRequest = listener.GetContextAsync()
            registerExactSession 'A' identity path url (Some(SessionId.value owner)) None |> ignore
            let failed = await failedRequest
            registerExactSession 'A' identity path url (Some(SessionId.value owner)) None |> ignore
            respond 503 failed
            runAsync (flushPending path)
            Assert.That(runAsync (queue (message "fresh")), Is.EqualTo SendResult.Queued)
            let received = listener.GetContextAsync()
            bind (launchAt.AddTicks 1L)
            let fresh = await received
            let body = readBody fresh
            respond 200 fresh
            runAsync (flushPending path)
            let retained = runAsync (pendingPrompts path)
            Assert.Multiple(fun () ->
                Assert.That(body, Is.EqualTo(serializePrompt (message "fresh").Prompt))
                Assert.That(retained |> List.map _.Prompt.Text, Is.EqualTo [ "first"; "second" ])
                Assert.That(retained |> List.map _.EnqueuedAt, Is.EqualTo(original |> List.map _.EnqueuedAt))
                Assert.That(retained |> List.forall _.LastFailure.IsSome, Is.True)))

    [<Test>]
    [<Category("CanvasRoutingRaces")>]
    member _.``Registration wakeups coalesce while one HTTP dispatch is held``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = uniquePath "coalesced-registration-wakes"
            let owner = $"owner-{Guid.NewGuid():N}"
            let identity = nextIdentity ()
            registerExactSession 'A' identity path url (Some owner) None |> ignore
            runAsync (flushPending path)
            let held = listener.GetContextAsync()
            let message text =
                { WorktreePath = path
                  Target = SendTarget.DurableSession(SessionId owner)
                  Prompt = Prompt.agentPrompt text }
            let first = queue (message "held") |> Async.StartAsTask
            let context = await held
            [ 1..40 ] |> List.iter (fun index ->
                registerExactSession 'A' identity path url (Some owner) None |> ignore
                Assert.That(runAsync (queue (message $"queued-{index}")), Is.EqualTo SendResult.Queued))
            Assert.That(
                deliveryStatus path,
                Is.EqualTo { QueuedMessages = 10; ActiveDrains = 1; PendingNotifications = 1 })
            respond 200 context
            let bodies =
                [ for _ in 1..10 do
                    let queued = await (listener.GetContextAsync())
                    let body = readBody queued
                    respond 200 queued
                    yield body ]
            Assert.That(await first, Is.EqualTo SendResult.Delivered)
            runAsync (flushPending path)
            Assert.That(
                bodies,
                Is.EqualTo([ 31..40 ] |> List.map (fun index -> serializePrompt (message $"queued-{index}").Prompt)))
            Assert.That((deliveryStatus path).ActiveDrains, Is.Zero))

    [<Test>]
    member _.``Unverified location allows canvas but makes generic prompt and exact shutdown unavailable``() =
        withTempCwd (fun () ->
            withBridges 1 (fun bridges ->
                let listener, url = List.exactlyOne bridges
                let path = uniquePath "unverified-operations"
                let identity = nextIdentity ()
                let owner = $"owner-{Guid.NewGuid():N}"
                runAsync (CanvasDocOwnership.assign path "report.html" owner)
                |> Result.defaultWith (fun _ -> failwith "owner save failed")
                let entry =
                    { validRequest identity path (Some owner) with InjectUrl = url; ParentProcessId = None }
                    |> registerOrFail (ProcessIdentityResolver.create (fun _ -> Error "unavailable"))
                let received = listener.GetContextAsync()
                let canvas =
                    queue
                        { WorktreePath = path
                          Target = SendTarget.DurableSession entry.SessionId
                          Prompt = Prompt.canvasFor (PathUtils.normalizePath path) "report.html" canvasPayload }
                    |> Async.StartAsTask
                await received |> respond 200
                Assert.That(await canvas, Is.EqualTo SendResult.Delivered)
                Assert.That(
                    await (deliverAgentPrompt path (SendTarget.DurableSession entry.SessionId) "generic"),
                    Is.EqualTo DeliveryResult.NoLiveSession)
                let shutdown =
                    shutdownExactBatch
                        (fun () -> async.Return(Ok Set.empty))
                        [ { WorktreePath = path; SessionId = entry.SessionId; ProcessIdentity = identity } ]
                    |> runAsync
                Assert.That(
                    shutdown |> List.map _.Outcome,
                    Is.EqualTo([ Error ShutdownFailure.LocationUnavailable ]: Result<ShutdownCompletion, ShutdownFailure> list))
                Assert.That(canvasSessionsForWorktree path, Is.EqualTo [ entry ])))

    [<Test>]
    member _.``cancelling one queued request preserves another with identical content``() =
        let path = uniquePath "cancel-exact-queued-prompt"
        let prompt = Prompt.canvasFor (PathUtils.normalizePath path) "report.html" canvasPayload
        let request = { WorktreePath = path; Target = SendTarget.Unspecified; Prompt = prompt }
        use cancellation = new CancellationTokenSource()
        send cancellation.Token request |> Async.RunSynchronously |> ignore
        send CancellationToken.None request |> Async.RunSynchronously |> ignore
        cancellation.Cancel()

        Assert.That(
            runAsync (pendingPrompts path) |> List.map _.Prompt,
            Is.EqualTo([ prompt ]))

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
        | LifecycleDiagnostics.ShutdownRejection.LocationUnavailable -> "location-unavailable"
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
    let sessionId = SessionId $"shutdown-{Guid.NewGuid():N}"

    let registeredAt =
        if scenario.Registered then
            (registerExactSession 'A' identity path injectUrl (Some(SessionId.value sessionId)) None).RegisteredAt
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
            [ { WorktreePath = path; SessionId = sessionId; ProcessIdentity = identity } ]
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
                    [ { WorktreePath = entry.WorktreePath; SessionId = entry.SessionId; ProcessIdentity = identity } ]
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
            |> List.map (fun entry ->
                { WorktreePath = entry.WorktreePath
                  SessionId = entry.SessionId
                  ProcessIdentity = entry.ProcessIdentity.Value })

        let indexed projection =
            entries |> List.indexed |> List.map (fun (index, entry) -> projection entry, index) |> Map.ofList

        let indexByIdentity = indexed _.ProcessIdentity.Value
        let indexByCapability = indexed (fun entry -> fakeShutdownCapability 'A' entry.ProcessIdentity.Value)
        let closedProcesses = entries |> List.take 30 |> List.map _.ProcessIdentity.Value |> Set.ofList
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
