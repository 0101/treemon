module Tests.CardEventLogTests

open System
open System.Threading
open NUnit.Framework
open Shared
open Server.CardEventLog

let private emptyState : CardEventLogState = { Events = Map.empty }

let private makeState (events: (string * CardEvent list) list) : CardEventLogState =
    { Events = events |> Map.ofList }

let private makeEvent source message status : CardEvent =
    { Source = source
      Message = message
      Timestamp = DateTimeOffset.UtcNow
      Status = Some status
      Duration = None }

let private branchEvents key (state: CardEventLogState) =
    state.Events |> Map.tryFind key |> Option.defaultValue []

let private isRunning (e: CardEvent) = e.Status = Some StepStatus.Running

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CardEventLogTests() =

    [<Test>]
    member _.``SyncStarted prepends a running sync marker``() =
        let newState = processMessage emptyState (SyncStarted "feature")

        let events = branchEvents "feature" newState
        Assert.That(events.Length, Is.EqualTo(1))
        Assert.That(events[0].Source, Is.EqualTo(EventSource.Sync))
        Assert.That(events[0].Status, Is.EqualTo(Some StepStatus.Running))

    [<Test>]
    member _.``SyncStarted preserves an unacknowledged post-fork terminal event``() =
        let postForkFailed = makeEvent EventSource.PostFork "setup" (StepStatus.Failed "install failed")
        let state = makeState [ "feature", [ postForkFailed ] ]

        let newState = processMessage state (SyncStarted "feature")

        let events = branchEvents "feature" newState
        Assert.That(
            events |> List.exists (fun e -> e.Source = EventSource.PostFork && e.Status = Some(StepStatus.Failed "install failed")),
            Is.True,
            "Post-fork failure event must survive a following SyncStarted")
        Assert.That(events[0].Source, Is.EqualTo(EventSource.Sync), "Fresh sync running event should be prepended")

    [<Test>]
    member _.``SyncStarted drops the previous run's sync history so the log stays bounded``() =
        // A completed prior sync run leaves several terminal step events behind.
        let priorRun =
            [ makeEvent "Push" "git push" StepStatus.Succeeded
              makeEvent "Merge" "git merge" StepStatus.Succeeded
              makeEvent EventSource.Sync "Sync starting" StepStatus.Running ]
        let postForkDone = makeEvent EventSource.PostFork "setup" StepStatus.Succeeded
        let state = makeState [ "feature", priorRun @ [ postForkDone ] ]

        let newState = processMessage state (SyncStarted "feature")

        let events = branchEvents "feature" newState
        Assert.That(
            events |> List.exists (fun e -> e.Source = "Push" || e.Source = "Merge"),
            Is.False,
            "Prior sync run's step history must not accumulate across syncs")
        Assert.That((events |> List.filter (fun e -> e.Source = EventSource.PostFork)).Length, Is.EqualTo(1), "Post-fork context is kept")
        Assert.That(events.Length, Is.EqualTo(2), "Only the new running marker plus the retained post-fork event")

    [<Test>]
    member _.``SyncStep prepends the event``() =
        let existing = makeEvent EventSource.Sync "first" StepStatus.Running
        let state = makeState [ "feature", [ existing ] ]

        let event = makeEvent "Pull" "git fetch" StepStatus.Succeeded
        let newState = processMessage state (SyncStep("feature", event))

        let events = branchEvents "feature" newState
        Assert.That(events.Length, Is.EqualTo(2))
        Assert.That(events[0].Message, Is.EqualTo("git fetch"), "New event should be prepended")

    [<Test>]
    member _.``SyncStep creates the branch entry if absent``() =
        let event = makeEvent "Pull" "git fetch" StepStatus.Running
        let newState = processMessage emptyState (SyncStep("feature", event))

        Assert.That((branchEvents "feature" newState).Length, Is.EqualTo(1))

    [<Test>]
    member _.``SyncEnded clears lingering sync running markers``() =
        let state = makeState [ "feature", [ makeEvent EventSource.Sync "syncing" StepStatus.Running ] ]

        let newState = processMessage state (SyncEnded "feature")

        Assert.That(branchEvents "feature" newState |> List.exists isRunning, Is.False)

    [<Test>]
    member _.``SyncEnded preserves a running post-fork event on the shared branch key``() =
        let syncRunning = makeEvent EventSource.Sync "syncing" StepStatus.Running
        let postForkRunning = makeEvent EventSource.PostFork "setup" StepStatus.Running
        let state = makeState [ "feature", [ syncRunning; postForkRunning ] ]

        let newState = processMessage state (SyncEnded "feature")

        let events = branchEvents "feature" newState
        Assert.That(
            events |> List.exists (fun e -> e.Source = EventSource.PostFork && isRunning e),
            Is.True,
            "A concurrent post-fork's running event must not be cleared by sync completion")
        Assert.That(events |> List.exists (fun e -> e.Source = EventSource.Sync && isRunning e), Is.False)

    [<Test>]
    member _.``SyncCancelled prepends a cancelled marker and clears sync running markers``() =
        let state = makeState [ "feature", [ makeEvent EventSource.Sync "syncing" StepStatus.Running ] ]

        let newState = processMessage state (SyncCancelled "feature")

        let events = branchEvents "feature" newState
        Assert.That(events[0].Status, Is.EqualTo(Some StepStatus.Cancelled))
        Assert.That(events |> List.exists isRunning, Is.False, "Running sync markers should be cleared")

    [<Test>]
    member _.``PostForkStarted appends a running event under the post-fork source``() =
        let newState = processMessage emptyState (PostForkStarted "feature")

        let events = branchEvents "feature" newState
        Assert.That(events.Length, Is.EqualTo(1))
        Assert.That(events[0].Source, Is.EqualTo(EventSource.PostFork))
        Assert.That(events[0].Status, Is.EqualTo(Some StepStatus.Running))

    [<Test>]
    member _.``PostForkEnded pushes a terminal event and clears the post-fork running event``() =
        let state = makeState [ "feature", [ makeEvent EventSource.PostFork "setup" StepStatus.Running ] ]

        let newState = processMessage state (PostForkEnded("feature", StepStatus.Succeeded))

        let events = branchEvents "feature" newState
        Assert.That(events |> List.exists isRunning, Is.False, "Running post-fork event should be cleared")
        Assert.That(events |> List.exists (fun e -> e.Status = Some StepStatus.Succeeded), Is.True)

    [<Test>]
    member _.``PostForkEnded records a failure status on the terminal event``() =
        let state = makeState [ "feature", [ makeEvent EventSource.PostFork "setup" StepStatus.Running ] ]

        let newState = processMessage state (PostForkEnded("feature", StepStatus.Failed "boom"))

        let events = branchEvents "feature" newState
        Assert.That(events |> List.exists (fun e -> e.Status = Some(StepStatus.Failed "boom")), Is.True)
        Assert.That(events |> List.exists isRunning, Is.False)

    [<Test>]
    member _.``PostForkEnded preserves a running sync event on the shared branch key``() =
        let syncRunning = makeEvent EventSource.Sync "syncing" StepStatus.Running
        let postForkRunning = makeEvent EventSource.PostFork "setup" StepStatus.Running
        let state = makeState [ "feature", [ syncRunning; postForkRunning ] ]

        let newState = processMessage state (PostForkEnded("feature", StepStatus.Succeeded))

        let events = branchEvents "feature" newState
        Assert.That(
            events |> List.exists (fun e -> e.Source = EventSource.Sync && isRunning e),
            Is.True,
            "A concurrent sync's running event must not be cleared by post-fork completion")
        Assert.That(events |> List.exists (fun e -> e.Source = EventSource.PostFork && isRunning e), Is.False)

    [<Test>]
    member _.``GetAll replies with the full events map via the running agent``() =
        let agent = createAgent ()
        agent.Post(PostForkStarted "feature")
        agent.Post(SyncStarted "main")

        let all = agent.PostAndAsyncReply(GetAll) |> Async.RunSynchronously

        Assert.That(all.ContainsKey("feature"), Is.True)
        Assert.That(all.ContainsKey("main"), Is.True)
