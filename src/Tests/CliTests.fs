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

    let basePr =
        { Id = 1
          Title = "A pull request"
          Url = "https://example.com/pr/1"
          IsDraft = false
          Comments = WithResolution(0, 0)
          Builds = []
          IsMerged = false
          AutoMergeEnabled = false
          HasConflicts = false }

    [<Test>]
    member _.``NoPr formats as No PR``() =
        Assert.That(formatPr NoPr, Is.EqualTo("No PR"))

    [<Test>]
    member _.``HasPr with no flags shows PR number and title``() =
        let result = formatPr (HasPr { basePr with Id = 42; Title = "Add feature X" })
        Assert.That(result, Is.EqualTo("PR #42: Add feature X"))

    [<Test>]
    member _.``HasPr draft shows draft flag``() =
        let result = formatPr (HasPr { basePr with Id = 7; Title = "WIP changes"; IsDraft = true })
        Assert.That(result, Is.EqualTo("PR #7 [draft]: WIP changes"))

    [<Test>]
    member _.``HasPr merged shows merged flag``() =
        let result = formatPr (HasPr { basePr with Id = 10; Title = "Done"; IsMerged = true })
        Assert.That(result, Is.EqualTo("PR #10 [merged]: Done"))

    [<Test>]
    member _.``HasPr with auto-merge shows auto-merge flag``() =
        let result = formatPr (HasPr { basePr with Id = 8; Title = "Queued"; AutoMergeEnabled = true })
        Assert.That(result, Is.EqualTo("PR #8 [auto-merge]: Queued"))

    [<Test>]
    member _.``HasPr with conflicts shows conflicts flag``() =
        let result = formatPr (HasPr { basePr with Id = 5; Title = "Conflicting"; HasConflicts = true })
        Assert.That(result, Is.EqualTo("PR #5 [conflicts]: Conflicting"))

    [<Test>]
    member _.``HasPr with all flags shows all flags``() =
        let result =
            formatPr (
                HasPr
                    { basePr with
                        Id = 99
                        Title = "Everything"
                        IsDraft = true
                        IsMerged = true
                        AutoMergeEnabled = true
                        HasConflicts = true })

        Assert.That(result, Is.EqualTo("PR #99 [draft, merged, auto-merge, conflicts]: Everything"))

    [<Test>]
    member _.``HasPr draft and conflicts shows both flags``() =
        let result =
            formatPr (HasPr { basePr with Id = 3; Title = "Draft conflict"; IsDraft = true; HasConflicts = true })

        Assert.That(result, Is.EqualTo("PR #3 [draft, conflicts]: Draft conflict"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FoldRootResultsTests() =

    // Stub root op: fails for any path in failOn, succeeds otherwise. Lets us exercise the
    // tri-state exit code (0 = all ok, 1 = all failed, 2 = partial) without a live server.
    let stubOp (failOn: Set<string>) (path: string) : Async<Result<unit, string>> =
        async { return (if failOn.Contains path then Error $"bad path: {path}" else Ok()) }

    [<Test>]
    member _.``all paths succeed returns 0``() =
        let result = foldRootResults "Added" (stubOp Set.empty) [| "a"; "b"; "c" |]
        Assert.That(result, Is.EqualTo(0))

    [<Test>]
    member _.``single path success returns 0``() =
        let result = foldRootResults "Added" (stubOp Set.empty) [| "a" |]
        Assert.That(result, Is.EqualTo(0))

    [<Test>]
    member _.``all paths fail returns 1``() =
        let result = foldRootResults "Added" (stubOp (Set.ofList [ "a"; "b" ])) [| "a"; "b" |]
        Assert.That(result, Is.EqualTo(1))

    [<Test>]
    member _.``single path failure returns 1``() =
        let result = foldRootResults "Removed" (stubOp (Set.ofList [ "a" ])) [| "a" |]
        Assert.That(result, Is.EqualTo(1))

    [<Test>]
    member _.``partial success (valid then invalid) returns 2``() =
        // The exact regression: a [valid; invalid] batch persists the valid root but
        // must still signal "something changed" (2) so treemon.ps1 restarts to apply it.
        let result = foldRootResults "Added" (stubOp (Set.ofList [ "invalid" ])) [| "valid"; "invalid" |]
        Assert.That(result, Is.EqualTo(2))

    [<Test>]
    member _.``partial success (invalid then valid) returns 2``() =
        // Order independence: failure first must not mask the later success.
        let result = foldRootResults "Added" (stubOp (Set.ofList [ "invalid" ])) [| "invalid"; "valid" |]
        Assert.That(result, Is.EqualTo(2))

    [<Test>]
    member _.``empty batch returns 0``() =
        // Degenerate (CLI arity is OneOrMore, so unreachable in practice): no failures → 0.
        let result = foldRootResults "Added" (stubOp Set.empty) [||]
        Assert.That(result, Is.EqualTo(0))
