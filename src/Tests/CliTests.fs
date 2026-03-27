module Tests.CliTests

open NUnit.Framework
open Shared
open Cli.Program

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ResolvePortTests() =

    [<Test>]
    member _.``None with no env var returns 5000``() =
        let result = resolvePort None None
        Assert.That(result, Is.EqualTo(5000))

    [<Test>]
    member _.``Some port returns that port``() =
        let result = resolvePort (Some 8080) None
        Assert.That(result, Is.EqualTo(8080))

    [<Test>]
    member _.``Some 0 returns 0``() =
        let result = resolvePort (Some 0) None
        Assert.That(result, Is.EqualTo(0))

    [<Test>]
    member _.``None with TREEMON_PORT env var returns parsed port``() =
        let result = resolvePort None (Some "9090")
        Assert.That(result, Is.EqualTo(9090))

    [<Test>]
    member _.``None with non-numeric TREEMON_PORT returns 5000``() =
        let result = resolvePort None (Some "not-a-number")
        Assert.That(result, Is.EqualTo(5000))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FormatPrTests() =

    let makePrInfo id title isDraft isMerged hasConflicts =
        HasPr
            { Id = id
              Title = title
              Url = $"https://example.com/pr/{id}"
              IsDraft = isDraft
              Comments = WithResolution(0, 0)
              Builds = []
              IsMerged = isMerged
              HasConflicts = hasConflicts }

    [<Test>]
    member _.``NoPr formats as No PR``() =
        Assert.That(formatPr NoPr, Is.EqualTo("No PR"))

    [<Test>]
    member _.``HasPr with no flags shows PR number and title``() =
        let result = formatPr (makePrInfo 42 "Add feature X" false false false)
        Assert.That(result, Is.EqualTo("PR #42: Add feature X"))

    [<Test>]
    member _.``HasPr draft shows draft flag``() =
        let result = formatPr (makePrInfo 7 "WIP changes" true false false)
        Assert.That(result, Is.EqualTo("PR #7 [draft]: WIP changes"))

    [<Test>]
    member _.``HasPr merged shows merged flag``() =
        let result = formatPr (makePrInfo 10 "Done" false true false)
        Assert.That(result, Is.EqualTo("PR #10 [merged]: Done"))

    [<Test>]
    member _.``HasPr with conflicts shows conflicts flag``() =
        let result = formatPr (makePrInfo 5 "Conflicting" false false true)
        Assert.That(result, Is.EqualTo("PR #5 [conflicts]: Conflicting"))

    [<Test>]
    member _.``HasPr with all flags shows all flags``() =
        let result = formatPr (makePrInfo 99 "Everything" true true true)
        Assert.That(result, Is.EqualTo("PR #99 [draft, merged, conflicts]: Everything"))

    [<Test>]
    member _.``HasPr draft and conflicts shows both flags``() =
        let result = formatPr (makePrInfo 3 "Draft conflict" true false true)
        Assert.That(result, Is.EqualTo("PR #3 [draft, conflicts]: Draft conflict"))
