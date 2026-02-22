module Tests.PrFilteringTests

open System
open NUnit.Framework
open Server.PrStatus

let private mkPr branch prId isMerged closedDate =
    { BranchName = branch
      PrId = prId
      Title = $"PR for {branch}"
      IsDraft = false
      IsMerged = isMerged
      ClosedDate = closedDate }

let private active branch prId = mkPr branch prId false None
let private merged branch prId date = mkPr branch prId true (Some date)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FirstPerBranchTests() =

    [<Test>]
    member _.``Empty list returns empty``() =
        let result = firstPerBranch []
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Active PR preferred over merged for same branch``() =
        let prs =
            [ merged "feature/foo" 1 (DateTimeOffset(2025, 1, 10, 0, 0, 0, TimeSpan.Zero))
              active "feature/foo" 2 ]

        let result = firstPerBranch prs

        Assert.That(result, Has.Exactly(1).Items)
        Assert.That(result.[0].PrId, Is.EqualTo(2))

    [<Test>]
    member _.``Most recently closed merged PR preferred when no active``() =
        let prs =
            [ merged "feature/bar" 1 (DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
              merged "feature/bar" 2 (DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero)) ]

        let result = firstPerBranch prs

        Assert.That(result, Has.Exactly(1).Items)
        Assert.That(result.[0].PrId, Is.EqualTo(2))

    [<Test>]
    member _.``Different branches each get one PR``() =
        let prs =
            [ active "feature/a" 1
              active "feature/b" 2
              active "feature/a" 3 ]

        let result = firstPerBranch prs

        let branches = result |> List.map _.BranchName |> set
        Assert.That(branches, Is.EqualTo(set [ "feature/a"; "feature/b" ]))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FilterRelevantPrsTests() =

    [<Test>]
    member _.``Only PRs matching known branches are returned``() =
        let prs =
            [ active "feature/mine" 1
              active "feature/theirs" 2
              active "feature/also-mine" 3 ]

        let known = set [ "feature/mine"; "feature/also-mine" ]
        let result = filterRelevantPrs known prs

        let ids = result |> List.map _.PrId
        Assert.That(ids, Is.EquivalentTo([ 1; 3 ]))

    [<Test>]
    member _.``Empty known branches returns empty``() =
        let prs = [ active "feature/a" 1; active "feature/b" 2 ]
        let result = filterRelevantPrs Set.empty prs

        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Merged PR for known branch is included``() =
        let prs =
            [ merged "feature/done" 1 (DateTimeOffset(2025, 1, 10, 0, 0, 0, TimeSpan.Zero)) ]

        let known = set [ "feature/done" ]
        let result = filterRelevantPrs known prs

        Assert.That(result, Has.Exactly(1).Items)
        Assert.That(result.[0].IsMerged, Is.True)

    [<Test>]
    member _.``Deduplication happens before filtering``() =
        let prs =
            [ active "feature/x" 1
              merged "feature/x" 2 (DateTimeOffset(2025, 1, 10, 0, 0, 0, TimeSpan.Zero)) ]

        let known = set [ "feature/x" ]
        let result = filterRelevantPrs known prs

        Assert.That(result, Has.Exactly(1).Items)
        Assert.That(result.[0].PrId, Is.EqualTo(1), "Active PR should win over merged")
