module Tests.ArchiveTests

open System
open System.IO
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Server.TreemonConfig
open Navigation
open Shared

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TreemonConfigReadTests() =

    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-test-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, recursive = true)

    [<Test>]
    member _.``readArchivedBranches from missing file returns empty list``() =
        let result = readArchivedBranches tempDir
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``readArchivedBranches from file without archivedBranches field returns empty list``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "codingTool": "claude" }""")

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``readArchivedBranches from file with archivedBranches returns correct list``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": ["feature-a", "feature-b", "old-branch"] }""")

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.EqualTo([ "feature-a"; "feature-b"; "old-branch" ]))

    [<Test>]
    member _.``readArchivedBranches ignores non-string array elements``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": ["valid", 42, null, "also-valid"] }""")

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.EqualTo([ "valid"; "also-valid" ]))

    [<Test>]
    member _.``readArchivedBranches returns empty for invalid JSON``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """not json at all""")

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``readArchivedBranches returns empty when archivedBranches is not an array``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": "not-an-array" }""")

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TreemonConfigWriteTests() =

    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-test-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, recursive = true)

    [<Test>]
    member _.``setArchivedBranches creates file when missing``() =
        setArchivedBranches tempDir [ "branch-a"; "branch-b" ]

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.EqualTo([ "branch-a"; "branch-b" ]))

    [<Test>]
    member _.``setArchivedBranches preserves other fields via round-trip``() =
        let configPath = Path.Combine(tempDir, ".treemon.json")
        File.WriteAllText(
            configPath,
            """{ "codingTool": "claude", "testSolution": "src/Tests/Tests.fsproj" }""")

        setArchivedBranches tempDir [ "archived-1" ]

        let json = File.ReadAllText(configPath)
        use doc = System.Text.Json.JsonDocument.Parse(json)

        let codingTool =
            match doc.RootElement.TryGetProperty("codingTool") with
            | true, elem -> elem.GetString()
            | _ -> ""

        let testSolution =
            match doc.RootElement.TryGetProperty("testSolution") with
            | true, elem -> elem.GetString()
            | _ -> ""

        Assert.That(codingTool, Is.EqualTo("claude"), "codingTool should be preserved")
        Assert.That(testSolution, Is.EqualTo("src/Tests/Tests.fsproj"), "testSolution should be preserved")

        let archived = readArchivedBranches tempDir
        Assert.That(archived, Is.EqualTo([ "archived-1" ]))

    [<Test>]
    member _.``setArchivedBranches overwrites existing archivedBranches``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": ["old-branch"] }""")

        setArchivedBranches tempDir [ "new-branch-1"; "new-branch-2" ]

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.EqualTo([ "new-branch-1"; "new-branch-2" ]))

    [<Test>]
    member _.``setArchivedBranches with empty list writes empty array``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": ["something"] }""")

        setArchivedBranches tempDir []

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OrphanCleanupTests() =

    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-test-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, recursive = true)

    [<Test>]
    member _.``Orphan cleanup removes branches not in worktree list``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": ["active-branch", "deleted-branch", "also-deleted"] }""")

        let liveBranches = Set.ofList [ "active-branch"; "main"; "feature-x" ]
        let archivedBranches = readArchivedBranches tempDir |> Set.ofList
        let cleanedArchived = Set.intersect archivedBranches liveBranches

        setArchivedBranches tempDir (Set.toList cleanedArchived)

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.EqualTo([ "active-branch" ]))

    [<Test>]
    member _.``Orphan cleanup with no overlap removes all archived branches``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": ["gone-a", "gone-b"] }""")

        let liveBranches = Set.ofList [ "main"; "feature" ]
        let archivedBranches = readArchivedBranches tempDir |> Set.ofList
        let cleanedArchived = Set.intersect archivedBranches liveBranches

        setArchivedBranches tempDir (Set.toList cleanedArchived)

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Orphan cleanup preserves all when all archived branches are live``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "codingTool": "copilot", "archivedBranches": ["feature-a", "feature-b"] }""")

        let liveBranches = Set.ofList [ "main"; "feature-a"; "feature-b" ]
        let archivedBranches = readArchivedBranches tempDir |> Set.ofList
        let cleanedArchived = Set.intersect archivedBranches liveBranches

        if cleanedArchived <> archivedBranches then
            setArchivedBranches tempDir (Set.toList cleanedArchived)

        let result = readArchivedBranches tempDir
        Assert.That(result, Is.EqualTo([ "feature-a"; "feature-b" ]))

        let json = File.ReadAllText(Path.Combine(tempDir, ".treemon.json"))
        use doc = System.Text.Json.JsonDocument.Parse(json)
        let codingTool =
            match doc.RootElement.TryGetProperty("codingTool") with
            | true, elem -> elem.GetString()
            | _ -> ""
        Assert.That(codingTool, Is.EqualTo("copilot"), "Other fields preserved when no orphan cleanup needed")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type NavigationArchiveTests() =

    let makeRepoModel repoId worktrees archivedWorktrees =
        { RepoId = RepoId repoId
          Name = repoId
          Worktrees = worktrees
          ArchivedWorktrees = archivedWorktrees
          IsReady = true
          IsCollapsed = false
          Provider = None }

    let makeWorktreeStatus branch isArchived : WorktreeStatus =
        { Path = WorktreePath.create $"/repo/{branch}"
          Branch = branch
          LastCommitMessage = "test"
          LastCommitTime = DateTimeOffset.UtcNow
          Beads = BeadsSummary.zero
          CodingTool = CodingToolStatus.Idle
          CodingToolProvider = None
          LastUserMessage = None
          Pr = NoPr
          MainBehindCount = 0
          IsDirty = false
          WorkMetrics = None
          HasActiveSession = false
          IsArchived = isArchived }

    [<Test>]
    member _.``visibleFocusTargets excludes archived worktrees``() =
        let active1 = makeWorktreeStatus "main" false
        let active2 = makeWorktreeStatus "feature-x" false
        let archived1 = makeWorktreeStatus "old-branch" true
        let archived2 = makeWorktreeStatus "stale-feature" true

        let repos =
            [ makeRepoModel "TestRepo" [ active1; active2 ] [ archived1; archived2 ] ]

        let targets = visibleFocusTargets repos

        let targetStrings =
            targets
            |> List.map (function
                | RepoHeader rid -> $"header:{RepoId.value rid}"
                | Card key -> $"card:{key}")

        Assert.That(targetStrings, Does.Contain("header:TestRepo"))
        Assert.That(targetStrings, Does.Contain("card:TestRepo/main"))
        Assert.That(targetStrings, Does.Contain("card:TestRepo/feature-x"))
        Assert.That(targetStrings, Does.Not.Contain("card:TestRepo/old-branch"))
        Assert.That(targetStrings, Does.Not.Contain("card:TestRepo/stale-feature"))

    [<Test>]
    member _.``visibleFocusTargets with only archived worktrees shows only header``() =
        let archived = makeWorktreeStatus "archived-only" true

        let repos =
            [ makeRepoModel "TestRepo" [] [ archived ] ]

        let targets = visibleFocusTargets repos

        Assert.That(targets, Is.EqualTo([ RepoHeader (RepoId "TestRepo") ]))

    [<Test>]
    member _.``repoNavSections excludes archived worktrees from cards``() =
        let active = makeWorktreeStatus "main" false
        let archived = makeWorktreeStatus "old-branch" true

        let repos =
            [ makeRepoModel "TestRepo" [ active ] [ archived ] ]

        let sections = repoNavSections repos

        Assert.That(sections.Length, Is.EqualTo(1))
        Assert.That(sections[0].Cards, Is.EqualTo([ Card "TestRepo/main" ]))

    [<Test>]
    member _.``visibleFocusTargets with collapsed repo containing archived worktrees shows only header``() =
        let active = makeWorktreeStatus "main" false
        let archived = makeWorktreeStatus "old" true

        let repos =
            [ { makeRepoModel "TestRepo" [ active ] [ archived ] with IsCollapsed = true } ]

        let targets = visibleFocusTargets repos

        Assert.That(targets, Is.EqualTo([ RepoHeader (RepoId "TestRepo") ]))

    [<Test>]
    member _.``visibleFocusTargets across multiple repos excludes archived from all``() =
        let repo1Active = makeWorktreeStatus "main" false
        let repo1Archived = makeWorktreeStatus "old-1" true
        let repo2Active = makeWorktreeStatus "develop" false
        let repo2Archived = makeWorktreeStatus "old-2" true

        let repos =
            [ makeRepoModel "Repo1" [ repo1Active ] [ repo1Archived ]
              makeRepoModel "Repo2" [ repo2Active ] [ repo2Archived ] ]

        let targets = visibleFocusTargets repos

        let cardKeys =
            targets
            |> List.choose (function Card key -> Some key | _ -> None)

        Assert.That(cardKeys, Is.EqualTo([ "Repo1/main"; "Repo2/develop" ]))


[<TestFixture>]
[<Category("E2E")>]
[<Category("Fast")>]
type ArchiveE2ETests() =
    inherit PageTest()

    let baseUrl = ServerFixture.viteUrl

    let makeWorktreeJson (branch: string) (isArchived: bool) =
        let archived = if isArchived then "true" else "false"
        $"""{{
            "Path":{{"WorktreePath":"Q:/test/{branch}"}},"Branch":"{branch}",
            "LastCommitMessage":"test commit","LastCommitTime":"2026-02-16T22:30:00+00:00",
            "Beads":{{"Open":0,"InProgress":0,"Closed":0}},
            "CodingTool":"Idle","CodingToolProvider":null,"LastUserMessage":null,
            "Pr":"NoPr","MainBehindCount":0,"IsDirty":false,
            "WorkMetrics":null,"HasActiveSession":false,"IsArchived":{archived}
        }}"""

    let makeDashboardJson (worktrees: string list) =
        let wts = worktrees |> String.concat ","
        $"""{{"Repos":[{{"RepoId":{{"RepoId":"TestRepo"}},"RootFolderName":"TestRepo","Worktrees":[{wts}],"IsReady":true}}],"SchedulerEvents":[],"LatestByCategory":{{}},"AppVersion":"test","EditorName":""}}"""

    let emptySyncStatus = "{}"

    let setupMockedPage (page: IPage) (getWorktreesJson: unit -> string) (archiveCalls: System.Collections.Generic.List<string>) (unarchiveCalls: System.Collections.Generic.List<string>) =
        task {
            do! page.RouteAsync("**/IWorktreeApi/getWorktrees", fun route ->
                let json = getWorktreesJson ()
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = json)))

            do! page.RouteAsync("**/IWorktreeApi/getSyncStatus", fun route ->
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = emptySyncStatus)))

            do! page.RouteAsync("**/IWorktreeApi/archiveWorktree", fun route ->
                let body = route.Request.PostData
                archiveCalls.Add(if body = null then "" else body)
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = "\"Ok\"")))

            do! page.RouteAsync("**/IWorktreeApi/unarchiveWorktree", fun route ->
                let body = route.Request.PostData
                unarchiveCalls.Add(if body = null then "" else body)
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = "\"Ok\"")))
        }

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    [<Test>]
    member this.``Active worktree cards have archive button``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "feature-a" false
                makeWorktreeJson "feature-b" false
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let archiveBtns = page.Locator(".wt-card .archive-btn")
            do! archiveBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = archiveBtns.CountAsync()
            Assert.That(count, Is.EqualTo(2), "Each active card should have an archive button")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Archived worktrees render in archive section``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "active-branch" false
                makeWorktreeJson "old-branch" true
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let activeCards = page.Locator(".card-grid .wt-card:not(.skeleton)")
            do! activeCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! activeCount = activeCards.CountAsync()
            Assert.That(activeCount, Is.EqualTo(1), "Only non-archived worktrees appear in card grid")

            let archiveSection = page.Locator(".archive-section")
            do! archiveSection.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! sectionCount = archiveSection.CountAsync()
            Assert.That(sectionCount, Is.EqualTo(1), "Archive section should be present when archived worktrees exist")

            let archiveCards = page.Locator(".archive-section .archive-card")
            let! archiveCardCount = archiveCards.CountAsync()
            Assert.That(archiveCardCount, Is.EqualTo(1), "Archived worktree should appear as archive-card")

            let! branchText = archiveCards.First.Locator(".branch-name").TextContentAsync()
            Assert.That(branchText, Is.EqualTo("old-branch"), "Archive card should show the archived branch name")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Archive section not visible when no archived worktrees``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "feature-a" false
                makeWorktreeJson "feature-b" false
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let cards = page.Locator(".wt-card:not(.skeleton)")
            do! cards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! archiveSectionCount = page.Locator(".archive-section").CountAsync()
            Assert.That(archiveSectionCount, Is.EqualTo(0), "Archive section should not render when no worktrees are archived")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Archive card has unarchive button``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "active" false
                makeWorktreeJson "archived" true
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let unarchiveBtns = page.Locator(".archive-card .unarchive-btn")
            do! unarchiveBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = unarchiveBtns.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Archive card should have an unarchive button")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Archive card has no CT dot or PR info``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "active" false
                makeWorktreeJson "archived" true
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let archiveCard = page.Locator(".archive-card")
            do! archiveCard.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! ctDotCount = archiveCard.Locator(".ct-dot").CountAsync()
            Assert.That(ctDotCount, Is.EqualTo(0), "Archive card should not have a coding tool status dot")

            let! prRowCount = archiveCard.Locator(".pr-row").CountAsync()
            Assert.That(prRowCount, Is.EqualTo(0), "Archive card should not have PR info")

            let! beadsCount = archiveCard.Locator(".beads-row").CountAsync()
            Assert.That(beadsCount, Is.EqualTo(0), "Archive card should not have beads info")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Clicking archive button calls archiveWorktree API``() =
        task {
            let! page = this.Context.NewPageAsync()
            let archiveCalls = System.Collections.Generic.List<string>()

            let json = makeDashboardJson [
                makeWorktreeJson "feature-to-archive" false
            ]

            do! setupMockedPage page (fun () -> json) archiveCalls (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let archiveBtn = page.Locator(".wt-card .archive-btn").First
            do! archiveBtn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let responseTask = page.WaitForResponseAsync(
                (fun (r: IResponse) -> r.Url.Contains("/IWorktreeApi/archiveWorktree")),
                PageWaitForResponseOptions(Timeout = 5000.0f))
            do! archiveBtn.ClickAsync()
            let! _ = responseTask

            Assert.That(archiveCalls.Count, Is.GreaterThanOrEqualTo(1), "Clicking archive should call archiveWorktree API")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Clicking unarchive button calls unarchiveWorktree API``() =
        task {
            let! page = this.Context.NewPageAsync()
            let unarchiveCalls = System.Collections.Generic.List<string>()

            let json = makeDashboardJson [
                makeWorktreeJson "active" false
                makeWorktreeJson "archived-branch" true
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) unarchiveCalls
            let! _ = page.GotoAsync(baseUrl)

            let unarchiveBtn = page.Locator(".archive-card .unarchive-btn").First
            do! unarchiveBtn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let responseTask = page.WaitForResponseAsync(
                (fun (r: IResponse) -> r.Url.Contains("/IWorktreeApi/unarchiveWorktree")),
                PageWaitForResponseOptions(Timeout = 5000.0f))
            do! unarchiveBtn.ClickAsync()
            let! _ = responseTask

            Assert.That(unarchiveCalls.Count, Is.GreaterThanOrEqualTo(1), "Clicking unarchive should call unarchiveWorktree API")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Archive round-trip moves card between sections``() =
        task {
            let! page = this.Context.NewPageAsync()
            let archiveCalls = System.Collections.Generic.List<string>()
            let callCount = ref 0

            let getJson () =
                let c = System.Threading.Interlocked.Increment(callCount)
                if c <= 2 then
                    makeDashboardJson [
                        makeWorktreeJson "main" false
                        makeWorktreeJson "to-archive" false
                    ]
                else
                    makeDashboardJson [
                        makeWorktreeJson "main" false
                        makeWorktreeJson "to-archive" true
                    ]

            do! setupMockedPage page getJson archiveCalls (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let activeCards = page.Locator(".card-grid .wt-card:not(.skeleton)")
            do! activeCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! initialActiveCount = activeCards.CountAsync()
            Assert.That(initialActiveCount, Is.EqualTo(2), "Initially both worktrees are active cards")

            let! initialArchiveCount = page.Locator(".archive-section").CountAsync()
            Assert.That(initialArchiveCount, Is.EqualTo(0), "Initially no archive section")

            let archiveBtn = page.Locator(".wt-card:has(.branch-name:text-is('to-archive')) .archive-btn")
            do! archiveBtn.ClickAsync()

            let archiveSection = page.Locator(".archive-section")
            do! archiveSection.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! finalActiveCount = page.Locator(".card-grid .wt-card:not(.skeleton)").CountAsync()
            Assert.That(finalActiveCount, Is.EqualTo(1), "After archive, only one card in main grid")

            let! archiveCardCount = page.Locator(".archive-section .archive-card").CountAsync()
            Assert.That(archiveCardCount, Is.EqualTo(1), "Archived card appears in archive section")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Unarchive round-trip moves card back to main grid``() =
        task {
            let! page = this.Context.NewPageAsync()
            let unarchiveCalls = System.Collections.Generic.List<string>()
            let callCount = ref 0

            let getJson () =
                let c = System.Threading.Interlocked.Increment(callCount)
                if c <= 2 then
                    makeDashboardJson [
                        makeWorktreeJson "main" false
                        makeWorktreeJson "was-archived" true
                    ]
                else
                    makeDashboardJson [
                        makeWorktreeJson "main" false
                        makeWorktreeJson "was-archived" false
                    ]

            do! setupMockedPage page getJson (System.Collections.Generic.List()) unarchiveCalls
            let! _ = page.GotoAsync(baseUrl)

            let archiveCard = page.Locator(".archive-section .archive-card")
            do! archiveCard.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! initialArchiveCount = archiveCard.CountAsync()
            Assert.That(initialArchiveCount, Is.EqualTo(1), "Initially one archive card")

            let unarchiveBtn = page.Locator(".archive-card .unarchive-btn").First
            do! unarchiveBtn.ClickAsync()

            let activeCards = page.Locator(".card-grid .wt-card:not(.skeleton)")
            do! page.WaitForFunctionAsync("() => document.querySelectorAll('.card-grid .wt-card:not(.skeleton)').length === 2", null, PageWaitForFunctionOptions(Timeout = 5000.0f))
                |> Async.AwaitTask
                |> Async.Ignore
                |> Async.StartAsTask

            let! finalActiveCount = activeCards.CountAsync()
            Assert.That(finalActiveCount, Is.EqualTo(2), "After unarchive, both cards in main grid")

            let! finalArchiveCount = page.Locator(".archive-section").CountAsync()
            Assert.That(finalArchiveCount, Is.EqualTo(0), "Archive section hidden when no archived worktrees remain")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Archive section has dimmed opacity``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "active" false
                makeWorktreeJson "archived" true
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let archiveSection = page.Locator(".archive-section")
            do! archiveSection.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! opacity = archiveSection.EvaluateAsync<string>("el => getComputedStyle(el).opacity")
            Assert.That(float opacity, Is.LessThan(1.0), "Archive section should have dimmed opacity")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Archive section uses flex-wrap layout``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "active" false
                makeWorktreeJson "arch-a" true
                makeWorktreeJson "arch-b" true
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let archiveSection = page.Locator(".archive-section")
            do! archiveSection.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! display = archiveSection.EvaluateAsync<string>("el => getComputedStyle(el).display")
            Assert.That(display, Is.EqualTo("flex"), "Archive section should use flex layout")

            let! flexWrap = archiveSection.EvaluateAsync<string>("el => getComputedStyle(el).flexWrap")
            Assert.That(flexWrap, Is.EqualTo("wrap"), "Archive section should use flex-wrap")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Archive card shows commit time``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "active" false
                makeWorktreeJson "archived" true
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let commitTime = page.Locator(".archive-card .commit-time")
            do! commitTime.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! text = commitTime.First.TextContentAsync()
            Assert.That(text, Does.Contain("ago"), "Archive card should show relative commit time")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Keyboard navigation skips archived cards``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = makeDashboardJson [
                makeWorktreeJson "active-1" false
                makeWorktreeJson "active-2" false
                makeWorktreeJson "archived-skip" true
            ]

            do! setupMockedPage page (fun () -> json) (System.Collections.Generic.List()) (System.Collections.Generic.List())
            let! _ = page.GotoAsync(baseUrl)

            let cards = page.Locator(".card-grid .wt-card:not(.skeleton)")
            do! cards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let dashboard = page.Locator(".dashboard")
            do! dashboard.FocusAsync()

            do! page.Keyboard.PressAsync("ArrowDown")
            let! _ = page.WaitForFunctionAsync(
                "() => document.querySelector('.focused') !== null",
                null, PageWaitForFunctionOptions(Timeout = 5000.0f))

            let focusedElements = System.Collections.Generic.List<string>()
            let rec pressAndCollect remaining =
                task {
                    if remaining > 0 then
                        let focused = page.Locator(".focused")
                        let! count = focused.CountAsync()
                        if count > 0 then
                            let! classes = focused.First.GetAttributeAsync("class")
                            focusedElements.Add(classes)
                        do! page.Keyboard.PressAsync("ArrowDown")
                        let! _ = page.WaitForFunctionAsync(
                            "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))).then(() => true)",
                            null, PageWaitForFunctionOptions(Timeout = 5000.0f))
                        return! pressAndCollect (remaining - 1)
                }
            do! pressAndCollect 10

            let hasArchiveCardFocused =
                focusedElements
                |> Seq.exists (fun c -> c.Contains("archive-card"))
            Assert.That(hasArchiveCardFocused, Is.False, "Keyboard navigation should never focus an archive-card")

            do! page.CloseAsync()
        }
