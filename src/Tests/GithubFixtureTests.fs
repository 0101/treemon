module Tests.GithubFixtureTests

open System.IO
open NUnit.Framework
open Server.GithubPrStatus
open Server.PrOpenState
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
    member _.``Extracts immutable head SHA``() =
        let prs = readFixture "pr-list-with-closed.json" |> parsePrList
        let merged = prs |> List.find (fun pr -> pr.PrNumber = 3)
        Assert.That(merged.HeadSha, Is.EqualTo(Some "sha-merged-branch"))

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
type GithubFirstPerBranchFixtureTests() =

    /// Mirrors fetchGithubPrStatuses: open PRs first, then closed PRs newest-updated first.
    let fetchedPrs () =
        (readFixture "pr-list-branch-reuse-open.json" |> parsePrList)
        @ (readFixture "pr-list-branch-reuse-closed.json" |> parsePrList)

    [<Test>]
    member _.``One PR per branch is kept``() =
        let result = fetchedPrs () |> firstPerBranch
        let branches = result |> List.map _.BranchName
        Assert.That(branches, Is.EquivalentTo([ "feature/reopened-work"; "feature/reused-name" ]))

    [<Test>]
    member _.``Newer merged PR wins over older closed-unmerged PR on a reused branch``() =
        let result = fetchedPrs () |> firstPerBranch
        let pr = result |> List.find (fun pr -> pr.BranchName = "feature/reused-name")
        Assert.That(pr.PrNumber, Is.EqualTo(20))
        Assert.That(pr.IsMerged, Is.True)

    [<Test>]
    member _.``Open PR wins over merged PR on the same branch``() =
        let result = fetchedPrs () |> firstPerBranch
        let pr = result |> List.find (fun pr -> pr.BranchName = "feature/reopened-work")
        Assert.That(pr.PrNumber, Is.EqualTo(21))
        Assert.That(pr.IsMerged, Is.False)

    [<Test>]
    member _.``Filtering to known branches keeps the merged winner``() =
        let result = fetchedPrs () |> filterRelevantPrs (set [ "feature/reused-name" ])
        Assert.That(result, Has.Exactly(1).Items)
        Assert.That(result[0].PrNumber, Is.EqualTo(20))
        Assert.That(result[0].IsMerged, Is.True)


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
type ParseReviewThreadsTests() =

    [<Test>]
    member _.``Counts unresolved and total threads from fixture``() =
        let result = readFixture "review-threads.json" |> parseReviewThreads
        Assert.That(result, Is.EqualTo(CommentSummary.WithResolution(2, 5)))

    [<Test>]
    member _.``Empty threads returns WithResolution(0, 0)``() =
        let json = """{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[]}}}}}"""
        let result = parseReviewThreads json
        Assert.That(result, Is.EqualTo(CommentSummary.WithResolution(0, 0)))

    [<Test>]
    member _.``All resolved threads returns WithResolution(0, N)``() =
        let json = """{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[{"isResolved":true},{"isResolved":true},{"isResolved":true}]}}}}}"""
        let result = parseReviewThreads json
        Assert.That(result, Is.EqualTo(CommentSummary.WithResolution(0, 3)))

    [<Test>]
    member _.``Invalid JSON returns WithResolution(0, 0)``() =
        let result = parseReviewThreads "not json"
        Assert.That(result, Is.EqualTo(CommentSummary.WithResolution(0, 0)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParsePrMergeabilityTests() =

    [<Test>]
    member _.``PR with mergeable false has conflicts``() =
        let result = readFixture "pr-detail-conflicts.json" |> parsePrMergeability
        Assert.That(result, Is.True)

    [<Test>]
    member _.``PR with mergeable true has no conflicts``() =
        let result = readFixture "pr-detail-mergeable.json" |> parsePrMergeability
        Assert.That(result, Is.False)

    [<Test>]
    member _.``PR with mergeable null has no conflicts``() =
        let result = readFixture "pr-detail-null-mergeable.json" |> parsePrMergeability
        Assert.That(result, Is.False)

    [<Test>]
    member _.``Invalid JSON returns false``() =
        let result = parsePrMergeability "not json"
        Assert.That(result, Is.False)

    [<Test>]
    member _.``JSON without mergeable field returns false``() =
        let result = parsePrMergeability """{"number": 1, "title": "test"}"""
        Assert.That(result, Is.False)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type GithubOpenPrQueryTests() =

    let remote = { Owner = "testowner"; Repo = "testrepo" }
    let branch = "feature/sync"
    let queryPath () = openPrQueryArgs remote branch |> List.last

    [<Test>]
    member _.``Query asks GitHub for open pull requests headed by the branch``() =
        let path = queryPath ()
        Assert.That(path, Does.Contain("/repos/testowner/testrepo/pulls?"))
        Assert.That(path, Does.Contain("state=open"))
        Assert.That(path, Does.Contain("head=testowner:feature%2Fsync"))

    [<Test>]
    member _.``Query asks for no comments, builds, or mergeability``() =
        let args = openPrQueryArgs remote branch
        let command = String.concat " " args
        Assert.That(command, Does.Not.Contain("graphql"))
        Assert.That(command, Does.Not.Contain("actions"))
        Assert.That(command, Does.Not.Match(@"/pulls/\d"))
        Assert.That(queryPath (), Does.Contain("per_page=1"))

    [<Test>]
    member _.``An open pull request on the branch is reported as open``() =
        let state = readFixture "open-pr-for-branch.json" |> parseOpenPrState branch
        Assert.That(state, Is.EqualTo(OpenPr))

    [<Test>]
    member _.``An empty response is a confirmed absence``() =
        let state = parseOpenPrState branch "[]"
        Assert.That(state, Is.EqualTo(NoOpenPr))

    [<Test>]
    member _.``An unparseable response leaves the state unknown``() =
        let state = parseOpenPrState branch "not json"
        Assert.That(state, Is.EqualTo(UnknownPrState))

    [<Test>]
    member _.``An error object instead of a pull request list leaves the state unknown``() =
        let state = parseOpenPrState branch """{"message":"Not Found"}"""
        Assert.That(state, Is.EqualTo(UnknownPrState))

    [<Test>]
    member _.``A response naming other branches leaves the state unknown``() =
        let state = readFixture "pr-list.json" |> parseOpenPrState branch
        Assert.That(state, Is.EqualTo(UnknownPrState))

    [<Test>]
    member _.``A pull request without a head branch leaves the state unknown``() =
        let state = parseOpenPrState branch """[{"number":7}]"""
        Assert.That(state, Is.EqualTo(UnknownPrState))

