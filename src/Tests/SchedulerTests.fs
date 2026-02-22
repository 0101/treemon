module Tests.SchedulerTests

open System
open NUnit.Framework
open Server.GitWorktree
open Server.RefreshScheduler

let private testRepoId = "TestRepo"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PickMostOverdueTests() =

    [<Test>]
    member _.``Cold start with empty lastRuns returns first task``() =
        let tasks = [ RefreshWorktreeList testRepoId; RefreshPr testRepoId; RefreshGit(testRepoId, "/repo/a") ]
        let result = pickMostOverdue DateTimeOffset.UtcNow Map.empty tasks

        Assert.That(result.IsSome, Is.True)

    [<Test>]
    member _.``Cold start with empty lastRuns picks earliest deadline (all MinValue)``() =
        let tasks = [ RefreshWorktreeList testRepoId; RefreshPr testRepoId; RefreshGit(testRepoId, "/repo/a") ]
        let result = pickMostOverdue DateTimeOffset.UtcNow Map.empty tasks

        Assert.That(result, Is.EqualTo(Some(RefreshWorktreeList testRepoId)))

    [<Test>]
    member _.``All tasks just ran returns None``() =
        let now = DateTimeOffset.UtcNow
        let tasks = [ RefreshWorktreeList testRepoId; RefreshPr testRepoId; RefreshGit(testRepoId, "/repo/a") ]

        let lastRuns =
            tasks
            |> List.map (fun t -> t, now)
            |> Map.ofList

        let result = pickMostOverdue now lastRuns tasks

        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``One task overdue returns that task``() =
        let now = DateTimeOffset.UtcNow
        let longAgo = now.AddSeconds(-200.0)

        let tasks = [ RefreshWorktreeList testRepoId; RefreshPr testRepoId; RefreshGit(testRepoId, "/repo/a") ]

        let lastRuns =
            [ RefreshWorktreeList testRepoId, now
              RefreshPr testRepoId, longAgo
              RefreshGit(testRepoId, "/repo/a"), now ]
            |> Map.ofList

        let result = pickMostOverdue now lastRuns tasks

        Assert.That(result, Is.EqualTo(Some(RefreshPr testRepoId)))

    [<Test>]
    member _.``Multiple overdue returns most overdue (earliest deadline)``() =
        let now = DateTimeOffset.UtcNow
        let slightlyOverdue = now.AddSeconds(-20.0)
        let veryOverdue = now.AddSeconds(-200.0)

        let tasks = [ RefreshWorktreeList testRepoId; RefreshPr testRepoId; RefreshGit(testRepoId, "/repo/a") ]

        let lastRuns =
            [ RefreshWorktreeList testRepoId, slightlyOverdue
              RefreshPr testRepoId, veryOverdue
              RefreshGit(testRepoId, "/repo/a"), now ]
            |> Map.ofList

        let result = pickMostOverdue now lastRuns tasks

        Assert.That(result, Is.EqualTo(Some(RefreshPr testRepoId)))

    [<Test>]
    member _.``Empty task list returns None``() =
        let result = pickMostOverdue DateTimeOffset.UtcNow Map.empty []
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Tasks not in lastRuns are treated as maximally overdue``() =
        let now = DateTimeOffset.UtcNow

        let lastRuns =
            [ RefreshWorktreeList testRepoId, now ]
            |> Map.ofList

        let tasks = [ RefreshWorktreeList testRepoId; RefreshPr testRepoId ]

        let result = pickMostOverdue now lastRuns tasks

        Assert.That(result, Is.EqualTo(Some(RefreshPr testRepoId)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ComputeSleepMsTests() =

    [<Test>]
    member _.``Nothing due soon returns large sleep``() =
        let now = DateTimeOffset.UtcNow
        let tasks = [ RefreshWorktreeList testRepoId; RefreshPr testRepoId ]

        let lastRuns =
            [ RefreshWorktreeList testRepoId, now
              RefreshPr testRepoId, now ]
            |> Map.ofList

        let result = computeSleepMs now lastRuns tasks

        Assert.That(result, Is.GreaterThan(1000))

    [<Test>]
    member _.``Overdue task returns minimum 100ms``() =
        let now = DateTimeOffset.UtcNow
        let longAgo = now.AddSeconds(-200.0)
        let tasks = [ RefreshPr testRepoId ]

        let lastRuns =
            [ RefreshPr testRepoId, longAgo ]
            |> Map.ofList

        let result = computeSleepMs now lastRuns tasks

        Assert.That(result, Is.EqualTo(100))

    [<Test>]
    member _.``Returns correct ms until next task due``() =
        let now = DateTimeOffset.UtcNow
        let ranTenSecondsAgo = now.AddSeconds(-10.0)
        let tasks = [ RefreshGit(testRepoId, "/repo/a") ]

        let lastRuns =
            [ RefreshGit(testRepoId, "/repo/a"), ranTenSecondsAgo ]
            |> Map.ofList

        let result = computeSleepMs now lastRuns tasks

        Assert.That(result, Is.InRange(4000, 6000))

    [<Test>]
    member _.``Empty task list returns max int``() =
        let result = computeSleepMs DateTimeOffset.UtcNow Map.empty []
        Assert.That(result, Is.EqualTo(Int32.MaxValue))

    [<Test>]
    member _.``Multiple tasks returns sleep for soonest``() =
        let now = DateTimeOffset.UtcNow
        let tasks = [ RefreshGit(testRepoId, "/repo/a"); RefreshPr testRepoId ]

        let lastRuns =
            [ RefreshGit(testRepoId, "/repo/a"), now.AddSeconds(-10.0)
              RefreshPr testRepoId, now ]
            |> Map.ofList

        let result = computeSleepMs now lastRuns tasks

        Assert.That(result, Is.InRange(4000, 6000))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StateAgentTests() =

    let waitForAgent (agent: MailboxProcessor<StateMsg>) =
        agent.PostAndAsyncReply(GetState) |> Async.Ignore

    let getRepo (state: DashboardState) =
        state.Repos |> Map.find testRepoId

    [<Test>]
    member _.``UpdateWorktreeList then UpdateGit populates state``() =
        async {
            let agent = createAgent ()

            let worktrees =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" } ]

            agent.Post(UpdateWorktreeList(testRepoId, worktrees))
            do! waitForAgent agent

            let gitData : GitData =
                { Path = "/repo/main"
                  Branch = "main"
                  LastCommitMessage = "initial"
                  LastCommitTime = DateTimeOffset.UtcNow
                  UpstreamBranch = None
                  MainBehindCount = 0
                  IsDirty = false
                  WorkMetrics = None }

            agent.Post(UpdateGit(testRepoId, "/repo/main", gitData))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = getRepo state

            Assert.That(repo.WorktreeList.Length, Is.EqualTo(1))
            Assert.That(repo.WorktreeList.[0].Path, Is.EqualTo("/repo/main"))
            Assert.That(repo.GitData.ContainsKey("/repo/main"), Is.True)
            Assert.That(repo.GitData.["/repo/main"].Branch, Is.EqualTo("main"))
            Assert.That(repo.IsReady, Is.True)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``RemoveWorktree cleans up all maps``() =
        async {
            let agent = createAgent ()

            let worktrees =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" }
                  { Path = "/repo/feature"
                    Head = "def456"
                    Branch = Some "feature" } ]

            agent.Post(UpdateWorktreeList(testRepoId, worktrees))
            do! waitForAgent agent

            let gitData : GitData =
                { Path = "/repo/feature"
                  Branch = "feature"
                  LastCommitMessage = "wip"
                  LastCommitTime = DateTimeOffset.UtcNow
                  UpstreamBranch = None
                  MainBehindCount = 0
                  IsDirty = false
                  WorkMetrics = None }

            agent.Post(UpdateGit(testRepoId, "/repo/feature", gitData))

            let beads : Shared.BeadsSummary = { Open = 1; InProgress = 2; Closed = 3 }
            agent.Post(UpdateBeads(testRepoId, "/repo/feature", beads))
            do! waitForAgent agent

            agent.Post(RemoveWorktree(testRepoId, "/repo/feature"))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = getRepo state

            Assert.That(repo.WorktreeList.Length, Is.EqualTo(1))
            Assert.That(repo.WorktreeList.[0].Path, Is.EqualTo("/repo/main"))
            Assert.That(repo.GitData.ContainsKey("/repo/feature"), Is.False)
            Assert.That(repo.BeadsData.ContainsKey("/repo/feature"), Is.False)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Event ring buffer caps at 50``() =
        async {
            let agent = createAgent ()
            let baseTime = DateTimeOffset.UtcNow

            [ 1 .. 60 ]
            |> List.iter (fun i ->
                agent.Post(
                    LogSchedulerEvent
                        { Source = "Test"
                          Message = $"event-{i}"
                          Timestamp = baseTime.AddSeconds(float i)
                          Status = Some Shared.StepStatus.Succeeded
                          Duration = Some (TimeSpan.FromMilliseconds(100.0)) }))

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.SchedulerEvents.Length, Is.EqualTo(50))

            let messages = state.SchedulerEvents |> List.map (fun e -> e.Message)
            Assert.That(messages, Does.Contain("event-60"))
            Assert.That(messages, Does.Not.Contain("event-1"))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``UpdateGit for unknown worktree is ignored``() =
        async {
            let agent = createAgent ()

            let worktrees =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" } ]

            agent.Post(UpdateWorktreeList(testRepoId, worktrees))
            do! waitForAgent agent

            let gitData : GitData =
                { Path = "/repo/unknown"
                  Branch = "unknown"
                  LastCommitMessage = "nope"
                  LastCommitTime = DateTimeOffset.UtcNow
                  UpstreamBranch = None
                  MainBehindCount = 0
                  IsDirty = false
                  WorkMetrics = None }

            agent.Post(UpdateGit(testRepoId, "/repo/unknown", gitData))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = getRepo state

            Assert.That(repo.GitData.ContainsKey("/repo/unknown"), Is.False)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``UpdateBeads for unknown worktree is ignored``() =
        async {
            let agent = createAgent ()

            let worktrees =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" } ]

            agent.Post(UpdateWorktreeList(testRepoId, worktrees))
            do! waitForAgent agent

            let beads : Shared.BeadsSummary = { Open = 1; InProgress = 0; Closed = 0 }
            agent.Post(UpdateBeads(testRepoId, "/repo/unknown", beads))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = getRepo state

            Assert.That(repo.BeadsData.ContainsKey("/repo/unknown"), Is.False)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Initial state is empty and repos map is empty``() =
        async {
            let agent = createAgent ()
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.Repos, Is.Empty)
            Assert.That(state.SchedulerEvents, Is.Empty)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``UpdateWorktreeList auto-removes stale data for removed worktrees``() =
        async {
            let agent = createAgent ()

            let initial =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" }
                  { Path = "/repo/old"
                    Head = "def456"
                    Branch = Some "old" } ]

            agent.Post(UpdateWorktreeList(testRepoId, initial))
            do! waitForAgent agent

            let gitData : GitData =
                { Path = "/repo/old"
                  Branch = "old"
                  LastCommitMessage = "old"
                  LastCommitTime = DateTimeOffset.UtcNow
                  UpstreamBranch = None
                  MainBehindCount = 0
                  IsDirty = false
                  WorkMetrics = None }

            agent.Post(UpdateGit(testRepoId, "/repo/old", gitData))
            do! waitForAgent agent

            let updated =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" } ]

            agent.Post(UpdateWorktreeList(testRepoId, updated))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = getRepo state

            Assert.That(repo.WorktreeList.Length, Is.EqualTo(1))
            Assert.That(repo.GitData.ContainsKey("/repo/old"), Is.False)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Multiple repos are independently tracked``() =
        async {
            let agent = createAgent ()

            let repo1Worktrees =
                [ { WorktreeInfo.Path = "/repo1/main"
                    Head = "abc123"
                    Branch = Some "main" } ]

            let repo2Worktrees =
                [ { WorktreeInfo.Path = "/repo2/main"
                    Head = "def456"
                    Branch = Some "main" } ]

            agent.Post(UpdateWorktreeList("Repo1", repo1Worktrees))
            agent.Post(UpdateWorktreeList("Repo2", repo2Worktrees))

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.Repos.Count, Is.EqualTo(2))
            Assert.That(state.Repos.ContainsKey("Repo1"), Is.True)
            Assert.That(state.Repos.ContainsKey("Repo2"), Is.True)

            let r1 = state.Repos |> Map.find "Repo1"
            let r2 = state.Repos |> Map.find "Repo2"

            Assert.That(r1.WorktreeList.Length, Is.EqualTo(1))
            Assert.That(r1.WorktreeList.[0].Path, Is.EqualTo("/repo1/main"))
            Assert.That(r2.WorktreeList.Length, Is.EqualTo(1))
            Assert.That(r2.WorktreeList.[0].Path, Is.EqualTo("/repo2/main"))
        }
        |> Async.RunSynchronously


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LatestByCategoryTests() =

    let baseTime = DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)

    let makeEvent source message timestamp : Shared.CardEvent =
        { Source = source
          Message = message
          Timestamp = timestamp
          Status = Some Shared.StepStatus.Succeeded
          Duration = Some (TimeSpan.FromMilliseconds(100.0)) }

    [<Test>]
    member _.``Empty map plus first event for category contains that event``() =
        async {
            let agent = createAgent ()
            let event = makeEvent "GitRefresh" "main" baseTime

            agent.Post(LogSchedulerEvent event)
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.LatestByCategory.Count, Is.EqualTo(1))
            Assert.That(state.LatestByCategory.ContainsKey("GitRefresh"), Is.True)
            Assert.That(state.LatestByCategory.["GitRefresh"].Message, Is.EqualTo("main"))
            Assert.That(state.LatestByCategory.["GitRefresh"].Timestamp, Is.EqualTo(baseTime))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Newer event for same category replaces existing entry``() =
        async {
            let agent = createAgent ()
            let older = makeEvent "GitRefresh" "main" baseTime
            let newer = makeEvent "GitRefresh" "feature" (baseTime.AddSeconds(30.0))

            agent.Post(LogSchedulerEvent older)
            agent.Post(LogSchedulerEvent newer)
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.LatestByCategory.Count, Is.EqualTo(1))
            Assert.That(state.LatestByCategory.["GitRefresh"].Message, Is.EqualTo("feature"))
            Assert.That(state.LatestByCategory.["GitRefresh"].Timestamp, Is.EqualTo(baseTime.AddSeconds(30.0)))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Event for different category adds new entry without removing existing``() =
        async {
            let agent = createAgent ()
            let gitEvent = makeEvent "GitRefresh" "main" baseTime
            let prEvent = makeEvent "PrFetch" "fetched" (baseTime.AddSeconds(10.0))

            agent.Post(LogSchedulerEvent gitEvent)
            agent.Post(LogSchedulerEvent prEvent)
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.LatestByCategory.Count, Is.EqualTo(2))
            Assert.That(state.LatestByCategory.ContainsKey("GitRefresh"), Is.True)
            Assert.That(state.LatestByCategory.ContainsKey("PrFetch"), Is.True)
            Assert.That(state.LatestByCategory.["GitRefresh"].Message, Is.EqualTo("main"))
            Assert.That(state.LatestByCategory.["PrFetch"].Message, Is.EqualTo("fetched"))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Multiple categories updated independently``() =
        async {
            let agent = createAgent ()

            let events =
                [ makeEvent "GitRefresh" "main" baseTime
                  makeEvent "BeadsRefresh" "feature" (baseTime.AddSeconds(1.0))
                  makeEvent "ClaudeRefresh" "dev" (baseTime.AddSeconds(2.0))
                  makeEvent "PrFetch" "all" (baseTime.AddSeconds(3.0))
                  makeEvent "GitRefresh" "feature" (baseTime.AddSeconds(4.0))
                  makeEvent "BeadsRefresh" "main" (baseTime.AddSeconds(5.0)) ]

            events |> List.iter (fun e -> agent.Post(LogSchedulerEvent e))
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.LatestByCategory.Count, Is.EqualTo(4))
            Assert.That(state.LatestByCategory.["GitRefresh"].Message, Is.EqualTo("feature"),
                "GitRefresh should have the last posted event (feature, not main)")
            Assert.That(state.LatestByCategory.["BeadsRefresh"].Message, Is.EqualTo("main"),
                "BeadsRefresh should have the last posted event (main, not feature)")
            Assert.That(state.LatestByCategory.["ClaudeRefresh"].Message, Is.EqualTo("dev"))
            Assert.That(state.LatestByCategory.["PrFetch"].Message, Is.EqualTo("all"))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``LatestByCategory is empty in initial state``() =
        async {
            let agent = createAgent ()
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.LatestByCategory, Is.Empty)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``LatestByCategory is never trimmed unlike SchedulerEvents``() =

        async {
            let agent = createAgent ()

            [ 1 .. 60 ]
            |> List.iter (fun i ->
                agent.Post(
                    LogSchedulerEvent
                        (makeEvent $"Category{i}" $"event-{i}" (baseTime.AddSeconds(float i)))))

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.SchedulerEvents.Length, Is.EqualTo(50),
                "SchedulerEvents should be trimmed to 50")
            Assert.That(state.LatestByCategory.Count, Is.EqualTo(60),
                "LatestByCategory should never be trimmed, holding all 60 categories")
        }
        |> Async.RunSynchronously


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BuildTaskListTests() =

    let makeWorktree path branch : WorktreeInfo =
        { Path = path; Head = "abc123"; Branch = Some branch }

    let makeRepo worktrees : PerRepoState =
        { PerRepoState.empty with
            WorktreeList = worktrees
            KnownPaths = worktrees |> List.map (fun wt -> wt.Path) |> Set.ofList }

    [<Test>]
    member _.``All worktree-list tasks come before any per-worktree tasks``() =
        let repos =
            [ "Repo1", makeRepo [ makeWorktree "/r1/main" "main"; makeWorktree "/r1/feat" "feat" ]
              "Repo2", makeRepo [ makeWorktree "/r2/main" "main" ] ]
            |> Map.ofList

        let tasks = buildTaskList repos

        let isWorktreeList = function RefreshWorktreeList _ -> true | _ -> false
        let isPerWorktree = function RefreshGit _ | RefreshBeads _ | RefreshClaude _ -> true | _ -> false

        let lastWorktreeListIdx =
            tasks
            |> List.mapi (fun i t -> i, t)
            |> List.filter (fun (_, t) -> isWorktreeList t)
            |> List.map fst
            |> List.max

        let firstPerWorktreeIdx =
            tasks
            |> List.mapi (fun i t -> i, t)
            |> List.filter (fun (_, t) -> isPerWorktree t)
            |> List.map fst
            |> List.min

        Assert.That(lastWorktreeListIdx, Is.LessThan(firstPerWorktreeIdx),
            "All RefreshWorktreeList tasks must appear before any RefreshGit/Beads/Claude tasks")

    [<Test>]
    member _.``All local tasks come before any network tasks``() =
        let repos =
            [ "Repo1", makeRepo [ makeWorktree "/r1/main" "main" ]
              "Repo2", makeRepo [ makeWorktree "/r2/main" "main" ] ]
            |> Map.ofList

        let tasks = buildTaskList repos

        let isLocal = function RefreshGit _ | RefreshBeads _ | RefreshClaude _ -> true | _ -> false
        let isNetwork = function RefreshPr _ | RefreshFetch _ -> true | _ -> false

        let lastLocalIdx =
            tasks
            |> List.mapi (fun i t -> i, t)
            |> List.filter (fun (_, t) -> isLocal t)
            |> List.map fst
            |> List.max

        let firstNetworkIdx =
            tasks
            |> List.mapi (fun i t -> i, t)
            |> List.filter (fun (_, t) -> isNetwork t)
            |> List.map fst
            |> List.min

        Assert.That(lastLocalIdx, Is.LessThan(firstNetworkIdx),
            "All local tasks (Git/Beads/Claude) must appear before any network tasks (Pr/Fetch)")

    [<Test>]
    member _.``Contains expected task count``() =
        let repos =
            [ "Repo1", makeRepo [ makeWorktree "/r1/main" "main"; makeWorktree "/r1/feat" "feat" ]
              "Repo2", makeRepo [ makeWorktree "/r2/main" "main" ] ]
            |> Map.ofList

        let tasks = buildTaskList repos

        // 2 worktree lists + 3 worktrees * 3 task types + 2 repos * 2 network tasks = 2 + 9 + 4 = 15
        Assert.That(tasks.Length, Is.EqualTo(15))

    [<Test>]
    member _.``Local tasks are interleaved across repos not grouped by repo``() =
        let repos =
            [ "Repo1", makeRepo [ makeWorktree "/r1/main" "main" ]
              "Repo2", makeRepo [ makeWorktree "/r2/main" "main" ] ]
            |> Map.ofList

        let tasks = buildTaskList repos

        let localTasks =
            tasks
            |> List.filter (function RefreshGit _ | RefreshBeads _ | RefreshClaude _ -> true | _ -> false)

        let repoIds =
            localTasks
            |> List.map (function
                | RefreshGit(r, _) -> r
                | RefreshBeads(r, _) -> r
                | RefreshClaude(r, _) -> r
                | _ -> "")

        Assert.That(repoIds |> List.filter ((=) "Repo1") |> List.length, Is.EqualTo(3))
        Assert.That(repoIds |> List.filter ((=) "Repo2") |> List.length, Is.EqualTo(3))
