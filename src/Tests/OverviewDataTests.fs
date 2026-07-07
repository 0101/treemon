module Tests.OverviewDataTests

open System
open NUnit.Framework
open Shared
open OverviewData

/// Tests for the pure cross-worktree aggregation (OverviewData.aggregate), the data behind the
/// Overview band. It folds a RepoWorktrees list into: task buckets (Planned folds in Loose; Done
/// counts only non-archived worktrees; every other bucket sums across all), activity groups (active
/// worktrees grouped by Activity.classify of their CurrentSkill), and Scale (the largest bucket
/// count). Empty buckets and activities are omitted; both lists come back in canonical order.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewDataTests() =

    let baseWt: WorktreeStatus =
        { Path = WorktreePath "/wt"
          Branch = "b"
          LastCommitMessage = "m"
          LastCommitTime = DateTimeOffset.UnixEpoch
          Beads = BeadsSummary.zero
          Planning = BeadsPlanning.zero
          CodingTool = CodingToolStatus.Idle
          CodingToolProvider = None
          CurrentSkill = None
          LastUserMessage = None
          Pr = PrStatus.NoPr
          MainBehindCount = 0
          IsDirty = false
          WorkMetrics = None
          HasActiveSession = false
          HasTestFailureLog = false
          IsMainWorktree = false
          IsArchived = false
          CanvasDocs = [] }

    let beads o ip b c : BeadsSummary = { Open = o; InProgress = ip; Blocked = b; Closed = c }
    let planning p q l : BeadsPlanning = { Planned = p; Queued = q; Loose = l }

    /// A worktree carrying beads/planning counts (inactive, not archived) — for task-bucket tests.
    let taskWt bd pl = { baseWt with Beads = bd; Planning = pl }

    /// An active/inactive agent worktree running a skill — for activity-group tests.
    let agentWt active skill = { baseWt with HasActiveSession = active; CurrentSkill = skill }

    let repo (wts: WorktreeStatus list) : RepoWorktrees =
        { RepoId = RepoId "r"
          RootFolderName = "root"
          Worktrees = wts
          IsReady = true
          Provider = None
          BaseBranch = "main" }

    /// Count in a task bucket, or None when the bucket was omitted (empty).
    let taskCount kind (o: Overview) =
        o.Tasks |> List.tryFind (fun t -> t.Kind = kind) |> Option.map (fun t -> t.Count)

    /// Count in an activity group, or None when the group was omitted (empty).
    let activityCount act (o: Overview) =
        o.Activities |> List.tryFind (fun g -> g.Activity = act) |> Option.map (fun g -> g.Count)

    // ----- Task-bucket sums -----

    [<Test>]
    member _.``Task buckets sum every bucket across all repos and worktrees``() =
        let result =
            aggregate
                [ repo
                    [ taskWt (beads 3 2 1 4) (planning 2 1 1)
                      taskWt (beads 0 1 0 2) (planning 1 2 0) ]
                  repo [ taskWt (beads 1 0 3 5) (planning 0 1 2) ] ]

        // Planned = Σ(Planning.Planned + Planning.Loose) = (2+1)+(1+0)+(0+2)
        Assert.That(taskCount TaskBucketKind.Planned result, Is.EqualTo(Some 6))
        // Queued = Σ Planning.Queued = 1+2+1
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(Some 4))
        // InProgress = Σ Beads.InProgress = 2+1+0
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 3))
        // Blocked = Σ Beads.Blocked = 1+0+3
        Assert.That(taskCount TaskBucketKind.Blocked result, Is.EqualTo(Some 4))
        // Done = Σ Beads.Closed = 4+2+5
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(Some 11))

    [<Test>]
    member _.``Planned folds Loose in on top of Planned``() =
        // Planned display bucket = Planning.Planned + Planning.Loose (decision #6); Loose is never a
        // bucket of its own.
        let result = aggregate [ repo [ taskWt BeadsSummary.zero (planning 4 0 3) ] ]
        Assert.That(taskCount TaskBucketKind.Planned result, Is.EqualTo(Some 7))

    [<Test>]
    member _.``An empty repo list yields an empty roll-up``() =
        let result = aggregate []
        Assert.That(result.Tasks, Is.Empty)
        Assert.That(result.Activities, Is.Empty)
        Assert.That(result.Scale, Is.EqualTo(0))

    // ----- Archived handling (only Done filters archived) -----

    [<Test>]
    member _.``Archived worktrees are excluded from Done``() =
        let result =
            aggregate
                [ repo
                    [ { taskWt (beads 0 0 0 7) BeadsPlanning.zero with IsArchived = false }
                      { taskWt (beads 0 0 0 100) BeadsPlanning.zero with IsArchived = true } ] ]
        // Only the non-archived worktree's Closed count reaches Done.
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(Some 7))

    [<Test>]
    member _.``Only Done filters archived - every other bucket still counts archived worktrees``() =
        // Locks decision #7: the archived filter is scoped to Done alone. An archived worktree's
        // open/in_progress/blocked/planned work still rolls up; only its Closed count is dropped.
        let result =
            aggregate
                [ repo
                    [ { taskWt (beads 0 5 2 7) (planning 3 4 0) with IsArchived = false }
                      { taskWt (beads 0 6 8 100) (planning 9 10 1) with IsArchived = true } ] ]
        Assert.That(taskCount TaskBucketKind.Planned result, Is.EqualTo(Some 13))    // (3+0)+(9+1)
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(Some 14))     // 4+10
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 11)) // 5+6
        Assert.That(taskCount TaskBucketKind.Blocked result, Is.EqualTo(Some 10))    // 2+8
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(Some 7))        // 7 only

    [<Test>]
    member _.``Scale is not inflated by an archived worktree's Closed count``() =
        // Scale is derived from the archived-filtered bucket counts, so a large archived Closed count
        // must neither raise Scale nor resurrect the (omitted, zero) Done bucket.
        let result =
            aggregate
                [ repo
                    [ { taskWt (beads 0 3 0 0) BeadsPlanning.zero with IsArchived = false }
                      { taskWt (beads 0 0 0 100) BeadsPlanning.zero with IsArchived = true } ] ]
        Assert.That(result.Scale, Is.EqualTo(3))                          // not 100
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(None))

    // ----- Empty categories omitted -----

    [<Test>]
    member _.``A bucket with a zero count is omitted, not rendered as a 0``() =
        // Only Done is non-zero: the other four buckets must be absent from Tasks.
        let result = aggregate [ repo [ taskWt (beads 0 0 0 3) BeadsPlanning.zero ] ]
        Assert.That(result.Tasks |> List.length, Is.EqualTo(1))
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(Some 3))
        Assert.That(taskCount TaskBucketKind.Planned result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.Blocked result, Is.EqualTo(None))

    [<Test>]
    member _.``All-zero worktrees produce no task buckets``() =
        let result = aggregate [ repo [ taskWt BeadsSummary.zero BeadsPlanning.zero ] ]
        Assert.That(result.Tasks, Is.Empty)
        Assert.That(result.Scale, Is.EqualTo(0))

    [<Test>]
    member _.``Present task buckets keep canonical Planned-Queued-InProgress-Blocked-Done order``() =
        let result = aggregate [ repo [ taskWt (beads 0 1 1 1) (planning 1 1 0) ] ]
        let kinds = result.Tasks |> List.map (fun t -> t.Kind)
        Assert.That(
            kinds,
            Is.EqualTo(
                [ TaskBucketKind.Planned
                  TaskBucketKind.Queued
                  TaskBucketKind.InProgress
                  TaskBucketKind.Blocked
                  TaskBucketKind.Done ]))

    // ----- Scale (one true shared linear scale) -----

    [<Test>]
    member _.``Scale is the largest task-bucket count``() =
        // Buckets: Planned=2, Done=9 -> Scale = 9.
        let result = aggregate [ repo [ taskWt (beads 0 0 0 9) (planning 2 0 0) ] ]
        Assert.That(result.Scale, Is.EqualTo(9))

    [<Test>]
    member _.``Scale ignores activity groups - it is a task-only denominator``() =
        // Ten active agents but the biggest task bucket is 4: Scale must track tasks, not agents.
        let agents = List.replicate 10 (agentWt true (Some "investigate"))
        let tasks = taskWt (beads 0 4 0 3) BeadsPlanning.zero
        let result = aggregate [ repo (tasks :: agents) ]
        Assert.That(result.Scale, Is.EqualTo(4))

    // ----- Activity groups -----

    [<Test>]
    member _.``Active worktrees group by the activity their skill classifies to``() =
        let result =
            aggregate
                [ repo
                    [ agentWt true (Some "investigate")   // Investigating
                      agentWt true (Some "bd-plan")       // Planning
                      agentWt true (Some "bd-improve") ] ] // Planning (same group)
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 1))
        Assert.That(activityCount CurrentActivity.Planning result, Is.EqualTo(Some 2))

    [<Test>]
    member _.``Inactive worktrees are excluded even when they carry a skill``() =
        // Only worktrees with a live session count toward activity — an idle card never contributes,
        // even though CurrentSkill may still be populated.
        let result =
            aggregate
                [ repo
                    [ agentWt true (Some "investigate")
                      agentWt false (Some "investigate") ] ]
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``An active agent with no skill falls back to the Working group``() =
        let result = aggregate [ repo [ agentWt true None ] ]
        Assert.That(activityCount CurrentActivity.Working result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``An active agent running an unrecognized skill falls back to Working``() =
        let result =
            aggregate
                [ repo
                    [ agentWt true (Some "totally-unknown-skill")
                      agentWt true None ] ]
        // Both the unknown-skill agent and the no-skill agent land in Working.
        Assert.That(activityCount CurrentActivity.Working result, Is.EqualTo(Some 2))

    [<Test>]
    member _.``Activity classification goes through Activity.classify (slash command with args)``() =
        // A raw Claude slash command with an argument still classifies via the shared normalizer.
        let result = aggregate [ repo [ agentWt true (Some "/pr https://example.com/pull/1") ] ]
        Assert.That(activityCount CurrentActivity.Reviewing result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``Activities with no active agents are omitted``() =
        let result = aggregate [ repo [ agentWt true (Some "investigate") ] ]
        Assert.That(result.Activities |> List.length, Is.EqualTo(1))
        Assert.That(activityCount CurrentActivity.Planning result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Executing result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Reviewing result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Fixing result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Working result, Is.EqualTo(None))

    [<Test>]
    member _.``No active agents yields no activity groups``() =
        let result =
            aggregate [ repo [ agentWt false (Some "investigate"); agentWt false None ] ]
        Assert.That(result.Activities, Is.Empty)

    [<Test>]
    member _.``Present activity groups keep canonical order``() =
        // Skills chosen so Investigating, Executing and Working are present but Planning/Reviewing/
        // Fixing are absent — the survivors must still appear in canonical order.
        let result =
            aggregate
                [ repo
                    [ agentWt true (Some "bd-execute")   // Executing
                      agentWt true None                  // Working
                      agentWt true (Some "investigate") ] ] // Investigating
        let order = result.Activities |> List.map (fun g -> g.Activity)
        Assert.That(
            order,
            Is.EqualTo([ CurrentActivity.Investigating; CurrentActivity.Executing; CurrentActivity.Working ]))

    [<Test>]
    member _.``Activity groups aggregate active worktrees across repos``() =
        let result =
            aggregate
                [ repo [ agentWt true (Some "investigate") ]
                  repo [ agentWt true (Some "investigate"); agentWt true (Some "fix-build") ] ]
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 2))
        Assert.That(activityCount CurrentActivity.Fixing result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``An active worktree contributes to both its task buckets and its activity group``() =
        // The task-fold and the activity-fold must both see the same worktree: task summation must
        // not skip active worktrees, and activity grouping must not depend on zero task counts.
        let wt =
            { baseWt with
                HasActiveSession = true
                Beads = beads 0 2 0 0
                CurrentSkill = Some "investigate" }
        let result = aggregate [ repo [ wt ] ]
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 2))
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 1))
