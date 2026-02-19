module Tests.EventProcessingTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils

[<TestFixture>]
[<Category("Unit")>]
type ExtractBranchNameTests() =

    [<Test>]
    member _.``Parenthesized suffix is stripped``() =
        let result = extractBranchName "feature/foo (450ms)"
        Assert.That(result, Is.EqualTo(Some "feature/foo"))

    [<Test>]
    member _.``Colon suffix is stripped``() =
        let result = extractBranchName "main: error"
        Assert.That(result, Is.EqualTo(Some "main"))

    [<Test>]
    member _.``Plain text without delimiter returns None``() =
        let result = extractBranchName "just-plain-text"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Empty string returns None``() =
        let result = extractBranchName ""
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Branch with slash and parenthesized suffix``() =
        let result = extractBranchName "release/v2.0 (completed)"
        Assert.That(result, Is.EqualTo(Some "release/v2.0"))

    [<Test>]
    member _.``Branch with colon and detailed message``() =
        let result = extractBranchName "feature/auth: timeout after 30s"
        Assert.That(result, Is.EqualTo(Some "feature/auth"))

    [<Test>]
    member _.``Parenthesis at start does not match``() =
        let result = extractBranchName " (no-branch)"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Colon at start does not match``() =
        let result = extractBranchName ": no-branch"
        Assert.That(result, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
type EventKeyTests() =

    let makeEvent source message =
        { Source = source
          Message = message
          Timestamp = DateTimeOffset.UtcNow
          Status = None
          Duration = None }

    [<Test>]
    member _.``Composite key from source and extracted branch``() =
        let evt = makeEvent "Git" "main (15s)"
        let key = eventKey evt
        Assert.That(key, Is.EqualTo(("Git", "main")))

    [<Test>]
    member _.``Plain message uses empty string for branch``() =
        let evt = makeEvent "Scheduler" "tick"
        let key = eventKey evt
        Assert.That(key, Is.EqualTo(("Scheduler", "")))

    [<Test>]
    member _.``Different sources produce different keys``() =
        let evt1 = makeEvent "Git" "main (5s)"
        let evt2 = makeEvent "PR" "main (5s)"
        Assert.That(eventKey evt1, Is.Not.EqualTo(eventKey evt2))

    [<Test>]
    member _.``Same source and branch produce same key``() =
        let evt1 = makeEvent "Git" "feature/x (10ms)"
        let evt2 = makeEvent "Git" "feature/x (200ms)"
        Assert.That(eventKey evt1, Is.EqualTo(eventKey evt2))


[<TestFixture>]
[<Category("Unit")>]
type PinnedErrorsTests() =

    let makeEvent source message status timestamp =
        { Source = source
          Message = message
          Timestamp = timestamp
          Status = Some status
          Duration = None }

    let baseTime = DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``Empty list returns empty``() =
        let result = pinnedErrors []
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Single error is pinned``() =
        let events =
            [ makeEvent "Git" "main (5s)" (StepStatus.Failed "timeout") baseTime ]

        let result = pinnedErrors events
        Assert.That(result.Length, Is.EqualTo(1))
        Assert.That(result.[0].Message, Is.EqualTo("main (5s)"))

    [<Test>]
    member _.``Success after error clears the pin``() =
        let events =
            [ makeEvent "Git" "main (5s)" (StepStatus.Failed "timeout") baseTime
              makeEvent "Git" "main (3s)" StepStatus.Succeeded (baseTime.AddSeconds(10.0)) ]

        let result = pinnedErrors events
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Error after success is pinned``() =
        let events =
            [ makeEvent "Git" "main (3s)" StepStatus.Succeeded baseTime
              makeEvent "Git" "main (5s)" (StepStatus.Failed "crash") (baseTime.AddSeconds(10.0)) ]

        let result = pinnedErrors events
        Assert.That(result.Length, Is.EqualTo(1))

    [<Test>]
    member _.``Multiple keys tracked independently``() =
        let events =
            [ makeEvent "Git" "main (5s)" (StepStatus.Failed "err1") baseTime
              makeEvent "PR" "feature/x (3s)" (StepStatus.Failed "err2") (baseTime.AddSeconds(1.0))
              makeEvent "Git" "main (2s)" StepStatus.Succeeded (baseTime.AddSeconds(2.0)) ]

        let result = pinnedErrors events

        Assert.That(result.Length, Is.EqualTo(1))
        Assert.That(result.[0].Source, Is.EqualTo("PR"))

    [<Test>]
    member _.``All succeeded returns empty``() =
        let events =
            [ makeEvent "Git" "main (5s)" StepStatus.Succeeded baseTime
              makeEvent "PR" "dev (3s)" StepStatus.Succeeded (baseTime.AddSeconds(1.0)) ]

        let result = pinnedErrors events
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Only latest event per key matters``() =
        let events =
            [ makeEvent "Git" "main (1s)" (StepStatus.Failed "old") baseTime
              makeEvent "Git" "main (2s)" (StepStatus.Failed "newer") (baseTime.AddSeconds(5.0))
              makeEvent "Git" "main (3s)" (StepStatus.Failed "newest") (baseTime.AddSeconds(10.0)) ]

        let result = pinnedErrors events

        Assert.That(result.Length, Is.EqualTo(1))
        Assert.That(result.[0].Message, Is.EqualTo("main (3s)"))

    [<Test>]
    member _.``Pending and Running status are not pinned``() =
        let events =
            [ makeEvent "Git" "main (5s)" StepStatus.Pending baseTime
              makeEvent "Git" "dev (3s)" StepStatus.Running (baseTime.AddSeconds(1.0)) ]

        let result = pinnedErrors events
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Events with no status are not pinned``() =
        let events =
            [ { Source = "Git"
                Message = "main (5s)"
                Timestamp = baseTime
                Status = None
                Duration = None } ]

        let result = pinnedErrors events
        Assert.That(result, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
type MergeWithPinnedErrorsTests() =

    let makeEvent source message status timestamp =
        { Source = source
          Message = message
          Timestamp = timestamp
          Status = Some status
          Duration = None }

    let baseTime = DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``Empty pinned map returns events unchanged``() =
        let events =
            [ makeEvent "Git" "main (5s)" StepStatus.Succeeded baseTime ]

        let result = mergeWithPinnedErrors events Map.empty
        Assert.That(result.Length, Is.EqualTo(1))

    [<Test>]
    member _.``Missing pinned error is appended``() =
        let events =
            [ makeEvent "Git" "main (5s)" StepStatus.Succeeded baseTime ]
        let pinnedError = makeEvent "PrFetch" "timeout" (StepStatus.Failed "timeout") baseTime
        let pinnedMap = Map.ofList [ (("PrFetch", ""), pinnedError) ]

        let result = mergeWithPinnedErrors events pinnedMap
        Assert.That(result.Length, Is.EqualTo(2))
        Assert.That(result.[1].Source, Is.EqualTo("PrFetch"))

    [<Test>]
    member _.``Pinned error already in events is not duplicated``() =
        let error = makeEvent "PrFetch" "timeout" (StepStatus.Failed "timeout") baseTime
        let events = [ error ]
        let pinnedMap = Map.ofList [ (("PrFetch", ""), error) ]

        let result = mergeWithPinnedErrors events pinnedMap
        Assert.That(result.Length, Is.EqualTo(1))

    [<Test>]
    member _.``Multiple missing pinned errors are all appended``() =
        let events =
            [ makeEvent "Git" "main (5s)" StepStatus.Succeeded baseTime ]
        let err1 = makeEvent "PrFetch" "timeout" (StepStatus.Failed "timeout") baseTime
        let err2 = makeEvent "GitFetch" "network error" (StepStatus.Failed "network") (baseTime.AddSeconds(1.0))
        let pinnedMap =
            Map.ofList
                [ (("PrFetch", ""), err1)
                  (("GitFetch", ""), err2) ]

        let result = mergeWithPinnedErrors events pinnedMap
        Assert.That(result.Length, Is.EqualTo(3))

    [<Test>]
    member _.``Empty events with pinned errors returns only pinned``() =
        let err = makeEvent "PrFetch" "timeout" (StepStatus.Failed "timeout") baseTime
        let pinnedMap = Map.ofList [ (("PrFetch", ""), err) ]

        let result = mergeWithPinnedErrors [] pinnedMap
        Assert.That(result.Length, Is.EqualTo(1))
        Assert.That(result.[0].Source, Is.EqualTo("PrFetch"))


[<TestFixture>]
[<Category("Unit")>]
type SortWorktreesTests() =

    let makeWorktree branch commitTime =
        { Path = $"/repo/{branch}"
          Branch = branch
          LastCommitMessage = "msg"
          LastCommitTime = commitTime
          Beads = BeadsSummary.zero
          Claude = ClaudeCodeStatus.Idle
          Pr = PrStatus.NoPr
          MainBehindCount = 0
          IsDirty = false
          WorkMetrics = None }

    let baseTime = DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``ByName sorts alphabetically``() =
        let worktrees =
            [ makeWorktree "zebra" baseTime
              makeWorktree "alpha" baseTime
              makeWorktree "middle" baseTime ]

        let result = sortWorktrees ByName worktrees

        let branches = result |> List.map (fun wt -> wt.Branch)
        Assert.That(branches, Is.EqualTo([ "alpha"; "middle"; "zebra" ]))

    [<Test>]
    member _.``ByActivity sorts by LastCommitTime descending``() =
        let worktrees =
            [ makeWorktree "old" (baseTime.AddHours(-5.0))
              makeWorktree "newest" (baseTime.AddHours(1.0))
              makeWorktree "middle" baseTime ]

        let result = sortWorktrees ByActivity worktrees

        let branches = result |> List.map (fun wt -> wt.Branch)
        Assert.That(branches, Is.EqualTo([ "newest"; "middle"; "old" ]))

    [<Test>]
    member _.``Empty list returns empty for ByName``() =
        let result = sortWorktrees ByName []
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Empty list returns empty for ByActivity``() =
        let result = sortWorktrees ByActivity []
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Single item list returns same item``() =
        let worktrees = [ makeWorktree "only" baseTime ]

        let resultName = sortWorktrees ByName worktrees
        let resultActivity = sortWorktrees ByActivity worktrees

        Assert.That(resultName.Length, Is.EqualTo(1))
        Assert.That(resultActivity.Length, Is.EqualTo(1))

    [<Test>]
    member _.``ByName is case-sensitive``() =
        let worktrees =
            [ makeWorktree "Beta" baseTime
              makeWorktree "alpha" baseTime ]

        let result = sortWorktrees ByName worktrees
        let branches = result |> List.map (fun wt -> wt.Branch)
        Assert.That(branches, Is.EqualTo([ "Beta"; "alpha" ]))

    [<Test>]
    member _.``ByActivity with same timestamps preserves relative order``() =
        let worktrees =
            [ makeWorktree "first" baseTime
              makeWorktree "second" baseTime ]

        let result = sortWorktrees ByActivity worktrees

        Assert.That(result.Length, Is.EqualTo(2))
