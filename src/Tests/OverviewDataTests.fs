module Tests.OverviewDataTests

open NUnit.Framework
open Shared
open OverviewData
open Tests.WorktreeFixtures

/// Tests for the pure cross-worktree aggregation (OverviewData.aggregate), the data behind the
/// Overview band. It folds a RepoWorktrees list into: task buckets (Planned folds in Loose;
/// In-progress and Queued count only where the worktree has an ACTIVE agent — Working or
/// WaitingForUser — otherwise folding into the muted Unattended catch-all; every other bucket sums
/// across all), agent groups (red-dot WORKING worktrees grouped by Activity.classify of their
/// CurrentSkill, plus a distinct Waiting group for CodingTool = WaitingForUser), and Scale (the
/// largest bucket count). Archived worktrees are excluded from the whole roll-up (every task bucket
/// and every agent group). Empty buckets and groups are omitted; both lists come back in canonical
/// order (Unattended trails Done; the Idle group sorts last).
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
    /// the activity groups (WaitingForUser goes to its own group, Idle goes to its own group, and
    /// NoSession is excluded).
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

    /// Members of a task bucket, or [] when the bucket was omitted.
    let taskMembers kind (o: Overview) =
        o.Tasks |> List.tryFind (fun t -> t.Kind = kind) |> Option.map _.Members |> Option.defaultValue []

    /// Members of an agent group, or [] when the group was omitted.
    let agentMembers kind (o: Overview) =
        o.Agents |> List.tryFind (fun g -> g.Kind = kind) |> Option.map _.Members |> Option.defaultValue []

    /// A repo with a distinct name (RepoId + RootFolderName), for repo-membership tests.
    let namedRepo name (wts: WorktreeStatus list) : RepoWorktrees =
        { RepoId = RepoId name
          RootFolderName = name
          Worktrees = wts
          IsReady = true
          Provider = None
          BaseBranch = "main" }

    /// A repo with a stable RepoId decoupled from its display RootFolderName — for the drill-down's
    /// repo-identity tests, where two DISTINCT repos can legitimately share a folder name.
    let idNamedRepo id name (wts: WorktreeStatus list) : RepoWorktrees =
        { RepoId = RepoId id
          RootFolderName = name
          Worktrees = wts
          IsReady = true
          Provider = None
          BaseBranch = "main" }

    /// Give a worktree a distinct path (the focus/ScopedKey) and branch.
    let at path branch (wt: WorktreeStatus) = { wt with Path = WorktreePath path; Branch = branch }

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
    member _.``Archived worktrees are excluded from every task bucket``() =
        // Archiving removes a worktree from the whole roll-up: none of its task counts contribute,
        // not just its Closed count. Both worktrees are active, so the non-archived one's
        // In-progress/Queued stay in their live buckets (not Unattended).
        let result =
            aggregate
                [ repo
                    [ { activeTaskWt (beads 0 5 2 7) (planning 3 4 0) with IsArchived = false }
                      { activeTaskWt (beads 0 6 8 100) (planning 9 10 1) with IsArchived = true } ] ]
        Assert.That(taskCount TaskBucketKind.Planned result, Is.EqualTo(Some 3))     // 3+0 (archived 9+1 dropped)
        Assert.That(taskCount TaskBucketKind.Queued result, Is.EqualTo(Some 4))      // 4 (archived 10 dropped)
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 5))  // 5 (archived 6 dropped)
        Assert.That(taskCount TaskBucketKind.Blocked result, Is.EqualTo(Some 2))     // 2 (archived 8 dropped)
        Assert.That(taskCount TaskBucketKind.Done result, Is.EqualTo(Some 7))        // 7 (archived 100 dropped)
        Assert.That(taskCount TaskBucketKind.Unattended result, Is.EqualTo(None))

    [<Test>]
    member _.``Archived worktrees are excluded from agent groups``() =
        // An archived worktree in a Working or WaitingForUser state must not surface as an active
        // agent — archiving removes it from the agent lens as well as the task buckets.
        let result =
            aggregate
                [ repo
                    [ { workingWt (Some "investigate") with IsArchived = true }
                      { agentWt CodingToolStatus.WaitingForUser None with IsArchived = true } ] ]
        Assert.That(result.Agents, Is.Empty)

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
    member _.``Idle and NoSession worktrees route their In-progress and Queued into Unattended``() =
        let result =
            aggregate
                [ repo
                    [ { taskWt (beads 0 2 0 0) (planning 0 3 0) with CodingTool = CodingToolStatus.NoSession }
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
                      { agentWt CodingToolStatus.NoSession (Some "investigate") with HasActiveSession = true } ] ]
        // Only the red-dot worktree contributes; the two terminal-present-but-not-working ones don't.
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``Idle and NoSession worktrees are excluded from the activity groups even with a skill``() =
        // Idle (blue) and NoSession (grey) dots never contribute to an ACTIVITY group, even though
        // CurrentSkill may still be populated (last-seen skill). Idle forms the distinct Idle
        // group; NoSession contributes to nothing.
        let result =
            aggregate
                [ repo
                    [ agentWt CodingToolStatus.Idle (Some "investigate")
                      agentWt CodingToolStatus.NoSession (Some "bd-plan") ] ]
        Assert.That(activityCount CurrentActivity.Investigating result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Planning result, Is.EqualTo(None))
        Assert.That(agentCount AgentGroupKind.Idle result, Is.EqualTo(Some 1))

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
        Assert.That(activityCount CurrentActivity.PR result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``Activities with no red-dot agents are omitted``() =
        let result = aggregate [ repo [ workingWt (Some "investigate") ] ]
        Assert.That(result.Agents |> List.length, Is.EqualTo(1))
        Assert.That(activityCount CurrentActivity.Planning result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Executing result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Reviewing result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.PR result, Is.EqualTo(None))
        Assert.That(activityCount CurrentActivity.Working result, Is.EqualTo(None))
        Assert.That(agentCount AgentGroupKind.Waiting result, Is.EqualTo(None))

    [<Test>]
    member _.``Only NoSession worktrees yield no agent groups``() =
        // NoSession (grey) is the sole terminal excluded from every agent group — including Idle.
        let result =
            aggregate [ repo [ agentWt CodingToolStatus.NoSession (Some "investigate"); agentWt CodingToolStatus.NoSession None ] ]
        Assert.That(result.Agents, Is.Empty)

    [<Test>]
    member _.``Idle worktrees form a distinct Idle group``() =
        let result =
            aggregate [ repo [ agentWt CodingToolStatus.Idle None
                               agentWt CodingToolStatus.Idle (Some "investigate") ] ]
        Assert.That(agentCount AgentGroupKind.Idle result, Is.EqualTo(Some 2))

    [<Test>]
    member _.``NoSession worktrees never join the Idle group``() =
        // Idle is blue-dot open-but-idle only; grey NoSession stays out even alongside an Idle worktree.
        let result =
            aggregate [ repo [ agentWt CodingToolStatus.Idle None; agentWt CodingToolStatus.NoSession None ] ]
        Assert.That(agentCount AgentGroupKind.Idle result, Is.EqualTo(Some 1))

    [<Test>]
    member _.``The Idle group sorts after the Waiting group (canonical order)``() =
        let result =
            aggregate
                [ repo
                    [ agentWt CodingToolStatus.Idle None             // Idle
                      agentWt CodingToolStatus.WaitingForUser None   // Waiting
                      workingWt (Some "investigate") ] ]            // Investigating
        let order = result.Agents |> List.map _.Kind
        Assert.That(
            order,
            Is.EqualTo(
                [ AgentGroupKind.Activity CurrentActivity.Investigating
                  AgentGroupKind.Waiting
                  AgentGroupKind.Idle ]))

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
        // PR are absent — the survivors must still appear in canonical order.
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
        Assert.That(activityCount CurrentActivity.PR result, Is.EqualTo(Some 1))
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

    // ----- Group membership (drill-down): the worktrees behind each aggregate -----

    [<Test>]
    member _.``Each agent group carries one member per worktree with ScopedKey, Branch, RepoName and Contribution 1``() =
        let result =
            aggregate
                [ namedRepo "alpha" [ at "/wt/a1" "feat-a1" (workingWt (Some "investigate")) ] ]
        let members = agentMembers (AgentGroupKind.Activity CurrentActivity.Investigating) result
        Assert.That(members |> List.length, Is.EqualTo(1))
        let m = List.head members
        Assert.That(m.ScopedKey, Is.EqualTo("/wt/a1"))
        Assert.That(m.Branch, Is.EqualTo("feat-a1"))
        Assert.That(m.RepoName, Is.EqualTo("alpha"))
        Assert.That(m.Contribution, Is.EqualTo(1))

    [<Test>]
    member _.``Agent group Count equals its Members length``() =
        let result =
            aggregate
                [ repo
                    [ at "/wt/1" "b1" (workingWt (Some "investigate"))
                      at "/wt/2" "b2" (workingWt (Some "investigate"))
                      at "/wt/3" "b3" (workingWt (Some "investigate")) ] ]
        for g in result.Agents do
            Assert.That(g.Count, Is.EqualTo(g.Members |> List.length))
        let members = agentMembers (AgentGroupKind.Activity CurrentActivity.Investigating) result
        Assert.That(members |> List.map _.ScopedKey, Is.EqualTo([ "/wt/1"; "/wt/2"; "/wt/3" ]))

    [<Test>]
    member _.``The Waiting group carries its waiting worktrees as members, each contributing 1``() =
        let result =
            aggregate
                [ repo
                    [ at "/wt/w1" "wait-1" (agentWt CodingToolStatus.WaitingForUser None)
                      at "/wt/w2" "wait-2" (agentWt CodingToolStatus.WaitingForUser None) ] ]
        let members = agentMembers AgentGroupKind.Waiting result
        Assert.That(members |> List.map _.ScopedKey, Is.EqualTo([ "/wt/w1"; "/wt/w2" ]))
        Assert.That(members |> List.forall (fun m -> m.Contribution = 1))
        Assert.That(agentCount AgentGroupKind.Waiting result, Is.EqualTo(Some(members |> List.length)))

    [<Test>]
    member _.``The Idle group carries its Idle worktrees as members, each contributing 1``() =
        let result =
            aggregate
                [ repo
                    [ at "/wt/s1" "idle-1" (agentWt CodingToolStatus.Idle None)
                      at "/wt/s2" "idle-2" (agentWt CodingToolStatus.Idle None) ] ]
        let members = agentMembers AgentGroupKind.Idle result
        Assert.That(members |> List.map _.ScopedKey, Is.EqualTo([ "/wt/s1"; "/wt/s2" ]))
        Assert.That(members |> List.forall (fun m -> m.Contribution = 1))
        Assert.That(agentCount AgentGroupKind.Idle result, Is.EqualTo(Some(members |> List.length)))

    [<Test>]
    member _.``Agent members carry the worktree's CodingToolSince (time in category)``() =
        let since = System.DateTimeOffset(2025, 1, 1, 12, 0, 0, System.TimeSpan.Zero)
        let idle = { agentWt CodingToolStatus.Idle None with CodingToolSince = Some since }
        let result = aggregate [ repo [ at "/wt/s1" "idle-1" idle ] ]
        let members = agentMembers AgentGroupKind.Idle result
        Assert.That(members |> List.map _.Since, Is.EqualTo([ Some since ]))

    [<Test>]
    member _.``Task-bucket members always have Since = None, even when the worktree carries one``() =
        let since = System.DateTimeOffset(2025, 1, 1, 12, 0, 0, System.TimeSpan.Zero)
        let wt = { activeTaskWt (beads 0 2 0 0) BeadsPlanning.zero with CodingToolSince = Some since }
        let result = aggregate [ repo [ at "/wt/1" "b1" wt ] ]
        let members = taskMembers TaskBucketKind.InProgress result
        Assert.That(members |> List.forall (fun m -> m.Since = None))

    [<Test>]
    member _.``Agent members carry the worktree's ContextUsage (for the fill donut)``() =
        let usage = { CurrentTokens = 120000; TokenLimit = 200000 }
        let wt = { workingWt None with ContextUsage = Some usage }
        let result = aggregate [ repo [ at "/wt/s1" "w1" wt ] ]
        let members = agentMembers (AgentGroupKind.Activity CurrentActivity.Working) result
        Assert.That(members |> List.map _.ContextUsage, Is.EqualTo([ Some usage ]))

    [<Test>]
    member _.``Task-bucket members always have ContextUsage = None, even when the worktree carries one``() =
        let wt = { activeTaskWt (beads 0 2 0 0) BeadsPlanning.zero with ContextUsage = Some { CurrentTokens = 1; TokenLimit = 2 } }
        let result = aggregate [ repo [ at "/wt/1" "b1" wt ] ]
        let members = taskMembers TaskBucketKind.InProgress result
        Assert.That(members |> List.forall (fun m -> m.ContextUsage = None))

    [<Test>]
    member _.``Task bucket Count equals the sum of its member Contributions``() =
        let result =
            aggregate
                [ repo
                    [ at "/wt/1" "b1" (activeTaskWt (beads 0 2 0 0) BeadsPlanning.zero)
                      at "/wt/2" "b2" (activeTaskWt (beads 0 3 0 0) BeadsPlanning.zero) ] ]
        let members = taskMembers TaskBucketKind.InProgress result
        Assert.That(taskCount TaskBucketKind.InProgress result, Is.EqualTo(Some 5))
        Assert.That(members |> List.sumBy _.Contribution, Is.EqualTo(5))
        // Each worktree's contribution is exactly its own in-progress count.
        Assert.That(
            members |> List.map (fun m -> m.ScopedKey, m.Contribution),
            Is.EqualTo([ ("/wt/1", 2); ("/wt/2", 3) ]))

    [<Test>]
    member _.``A worktree is a task-bucket member iff its contribution to that bucket is greater than zero``() =
        // /wt/1 has in-progress work; /wt/2 has none. Only /wt/1 is an InProgress member.
        let result =
            aggregate
                [ repo
                    [ at "/wt/1" "b1" (activeTaskWt (beads 0 4 0 0) BeadsPlanning.zero)
                      at "/wt/2" "b2" (activeTaskWt (beads 0 0 0 7) BeadsPlanning.zero) ] ]
        let inProgress = taskMembers TaskBucketKind.InProgress result
        Assert.That(inProgress |> List.map _.ScopedKey, Is.EqualTo([ "/wt/1" ]))
        // /wt/2's Closed work makes it (and only it) a Done member.
        let done_ = taskMembers TaskBucketKind.Done result
        Assert.That(done_ |> List.map _.ScopedKey, Is.EqualTo([ "/wt/2" ]))

    [<Test>]
    member _.``Planned members carry Planned + Loose as their contribution``() =
        let result = aggregate [ repo [ at "/wt/1" "b1" (taskWt BeadsSummary.zero (planning 4 0 3)) ] ]
        let members = taskMembers TaskBucketKind.Planned result
        Assert.That(members |> List.map (fun m -> m.ScopedKey, m.Contribution), Is.EqualTo([ ("/wt/1", 7) ]))

    [<Test>]
    member _.``Inactive in-progress/queued work becomes an Unattended member, not an InProgress/Queued member``() =
        let result = aggregate [ repo [ at "/wt/idle" "b" (taskWt (beads 0 3 0 0) (planning 0 2 0)) ] ]
        Assert.That(taskMembers TaskBucketKind.InProgress result, Is.Empty)
        Assert.That(taskMembers TaskBucketKind.Queued result, Is.Empty)
        let unattended = taskMembers TaskBucketKind.Unattended result
        Assert.That(unattended |> List.map (fun m -> m.ScopedKey, m.Contribution), Is.EqualTo([ ("/wt/idle", 5) ])) // 3 + 2

    [<Test>]
    member _.``An archived worktree is a member of no task bucket``() =
        let result =
            aggregate
                [ repo
                    [ at "/wt/live" "b1" ({ activeTaskWt (beads 0 0 2 7) BeadsPlanning.zero with IsArchived = false })
                      at "/wt/arch" "b2" ({ activeTaskWt (beads 0 0 5 100) BeadsPlanning.zero with IsArchived = true }) ] ]
        // Only the non-archived worktree is ever a member — the archived one is dropped from every bucket.
        Assert.That(taskMembers TaskBucketKind.Done result |> List.map _.ScopedKey, Is.EqualTo([ "/wt/live" ]))
        Assert.That(
            taskMembers TaskBucketKind.Blocked result |> List.map (fun m -> m.ScopedKey, m.Contribution),
            Is.EqualTo([ ("/wt/live", 2) ]))

    [<Test>]
    member _.``Members carry their owning repo name and preserve repo then worktree order``() =
        let result =
            aggregate
                [ namedRepo "alpha" [ at "/a/1" "a1" (workingWt (Some "investigate")) ]
                  namedRepo "bravo"
                      [ at "/b/1" "b1" (workingWt (Some "investigate"))
                        at "/b/2" "b2" (workingWt (Some "investigate")) ] ]
        let members = agentMembers (AgentGroupKind.Activity CurrentActivity.Investigating) result
        Assert.That(
            members |> List.map (fun m -> m.RepoName, m.ScopedKey),
            Is.EqualTo([ ("alpha", "/a/1"); ("bravo", "/b/1"); ("bravo", "/b/2") ]))

    [<Test>]
    member _.``Invariants hold across a mixed multi-repo roll-up``() =
        // A blend of active/inactive/archived task worktrees and red-dot/waiting agents.
        let result =
            aggregate
                [ namedRepo "alpha"
                    [ at "/a/work" "w" (activeTaskWt (beads 0 2 1 3) (planning 1 4 0))
                      at "/a/idle" "i" (taskWt (beads 0 5 0 0) (planning 0 2 0)) ]
                  namedRepo "bravo"
                    [ at "/b/wait" "wa" ({ agentWt CodingToolStatus.WaitingForUser None with Beads = beads 0 1 0 0 })
                      at "/b/arch" "ar" ({ activeTaskWt (beads 0 0 0 9) BeadsPlanning.zero with IsArchived = true }) ] ]
        // Every agent group: Count = Members.Length.
        for g in result.Agents do
            Assert.That(g.Count, Is.EqualTo(g.Members |> List.length))
        // Every task bucket: Count = Σ member Contribution, and every member contributes > 0.
        for b in result.Tasks do
            Assert.That(b.Count, Is.EqualTo(b.Members |> List.sumBy _.Contribution))
            Assert.That(b.Members |> List.forall (fun m -> m.Contribution > 0))
            Assert.That(b.Members |> List.forall (fun m -> m.RepoName <> ""))

    [<Test>]
    member _.``Members carry a stable RepoId so two distinct same-named repos stay separable``() =
        // Two DISTINCT repos (different RepoId) that happen to share a folder name ("dup"). The
        // drill-down groups/counts/keys repo blocks on RepoId, so these must NOT collapse: each
        // worktree keeps its own repo identity even though the display label is identical.
        let result =
            aggregate
                [ idNamedRepo "repo-a" "dup" [ at "/a/1" "a1" (workingWt (Some "investigate")) ]
                  idNamedRepo "repo-b" "dup" [ at "/b/1" "b1" (workingWt (Some "investigate")) ] ]
        let members = agentMembers (AgentGroupKind.Activity CurrentActivity.Investigating) result
        // Both worktrees are members, in repo/worktree order, each tagged with its own RepoId but the
        // shared display name.
        Assert.That(
            members |> List.map (fun m -> m.RepoId, m.RepoName, m.ScopedKey),
            Is.EqualTo([ (RepoId "repo-a", "dup", "/a/1"); (RepoId "repo-b", "dup", "/b/1") ]))
        // The two distinct repos stay distinct by identity (2 RepoIds) though they share one name.
        Assert.That(members |> List.map _.RepoId |> List.distinct |> List.length, Is.EqualTo(2))
        Assert.That(members |> List.map _.RepoName |> List.distinct |> List.length, Is.EqualTo(1))
