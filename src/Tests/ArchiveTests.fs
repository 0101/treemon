module Tests.ArchiveTests

open System
open System.IO
open NUnit.Framework
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
          IsCollapsed = false }

    let makeWorktreeStatus branch isArchived : WorktreeStatus =
        { Path = $"/repo/{branch}"
          Branch = branch
          LastCommitMessage = "test"
          LastCommitTime = DateTimeOffset.UtcNow
          Beads = BeadsSummary.zero
          CodingTool = CodingToolStatus.Idle
          CodingToolProvider = None
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
