module Tests.SessionBridgeTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Server
open Server.SessionBridge
open Server.SessionActivity

let private uniquePath prefix =
    Path.Combine(Path.GetTempPath(), "treemon-session-bridge-tests", prefix, $"{Guid.NewGuid():N}")

let private clockSnapshot = DateTime(2042, 7, 23, 12, 0, 0, DateTimeKind.Utc)

let private clockIdentity =
    ProcessIdentity.create 91001 91001001L
    |> Result.defaultWith invalidOp

let private sessionEntry registeredAt =
    { ProcessIdentity = clockIdentity
      WorktreePath = Path.Combine("test", "clock")
      InjectUrl = "http://localhost/inject"
      SessionId = Some(SessionId "clock-session")
      TerminalSessionId = None
      RegisteredAt = registeredAt }

let private queuedPrompt enqueuedAt text =
    { EnqueuedAt = enqueuedAt
      Target = SendTarget.Unspecified
      Prompt = Prompt.agentPrompt text }

// Tests share the module-level bridge registry, so each synthetic physical process needs a unique
// exact identity even when several tests intentionally reuse one durable SessionId.
let mutable private nextProcessId = 92000

let private nextIdentity () =
    let processId = Interlocked.Increment(&nextProcessId)
    ProcessIdentity.create processId (int64 processId * 1000L + 1L)
    |> Result.defaultWith invalidOp

let private capabilityFor identity =
    let processId, startTicks = ProcessIdentity.sortKey identity
    let suffix = $"{processId:x8}{startTicks:x16}"
    String('A', 43 - suffix.Length) + suffix

let private resolverFor identity =
    ProcessIdentityResolver.create (fun processId ->
        if processId = ProcessIdentity.processId identity then
            Ok(Some identity)
        else
            Ok None)

let private registrationRequest identity path injectUrl sessionId terminalSessionId capability =
    { WorktreePath = path
      InjectUrl = injectUrl
      ShutdownUrl = "http://127.0.0.1:1/shutdown"
      ShutdownCapability = capability
      SessionId = sessionId
      ParentProcessId = ProcessIdentity.processId identity
      TerminalSessionId = terminalSessionId }

let private registerExactSession identity path injectUrl sessionId =
    let request =
        registrationRequest
            identity
            path
            injectUrl
            sessionId
            None
            (capabilityFor identity)

    registerSession (resolverFor identity) request
    |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}")

let private registerTestSession path injectUrl sessionId =
    registerExactSession (nextIdentity ()) path injectUrl sessionId
    |> ignore

let private assertRegistrationFailure
    (expected: RegistrationFailure)
    (actual: Result<SessionEntry, RegistrationFailure>)
    : unit =
    match actual with
    | Error failure -> Assert.That(failure, Is.EqualTo expected)
    | Ok entry ->
        Assert.Fail(
            $"Expected registration failure {expected}, but registered {entry.ProcessIdentity}"
        )

