module Tests.StartupPromptTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Text.Json
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Server.SessionActivity
open Tests.BridgeFixture
open Tests.TestUtils

let private await (task: Task<'a>) =
    task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

let private register path sessionId url =
    let identity =
        Guid.NewGuid().ToString()
        |> collisionResistantProcessIdentityForSessionId

    registerExactSession 'S' identity path url sessionId None |> ignore

let private readPrompt (context: HttpListenerContext) =
    use reader = new StreamReader(context.Request.InputStream)
    use document = JsonDocument.Parse(reader.ReadToEnd())
    document.RootElement.GetProperty("kind").GetString(),
    document.RootElement.GetProperty("prompt").GetString()

let private respond status (context: HttpListenerContext) =
    context.Response.StatusCode <- status
    context.Response.Close()

let private started path =
    let id = EmbeddedTerminalId "6e4d33254b564bde97b723a40721351c"
    { TerminalId = id
      Snapshot =
        { Tabs =
            [ { Id = id
                Worktree = path
                ReportedActivity = None
                SessionIds = []
                Lifecycle = EmbeddedTerminalLifecycle.Running "http://127.0.0.1:41001/" } ] } }

let private start timeout launch close sessionId path prompt =
    TerminalLaunch.startPromptedAgentWith
        { StartCommand = launch
          CloseTerminal = close
          Timeout = timeout }
        (SessionId sessionId)
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

    [<TestCase("AgentDoc")>]
    [<TestCase("SystemView")>]
    [<TestCase("worktree")>]
    member _.``full initial prompt crosses the bridge and launch waits for acceptance``(kind: string) =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath("Q:\\owner's repo with spaces & %PATH%\\" + Guid.NewGuid().ToString("N"))
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
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun requestedPath command ->
                        async {
                            commands.Enqueue(requestedPath, command)
                            register (WorktreePath.value path) (Some sessionId) url
                            return Ok expectedStart
                        })
                    (fun id ->
                        closes.Enqueue id
                        async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    sessionId
                    path
                    prompt

            let context = await incoming
            let wireKind, deliveredPrompt = readPrompt context

            Assert.Multiple(fun () ->
                Assert.That(wireKind, Is.EqualTo "startup-prompt")
                Assert.That(deliveredPrompt, Is.EqualTo prompt)
                Assert.That(launched.IsCompleted, Is.False, "HTTP queueing is not SDK acceptance")
                Assert.That(
                    commands.ToArray(),
                    Is.EqualTo(
                        [| path, $"copilot --experimental --yolo --session-id='{sessionId}'" |]))
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
    member _.``concurrent starts deliver only to their own durable session``() =
        withBridges 2 (fun bridges ->
            let firstListener, firstUrl = bridges[0]
            let secondListener, secondUrl = bridges[1]
            let path = WorktreePath(uniquePath "concurrent-startup")
            let firstId = Guid.NewGuid().ToString()
            let secondId = Guid.NewGuid().ToString()
            let expectedStart = started path
            use ready = new System.Threading.CountdownEvent(2)
            let launch id =
                start
                    (TimeSpan.FromSeconds 5.0)
                    (fun _ _ ->
                        ready.Signal() |> ignore
                        async.Return(Ok expectedStart))
                    (fun _ -> async.Return(Ok EmbeddedTerminalSnapshot.empty))
                    id
                    path
                    $"Initial task for {id}"

            let first = launch firstId
            let second = launch secondId
            Assert.That(
                ready.Wait(TimeSpan.FromSeconds 5.0),
                Is.True)

            let secondRequest = secondListener.GetContextAsync()
            register (WorktreePath.value path) None secondUrl
            register (WorktreePath.value path) (Some "unrelated-session") firstUrl
            register (WorktreePath.value path) (Some secondId) secondUrl
            let secondContext = await secondRequest
            Assert.That(readPrompt secondContext, Is.EqualTo(("startup-prompt", $"Initial task for {secondId}")))
            respond 200 secondContext
            await second |> assertStarted expectedStart
            Assert.That(first.IsCompleted, Is.False)

            let firstRequest = firstListener.GetContextAsync()
            register (WorktreePath.value path) (Some firstId) firstUrl
            let firstContext = await firstRequest
            Assert.That(readPrompt firstContext, Is.EqualTo(("startup-prompt", $"Initial task for {firstId}")))
            respond 200 firstContext
            await first |> assertStarted expectedStart)

    [<Test>]
    member _.``startup instruction precedes the queued SystemView interaction``() =
        withBridges 1 (fun bridges ->
            let listener, url = List.exactlyOne bridges
            let path = WorktreePath(uniquePath "startup-before-canvas")
            let sessionId = Guid.NewGuid().ToString()
            let initial = CanvasPrompt.continueWorking (WorktreePath.value path) "diff.html"
            let interaction = """{"action":"canvas-selection","request":"Explain the change"}"""

            SessionBridge.send
                { WorktreePath = WorktreePath.value path
                  Target = SessionBridge.SendTarget.Unspecified
                  Prompt = SessionBridge.Prompt.canvasFor "diff.html" interaction }
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
                    sessionId
                    path
                    initial

            let first = await incoming
            Assert.That(readPrompt first, Is.EqualTo(("startup-prompt", initial)))
            respond 200 first
            let second = await (listener.GetContextAsync())
            Assert.That(readPrompt second, Is.EqualTo(("canvas", interaction)))
            respond 200 second
            await launched |> assertStarted (started path))

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
                    sessionId
                    path
                    initial

            try
                await launchEntered.Task
                let interactions =
                    [ 1 .. messageCount ]
                    |> List.map (fun index -> $"""{{"action":"canvas-selection","request":"Explain change {index}"}}""")

                interactions
                |> List.iter (fun interaction ->
                    SessionBridge.send
                        { WorktreePath = WorktreePath.value path
                          Target = SessionBridge.SendTarget.Unspecified
                          Prompt = SessionBridge.Prompt.canvasFor "diff.html" interaction }
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
                    sessionId
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
                    sessionId
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
                    sessionId
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
                    sessionId
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
                (Guid.NewGuid().ToString())
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
                (Guid.NewGuid().ToString())
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
                    (SessionId sessionId)
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

    [<Test>]
    member _.``CMD launcher receives only the exact fresh session selector and permission flags``() =
        withTempDir "treemon-startup argv owner's fixture" (fun root ->
            let scriptPath = Path.Combine(root, "launch.ps1")
            let shimPath = Path.Combine(root, "copilot.cmd")
            let recordingPath = Path.Combine(root, "argv.jsonl")
            let sessionId = Guid.NewGuid().ToString()
            let command =
                CodingToolCli.build None (CodingToolCli.NewSession sessionId)
                |> _.AsShellString

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
                    [ "--no-auto-update"; "--experimental"; "--yolo"; $"--session-id={sessionId}" ])))
