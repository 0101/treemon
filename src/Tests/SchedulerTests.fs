module Tests.SchedulerTests

open System
open NUnit.Framework
open Server.GitWorktree
open Server.RefreshScheduler

[<TestFixture>]
[<Category("Unit")>]
type PickMostOverdueTests() =

    [<Test>]
    member _.``Cold start with empty lastRuns returns first task``() =
        let tasks = [ RefreshWorktreeList; RefreshPr; RefreshGit "/repo/a" ]
        let result = pickMostOverdue DateTimeOffset.UtcNow Map.empty tasks

        Assert.That(result.IsSome, Is.True)

    [<Test>]
    member _.``Cold start with empty lastRuns picks earliest deadline (all MinValue)``() =
        let tasks = [ RefreshWorktreeList; RefreshPr; RefreshGit "/repo/a" ]
        let result = pickMostOverdue DateTimeOffset.UtcNow Map.empty tasks

        Assert.That(result, Is.EqualTo(Some RefreshWorktreeList))

    [<Test>]
    member _.``All tasks just ran returns None``() =
        let now = DateTimeOffset.UtcNow
        let tasks = [ RefreshWorktreeList; RefreshPr; RefreshGit "/repo/a" ]

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

        let tasks = [ RefreshWorktreeList; RefreshPr; RefreshGit "/repo/a" ]

        let lastRuns =
            [ RefreshWorktreeList, now
              RefreshPr, longAgo
              RefreshGit "/repo/a", now ]
            |> Map.ofList

        let result = pickMostOverdue now lastRuns tasks

        Assert.That(result, Is.EqualTo(Some RefreshPr))

    [<Test>]
    member _.``Multiple overdue returns most overdue (earliest deadline)``() =
        let now = DateTimeOffset.UtcNow
        let slightlyOverdue = now.AddSeconds(-20.0)
        let veryOverdue = now.AddSeconds(-200.0)

        let tasks = [ RefreshWorktreeList; RefreshPr; RefreshGit "/repo/a" ]

        let lastRuns =
            [ RefreshWorktreeList, slightlyOverdue
              RefreshPr, veryOverdue
              RefreshGit "/repo/a", now ]
            |> Map.ofList

        let result = pickMostOverdue now lastRuns tasks

        Assert.That(result, Is.EqualTo(Some RefreshPr))

    [<Test>]
    member _.``Empty task list returns None``() =
        let result = pickMostOverdue DateTimeOffset.UtcNow Map.empty []
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Tasks not in lastRuns are treated as maximally overdue``() =
        let now = DateTimeOffset.UtcNow

        let lastRuns =
            [ RefreshWorktreeList, now ]
            |> Map.ofList

        let tasks = [ RefreshWorktreeList; RefreshPr ]

        let result = pickMostOverdue now lastRuns tasks

        Assert.That(result, Is.EqualTo(Some RefreshPr))


[<TestFixture>]
[<Category("Unit")>]
type ComputeSleepMsTests() =

    [<Test>]
    member _.``Nothing due soon returns large sleep``() =
        let now = DateTimeOffset.UtcNow
        let tasks = [ RefreshWorktreeList; RefreshPr ]

        let lastRuns =
            [ RefreshWorktreeList, now
              RefreshPr, now ]
            |> Map.ofList

        let result = computeSleepMs now lastRuns tasks

        Assert.That(result, Is.GreaterThan(1000))

    [<Test>]
    member _.``Overdue task returns minimum 100ms``() =
        let now = DateTimeOffset.UtcNow
        let longAgo = now.AddSeconds(-200.0)
        let tasks = [ RefreshPr ]

        let lastRuns =
            [ RefreshPr, longAgo ]
            |> Map.ofList

        let result = computeSleepMs now lastRuns tasks

        Assert.That(result, Is.EqualTo(100))

    [<Test>]
    member _.``Returns correct ms until next task due``() =
        let now = DateTimeOffset.UtcNow
        let ranTenSecondsAgo = now.AddSeconds(-10.0)
        let tasks = [ RefreshGit "/repo/a" ]

        let lastRuns =
            [ RefreshGit "/repo/a", ranTenSecondsAgo ]
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
        let tasks = [ RefreshGit "/repo/a"; RefreshPr ]

        let lastRuns =
            [ RefreshGit "/repo/a", now.AddSeconds(-10.0)
              RefreshPr, now ]
            |> Map.ofList

        let result = computeSleepMs now lastRuns tasks

        Assert.That(result, Is.InRange(4000, 6000))


[<TestFixture>]
[<Category("Unit")>]
type StateAgentTests() =

    let waitForAgent (agent: MailboxProcessor<StateMsg>) =
        async {
            let! _ = agent.PostAndAsyncReply(GetState)
            return ()
        }

    [<Test>]
    member _.``UpdateWorktreeList then UpdateGit populates state``() =
        async {
            let agent = createAgent ()

            let worktrees =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" } ]

            agent.Post(UpdateWorktreeList worktrees)
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

            agent.Post(UpdateGit("/repo/main", gitData))

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.WorktreeList.Length, Is.EqualTo(1))
            Assert.That(state.WorktreeList.[0].Path, Is.EqualTo("/repo/main"))
            Assert.That(state.GitData.ContainsKey("/repo/main"), Is.True)
            Assert.That(state.GitData.["/repo/main"].Branch, Is.EqualTo("main"))
            Assert.That(state.IsReady, Is.True)
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

            agent.Post(UpdateWorktreeList worktrees)
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

            agent.Post(UpdateGit("/repo/feature", gitData))

            let beads : Shared.BeadsSummary = { Open = 1; InProgress = 2; Closed = 3 }
            agent.Post(UpdateBeads("/repo/feature", beads))
            do! waitForAgent agent

            agent.Post(RemoveWorktree "/repo/feature")

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.WorktreeList.Length, Is.EqualTo(1))
            Assert.That(state.WorktreeList.[0].Path, Is.EqualTo("/repo/main"))
            Assert.That(state.GitData.ContainsKey("/repo/feature"), Is.False)
            Assert.That(state.BeadsData.ContainsKey("/repo/feature"), Is.False)
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

            agent.Post(UpdateWorktreeList worktrees)
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

            agent.Post(UpdateGit("/repo/unknown", gitData))

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.GitData.ContainsKey("/repo/unknown"), Is.False)
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

            agent.Post(UpdateWorktreeList worktrees)
            do! waitForAgent agent

            let beads : Shared.BeadsSummary = { Open = 1; InProgress = 0; Closed = 0 }
            agent.Post(UpdateBeads("/repo/unknown", beads))

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.BeadsData.ContainsKey("/repo/unknown"), Is.False)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Initial state is empty and not ready``() =
        async {
            let agent = createAgent ()
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.WorktreeList, Is.Empty)
            Assert.That(state.GitData, Is.Empty)
            Assert.That(state.BeadsData, Is.Empty)
            Assert.That(state.PrData, Is.Empty)
            Assert.That(state.SchedulerEvents, Is.Empty)
            Assert.That(state.IsReady, Is.False)
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

            agent.Post(UpdateWorktreeList initial)
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

            agent.Post(UpdateGit("/repo/old", gitData))
            do! waitForAgent agent

            let updated =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" } ]

            agent.Post(UpdateWorktreeList updated)

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(state.WorktreeList.Length, Is.EqualTo(1))
            Assert.That(state.GitData.ContainsKey("/repo/old"), Is.False)
        }
        |> Async.RunSynchronously
