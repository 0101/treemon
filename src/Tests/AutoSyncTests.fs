module Tests.AutoSyncTests

open System
open System.IO
open System.Net
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Server.AutoSync
open Server.RefreshScheduler
open Server.SessionActivity
open Server.SessionActivityStore

let private tempDirectory () =
    let path = Path.Combine(Path.GetTempPath(), $"treemon-auto-sync-{Guid.NewGuid():N}")
    Directory.CreateDirectory(path) |> ignore
    path

let private storedSession sessionId worktreePath status updatedAt lastSeen =
    { SessionId = SessionId sessionId
      WorktreePath = WorktreePath worktreePath
      Provider = CopilotCli
      Status = { emptyStatus with Status = status }
      UpdatedAt = updatedAt
      LastSeen = lastSeen
      ContextUsageAt = None }

let private gitData path branch behind revision dirty : GitWorktree.GitData =
    { Path = path
      Branch = branch
      LastCommitMessage = ""
      LastCommitTime = DateTimeOffset.MinValue
      UpstreamBranch = None
      MainBehindCount = behind
      BaseRevision = revision
      IsDirty = dirty
      HasDiff = false
      WorkMetrics = None }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncPersistenceTests() =

    // NUnit lifecycle field: setup creates the per-test directory and teardown consumes it, so it must span fixture members.
    let mutable root = ""

    [<SetUp>]
    member _.SetUp() =
        root <- tempDirectory ()

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    member _.``Missing auto-sync config reads as an empty set``() =
        Assert.That(TreemonConfig.readAutoSyncBranchSet (Some root), Is.Empty)

    [<Test>]
    member _.``Writing auto-sync branches preserves unrelated repo config``() =
        let path = Path.Combine(root, ".treemon.json")
        File.WriteAllText(path, """{ "archivedBranches": ["old"], "baseBranch": "develop" }""")

        TreemonConfig.setAutoSyncBranches root [ "feature-a"; "feature-b" ]

        Assert.Multiple(fun () ->
            Assert.That(
                TreemonConfig.readAutoSyncBranches root,
                Is.EqualTo([ "feature-a"; "feature-b" ]))
            Assert.That(TreemonConfig.readArchivedBranches root, Is.EqualTo([ "old" ]))
            Assert.That(TreemonConfig.readBaseBranch root, Is.EqualTo("develop")))

    [<Test>]
    member _.``Adding auto-sync preference does not prune stale branch names``() =
        TreemonConfig.setAutoSyncBranches root [ "deleted-branch" ]

        TreemonConfig.modifyAutoSyncBranches root (Set.ofList >> Set.add "feature-a" >> Set.toList)

        Assert.That(
            TreemonConfig.readAutoSyncBranchSet (Some root),
            Is.EqualTo(Set.ofList [ "deleted-branch"; "feature-a" ]))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncSelectionTests() =

    let now = DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``Active open winner supplies the auto-sync session id``() =
        let active =
            storedSession
                "active"
                "/repo/wt"
                SessionLevelStatus.Working
                (now.AddMinutes(-2.0))
                (now.AddMinutes(-2.0))

        let newerIdle =
            storedSession
                "idle"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-1.0))
                (now.AddMinutes(-1.0))

        Assert.That(
            selectTargetSessionId now [ newerIdle; active ],
            Is.EqualTo(Some "active"))

    [<Test>]
    member _.``Greatest activity UpdatedAt supplies the open idle auto-sync session id``() =
        let older =
            storedSession
                "older"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-2.0))
                (now.AddSeconds(-10.0))

        let newer =
            storedSession
                "newer"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-1.0))
                (now.AddMinutes(-2.0))

        Assert.That(
            selectTargetSessionId now [ older; newer ],
            Is.EqualTo(Some "newer"))

    [<Test>]
    member _.``Greatest activity UpdatedAt supplies a retained id only when no session is open``() =
        let older =
            storedSession
                "older"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-2.0))
                (now.AddMinutes(-20.0))

        let newer =
            storedSession
                "newer"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-1.0))
                (now.AddMinutes(-10.0))

        Assert.That(
            selectTargetSessionId now [ older; newer ],
            Is.EqualTo(Some "newer"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncTriggerTests() =

    [<Test>]
    member _.``Base revision is the deduplication identity``() =
        async {
            let agent = createAgent ()
            let path = "/repo/wt"

            let! first =
                agent.PostAndAsyncReply(fun reply -> ClaimAutoSyncTrigger(path, "base-a", reply))

            let! repeated =
                agent.PostAndAsyncReply(fun reply -> ClaimAutoSyncTrigger(path, "base-a", reply))

            let! advanced =
                agent.PostAndAsyncReply(fun reply -> ClaimAutoSyncTrigger(path, "base-b", reply))

            Assert.Multiple(fun () ->
                Assert.That(first, Is.True)
                Assert.That(repeated, Is.False)
                Assert.That(advanced, Is.True))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Disabling clears trigger state so re-enabling can send immediately``() =
        async {
            let agent = createAgent ()
            let path = "/repo/wt"

            let! _ =
                agent.PostAndAsyncReply(fun reply -> ClaimAutoSyncTrigger(path, "base-a", reply))

            agent.Post(ClearAutoSyncTrigger path)

            let! afterDisable =
                agent.PostAndAsyncReply(fun reply -> ClaimAutoSyncTrigger(path, "base-a", reply))

            Assert.That(afterDisable, Is.True)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Fallback launch guard allows only one in-flight launch per worktree``() =
        async {
            let agent = createAgent ()
            let path = "/repo/wt"

            let! first =
                agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncLaunch(path, reply))

            let! duplicate =
                agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncLaunch(path, reply))

            agent.Post(CompleteAutoSyncLaunch path)

            let! afterCompletion =
                agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncLaunch(path, reply))

            Assert.Multiple(fun () ->
                Assert.That(first, Is.True)
                Assert.That(duplicate, Is.False)
                Assert.That(afterCompletion, Is.True))
        }
        |> Async.RunSynchronously

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Dirty behind worktrees remain eligible and up-to-date worktrees do not``() =
        let dirtyBehind = gitData "/repo/wt" "feature" 2 (Some "base-a") true
        let upToDate = gitData "/repo/wt" "feature" 0 (Some "base-a") true
        let dirtyBehindRevision = revision true dirtyBehind
        let upToDateRevision = revision true upToDate
        let disabledRevision = revision false dirtyBehind

        Assert.Multiple(fun () ->
            Assert.That(dirtyBehindRevision, Is.EqualTo(Some "base-a"))
            Assert.That(upToDateRevision, Is.EqualTo None)
            Assert.That(disabledRevision, Is.EqualTo None))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncSchedulerDispatchTests() =

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Slow auto-sync delivery does not delay the next scheduled task``() =
        let root = tempDirectory ()
        let releaseDelivery =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let deliveryStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let deliveryCompleted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let nextTaskRan =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        try
            TreemonConfig.setAutoSyncBranches root [ "feature" ]

            let dependencies =
                { ClaimRevision = fun _ _ -> async { return true }
                  ReleaseRevision = fun _ _ -> ()
                  SelectSessionId = fun _ -> async { return Some "session-a" }
                  Deliver =
                    fun _ ->
                        async {
                            deliveryStarted.TrySetResult(()) |> ignore
                            do! releaseDelivery.Task |> Async.AwaitTask
                            deliveryCompleted.TrySetResult(()) |> ignore
                            return true
                        } }

            let schedulerStep =
                async {
                    triggerInBackground
                        dependencies
                        root
                        "origin"
                        "main"
                        (gitData (Path.Combine(root, "feature")) "feature" 1 (Some "base-a") false)

                    nextTaskRan.TrySetResult(()) |> ignore
                }
                |> Async.StartAsTask

            nextTaskRan.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            schedulerStep.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            deliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

            let schedulerCompleted = schedulerStep.IsCompletedSuccessfully
            let deliveryCompletedBeforeRelease = deliveryCompleted.Task.IsCompleted

            Assert.Multiple(fun () ->
                Assert.That(schedulerCompleted, Is.True)
                Assert.That(deliveryCompletedBeforeRelease, Is.False))

            releaseDelivery.TrySetResult(()) |> ignore
            deliveryCompleted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        finally
            releaseDelivery.TrySetResult(()) |> ignore
            if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    member _.``Guarded background execution catches workflow failures``() =
        let observed =
            TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

        startGuarded
            (fun ex -> observed.TrySetResult(ex.Message) |> ignore)
            (async { return raise (InvalidOperationException "configuration read failed") })

        Assert.That(
            observed.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
            Is.EqualTo("configuration read failed"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncDeliveryTests() =

    let request =
        { WorktreePath = WorktreePath "/repo/wt"
          SessionId = Some "session-a"
          Prompt = "Sync with upstream/main." }

    [<Test>]
    member _.``Live selected session receives the agent prompt without fallback launch``() =
        let expected: SessionBridge.SendRequest =
            { WorktreePath = "/repo/wt"
              SessionId = Some "session-a"
              Prompt = SessionBridge.Prompt.agentPrompt "Sync with upstream/main." }

        let tryDeliver (value: SessionBridge.SendRequest) =
            async {
                Assert.That(value, Is.EqualTo expected)
                return SessionBridge.DeliveryResult.Delivered
            }

        let launch _ _ = failwith "fallback launch must not run"

        let accepted =
            deliver
                tryDeliver
                (fun () -> failwith "registration grace must not run")
                (fun _ -> async { return true })
                ignore
                launch
                request
            |> Async.RunSynchronously

        Assert.That(accepted, Is.True)

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Open idle session beats newer retained session without fallback launch``() =
        let root = tempDirectory ()

        try
            let now = DateTimeOffset.UtcNow
            let path = "/repo/wt"
            let openIdle =
                storedSession
                    "open-idle"
                    path
                    SessionLevelStatus.Idle
                    (now.AddMinutes(-2.0))
                    now

            let newerClosed =
                storedSession
                    "newer-closed"
                    path
                    SessionLevelStatus.Idle
                    (now.AddMinutes(-1.0))
                    (now.AddMinutes(-10.0))

            use store = new SessionActivityStore(Path.Combine(root, "session-activity.db"))
            store.UpsertStatus newerClosed

            let sessionId = selectSessionId (Some store) [ openIdle ] path

            let tryDeliver (value: SessionBridge.SendRequest) =
                async {
                    Assert.That(value.SessionId, Is.EqualTo(Some "open-idle"))
                    return SessionBridge.DeliveryResult.Delivered
                }

            let tryBeginLaunch _ = failwith "fallback launch guard must not run"
            let launch _ _ = failwith "fallback launch must not run"

            let accepted =
                deliver
                    tryDeliver
                    (fun () -> failwith "registration grace must not run")
                    tryBeginLaunch
                    ignore
                    launch
                    { request with SessionId = sessionId }
                |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(sessionId, Is.EqualTo(Some "open-idle"))
                Assert.That(accepted, Is.True))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Selected session registering during grace receives prompt without fallback launch``() =
        // Callback probes cross async boundaries, so immutable values cannot capture their ordering.
        let mutable attempts = 0
        let mutable registrationCompleted = false
        let mutable graceCalls = 0
        let mutable launchAttempts = 0

        let tryDeliver _ =
            async {
                attempts <- attempts + 1

                return
                    if registrationCompleted then
                        SessionBridge.DeliveryResult.Delivered
                    else
                        SessionBridge.DeliveryResult.NoLiveSession
            }

        let accepted =
            deliver
                tryDeliver
                (fun () ->
                    async {
                        graceCalls <- graceCalls + 1
                        registrationCompleted <- true
                    })
                (fun _ -> failwith "fallback launch guard must not run")
                ignore
                (fun _ _ ->
                    async {
                        launchAttempts <- launchAttempts + 1
                        return Ok ()
                    })
                request
            |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(attempts, Is.EqualTo(2))
            Assert.That(graceCalls, Is.EqualTo(1))
            Assert.That(registrationGraceMilliseconds, Is.InRange(1, 5000))
            Assert.That(launchAttempts, Is.Zero))

    [<Test>]
    member _.``Successful fallback holds the launch guard through registration grace``() =
        // Callback probes cross async boundaries, so immutable values cannot capture invocation counts.
        let mutable guardHeld = false
        let mutable completions = 0
        let graceStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseGrace =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let tryBeginLaunch _ =
            async {
                if guardHeld then
                    return false
                else
                    guardHeld <- true
                    return true
            }

        let delivery =
            deliver
                (fun value ->
                    async {
                        Assert.That(value.SessionId, Is.EqualTo None)
                        return SessionBridge.DeliveryResult.NoLiveSession
                    })
                (fun () ->
                    async {
                        graceStarted.TrySetResult(()) |> ignore
                        do! releaseGrace.Task |> Async.AwaitTask
                    })
                tryBeginLaunch
                (fun _ ->
                    completions <- completions + 1
                    guardHeld <- false)
                (fun path prompt ->
                    async {
                        Assert.That(path, Is.EqualTo(WorktreePath "/repo/wt"))
                        Assert.That(prompt, Is.EqualTo("Sync with upstream/main."))
                        return Ok ()
                    })
                { request with SessionId = None }
            |> Async.StartAsTask

        graceStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        let duplicateAccepted = tryBeginLaunch "/repo/wt" |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(duplicateAccepted, Is.False)
            Assert.That(guardHeld, Is.True)
            Assert.That(completions, Is.Zero)
            Assert.That(delivery.IsCompleted, Is.False))

        releaseGrace.TrySetResult(()) |> ignore
        let accepted =
            delivery.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        let acceptedAfterGrace = tryBeginLaunch "/repo/wt" |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(completions, Is.EqualTo(1))
            Assert.That(acceptedAfterGrace, Is.True))

    [<Test>]
    member _.``No selected session attempts bridge before fallback launch``() =
        // The async launch callback is the impure boundary under test.
        let mutable deliveryAttempts = 0
        let mutable graceCalls = 0
        let mutable launchAttempts = 0

        let accepted =
            deliver
                (fun value ->
                    async {
                        deliveryAttempts <- deliveryAttempts + 1
                        Assert.That(value.SessionId, Is.EqualTo None)
                        return SessionBridge.DeliveryResult.NoLiveSession
                    })
                (fun () ->
                    async {
                        graceCalls <- graceCalls + 1
                    })
                (fun _ -> async { return true })
                ignore
                (fun _ _ ->
                    async {
                        launchAttempts <- launchAttempts + 1
                        return Ok ()
                    })
                { request with SessionId = None }
            |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(deliveryAttempts, Is.EqualTo(1))
            Assert.That(graceCalls, Is.EqualTo(1))
            Assert.That(launchAttempts, Is.EqualTo(1)))

    [<TestCase("error")>]
    [<TestCase("exception")>]
    member _.``Failed fallback releases the launch guard immediately``(failureKind: string) =
        // Callback probes cross async boundaries, so immutable values cannot capture guard state.
        let mutable guardHeld = false
        let mutable completions = 0

        let accepted =
            deliver
                (fun _ -> async { return SessionBridge.DeliveryResult.NoLiveSession })
                (fun () -> failwith "registration grace must not run after failed launch")
                (fun _ ->
                    async {
                        guardHeld <- true
                        return true
                    })
                (fun _ ->
                    completions <- completions + 1
                    guardHeld <- false)
                (fun _ _ ->
                    async {
                        if failureKind = "exception" then
                            return raise (InvalidOperationException "launch failed")
                        else
                            return Error "launch failed"
                    })
                { request with SessionId = None }
            |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.False)
            Assert.That(guardHeld, Is.False)
            Assert.That(completions, Is.EqualTo(1)))

    [<Test>]
    member _.``Delivery failure is accepted for queued retry without fallback launch``() =
        let accepted =
            deliver
                (fun _ -> async { return SessionBridge.DeliveryResult.DeliveryFailed })
                (fun () -> failwith "registration grace must not run")
                (fun _ -> failwith "fallback launch guard must not run")
                ignore
                (fun _ _ -> failwith "fallback launch must not run")
                request
            |> Async.RunSynchronously

        Assert.That(accepted, Is.True)

    [<Test>]
    member _.``In-flight fallback guard prevents a duplicate launch after grace``() =
        let launch _ _ = failwith "duplicate launch must not run"

        let accepted =
            deliver
                (fun _ -> async { return SessionBridge.DeliveryResult.NoLiveSession })
                (fun () -> async { return () })
                (fun _ -> async { return false })
                ignore
                launch
                request
            |> Async.RunSynchronously

        Assert.That(accepted, Is.False)

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Transient bridge POST failure queues the prompt for retry on registration``() =
        let path = $"/test/auto-sync-retry/{Guid.NewGuid():N}"
        let sessionId = $"session-{Guid.NewGuid():N}"
        let port = TestUtils.getFreeTcpPort ()
        // Delivery callbacks cross async HTTP boundaries, so counters must be captured mutably.
        let mutable fallbackGuardAttempts = 0
        let mutable fallbackLaunchAttempts = 0

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        SessionBridge.registerSession path $"http://127.0.0.1:{port}/" (Some sessionId)

        let firstRequest = listener.GetContextAsync()
        let delivery =
            deliver
                SessionBridge.tryDeliver
                (fun () -> failwith "registration grace must not run")
                (fun _ ->
                    async {
                        fallbackGuardAttempts <- fallbackGuardAttempts + 1
                        return true
                    })
                ignore
                (fun _ _ ->
                    async {
                        fallbackLaunchAttempts <- fallbackLaunchAttempts + 1
                        return Ok ()
                    })
                { request with
                    WorktreePath = WorktreePath path
                    SessionId = Some sessionId }
            |> Async.StartAsTask

        let firstContext =
            firstRequest.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        firstContext.Response.StatusCode <- 503
        firstContext.Response.Close()

        let accepted = delivery.GetAwaiter().GetResult()

        let retryRequest = listener.GetContextAsync()
        SessionBridge.registerSession path $"http://127.0.0.1:{port}/" (Some sessionId)

        let retryContext =
            retryRequest.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        use reader = new StreamReader(retryContext.Request.InputStream)
        let body = reader.ReadToEnd()
        retryContext.Response.StatusCode <- 200
        retryContext.Response.Close()

        let expectedBody =
            SessionBridge.serializePrompt(
                SessionBridge.Prompt.agentPrompt request.Prompt)

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(body, Is.EqualTo expectedBody)
            Assert.That(fallbackGuardAttempts, Is.Zero)
            Assert.That(fallbackLaunchAttempts, Is.Zero))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncEndpointTests() =

    // NUnit lifecycle fields: setup initializes per-test paths consumed by tests and teardown, so immutable locals cannot span those members.
    let mutable root = ""
    let mutable worktree = ""

    [<SetUp>]
    member _.SetUp() =
        root <- tempDirectory ()
        worktree <- Path.Combine(root, "feature-a")
        Directory.CreateDirectory(worktree) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Differently-cased disable lets the same base revision trigger again``() =
        let normalizedPath = PathUtils.normalizePath worktree
        let differentlyCasedPath = normalizedPath.ToUpperInvariant()
        let repoId = PathUtils.toRepoId root
        let now = DateTimeOffset.UtcNow
        let agent = createAgent ()
        let sessionAgent = SessionManager.createAgent ()
        let port = TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        agent.Post(
            UpdateWorktreeList(
                repoId,
                [ { GitWorktree.WorktreeInfo.Path = normalizedPath
                    Head = "head"
                    Branch = Some "feature-a" } ]))

        agent.Post(
            UpdateGit(
                repoId,
                normalizedPath,
                gitData normalizedPath "feature-a" 2 (Some "base-a") true))

        agent.Post(
            UpdateSessionStatus(
                storedSession
                    "session-a"
                    normalizedPath
                    SessionLevelStatus.Working
                    now
                    now))

        SessionBridge.registerSession
            normalizedPath
            $"http://127.0.0.1:{port}/"
            (Some "session-a")

        let api =
            WorktreeApi.worktreeApi
                agent
                (CardEventLog.createAgent ())
                sessionAgent
                None
                None
                [ root ]
                None
                "1.0"
                None

        let enableAndReceive apiWorktreePath =
            let receive = listener.GetContextAsync()
            let toggleTask =
                api.toggleAutoSync apiWorktreePath true
                |> Async.StartAsTask

            let context = receive.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            use reader = new StreamReader(context.Request.InputStream)
            let body = reader.ReadToEnd()
            context.Response.StatusCode <- 200
            context.Response.Close()
            body, toggleTask.GetAwaiter().GetResult()

        let body, result = enableAndReceive (WorktreePath differentlyCasedPath)
        let dashboard = api.getWorktrees () |> Async.RunSynchronously
        let status = dashboard.Repos |> List.collect _.Worktrees |> List.exactlyOne
        let enabledState = agent.PostAndReply(GetState)
        let duplicateRefreshClaim =
            agent.PostAndReply(fun reply ->
                ClaimAutoSyncTrigger(normalizedPath, "base-a", reply))

        Assert.Multiple(fun () ->
            Assert.That(differentlyCasedPath, Is.Not.EqualTo(normalizedPath))
            Assert.That(Result.isOk result, Is.True)
            Assert.That(
                TreemonConfig.readAutoSyncBranchSet (Some root),
                Is.EqualTo(Set.singleton "feature-a"))
            Assert.That(
                body,
                Is.EqualTo(
                    SessionBridge.serializePrompt(
                    SessionBridge.Prompt.agentPrompt(prompt "origin" "main"))))
            Assert.That(status.AutoSyncEnabled, Is.True)
            Assert.That(
                enabledState.AutoSyncTriggeredRevisions
                |> Map.toList
                |> List.map fst,
                Is.EqualTo([ normalizedPath ]))
            Assert.That(duplicateRefreshClaim, Is.False))

        let disableResult =
            api.toggleAutoSync (WorktreePath differentlyCasedPath) false
            |> Async.RunSynchronously
        let disabledBranches = TreemonConfig.readAutoSyncBranchSet (Some root)
        let disabledState = agent.PostAndReply(GetState)
        let secondBody, reenableResult = enableAndReceive (WorktreePath normalizedPath)
        let finalState = agent.PostAndReply(GetState)

        Assert.Multiple(fun () ->
            Assert.That(Result.isOk disableResult, Is.True)
            Assert.That(disabledBranches, Is.Empty)
            Assert.That(Map.containsKey normalizedPath disabledState.AutoSyncTriggeredRevisions, Is.False)
            Assert.That(Result.isOk reenableResult, Is.True)
            Assert.That(secondBody, Is.EqualTo(body))
            Assert.That(
                finalState.AutoSyncTriggeredRevisions |> Map.tryFind normalizedPath,
                Is.EqualTo(Some "base-a")))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("AutoSyncVerification")>]
