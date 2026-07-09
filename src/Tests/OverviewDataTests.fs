module Tests.OverviewDataTests

open NUnit.Framework
open Shared
open OverviewData
open Tests.WorktreeFixtures

/// Tests for the pure cross-worktree aggregation (OverviewData.aggregate), the data behind the
/// Overview band. It folds a RepoWorktrees list into: task buckets (Planned folds in Loose; Done
/// counts only non-archived worktrees; In-progress and Queued count only where the worktree has an
/// ACTIVE agent — Working or WaitingForUser — otherwise folding into the muted Unattended catch-all;
/// every other bucket sums across all), agent groups (red-dot WORKING worktrees grouped by
/// Activity.classify of their CurrentSkill, plus a distinct Waiting group for CodingTool =
/// WaitingForUser), and Scale (the largest bucket count). Empty buckets and groups are omitted; both
/// lists come back in canonical order (Unattended trails Done; Waiting sorts last).
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewDataTests() =

    let beads o ip b c : BeadsSummary = { Open = o; InProgress = ip; Blocked = b; Closed = c }
    let planning p q l : BeadsPlanning = { Planned = p; Queued = q; Loose = l }

    /// A worktree carrying beads/planning counts, INACTIVE (Idle, not archived): its In-progress and
    /// Queued fold into Unattended. Use activeTaskWt when those should count toward the live buckets.
    let taskWt bd pl = { baseWt with Beads = bd; Planning = pl }

    /// Like taskWt but in an ACTIVE red-dot Working state, so its In-progress/Queued count live.
    let activeTaskWt bd pl = { taskWt bd pl with CodingTool = CodingToolStatus.Working }

    /// A worktree in a given CodingTool state carrying an optional skill — for agent-group tests.
    /// Activity is derived only for red-dot (Working) worktrees; other states never contribute to
    /// the activity groups (WaitingForUser goes to its own group, Done/Idle are excluded).
    let agentWt tool skill = { baseWt with CodingTool = tool; CurrentSkill = skill }

    let workingWt skill = agentWt CodingToolStatus.Working skill

    let repo (wts: WorktreeStatus list) : RepoWorktrees =
        { RepoId = RepoId "r"
          RootFolderName = "root"
          Worktrees = wts
          IsReady = true
          Provider = None
          BaseBranch = "main" }

    /// Count in a task bucket, or None when the bucket was omitted (empty).
    let taskCount kind (o: Overview) =
        o.Tasks |> List.tryFind (fun t -> t.Kind = kind) |> Option.map _.Count

    /// Count in an agent group, or None when the group was omitted (empty).
    let agentCount kind (o: Overview) =
        o.Agents |> List.tryFind (fun g -> g.Kind = kind) |> Option.map _.Count

    /// Count in a red-dot activity group, or None when omitted.
    let activityCount act (o: Overview) =
        agentCount (AgentGroupKind.Activity act) o

    // ----- Task-bucket sums -----

    [<Test>]
    member _.``Task buckets sum every bucket across all repos and worktrees``() =
        let result =
            aggregate
                [ repo
                    [ activeTaskWt (beads 3 2 1 4) (planning 2 1 1)
                      activeTaskWt (beads 0 1 0 2) (planning 1 2 0) ]
                  repo [ activeTaskWt (beads 1 0 3 5) (planning 0 1 2) ] ]

        // Planned = Σ(Planning.Planned + Planning.Loose) = (2+1)+(1+0)+(0+2)
        Assert.That(taskCount TaskBucketKind.Planned result, Is.EqualTo(Some 6))
        // Queued = Σ Planning.Queued (active worktrees) = 1+2+1
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(Some 4))
        // InProgress = Σ Beads.InProgress (active worktrees) = 2+1+0
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 3))
        // Blocked = Σ Beads.Blocked = 1+0+3
        Assert.That(taskCount TaskBucketKind.Blocked result, Is.EqualTo(Some 4))
        // Done = Σ Beads.Closed = 4+2+5
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(Some 11))
        // Every worktree is active, so nothing is Unattended.
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(None))

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
        Assert.That(result.Agents, Is.Empty)
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
        // Both worktrees are active so In-progress/Queued stay in their live buckets (not Unattended).
        let result =
            aggregate
                [ repo
                    [ { activeTaskWt (beads 0 5 2 7) (planning 3 4 0) with IsArchived = false }
                      { activeTaskWt (beads 0 6 8 100) (planning 9 10 1) with IsArchived = true } ] ]
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
                    [ { activeTaskWt (beads 0 3 0 0) BeadsPlanning.zero with IsArchived = false }
                      { taskWt (beads 0 0 0 100) BeadsPlanning.zero with IsArchived = true } ] ]
        Assert.That(result.Scale, Is.EqualTo(3))                          // not 100
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(None))

    // ----- Empty categories omitted -----

    [<Test>]
    member _.``A bucket with a zero count is omitted, not rendered as a 0``() =
        // Only Done is non-zero: the other buckets must be absent from Tasks.
        let result = aggregate [ repo [ taskWt (beads 0 0 0 3) BeadsPlanning.zero ] ]
        Assert.That(result.Tasks |> List.length, Is.EqualTo(1))
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(Some 3))
        Assert.That(taskCount TaskBucketKind.Planned result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.Blocked result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(None))

    [<Test>]
    member _.``All-zero worktrees produce no task buckets``() =
        let result = aggregate [ repo [ taskWt BeadsSummary.zero BeadsPlanning.zero ] ]
        Assert.That(result.Tasks, Is.Empty)
        Assert.That(result.Scale, Is.EqualTo(0))

    [<Test>]
    member _.``Present task buckets keep canonical Planned-Queued-InProgress-Blocked-Done order``() =
        // Active worktree so In-progress/Queued stay live and all five ordered buckets are present.
        let result = aggregate [ repo [ activeTaskWt (beads 0 1 1 1) (planning 1 1 0) ] ]
        let kinds = result.Tasks |> List.map _.Kind
        Assert.That(
            kinds,
            Is.EqualTo(
                [ TaskBucketKind.Planned
                  TaskBucketKind.Queued
                  TaskBucketKind.InProgress
                  TaskBucketKind.Blocked
                  TaskBucketKind.Done ]))

    // ----- In-progress / Queued active-gating + Unattended catch-all -----

    [<Test>]
    member _.``In-progress and Queued on an inactive worktree fold into Unattended, not their live buckets``() =
        // Idle worktree: nobody is actively working, so its In-progress/Queued are likely stale beads
        // status and collapse into the single Unattended bucket.
        let result = aggregate [ repo [ taskWt (beads 0 3 0 0) (planning 0 2 0) ] ]
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(Some 5)) // 3 + 2

    [<Test>]
    member _.``A Working worktree keeps its In-progress and Queued live, nothing is Unattended``() =
        let result = aggregate [ repo [ activeTaskWt (beads 0 3 0 0) (planning 0 2 0) ] ]
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 3))
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(Some 2))
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(None))

    [<Test>]
    member _.``A WaitingForUser worktree also counts as active and keeps its In-progress and Queued live``() =
        // Decision: an agent parked on the user (yellow dot) still counts as actively worked.
        let wt =
            { taskWt (beads 0 4 0 0) (planning 0 1 0) with CodingTool = CodingToolStatus.WaitingForUser }
        let result = aggregate [ repo [ wt ] ]
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 4))
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(Some 1))
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(None))

    [<Test>]
    member _.``Done and Idle worktrees route their In-progress and Queued into Unattended``() =
        let result =
            aggregate
                [ repo
                    [ { taskWt (beads 0 2 0 0) (planning 0 3 0) with CodingTool = CodingToolStatus.Done }
                      { taskWt (beads 0 1 0 0) BeadsPlanning.zero with CodingTool = CodingToolStatus.Idle } ] ]
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(Some 6)) // (2+3)+(1+0)
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(None))
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(None))

    [<Test>]
    member _.``Unattended holds only inactive worktrees' work while active worktrees fill the live buckets``() =
        let result =
            aggregate
                [ repo
                    [ activeTaskWt (beads 0 5 0 0) (planning 0 2 0) // active -> live buckets
                      { taskWt (beads 0 4 0 0) (planning 0 3 0) with CodingTool = CodingToolStatus.Idle } ] ] // inactive -> Unattended
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 5)) // active only
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(Some 2))     // active only
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(Some 7)) // inactive 4+3

    [<Test>]
    member _.``Unattended trails Done in canonical order``() =
        let result =
            aggregate
                [ repo
                    [ activeTaskWt (beads 0 2 0 3) BeadsPlanning.zero // active: InProgress + Done
                      { taskWt (beads 0 4 0 0) BeadsPlanning.zero with CodingTool = CodingToolStatus.Idle } ] ] // Unattended
        let kinds = result.Tasks |> List.map _.Kind
        Assert.That(
            kinds,
            Is.EqualTo([ TaskBucketKind.InProgress; TaskBucketKind.Done; TaskBucketKind.Unattended ]))

    [<Test>]
    member _.``Unattended counts toward the shared Scale``() =
        // Idle worktree: InProgress 8 -> Unattended 8; Done 3. Scale tracks the largest bucket = 8.
        let result =
            aggregate [ repo [ { taskWt (beads 0 8 0 3) BeadsPlanning.zero with CodingTool = CodingToolStatus.Idle } ] ]
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(Some 8))
        Assert.That(result.Scale, Is.EqualTo(8))

    // ----- Scale (one true shared linear scale) -----

    [<Test>]
    member _.``Scale is the largest task-bucket count``() =
        // Buckets: Planned=2, Done=9 -> Scale = 9.
        let result = aggregate [ repo [ taskWt (beads 0 0 0 9) (planning 2 0 0) ] ]
        Assert.That(result.Scale, Is.EqualTo(9))

    [<Test>]
    member _.``Scale ignores agent groups - it is a task-only denominator``() =
        // Ten red-dot agents but the biggest task bucket is 4: Scale must track tasks, not agents.
        let agents = List.replicate 10 (workingWt (Some "investigate"))
        let tasks = taskWt (beads 0 4 0 3) BeadsPlanning.zero
        let result = aggregate [ repo (tasks :: agents) ]
        Assert.That(result.Scale, Is.EqualTo(4))

    // ----- Agent groups (red-dot working agents + a distinct Waiting group) -----

    [<Test>]
    member _.``Red-dot working worktrees group by the activity their skill classifies to``() =
        let result =
            aggregate
                [ repo
                    [ workingWt (Some "investigate")   // Investigating
                      workingWt (Some "bd-plan")       // Planning
                      workingWt (Some "bd-improve") ] ] // Planning (same group)
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 1))
        Assert.That(activityCount CurrentActivity.Planning result, Is.EqualTo(Some 2))

    [<Test>]
    member _.``Only red-dot (Working) worktrees count - HasActiveSession no longer counts``() =
        let result =
            aggregate
                [ repo
                    [ { workingWt (Some "investigate") with HasActiveSession = true }
                      { agentWt CodingToolStatus.Idle (Some "investigate") with HasActiveSession = true }
                      { agentWt CodingToolStatus.Done (Some "investigate") with HasActiveSession = true } ] ]
        // Only the red-dot worktree contributes; the two terminal-present-but-not-working ones don't.
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``Done and Idle worktrees are excluded from the activity groups even with a skill``() =
        // Done (blue) and Idle (grey) dots are finished/parked terminals — they never contribute to
        // an activity group, even though CurrentSkill may still be populated (last-seen skill).
        let result =
            aggregate
                [ repo
                    [ agentWt CodingToolStatus.Done (Some "investigate")
                      agentWt CodingToolStatus.Idle (Some "bd-plan") ] ]
        Assert.That(result.Agents, Is.Empty)

    [<Test>]
    member _.``A red-dot agent with no skill falls back to the Working group``() =
        let result = aggregate [ repo [ workingWt None ] ]
        Assert.That(activityCount CurrentActivity.Working result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``A red-dot agent running an unrecognized skill falls back to Working``() =
        let result =
            aggregate
                [ repo
                    [ workingWt (Some "totally-unknown-skill")
                      workingWt None ] ]
        // Both the unknown-skill agent and the no-skill agent land in Working.
        Assert.That(activityCount CurrentActivity.Working result, Is.EqualTo(Some 2))

    [<Test>]
    member _.``Activity classification goes through Activity.classify (slash command with args)``() =
        // A raw Claude slash command with an argument still classifies via the shared normalizer.
        let result = aggregate [ repo [ workingWt (Some "/pr https://example.com/pull/1") ] ]
        Assert.That(activityCount CurrentActivity.Reviewing result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``Activities with no red-dot agents are omitted``() =
        let result = aggregate [ repo [ workingWt (Some "investigate") ] ]
        Assert.That(result.Agents |> List.length, Is.EqualTo(1))
        Assert.That(activityCount CurrentActivity.Planning result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Executing result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Reviewing result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Fixing result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Working result, Is.EqualTo(None))
        Assert.That(agentCount AgentGroupKind.Waiting result, Is.EqualTo(None))

    [<Test>]
    member _.``No red-dot or waiting agents yields no agent groups``() =
        let result =
            aggregate [ repo [ agentWt CodingToolStatus.Idle (Some "investigate"); agentWt CodingToolStatus.Done None ] ]
        Assert.That(result.Agents, Is.Empty)

    [<Test>]
    member _.``WaitingForUser worktrees form a distinct Waiting group``() =
        let result =
            aggregate [ repo [ agentWt CodingToolStatus.WaitingForUser None
                               agentWt CodingToolStatus.WaitingForUser None ] ]
        Assert.That(agentCount AgentGroupKind.Waiting result, Is.EqualTo(Some 2))

    [<Test>]
    member _.``WaitingForUser goes to Waiting, not an activity group, even with a recognized skill``() =
        // A yellow-dot agent is parked on the user; its last skill must not classify it into an
        // activity — it belongs to the Waiting group regardless of CurrentSkill.
        let result = aggregate [ repo [ agentWt CodingToolStatus.WaitingForUser (Some "investigate") ] ]
        Assert.That(agentCount AgentGroupKind.Waiting result, Is.EqualTo(Some 1))
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(None))

    [<Test>]
    member _.``Working and Waiting are separate groups drawn from separate coding-tool states``() =
        let result =
            aggregate
                [ repo
                    [ workingWt (Some "investigate")                 // red dot -> Investigating
                      agentWt CodingToolStatus.WaitingForUser None ] ] // yellow dot -> Waiting
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 1))
        Assert.That(agentCount AgentGroupKind.Waiting result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``The Waiting group sorts after every activity group (canonical order)``() =
        let result =
            aggregate
                [ repo
                    [ agentWt CodingToolStatus.WaitingForUser None  // Waiting
                      workingWt (Some "bd-execute")                 // Executing
                      workingWt (Some "investigate") ] ]            // Investigating
        let order = result.Agents |> List.map _.Kind
        Assert.That(
            order,
            Is.EqualTo(
                [ AgentGroupKind.Activity CurrentActivity.Investigating
                  AgentGroupKind.Activity CurrentActivity.Executing
                  AgentGroupKind.Waiting ]))

    [<Test>]
    member _.``Present activity groups keep canonical order``() =
        // Skills chosen so Investigating, Executing and Working are present but Planning/Reviewing/
        // Fixing are absent — the survivors must still appear in canonical order.
        let result =
            aggregate
                [ repo
                    [ workingWt (Some "bd-execute")   // Executing
                      workingWt None                  // Working
                      workingWt (Some "investigate") ] ] // Investigating
        let order = result.Agents |> List.map _.Kind
        Assert.That(
            order,
            Is.EqualTo(
                [ AgentGroupKind.Activity CurrentActivity.Investigating
                  AgentGroupKind.Activity CurrentActivity.Executing
                  AgentGroupKind.Activity CurrentActivity.Working ]))

    [<Test>]
    member _.``Agent groups aggregate red-dot and waiting worktrees across repos``() =
        let result =
            aggregate
                [ repo [ workingWt (Some "investigate"); agentWt CodingToolStatus.WaitingForUser None ]
                  repo [ workingWt (Some "investigate"); workingWt (Some "fix-build") ] ]
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 2))
        Assert.That(activityCount CurrentActivity.Fixing result, Is.EqualTo(Some 1))
        Assert.That(agentCount AgentGroupKind.Waiting result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``A red-dot worktree contributes to both its task buckets and its activity group``() =
        // The task-fold and the agent-fold must both see the same worktree: task summation must not
        // skip working worktrees, and activity grouping must not depend on zero task counts.
        let wt =
            { baseWt with
                CodingTool = CodingToolStatus.Working
                Beads = beads 0 2 0 0
                CurrentSkill = Some "investigate" }
        let result = aggregate [ repo [ wt ] ]
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 2))
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 1))
