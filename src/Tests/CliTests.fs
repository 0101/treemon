module Tests.CliTests

open System
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
type LaunchCommandTests() =

    [<Test>]
    member _.``successful embedded launch reports terminal placement``() =
        // Writer callbacks are the side-effect boundary under test, so their observations are local mutation.
        let mutable output = []
        let mutable errors = []
        let result =
            Ok
                { Snapshot = EmbeddedTerminalSnapshot.empty
                  TerminalId = EmbeddedTerminalId "terminal-1" }

        let exitCode =
            writeLaunchResult
                (fun line -> output <- line :: output)
                (fun line -> errors <- line :: errors)
                result

        Assert.Multiple(fun () ->
            Assert.That(exitCode, Is.Zero)
            Assert.That(output, Is.EqualTo([ "✓ Agent launched in embedded terminal" ]))
            Assert.That(errors, Is.Empty))

    [<Test>]
    member _.``failed embedded launch reports the server error``() =
        // Writer callbacks are the side-effect boundary under test, so their observations are local mutation.
        let mutable output = []
        let mutable errors = []

        let exitCode =
            writeLaunchResult
                (fun line -> output <- line :: output)
                (fun line -> errors <- line :: errors)
                (Error "command delivery failed")

        Assert.Multiple(fun () ->
            Assert.That(exitCode, Is.EqualTo(1))
            Assert.That(output, Is.Empty)
            Assert.That(errors, Is.EqualTo([ "Error: command delivery failed" ])))

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
          State = PrState.Open
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
        let result = formatPr (HasPr { basePr with Id = 10; Title = "Done"; State = PrState.Merged })
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
                        State = PrState.Merged
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
type FormatEmbeddedTerminalReportTests() =

    let repo name worktrees : RepoWorktrees =
        { RepoId = RepoId name
          RootFolderName = name
          Worktrees = worktrees
          IsReady = true
          Provider = None
          BaseBranch = "main" }

    let tab id path activity lifecycle : EmbeddedTerminalTab =
        { Id = EmbeddedTerminalId id
          Worktree = WorktreePath path
          ReportedActivity = activity
          Lifecycle = lifecycle }

    [<Test>]
    member _.``empty registry reports no open embedded terminals``() =
        let lines = formatEmbeddedTerminalReport [] EmbeddedTerminalSnapshot.empty

        Assert.That(lines, Is.EqualTo([ "No embedded terminals open." ]))

    [<Test>]
    member _.``report maps tracked terminals to repositories without exposing attachment credentials``() =
        let path = WorktreePath "Q:\\code\\tm-feature"
        let worktree =
            { Tests.WorktreeFixtures.baseWt with
                Path = path
                Branch = "feature" }
        let endpoint = "http://127.0.0.1:54321/_treemon/terminal/secret-token/"
        let snapshot =
            { Tabs =
                [ tab
                      "terminal-1"
                      "q:\\code\\tm-feature"
                      (Some "Investigating\u001b[2J terminal titles")
                      (EmbeddedTerminalLifecycle.Running endpoint) ] }

        let lines =
            formatEmbeddedTerminalReport [ repo "treemon" [ worktree ] ] snapshot
        let rendered = lines |> String.concat Environment.NewLine

        Assert.Multiple(fun () ->
            Assert.That(rendered, Does.Contain("Open embedded terminals: 1"))
            Assert.That(
                rendered,
                Does.Contain(
                    "treemon | Q:\\code\\tm-feature | feature | Running | Investigating[2J terminal titles"))
            Assert.That(rendered, Does.Not.Contain("secret-token"))
            Assert.That(
                lines
                |> List.collect (Seq.filter Char.IsControl >> Seq.toList),
                Is.Empty))

    [<Test>]
    member _.``report preserves untracked and interrupted terminal diagnostics``() =
        let snapshot =
            { Tabs =
                [ tab
                      "terminal-2"
                      "Q:\\scratch\\terminal"
                      None
                      (EmbeddedTerminalLifecycle.Interrupted "Host exited\u001b[31m") ] }

        let lines = formatEmbeddedTerminalReport [] snapshot

        Assert.That(
            lines,
            Does.Contain(
                "(untracked) | Q:\\scratch\\terminal | (unknown) | Interrupted: Host exited[31m | (no reported title or intent)"))

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


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FormatDiffCategoryReportTests() =

    let coverage path count : DiffCategoryCoverage = { CategoryPath = path; FileCount = count }

    let linesOf report = formatDiffCategoryReport report |> fst

    let exitCodeOf report = formatDiffCategoryReport report |> snd

    let configured =
        DiffCategoryReport.Configured(
            [ coverage [ "Production code"; "Client" ] 8
              coverage [ "Production code"; "Server" ] 112
              coverage [ "Docs" ] 0 ],
            14
        )

    [<Test>]
    member _.``a missing configuration exits non-zero so the command works as a check``() =
        Assert.That(exitCodeOf DiffCategoryReport.Missing, Is.EqualTo(1))

    [<Test>]
    member _.``an invalid configuration exits non-zero and reports the validation reason``() =
        let reason = "categories sharing a parent need distinct names"
        let lines = linesOf (DiffCategoryReport.Invalid reason)

        Assert.That(exitCodeOf (DiffCategoryReport.Invalid reason), Is.EqualTo(1))
        Assert.That(String.Join("\n", lines), Does.Contain(reason))

    [<Test>]
    member _.``a configured repository exits zero even when a category matches nothing``() =
        // Exit status reports the configuration state; a zero-match leaf is surfaced in the output
        // instead, so a caller cannot mistake "configured but useless" for "not configured".
        Assert.That(exitCodeOf configured, Is.EqualTo(0))

    [<Test>]
    member _.``every declared category is listed with its count, in configuration order``() =
        let listed =
            linesOf configured
            |> List.filter (fun line -> line.StartsWith("  "))
            |> List.map (fun line -> line.Trim())

        Assert.That(
            listed,
            Is.EqualTo(
                [ "8  Production code > Client"
                  "112  Production code > Server"
                  "0  Docs  ⚠ matches no tracked file" ]),
            "counts are right-aligned and a zero-match category is flagged")

    [<Test>]
    member _.``the summary reports matched and unmatched totals``() =
        let summary = linesOf configured |> List.last

        Assert.That(summary, Does.Contain("120 of 134 tracked files matched"))
        Assert.That(summary, Does.Contain("14 unmatched"))

    [<Test>]
    member _.``a repository-authored category name cannot emit terminal control characters``() =
        let hostile = DiffCategoryReport.Configured([ coverage [ "Cli\u001b[2Jent" ] 1 ], 0)
        let rendered = linesOf hostile |> String.concat " "

        Assert.That(rendered |> Seq.filter Char.IsControl |> Seq.toList, Is.Empty)
        Assert.That(rendered, Does.Contain("Cli[2Jent"), "the name still renders, without the escape")