let private assertShutdownResult
    (expected: Result<ShutdownCompletion, ShutdownFailure>)
    (actual: Result<ShutdownCompletion, ShutdownFailure>)
    =
    Assert.That(actual, Is.EqualTo expected)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ClockTests() =

    [<Test>]
    member _.``Queue TTL expires prompts at the exact threshold``() =
        let threshold = clockSnapshot - TimeSpan.FromMinutes 5.0
        let expired = queuedPrompt threshold "expired"
        let fresh = queuedPrompt (threshold.AddTicks 1L) "fresh"

        Assert.That(cleanExpired clockSnapshot [ expired; fresh ], Is.EqualTo [ fresh ])

    [<Test>]
    member _.``Session liveness becomes stale at the exact threshold``() =
        let threshold = clockSnapshot - TimeSpan.FromSeconds 60.0

        Assert.That(isSessionAlive clockSnapshot (sessionEntry (threshold.AddTicks 1L)), Is.True)
        Assert.That(isSessionAlive clockSnapshot (sessionEntry threshold), Is.False)

    [<Test>]
    member _.``Poll liveness becomes stale at the exact threshold``() =
        let threshold = clockSnapshot - TimeSpan.FromSeconds 60.0

        Assert.That(isPollAlive clockSnapshot (threshold.AddTicks 1L), Is.True)
        Assert.That(isPollAlive clockSnapshot threshold, Is.False)

    [<Test>]
    member _.``Combined liveness uses one supplied clock snapshot``() =
        let staleSession = sessionEntry (clockSnapshot - TimeSpan.FromSeconds 60.0)
        let liveHeartbeat = clockSnapshot - TimeSpan.FromSeconds 60.0 + TimeSpan.FromTicks 1L

        let age, liveness =
            computeLiveness clockSnapshot (Some staleSession) (true, liveHeartbeat)
            |> Option.get

        Assert.That(age, Is.EqualTo((clockSnapshot - liveHeartbeat).TotalSeconds))
        Assert.That(liveness.IsAlive, Is.True)
        Assert.That(
            liveness.SessionId,
            Is.EqualTo(staleSession.SessionId |> Option.map SessionId.value)
        )
        Assert.That(liveness.LiveSessionIds, Is.Empty)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ExactRegistrationTests() =

    [<Test>]
    member _.``Registration stores the resolver's exact identity and normalized terminal origin``() =
        let identity = nextIdentity ()
        let path = uniquePath "exact-registration"
        let terminal = "ABCDEF0123456789ABCDEF0123456789"

        let entry =
            registrationRequest
                identity
                path
                "http://127.0.0.1:1234/inject"
                (Some "session.exact:1")
                (Some terminal)
                (capabilityFor identity)
            |> registerSession (resolverFor identity)
            |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}")

        Assert.Multiple(fun () ->
            Assert.That(entry.ProcessIdentity, Is.EqualTo identity)
            Assert.That(entry.SessionId, Is.EqualTo(Some(SessionId "session.exact:1")))
            Assert.That(
                entry.TerminalSessionId,
                Is.EqualTo(
                    Some(
                        TerminalSessionId(
                            terminal.ToLowerInvariant()
                        )
                    )
                )
            ))

    [<Test>]
    member _.``Registration rejects missing dead and unresolved parent identities without an entry``() =
        let identity = nextIdentity ()
        let path = uniquePath "invalid-parent"
        let valid =
            registrationRequest
                identity
                path
                "http://127.0.0.1:1234/inject"
                (Some "session-parent")
                None
                (capabilityFor identity)

        let missing =
            registerSession
                (resolverFor identity)
                { valid with ParentProcessId = 0 }

        let dead =
            registerSession
                (ProcessIdentityResolver.create (fun _ -> Ok None))
                valid

        let unresolved =
            registerSession
                (ProcessIdentityResolver.create (fun _ -> Error "probe failed"))
                valid

        Assert.Multiple(fun () ->
            assertRegistrationFailure RegistrationFailure.InvalidParentProcessId missing
            assertRegistrationFailure RegistrationFailure.ParentProcessNotRunning dead
            assertRegistrationFailure RegistrationFailure.ParentProcessResolutionFailed unresolved
            Assert.That(sessionsForWorktree path, Is.Empty))

    [<Test>]
    member _.``Registration rejects invalid terminal identity and shutdown capability``() =
        let identity = nextIdentity ()
        let path = uniquePath "invalid-registration-metadata"
        let valid =
            registrationRequest
                identity
                path
                "http://127.0.0.1:1234/inject"
                (Some "session-metadata")
                None
                (capabilityFor identity)

        let invalidTerminal =
            registerSession
                (resolverFor identity)
                { valid with TerminalSessionId = Some "not-a-terminal-id" }

        let invalidCapability =
            registerSession
                (resolverFor identity)
                { valid with ShutdownCapability = "too-short" }

        Assert.Multiple(fun () ->
            assertRegistrationFailure RegistrationFailure.InvalidTerminalSessionId invalidTerminal
            assertRegistrationFailure RegistrationFailure.InvalidShutdownCapability invalidCapability
            Assert.That(sessionsForWorktree path, Is.Empty))

    [<Test>]
    member _.``A reused PID heartbeat cannot re-key one bridge capability to another process``() =
        let original = nextIdentity ()
        let processId = ProcessIdentity.processId original
        let reused =
            ProcessIdentity.create
                processId
                (ProcessIdentity.processStartTimeUtcTicks original + 1L)
            |> Result.defaultWith invalidOp

        // The mutable value is the injected operating-system resolver state under test.
        let mutable current = original
        let resolver =
            ProcessIdentityResolver.create (fun requested ->
                if requested = processId then Ok(Some current) else Ok None)

        let path = uniquePath "pid-reuse"
        let capability = capabilityFor original
        let request =
            registrationRequest
                original
                path
                "http://127.0.0.1:1234/inject"
                (Some "session-reuse")
                None
                capability

        registerSession resolver request
        |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}")
        |> ignore

        current <- reused
        let repeated = registerSession resolver request

        assertRegistrationFailure RegistrationFailure.ParentProcessReused repeated
        Assert.That(
            sessionsForWorktree path,
            Is.Empty,
            "The stale original registration is not live and the reused process was not inserted"
        )

    [<Test>]
    member _.``Liveness lookup prunes exited and reused exact registrations once``() =
        let path = uniquePath "terminal-registration-pruning"
        let exited = nextIdentity ()
        let original = nextIdentity ()
        let reused =
            ProcessIdentity.create
                (ProcessIdentity.processId original)
                (ProcessIdentity.processStartTimeUtcTicks original + 1L)
            |> Result.defaultWith invalidOp

        let states =
            ConcurrentDictionary<int, ProcessIdentity option>()

        let probes = ConcurrentDictionary<int, int>()

        [ exited; original ]
        |> List.iter (fun identity ->
            states[ProcessIdentity.processId identity] <- Some identity)

        let resolver =
            ProcessIdentityResolver.create (fun processId ->
                probes.AddOrUpdate(
                    processId,
                    1,
                    fun _ count -> count + 1
                )
                |> ignore

                match states.TryGetValue processId with
                | true, current -> Ok current
                | false, _ -> Ok None)

        let register identity sessionId =
            registrationRequest
                identity
                path
                "http://127.0.0.1:1234/inject"
                (Some sessionId)
                None
                (capabilityFor identity)
            |> registerSessionWithDiagnostics ignore resolver
            |> Result.defaultWith (fun failure ->
                invalidOp $"registration failed: {failure}")
            |> ignore

        register exited "session-exited"
        register original "session-reused"

        states[ProcessIdentity.processId exited] <- None
        states[ProcessIdentity.processId original] <- Some reused

        Assert.That(sessionsForWorktree path, Is.Empty)

        let probesAfterPrune =
            [ exited; original ]
            |> List.map (fun identity ->
                let processId = ProcessIdentity.processId identity
                processId, probes[processId])
            |> Map.ofList

        Assert.That(sessionsForWorktree path, Is.Empty)

        [ exited; original ]
        |> List.iter (fun identity ->
            let processId = ProcessIdentity.processId identity

            Assert.That(
                probes[processId],
                Is.EqualTo probesAfterPrune[processId],
                "A later lookup must not retain or probe a terminal exact registration"
            ))

    [<Test>]
    member _.``One exact process cannot change durable identity or worktree on heartbeat``() =
        let identity = nextIdentity ()
        let path = uniquePath "identity-mismatch"
        let request =
            registrationRequest
                identity
                path
                "http://127.0.0.1:1234/inject"
                (Some "session-original")
                None
                (capabilityFor identity)

        registerSession (resolverFor identity) request
        |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}")
        |> ignore

        let mismatch =
            registerSession
                (resolverFor identity)
                { request with SessionId = Some "session-other" }

        assertRegistrationFailure RegistrationFailure.ParentIdentityMismatch mismatch
        Assert.That(
            sessionsForWorktree path
            |> List.choose _.SessionId
            |> List.map SessionId.value,
            Is.EqualTo [ "session-original" ]
        )

    [<Test>]
    member _.``Bridge diagnostics distinguish refreshes normal sessions and duplicate physical registrations``() =
        let path = uniquePath "registration-diagnostics"
        let sharedSession = $"shared-{Guid.NewGuid():N}"
        let independentSession = $"independent-{Guid.NewGuid():N}"
        let firstIdentity = nextIdentity ()
        let secondIdentity = nextIdentity ()
        let thirdIdentity = nextIdentity ()
        let firstTerminal = Guid.NewGuid().ToString("N")
        let secondTerminal = Guid.NewGuid().ToString("N")
        let diagnostics =
            ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

        let register identity sessionId terminalId =
            registrationRequest
                identity
                path
                "http://127.0.0.1:1234/inject"
                (Some sessionId)
                (Some terminalId)
                (capabilityFor identity)
            |> registerSessionWithDiagnostics
                diagnostics.Enqueue
                (resolverFor identity)
            |> Result.defaultWith (fun failure ->
                invalidOp $"registration failed: {failure}")

        register firstIdentity sharedSession firstTerminal
        |> ignore

        register secondIdentity sharedSession secondTerminal
        |> ignore

        register firstIdentity sharedSession firstTerminal
        |> ignore

        register thirdIdentity independentSession firstTerminal
        |> ignore

        let events = diagnostics.ToArray()

        let registrationKinds =
            events
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.BridgeRegistration registration ->
                    Some registration.Kind
                | _ -> None)

        let sameSession =
            events
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved multiplicity
                    when multiplicity.Boundary =
                         LifecycleDiagnostics.ObservationBoundary.Bridge ->
                    Some multiplicity
                | _ -> None)
            |> Array.last

        let multipleSessions =
            events
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved multiple
                    when multiple.Boundary =
                         LifecycleDiagnostics.ObservationBoundary.Bridge ->
                    Some multiple
                | _ -> None)
            |> Array.last

        Assert.Multiple(fun () ->
            Assert.That(
                registrationKinds,
                Is.EqualTo(
                    [| LifecycleDiagnostics.BridgeRegistrationKind.Added
                       LifecycleDiagnostics.BridgeRegistrationKind.Added
                       LifecycleDiagnostics.BridgeRegistrationKind.Refreshed
                       LifecycleDiagnostics.BridgeRegistrationKind.Added |]
                )
            )
            Assert.That(
                sameSession.ProcessIdentities,
                Is.EquivalentTo([ firstIdentity; secondIdentity ])
            )
            Assert.That(
                sameSession.TerminalSessionIds
                |> List.map TerminalSessionId.value,
                Is.EquivalentTo([ firstTerminal; secondTerminal ])
            )
            Assert.That(
                multipleSessions.SessionIds
                |> List.map SessionId.value,
                Is.EquivalentTo([ sharedSession; independentSession ])
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type ExactPromptRoutingTests() =

    [<Test>]
    member _.``Exact prompt targets address two physical processes sharing one durable session``() =
        let path = uniquePath "exact-prompt"
        let sessionId = $"shared-{Guid.NewGuid():N}"
        let firstIdentity = nextIdentity ()
        let secondIdentity = nextIdentity ()
        let firstPort, secondPort =
            match Tests.TestUtils.getFreeTcpPorts 2 with
            | [ first; second ] -> first, second
            | ports -> failwith $"expected two free ports, got {ports.Length}"

        use first = new HttpListener()
        use second = new HttpListener()
        first.Prefixes.Add($"http://127.0.0.1:{firstPort}/")
        second.Prefixes.Add($"http://127.0.0.1:{secondPort}/")
        first.Start()
        second.Start()

        registerExactSession
            firstIdentity
            path
            $"http://127.0.0.1:{firstPort}/"
            (Some sessionId)
        |> ignore

        registerExactSession
            secondIdentity
            path
            $"http://127.0.0.1:{secondPort}/"
            (Some sessionId)
        |> ignore

        let firstRequest = first.GetContextAsync()
        let secondRequest = second.GetContextAsync()

        let firstDelivery =
            tryDeliver
                { WorktreePath = path
                  Target = SendTarget.ExactProcess firstIdentity
                  Prompt = Prompt.agentPrompt "first" }
            |> Async.StartAsTask

        let firstCompleted =
            Task.WhenAny(firstRequest, secondRequest)
                .WaitAsync(TimeSpan.FromSeconds 5.0)
                .GetAwaiter()
                .GetResult()

        Assert.That(
            Object.ReferenceEquals(firstCompleted, firstRequest),
            Is.True,
            "The first exact identity must not deliver to its same-SessionId sibling"
        )

        let firstContext = firstRequest.GetAwaiter().GetResult()
        firstContext.Response.StatusCode <- 200
        firstContext.Response.Close()
        Assert.That(
            firstDelivery.GetAwaiter().GetResult(),
            Is.EqualTo DeliveryResult.Delivered
        )

        let secondDelivery =
            tryDeliver
                { WorktreePath = path
                  Target = SendTarget.ExactProcess secondIdentity
                  Prompt = Prompt.agentPrompt "second" }
            |> Async.StartAsTask

        let secondContext =
            secondRequest
                .WaitAsync(TimeSpan.FromSeconds 5.0)
                .GetAwaiter()
                .GetResult()

        secondContext.Response.StatusCode <- 200
        secondContext.Response.Close()
        Assert.That(
            secondDelivery.GetAwaiter().GetResult(),
            Is.EqualTo DeliveryResult.Delivered
        )

    [<Test>]
    member _.``An exact queued prompt cannot drain to a same-SessionId sibling``() =
        let path = uniquePath "exact-queue"
        let sessionId = $"shared-{Guid.NewGuid():N}"
        let targetIdentity = nextIdentity ()
        let siblingIdentity = nextIdentity ()
        let failedPort, siblingPort, recoveredPort =
            match Tests.TestUtils.getFreeTcpPorts 3 with
            | [ failed; sibling; recovered ] -> failed, sibling, recovered
            | ports -> failwith $"expected three free ports, got {ports.Length}"

        use failed = new HttpListener()
        use sibling = new HttpListener()
        use recovered = new HttpListener()
        failed.Prefixes.Add($"http://127.0.0.1:{failedPort}/")
        sibling.Prefixes.Add($"http://127.0.0.1:{siblingPort}/")
        recovered.Prefixes.Add($"http://127.0.0.1:{recoveredPort}/")
        failed.Start()
        sibling.Start()
        recovered.Start()

        registerExactSession
            targetIdentity
            path
            $"http://127.0.0.1:{failedPort}/"
            (Some sessionId)
        |> ignore

        registerExactSession
            siblingIdentity
            path
            $"http://127.0.0.1:{siblingPort}/"
            (Some sessionId)
        |> ignore

        let failedRequest = failed.GetContextAsync()
        let firstDelivery =
            tryDeliver
                { WorktreePath = path
                  Target = SendTarget.ExactProcess targetIdentity
                  Prompt = Prompt.agentPrompt "retry-exact" }
            |> Async.StartAsTask

        let failedContext =
            failedRequest
                .WaitAsync(TimeSpan.FromSeconds 5.0)
                .GetAwaiter()
                .GetResult()

        failedContext.Response.StatusCode <- 503
        failedContext.Response.Close()
        Assert.That(
            firstDelivery.GetAwaiter().GetResult(),
            Is.EqualTo DeliveryResult.DeliveryFailed
        )

        let siblingRequest = sibling.GetContextAsync()
        let recoveredRequest = recovered.GetContextAsync()

        registerExactSession
            siblingIdentity
            path
            $"http://127.0.0.1:{siblingPort}/"
            (Some sessionId)
        |> ignore

        registerExactSession
            targetIdentity
            path
            $"http://127.0.0.1:{recoveredPort}/"
            (Some sessionId)
        |> ignore

        let drained =
            Task.WhenAny(recoveredRequest, siblingRequest)
                .WaitAsync(TimeSpan.FromSeconds 5.0)
                .GetAwaiter()
                .GetResult()

        Assert.That(
            Object.ReferenceEquals(drained, recoveredRequest),
            Is.True,
            "Re-registering the sibling must leave the exact-target queue untouched"
        )

        let recoveredContext = recoveredRequest.GetAwaiter().GetResult()
        recoveredContext.Response.StatusCode <- 200
        recoveredContext.Response.Close()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type ExactShutdownTests() =

    let options =
        { Timeout = TimeSpan.FromSeconds 2.0
          PollInterval = TimeSpan.FromSeconds 1.0 }

    let targetFor (entry: SessionEntry) : ShutdownTarget =
        { WorktreePath = entry.WorktreePath
          ProcessIdentity = entry.ProcessIdentity }

    let dependencies send closureSnapshot probe now delay : ShutdownDependencies =
        { SendShutdown = fun _ _ -> async { return send }
          ClosureSnapshot = closureSnapshot
          ProbeProcess =
            fun _ _ ->
                async {
                    return probe ()
                }
          Delay = delay
          UtcNow = now }

    [<Test>]
    member _.``Missing exact registration is explicit and sends nothing``() =
        let mutable sent = false
        let identity = nextIdentity ()

        let runtime =
            { SendShutdown =
                fun _ _ ->
                    async {
                        sent <- true
                        return ShutdownRequestOutcome.Accepted
                    }
              ClosureSnapshot =
                fun () -> async { return Ok Set.empty }
              ProbeProcess =
                fun _ _ ->
                    async {
                        return Ok ExactProcessState.Running
                    }
              Delay = fun _ -> async { return () }
              UtcNow = fun () -> DateTime.UtcNow }

        let result =
            shutdownExactWith
                runtime
                options
                { WorktreePath = uniquePath "missing-shutdown"
                  ProcessIdentity = identity }
            |> Async.RunSynchronously

        assertShutdownResult
            (Error ShutdownFailure.MissingRegistration)
            result
        Assert.That(sent, Is.False)

    [<Test>]
    member _.``Stale and reused registrations are rejected before shutdown delivery``() =
        let entry =
            registerExactSession
                (nextIdentity ())
                (uniquePath "stale-shutdown")
                "http://127.0.0.1:1/inject"
                (Some "session-stale")

        let staleRuntime =
            dependencies
                ShutdownRequestOutcome.Accepted
                (fun () -> async { return Ok Set.empty })
                (fun () -> Ok ExactProcessState.Running)
                (fun () -> entry.RegisteredAt + TimeSpan.FromSeconds 60.0)
                (fun _ -> async { return () })

        let reusedRuntime =
            { staleRuntime with
                ProbeProcess =
                    fun _ _ ->
                        async {
                            return Ok ExactProcessState.Reused
                        }
                UtcNow = fun () -> entry.RegisteredAt }

        Assert.Multiple(fun () ->
            assertShutdownResult
                (Error ShutdownFailure.StaleRegistration)
                (
                shutdownExactWith staleRuntime options (targetFor entry)
                |> Async.RunSynchronously
                )
            assertShutdownResult
                (Error ShutdownFailure.StaleRegistration)
                (
                shutdownExactWith reusedRuntime options (targetFor entry)
                |> Async.RunSynchronously
                ))

    [<Test>]
    member _.``Endpoint rejection outcomes remain typed``() =
        let entry =
            registerExactSession
                (nextIdentity ())
                (uniquePath "shutdown-rejections")
                "http://127.0.0.1:1/inject"
                (Some "session-rejections")

        [ ShutdownRequestOutcome.InvalidCapability,
          ShutdownFailure.InvalidCapability,
          LifecycleDiagnostics.ShutdownRejection.InvalidCapability
          ShutdownRequestOutcome.NonLoopbackRequest,
          ShutdownFailure.NonLoopbackRequest,
          LifecycleDiagnostics.ShutdownRejection.NonLoopbackRequest
          ShutdownRequestOutcome.Rejected,
          ShutdownFailure.Rejected,
          LifecycleDiagnostics.ShutdownRejection.Rejected
          ShutdownRequestOutcome.TransportFailed,
          ShutdownFailure.RequestFailed,
          LifecycleDiagnostics.ShutdownRejection.RequestFailed ]
        |> List.iter (fun (outcome, expected, diagnosticOutcome) ->
            let runtime =
                dependencies
                    outcome
                    (fun () -> async { return Ok Set.empty })
                    (fun () -> Ok ExactProcessState.Running)
                    (fun () -> entry.RegisteredAt)
                    (fun _ -> async { return () })

            let diagnostics =
                ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

            assertShutdownResult
                (Error expected)
                (
                shutdownExactWithDiagnostics
                    diagnostics.Enqueue
                    runtime
                    options
                    (targetFor entry)
                |> Async.RunSynchronously
                )

            let stages =
                diagnostics.ToArray()
                |> Array.choose (function
                    | LifecycleDiagnostics.Diagnostic.ShutdownTransition shutdown ->
                        Some shutdown.Stage
                    | _ -> None)

            Assert.That(
                stages,
                Is.EqualTo(
                    [| LifecycleDiagnostics.ShutdownStage.Requested
                       LifecycleDiagnostics.ShutdownStage.Rejected
                           diagnosticOutcome |]
                )
            ))

    [<Test>]
    member _.``Accepted shutdown completes from exact closure``() =
        let entry =
            registerExactSession
                (nextIdentity ())
                (uniquePath "shutdown-closure")
                "http://127.0.0.1:1/inject"
                (Some "session-closure")

        let runtime =
            dependencies
                ShutdownRequestOutcome.Accepted
                (fun () ->
                    async {
                        return
                            Ok(
                                Set.singleton
                                    entry.ProcessIdentity
                            )
                    })
                (fun () -> Ok ExactProcessState.Running)
                (fun () -> entry.RegisteredAt)
                (fun _ -> async { return () })

        let diagnostics =
            ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

        assertShutdownResult
            (Ok ShutdownCompletion.ExactClosure)
            (
            shutdownExactWithDiagnostics
                diagnostics.Enqueue
                runtime
                options
                (targetFor entry)
            |> Async.RunSynchronously
            )

        let stages =
            diagnostics.ToArray()
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.ShutdownTransition shutdown ->
                    Some shutdown.Stage
                | _ -> None)

        Assert.That(
            stages,
            Is.EqualTo(
                [| LifecycleDiagnostics.ShutdownStage.Requested
                   LifecycleDiagnostics.ShutdownStage.RequestAccepted
                   LifecycleDiagnostics.ShutdownStage.CompletedExactClosure |]
            )
        )

    [<Test>]
    member _.``Production shutdown transport posts the opaque capability and then observes closure``() =
        let identity = nextIdentity ()
        let path = uniquePath "shutdown-http"
        let capability = capabilityFor identity
        let port = Tests.TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let request =
            { registrationRequest
                identity
                path
                "http://127.0.0.1:1/inject"
                (Some "session-http-shutdown")
                None
                capability with
                ShutdownUrl = $"http://127.0.0.1:{port}/" }

        let entry =
            registerSession (resolverFor identity) request
            |> Result.defaultWith (fun failure -> invalidOp $"registration failed: {failure}")

        let received = listener.GetContextAsync()
        let shutdown =
            shutdownExact
                (fun () ->
                    async {
                        return Ok(Set.singleton identity)
                    })
                (targetFor entry)
            |> Async.StartAsTask

        let context =
            received
                .WaitAsync(TimeSpan.FromSeconds 5.0)
                .GetAwaiter()
                .GetResult()

        use reader = new StreamReader(context.Request.InputStream)
        use body = JsonDocument.Parse(reader.ReadToEnd())
        context.Response.StatusCode <- 202
        context.Response.Close()

        Assert.That(
            body.RootElement.GetProperty("capability").GetString(),
            Is.EqualTo capability
        )
        assertShutdownResult
            (Ok ShutdownCompletion.ExactClosure)
            (shutdown.GetAwaiter().GetResult())

    [<Test>]
    member _.``Accepted shutdown completes when the exact process exits``() =
        let entry =
            registerExactSession
                (nextIdentity ())
                (uniquePath "shutdown-exit")
                "http://127.0.0.1:1/inject"
                (Some "session-exit")

        // The first probe verifies the registration before delivery; the second observes exit.
        let mutable probeCount = 0
        let runtime =
            dependencies
                ShutdownRequestOutcome.Accepted
                (fun () -> async { return Ok Set.empty })
                (fun () ->
                    probeCount <- probeCount + 1
                    if probeCount = 1 then
                        Ok ExactProcessState.Running
                    else
                        Ok ExactProcessState.Exited)
                (fun () -> entry.RegisteredAt)
                (fun _ -> async { return () })

        assertShutdownResult
            (Ok ShutdownCompletion.ProcessExit)
            (
            shutdownExactWith runtime options (targetFor entry)
            |> Async.RunSynchronously
            )

    [<Test>]
    member _.``Accepted shutdown times out while closure and process exit remain absent``() =
        let entry =
            registerExactSession
                (nextIdentity ())
                (uniquePath "shutdown-timeout")
                "http://127.0.0.1:1/inject"
                (Some "session-timeout")

        // The injected clock advances only through Delay, making timeout behavior deterministic.
        let mutable now = entry.RegisteredAt
        let runtime =
            dependencies
                ShutdownRequestOutcome.Accepted
                (fun () -> async { return Ok Set.empty })
                (fun () -> Ok ExactProcessState.Running)
                (fun () -> now)
                (fun interval ->
                    async {
                        now <- now + interval
                    })

        let diagnostics =
            ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

        assertShutdownResult
            (Error ShutdownFailure.TimedOut)
            (
            shutdownExactWithDiagnostics
                diagnostics.Enqueue
                runtime
                options
                (targetFor entry)
            |> Async.RunSynchronously
            )

        let stages =
            diagnostics.ToArray()
            |> Array.choose (function
                | LifecycleDiagnostics.Diagnostic.ShutdownTransition shutdown ->
                    Some shutdown.Stage
                | _ -> None)

        Assert.That(
            stages,
            Is.EqualTo(
                [| LifecycleDiagnostics.ShutdownStage.Requested
                   LifecycleDiagnostics.ShutdownStage.RequestAccepted
                   LifecycleDiagnostics.ShutdownStage.TimedOut |]
            )
        )

    [<Test>]
    member _.``Bulk shutdown shares polling and bounds request and process operations``() =
        let path = uniquePath "bulk-shutdown"
        let entries =
            [ 0..99 ]
            |> List.map (fun index ->
                let identity = nextIdentity ()

                registrationRequest
                    identity
                    path
                    "http://127.0.0.1:1/inject"
                    (Some $"batch-session-{index}")
                    None
                    (capabilityFor identity)
                |> registerSessionWithDiagnostics
                    ignore
                    (resolverFor identity)
                |> Result.defaultWith (fun failure ->
                    invalidOp $"registration failed: {failure}"))

        let startedAt =
            entries
            |> List.maxBy _.RegisteredAt
            |> _.RegisteredAt

        let targets = entries |> List.map targetFor

        let indexByIdentity =
            entries
            |> List.indexed
            |> List.map (fun (index, entry) ->
                entry.ProcessIdentity, index)
            |> Map.ofList

        let indexByCapability =
            entries
            |> List.indexed
            |> List.map (fun (index, entry) ->
                capabilityFor entry.ProcessIdentity, index)
            |> Map.ofList

        let closedProcesses =
            entries
            |> List.take 30
            |> List.map _.ProcessIdentity
            |> Set.ofList

        let probeCounts =
            ConcurrentDictionary<ProcessIdentity, int>()

        let probeRelease =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )

        let requestRelease =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )

        let probeGate, requestGate, clockGate =
            obj (), obj (), obj ()

        // Mutable counters and clock expose the injected concurrency and timing boundaries.
        let mutable activeProbes = 0
        let mutable maxProbes = 0
        let mutable activeRequests = 0
        let mutable maxRequests = 0
        let mutable closureSnapshots = 0
        let mutable delays = 0
        let mutable now = startedAt

        let track
            (gate: obj)
            (release: TaskCompletionSource<unit>)
            (increment: unit -> int)
            (decrement: unit -> unit)
            (updateMaximum: int -> unit)
            result
            =
            async {
                let active =
                    lock gate (fun () ->
                        let current = increment ()
                        updateMaximum current
                        current)

                if active = maxConcurrentShutdownOperations then
                    release.TrySetResult() |> ignore

                do!
                    release.Task
                        .WaitAsync(TimeSpan.FromSeconds 5.0)
                    |> Async.AwaitTask

                let completed = result ()

                lock gate decrement
                return completed
            }

        let runtime: ShutdownDependencies =
            { SendShutdown =
                fun _ capability ->
                    track
                        requestGate
                        requestRelease
                        (fun () ->
                            activeRequests <- activeRequests + 1
                            activeRequests)
                        (fun () ->
                            activeRequests <- activeRequests - 1)
                        (fun active ->
                            maxRequests <- max maxRequests active)
                        (fun () ->
                            match indexByCapability[capability] with
                            | index when index < 80 ->
                                ShutdownRequestOutcome.Accepted
                            | index when index < 90 ->
                                ShutdownRequestOutcome.Rejected
                            | _ ->
                                ShutdownRequestOutcome.TransportFailed)
              ClosureSnapshot =
                fun () ->
                    async {
                        lock clockGate (fun () ->
                            closureSnapshots <- closureSnapshots + 1)

                        return Ok closedProcesses
                    }
              ProbeProcess =
                fun _ identity ->
                    track
                        probeGate
                        probeRelease
                        (fun () ->
                            activeProbes <- activeProbes + 1
                            activeProbes)
                        (fun () ->
                            activeProbes <- activeProbes - 1)
                        (fun active ->
                            maxProbes <- max maxProbes active)
                        (fun () ->
                            let count =
                                probeCounts.AddOrUpdate(
                                    identity,
                                    1,
                                    fun _ current -> current + 1
                                )

                            let index = indexByIdentity[identity]

                            if
                                count > 1
                                && index >= 30
                                && index < 60
                            then
                                Ok ExactProcessState.Exited
                            else
                                Ok ExactProcessState.Running)
              Delay =
                fun interval ->
                    async {
                        Assert.That(
                            interval,
                            Is.EqualTo(
                                TimeSpan.FromMilliseconds 100.0
                            )
                        )

                        lock clockGate (fun () ->
                            delays <- delays + 1
                            now <- startedAt + TimeSpan.FromSeconds 30.0)
                    }
              UtcNow =
                fun () ->
                    lock clockGate (fun () -> now) }

        let attempts =
            shutdownExactBatchWithDiagnostics
                ignore
                runtime
                { Timeout = TimeSpan.FromSeconds 30.0
                  PollInterval = TimeSpan.FromMilliseconds 100.0 }
                targets
            |> Async.RunSynchronously

        let expected =
            [ 0..99 ]
            |> List.map (function
                | index when index < 30 ->
                    Ok ShutdownCompletion.ExactClosure
                | index when index < 60 ->
                    Ok ShutdownCompletion.ProcessExit
                | index when index < 80 ->
                    Error ShutdownFailure.TimedOut
                | index when index < 90 ->
                    Error ShutdownFailure.Rejected
                | _ ->
                    Error ShutdownFailure.RequestFailed)

        Assert.Multiple(fun () ->
            Assert.That(
                attempts |> List.map _.Outcome,
                Is.EqualTo expected
            )
            Assert.That(closureSnapshots, Is.EqualTo 2)
            Assert.That(delays, Is.EqualTo 1)
            Assert.That(
                maxProbes,
                Is.EqualTo maxConcurrentShutdownOperations
            )
            Assert.That(
                maxRequests,
                Is.EqualTo maxConcurrentShutdownOperations
            )
            Assert.That(
                now,
                Is.EqualTo(startedAt + TimeSpan.FromSeconds 30.0)
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PromptTransportTests() =

    [<Test>]
    member _.``Canvas prompt transport has an explicit canvas kind``() =
        Assert.That(
            serializePrompt (Prompt.canvas """{"action":"refresh"}"""),
            Is.EqualTo("""{"kind":"canvas","prompt":"{\u0022action\u0022:\u0022refresh\u0022}"}"""))

    [<Test>]
    member _.``Generic agent prompt transport has an explicit agent-prompt kind``() =
        Assert.That(
            serializePrompt (Prompt.agentPrompt "Sync with upstream/main when safe."),
            Is.EqualTo("""{"kind":"agent-prompt","prompt":"Sync with upstream/main when safe."}"""))

    [<Test>]
    member _.``Untargeted agent prompt uses the only live bridge``() =
        let path = uniquePath "unique-live"
        let sessionId = $"session-{Guid.NewGuid():N}"
        let port = Tests.TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()
        registerTestSession path $"http://127.0.0.1:{port}/" (Some sessionId)

        let received = listener.GetContextAsync()
        let delivery =
            tryDeliver
                { WorktreePath = path
                  Target = SendTarget.Unspecified
                  Prompt = Prompt.agentPrompt "sync" }
            |> Async.StartAsTask

        let context = received.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        context.Response.StatusCode <- 200
        context.Response.Close()

        Assert.That(delivery.GetAwaiter().GetResult(), Is.EqualTo(DeliveryResult.Delivered))

    [<Test>]
    member _.``Untargeted agent prompt remains ambiguous with multiple live bridges``() =
        let path = uniquePath "ambiguous-live"
        let firstPort, secondPort =
            match Tests.TestUtils.getFreeTcpPorts 2 with
            | [ first; second ] -> first, second
            | ports -> failwith $"expected two free ports, got {ports.Length}"

        use first = new HttpListener()
        use second = new HttpListener()
        first.Prefixes.Add($"http://127.0.0.1:{firstPort}/")
        second.Prefixes.Add($"http://127.0.0.1:{secondPort}/")
        first.Start()
        second.Start()
        registerTestSession path $"http://127.0.0.1:{firstPort}/" (Some $"first-{Guid.NewGuid():N}")
        registerTestSession path $"http://127.0.0.1:{secondPort}/" (Some $"second-{Guid.NewGuid():N}")

        let result =
            tryDeliver
                { WorktreePath = path
                  Target = SendTarget.Unspecified
                  Prompt = Prompt.agentPrompt "sync" }
            |> Async.RunSynchronously

        Assert.That(result, Is.EqualTo(DeliveryResult.NoLiveSession))

    [<Test>]
    member _.``Anonymous canvas prompt still queues for canvas polling``() =
        let path = uniquePath "canvas-poll"
        let payload = """{"action":"refresh"}"""
        let port = Tests.TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()
        registerTestSession path $"http://127.0.0.1:{port}/" (Some $"canvas-{Guid.NewGuid():N}")

        let unexpectedPost = listener.GetContextAsync()
        let sending =
            send
                { WorktreePath = path
                  Target = SendTarget.Unspecified
                  Prompt = Prompt.canvas payload }
            |> Async.StartAsTask
        let firstCompleted =
            Task.WhenAny(sending, unexpectedPost).WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        if Object.ReferenceEquals(firstCompleted, unexpectedPost) then
            let context = unexpectedPost.GetAwaiter().GetResult()
            context.Response.StatusCode <- 200
            context.Response.Close()

        let result = sending.GetAwaiter().GetResult()

        Assert.That(result, Is.EqualTo(SendResult.Queued))
        Assert.That(drainPendingCanvas path, Is.EqualTo [ Prompt.canvas payload ])

    [<Test>]
    member _.``Canvas heartbeat drain does not consume generic agent prompts``() =
        let path = uniquePath "kind-drain"

        let result =
            send
                { WorktreePath = path
                  Target = SendTarget.Unspecified
                  Prompt = Prompt.agentPrompt "sync" }
            |> Async.RunSynchronously

        Assert.That(result, Is.EqualTo(SendResult.Queued))
        Assert.That(drainPendingCanvas path, Is.Empty)

    [<Test>]
    member _.``Bridge failure formatting excludes the response body``() =
        let secretBody = $"first line{Environment.NewLine}secret-token=abc123"
        let failure = formatPostFailure 503 secretBody

        Assert.Multiple(fun () ->
            Assert.That(failure, Does.Contain("status=503"))
            Assert.That(failure, Does.Contain($"bodyLength={secretBody.Length}"))
            Assert.That(failure, Does.Not.Contain("first line"))
            Assert.That(failure, Does.Not.Contain("secret-token")))