type AutoSyncVerificationTests() =

    [<Test>]
    member _.``Verification selected working session receives one configured generic prompt``() =
        let root = tempDirectory ()
        let worktree = Path.Combine(root, "feature-a")
        Directory.CreateDirectory(worktree) |> ignore
        let normalizedPath = PathUtils.normalizePath worktree
        let repoId = PathUtils.toRepoId root
        let agent = createAgent ()
        let sessionAgent = SessionManager.createAgent ()
        let selectedPort, otherPort =
            match TestUtils.getFreeTcpPorts 2 with
            | [ selected; other ] -> selected, other
            | ports -> failwith $"expected two free ports, got {ports.Length}"

        use selectedListener = new HttpListener()
        use otherListener = new HttpListener()
        selectedListener.Prefixes.Add($"http://127.0.0.1:{selectedPort}/")
        otherListener.Prefixes.Add($"http://127.0.0.1:{otherPort}/")
        selectedListener.Start()
        otherListener.Start()

        try
            let now = DateTimeOffset.UtcNow

            agent.Post(
                UpdateWorktreeList(
                    repoId,
                    [ { GitWorktree.WorktreeInfo.Path = normalizedPath
                        Head = "head"
                        Branch = Some "feature-a" } ]))
            agent.Post(UpdateUpstreamRemote(repoId, "upstream"))
            agent.Post(UpdateBaseBranch(repoId, "develop"))
            agent.Post(
                UpdateGit(
                    repoId,
                    normalizedPath,
                    gitData normalizedPath "feature-a" 2 (Some "base-a") true))
            agent.Post(
                UpdateSessionStatus(
                    storedSession
                        "selected-working"
                        normalizedPath
                        SessionLevelStatus.Working
                        (now.AddMinutes(-2.0))
                        now))
            agent.Post(
                UpdateSessionStatus(
                    storedSession
                        "other-idle"
                        normalizedPath
                        SessionLevelStatus.Idle
                        (now.AddMinutes(-1.0))
                        now))

            SessionBridge.registerSession
                normalizedPath
                $"http://127.0.0.1:{selectedPort}/"
                (Some "selected-working")
            SessionBridge.registerSession
                normalizedPath
                $"http://127.0.0.1:{otherPort}/"
                (Some "other-idle")

            let api =
                WorktreeApi.worktreeApi
                    agent
                    (CardEventLog.createAgent ())
                    sessionAgent
                    None
                    None
                    [ root ]
                    None
                    "1.0"
                    None

            let selectedRequest = selectedListener.GetContextAsync()
            let otherRequest = otherListener.GetContextAsync()
            let toggleTask =
                api.toggleAutoSync (WorktreePath normalizedPath) true
                |> Async.StartAsTask

            let selectedContext =
                selectedRequest.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            use reader = new StreamReader(selectedContext.Request.InputStream)
            let body = reader.ReadToEnd()
            selectedContext.Response.StatusCode <- 200
            selectedContext.Response.Close()

            let result = toggleTask.GetAwaiter().GetResult()
            let duplicateSelectedRequest = selectedListener.GetContextAsync()
            let expectedPrompt = prompt "upstream" "develop"
            let expectedBody =
                SessionBridge.serializePrompt(
                    SessionBridge.Prompt.agentPrompt expectedPrompt)
            let otherReceived = otherRequest.IsCompleted
            let duplicateDelivery = duplicateSelectedRequest.IsCompleted

            Assert.Multiple(fun () ->
                Assert.That(Result.isOk result, Is.True)
                Assert.That(body, Is.EqualTo expectedBody)
                Assert.That(body.StartsWith("{\"kind\":\"agent-prompt\"", StringComparison.Ordinal), Is.True)
                Assert.That(body.Contains("[canvas]", StringComparison.Ordinal), Is.False)
                Assert.That(otherReceived, Is.False, "The other session must not receive the prompt")
                Assert.That(duplicateDelivery, Is.False, "Delivery must occur exactly once"))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    member _.``Verification repeated no-live observations launch one new session with the same prompt``() =
        let root = tempDirectory ()
        let path = Path.Combine(root, "feature-a")
        Directory.CreateDirectory(path) |> ignore
        let agent = createAgent ()
        let expectedPrompt = prompt "upstream" "develop"
        // Mutable because the launch callback is the impure boundary whose invocation count is under test.
        let mutable launches = []

        try
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]

            let dependencies =
                { ClaimRevision =
                    fun worktreePath baseRevision ->
                        agent.PostAndAsyncReply(fun reply ->
                            ClaimAutoSyncTrigger(worktreePath, baseRevision, reply))
                  ReleaseRevision =
                    fun worktreePath baseRevision ->
                        agent.Post(ReleaseAutoSyncTrigger(worktreePath, baseRevision))
                  SelectSessionId = fun _ -> async { return Some "retained-session" }
                  Deliver =
                    deliver
                        (fun _ -> async { return SessionBridge.DeliveryResult.NoLiveSession })
                        (fun () -> async { return () })
                        (fun worktreePath ->
                            agent.PostAndAsyncReply(fun reply ->
                                TryBeginAutoSyncLaunch(worktreePath, reply)))
                        (CompleteAutoSyncLaunch >> agent.Post)
                        (fun worktreePath promptText ->
                            async {
                                launches <- (worktreePath, promptText) :: launches
                                return Ok ()
                            }) }

            let observation = gitData path "feature-a" 2 (Some "base-a") true
            trigger dependencies root "upstream" "develop" observation
            |> Async.RunSynchronously
            trigger dependencies root "upstream" "develop" observation
            |> Async.RunSynchronously

            let expectedLaunches = [ WorktreePath path, expectedPrompt ]

            Assert.That(
                launches,
                Is.EqualTo expectedLaunches,
                "Repeated observations of one base revision must start exactly one new prompted session")
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    member _.``Verification revision observations disabling re-enabling and config reload``() =
        let root = tempDirectory ()
        let path = Path.Combine(root, "feature-a")
        Directory.CreateDirectory(path) |> ignore
        let agent = createAgent ()
        // Mutable because delivery is the impure boundary whose exact invocation sequence is under test.
        let mutable deliveries = []

        try
            File.WriteAllText(
                Path.Combine(root, ".treemon.json"),
                """{ "archivedBranches": ["old"], "baseBranch": "develop", "custom": {"keep": true} }""")
            TreemonConfig.modifyAutoSyncBranches root (Set.ofList >> Set.add "feature-a" >> Set.toList)

            let dependencies =
                { ClaimRevision =
                    fun worktreePath baseRevision ->
                        agent.PostAndAsyncReply(fun reply ->
                            ClaimAutoSyncTrigger(worktreePath, baseRevision, reply))
                  ReleaseRevision =
                    fun worktreePath baseRevision ->
                        agent.Post(ReleaseAutoSyncTrigger(worktreePath, baseRevision))
                  SelectSessionId = fun _ -> async { return Some "selected-working" }
                  Deliver =
                    fun request ->
                        async {
                            deliveries <- request :: deliveries
                            return true
                        } }

            let observe revision =
                gitData path "feature-a" 2 (Some revision) true
                |> trigger dependencies root "upstream" "develop"
                |> Async.RunSynchronously

            observe "base-a"
            observe "base-a"
            let afterRepeated = deliveries.Length

            observe "base-b"
            let afterAdvance = deliveries.Length

            TreemonConfig.modifyAutoSyncBranches root (Set.ofList >> Set.remove "feature-a" >> Set.toList)
            agent.Post(ClearAutoSyncTrigger path)
            observe "base-b"
            let afterDisable = deliveries.Length

            TreemonConfig.modifyAutoSyncBranches root (Set.ofList >> Set.add "feature-a" >> Set.toList)
            observe "base-b"
            let afterReenable = deliveries.Length

            use config =
                System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(root, ".treemon.json")))
            let custom = config.RootElement.GetProperty("custom").GetProperty("keep").GetBoolean()
            let finalState = agent.PostAndReply(GetState)
            let expectedPrompt = prompt "upstream" "develop"

            Assert.Multiple(fun () ->
                Assert.That(afterRepeated, Is.EqualTo(1))
                Assert.That(afterAdvance, Is.EqualTo(2))
                Assert.That(afterDisable, Is.EqualTo(2))
                Assert.That(afterReenable, Is.EqualTo(3))
                Assert.That(
                    deliveries |> List.map _.Prompt,
                    Is.EqualTo(List.replicate 3 expectedPrompt))
                Assert.That(
                    TreemonConfig.readAutoSyncBranchSet (Some root),
                    Is.EqualTo(Set.singleton "feature-a"))
                Assert.That(TreemonConfig.readArchivedBranches root, Is.EqualTo([ "old" ]))
                Assert.That(TreemonConfig.readBaseBranch root, Is.EqualTo("develop"))
                Assert.That(custom, Is.True)
                Assert.That(
                    finalState.AutoSyncTriggeredRevisions |> Map.tryFind path,
                    Is.EqualTo(Some "base-b")))
        finally
            if Directory.Exists root then Directory.Delete(root, true)