/// `tm state` rewrites the whole file, and the file holds several independent statements: what the
/// agent is on, what it last did, whether it is busy, and which repository its session belongs to.
/// Naming one must not erase the rest — losing the repository moves a session to another card, or
/// off every card, with nothing to see.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StateDeclarationMergeTests() =

    let nothing =
        { Label = None; Summary = None; Busy = None; Repo = None }

    let existing =
        { Label = Some "CEN-482"
          Summary = Some "Closed 3 tickets"
          Busy = Some true
          Repo = Some "git/Centro" }

    [<Test>]
    member _.``naming only the summary keeps the label, the repository and the busy flag``() =
        let merged = mergeDeclaration (Some existing) { nothing with Summary = Some "Rotated the creds" }

        Assert.Multiple(fun () ->
            Assert.That(merged.Summary, Is.EqualTo(Some "Rotated the creds"))
            Assert.That(merged.Label, Is.EqualTo(Some "CEN-482"))
            Assert.That(merged.Repo, Is.EqualTo(Some "git/Centro"))
            Assert.That(merged.Busy, Is.EqualTo(Some true)))

    [<Test>]
    member _.``a named value replaces the one that was there``() =
        let merged = mergeDeclaration (Some existing) { nothing with Repo = Some "git/Clabe"; Busy = Some false }

        Assert.Multiple(fun () ->
            Assert.That(merged.Repo, Is.EqualTo(Some "git/Clabe"))
            Assert.That(merged.Busy, Is.EqualTo(Some false), "false is a value, not an absence")
            Assert.That(merged.Label, Is.EqualTo(Some "CEN-482")))

    // Blank is how a shell passes "I did not really mean this", so it carries forward rather than
    // erasing — clearing a field is not something this command offers.
    [<Test>]
    member _.``a blank value is the same as naming nothing``() =
        let merged = mergeDeclaration (Some existing) { nothing with Label = Some "   " }

        Assert.That(merged.Label, Is.EqualTo(Some "CEN-482"))

    [<Test>]
    member _.``surrounding whitespace is not part of what is declared``() =
        let merged = mergeDeclaration None { nothing with Repo = Some "  git/Centro  " }

        Assert.That(merged.Repo, Is.EqualTo(Some "git/Centro"))

    [<Test>]
    member _.``a folder with nothing already declared keeps only what it is given``() =
        let merged = mergeDeclaration None { nothing with Repo = Some "git/Centro" }

        Assert.Multiple(fun () ->
            Assert.That(merged.Repo, Is.EqualTo(Some "git/Centro"))
            Assert.That(merged.Label, Is.EqualTo None, "a folder above its repositories wants no card")
            Assert.That(merged.Busy, Is.EqualTo None))
