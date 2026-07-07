module Tests.BeadsDataTests

open NUnit.Framework
open Shared
open Server.BeadsStatus

/// Tests for the .beads/issues.jsonl parser and status summary in Server.BeadsStatus — the
/// beads-schema knowledge isolated in that module. They pin the parent-child edge direction
/// (issue_id = child, depends_on_id = parent), the deliberate ignoring of `blocks` edges, and
/// the "count ALL issue types by status" rule for the summary.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BeadsDataTests() =

    // Real beads JSONL shape: one object per line, dependency edges inline on the child record.
    let jsonl (lines: string list) = String.concat "\n" lines

    let feature id status =
        sprintf """{"id":"%s","status":"%s","issue_type":"feature"}""" id status

    // A task carrying a single parent-child edge to its parent feature.
    let childTask id status parent =
        sprintf
            """{"id":"%s","status":"%s","issue_type":"task","dependencies":[{"issue_id":"%s","depends_on_id":"%s","type":"parent-child","created_by":"unknown"}]}"""
            id status id parent

    // A task whose ONLY edge is a blocks edge (blocked-by a blocker) — must NOT become a parent.
    let blockedTask id status blocker =
        sprintf
            """{"id":"%s","status":"%s","issue_type":"task","dependencies":[{"issue_id":"%s","depends_on_id":"%s","type":"blocks","created_by":"unknown"}]}"""
            id status id blocker

    // A task with no dependencies field at all.
    let looseTask id status =
        sprintf """{"id":"%s","status":"%s","issue_type":"task"}""" id status

    // A task whose parent-child edge is MALFORMED: it omits issue_id, so the edge cannot be
    // proven to belong to THIS record (the direction guard) and must be rejected — never a match.
    let childTaskMissingIssueId id status parent =
        sprintf
            """{"id":"%s","status":"%s","issue_type":"task","dependencies":[{"depends_on_id":"%s","type":"parent-child","created_by":"unknown"}]}"""
            id status parent

    [<Test>]
    member _.``parseIssues resolves ParentId from a parent-child edge (depends_on_id = parent)``() =
        let issues = parseIssues (jsonl [ feature "feat-1" "open"; childTask "task-1" "open" "feat-1" ])
        let task = issues |> List.find (fun i -> i.Id = "task-1")
        Assert.That(task.ParentId, Is.EqualTo(Some "feat-1"))
        Assert.That(task.IssueType, Is.EqualTo("task"))
        Assert.That(task.Status, Is.EqualTo("open"))

    [<Test>]
    member _.``parseIssues never treats a blocks edge as a parent``() =
        let issues = parseIssues (jsonl [ blockedTask "task-1" "open" "blocker-1" ])
        Assert.That((List.exactlyOne issues).ParentId, Is.EqualTo(None))

    [<Test>]
    member _.``parseIssues yields no parent when the dependencies field is absent``() =
        let issues = parseIssues (jsonl [ looseTask "task-1" "open" ])
        Assert.That((List.exactlyOne issues).ParentId, Is.EqualTo(None))

    [<Test>]
    member _.``parseIssues rejects a parent-child edge missing issue_id (cannot prove it belongs to this record)``() =
        // A malformed edge with no issue_id must NOT match this record: without the child id we
        // cannot prove the edge belongs here, so depends_on_id must not leak into ParentId.
        let issues =
            parseIssues (jsonl [ feature "feat-1" "open"; childTaskMissingIssueId "task-1" "open" "feat-1" ])
        let task = issues |> List.find (fun i -> i.Id = "task-1")
        Assert.That(task.ParentId, Is.EqualTo(None))

    [<Test>]
    member _.``parseIssues picks the parent-child edge when a record also has a blocks edge``() =
        // Real records (e.g. tm-planning-box-1rf) carry BOTH a parent-child and a blocks edge.
        let record =
            """{"id":"task-1","status":"open","issue_type":"task","dependencies":[{"issue_id":"task-1","depends_on_id":"feat-1","type":"parent-child"},{"issue_id":"task-1","depends_on_id":"blocker-1","type":"blocks"}]}"""
        let issues = parseIssues record
        Assert.That((List.exactlyOne issues).ParentId, Is.EqualTo(Some "feat-1"))

    [<Test>]
    member _.``parseIssues skips blank lines and parses every record``() =
        let content = jsonl [ feature "feat-1" "open"; ""; childTask "task-1" "open" "feat-1"; "   " ]
        Assert.That(parseIssues content |> List.length, Is.EqualTo(2))

    [<Test>]
    member _.``parseIssues of empty content is empty``() =
        Assert.That(parseIssues "", Is.Empty)

    [<Test>]
    member _.``summarize counts ALL issue types by status including features``() =
        let issues =
            parseIssues (
                jsonl
                    [ feature "feat-1" "open" // a feature counts toward Open too
                      childTask "t1" "open" "feat-1"
                      childTask "t2" "in_progress" "feat-1"
                      childTask "t3" "blocked" "feat-1"
                      childTask "t4" "closed" "feat-1" ])
        let summary = summarize issues
        Assert.That(summary.Open, Is.EqualTo(2)) // feat-1 + t1
        Assert.That(summary.InProgress, Is.EqualTo(1)) // t2
        Assert.That(summary.Blocked, Is.EqualTo(1)) // t3
        Assert.That(summary.Closed, Is.EqualTo(1)) // t4

    [<Test>]
    member _.``summarize of empty issues is zero``() =
        Assert.That(summarize [], Is.EqualTo(BeadsSummary.zero))

    [<Test>]
    member _.``parse then classify buckets an open task under an in_progress feature as Queued``() =
        let planning =
            parseIssues (jsonl [ feature "feat-1" "in_progress"; childTask "task-1" "open" "feat-1" ])
            |> Planning.classify
        Assert.That(planning.Queued, Is.EqualTo(1))
        Assert.That(planning.Planned, Is.EqualTo(0))
        Assert.That(planning.Loose, Is.EqualTo(0))

    [<Test>]
    member _.``parse then classify: a blocks edge to an in_progress feature stays Loose (not Queued)``() =
        // Proves the parser does not leak a blocks edge into ParentId: task-1 is only *blocked by*
        // an in_progress feature, so it must land in Loose, never Queued.
        let planning =
            parseIssues (jsonl [ feature "feat-1" "in_progress"; blockedTask "task-1" "open" "feat-1" ])
            |> Planning.classify
        Assert.That(planning.Loose, Is.EqualTo(1))
        Assert.That(planning.Queued, Is.EqualTo(0))
        Assert.That(planning.Planned, Is.EqualTo(0))

    [<Test>]
    member _.``parse then classify: a parent-child edge missing issue_id does not bucket the task (stays Loose)``() =
        // Regression guard: a malformed edge with no child issue_id must not match ANY issue.
        // task-1 stays Loose even though a matching OPEN feature is present — with the old bug
        // (missing issue_id treated as a match) it would have been miscounted as Planned.
        let planning =
            parseIssues (jsonl [ feature "feat-1" "open"; childTaskMissingIssueId "task-1" "open" "feat-1" ])
            |> Planning.classify
        Assert.That(planning.Loose, Is.EqualTo(1))
        Assert.That(planning.Planned, Is.EqualTo(0))
        Assert.That(planning.Queued, Is.EqualTo(0))
