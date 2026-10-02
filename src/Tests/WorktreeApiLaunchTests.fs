module Tests.WorktreeApiLaunchTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Server.CodingToolCli
open Server.SchedulerState
open Server.SessionActivity
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

let private createApiWithStateUsing
    registrationDelay
    beginInteractionLaunch
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

    let api =
        WorktreeApi.worktreeApiWithLaunchUsing
            registrationDelay
            beginInteractionLaunch
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
              DeployBranch = None }
    api, agent

let private createApiWithState root worktreePath activityStore terminalLaunch =
    createApiWithStateUsing Async.Sleep CanvasBridge.beginPendingLaunch root worktreePath activityStore terminalLaunch

let private createApi root worktreePath activityStore terminalLaunch =
    createApiWithState root worktreePath activityStore terminalLaunch |> fst

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

            let api, _ = createApiWithState root path None terminalLaunch

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
            let api, _ = createApiWithState root path None terminalLaunch

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
            let api, _ = createApiWithState root known None terminalLaunch
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
                createApiWithState
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

            let api, agent = createApiWithState repoRoot rootPath None terminalLaunch
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

            let api, _ = createApiWithState root path None terminalLaunch
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
            let api, _ = createApiWithState root path None terminalLaunch
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
                let recoveredSessionId = Guid.NewGuid().ToString()
                let recoveredIdentity = collisionResistantProcessIdentityForSessionId recoveredSessionId
                let recoveredTerminal = EmbeddedTerminalId "77777777777777777777777777777777"
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
                                return Ok(startResult requestedPath (EmbeddedTerminalId.value recoveredTerminal))
                        })
                let api, agent = createApiWithState root path None terminalLaunch
                let request =
                    { WorktreePath = path
                      Filename = "diff.html"
                      Payload = """{"action":"canvas-selection","request":"Explain the change"}""" }
                let queue payload =
                    SessionBridge.send CancellationToken.None
                        { WorktreePath = root
                          Target = SessionBridge.SendTarget.Unspecified
                          Prompt = SessionBridge.Prompt.canvasFor root request.Filename payload }
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
                    Assert.That(
                        retry.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
                        Is.EqualTo CanvasMessageResult.Queued)
                    let now = DateTimeOffset.UtcNow
                    agent.Post(UpdateSessionInstance(liveSession now path recoveredTerminal recoveredSessionId, now))
                    agent.PostAndAsyncReply(GetState) |> runAsync |> ignore
                    registerExactSession
                        'R'
                        recoveredIdentity
                        root
                        url
                        (Some recoveredSessionId)
                        (Some(EmbeddedTerminalId.value recoveredTerminal))
                    |> ignore
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
                        Assert.That(delivered |> List.toArray, Is.EqualTo([| before; after; request.Payload |]))
                        Assert.That(calls.ToArray(), Is.EqualTo([| path; path |])))
                finally
                    releaseLaunch.TrySetResult() |> ignore))

