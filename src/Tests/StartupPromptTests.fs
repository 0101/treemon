module Tests.StartupPromptTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Text.Json
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open NUnit.Framework
open Shared
open Server
open Server.SessionActivity
open Tests.BridgeFixture
open Tests.TestUtils

let private await (task: Task<'a>) =
    task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

let private defaultTerminalId = EmbeddedTerminalId "6e4d33254b564bde97b723a40721351c"

let private registerFor terminalId path sessionId url =
    let identity =
        Guid.NewGuid().ToString()
        |> collisionResistantProcessIdentityForSessionId

    registerExactSession 'S' identity path url sessionId (Some(EmbeddedTerminalId.value terminalId)) |> ignore

let private register path sessionId url =
    registerFor defaultTerminalId path sessionId url

let private respond status (context: HttpListenerContext) =
    context.Response.StatusCode <- status
    context.Response.Close()

let private startedWith terminalId path =
    { TerminalId = terminalId
      Snapshot =
        { Tabs =
            [ { Id = terminalId
                Worktree = path
                ReportedActivity = None
                SessionIds = []
                Lifecycle = EmbeddedTerminalLifecycle.Running "http://127.0.0.1:41001/" } ] } }

let private started path = startedWith defaultTerminalId path

let private start timeout launch close path prompt =
    TerminalLaunch.startPromptedAgentWith
        { StartCommand = launch
          CloseTerminal = close
          Timeout = timeout }
        None
        None
        path
        prompt
    |> Async.StartAsTask

let private assertStarted expected (result: Result<EmbeddedTerminalStartResult, PromptedLaunchError>) =
    match result with
    | Ok actual -> Assert.That(actual, Is.EqualTo expected)
    | Error error -> Assert.Fail($"Startup failed: {PromptedLaunchError.message error}")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("StartupPrompt")>]
