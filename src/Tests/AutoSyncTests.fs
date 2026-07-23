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
      WorkMetrics = None }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncPersistenceTests() =

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
    member _.``Dirty behind worktrees remain eligible and up-to-date worktrees do not``() =
        let dirtyBehind = gitData "/repo/wt" "feature" 2 (Some "base-a") true
        let upToDate = gitData "/repo/wt" "feature" 0 (Some "base-a") true

        Assert.Multiple(fun () ->
            Assert.That(revision true dirtyBehind, Is.EqualTo(Some "base-a"))
            Assert.That(revision true upToDate, Is.EqualTo None)
            Assert.That(revision false dirtyBehind, Is.EqualTo None))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncSchedulerDispatchTests() =

    [<Test>]
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

            Assert.Multiple(fun () ->
                Assert.That(schedulerStep.IsCompletedSuccessfully, Is.True)
                Assert.That(deliveryCompleted.Task.IsCompleted, Is.False))

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
        let mutable delivered = None

        let tryDeliver value =
            async {
                delivered <- Some value
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

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            let expected: SessionBridge.SendRequest =
                { WorktreePath = "/repo/wt"
                  SessionId = Some "session-a"
                  Prompt = SessionBridge.Prompt.agentPrompt "Sync with upstream/main." }
            Assert.That(
                delivered,
                Is.EqualTo(Some expected)))

    [<Test>]
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
    member _.``Selected session registering during grace receives prompt without fallback launch``() =
        // Callback probes cross async boundaries, so immutable values cannot capture their ordering.
        let mutable attempts = 0
        let mutable registrationCompleted = false

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
                        registrationCompleted <- true
                    })
                (fun _ -> failwith "fallback launch guard must not run")
                ignore
                (fun _ _ -> failwith "fallback launch must not run")
                request
            |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(attempts, Is.EqualTo(2)))

    [<Test>]
    member _.``Registration grace expiry launches exactly one prompted session and releases the guard``() =
        // Callback probes cross async boundaries, so immutable values cannot capture invocation counts.
        let mutable launched = None
        let mutable completed = []
        let mutable deliveryAttempts = 0
        let mutable launchAttempts = 0

        let launch path prompt =
            async {
                launchAttempts <- launchAttempts + 1
                launched <- Some(path, prompt)
                return Ok ()
            }

        let accepted =
            deliver
                (fun _ ->
                    async {
                        deliveryAttempts <- deliveryAttempts + 1
                        return SessionBridge.DeliveryResult.NoLiveSession
                    })
                (fun () -> async { return () })
                (fun _ -> async { return true })
                (fun path -> completed <- path :: completed)
                launch
                request
            |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(deliveryAttempts, Is.EqualTo(2))
            Assert.That(launchAttempts, Is.EqualTo(1))
            Assert.That(launched, Is.EqualTo(Some(WorktreePath "/repo/wt", "Sync with upstream/main.")))
            Assert.That(completed, Is.EqualTo([ "/repo/wt" ])))

    [<Test>]
    member _.``No selected session launches immediately without registration grace``() =
        // The async launch callback is the impure boundary under test.
        let mutable launchAttempts = 0

        let accepted =
            deliver
                (fun _ -> failwith "delivery must not run without a selected session")
                (fun () -> failwith "registration grace must not run without a selected session")
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
            Assert.That(launchAttempts, Is.EqualTo(1)))

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
    member _.``Transient bridge POST failure queues the prompt for retry on registration``() =
        let path = $"/test/auto-sync-retry/{Guid.NewGuid():N}"
        let sessionId = $"session-{Guid.NewGuid():N}"
        let port = TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        SessionBridge.registerSession path $"http://127.0.0.1:{port}/" (Some sessionId)

        let firstRequest = listener.GetContextAsync()
        let delivery =
            deliver
                SessionBridge.tryDeliver
                (fun () -> failwith "registration grace must not run")
                (fun _ -> failwith "fallback launch guard must not run")
                ignore
                (fun _ _ -> failwith "fallback launch must not run")
                { request with
                    WorktreePath = WorktreePath path
                    SessionId = Some sessionId }
            |> Async.StartAsTask

        let firstContext =
            firstRequest.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        firstContext.Response.StatusCode <- 503
        firstContext.Response.Close()

        Assert.That(delivery.GetAwaiter().GetResult(), Is.True)

        let retryRequest = listener.GetContextAsync()
        SessionBridge.registerSession path $"http://127.0.0.1:{port}/" (Some sessionId)

        let retryContext =
            retryRequest.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        use reader = new StreamReader(retryContext.Request.InputStream)
        let body = reader.ReadToEnd()
        retryContext.Response.StatusCode <- 200
        retryContext.Response.Close()

        Assert.That(
            body,
            Is.EqualTo(
                SessionBridge.serializePrompt(
                    SessionBridge.Prompt.agentPrompt request.Prompt)))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncEndpointTests() =

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
    member _.``Enabling an already-behind worktree persists and delivers immediately``() =
        let normalizedPath = PathUtils.normalizePath worktree
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
                [ root ]
                None
                "1.0"
                None

        let receive = listener.GetContextAsync()
        let toggleTask =
            api.toggleAutoSync (WorktreePath normalizedPath) true
            |> Async.StartAsTask

        let context = receive.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        use reader = new StreamReader(context.Request.InputStream)
        let body = reader.ReadToEnd()
        context.Response.StatusCode <- 200
        context.Response.Close()
        let result = toggleTask.GetAwaiter().GetResult()
        let dashboard = api.getWorktrees () |> Async.RunSynchronously
        let status = dashboard.Repos |> List.collect _.Worktrees |> List.exactlyOne

        Assert.Multiple(fun () ->
            Assert.That(Result.isOk result, Is.True)
            Assert.That(
                TreemonConfig.readAutoSyncBranchSet (Some root),
                Is.EqualTo(Set.singleton "feature-a"))
            Assert.That(
                body,
                Is.EqualTo(
                    SessionBridge.serializePrompt(
                    SessionBridge.Prompt.agentPrompt(prompt "origin" "main"))))
            Assert.That(status.AutoSyncEnabled, Is.True))

        let disableResult =
            api.toggleAutoSync (WorktreePath normalizedPath) false
            |> Async.RunSynchronously

        let state = agent.PostAndReply(GetState)

        Assert.Multiple(fun () ->
            Assert.That(Result.isOk disableResult, Is.True)
            Assert.That(TreemonConfig.readAutoSyncBranchSet (Some root), Is.Empty)
            Assert.That(Map.containsKey normalizedPath state.AutoSyncTriggeredRevisions, Is.False))
