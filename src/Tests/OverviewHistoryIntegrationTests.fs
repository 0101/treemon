module Tests.OverviewHistoryIntegrationTests

open System
open System.IO
open NUnit.Framework
open Shared
open OverviewData
open Server
open Server.SessionActivity
open Server.SessionActivityStore

// Server-side INTEGRATION test for the unified Overview history read path (spec:
// docs/spec/overview-activity-history.md + docs/spec/session-status-push.md "Overview-history
// unification"). Drives the FULL read against a real temp SQLite store, exactly as
// WorktreeApi.getOverviewHistory does:
//
//   store (task_snapshots + activity_events + session_liveness)
//     ->  OverviewHistory.deriveAgents  ->  OverviewHistory.mergeHistory  ->  OverviewSnapshot list
//
// so the SQLite round-trip (task-snapshot JSON blobs + event rows) and the pure derivation/merge are
// proven together, end to end.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryIntegrationTests() =

    let baseTime = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)

    /// A fresh store over a throwaway temp .db, disposed and its dir deleted afterwards.
    let withStore (action: SessionActivityStore -> unit) =
        let dir = Path.Combine(Path.GetTempPath(), $"treemon-ovh-test-{Guid.NewGuid()}")
        Directory.CreateDirectory dir |> ignore
        let store = new SessionActivityStore(Path.Combine(dir, "activity.db"))

        try
            action store
        finally
            (store :> IDisposable).Dispose()

            try
                Directory.Delete(dir, true)
            with _ ->
                ()

    let evt sid wt status skill (minsBefore: float) : ActivityEventRow =
        { EventId = EventId(Guid.NewGuid().ToString())
          SessionId = SessionId sid
          WorktreePath = WorktreePath wt
          Provider = CopilotCli
          Kind = "x"
          Status = status
          Skill = skill
          Ts = baseTime.AddMinutes(-minsBefore) }

    let tc kind count : TaskCount = { Kind = kind; Count = count }
    let ac kind count : AgentCount = { Kind = kind; Count = count }

    /// Mirror of WorktreeApi.getOverviewHistory's read: store → derive → merge (the widened event
    /// query catches a session already running at the window's left edge).
    let readHistory (store: SessionActivityStore) (window: TimeSpan) : OverviewSnapshot list =
        let start = baseTime - window
        let taskSnaps =
            match store.QueryTaskSnapshotBefore start with
            | Some (_, tasks) -> (start, tasks) :: store.QueryTaskSnapshots(start, baseTime)
            | None -> store.QueryTaskSnapshots(start, baseTime)
        let events = store.QueryHistoryWindow(start, baseTime)
        let liveness = store.QueryLiveness(start - SessionActivity.openWindow, baseTime)
        let agentSnaps = OverviewHistory.deriveAgents baseTime window events liveness
        OverviewHistory.mergeHistory taskSnaps agentSnaps

    [<Test>]
    member _.``task snapshots round-trip through the store as count-only JSON``() =
        withStore (fun store ->
            let tasks = [ tc TaskBucketKind.Planned 4; tc TaskBucketKind.InProgress 2 ]
            store.AppendTaskSnapshot(baseTime.AddMinutes(-10.0), tasks)
            let read = store.QueryTaskSnapshots(baseTime - TimeSpan.FromHours 72.0, baseTime)
            Assert.That(read, Is.EqualTo [ baseTime.AddMinutes(-10.0), tasks ]))

    [<Test>]
    member _.``full read merges store Tasks with event-derived Agents into one stepped stream``() =
        withStore (fun store ->
            // Tasks snapshots (logged on change by the scheduler).
            store.AppendTaskSnapshot(baseTime.AddMinutes(-50.0), [ tc TaskBucketKind.Planned 2 ])
            store.AppendTaskSnapshot(baseTime.AddMinutes(-20.0), [ tc TaskBucketKind.Planned 5 ])
            // Raw push events (Agents derived from these on read).
            store.AppendEvent(evt "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") 40.0) |> ignore
            store.AppendEvent(evt "s1" "C:/wt/a" SessionLevelStatus.Idle None 10.0) |> ignore

            let history = readHistory store (TimeSpan.FromHours 72.0)

            let executing = ac (AgentGroupKind.Activity CurrentActivity.Executing) 1

            let expected =
                [ { Timestamp = baseTime - TimeSpan.FromHours 72.0; Tasks = []; Agents = [] }
                  { Timestamp = baseTime.AddMinutes(-50.0); Tasks = [ tc TaskBucketKind.Planned 2 ]; Agents = [] }
                  { Timestamp = baseTime.AddMinutes(-40.0); Tasks = [ tc TaskBucketKind.Planned 2 ]; Agents = [ executing ] }
                  { Timestamp = baseTime.AddMinutes(-40.0) + SessionActivity.openWindow; Tasks = [ tc TaskBucketKind.Planned 2 ]; Agents = [] }
                  { Timestamp = baseTime.AddMinutes(-20.0); Tasks = [ tc TaskBucketKind.Planned 5 ]; Agents = [] }
                  { Timestamp = baseTime.AddMinutes(-10.0); Tasks = [ tc TaskBucketKind.Planned 5 ]; Agents = [ ac AgentGroupKind.Idle 1 ] }
                  { Timestamp = baseTime.AddMinutes(-10.0) + SessionActivity.openWindow; Tasks = [ tc TaskBucketKind.Planned 5 ]; Agents = [] } ]

            Assert.That(history, Is.EqualTo expected))

    [<Test>]
    member _.``task history carries the snapshot active before the window to its left edge``() =
        withStore (fun store ->
            let window = TimeSpan.FromHours 72.0
            let tasks = [ tc TaskBucketKind.Planned 5 ]
            store.AppendTaskSnapshot(baseTime.AddHours(-96.0), tasks)

            let history = readHistory store window

            Assert.That(history.Head.Timestamp, Is.EqualTo(baseTime - window))
            Assert.That(history.Head.Tasks, Is.EqualTo tasks))

    [<Test>]
    member _.``pruneOld retains one old task snapshot as the future window baseline``() =
        withStore (fun store ->
            store.AppendTaskSnapshot(baseTime.AddDays(-90.0), [ tc TaskBucketKind.Planned 1 ])
            store.AppendTaskSnapshot(baseTime.AddMinutes(-5.0), [ tc TaskBucketKind.Planned 2 ])

            let deleted = store.PruneOld(baseTime.AddDays(-60.0))
            Assert.That(deleted, Is.EqualTo 0)

            let remaining = store.QueryTaskSnapshots(baseTime.AddDays(-120.0), baseTime)
            Assert.That(
                remaining,
                Is.EqualTo
                    [ baseTime.AddDays(-90.0), [ tc TaskBucketKind.Planned 1 ]
                      baseTime.AddMinutes(-5.0), [ tc TaskBucketKind.Planned 2 ] ]
            ))