[<Category("BridgeTransport")>]
[<NonParallelizable>]
type StartupPromptTests() =

    [<TestCase("AgentDoc", "")>]
    [<TestCase("SystemView", "")>]
    [<TestCase("worktree", "")>]
    [<TestCase("worktree", "gpt-6.1-sol")>]
    member _.``full initial prompt crosses the bridge and launch waits for acceptance``(kind: string, model: string) =
        withBridges 1 (fun bridges ->
            let selectedModel = if model = "" then None else Some model
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "owner's repo with spaces & %PATH%")
            let sessionId = Guid.NewGuid().ToString()
            let expectedStart = started path
            let commands = ConcurrentQueue<WorktreePath * string>()
            let closes = ConcurrentQueue<EmbeddedTerminalId>()
            let prompt =
                match kind with
                | "AgentDoc" -> CanvasSessionPrompt.forAgentDoc (WorktreePath.value path) "selected.html"
                | "SystemView" -> CanvasPrompt.continueWorking (WorktreePath.value path) "diff.html"
                | "worktree" ->
                    CodingToolStatus.skillInvocation
                        None
                        "investigate"
                        "Preserve 'quotes', \"JSON\", $variables, and %PATH%.\r\nKeep this second line.\nAnd this third line."
                | other -> invalidOp $"Unknown startup fixture: {other}"

            let incoming = listener.GetContextAsync()
            let launched =
                TerminalLaunch.startPromptedAgentWith
                    { StartCommand =
                        fun requestedPath command ->
                            async {
                                commands.Enqueue(requestedPath, command)
                                register (WorktreePath.value path) (Some sessionId) url
                                return Ok expectedStart
                            }
                      CloseTerminal =
                        fun id ->
                            closes.Enqueue id
                            async.Return(Ok EmbeddedTerminalSnapshot.empty)
                      Timeout = TimeSpan.FromSeconds 5.0 }
                    None
                    selectedModel
                    path
                    prompt
                |> Async.StartAsTask

            let context = await incoming
            let wireKind, deliveredPrompt = readPrompt context

            Assert.Multiple(fun () ->
                Assert.That(wireKind, Is.EqualTo "startup-prompt")
                Assert.That(deliveredPrompt, Is.EqualTo prompt)
                Assert.That(launched.IsCompleted, Is.False, "HTTP queueing is not SDK acceptance")
                Assert.That(
                    commands.ToArray(),
                    Is.EqualTo(
                        [| path,
                           if model = "" then "copilot --experimental --yolo"
                           else $"copilot --experimental --yolo --model='{model}'" |]))
                Assert.That(closes, Is.Empty))

            if kind = "AgentDoc" || kind = "SystemView" then
                let identityJson =
                    deliveredPrompt.Split('\n')
                    |> Array.find _.StartsWith("{\"worktreePath\":")
                use identity = JsonDocument.Parse(identityJson)
                Assert.Multiple(fun () ->
                    Assert.That(
                        identity.RootElement.GetProperty("worktreePath").GetString(),
                        Is.EqualTo(WorktreePath.value path))
                    Assert.That(
                        identity.RootElement.GetProperty("filename").GetString(),
                        Is.EqualTo(if kind = "AgentDoc" then "selected.html" else "diff.html")))

            respond 200 context
            await launched |> assertStarted expectedStart)

    [<Test>]
    member _.``concurrent starts deliver only to their returned terminals``() =
        withBridges 2 (fun bridges ->
            let firstListener, firstUrl = bridges[0]
            let secondListener, secondUrl = bridges[1]
            let path = WorktreePath(uniquePath "concurrent-startup")
            let firstId = Guid.NewGuid().ToString()
            let secondId = Guid.NewGuid().ToString()
            let firstTerminal = EmbeddedTerminalId(Guid.NewGuid().ToString("N"))
            let secondTerminal = EmbeddedTerminalId(Guid.NewGuid().ToString("N"))
            let firstStart = startedWith firstTerminal path
            let secondStart = startedWith secondTerminal path
            use ready = new System.Threading.CountdownEvent(2)
            let launch id expectedStart =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        ready.Signal() |> ignore
                        async.Return(Ok expectedStart))
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    $"Initial task for {id}"

            let first = launch firstId firstStart
            let second = launch secondId secondStart
            Assert.That(
                ready.Wait(TimeSpan.FromSeconds 5.0),
                Is.True)

            let secondRequest = secondListener.GetContextAsync()
            register (WorktreePath.value path) (Some "unrelated-session") firstUrl
            registerFor secondTerminal (WorktreePath.value path) (Some secondId) secondUrl
            let secondContext = await secondRequest
            Assert.That(readPrompt secondContext, Is.EqualTo(("startup-prompt", $"Initial task for {secondId}")))
            respond 200 secondContext
            await second |> assertStarted secondStart
            Assert.That(first.IsCompleted, Is.False)

            let firstRequest = firstListener.GetContextAsync()
            registerFor firstTerminal (WorktreePath.value path) (Some firstId) firstUrl
            let firstContext = await firstRequest
            Assert.That(readPrompt firstContext, Is.EqualTo(("startup-prompt", $"Initial task for {firstId}")))
            respond 200 firstContext
            await first |> assertStarted firstStart)

    [<Test>]
    member _.``early registration cannot receive startup or interactions before terminal binding``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "early-startup-registration")
            let sessionId = Guid.NewGuid().ToString()
            let expectedStart = started path
            let registered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseLaunch = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let incoming = listener.GetContextAsync()
            let launched =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        async {
                            register (WorktreePath.value path) (Some sessionId) url
                            registered.TrySetResult() |> ignore
                            do! releaseLaunch.Task |> Async.AwaitTask
                            return Ok expectedStart
                        })
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    "Initial task"

            try
                await registered.Task
                SessionBridge.send System.Threading.CancellationToken.None
                    { WorktreePath = WorktreePath.value path
                      Target = SessionBridge.SendTarget.DurableSession(SessionId sessionId)
                      Prompt = SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" "Follow-up" }
                |> Async.RunSynchronously
                |> ignore
                SessionBridge.flushPending (WorktreePath.value path) |> Async.RunSynchronously
                Assert.That(incoming.IsCompleted, Is.False, "An unbound launch must not send")

                releaseLaunch.TrySetResult() |> ignore
                let first = await incoming
                Assert.That(readPrompt first, Is.EqualTo(("startup-prompt", "Initial task")))
                respond 200 first
                let second = await (listener.GetContextAsync())
                Assert.That(readPrompt second, Is.EqualTo(("canvas", "Follow-up")))
                respond 200 second
                await launched |> assertStarted expectedStart
            finally
                releaseLaunch.TrySetResult() |> ignore)

    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.``startup ignores a bridge with the wrong terminal or worktree``(wrongTerminal: bool) =
        withBridges 2 (fun bridges ->
            let wrongListener, wrongUrl = bridges[0]
            let listener, url = bridges[1]
            let path = WorktreePath(uniquePath "startup-exact-terminal")
            let sessionId = Guid.NewGuid().ToString()
            let expectedStart = started path
            let launchEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let launched =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        launchEntered.TrySetResult() |> ignore
                        async.Return(Ok expectedStart))
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    "Initial task"
            await launchEntered.Task
            let wrongRequest = wrongListener.GetContextAsync()
            let wrongPath =
                if wrongTerminal then WorktreePath.value path
                else uniquePath "another-startup-worktree"
            let terminal =
                if wrongTerminal then EmbeddedTerminalId(Guid.NewGuid().ToString("N"))
                else expectedStart.TerminalId
            registerFor terminal wrongPath (Some sessionId) wrongUrl
            SessionBridge.flushPending (WorktreePath.value path) |> Async.RunSynchronously
            Assert.Multiple(fun () ->
                Assert.That(wrongRequest.IsCompleted, Is.False)
                Assert.That(launched.IsCompleted, Is.False))

            let incoming = listener.GetContextAsync()
            register (WorktreePath.value path) (Some sessionId) url
            let context = await incoming
            Assert.That(readPrompt context, Is.EqualTo(("startup-prompt", "Initial task")))
            respond 200 context
            await launched |> assertStarted expectedStart)

    [<Test>]
    member _.``ambiguous terminal registrations do not consume the initial prompt``() =
        withBridges 2 (fun bridges ->
            let firstListener, firstUrl = bridges[0]
            let secondListener, secondUrl = bridges[1]
            let path = WorktreePath(uniquePath "ambiguous-startup-terminal")
            let expectedStart = started path
            let registered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseLaunch = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let firstRequest = firstListener.GetContextAsync()
            let secondRequest = secondListener.GetContextAsync()
            let launched =
                start
                    (TimeSpan.FromSeconds 1.0)
                    (fun _ _ ->
                        async {
                            register (WorktreePath.value path) (Some(Guid.NewGuid().ToString())) firstUrl
                            register (WorktreePath.value path) (Some(Guid.NewGuid().ToString())) secondUrl
                            registered.TrySetResult() |> ignore
                            do! releaseLaunch.Task |> Async.AwaitTask
                            return Ok expectedStart
                        })
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    "Initial task"
            try
                await registered.Task
                releaseLaunch.TrySetResult() |> ignore
                Assert.That(
                    await launched,
                    Is.EqualTo(
                        Error(PromptedLaunchError.StartupFailed StartupPromptFailure.TimedOut)
                        : Result<EmbeddedTerminalStartResult, PromptedLaunchError>))
                SessionBridge.flushPending (WorktreePath.value path) |> Async.RunSynchronously
                Assert.Multiple(fun () ->
                    Assert.That(firstRequest.IsCompleted, Is.False)
                    Assert.That(secondRequest.IsCompleted, Is.False))
            finally
                releaseLaunch.TrySetResult() |> ignore)

    [<TestCase("canvas")>]
    [<TestCase("agent-prompt")>]
    member _.``ambiguous startup holds ordinary prompts only for its terminal``(kind: string) =
        withBridges 3 (fun bridges ->
            let listener, url = bridges[0]
            let conflictingListener, conflictingUrl = bridges[1]
            let otherListener, otherUrl = bridges[2]
            let path = uniquePath "ambiguous-startup-ordering"
            let sessionId = Guid.NewGuid().ToString()
            let conflictingSessionId = Guid.NewGuid().ToString()
            let otherSessionId = Guid.NewGuid().ToString()
            let otherTerminal = EmbeddedTerminalId(Guid.NewGuid().ToString("N"))
            let expectedStart = started (WorktreePath path)
            register path (Some sessionId) url
            register path (Some conflictingSessionId) conflictingUrl
            registerFor otherTerminal path (Some otherSessionId) otherUrl
            let incoming = listener.GetContextAsync()
            let conflictingRequest = conflictingListener.GetContextAsync()
            let launched =
                SessionBridge.startWithPrompt
                    (TimeSpan.FromSeconds 5.0)
                    path
                    "Initial task"
                    (fun () -> async.Return(Ok expectedStart))
                |> Async.StartImmediateAsTask
            let send target text =
                SessionBridge.send System.Threading.CancellationToken.None
                    { WorktreePath = path
                      Target = SessionBridge.SendTarget.DurableSession(SessionId target)
                      Prompt =
                        match kind with
                        | "canvas" -> SessionBridge.Prompt.canvasFor path "diff.html" text
                        | "agent-prompt" -> SessionBridge.Prompt.agentPrompt text
                        | other -> invalidOp $"Unknown prompt fixture: {other}" }
                |> Async.StartAsTask

            let followUp = send sessionId "Follow-up"
            Task.WhenAny(followUp :> Task, incoming :> Task) |> await |> ignore
            Assert.Multiple(fun () ->
                Assert.That(incoming.IsCompleted, Is.False, "An ambiguous startup must hold later instructions")
                Assert.That(conflictingRequest.IsCompleted, Is.False)
                Assert.That(launched.IsCompleted, Is.False))
            Assert.That(await followUp, Is.EqualTo SessionBridge.SendResult.Queued)
            SessionBridge.flushPending path |> Async.RunSynchronously

            let otherRequest = otherListener.GetContextAsync()
            let otherSend = send otherSessionId "Other terminal instruction"
            let otherContext = await otherRequest
            Assert.That(readPrompt otherContext, Is.EqualTo((kind, "Other terminal instruction")))
            respond 200 otherContext
            Assert.That(await otherSend, Is.EqualTo SessionBridge.SendResult.Delivered)

            registerFor otherTerminal path (Some conflictingSessionId) conflictingUrl
            let startup = await incoming
            Assert.That(readPrompt startup, Is.EqualTo(("startup-prompt", "Initial task")))
            let followUpRequest = listener.GetContextAsync()
            Assert.That(followUpRequest.IsCompleted, Is.False, "Later instructions must wait for startup acceptance")
            respond 200 startup
            let followUpContext = await followUpRequest
            Assert.That(readPrompt followUpContext, Is.EqualTo((kind, "Follow-up")))
            respond 200 followUpContext
            Assert.That(
                await launched,
                Is.EqualTo(SessionBridge.StartupResult.Accepted expectedStart))
            SessionBridge.flushPending path |> Async.RunSynchronously
            Assert.Multiple(fun () ->
                Assert.That(conflictingRequest.IsCompleted, Is.False)
                Assert.That(
                    SessionBridge.pendingPrompts path |> Async.RunSynchronously,
                    Is.Empty)))

    [<TestCase(false, "exact")>]
    [<TestCase(false, "retained")>]
    [<TestCase(false, "unspecified")>]
    [<TestCase(true, "exact")>]
    [<TestCase(true, "retained")>]
    [<TestCase(true, "unspecified")>]
    member _.``AutoSync retains instructions during startup without a fallback launch``(bound: bool, targetKind: string) =
        withBridges 2 (fun bridges ->
            let listener, url = bridges[0]
            let _, conflictingUrl = bridges[1]
            let path = uniquePath "auto-sync-startup"
            let sessionId = Guid.NewGuid().ToString()
            let conflictingSessionId = Guid.NewGuid().ToString()
            let identity = collisionResistantProcessIdentityForSessionId sessionId
            registerExactSession 'S' identity path url (Some sessionId)
                (Some(EmbeddedTerminalId.value defaultTerminalId))
            |> ignore
            register path (Some conflictingSessionId) conflictingUrl
            let expectedStart = started (WorktreePath path)
            let incoming = listener.GetContextAsync()
            let calls = ConcurrentQueue<string>()
            let releaseLaunch = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let launched =
                SessionBridge.startWithPrompt
                    (TimeSpan.FromSeconds 5.0)
                    path
                    "Initial task"
                    (fun () ->
                        async {
                            if not bound then
                                do! releaseLaunch.Task |> Async.AwaitTask
                            return Ok expectedStart
                        })
                |> Async.StartImmediateAsTask
            let target =
                match targetKind with
                | "exact" -> AutoSync.IdleSession(identity, SessionId sessionId)
                | "retained" -> AutoSync.NoOpenSession(Some(SessionId sessionId))
                | "unspecified" -> AutoSync.NoOpenSession None
                | other -> invalidOp $"Unknown AutoSync target fixture: {other}"
            let instruction = "Sync with upstream/main."

            try
                let accepted =
                    AutoSync.deliver
                        SessionBridge.tryDeliver
                        (fun () -> async { calls.Enqueue "registration-grace" })
                        (fun _ _ ->
                            async {
                                calls.Enqueue "fallback-launch"
                                return Ok()
                            })
                        { WorktreePath = WorktreePath path
                          Target = target
                          Prompt = instruction }
                    |> Async.RunSynchronously

                Assert.Multiple(fun () ->
                    Assert.That(accepted, Is.True)
                    Assert.That(calls, Is.Empty, "Pending startup is not an absent bridge")
                    Assert.That(incoming.IsCompleted, Is.False)
                    Assert.That(
                        SessionBridge.pendingPrompts path |> Async.RunSynchronously |> List.map _.Prompt.Text,
                        Is.EqualTo([ "Initial task"; instruction ])))

                releaseLaunch.TrySetResult() |> ignore
                register (uniquePath "relocated-startup-registration") (Some conflictingSessionId) conflictingUrl
                SessionBridge.retryPending path
                let startup = await incoming
                Assert.That(readPrompt startup, Is.EqualTo(("startup-prompt", "Initial task")))
                let followUp = listener.GetContextAsync()
                Assert.That(followUp.IsCompleted, Is.False)
                respond 200 startup
                let followUpContext = await followUp
                Assert.That(readPrompt followUpContext, Is.EqualTo(("agent-prompt", instruction)))
                respond 200 followUpContext
                Assert.That(await launched, Is.EqualTo(SessionBridge.StartupResult.Accepted expectedStart))
                SessionBridge.flushPending path |> Async.RunSynchronously
                Assert.That(SessionBridge.pendingPrompts path |> Async.RunSynchronously, Is.Empty)
            finally
                releaseLaunch.TrySetResult() |> ignore)

    [<Test>]
    member _.``failed launch releases interactions held for startup binding``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "failed-startup-releases-interactions")
            let sessionId = Guid.NewGuid().ToString()
            let registered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseLaunch = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let incoming = listener.GetContextAsync()
            let launched =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        async {
                            register (WorktreePath.value path) (Some sessionId) url
                            registered.TrySetResult() |> ignore
                            do! releaseLaunch.Task |> Async.AwaitTask
                            return Error "shell launch failed"
                        })
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    "Initial task"
            try
                await registered.Task
                SessionBridge.send System.Threading.CancellationToken.None
                    { WorktreePath = WorktreePath.value path
                      Target = SessionBridge.SendTarget.DurableSession(SessionId sessionId)
                      Prompt = SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" "Preserved interaction" }
                |> Async.RunSynchronously
                |> ignore
                SessionBridge.flushPending (WorktreePath.value path) |> Async.RunSynchronously
                Assert.That(incoming.IsCompleted, Is.False)

                releaseLaunch.TrySetResult() |> ignore
                Assert.That(
                    await launched,
                    Is.EqualTo(
                        Error PromptedLaunchError.TerminalStartFailed
                        : Result<EmbeddedTerminalStartResult, PromptedLaunchError>))
                let context = await incoming
                Assert.That(readPrompt context, Is.EqualTo(("canvas", "Preserved interaction")))
                respond 200 context
                SessionBridge.flushPending (WorktreePath.value path) |> Async.RunSynchronously
                Assert.That(
                    SessionBridge.pendingPrompts (WorktreePath.value path) |> Async.RunSynchronously,
                    Is.Empty)
            finally
                releaseLaunch.TrySetResult() |> ignore)

    [<Test>]
    member _.``startup instruction precedes the queued SystemView interaction``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "startup-before-canvas")
            let sessionId = Guid.NewGuid().ToString()
            let initial = CanvasPrompt.continueWorking (WorktreePath.value path) "diff.html"
            let interaction = """{"action":"canvas-selection","request":"Explain the change"}"""

            SessionBridge.send System.Threading.CancellationToken.None
                { WorktreePath = WorktreePath.value path
                  Target = SessionBridge.SendTarget.DurableSession(SessionId sessionId)
                  Prompt = SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" interaction }
            |> Async.RunSynchronously
            |> ignore

            let incoming = listener.GetContextAsync()
            let launched =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        register (WorktreePath.value path) (Some sessionId) url
                        async.Return(Ok(started path)))
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    initial

            let first = await incoming
            Assert.That(readPrompt first, Is.EqualTo(("startup-prompt", initial)))
            respond 200 first
            let second = await (listener.GetContextAsync())
            Assert.That(readPrompt second, Is.EqualTo(("canvas", interaction)))
            respond 200 second
            await launched |> assertStarted (started path))

    [<Test>]
    member _.``cancellation during startup does not requeue the failed interaction``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "cancel-in-flight-startup")
            let sessionId = Guid.NewGuid().ToString()
            use queueCancellation = new System.Threading.CancellationTokenSource()
            let failedInteraction = """{"action":"canvas-selection","request":"Retry this request"}"""
            let preservedInteraction = """{"action":"canvas-selection","request":"Keep this request"}"""
            let queue token payload =
                SessionBridge.send token
                    { WorktreePath = WorktreePath.value path
                      Target = SessionBridge.SendTarget.DurableSession(SessionId sessionId)
                      Prompt = SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" payload }
                |> Async.RunSynchronously
                |> ignore

            queue queueCancellation.Token failedInteraction
            queue System.Threading.CancellationToken.None preservedInteraction
            let incoming = listener.GetContextAsync()
            let launched =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        register (WorktreePath.value path) (Some sessionId) url
                        async.Return(Ok(started path)))
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    "Initial task"

            let startup = await incoming
            Assert.That(readPrompt startup, Is.EqualTo(("startup-prompt", "Initial task")))
            queueCancellation.Cancel()
            respond 503 startup
            Assert.That(
                await launched,
                Is.EqualTo(
                    Error(PromptedLaunchError.StartupFailed StartupPromptFailure.Rejected)
                    : Result<EmbeddedTerminalStartResult, PromptedLaunchError>))

            queue System.Threading.CancellationToken.None failedInteraction
            register (WorktreePath.value path) (Some sessionId) url
            let delivered =
                [ 1 .. 2 ]
                |> List.map (fun _ ->
                    let context = await (listener.GetContextAsync())
                    let prompt = readPrompt context
                    respond 200 context
                    prompt)

            Assert.That(
                delivered |> List.toArray,
                Is.EqualTo([| ("canvas", preservedInteraction); ("canvas", failedInteraction) |])))

    [<TestCase(10)>]
    [<TestCase(12)>]
    member _.``ordinary queue pressure preserves the required startup instruction``(messageCount: int) =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "startup-queue-pressure")
            let sessionId = Guid.NewGuid().ToString()
            let initial = CanvasPrompt.continueWorking (WorktreePath.value path) "diff.html"
            let launchEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseLaunch = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let closes = ConcurrentQueue<EmbeddedTerminalId>()
            let incoming = listener.GetContextAsync()
            let launched =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        async {
                            launchEntered.TrySetResult() |> ignore
                            do! releaseLaunch.Task |> Async.AwaitTask
                            register (WorktreePath.value path) (Some sessionId) url
                            return Ok(started path)
                        })
                    (fun id ->
                        closes.Enqueue id
                        async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    initial

            try
                await launchEntered.Task
                let interactions =
                    [ 1 .. messageCount ]
                    |> List.map (fun index -> $"""{{"action":"canvas-selection","request":"Explain change {index}"}}""")

                interactions
                |> List.iter (fun interaction ->
                    SessionBridge.send System.Threading.CancellationToken.None
                        { WorktreePath = WorktreePath.value path
                          Target = SessionBridge.SendTarget.DurableSession(SessionId sessionId)
                          Prompt = SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" interaction }
                    |> Async.RunSynchronously
                    |> ignore)

                releaseLaunch.TrySetResult() |> ignore
                let first = await incoming
                let ordinary = listener.GetContextAsync()
                Assert.Multiple(fun () ->
                    Assert.That(readPrompt first, Is.EqualTo(("startup-prompt", initial)))
                    Assert.That(ordinary.IsCompleted, Is.False, "Interactions must wait for startup acceptance"))
                respond 200 first

                let expected = interactions |> List.skip (messageCount - 10)
                expected
                |> List.iteri (fun index interaction ->
                    let request =
                        if index = 0 then ordinary
                        else listener.GetContextAsync()
                    let context = await request
                    Assert.That(readPrompt context, Is.EqualTo(("canvas", interaction)))
                    respond 200 context)

                await launched |> assertStarted (started path)
                Assert.That(closes, Is.Empty)
            finally
                releaseLaunch.TrySetResult() |> ignore)

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.``SDK rejection closes only the new terminal and reports cleanup failure``(cleanupFails: bool) =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "startup-rejection")
            let sessionId = Guid.NewGuid().ToString()
            let expectedStart = started path
            let closes = ConcurrentQueue<EmbeddedTerminalId>()
            let incoming = listener.GetContextAsync()
            let launched =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        register (WorktreePath.value path) (Some sessionId) url
                        async.Return(Ok expectedStart))
                    (fun id ->
                        closes.Enqueue id
                        async.Return(
                            if cleanupFails then Error "exact cleanup failed"
                            else Ok EmbeddedTerminalSnapshot.empty))
                    path
                    "Initial task"

            let context = await incoming
            readPrompt context |> ignore
            respond 503 context
            let result = await launched

            let expected =
                if cleanupFails then
                    PromptedLaunchError.StartupCleanupFailed(
                        StartupPromptFailure.Rejected,
                        expectedStart.TerminalId)
                else
                    PromptedLaunchError.StartupFailed StartupPromptFailure.Rejected

            Assert.Multiple(fun () ->
                Assert.That(closes.ToArray(), Is.EqualTo([| expectedStart.TerminalId |]))
                Assert.That(
                    result,
                    Is.EqualTo(Error expected : Result<EmbeddedTerminalStartResult, PromptedLaunchError>))))

    [<Test>]
    member _.``unexpected cleanup failure preserves rejection without exposing exception text``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "startup-cleanup-exception")
            let sessionId = Guid.NewGuid().ToString()
            let incoming = listener.GetContextAsync()
            let launched =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        register (WorktreePath.value path) (Some sessionId) url
                        async.Return(Ok(started path)))
                    (fun _ ->
                        async { return raise (InvalidOperationException "private cleanup diagnostics") })
                    path
                    "Initial task"

            let context = await incoming
            readPrompt context |> ignore
            respond 503 context
            let expected =
                PromptedLaunchError.StartupCleanupFailed(
                    StartupPromptFailure.Rejected,
                    (started path).TerminalId)

            Assert.That(
                await launched,
                Is.EqualTo(Error expected : Result<EmbeddedTerminalStartResult, PromptedLaunchError>)))

    [<Test>]
    member _.``startup timeout removes the pending prompt and closes its terminal``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "startup-timeout")
            let sessionId = Guid.NewGuid().ToString()
            let expectedStart = started path
            let closes = ConcurrentQueue<EmbeddedTerminalId>()
            let result =
                start
                    TimeSpan.Zero
                    (fun _ _ -> async.Return(Ok expectedStart))
                    (fun id ->
                        closes.Enqueue id
                        async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    "Expired initial task"
                |> await

            Assert.That(
                result,
                Is.EqualTo(
                    Error(PromptedLaunchError.StartupFailed StartupPromptFailure.TimedOut)
                    : Result<EmbeddedTerminalStartResult, PromptedLaunchError>))
            Assert.That(closes.ToArray(), Is.EqualTo([| expectedStart.TerminalId |]))

            let incoming = listener.GetContextAsync()
            let replacement =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        register (WorktreePath.value path) (Some sessionId) url
                        async.Return(Ok expectedStart))
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    path
                    "Replacement initial task"

            let context = await incoming
            Assert.That(readPrompt context, Is.EqualTo(("startup-prompt", "Replacement initial task")))
            respond 200 context
            await replacement |> assertStarted expectedStart)

    [<Test>]
    member _.``failed shell launch clears its reserved prompt without closing another terminal``() =
        let path = WorktreePath(uniquePath "startup-launch-failure")
        let closes = ConcurrentQueue<EmbeddedTerminalId>()
        let result =
            start
                (TimeSpan.FromSeconds 5.0)
                (fun _ _ -> async.Return(Error "shell launch failed"))
                (fun id ->
                    closes.Enqueue id
                    async.Return(Ok EmbeddedTerminalSnapshot.empty))
                path
                "Initial task"
            |> await

        Assert.Multiple(fun () ->
            Assert.That(
                result,
                Is.EqualTo(
                    Error PromptedLaunchError.TerminalStartFailed
                    : Result<EmbeddedTerminalStartResult, PromptedLaunchError>))
            Assert.That(closes, Is.Empty))

    [<Test>]
    member _.``unexpected launch failure becomes a generic typed error``() =
        let path = WorktreePath(uniquePath "startup-launch-exception")
        let closes = ConcurrentQueue<EmbeddedTerminalId>()
        let result =
            start
                (TimeSpan.FromSeconds 5.0)
                (fun _ _ ->
                    async { return raise (InvalidOperationException "private launch diagnostics") })
                (fun id ->
                    closes.Enqueue id
                    async.Return(Ok EmbeddedTerminalSnapshot.empty))
                path
                "Initial task"
            |> await

        Assert.Multiple(fun () ->
            Assert.That(
                result,
                Is.EqualTo(
                    Error PromptedLaunchError.Unexpected
                    : Result<EmbeddedTerminalStartResult, PromptedLaunchError>))
            Assert.That(closes, Is.Empty))

    [<Test>]
    member _.``caller cancellation cannot abandon startup failure cleanup``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "startup-cancellation")
            let sessionId = Guid.NewGuid().ToString()
            let expectedStart = started path
            let closed = TaskCompletionSource<EmbeddedTerminalId>(TaskCreationOptions.RunContinuationsAsynchronously)
            use caller = new System.Threading.CancellationTokenSource()
            let incoming = listener.GetContextAsync()
            let operation =
                TerminalLaunch.startPromptedAgentWith
                    { StartCommand =
                        fun _ _ ->
                            register (WorktreePath.value path) (Some sessionId) url
                            async.Return(Ok expectedStart)
                      CloseTerminal =
                        fun id ->
                            closed.TrySetResult id |> ignore
                            async.Return(Ok EmbeddedTerminalSnapshot.empty)
                      Timeout = TimeSpan.FromSeconds 5.0 }
                    None
                    None
                    path
                    "Initial task"
            let launched = Async.StartAsTask(operation, cancellationToken = caller.Token)
            let context = await incoming
            readPrompt context |> ignore
            caller.Cancel()

            respond 503 context
            Assert.That(await closed.Task, Is.EqualTo expectedStart.TerminalId))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("StartupPrompt")>]
