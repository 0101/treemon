module Tests.WorktreeApiLaunchTests

open System
open System.Collections.Concurrent
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Server.CodingToolCli
open Server.SchedulerState
open Tests.BridgeFixture
open Tests.GitTestHelpers
open Tests.TestUtils

let private terminalId value =
    EmbeddedTerminalId value

[<RequireQualifiedAccess>]
type private LaunchCall =
    | OpenNativeTerminal of WorktreePath
    | StartEmbeddedTerminal of WorktreePath
    | StartEmbeddedCommand of WorktreePath * string
    | StartPromptedAgent of CodingToolProvider option * WorktreePath * string

let private assertTerminalCommandAccepted command =
    Assert.That(
        TerminalHostClient.validateTerminalCommand command,
        Is.EqualTo(Ok command : Result<string, string>)
    )

let private startResult path id =
    let id = terminalId id

    { Snapshot =
        { Tabs =
            [ { Id = id
                Worktree = path
                ReportedActivity = None
                SessionIds = []
                Lifecycle =
                    EmbeddedTerminalLifecycle.Running
                        $"http://127.0.0.1:41001/{EmbeddedTerminalId.value id}/" } ] }
      TerminalId = id }

let private liveSession now path terminalId sessionId : SessionActivityStore.StoredInstance =
    { ProcessIdentity =
        syntheticProcessIdentityForSessionId sessionId
      SessionId = SessionActivity.SessionId sessionId
      TerminalSessionId =
        terminalId
        |> EmbeddedTerminalId.value
        |> SessionActivity.TerminalSessionId
        |> Some
      WorktreePath = path
      Provider = CodingToolProvider.CopilotCli
      Status = SessionActivity.emptyStatus
      UpdatedAt = now
      LifecycleAt = Some now
      LastSeen = now
      ContextUsageAt = None
      ClosedAt = None }

let private createApi
    root
    worktreePath
    activityStore
    terminalLaunch
    =
    let agent = SchedulerState.createAgent ()
    let repoId = PathUtils.toRepoId root

    agent.Post(
        UpdateWorktreeList(
            repoId,
            [ { GitWorktree.WorktreeInfo.Path = WorktreePath.value worktreePath
                Head = "head"
                Branch = Some "main" } ]))

    agent.PostAndAsyncReply(GetState)
    |> runAsync
    |> ignore

    WorktreeApi.worktreeApiWithLaunch
        terminalLaunch
        { Agent = agent
          CardLog = CardEventLog.createAgent ()
          // These backends are deliberately unavailable: every start in this fixture must cross
          // the injected TerminalLaunch boundary or fail loudly by dereferencing the test sentinel.
          SessionAgent = Unchecked.defaultof<SessionManager.SessionAgent>
          EmbeddedTerminal = Unchecked.defaultof<EmbeddedTerminal.Manager>
          TerminalSessionCleanup = WorktreeCleanup.noSessionClose
          ActivityStore = activityStore
          SnapshotStore = None
          AutoSyncStore = None
          TerminalHostRestartSessions = None
          WorktreeRoots = [ root ]
          DeletedWorktreeFile = Path.Combine(root, "deleted-worktrees-test.json")
          TestFixtures = None
          AppVersion = "test"
          DeployBranch = None },
    agent

let private promptedLaunchOnly startPromptedAgent : TerminalLaunch.Operations =
    { OpenNativeTerminal = fun _ -> async.Return(Error "Unexpected native launch")
      StartEmbeddedTerminal = fun _ -> async.Return(Error "Unexpected plain launch")
      StartEmbeddedCommand = fun _ _ -> async.Return(Error "Unexpected command launch")
      StartPromptedAgent = startPromptedAgent }

let private assertStart expectedId result =
    match result with
    | Ok started ->
        Assert.Multiple(fun () ->
            Assert.That(started.TerminalId, Is.EqualTo expectedId)
            Assert.That(
                started.Snapshot.Tabs |> List.map _.Id,
                Is.EqualTo([ expectedId ])
            ))
    | Error error ->
        Assert.Fail($"Expected embedded launch success but got: {error}")