[<TestFixture>]
[<Category("Integration")>]
[<NonParallelizable>]
type WorktreeApiCanvasRoutingIntegrationTests() =

    [<Test>]
    [<Category("CanvasRoutingFollowers")>]
    member _.``SystemView fallback delivers in order only to the returned terminal's activity session``() =
        withTempDir "treemon-canvas-exact-fallback" (fun root ->
            let path = PathUtils.toWorktreePath root
            let terminalId = EmbeddedTerminalId(Guid.NewGuid().ToString "N")
            let launches = ConcurrentQueue<WorktreePath>()
            let terminalLaunch =
                promptedLaunchOnly
                    (fun _ requestedPath _ ->
                        async {
                            launches.Enqueue requestedPath
                            return Ok(startResult requestedPath (EmbeddedTerminalId.value terminalId))
                        })
            let api, agent = createApiWithState root path None terminalLaunch
            let message text =
                { WorktreePath = path
                  Filename = "diff.html"
                  Payload = text }
            [ "first"; "second" ]
            |> List.iter (fun text ->
                Assert.That(runAsync (api.sendCanvasMessage (message text)), Is.EqualTo CanvasMessageResult.Queued))
            Assert.That(launches.ToArray(), Is.EqualTo [| path |])
            use listener = new HttpListener()
            let url = $"http://127.0.0.1:{getFreeTcpPort ()}/"
            listener.Prefixes.Add url
            listener.Start()
            let unrelated = $"unrelated-{Guid.NewGuid():N}"
            let owner = $"fallback-{Guid.NewGuid():N}"
            let unrelatedTerminal = EmbeddedTerminalId(Guid.NewGuid().ToString "N")
            let now = DateTimeOffset.UtcNow
            let otherActivity = liveSession now path unrelatedTerminal unrelated
            agent.Post(UpdateSessionInstance(otherActivity, now))
            registerExactSession 'A' otherActivity.ProcessIdentity (WorktreePath.value path) url (Some unrelated) None |> ignore
            runAsync (SessionBridge.flushPending (WorktreePath.value path))
            let original = runAsync (SessionBridge.pendingPrompts (WorktreePath.value path))
            Assert.That(original |> List.map _.Prompt.Text, Is.EqualTo [ "first"; "second" ])
            let actualActivity = liveSession now path terminalId owner
            agent.Post(UpdateSessionInstance(actualActivity, now))
            let received = listener.GetContextAsync()
            registerExactSession 'A' actualActivity.ProcessIdentity (WorktreePath.value path) url (Some owner) None |> ignore
            let bodies =
                [ for index in 0..1 do
                    let context =
                        (if index = 0 then received else listener.GetContextAsync())
                            .WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                    use reader = new StreamReader(context.Request.InputStream)
                    let body = reader.ReadToEnd()
                    context.Response.StatusCode <- 200
                    context.Response.Close()
                    yield body ]
            runAsync (SessionBridge.flushPending (WorktreePath.value path))
            Assert.That(
                bodies,
                Is.EqualTo([ "first"; "second" ] |> List.map (fun text ->
                    SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" text
                    |> SessionBridge.serializePrompt)))
            Assert.That(runAsync (CanvasDocOwnership.getOwner (WorktreePath.value path) "diff.html"), Is.EqualTo(None: SessionId option)))

    [<Test>]
    member _.``Failed SystemView launch releases its guard and the identical interaction can retry``() =
        withTempDir "treemon-canvas-launch-retry" (fun root ->
            let path = PathUtils.toWorktreePath root
            let terminalId = EmbeddedTerminalId(Guid.NewGuid().ToString "N")
            // Mutable at the injected launch boundary to fail the first spawn only.
            let mutable attempts = 0
            let terminalLaunch =
                promptedLaunchOnly
                    (fun _ requestedPath _ ->
                        async {
                            attempts <- attempts + 1
                            if attempts = 1 then return Error PromptedLaunchError.TerminalStartFailed
                            else return Ok(startResult requestedPath (EmbeddedTerminalId.value terminalId))
                        })
            let api = createApi root path None terminalLaunch
            let request =
                { WorktreePath = path
                  Filename = "diff.html"
                  Payload = """{"action":"comment","text":"retry"}""" }
            Assert.That(
                runAsync (api.sendCanvasMessage request),
                Is.EqualTo(CanvasMessageResult.SessionStartFailed PromptedLaunchError.TerminalStartFailed))
            Assert.That(runAsync (api.sendCanvasMessage request), Is.EqualTo CanvasMessageResult.Queued)
            Assert.That(attempts, Is.EqualTo 2)
            let pending = runAsync (SessionBridge.pendingPrompts (WorktreePath.value path))
            Assert.That(pending |> List.map _.Prompt.Text, Is.EqualTo [ request.Payload ])
            Assert.That(pending |> List.forall (fun queued ->
                match queued.Target with
                | SessionBridge.QueuedTarget.ExactTerminal(actual, _) -> TerminalSessionId.value actual = EmbeddedTerminalId.value terminalId
                | _ -> false), Is.True))

    [<Test>]
    [<Category("CanvasRoutingRaces")>]
    member _.``SystemView APIs and heartbeats do not accumulate lane waiters during held HTTP``() =
        withTempDir "treemon-canvas-coalesced-api" (fun root ->
            let path = PathUtils.toWorktreePath root
            let owner = $"owner-{Guid.NewGuid():N}"
            let terminal = EmbeddedTerminalId(Guid.NewGuid().ToString "N")
            let now = DateTimeOffset.UtcNow
            let activity = liveSession now path terminal owner
            let graceEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let graceRelease = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let delays = ConcurrentQueue<int>()
            let delay milliseconds =
                async {
                    delays.Enqueue milliseconds
                    graceEntered.TrySetResult() |> ignore
                    do! graceRelease.Task |> Async.AwaitTask
                }
            let operations =
                promptedLaunchOnly (fun _ _ _ -> failwith "A reachable busy bridge must not launch")
            let api, agent = createApiWithStateUsing delay CanvasBridge.beginPendingLaunch root path None operations
            agent.Post(UpdateSessionInstance(activity, now))
            use listener = new HttpListener()
            let url = $"http://127.0.0.1:{getFreeTcpPort ()}/"
            listener.Prefixes.Add url
            listener.Start()
            let register () =
                registerExactSession 'A' activity.ProcessIdentity (WorktreePath.value path) url (Some owner) None |> ignore
            register ()
            runAsync (SessionBridge.flushPending (WorktreePath.value path))
            let message text =
                { WorktreePath = path; Filename = "diff.html"; Payload = text }
            let held = listener.GetContextAsync()
            let first = api.sendCanvasMessage (message "held") |> Async.StartAsTask
            let context = held.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            let coordinating = api.sendCanvasMessage (message "queued-1") |> Async.StartAsTask
            graceEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            let results =
                [| for index in 2..40 do
                    register ()
                    let follower = api.sendCanvasMessage (message $"queued-{index}") |> Async.StartAsTask
                    yield follower.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult() |]
            Assert.Multiple(fun () ->
                Assert.That(results, Is.All.EqualTo CanvasMessageResult.Queued)
                Assert.That(first.IsCompleted, Is.False)
                Assert.That(coordinating.IsCompleted, Is.False)
                Assert.That(delays.ToArray(), Is.EqualTo [| 3000 |])
                Assert.That(
                    SessionBridge.deliveryStatus (WorktreePath.value path),
                    Is.EqualTo
                        ({ QueuedMessages = 10; ActiveDrains = 1; PendingNotifications = 1 }
                         : SessionBridge.DeliveryStatus)))
            graceRelease.TrySetResult() |> ignore
            Assert.That(
                coordinating.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
                Is.EqualTo CanvasMessageResult.Queued,
                "The single coordinator must not wait behind HTTP either")
            context.Response.StatusCode <- 200
            context.Response.Close()
            let bodies =
                [ for _ in 1..10 do
                    let queued = listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                    use reader = new StreamReader(queued.Request.InputStream)
                    let body = reader.ReadToEnd()
                    queued.Response.StatusCode <- 200
                    queued.Response.Close()
                    yield body ]
            Assert.That(first.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(), Is.EqualTo CanvasMessageResult.Ok)
            runAsync (SessionBridge.flushPending (WorktreePath.value path))
            Assert.That(
                bodies,
                Is.EqualTo([ 31..40 ] |> List.map (fun index ->
                    SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" $"queued-{index}"
                    |> SessionBridge.serializePrompt))))

    [<Test>]
    [<Category("CanvasRoutingRaces")>]
    member _.``Coalesced fallback still reports the original launch failure``() =
        withTempDir "treemon-canvas-coalesced-failure" (fun root ->
            let path = PathUtils.toWorktreePath root
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let launches = ConcurrentQueue<WorktreePath>()
            let operations =
                promptedLaunchOnly
                    (fun _ requestedPath _ ->
                      async {
                          launches.Enqueue requestedPath
                          entered.TrySetResult() |> ignore
                          do! release.Task |> Async.AwaitTask
                          return Error PromptedLaunchError.TerminalStartFailed
                      })
            let api = createApi root path None operations
            let request = { WorktreePath = path; Filename = "diff.html"; Payload = "queued" }
            let first = api.sendCanvasMessage request |> Async.StartAsTask
            entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            let followers =
                [| for _ in 1..25 -> api.sendCanvasMessage request |> Async.StartAsTask |]
            let results = Task.WhenAll(followers).WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            Assert.That(results, Is.All.EqualTo CanvasMessageResult.Queued)
            Assert.That(launches.Count, Is.EqualTo 1)
            release.TrySetResult() |> ignore
            Assert.That(
                first.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
                Is.EqualTo(CanvasMessageResult.SessionStartFailed PromptedLaunchError.TerminalStartFailed))
            Assert.That(runAsync (SessionBridge.pendingPrompts (WorktreePath.value path)) |> List.length, Is.EqualTo 10))

    [<TestCase(1, 0, false)>]
    [<TestCase(15, 0, false)>]
    [<TestCase(1, 29, false)>]
    [<TestCase(1, 30, false)>]
    [<TestCase(1, 31, false)>]
    [<TestCase(1, 150, false)>]
    [<TestCase(15, 30, false)>]
    [<TestCase(15, 31, false)>]
    [<TestCase(15, 150, false)>]
    [<TestCase(1, 150, true)>]
    [<Category("CanvasRoutingFollowers")>]
    [<Category("CanvasRoutingSlowLaunch")>]
    member _.``Held successful launch admits prompt followers and delivers retained work without repair``(followers: int, elapsedSeconds: int, expiredPrior: bool) =
        withTempDir "treemon-canvas-held-success-followers" (fun root ->
            let path = PathUtils.toWorktreePath root
            let startedAt = DateTime.UtcNow.AddSeconds(-float elapsedSeconds)
            // Fixture time advances only at controlled launch/follower transitions, never by sleeping.
            let mutable now = startedAt
            SessionBridge.initializeDeliveryClock (WorktreePath.value path) (fun () -> now)
            if expiredPrior then
                now <- startedAt.AddSeconds(-150.0)
                runAsync (SessionBridge.send CancellationToken.None
                    { WorktreePath = WorktreePath.value path
                      Target = SessionBridge.SendTarget.Unspecified
                      Prompt = SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" "expired-prior" }) |> ignore
                now <- startedAt
            let exactTerminal = EmbeddedTerminalId(Guid.NewGuid().ToString "N")
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let launches = ConcurrentQueue<WorktreePath>()
            let operations =
                promptedLaunchOnly
                    (fun _ requestedPath _ ->
                      async {
                          launches.Enqueue requestedPath
                          entered.TrySetResult() |> ignore
                          do! release.Task |> Async.AwaitTask
                          return Ok(startResult requestedPath (EmbeddedTerminalId.value exactTerminal))
                      })
            let beginLaunch worktree = CanvasBridge.beginPendingLaunchAt now worktree
            let api, agent = createApiWithStateUsing Async.Sleep beginLaunch root path None operations
            let message index = { WorktreePath = path; Filename = "diff.html"; Payload = $"message-{index}" }
            let first = api.sendCanvasMessage (message 0) |> Async.StartAsTask
            entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            now <- startedAt.AddSeconds(float elapsedSeconds)
            for index in 1..followers do
                let follower = api.sendCanvasMessage (message index) |> Async.StartAsTask
                Assert.That(
                    follower.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
                    Is.EqualTo CanvasMessageResult.Queued)
            Assert.That(first.IsCompleted, Is.False)
            Assert.That(launches.Count, Is.EqualTo 1)
            use unrelated = new HttpListener()
            use expected = new HttpListener()
            let ports = getFreeTcpPorts 2
            let unrelatedUrl, expectedUrl = $"http://127.0.0.1:{ports[0]}/", $"http://127.0.0.1:{ports[1]}/"
            unrelated.Prefixes.Add unrelatedUrl
            expected.Prefixes.Add expectedUrl
            unrelated.Start()
            expected.Start()
            let nonowner = $"unrelated-{Guid.NewGuid():N}"
            registerExactSession 'A' (syntheticProcessIdentityForSessionId nonowner) (WorktreePath.value path) unrelatedUrl (Some nonowner) None |> ignore
            let stolen = unrelated.GetContextAsync()
            runAsync (SessionBridge.flushPending (WorktreePath.value path))
            Assert.That(stolen.IsCompleted, Is.False, "An unrelated same-worktree registration cannot admit the follower")
            now <- startedAt.AddSeconds(float (max elapsedSeconds 40))
            release.TrySetResult() |> ignore
            Assert.That(first.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(), Is.EqualTo CanvasMessageResult.Queued)
            let now = DateTimeOffset.UtcNow
            let owner = $"launched-{Guid.NewGuid():N}"
            let current = liveSession now path exactTerminal owner
            agent.Post(UpdateSessionInstance(current, now))
            registerExactSession 'A' current.ProcessIdentity (WorktreePath.value path) expectedUrl (Some owner) None |> ignore
            let firstRetained = max 0 (followers + 1 - 10)
            let bodies =
                [ for _ in firstRetained..followers do
                    let context = expected.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                    use reader = new StreamReader(context.Request.InputStream)
                    let body = reader.ReadToEnd()
                    context.Response.StatusCode <- 200
                    context.Response.Close()
                    yield body ]
            runAsync (SessionBridge.flushPending (WorktreePath.value path))
            Assert.That(
                bodies,
                Is.EqualTo([ firstRetained..followers ] |> List.map (fun index ->
                    SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" (message index).Payload
                    |> SessionBridge.serializePrompt)),
                "Retained follower order: " + String.concat Environment.NewLine bodies)
            Assert.That(stolen.IsCompleted, Is.False)
            Assert.That(runAsync (SessionBridge.pendingPrompts (WorktreePath.value path)), Is.Empty))

    [<TestCase("completed", 0)>]
    [<TestCase("cancelled", 0)>]
    [<TestCase("completed", 31)>]
    [<TestCase("completed", 150)>]
    [<TestCase("cancelled", 31)>]
    [<TestCase("cancelled", 150)>]
    [<TestCase("replaced", 31)>]
    [<TestCase("replaced", 150)>]
    [<TestCase("replaced-failed", 31)>]
    [<TestCase("replaced-failed", 150)>]
    [<TestCase("superseded", 31)>]
    [<TestCase("superseded", 150)>]
    [<Category("CanvasRoutingFollowers")>]
    [<Category("CanvasRoutingSlowLaunch")>]
    member _.``Follower admission observes completion or cancellation before coordination finishes``(change: string, elapsedSeconds: int) =
        withTempDir "treemon-canvas-follower-launch-transition" (fun root ->
            let path = PathUtils.toWorktreePath root
            let launchAt = DateTime.UtcNow.AddSeconds(-float elapsedSeconds)
            // Backdated startup and queue time exercise long replies without real-time launch waits.
            let mutable now = launchAt
            SessionBridge.initializeDeliveryClock (WorktreePath.value path) (fun () -> now)
            let exactTerminal = EmbeddedTerminalId(Guid.NewGuid().ToString "N")
            let replacement = change.StartsWith("replaced", StringComparison.Ordinal)
            let superseded = change = "superseded"
            let failed = change = "cancelled" || change = "replaced-failed"
            let recipientTerminal =
                if replacement then EmbeddedTerminalId(Guid.NewGuid().ToString "N") else exactTerminal
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let launches = ConcurrentQueue<WorktreePath>()
            let operations =
                promptedLaunchOnly
                    (fun _ requestedPath _ ->
                      async {
                          launches.Enqueue requestedPath
                          if launches.Count = 1 then
                              entered.TrySetResult() |> ignore
                              do! release.Task |> Async.AwaitTask
                              if failed then return Error PromptedLaunchError.TerminalStartFailed
                              else return Ok(startResult requestedPath (EmbeddedTerminalId.value exactTerminal))
                          else return Ok(startResult requestedPath (EmbeddedTerminalId.value exactTerminal))
                      })
            let beginLaunch worktree = CanvasBridge.beginPendingLaunchAt now worktree
            let api, agent = createApiWithStateUsing Async.Sleep beginLaunch root path None operations
            let message text = { WorktreePath = path; Filename = "diff.html"; Payload = text }
            let first = api.sendCanvasMessage (message "first") |> Async.StartAsTask
            entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            now <- launchAt.AddSeconds(float elapsedSeconds)
            let terminal = TerminalSessionId(EmbeddedTerminalId.value exactTerminal)
            let resolveSession expectedTerminal () =
                async {
                    let! current = agent.PostAndAsyncReply(GetState)
                    return
                        TerminalSessionActivity.tryFindCurrentSessionForTerminal
                            DateTimeOffset.UtcNow
                            path
                            expectedTerminal
                            (current.SessionInstances |> Map.values)
                }
            if change = "completed" then
                runAsync (CanvasBridge.completePendingLaunch (WorktreePath.value path) launchAt terminal (resolveSession terminal))
            elif not superseded then runAsync (CanvasBridge.cancelPendingLaunchAt (WorktreePath.value path) launchAt)
            let replacementStart =
                if replacement || superseded then
                    match runAsync (CanvasBridge.beginPendingLaunchAt now (WorktreePath.value path)) with
                    | CanvasBridge.PendingLaunchStarted started ->
                        Assert.That(runAsync (CanvasBridge.reservePendingLaunch (WorktreePath.value path) started), Is.True)
                        Some started
                    | _ -> failwith "Suppression expiry or cancellation must permit a distinct replacement launch"
                else None
            Assert.That(runAsync (api.sendCanvasMessage (message "follower")), Is.EqualTo CanvasMessageResult.Queued)
            let joined = runAsync (SessionBridge.pendingPrompts (WorktreePath.value path))
            Assert.That(joined |> List.map _.Prompt.Text, Is.EqualTo [ "first"; "follower" ])
            Assert.That(joined |> List.forall (fun queued ->
                match queued.Target with
                | SessionBridge.QueuedTarget.ExactTerminal(actual, _) -> change = "completed" && actual = terminal
                | SessionBridge.QueuedTarget.Session SessionBridge.SendTarget.Unspecified -> change = "cancelled"
                | SessionBridge.QueuedTarget.LaunchingTerminal started ->
                    if superseded then started = launchAt else replacementStart = Some started
                | _ -> false), Is.True, "Admission must bind completion or leave cancellation retryable")
            Assert.That(first.IsCompleted, Is.False)
            release.TrySetResult() |> ignore
            let expectedResult =
                if failed then CanvasMessageResult.SessionStartFailed PromptedLaunchError.TerminalStartFailed
                else CanvasMessageResult.Queued
            Assert.That(first.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(), Is.EqualTo expectedResult)
            if superseded then
                Assert.That(
                    runAsync (CanvasBridge.beginPendingLaunchAt (now.AddSeconds 1.0) (WorktreePath.value path)),
                    Is.EqualTo(CanvasBridge.PendingLaunchJoined(replacementStart.Value, None)),
                    "The original coordinator's completion must not replace the newer spawn guard")
            let payloads =
                if change = "cancelled" then
                    Assert.That(launches.Count, Is.EqualTo 2, "A new unadmitted follower must not be discarded as already handled None work")
                    Assert.That(runAsync (api.sendCanvasMessage (message "retry")), Is.EqualTo CanvasMessageResult.Queued)
                    Assert.That(launches.Count, Is.EqualTo 2, "Coordination finish must release the cancelled launch")
                    [ "follower"; "retry" ]
                elif failed then [ "follower" ]
                else [ "first"; "follower" ]
            use oldEndpoint = new HttpListener()
            let oldUrl = $"http://127.0.0.1:{getFreeTcpPort ()}/"
            oldEndpoint.Prefixes.Add oldUrl
            oldEndpoint.Start()
            let oldRequest = oldEndpoint.GetContextAsync()
            if replacement then
                let retained = runAsync (SessionBridge.pendingPrompts (WorktreePath.value path))
                Assert.That(retained |> List.forall (fun queued ->
                    match queued.Target with
                    | SessionBridge.QueuedTarget.LaunchingTerminal started -> replacementStart = Some started
                    | _ -> false), Is.True, "The stale original success/failure cannot bind or release the replacement group")
                let oldNow = DateTimeOffset.UtcNow
                let oldOwner = $"stale-{Guid.NewGuid():N}"
                let oldActivity = liveSession oldNow path exactTerminal oldOwner
                agent.Post(UpdateSessionInstance(oldActivity, oldNow))
                registerExactSession 'A' oldActivity.ProcessIdentity (WorktreePath.value path) oldUrl (Some oldOwner) None |> ignore
                runAsync (SessionBridge.flushPending (WorktreePath.value path))
                Assert.That(oldRequest.IsCompleted, Is.False)
                let expectedTerminal = TerminalSessionId(EmbeddedTerminalId.value recipientTerminal)
                runAsync (CanvasBridge.completePendingLaunch (WorktreePath.value path) replacementStart.Value expectedTerminal (resolveSession expectedTerminal))
            use listener = new HttpListener()
            let url = $"http://127.0.0.1:{getFreeTcpPort ()}/"
            listener.Prefixes.Add url
            listener.Start()
            let owner = $"launched-{Guid.NewGuid():N}"
            let now = DateTimeOffset.UtcNow
            let current = liveSession now path recipientTerminal owner
            agent.Post(UpdateSessionInstance(current, now))
            registerExactSession 'A' current.ProcessIdentity (WorktreePath.value path) url (Some owner) None |> ignore
            let bodies =
                [ for _ in payloads do
                    let context = listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                    use reader = new StreamReader(context.Request.InputStream)
                    let body = reader.ReadToEnd()
                    context.Response.StatusCode <- 200
                    context.Response.Close()
                    yield body ]
            runAsync (SessionBridge.flushPending (WorktreePath.value path))
            Assert.That(bodies, Is.EqualTo(payloads |> List.map (fun payload ->
                SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" payload
                |> SessionBridge.serializePrompt)))
            if replacement then Assert.That(oldRequest.IsCompleted, Is.False)
            if superseded then runAsync (CanvasBridge.cancelPendingLaunchAt (WorktreePath.value path) replacementStart.Value)
            Assert.That(runAsync (SessionBridge.pendingPrompts (WorktreePath.value path)), Is.Empty))

    [<TestCase(30)>]
    [<TestCase(31)>]
    [<TestCase(150)>]
    [<Category("CanvasRoutingSlowLaunch")>]
    member _.``Completed coordination does not retain expired launch attachment for a new interaction``(elapsedSeconds: int) =
        withTempDir "treemon-canvas-completed-cooldown" (fun root ->
            let path = PathUtils.toWorktreePath root
            let startedAt = DateTime.UtcNow.AddSeconds(-float elapsedSeconds)
            // Each completed interaction chooses a launch using this explicit fixture clock.
            let mutable now = startedAt
            SessionBridge.initializeDeliveryClock (WorktreePath.value path) (fun () -> now)
            let firstTerminal = EmbeddedTerminalId(Guid.NewGuid().ToString "N")
            let nextTerminal = EmbeddedTerminalId(Guid.NewGuid().ToString "N")
            let launches = ConcurrentQueue<WorktreePath>()
            let operations =
                promptedLaunchOnly
                    (fun _ requestedPath _ ->
                      async {
                          launches.Enqueue requestedPath
                          let terminal = if launches.Count = 1 then firstTerminal else nextTerminal
                          return Ok(startResult requestedPath (EmbeddedTerminalId.value terminal))
                      })
            let beginLaunch worktree = CanvasBridge.beginPendingLaunchAt now worktree
            let api, _ = createApiWithStateUsing Async.Sleep beginLaunch root path None operations
            let message payload = { WorktreePath = path; Filename = "diff.html"; Payload = payload }
            Assert.That(runAsync (api.sendCanvasMessage (message "first")), Is.EqualTo CanvasMessageResult.Queued)
            now <- startedAt.AddSeconds(float elapsedSeconds)
            Assert.That(runAsync (api.sendCanvasMessage (message "next")), Is.EqualTo CanvasMessageResult.Queued)
            let pending = runAsync (SessionBridge.pendingPrompts (WorktreePath.value path))
            let targets =
                pending |> List.map (fun item ->
                    match item.Target with
                    | SessionBridge.QueuedTarget.ExactTerminal(terminal, _) -> TerminalSessionId.value terminal
                    | _ -> failwith "Both completed launches must retain their own exact terminal")
            Assert.Multiple(fun () ->
                Assert.That(launches.Count, Is.EqualTo 2, "The 30-second spawn window is unchanged")
                Assert.That(pending |> List.map _.Prompt.Text, Is.EqualTo [ "first"; "next" ])
                Assert.That(targets, Is.EqualTo [ EmbeddedTerminalId.value firstTerminal; EmbeddedTerminalId.value nextTerminal ])))

    [<Test>]
    [<Category("CanvasRoutingFollowers")>]
    member _.``Caller cancellation does not abandon shared grace and preserves follower work``() =
        withTempDir "treemon-canvas-cancelled-grace-followers" (fun root ->
            let path = PathUtils.toWorktreePath root
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let delays = ConcurrentQueue<int>()
            let delay milliseconds =
                async {
                    delays.Enqueue milliseconds
                    if delays.Count = 1 then
                        entered.TrySetResult() |> ignore
                        do! release.Task |> Async.AwaitTask
                }
            let launches = ConcurrentQueue<WorktreePath>()
            let operations =
                promptedLaunchOnly
                    (fun _ requestedPath _ ->
                      async {
                          launches.Enqueue requestedPath
                          return Error PromptedLaunchError.TerminalStartFailed
                      })
            let api, agent = createApiWithStateUsing delay CanvasBridge.beginPendingLaunch root path None operations
            let now = DateTimeOffset.UtcNow
            let owner = $"selected-{Guid.NewGuid():N}"
            agent.Post(UpdateSessionInstance(liveSession now path (EmbeddedTerminalId(Guid.NewGuid().ToString "N")) owner, now))
            let message payload = { WorktreePath = path; Filename = "diff.html"; Payload = payload }
            use cancellation = new System.Threading.CancellationTokenSource()
            let first = api.sendCanvasMessage (message "first") |> fun workflow ->
                Async.StartAsTask(workflow, cancellationToken = cancellation.Token)
            entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            Assert.That(runAsync (api.sendCanvasMessage (message "follower")), Is.EqualTo CanvasMessageResult.Queued)
            let original = runAsync (SessionBridge.pendingPrompts (WorktreePath.value path))
            cancellation.Cancel()
            release.TrySetResult() |> ignore
            Assert.That(
                first.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
                Is.EqualTo(CanvasMessageResult.SessionStartFailed PromptedLaunchError.TerminalStartFailed))
            runAsync (CanvasBridge.cancelPendingLaunch (WorktreePath.value path))
            Assert.That(
                runAsync (api.sendCanvasMessage (message "retry")),
                Is.EqualTo(CanvasMessageResult.SessionStartFailed PromptedLaunchError.TerminalStartFailed))
            let retained = runAsync (SessionBridge.pendingPrompts (WorktreePath.value path))
            Assert.Multiple(fun () ->
                Assert.That(delays.ToArray(), Is.EqualTo [| 3000; 3000 |])
                Assert.That(launches.Count, Is.EqualTo 2)
                Assert.That(retained |> List.map _.Prompt.Text, Is.EqualTo [ "follower" ])
                Assert.That(retained |> List.map _.EnqueuedAt, Is.EqualTo(original |> List.tail |> List.map _.EnqueuedAt))))

    [<Test>]
    [<Category("CanvasRoutingFollowers")>]
    member _.``Shared grace retains a different selected follower recipient and rechecks its own bridge``() =
        withTempDir "treemon-canvas-distinct-grace-followers" (fun root ->
            let path = PathUtils.toWorktreePath root
            let originator = $"originator-{Guid.NewGuid():N}"
            let follower = $"follower-{Guid.NewGuid():N}"
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let followerGrace = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let followerRelease = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let delays = ConcurrentQueue<int>()
            let delay milliseconds =
                async {
                    delays.Enqueue milliseconds
                    if delays.Count = 1 then
                        entered.TrySetResult() |> ignore
                        do! release.Task |> Async.AwaitTask
                    else
                        followerGrace.TrySetResult() |> ignore
                        do! followerRelease.Task |> Async.AwaitTask
                }
            let operations =
                promptedLaunchOnly (fun _ _ _ -> failwith "Each selected recipient recovers its own bridge")
            let api, agent = createApiWithStateUsing delay CanvasBridge.beginPendingLaunch root path None operations
            let now = DateTimeOffset.UtcNow
            let originActivity = liveSession now path (EmbeddedTerminalId(Guid.NewGuid().ToString "N")) originator
            let followerActivity = liveSession (now.AddSeconds 1.0) path (EmbeddedTerminalId(Guid.NewGuid().ToString "N")) follower
            agent.Post(UpdateSessionInstance(originActivity, now))
            let first = api.sendCanvasMessage { WorktreePath = path; Filename = "diff.html"; Payload = "origin" } |> Async.StartAsTask
            entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            agent.Post(UpdateSessionInstance(followerActivity, now))
            let joining = api.sendCanvasMessage { WorktreePath = path; Filename = "diff.html"; Payload = "follower" } |> Async.StartAsTask
            Assert.That(joining.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(), Is.EqualTo CanvasMessageResult.Queued)
            let queued = runAsync (SessionBridge.pendingPrompts (WorktreePath.value path))
            match (queued |> List.find (fun item -> item.Prompt.Text = "follower")).Target with
            | SessionBridge.QueuedTarget.Session(SessionBridge.SendTarget.DurableSession expected) ->
                Assert.That(expected, Is.EqualTo(SessionId follower))
            | _ -> Assert.Fail "The follower lost its independently selected recipient"
            use firstListener = new HttpListener()
            use secondListener = new HttpListener()
            let ports = getFreeTcpPorts 2
            let firstUrl, secondUrl = $"http://127.0.0.1:{ports[0]}/", $"http://127.0.0.1:{ports[1]}/"
            firstListener.Prefixes.Add firstUrl
            secondListener.Prefixes.Add secondUrl
            firstListener.Start()
            secondListener.Start()
            registerExactSession 'A' originActivity.ProcessIdentity (WorktreePath.value path) firstUrl (Some originator) None |> ignore
            let firstContext = firstListener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            use firstReader = new StreamReader(firstContext.Request.InputStream)
            let originBody = firstReader.ReadToEnd()
            firstContext.Response.StatusCode <- 200
            firstContext.Response.Close()
            release.TrySetResult() |> ignore
            followerGrace.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            Assert.That(first.IsCompleted, Is.False, "The shared scheduler must retain the distinct unresolved recipient")
            registerExactSession 'A' followerActivity.ProcessIdentity (WorktreePath.value path) secondUrl (Some follower) None |> ignore
            let followerContext = secondListener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            use followerReader = new StreamReader(followerContext.Request.InputStream)
            let followerBody = followerReader.ReadToEnd()
            followerContext.Response.StatusCode <- 200
            followerContext.Response.Close()
            followerRelease.TrySetResult() |> ignore
            Assert.That(first.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(), Is.EqualTo CanvasMessageResult.Queued)
            Assert.That([ originBody; followerBody ], Is.EqualTo([ "origin"; "follower" ] |> List.map (fun payload ->
                SessionBridge.Prompt.canvasFor (WorktreePath.value path) "diff.html" payload
                |> SessionBridge.serializePrompt)))
            Assert.That(delays.ToArray(), Is.EqualTo [| 3000; 3000 |]))

    [<TestCase("relative", "report.html", "Invalid canvas worktree path")>]
    [<TestCase("", "report.html", "Invalid canvas worktree path")>]
    [<TestCase("known", "../report.html", "Invalid canvas filename")>]
    [<TestCase("known", "unsafe name.html", "Invalid canvas filename")>]
    member _.``Canvas API validates source coordinates before queueing or launching``(source: string, filename: string, expected: string) =
        withTempDir "treemon-canvas-invalid-source" (fun root ->
            let path = PathUtils.toWorktreePath root
            let operations =
                promptedLaunchOnly (fun _ _ _ -> failwith "Invalid source must not launch a terminal")
            let api = createApi root path None operations
            let requestedPath = if source = "known" then path else WorktreePath source
            let result =
                runAsync (api.sendCanvasMessage { WorktreePath = requestedPath; Filename = filename; Payload = "{}" })
            Assert.That(result, Is.EqualTo(CanvasMessageResult.Error expected))
            Assert.That(runAsync (SessionBridge.pendingPrompts (WorktreePath.value path)), Is.Empty))
