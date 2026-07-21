module Tests.BeadsPlanningTests

open NUnit.Framework
open Shared
open Server.BeadsStatus

/// Tests for the pure planning classifier (Server.BeadsStatus.Planning.classify).
/// It partitions OPEN, non-feature issues into Planned/Queued/Loose by the status of their
/// direct parent-child parent feature (one hop). ParentId is populated from a parent-child edge
/// ONLY (never a blocks edge) — that contract is enforced by the JSONL parser, so here a
/// blocks-only relationship is represented by ParentId = None.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PlanningClassifierTests() =

    let issue id issueType status parentId : PlanningIssue =
        { Id = id; IssueType = issueType; Status = status; ParentId = parentId }

    let feature id status = issue id "feature" status None
    let task id status parentId = issue id "task" status parentId

    [<Test>]
    member _.``Open task under an open feature is Planned``() =
        let result =
            Planning.classify
                [ feature "feat-1" "open"
                  task "task-1" "open" (Some "feat-1") ]
        Assert.That(result.Planned, Is.EqualTo(1))
        Assert.That(result.Queued, Is.EqualTo(0))
        Assert.That(result.Loose, Is.EqualTo(0))

    [<Test>]
    member _.``Open task under an in_progress feature is Queued``() =
        let result =
            Planning.classify
                [ feature "feat-1" "in_progress"
                  task "task-1" "open" (Some "feat-1") ]
        Assert.That(result.Queued, Is.EqualTo(1))
        Assert.That(result.Planned, Is.EqualTo(0))
        Assert.That(result.Loose, Is.EqualTo(0))

    [<Test>]
    member _.``Open task under a closed feature is Loose``() =
        let result =
            Planning.classify
                [ feature "feat-1" "closed"
                  task "task-1" "open" (Some "feat-1") ]
        Assert.That(result.Loose, Is.EqualTo(1))
        Assert.That(result.Planned, Is.EqualTo(0))
        Assert.That(result.Queued, Is.EqualTo(0))

    [<Test>]
    member _.``Open task under a blocked feature is Loose``() =
        let result =
            Planning.classify
                [ feature "feat-1" "blocked"
                  task "task-1" "open" (Some "feat-1") ]
        Assert.That(result.Loose, Is.EqualTo(1))
        Assert.That(result.Planned, Is.EqualTo(0))
        Assert.That(result.Queued, Is.EqualTo(0))

    [<Test>]
    member _.``Open task with no parent is Loose``() =
        let result = Planning.classify [ task "task-1" "open" None ]
        Assert.That(result.Loose, Is.EqualTo(1))
        Assert.That(result.Planned, Is.EqualTo(0))
        Assert.That(result.Queued, Is.EqualTo(0))

    [<Test>]
    member _.``A blocks edge is not treated as parent-child so the task is Loose``() =
        // The task is only *blocked by* an in_progress feature; a blocks edge never populates
        // ParentId, so the classifier sees no parent and must bucket it Loose — NOT Queued.
        let result =
            Planning.classify
                [ feature "feat-1" "in_progress"
                  task "task-1" "open" None ]
        Assert.That(result.Loose, Is.EqualTo(1))
        Assert.That(result.Queued, Is.EqualTo(0))
        Assert.That(result.Planned, Is.EqualTo(0))

    [<Test>]
    member _.``Open subtask whose parent is a task (non-feature) is Loose``() =
        // parent is an in_progress task (not a feature) — must not be read as a queued feature.
        let result =
            Planning.classify
                [ task "task-1" "in_progress" None
                  task "subtask-1" "open" (Some "task-1") ]
        Assert.That(result.Loose, Is.EqualTo(1))
        Assert.That(result.Planned, Is.EqualTo(0))
        Assert.That(result.Queued, Is.EqualTo(0))

    [<Test>]
    member _.``Parent referenced but missing from the set is Loose``() =
        // Dangling ParentId (parent not in this worktree's issue set) must degrade to Loose.
        let result = Planning.classify [ task "task-1" "open" (Some "ghost") ]
        Assert.That(result.Loose, Is.EqualTo(1))
        Assert.That(result.Planned, Is.EqualTo(0))
        Assert.That(result.Queued, Is.EqualTo(0))

    [<Test>]
    member _.``Only open issues are partitioned``() =
        // in_progress / blocked / closed tasks belong to the status summary, not the split.
        let result =
            Planning.classify
                [ feature "feat-1" "open"
                  task "task-open" "open" (Some "feat-1")
                  task "task-wip" "in_progress" (Some "feat-1")
                  task "task-blocked" "blocked" (Some "feat-1")
                  task "task-closed" "closed" (Some "feat-1") ]
        Assert.That(result.Planned, Is.EqualTo(1))
        Assert.That(result.Queued, Is.EqualTo(0))
        Assert.That(result.Loose, Is.EqualTo(0))

    [<Test>]
    member _.``An open feature is a container, not a bucketed task``() =
        // A top-level open feature must not itself inflate any bucket (display folds Loose into
        // Planned, and Planned is defined over open *tasks*, not features).
        let result = Planning.classify [ feature "feat-1" "open" ]
        Assert.That(result.Planned, Is.EqualTo(0))
        Assert.That(result.Queued, Is.EqualTo(0))
        Assert.That(result.Loose, Is.EqualTo(0))

    [<Test>]
    member _.``Empty issue set yields zero``() =
        Assert.That(Planning.classify [], Is.EqualTo(BeadsPlanning.zero))

    [<Test>]
    member _.``Mixed worktree partitions each open task by its own parent feature``() =
        let result =
            Planning.classify
                [ feature "feat-open" "open"
                  feature "feat-wip" "in_progress"
                  feature "feat-closed" "closed"
                  task "p1" "open" (Some "feat-open")     // Planned
                  task "p2" "open" (Some "feat-open")     // Planned
                  task "q1" "open" (Some "feat-wip")      // Queued
                  task "l1" "open" (Some "feat-closed")   // Loose (closed parent)
                  task "l2" "open" None                   // Loose (no parent)
                  task "done" "closed" (Some "feat-wip") ] // excluded (not open)
        Assert.That(result.Planned, Is.EqualTo(2))
        Assert.That(result.Queued, Is.EqualTo(1))
        Assert.That(result.Loose, Is.EqualTo(2))

    [<Test>]
    member _.``Parent lookup is one hop, not transitive - grandparent feature is not reached``() =
        // subtask-1 -> task-1 (non-feature) -> feat-1 (open feature). The classifier must stop at
        // the DIRECT parent (a task) and NOT walk up to the grandparent feature. A transitive walk
        // would make subtask-1 Planned (Planned=2, Loose=0); one-hop makes it Loose.
        let result =
            Planning.classify
                [ feature "feat-1" "open"
                  task "task-1" "open" (Some "feat-1")
                  task "subtask-1" "open" (Some "task-1") ]
        Assert.That(result.Planned, Is.EqualTo(1))  // task-1 only
        Assert.That(result.Loose, Is.EqualTo(1))    // subtask-1 (direct parent is a task)
        Assert.That(result.Queued, Is.EqualTo(0))

    [<Test>]
    member _.``Matching is case-insensitive against raw beads strings``() =
        // Guards decision (e): comparisons are OrdinalIgnoreCase, so mixed-case schema strings
        // still classify correctly.
        let result =
            Planning.classify
                [ issue "feat-1" "Feature" "OPEN" None
                  issue "task-1" "Task" "Open" (Some "feat-1") ]
        Assert.That(result.Planned, Is.EqualTo(1))
        Assert.That(result.Queued, Is.EqualTo(0))
        Assert.That(result.Loose, Is.EqualTo(0))
