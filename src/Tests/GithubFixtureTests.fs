module Tests.GithubFixtureTests

open System.IO
open NUnit.Framework
open Server.GithubPrStatus
open Shared

let private fixtureDir =
    Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "fixtures", "github")
    |> Path.GetFullPath

let private readFixture (name: string) =
    File.ReadAllText(Path.Combine(fixtureDir, name))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParsePrListFixtureTests() =

    [<Test>]
    member _.``Parses two open PRs from fixture``() =
        let prs = readFixture "pr-list.json" |> parsePrList
        Assert.That(prs.Length, Is.EqualTo(2))

    [<Test>]
    member _.``Extracts branch name from head ref``() =
        let prs = readFixture "pr-list.json" |> parsePrList
        let branches = prs |> List.map _.BranchName
        Assert.That(branches, Does.Contain("test/add-editorconfig"))
        Assert.That(branches, Does.Contain("test/add-health-endpoint"))

    [<Test>]
    member _.``Extracts PR number``() =
        let prs = readFixture "pr-list.json" |> parsePrList
        let numbers = prs |> List.map _.PrNumber
        Assert.That(numbers, Does.Contain(1))
        Assert.That(numbers, Does.Contain(2))

    [<Test>]
    member _.``Extracts title``() =
        let prs = readFixture "pr-list.json" |> parsePrList
        let pr1 = prs |> List.find (fun pr -> pr.PrNumber = 1)
        Assert.That(pr1.Title, Is.EqualTo("Add contributing guide and CI workflow"))

    [<Test>]
    member _.``Open PRs are not merged``() =
        let prs = readFixture "pr-list.json" |> parsePrList
        Assert.That(prs |> List.forall (fun pr -> not pr.IsMerged), Is.True)

    [<Test>]
    member _.``Closed PR with merged_at is marked as merged``() =
        let prs = readFixture "pr-list-with-closed.json" |> parsePrList
        let merged = prs |> List.find (fun pr -> pr.PrNumber = 3)
        Assert.That(merged.IsMerged, Is.True)

    [<Test>]
    member _.``Draft PR has IsDraft set``() =
        let prs = readFixture "pr-list-with-closed.json" |> parsePrList
        let draft = prs |> List.find (fun pr -> pr.PrNumber = 4)
        Assert.That(draft.IsDraft, Is.True)

    [<Test>]
    member _.``Non-draft PR has IsDraft false``() =
        let prs = readFixture "pr-list-with-closed.json" |> parsePrList
        let nonDraft = prs |> List.find (fun pr -> pr.PrNumber = 3)
        Assert.That(nonDraft.IsDraft, Is.False)

    [<Test>]
    member _.``Empty array returns empty list``() =
        let prs = parsePrList "[]"
        Assert.That(prs, Is.Empty)

    [<Test>]
    member _.``Invalid JSON returns empty list``() =
        let prs = parsePrList "not json"
        Assert.That(prs, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseActionRunsFixtureTests() =

    [<Test>]
    member _.``Parses two workflow runs from fixture``() =
        let runs = readFixture "actions-runs.json" |> parseActionRuns
        Assert.That(runs.Length, Is.EqualTo(2))

    [<Test>]
    member _.``Failed run has Failed status``() =
        let runs = readFixture "actions-runs.json" |> parseActionRuns
        let failed = runs |> List.find (fun (info, _) -> info.Name = "CI")
        Assert.That(fst failed |> _.Status, Is.EqualTo(BuildStatus.Failed))

    [<Test>]
    member _.``Successful run has Succeeded status``() =
        let runs = readFixture "actions-runs.json" |> parseActionRuns
        let succeeded = runs |> List.find (fun (info, _) -> info.Name = "Deploy")
        Assert.That(fst succeeded |> _.Status, Is.EqualTo(BuildStatus.Succeeded))

    [<Test>]
    member _.``Run includes html_url``() =
        let runs = readFixture "actions-runs.json" |> parseActionRuns
        let ci = runs |> List.find (fun (info, _) -> info.Name = "CI")
        Assert.That((fst ci).Url, Is.EqualTo(Some "https://github.com/testowner/testrepo/actions/runs/22279694651"))

    [<Test>]
    member _.``Run includes run ID``() =
        let runs = readFixture "actions-runs.json" |> parseActionRuns
        let ci = runs |> List.find (fun (info, _) -> info.Name = "CI")
        Assert.That(snd ci, Is.EqualTo(Some 22279694651L))

    [<Test>]
    member _.``In-progress run has Building status``() =
        let runs = readFixture "actions-runs-in-progress.json" |> parseActionRuns
        Assert.That(runs.Length, Is.EqualTo(1))
        Assert.That(fst runs[0] |> _.Status, Is.EqualTo(BuildStatus.Building))

    [<Test>]
    member _.``Empty workflow_runs returns empty list``() =
        let runs = readFixture "actions-runs-empty.json" |> parseActionRuns
        Assert.That(runs, Is.Empty)

    [<Test>]
    member _.``Invalid JSON returns empty list``() =
        let runs = parseActionRuns "not json"
        Assert.That(runs, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseFailedJobsFixtureTests() =

    [<Test>]
    member _.``Finds failed step name from fixture``() =
        let result = readFixture "actions-jobs-failed.json" |> parseFailedJobs
        Assert.That(result, Is.EqualTo(Some "Test"))

    [<Test>]
    member _.``All-success jobs returns None``() =
        let result = readFixture "actions-jobs-success.json" |> parseFailedJobs
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Invalid JSON returns None``() =
        let result = parseFailedJobs "not json"
        Assert.That(result, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParsePrCommentCountsTests() =

    [<Test>]
    member _.``Sums comments and review_comments from PR detail``() =
        let count = readFixture "pr-detail.json" |> parsePrCommentCounts
        Assert.That(count, Is.EqualTo(4))

    [<Test>]
    member _.``Returns zero for invalid JSON``() =
        let count = parsePrCommentCounts "not json"
        Assert.That(count, Is.EqualTo(0))

    [<Test>]
    member _.``Returns zero when fields are missing``() =
        let count = parsePrCommentCounts """{"number": 1}"""
        Assert.That(count, Is.EqualTo(0))
