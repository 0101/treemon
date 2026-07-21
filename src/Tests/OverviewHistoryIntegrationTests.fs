module Tests.OverviewHistoryIntegrationTests

open System
open System.IO
open NUnit.Framework
open Shared
open OverviewData
open Server
open Server.SessionActivity
open Server.SessionActivityStore

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryIntegrationTests() =

    let anchor = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
    let tc kind count : TaskCount = { Kind = kind; Count = count }
    let ac kind count : AgentCount = { Kind = kind; Count = count }

    let withStore (action: SessionActivityStore -> unit) =
        let directory = Path.Combine(Path.GetTempPath(), $"treemon-overview-history-{Guid.NewGuid()}")
        Directory.CreateDirectory directory |> ignore
        let store = new SessionActivityStore(Path.Combine(directory, "activity.db"))

        try
            action store
        finally
            (store :> IDisposable).Dispose()

            try
                Directory.Delete(directory, true)
            with _ ->
                ()

    let withStores (action: SessionActivityStore -> SessionActivityStore -> unit) =
        let directory = Path.Combine(Path.GetTempPath(), $"treemon-overview-history-{Guid.NewGuid()}")
        Directory.CreateDirectory directory |> ignore
        let path = Path.Combine(directory, "activity.db")
        let reader = new SessionActivityStore(path)
        let writer = new SessionActivityStore(path)

        try
            action reader writer
        finally
            (writer :> IDisposable).Dispose()
            (reader :> IDisposable).Dispose()

            try
                Directory.Delete(directory, true)
            with _ ->
                ()

    let evt id sid worktree status skill at : ActivityEventRow =
        { EventId = EventId id
          SessionId = SessionId sid
          WorktreePath = WorktreePath worktree
          Provider = CopilotCli
          Kind = "status"
          Status = status
          Skill = skill
          Ts = at }

    let stored sid worktree status updatedAt lastSeen : StoredStatus =
        { SessionId = SessionId sid
          WorktreePath = WorktreePath worktree
          Provider = CopilotCli
          Status =
            { Status = status
              Skill = None
              Intent = None
              Title = None
              LastUserMessage = None
              LastAssistantMessage = None
              ContextUsage = None }
          UpdatedAt = updatedAt
          LastSeen = lastSeen
          ContextUsageAt = None }

    let readHistory store window =
        WorktreeApi.overviewHistoryAt store anchor window

    [<Test>]
    member _.``task snapshots round trip as count only JSON``() =
        withStore (fun store ->
            let tasks = [ tc TaskBucketKind.Planned 4; tc TaskBucketKind.InProgress 2 ]
            store.AppendTaskSnapshot(anchor.AddMinutes(-10.0), tasks)

            Assert.That(
                store.QueryTaskSnapshots(anchor.AddHours(-1.0), anchor),
                Is.EqualTo [ anchor.AddMinutes(-10.0), tasks ]
            ))

    [<Test>]
    member _.``API read samples store changes at boundaries and returns its supplied anchor``() =
        withStore (fun store ->
            let window = HistoryWindow.duration HistoryWindow.Hours12
            let start = anchor - window
            let first = start.AddMinutes 2.5
            let tasksA = [ tc TaskBucketKind.Planned 2 ]
            let tasksB = [ tc TaskBucketKind.Planned 5 ]
            let executing = [ ac (AgentGroupKind.Activity CurrentActivity.Executing) 1 ]

            store.AppendTaskSnapshot(start.AddHours(-1.0), tasksA)
            store.AppendTaskSnapshot(first, tasksB)
            store.AppendEvent(evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") first) |> ignore

            let response = readHistory store HistoryWindow.Hours12

            Assert.Multiple(fun () ->
                Assert.That(response.Anchor, Is.EqualTo anchor)
                Assert.That(
                    response.Snapshots,
                    Is.EqualTo
                        [ { Timestamp = start; Tasks = tasksA; Agents = [] }
                          { Timestamp = first; Tasks = tasksB; Agents = executing }
                          { Timestamp = start.AddMinutes 7.5; Tasks = tasksB; Agents = [] } ]
                )))

    [<Test>]
    member _.``API read carries task status and liveness lookbacks to the left edge``() =
        withStore (fun store ->
            let window = HistoryWindow.duration HistoryWindow.Hours12
            let start = anchor - window
            let tasks = [ tc TaskBucketKind.Blocked 3 ]

            store.AppendTaskSnapshot(start.AddDays(-1.0), tasks)
            store.AppendEvent(evt "e1" "s1" "C:/wt/a" SessionLevelStatus.Working (Some "pr") (start.AddMinutes(-10.0))) |> ignore
            store.AppendEvent(evt "irrelevant" "closed" "C:/wt/closed" SessionLevelStatus.Working None (start.AddDays(-1.0))) |> ignore
            store.RecordLiveness(SessionId "s1", start.AddMinutes(-1.0))

            let response = readHistory store HistoryWindow.Hours12

            Assert.Multiple(fun () ->
                Assert.That(
                    store.QueryHistoryWindow(start, anchor) |> List.map (_.EventId >> EventId.value),
                    Is.EqualTo [ "e1" ]
                )
                Assert.That(response.Snapshots.Head.Timestamp, Is.EqualTo start)
                Assert.That(response.Snapshots.Head.Tasks, Is.EqualTo tasks)
                Assert.That(
                    response.Snapshots.Head.Agents,
                    Is.EqualTo [ ac (AgentGroupKind.Activity CurrentActivity.PR) 1 ]
                )
                Assert.That(response.Snapshots |> List.forall (fun snapshot -> snapshot.Timestamp >= start), Is.True)))

    [<Test>]
    member _.``history inputs stay on one snapshot when liveness commits after the status read``() =
        withStores (fun reader writer ->
            let window = HistoryWindow.duration HistoryWindow.Hours12
            let start = anchor - window
            let sessionId = SessionId "interleaved"
            let baselineAt = start - SessionActivity.openWindow - TimeSpan.FromMinutes 1.0
            let livenessAt = start.AddMinutes(-1.0)

            writer.AppendEvent(
                evt
                    "baseline"
                    (SessionId.value sessionId)
                    "C:/wt/interleaved"
                    SessionLevelStatus.Working
                    (Some "bd-execute")
                    baselineAt
            )
            |> ignore

            let inputs =
                reader.QueryOverviewHistoryInputs(
                    start,
                    anchor,
                    beforeLivenessRead = (fun () -> writer.RecordLiveness(sessionId, livenessAt))
                )

            let response: OverviewHistoryResponse =
                { Anchor = anchor
                  Snapshots =
                    OverviewHistory.sample
                        anchor
                        window
                        inputs.TaskSnapshots
                        inputs.Events
                        inputs.Liveness }

            Assert.Multiple(fun () ->
                Assert.That(inputs.Events, Is.Empty)
                Assert.That(inputs.Liveness, Is.Empty)
                Assert.That(
                    writer.QueryLiveness(start - SessionActivity.openWindow, anchor),
                    Is.EqualTo [ sessionId, livenessAt ]
                )
                Assert.That(
                    response.Snapshots,
                    Is.EqualTo [ { Timestamp = start; Tasks = []; Agents = [] } ]
                )))

    [<Test>]
    member _.``API read honors all supported windows and the hard output bound``() =
        withStore (fun store ->
            store.AppendTaskSnapshot(anchor.AddDays(-4.0), [ tc TaskBucketKind.Planned 1 ])

            [ HistoryWindow.Hours12; HistoryWindow.Hours24; HistoryWindow.Hours72 ]
            |> List.iter (fun window ->
                let response = readHistory store window
                let start = anchor - HistoryWindow.duration window

                Assert.Multiple(fun () ->
                    Assert.That(response.Snapshots.Length, Is.LessThanOrEqualTo(OverviewHistory.sampleBucketCount + 1))
                    Assert.That(response.Snapshots.Head.Timestamp, Is.EqualTo start)
                    Assert.That(response.Snapshots |> List.forall (fun snapshot -> snapshot.Timestamp <= anchor), Is.True))))

    [<Test>]
    member _.``task snapshot capture appends only changes``() =
        withStore (fun store ->
            let first = [ tc TaskBucketKind.Planned 1 ]
            let second = [ tc TaskBucketKind.Planned 2 ]

            Assert.Multiple(fun () ->
                Assert.That(store.AppendTaskSnapshotIfChanged(anchor.AddMinutes(-3.0), first), Is.True)
                Assert.That(store.AppendTaskSnapshotIfChanged(anchor.AddMinutes(-2.0), first), Is.False)
                Assert.That(store.AppendTaskSnapshotIfChanged(anchor.AddMinutes(-1.0), second), Is.True)
                Assert.That(store.QueryTaskSnapshots(anchor.AddHours(-1.0), anchor).Length, Is.EqualTo 2)))

    [<Test>]
    member _.``pruning retains one status baseline per retained session and one task baseline``() =
        withStore (fun store ->
            let cutoff = anchor.AddDays(-60.0)
            let old1 = anchor.AddDays(-90.0)
            let old2 = anchor.AddDays(-80.0)
            let recent = anchor.AddDays(-1.0)

            [ evt "s1-old" "s1" "C:/wt/a" SessionLevelStatus.Idle None old1
              evt "s1-base" "s1" "C:/wt/a" SessionLevelStatus.Working None old2
              evt "s2-old" "s2" "C:/wt/b" SessionLevelStatus.Working None old1
              evt "s2-base" "s2" "C:/wt/b" SessionLevelStatus.WaitingForUser None old2
              evt "expired" "s3" "C:/wt/c" SessionLevelStatus.Working None old2 ]
            |> List.iter (store.AppendEvent >> ignore)

            store.UpsertStatus(stored "s1" "C:/wt/a" SessionLevelStatus.Working old2 recent)
            store.UpsertStatus(stored "s2" "C:/wt/b" SessionLevelStatus.WaitingForUser old2 recent)
            store.UpsertStatus(stored "s3" "C:/wt/c" SessionLevelStatus.Working old2 old2)
            store.RecordLiveness(SessionId "s1", recent)
            store.RecordLiveness(SessionId "s2", recent)

            store.AppendTaskSnapshot(old1, [ tc TaskBucketKind.Planned 1 ])
            store.AppendTaskSnapshot(old2, [ tc TaskBucketKind.Planned 2 ])
            store.AppendTaskSnapshot(recent, [ tc TaskBucketKind.Planned 3 ])

            store.PruneOld cutoff |> ignore

            let retainedEvents =
                store.QueryWindow(anchor.AddDays(-120.0), anchor)
                |> List.map (_.EventId >> EventId.value)

            Assert.Multiple(fun () ->
                Assert.That(retainedEvents, Is.EqualTo [ "s1-base"; "s2-base" ])
                Assert.That(
                    store.QueryTaskSnapshots(anchor.AddDays(-120.0), anchor),
                    Is.EqualTo
                        [ old2, [ tc TaskBucketKind.Planned 2 ]
                          recent, [ tc TaskBucketKind.Planned 3 ] ]
                )
                Assert.That(
                    store.QueryHistoryWindow(anchor.AddHours(-72.0), anchor)
                    |> List.map (_.EventId >> EventId.value),
                    Is.EqualTo [ "s1-base"; "s2-base" ]
                )))
