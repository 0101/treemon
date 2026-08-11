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
    member _.``Open PRs are parsed as open``() =
        let prs = readFixture "pr-list.json" |> parsePrList
        Assert.That(prs |> List.forall (fun pr -> pr.State = PrState.Open), Is.True)

    [<Test>]
    member _.``Closed PR with merged_at is marked as merged``() =
        let prs = readFixture "pr-list-with-closed.json" |> parsePrList
        let merged = prs |> List.find (fun pr -> pr.PrNumber = 3)
        Assert.That(merged.State, Is.EqualTo(PrState.Merged))

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
    member _.``PR with auto_merge object has AutoMergeEnabled set``() =
        let prs = readFixture "pr-list.json" |> parsePrList
        let autoMerging = prs |> List.find (fun pr -> pr.PrNumber = 1)
        Assert.That(autoMerging.AutoMergeEnabled, Is.True)

    [<Test>]
    member _.``PR without auto_merge property has AutoMergeEnabled false``() =
        let prs = readFixture "pr-list.json" |> parsePrList
        let plain = prs |> List.find (fun pr -> pr.PrNumber = 2)
        Assert.That(plain.AutoMergeEnabled, Is.False)

    [<Test>]
    member _.``PR with null auto_merge has AutoMergeEnabled false``() =
        let prs = readFixture "pr-list-with-closed.json" |> parsePrList
        let draft = prs |> List.find (fun pr -> pr.PrNumber = 4)
        Assert.That(draft.AutoMergeEnabled, Is.False)

    [<Test>]
    member _.``Merged PR keeps AutoMergeEnabled false despite auto_merge object``() =
        let prs = readFixture "pr-list-with-closed.json" |> parsePrList
        let merged = prs |> List.find (fun pr -> pr.PrNumber = 3)
        Assert.That(merged.AutoMergeEnabled, Is.False)

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
type GithubHeadOwnerFixtureTests() =

    let forkPrs () = readFixture "pr-list-fork.json" |> parsePrList

    /// The repo's own owner plus a colleague whose fork is a configured remote.
    let knownOwners = set [ "contoso"; "colleague" ]

    [<Test>]
    member _.``The head owner is read from the head repository``() =
        let owners = forkPrs () |> List.map _.HeadOwner
        Assert.That(owners, Is.EquivalentTo([ Some "Contoso"; Some "colleague"; Some "outsider"; None ]))

    [<Test>]
    member _.``An outsider's fork PR cannot speak for a local branch of the same name``() =
        let kept = forkPrs () |> fromKnownOwners knownOwners

        Assert.Multiple(fun () ->
            Assert.That(
                kept |> List.map _.PrNumber,
                Is.EquivalentTo([ 30; 31 ]),
                "only heads this checkout could push to may decide a branch's PR status")
            Assert.That(
                kept |> List.exists (fun pr -> pr.BranchName = "feature/shared-name" && pr.PrNumber = 32),
                Is.False,
                "the outsider PR reuses a local branch name and would otherwise open the push gate"))

    [<Test>]
    member _.``Owner matching ignores login case``() =
        // The fixture's upstream head owner is "Contoso"; GitHub logins are case-insensitive.
        let kept = forkPrs () |> fromKnownOwners (set [ "contoso" ]) |> List.map _.PrNumber
        Assert.That(kept, Is.EquivalentTo([ 30 ]))

    [<Test>]
    member _.``A pull request whose head repository is gone is excluded``() =
        let kept = forkPrs () |> fromKnownOwners knownOwners
        Assert.That(
            kept |> List.exists (fun pr -> pr.PrNumber = 33),
            Is.False,
            "a deleted fork head cannot be attributed to an owner, so it cannot be trusted either")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type GithubFirstPerBranchFixtureTests() =

    /// Mirrors fetchGithubPrStatuses: open PRs first, then closed PRs newest-updated first.
    let fetchedPrs () =
        (readFixture "pr-list-branch-reuse-open.json" |> parsePrList)
        @ (readFixture "pr-list-branch-reuse-closed.json" |> parsePrList)

    [<Test>]
    member _.``A closed pull request without a merge is parsed as closed unmerged``() =
        let prs = readFixture "pr-list-branch-reuse-closed.json" |> parsePrList
        let abandoned = prs |> List.find (fun pr -> pr.PrNumber = 18)
        // The state git cannot infer from `merged_at` alone, and the one the push gate must not
        // mistake for an open pull request.
        Assert.That(abandoned.State, Is.EqualTo(PrState.ClosedUnmerged))

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
        Assert.That(pr.State, Is.EqualTo(PrState.Merged))

    [<Test>]
    member _.``Open PR wins over merged PR on the same branch``() =
        let result = fetchedPrs () |> firstPerBranch
        let pr = result |> List.find (fun pr -> pr.BranchName = "feature/reopened-work")
        Assert.That(pr.PrNumber, Is.EqualTo(21))
        Assert.That(pr.State, Is.EqualTo(PrState.Open))

    [<Test>]
    member _.``Filtering to known branches keeps the merged winner``() =
        let result = fetchedPrs () |> filterRelevantPrs (set [ "feature/reused-name" ])
        Assert.That(result, Has.Exactly(1).Items)
        Assert.That(result[0].PrNumber, Is.EqualTo(20))
        Assert.That(result[0].State, Is.EqualTo(PrState.Merged))


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
