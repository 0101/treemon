module Tests.CardEventLogTests

open System
open NUnit.Framework
open Shared
open Server.CardEventLog

let private emptyState : CardEventLogState = { Events = Map.empty }

let private makeEvent status : CardEvent =
    { Source = EventSource.PostFork
      Message = "setup"
      Timestamp = DateTimeOffset.UtcNow
      Status = Some status
      Duration = None }

let private branchEvents key (state: CardEventLogState) =
    state.Events |> Map.tryFind key |> Option.defaultValue []

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CardEventLogTests() =

    [<Test>]
    member _.``PostForkStarted adds a running event``() =
        let events =
            processMessage emptyState (PostForkStarted "feature")
            |> branchEvents "feature"

        Assert.That(events.Length, Is.EqualTo(1))
        Assert.That(events[0].Source, Is.EqualTo(EventSource.PostFork))
        Assert.That(events[0].Status, Is.EqualTo(Some StepStatus.Running))

    [<Test>]
    member _.``PostForkEnded replaces the running event with the terminal event``() =
        let state = { Events = Map.ofList [ "feature", [ makeEvent StepStatus.Running ] ] }

        let events =
            processMessage state (PostForkEnded("feature", StepStatus.Succeeded))
            |> branchEvents "feature"

        Assert.That(events.Length, Is.EqualTo(1))
        Assert.That(events[0].Status, Is.EqualTo(Some StepStatus.Succeeded))

    [<Test>]
    member _.``PostForkEnded records a failure``() =
        let state = { Events = Map.ofList [ "feature", [ makeEvent StepStatus.Running ] ] }

        let events =
            processMessage state (PostForkEnded("feature", StepStatus.Failed "boom"))
            |> branchEvents "feature"

        Assert.That(events.Length, Is.EqualTo(1))
        Assert.That(events[0].Status, Is.EqualTo(Some(StepStatus.Failed "boom")))

    [<Test>]
    member _.``New post-fork lifecycle removes the prior failure``() =
        let events =
            { Events = Map.ofList [ "feature", [ makeEvent (StepStatus.Failed "old failure") ] ] }
            |> fun state -> processMessage state (PostForkStarted "feature")
            |> fun state -> processMessage state (PostForkEnded("feature", StepStatus.Succeeded))
            |> branchEvents "feature"

        Assert.That(events |> List.map _.Status, Is.EqualTo [ Some StepStatus.Succeeded ])

    [<Test>]
    member _.``GetAll replies with post-fork events``() =
        let agent = createAgent ()
        agent.Post(PostForkStarted "feature")

        let all = agent.PostAndAsyncReply(GetAll) |> Async.RunSynchronously

        Assert.That(all.ContainsKey("feature"), Is.True)
