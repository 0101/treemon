module Tests.AzDoFixtureTests

open System
open System.IO
open NUnit.Framework
open Server.PrStatus
open Shared

let private fixtureDir =
    Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "fixtures", "azdo")
    |> Path.GetFullPath

let private readFixture (name: string) =
    File.ReadAllText(Path.Combine(fixtureDir, name))

let private testRemote =
    { Org = "testorg"; Project = "testproject"; Repo = "testrepo" }


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParsePrListFixtureTests() =

    [<Test>]
    member _.``Parses three PRs from fixture``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        Assert.That(prs.Length, Is.EqualTo(3))

    [<Test>]
    member _.``Extracts branch names from sourceRefName stripping refs/heads/``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let branches = prs |> List.map _.BranchName
        Assert.That(branches, Does.Contain("feature/auth"))
        Assert.That(branches, Does.Contain("fix/login-style"))
        Assert.That(branches, Does.Contain("refactor/data-layer"))

    [<Test>]
    member _.``Extracts repository GUID``() =
        let repoGuid, _ = readFixture "pr-list.json" |> parsePrList
        Assert.That(repoGuid, Is.EqualTo(Some "abc12345-def6-7890-abcd-ef1234567890"))

    [<Test>]
    member _.``Active PR is not merged``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let active = prs |> List.find (fun pr -> pr.PrId = 101)
        Assert.That(active.IsMerged, Is.False)

    [<Test>]
    member _.``Completed PR is marked as merged``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let completed = prs |> List.find (fun pr -> pr.PrId = 100)
        Assert.That(completed.IsMerged, Is.True)

    [<Test>]
    member _.``Draft PR has IsDraft set``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let draft = prs |> List.find (fun pr -> pr.PrId = 102)
        Assert.That(draft.IsDraft, Is.True)

    [<Test>]
    member _.``Completed PR has closedDate parsed``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let completed = prs |> List.find (fun pr -> pr.PrId = 100)
        Assert.That(completed.ClosedDate.IsSome, Is.True)
        Assert.That(completed.ClosedDate.Value.Year, Is.EqualTo(2026))

    [<Test>]
    member _.``Active PR has no closedDate``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let active = prs |> List.find (fun pr -> pr.PrId = 101)
        Assert.That(active.ClosedDate, Is.EqualTo(None))

    [<Test>]
    member _.``PR with mergeStatus conflicts has HasConflicts true``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let conflicting = prs |> List.find (fun pr -> pr.PrId = 101)
        Assert.That(conflicting.HasConflicts, Is.True)

    [<Test>]
    member _.``PR with mergeStatus succeeded has HasConflicts false``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let clean = prs |> List.find (fun pr -> pr.PrId = 102)
        Assert.That(clean.HasConflicts, Is.False)

    [<Test>]
    member _.``PR without mergeStatus field has HasConflicts false``() =
        let _, prs = readFixture "pr-list.json" |> parsePrList
        let completed = prs |> List.find (fun pr -> pr.PrId = 100)
        Assert.That(completed.HasConflicts, Is.False)

    [<Test>]
    member _.``Empty array returns empty list and no GUID``() =
        let guid, prs = parsePrList "[]"
        Assert.That(prs, Is.Empty)
        Assert.That(guid, Is.EqualTo(None))

    [<Test>]
    member _.``Invalid JSON returns empty list``() =
        let _, prs = parsePrList "not json"
        Assert.That(prs, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseThreadCountsFixtureTests() =

    [<Test>]
    member _.``Counts unresolved threads as active plus pending``() =
        let (WithResolution(unresolved, _)) = readFixture "threads.json" |> parseThreadCounts
        Assert.That(unresolved, Is.EqualTo(3))

    [<Test>]
    member _.``Excludes deleted threads from total``() =
        let (WithResolution(_, total)) = readFixture "threads.json" |> parseThreadCounts
        Assert.That(total, Is.EqualTo(5))

    [<Test>]
    member _.``Excludes threads without status from total``() =
        let (WithResolution(_, total)) = readFixture "threads.json" |> parseThreadCounts
        Assert.That(total, Is.EqualTo(5))

    [<Test>]
    member _.``Invalid JSON returns zero counts``() =
        let result = parseThreadCounts "not json"
        Assert.That(result, Is.EqualTo(WithResolution(0, 0)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseBuildsFixtureTests() =

    [<Test>]
    member _.``Parses builds keeping latest per definition``() =
        let builds = readFixture "builds.json" |> parseBuilds testRemote
        Assert.That(builds.Length, Is.EqualTo(3))

    [<Test>]
    member _.``Failed build has Failed status``() =
        let builds = readFixture "builds.json" |> parseBuilds testRemote
        let failed = builds |> List.find (fun (info, _) -> info.Name = "PR Validation")
        Assert.That((fst failed).Status, Is.EqualTo(BuildStatus.Failed))

    [<Test>]
    member _.``Succeeded build has Succeeded status``() =
        let builds = readFixture "builds.json" |> parseBuilds testRemote
        let succeeded = builds |> List.find (fun (info, _) -> info.Name = "Code Analysis")
        Assert.That((fst succeeded).Status, Is.EqualTo(BuildStatus.Succeeded))

    [<Test>]
    member _.``In-progress build has Building status``() =
        let builds = readFixture "builds.json" |> parseBuilds testRemote
        let building = builds |> List.find (fun (info, _) -> info.Name = "Integration Tests")
        Assert.That((fst building).Status, Is.EqualTo(BuildStatus.Building))

    [<Test>]
    member _.``Build URL is constructed from org, project, and build ID``() =
        let builds = readFixture "builds.json" |> parseBuilds testRemote
        let failed = builds |> List.find (fun (info, _) -> info.Name = "PR Validation")
        Assert.That((fst failed).Url, Is.EqualTo(Some "https://dev.azure.com/testorg/testproject/_build/results?buildId=5001"))

    [<Test>]
    member _.``Invalid JSON returns empty list``() =
        let builds = parseBuilds testRemote "not json"
        Assert.That(builds, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseFailedStepFixtureTests() =

    [<Test>]
    member _.``Finds failed task step name and log ID``() =
        let result = readFixture "build-timeline.json" |> parseFailedStep
        match result with
        | Some(stepName, logId) ->
            Assert.That(stepName, Is.EqualTo("Run unit tests"))
            Assert.That(logId, Is.EqualTo(42))
        | None -> Assert.Fail("Expected Some result")

    [<Test>]
    member _.``Invalid JSON returns None``() =
        let result = parseFailedStep "not json"
        Assert.That(result, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseBuildLogFixtureTests() =

    [<Test>]
    member _.``Parses log lines from fixture``() =
        let result = readFixture "build-log.json" |> parseBuildLog
        Assert.That(result.IsSome, Is.True)

    [<Test>]
    member _.``Log lines are joined with newlines``() =
        let result = readFixture "build-log.json" |> parseBuildLog
        let text = result.Value
        Assert.That(text, Does.Contain("Starting test execution"))
        Assert.That(text, Does.Contain("Failed!"))

    [<Test>]
    member _.``Timestamp prefix is trimmed from log lines``() =
        let result = readFixture "build-log.json" |> parseBuildLog
        let text = result.Value
        Assert.That(text, Does.Not.Contain("2026-02-22T15:13:38"))

    [<Test>]
    member _.``Invalid JSON returns None``() =
        let result = parseBuildLog "not json"
        Assert.That(result, Is.EqualTo(None))
