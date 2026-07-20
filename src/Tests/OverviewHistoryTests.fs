module Tests.OverviewHistoryTests

open System
open NUnit.Framework
open Shared
open OverviewData
open Server
open Server.SessionActivity
open Server.SessionActivityStore

// Unit tests for the repurposed OverviewHistory module (spec: docs/spec/overview-activity-history.md +
// docs/spec/session-status-push.md "Overview-history unification"). The history is now unified onto the
// push event store: Tasks are snapshot-based (change-detected by `tasksChanged`), Agents are DERIVED ON
// READ from the activity_events stream (`deriveAgents`, modelling openness decay), and the two series
// are stitched into one stepped OverviewSnapshot stream (`mergeHistory`). All three are pure.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryTests() =

    let baseTime = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)

    /// One activity_events row for session `sid` in worktree `wt`, `minsBefore` minutes before baseTime.
    /// Only Status/Skill/Ts/SessionId/WorktreePath bear on the agent reconstruction.
    let evt sid wt (status: SessionLevelStatus) skill (minsBefore: float) : ActivityEventRow =
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

    // --- tasksChanged -------------------------------------------------------------------------

    [<Test>]
    member _.``tasksChanged is true with no prior projection (baseline)``() =
        Assert.That(OverviewHistory.tasksChanged None [ tc TaskBucketKind.Planned 1 ], Is.True)

    [<Test>]
    member _.``tasksChanged is false for an identical projection``() =
        let p = [ tc TaskBucketKind.Planned 3; tc TaskBucketKind.InProgress 2 ]
        Assert.That(OverviewHistory.tasksChanged (Some p) p, Is.False)

    [<Test>]
    member _.``tasksChanged is true when a count differs``() =
        Assert.That(OverviewHistory.tasksChanged (Some [ tc TaskBucketKind.Planned 3 ]) [ tc TaskBucketKind.Planned 4 ], Is.True)

    // --- deriveAgents -------------------------------------------------------------------------

    [<Test>]
    member _.``deriveAgents surfaces a working agent, the idle transition, then openness decay``() =
        let window = TimeSpan.FromHours 72.0
        // s1: Working (bd-execute) at -60m, goes Idle at -30m, then stops emitting → drops out of the
        // counts one openWindow after its last event (openness decay), matching the live band.
        let events =
            [ evt "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") 60.0
              evt "s1" "C:/wt/a" SessionLevelStatus.Idle None 30.0 ]

        let derived = OverviewHistory.deriveAgents baseTime window events

        let expected =
            [ baseTime - window, []
              baseTime.AddMinutes(-60.0), [ ac (AgentGroupKind.Activity CurrentActivity.Executing) 1 ]
              baseTime.AddMinutes(-30.0), [ ac AgentGroupKind.Idle 1 ]
              baseTime.AddMinutes(-30.0) + SessionActivity.openWindow, [] ]

        Assert.That(derived, Is.EqualTo expected)

    [<Test>]
    member _.``deriveAgents skips heartbeats that do not change status or skill``() =
        let window = TimeSpan.FromHours 72.0
        // Three events, all Working/bd-execute (a status-preserving heartbeat cadence): the agent
        // count never moves, so only the appearance transition is emitted (no per-heartbeat churn).
        let events =
            [ evt "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") 40.0
              evt "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") 39.0
              evt "s1" "C:/wt/a" SessionLevelStatus.Working (Some "bd-execute") 38.0 ]

        let derived = OverviewHistory.deriveAgents baseTime window events

        let expected =
            [ baseTime - window, []
              baseTime.AddMinutes(-40.0), [ ac (AgentGroupKind.Activity CurrentActivity.Executing) 1 ]
              baseTime.AddMinutes(-38.0) + SessionActivity.openWindow, [] ]

        Assert.That(derived, Is.EqualTo expected)

    [<Test>]
    member _.``deriveAgents collapses two sessions in one worktree to the active winner``() =
        let window = TimeSpan.FromHours 72.0
        // Same worktree: one Working session, one Idle session, both fresh at -20m. The worktree
        // collapses (pickActive) to the active winner → one Working agent, not two. The "pr" skill
        // classifies to the PR activity (main #122 skill remap).
        let events =
            [ evt "s1" "C:/wt/a" SessionLevelStatus.Idle None 20.0
              evt "s2" "C:/wt/a" SessionLevelStatus.Working (Some "pr") 20.0 ]

        let derived = OverviewHistory.deriveAgents baseTime window events
        let atAppearance = derived |> List.find (fun (t, _) -> t = baseTime.AddMinutes(-20.0)) |> snd

        Assert.That(atAppearance, Is.EqualTo [ ac (AgentGroupKind.Activity CurrentActivity.PR) 1 ])

    [<Test>]
    member _.``deriveAgents returns a single empty baseline when there are no events``() =
        let derived = OverviewHistory.deriveAgents baseTime (TimeSpan.FromHours 72.0) []
        Assert.That(derived, Is.EqualTo [ baseTime - TimeSpan.FromHours 72.0, ([]: AgentCount list) ])

    // --- mergeHistory -------------------------------------------------------------------------

    [<Test>]
    member _.``mergeHistory carries each dimension forward at every change point``() =
        let t1 = baseTime.AddMinutes(-30.0)
        let t2 = baseTime.AddMinutes(-20.0)
        let t3 = baseTime.AddMinutes(-10.0)
        let tasksA = [ tc TaskBucketKind.Planned 2 ]
        let tasksB = [ tc TaskBucketKind.Planned 5 ]
        let agentsA = [ ac AgentGroupKind.Waiting 1 ]

        let merged = OverviewHistory.mergeHistory [ t1, tasksA; t3, tasksB ] [ t2, agentsA ]

        let expected =
            [ { Timestamp = t1; Tasks = tasksA; Agents = [] }
              { Timestamp = t2; Tasks = tasksA; Agents = agentsA }
              { Timestamp = t3; Tasks = tasksB; Agents = agentsA } ]

        Assert.That(merged, Is.EqualTo expected)

    [<Test>]
    member _.``mergeHistory collapses simultaneous task and agent changes into one snapshot``() =
        let t = baseTime.AddMinutes(-15.0)
        let tasks = [ tc TaskBucketKind.Blocked 1 ]
        let agents = [ ac AgentGroupKind.Idle 2 ]

        let merged = OverviewHistory.mergeHistory [ t, tasks ] [ t, agents ]

        Assert.That(merged, Is.EqualTo [ { Timestamp = t; Tasks = tasks; Agents = agents } ])
