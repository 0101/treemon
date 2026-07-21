module Tests.OverviewHistoryTests

open System
open NUnit.Framework
open Shared
open OverviewData
open Server
open Server.SessionActivity
open Server.SessionActivityStore
open Tests.OverviewTestHelpers

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryTests() =

    let anchor = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
    let tc kind count : TaskCount = { Kind = kind; Count = count }
    let ac kind count : AgentCount = { Kind = kind; Count = count }

    let boundary (window: TimeSpan) bucket =
        let start = anchor - window
        start.AddTicks(window.Ticks * int64 bucket / int64 OverviewHistory.sampleBucketCount)

    [<Test>]
    member _.``tasksChanged writes the baseline and only later changes``() =
        let tasks = [ tc TaskBucketKind.Planned 1 ]
        Assert.Multiple(fun () ->
            Assert.That(OverviewHistory.tasksChanged None tasks, Is.True)
            Assert.That(OverviewHistory.tasksChanged (Some tasks) tasks, Is.False)
            Assert.That(OverviewHistory.tasksChanged (Some tasks) [ tc TaskBucketKind.Planned 2 ], Is.True))

    [<Test>]
    member _.``changes exactly at a boundary are applied before the complete sample is emitted``() =
        let window = HistoryWindow.duration HistoryWindow.Hours12
        let start = anchor - window
        let first = boundary window 1
        let tasksA = [ tc TaskBucketKind.Planned 1 ]
        let tasksB = [ tc TaskBucketKind.Planned 2 ]
        let executing = [ ac (AgentGroupKind.Activity CurrentActivity.Executing) 1 ]

        let history =
            OverviewHistory.sample
                anchor
                window
                [ start.AddHours(-1.0), tasksA; first, tasksB ]
                [ evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") first ]
                []

        Assert.That(
            history,
            Is.EqualTo
                [ { Timestamp = start; Tasks = tasksA; Agents = [] }
                  { Timestamp = first; Tasks = tasksB; Agents = executing }
                  { Timestamp = boundary window 3; Tasks = tasksB; Agents = [] } ]
        )

    [<Test>]
    member _.``a brief state wholly between 72 hour boundaries is omitted``() =
        let window = HistoryWindow.duration HistoryWindow.Hours72
        let start = anchor - window
        let appeared = start.AddMinutes 1.0

        let history =
            OverviewHistory.sample
                anchor
                window
                []
                [ evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") appeared ]
                []

        Assert.That(history, Is.EqualTo [ { Timestamp = start; Tasks = []; Agents = [] } ])

    [<Test>]
    member _.``sampling uses the whole state at the boundary rather than category maxima``() =
        let window = HistoryWindow.duration HistoryWindow.Hours72
        let start = anchor - window
        let first = boundary window 1
        let tasksAtStart = [ tc TaskBucketKind.Planned 1 ]
        let finalTasks = [ tc TaskBucketKind.Blocked 2 ]
        let waiting = [ ac AgentGroupKind.Waiting 1 ]

        let history =
            OverviewHistory.sample
                anchor
                window
                [ start.AddHours(-1.0), tasksAtStart
                  start.AddMinutes 1.0, [ tc TaskBucketKind.Planned 5 ]
                  start.AddMinutes 14.0, finalTasks ]
                [ evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") (start.AddMinutes 1.0)
                  evt "e2" "s1" "C:/wt/a" SessionLevelStatus.WaitingForUser None (start.AddMinutes 14.0) ]
                []

        Assert.That(
            history |> List.take 2,
            Is.EqualTo
                [ { Timestamp = start; Tasks = tasksAtStart; Agents = [] }
                  { Timestamp = first; Tasks = finalTasks; Agents = waiting } ]
        )

    [<Test>]
    member _.``left edge carries status and liveness without fabricating a transition``() =
        let window = HistoryWindow.duration HistoryWindow.Hours12
        let start = anchor - window
        let executing = [ ac (AgentGroupKind.Activity CurrentActivity.Executing) 1 ]

        let history =
            OverviewHistory.sample
                anchor
                window
                []
                [ evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") (start.AddMinutes(-10.0)) ]
                [ SessionId "s1", start.AddMinutes(-1.0) ]

        let livenessOnly =
            OverviewHistory.sample
                anchor
                window
                []
                []
                [ SessionId "unknown", start.AddMinutes(-1.0) ]

        Assert.Multiple(fun () ->
            Assert.That(
                history,
                Is.EqualTo
                    [ { Timestamp = start; Tasks = []; Agents = executing }
                      { Timestamp = boundary window 1; Tasks = []; Agents = [] } ]
            )
            Assert.That(livenessOnly, Is.EqualTo [ { Timestamp = start; Tasks = []; Agents = [] } ]))

    [<Test>]
    member _.``multiple open sessions in one worktree are counted independently``() =
        let window = HistoryWindow.duration HistoryWindow.Hours12
        let start = anchor - window

        let history =
            OverviewHistory.sample
                anchor
                window
                []
                [ evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Idle None (start.AddMinutes(-1.0))
                  evt "e2" "s2" "C:/wt/a" SessionLevelStatus.Working (Some "pr") (start.AddMinutes(-1.0)) ]
                []

        Assert.That(
            history.Head.Agents,
            Is.EqualTo
                [ ac (AgentGroupKind.Activity CurrentActivity.PR) 1
                  ac AgentGroupKind.Idle 1 ]
        )

    [<TestCase(12.0, 2.5)>]
    [<TestCase(24.0, 5.0)>]
    [<TestCase(72.0, 15.0)>]
    member _.``every supported window has 288 equal buckets and at most 289 samples``(hours: float, bucketMinutes: float) =
        let window = TimeSpan.FromHours hours
        let snapshots =
            [ 0 .. OverviewHistory.sampleBucketCount ]
            |> List.map (fun bucket ->
                boundary window bucket,
                [ tc TaskBucketKind.Planned (1 + bucket % 2) ])

        let history = OverviewHistory.sample anchor window snapshots [] []

        Assert.Multiple(fun () ->
            Assert.That(history.Length, Is.EqualTo(OverviewHistory.sampleBucketCount + 1))
            Assert.That(history.Head.Timestamp, Is.EqualTo(anchor - window))
            Assert.That(history |> List.last |> _.Timestamp, Is.EqualTo anchor)
            Assert.That(history[1].Timestamp - history[0].Timestamp, Is.EqualTo(TimeSpan.FromMinutes bucketMinutes)))

    [<Test>]
    member _.``dense raw history remains ordered in range and bounded to 289 samples``() =
        let window = HistoryWindow.duration HistoryWindow.Hours72
        let start = anchor - window
        let eventCount = 40000

        let events =
            [ 0 .. eventCount - 1 ]
            |> List.map (fun index ->
                let at = start.AddTicks(window.Ticks * int64 (index + 1) / int64 (eventCount + 1))
                let status, skill =
                    match index % 3 with
                    | 0 -> SessionLevelStatus.Working, Some "bd-execute"
                    | 1 -> SessionLevelStatus.WaitingForUser, None
                    | _ -> SessionLevelStatus.Idle, None

                evt $"e{index}" $"s{index % 130}" $"C:/wt/{index % 17}" status skill at)

        let liveness =
            events
            |> List.indexed
            |> List.choose (fun (index, row) ->
                if index % 4 = 0 then Some(row.SessionId, row.Ts.AddSeconds 10.0)
                else None)

        let tasks =
            [ 0 .. 999 ]
            |> List.map (fun index ->
                start.AddTicks(window.Ticks * int64 (index + 1) / 1001L),
                [ tc TaskBucketKind.Planned (1 + index % 7) ])

        let history = OverviewHistory.sample anchor window tasks events liveness

        Assert.Multiple(fun () ->
            Assert.That(history.Length, Is.LessThanOrEqualTo(OverviewHistory.sampleBucketCount + 1))
            Assert.That(history |> List.forall (fun snapshot -> snapshot.Timestamp >= start && snapshot.Timestamp <= anchor), Is.True)
            Assert.That(
                history
                |> List.pairwise
                |> List.forall (fun (left, right) -> left.Timestamp < right.Timestamp),
                Is.True
            ))