[<Category("ShellTransport")>]
[<Platform("Win")>]
type StartupCommandTransportTests() =

    [<TestCase("")>]
    [<TestCase("gpt-6.1-sol")>]
    [<TestCase("provider/GPT_6.1-sol:preview+1")>]
    member _.``CMD launcher receives fresh startup flags and optional model without a session selector``(model: string) =
        withTempDir "treemon-startup argv owner's fixture" (fun root ->
            let selectedModel = if model = "" then None else Some model
            let scriptPath = Path.Combine(root, "launch.ps1")
            let shimPath = Path.Combine(root, "copilot.cmd")
            let recordingPath = Path.Combine(root, "argv.jsonl")
            let command =
                selectedModel
                |> Option.traverseResult CodingToolCli.ModelId.create
                |> Result.map (fun model -> (CodingToolCli.build None (CodingToolCli.NewSession model)).AsShellString)
                |> Result.defaultWith (PromptedLaunchError.message >> invalidOp)

            File.WriteAllText(
                shimPath,
                "@echo off\r\nset \"COPILOT_AUTO_UPDATE=false\"\r\n"
                + "\"%TM_COPILOT_RECORDER_EXECUTABLE%\" --no-auto-update %*\r\n")
            File.WriteAllText(
                scriptPath,
                "param($launcher, $recorder, $recording)\n"
                + "$ErrorActionPreference = 'Stop'\n"
                + "$env:TM_COPILOT_RECORDER_EXECUTABLE = $recorder\n"
                + "$env:TM_COPILOT_RECORDER = $recording\n"
                + "$env:TM_COPILOT_BRIDGE_STARTUP = ''\n"
                + "Set-Alias -Name copilot -Value $launcher\n"
                + command
                + "\nexit $LASTEXITCODE\n")

            let result =
                ProcessRunner.textResult
                    { ProcessRunner.Spawn.create "pwsh" with
                        Context = "StartupPromptTests"
                        WorkingDirectory = Some root
                        Limits = ProcessRunner.CaptureLimits.small
                        Deadline = ProcessRunner.Timeout 10_000 }
                    [ "-NoProfile"; "-NonInteractive"; "-File"; scriptPath
                      shimPath; copilotRecorderExecutable; recordingPath ]
                |> Async.RunSynchronously

            match result with
            | Error error -> Assert.Fail($"CMD argument fixture failed: {error}")
            | Ok _ -> ()

            use recording = JsonDocument.Parse(File.ReadAllText recordingPath)
            let arguments =
                recording.RootElement.GetProperty("args").EnumerateArray()
                |> Seq.map _.GetString()
                |> Seq.toList

            Assert.That(
                arguments,
                Is.EqualTo(
                    [ "--no-auto-update"; "--experimental"; "--yolo"
                      yield! selectedModel |> Option.map (fun value -> $"--model={value}") |> Option.toList ])))