let private writePostForkMarkerScript repoRoot =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        File.WriteAllText(
            Path.Combine(repoRoot, "post-fork.ps1"),
            "param($wt, $root, $baseRef, $branch)\nSet-Content -LiteralPath (Join-Path $wt 'post-fork-ready.txt') -Value 'ready'"
        )
    else
        File.WriteAllText(
            Path.Combine(repoRoot, "post-fork.sh"),
            "#!/usr/bin/env bash\nprintf 'ready' > \"$1/post-fork-ready.txt\"\n"
        )

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WorktreeApiLaunchTests() =

    [<Test>]
    member _.``Resume finds the terminal already running the exact session``() =
        let now = DateTimeOffset.UtcNow
        let path = WorktreePath "C:/wt/resume"
        let existingId =
            terminalId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        let existing = startResult path (EmbeddedTerminalId.value existingId)

        let result =
            TerminalSessionActivity.tryFindLiveTerminalId
                now
                path
                (SessionActivity.SessionId "copilot-session")
                [ liveSession
                      now
                      path
                      existingId
                      "copilot-session" ]
                existing.Snapshot

        Assert.That(result, Is.EqualTo(Some existingId))

    [<Test>]
    member _.``Resume does not reuse a terminal owned by a different session``() =
        let now = DateTimeOffset.UtcNow
        let path = WorktreePath "C:/wt/resume"
        let existingId =
            terminalId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        let existing = startResult path (EmbeddedTerminalId.value existingId)

        let result =
            TerminalSessionActivity.tryFindLiveTerminalId
                now
                path
                (SessionActivity.SessionId "target-session")
                [ liveSession
                      now
                      path
                      existingId
                      "different-session" ]
                existing.Snapshot

        Assert.That(result, Is.EqualTo None)

    [<Test>]
    member _.``Resume does not reuse an exact waiting session after its heartbeat is stale``() =
        let now = DateTimeOffset.UtcNow
        let path = WorktreePath "C:/wt/resume"
        let existingId =
            terminalId "cccccccccccccccccccccccccccccccc"
        let existing = startResult path (EmbeddedTerminalId.value existingId)
        let awaitingAt = now - TimeSpan.FromMinutes 10.0
        let staleWaiting =
            { liveSession now path existingId "copilot-session" with
                Status =
                    SessionActivity.fold
                        SessionActivity.emptyStatus
                        (SessionActivity.AwaitingUserInput(None, awaitingAt))
                UpdatedAt = awaitingAt
                LastSeen = awaitingAt }

        let result =
            TerminalSessionActivity.tryFindLiveTerminalId
                now
                path
                (SessionActivity.SessionId "copilot-session")
                [ staleWaiting ]
                existing.Snapshot

        Assert.That(result, Is.EqualTo None)

    [<Test>]
    member _.``Resume chooses the greatest-activity exact process when a durable session is duplicated``() =
        let now = DateTimeOffset.UtcNow
        let path = WorktreePath "C:/wt/resume"
        let firstId =
            terminalId "dddddddddddddddddddddddddddddddd"
        let secondId =
            terminalId "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"

        let snapshot =
            { Tabs =
                [ (startResult path (EmbeddedTerminalId.value firstId)).Snapshot.Tabs
                  (startResult path (EmbeddedTerminalId.value secondId)).Snapshot.Tabs ]
                |> List.concat }

        let firstIdentity =
            ProcessIdentity.create 6401 7401L
            |> Result.defaultWith invalidOp

        let secondIdentity =
            ProcessIdentity.create 6402 7402L
            |> Result.defaultWith invalidOp

        let first =
            { liveSession now path firstId "copilot-session" with
                ProcessIdentity = firstIdentity
                UpdatedAt = now.AddMinutes(-2.0) }

        let second =
            { liveSession now path secondId "copilot-session" with
                ProcessIdentity = secondIdentity
                UpdatedAt = now.AddMinutes(-1.0) }

        let result =
            TerminalSessionActivity.tryFindLiveTerminalId
                now
                path
                (SessionActivity.SessionId "copilot-session")
                [ first; second ]
                snapshot

        Assert.That(result, Is.EqualTo(Some secondId))

    [<Test>]
    member _.``Resume does not reuse a closed exact process even when its terminal still exists``() =
        let now = DateTimeOffset.UtcNow
        let path = WorktreePath "C:/wt/resume"
        let existingId =
            terminalId "ffffffffffffffffffffffffffffffff"
        let existing = startResult path (EmbeddedTerminalId.value existingId)
        let closed =
            { liveSession now path existingId "copilot-session" with
                ClosedAt = Some now }

        let result =
            TerminalSessionActivity.tryFindLiveTerminalId
                now
                path
                (SessionActivity.SessionId "copilot-session")
                [ closed ]
                existing.Snapshot

        Assert.That(result, Is.EqualTo None)

    [<Test>]
    member _.``Worktree API selects typed launch operations and preserves exact embedded results``() =
        withTempDir "treemon-worktree-api-launch" (fun root ->
            let path =
                root
                |> Path.GetFullPath
                |> PathUtils.normalizePath
                |> WorktreePath

            let launchPrompt = "Read the copied prompt and implement it"
            let action = FixBuild "https://example.test/build/42"
            let canvasPrompt =
                CanvasSessionPrompt.forAgentDoc
                    (WorktreePath.value path)
                    "review.html"
            let agentCommand =
                (build (Some CodingToolProvider.CopilotCli) Start).AsShellString
            let actionPrompt =
                action
                |> CodingToolStatus.actionPrompt None
            let resumeCommand =
                (build None (Resume None)).AsShellString

            let plainId = terminalId "11111111111111111111111111111111"
            let agentId = terminalId "22222222222222222222222222222222"
            let launchId = terminalId "33333333333333333333333333333333"
            let actionId = terminalId "44444444444444444444444444444444"
            let canvasId = terminalId "55555555555555555555555555555555"
            let resumeId = terminalId "66666666666666666666666666666666"
            let calls = ConcurrentQueue<LaunchCall>()

            let terminalLaunch: TerminalLaunch.Operations =
                { OpenNativeTerminal =
                    fun requestedPath ->
                        async {
                            calls.Enqueue(LaunchCall.OpenNativeTerminal requestedPath)
                            return Ok()
                        }
                  StartEmbeddedTerminal =
                    fun requestedPath ->
                        async {
                            calls.Enqueue(LaunchCall.StartEmbeddedTerminal requestedPath)

                            return
                                Ok(
                                    startResult
                                        requestedPath
                                        (EmbeddedTerminalId.value plainId)
                                )
                        }
                  StartEmbeddedCommand =
                    fun requestedPath command ->
                        async {
                            calls.Enqueue(
                                LaunchCall.StartEmbeddedCommand(requestedPath, command)
                            )

                            return
                                match command with
                                | value when value = agentCommand ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value agentId)
                                    )
                                | value when value = resumeCommand ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value resumeId)
                                    )
                                | _ -> Error "Unexpected embedded command"
                        }
                  StartPromptedAgent =
                    fun provider requestedPath prompt ->
                        async {
                            calls.Enqueue(
                                LaunchCall.StartPromptedAgent(provider, requestedPath, prompt))

                            return
                                match prompt with
                                | value when value = launchPrompt ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value launchId)
                                    )
                                | value when value = actionPrompt ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value actionId)
                                    )
                                | value when value = canvasPrompt ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value canvasId)
                                    )
                                | _ -> Error PromptedLaunchError.TerminalStartFailed
                        } }

            let api, _ = createApi root path None terminalLaunch

            api.openTerminal path |> runAsync
            let plain = api.startEmbeddedTerminal path |> runAsync
            let agent = api.startAgent path |> runAsync
            let launched =
                api.launchSession
                    { Path = path
                      Prompt = launchPrompt }
                |> runAsync
            let actionLaunched =
                api.launchAction
                    { Path = path
                      Action = action }
                |> runAsync
            let canvasLaunched =
                api.launchAction
                    { Path = path
                      Action = CanvasSession canvasPrompt }
                |> runAsync
            let resumed = api.resumeSession path |> runAsync

            assertStart plainId plain
            assertStart agentId agent
            assertStart launchId launched
            assertStart actionId actionLaunched
            assertStart canvasId canvasLaunched
            assertStart resumeId resumed
            Assert.That(
                calls.ToArray(),
                Is.EqualTo(
                    [| LaunchCall.OpenNativeTerminal path
                       LaunchCall.StartEmbeddedTerminal path
                       LaunchCall.StartEmbeddedCommand(path, agentCommand)
                       LaunchCall.StartPromptedAgent(None, path, launchPrompt)
                       LaunchCall.StartPromptedAgent(None, path, actionPrompt)
                       LaunchCall.StartPromptedAgent(None, path, canvasPrompt)
                       LaunchCall.StartEmbeddedCommand(path, resumeCommand) |]
                )
            )

            assertTerminalCommandAccepted agentCommand
            assertTerminalCommandAccepted resumeCommand)

    [<Test>]
    member _.``prompted API methods preserve structured startup cleanup failure``() =
        withTempDir "treemon-typed-launch-failure" (fun root ->
            let path = PathUtils.toWorktreePath root
            let expected =
                PromptedLaunchError.StartupCleanupFailed(
                    StartupPromptFailure.TimedOut,
                    EmbeddedTerminalId "00000000000000000000000000000001")
            let terminalLaunch =
                promptedLaunchOnly (fun _ _ _ -> async.Return(Error expected))
            let api, _ = createApi root path None terminalLaunch

            let session = api.launchSession { Path = path; Prompt = "Initial task" } |> runAsync
            let action = api.launchAction { Path = path; Action = CreatePr } |> runAsync
            let expectedResult: Result<EmbeddedTerminalStartResult, PromptedLaunchError> = Error expected

            Assert.Multiple(fun () ->
                Assert.That(session, Is.EqualTo expectedResult)
                Assert.That(action, Is.EqualTo expectedResult)))

    [<Test>]
    member _.``prompted API methods reject unknown worktrees before launching``() =
        withTempDir "treemon-unknown-launch-worktree" (fun root ->
            let known = PathUtils.toWorktreePath root
            let unknown = Path.Combine(root, "unknown") |> PathUtils.toWorktreePath
            let calls = ConcurrentQueue<WorktreePath>()
            let terminalLaunch =
                promptedLaunchOnly
                    (fun _ path _ ->
                        calls.Enqueue path
                        async.Return(Error PromptedLaunchError.Unexpected))
            let api, _ = createApi root known None terminalLaunch
            let session = api.launchSession { Path = unknown; Prompt = "Initial task" } |> runAsync
            let action = api.launchAction { Path = unknown; Action = CreatePr } |> runAsync
            let expectedResult: Result<EmbeddedTerminalStartResult, PromptedLaunchError> =
                Error(PromptedLaunchError.UnknownWorktree unknown)

            Assert.Multiple(fun () ->
                Assert.That(session, Is.EqualTo expectedResult)
                Assert.That(action, Is.EqualTo expectedResult)
                Assert.That(calls, Is.Empty)))

    [<Test>]
    member _.``Create refuses a tombstoned sibling before invoking Git``() =
        withTempDir "treemon-create-tombstone" (fun root ->
            let deletedBranch = "feature/x"
            let branch = "feature-x"
            let worktreePath =
                Shared.PathUtils.siblingWorktreePath
                    (PathUtils.normalizePath root)
                    deletedBranch
            let recordFile = Path.Combine(root, "deleted-worktrees-test.json")
            assertOk
                (DeletedWorktreeStore.recordAtPath recordFile worktreePath)
                "record deleted worktree"

            let unavailable =
                promptedLaunchOnly (fun _ _ _ -> async { return Error PromptedLaunchError.TerminalStartFailed })
            let api, _ =
                createApi
                    root
                    (PathUtils.toWorktreePath root)
                    None
                    unavailable

            let result =
                api.createWorktree
                    { RepoId = RepoId.value (PathUtils.toRepoId root)
                      BranchName = BranchName.create branch
                      BaseBranch = BranchName.create "main"
                      Prompt = None
                      Skill = None }
                |> runAsync

            match result with
            | Error message -> Assert.That(message, Does.Contain("hidden"))
            | Ok _ -> Assert.Fail("A tombstoned worktree path must not be created"))

    [<Test>]
    member _.``Create with prompt publishes discovery after post-fork without awaiting terminal completion``() =
        withTempDir "treemon-create-embedded-launch" (fun parent ->
            let repoRoot = Path.Combine(parent, "repo")
            initRepoOnMain repoRoot
            writePostForkMarkerScript repoRoot

            let rootPath =
                repoRoot
                |> Path.GetFullPath
                |> PathUtils.normalizePath
                |> WorktreePath

            let started =
                TaskCompletionSource<
                    WorktreePath * string * bool
                 >(TaskCreationOptions.RunContinuationsAsynchronously)
            let release =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let completed =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)

            let terminalLaunch =
                promptedLaunchOnly
                    (fun _ requestedPath prompt ->
                        async {
                            let markerExists =
                                File.Exists(
                                    Path.Combine(
                                        WorktreePath.value requestedPath,
                                        "post-fork-ready.txt"
                                    )
                                )

                            started.TrySetResult((requestedPath, prompt, markerExists))
                            |> ignore

                            do! release.Task |> Async.AwaitTask
                            completed.TrySetResult() |> ignore

                            return
                                Ok(
                                    startResult
                                        requestedPath
                                        "66666666666666666666666666666666"
                                )
                        })

            let api, agent = createApi repoRoot rootPath None terminalLaunch
            let prompt =
                "Implement the next ready task.\r\n"
                + "Preserve this second line exactly."
            let skill = "bd-execute"
            let branch = "routed-create"

            let createResult =
                api.createWorktree
                    { RepoId =
                        repoRoot
                        |> PathUtils.toRepoId
                        |> RepoId.value
                      BranchName = BranchName.create branch
                      BaseBranch = BranchName.create "main"
                      Prompt = Some prompt
                      Skill = Some skill }
                |> runAsync

            let launchedPath, deliveredPrompt, markerExists =
                started.Task
                    .WaitAsync(TimeSpan.FromSeconds 15.0)
                    .GetAwaiter()
                    .GetResult()

            try
                let wrapped =
                    CodingToolStatus.skillInvocation None skill prompt
                let expectedPath =
                    Path.Combine(parent, $"tm-{branch}")
                let knownPaths =
                    agent.PostAndAsyncReply GetState
                    |> runAsync
                    |> _.Repos
                    |> Map.find (PathUtils.toRepoId repoRoot)
                    |> _.KnownPaths

                Assert.Multiple(fun () ->
                    Assert.That(Result.isOk createResult, Is.True)
                    Assert.That(markerExists, Is.True,
                        "the embedded launch must wait until post-fork setup has completed")
                    Assert.That(
                        PathUtils.pathEquals
                            (WorktreePath.value launchedPath)
                            expectedPath,
                        Is.True
                    )
                    Assert.That(
                        knownPaths,
                        Does.Contain(PathUtils.normalizePath (WorktreePath.value launchedPath)),
                        "bridge registration must recognize the new worktree before its session starts")
                    Assert.That(deliveredPrompt, Is.EqualTo(wrapped))
                    Assert.That(completed.Task.IsCompleted, Is.False,
                        "createWorktree must not wait for the fire-and-forget terminal launch"))
            finally
                release.TrySetResult() |> ignore
                completed.Task
                    .WaitAsync(TimeSpan.FromSeconds 5.0)
                    .GetAwaiter()
                    .GetResult())

    [<Test>]
    member _.``Queued SystemView fallback passes the generated-view prompt without changing its queued result``() =
        withTempDir "treemon-canvas-embedded-launch" (fun root ->
            let path =
                root
                |> Path.GetFullPath
                |> PathUtils.normalizePath
                |> WorktreePath
            let calls = ConcurrentQueue<WorktreePath * string>()

            let terminalLaunch =
                promptedLaunchOnly
                    (fun _ requestedPath prompt ->
                        async {
                            calls.Enqueue((requestedPath, prompt))

                            return
                                Ok(
                                    startResult
                                        requestedPath
                                        "77777777777777777777777777777777"
                                )
                        })

            let api, _ = createApi root path None terminalLaunch
            let filename = "diff.html"
            let result =
                api.sendCanvasMessage
                    { WorktreePath = path
                      Filename = filename
                      Payload = """{"action":"canvas-selection"}""" }
                |> runAsync
            let expectedPrompt =
                CanvasPrompt.continueWorking
                    (WorktreePath.value path)
                    filename

            Assert.Multiple(fun () ->
                Assert.That(result, Is.EqualTo CanvasMessageResult.Queued)
                Assert.That(
                    calls.ToArray(),
                    Is.EqualTo([| (path, expectedPrompt) |])
                )))

    [<Test>]
    member _.``queued SystemView startup failure stays typed and releases launch suppression``() =
        withTempDir "treemon-canvas-startup-failure" (fun root ->
            let path = PathUtils.toWorktreePath root
            let calls = ConcurrentQueue<WorktreePath>()
            let expected = PromptedLaunchError.StartupFailed StartupPromptFailure.Rejected
            let terminalLaunch =
                promptedLaunchOnly
                    (fun _ path _ ->
                        calls.Enqueue path
                        async.Return(Error expected))
            let api, _ = createApi root path None terminalLaunch
            let request =
                { WorktreePath = path
                  Filename = "diff.html"
                  Payload = """{"action":"canvas-selection","request":"Explain the change"}""" }
            let first = api.sendCanvasMessage request |> runAsync
            let retry = api.sendCanvasMessage request |> runAsync

            Assert.Multiple(fun () ->
                Assert.That(first, Is.EqualTo(CanvasMessageResult.SessionStartFailed expected))
                Assert.That(retry, Is.EqualTo(CanvasMessageResult.SessionStartFailed expected))
                Assert.That(calls.ToArray(), Is.EqualTo([| path; path |]))))

    [<TestCase(false, false)>]
    [<TestCase(true, false)>]
    [<TestCase(false, true)>]
    [<TestCase(true, true)>]
    [<Category("BridgeTransport")>]
    member _.``retry after failed SystemView startup delivers its interaction once and preserves other requests``
        (startThrows: bool, callerDisconnects: bool) =
        withTempDir "treemon-canvas-startup-retry" (fun root ->
            withBridges 1 (fun bridges ->
                let listener, url = List.exactlyOne bridges
                let path = PathUtils.toWorktreePath root
                let calls = ConcurrentQueue<WorktreePath>()
                let launchEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let releaseLaunch = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let failure = PromptedLaunchError.StartupFailed StartupPromptFailure.Rejected
                let terminalLaunch =
                    promptedLaunchOnly (fun _ requestedPath _ ->
                        async {
                            calls.Enqueue requestedPath

                            if calls.Count = 1 then
                                launchEntered.TrySetResult() |> ignore
                                do! releaseLaunch.Task |> Async.AwaitTask

                                if startThrows then
                                    return raise (InvalidOperationException "Startup failed")
                                else
                                    return Error failure
                            else
                                let sessionId = Guid.NewGuid().ToString()
                                let identity = collisionResistantProcessIdentityForSessionId sessionId
                                registerExactSession 'R' identity root url (Some sessionId) None |> ignore
                                return Ok(startResult requestedPath "77777777777777777777777777777777")
                        })
                let api, _ = createApi root path None terminalLaunch
                let request =
                    { WorktreePath = path
                      Filename = "diff.html"
                      Payload = """{"action":"canvas-selection","request":"Explain the change"}""" }
                let queue payload =
                    SessionBridge.send CancellationToken.None
                        { WorktreePath = root
                          Target = SessionBridge.SendTarget.Unspecified
                          Prompt = SessionBridge.Prompt.canvasFor request.Filename payload }
                    |> runAsync
                    |> ignore
                let before = """{"action":"canvas-selection","request":"Keep the earlier request"}"""
                let after = """{"action":"canvas-selection","request":"Keep the later request"}"""

                queue before
                use caller = new CancellationTokenSource()
                let failed =
                    Async.StartAsTask(api.sendCanvasMessage request, cancellationToken = caller.Token)

                try
                    launchEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                    if callerDisconnects then caller.Cancel()
                    releaseLaunch.TrySetResult() |> ignore

                    let expectedFailure =
                        if startThrows then PromptedLaunchError.Unexpected else failure
                    Assert.That(
                        failed.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
                        Is.EqualTo(CanvasMessageResult.SessionStartFailed expectedFailure))

                    queue after
                    let retry = api.sendCanvasMessage request |> Async.StartAsTask
                    let delivered =
                        [ 1 .. 3 ]
                        |> List.map (fun _ ->
                            let context =
                                listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 5.0)
                                    .GetAwaiter().GetResult()
                            let _, prompt = readPrompt context
                            context.Response.StatusCode <- 200
                            context.Response.Close()
                            prompt)

                    Assert.Multiple(fun () ->
                        Assert.That(
                            retry.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
                            Is.EqualTo CanvasMessageResult.Queued)
                        Assert.That(delivered |> List.toArray, Is.EqualTo([| before; after; request.Payload |]))
                        Assert.That(calls.ToArray(), Is.EqualTo([| path; path |])))
                finally
                    releaseLaunch.TrySetResult() |> ignore))
