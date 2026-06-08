module Tests.BeadspaceProvisionerTests

open System
open System.IO
open NUnit.Framework
open Shared
open Server.BeadspaceProvisioner

let private createTempWorktree () =
    let dir = Path.Combine(Path.GetTempPath(), $"beadspace-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    dir

let private beadsHtmlPath worktreePath =
    Path.Combine(worktreePath, ".agents", "canvas", "beads.html")

let private summaryWithIssues =
    { Open = 3; InProgress = 1; Blocked = 1; Closed = 2 }


// ── Provisioning (create beads.html when issues exist) ──────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ProvisionTests() =
    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() = tempDir <- createTempWorktree ()

    [<TearDown>]
    member _.Teardown() =
        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, true)

    [<Test>]
    member _.``Creates beads.html when issues exist and file is absent``() =
        let result = provisionDashboard tempDir summaryWithIssues
        let htmlPath = beadsHtmlPath tempDir

        Assert.That(File.Exists(htmlPath), Is.True, "beads.html should be created")
        Assert.That(File.ReadAllText(htmlPath), Does.StartWith("<!DOCTYPE html>"))
        Assert.That(result.IsSome, Is.True, "Should return Some action description")

    [<Test>]
    member _.``Creates .agents/canvas directory if missing``() =
        provisionDashboard tempDir summaryWithIssues |> ignore
        let canvasDir = Path.Combine(tempDir, ".agents", "canvas")

        Assert.That(Directory.Exists(canvasDir), Is.True, ".agents/canvas/ should be created")

    [<Test>]
    member _.``Does not rewrite when content already matches template``() =
        let htmlPath = beadsHtmlPath tempDir
        let canvasDir = Path.GetDirectoryName(htmlPath)
        Directory.CreateDirectory(canvasDir) |> ignore
        File.WriteAllText(htmlPath, Server.BeadspaceTemplate.html)

        let result = provisionDashboard tempDir summaryWithIssues

        Assert.That(result, Is.EqualTo(None), "Should not rewrite when already current")
        Assert.That(File.ReadAllText(htmlPath), Is.EqualTo(Server.BeadspaceTemplate.html))

    [<Test>]
    member _.``Rewrites stale beads.html when content differs from template``() =
        let htmlPath = beadsHtmlPath tempDir
        let canvasDir = Path.GetDirectoryName(htmlPath)
        Directory.CreateDirectory(canvasDir) |> ignore
        File.WriteAllText(htmlPath, "<!-- stale template -->")

        let result = provisionDashboard tempDir summaryWithIssues

        Assert.That(result.IsSome, Is.True, "Should rewrite a stale dashboard")
        Assert.That(File.ReadAllText(htmlPath), Is.EqualTo(Server.BeadspaceTemplate.html))


// ── Removal (delete beads.html when zero issues) ────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RemovalTests() =
    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() = tempDir <- createTempWorktree ()

    [<TearDown>]
    member _.Teardown() =
        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, true)

    [<Test>]
    member _.``Deletes beads.html when zero issues and file exists``() =
        let htmlPath = beadsHtmlPath tempDir
        let canvasDir = Path.GetDirectoryName(htmlPath)
        Directory.CreateDirectory(canvasDir) |> ignore
        File.WriteAllText(htmlPath, "<html>old</html>")

        let result = provisionDashboard tempDir BeadsSummary.zero

        Assert.That(File.Exists(htmlPath), Is.False, "beads.html should be deleted")
        Assert.That(result.IsSome, Is.True, "Should return Some action description")

    [<Test>]
    member _.``No-op when zero issues and no beads.html``() =
        let result = provisionDashboard tempDir BeadsSummary.zero

        Assert.That(File.Exists(beadsHtmlPath tempDir), Is.False)
        Assert.That(result, Is.EqualTo(None), "Should take no action")

    [<Test>]
    member _.``Keeps beads.html when only closed issues exist``() =
        let htmlPath = beadsHtmlPath tempDir
        let canvasDir = Path.GetDirectoryName(htmlPath)
        Directory.CreateDirectory(canvasDir) |> ignore
        File.WriteAllText(htmlPath, Server.BeadspaceTemplate.html)

        let closedOnly = { Open = 0; InProgress = 0; Blocked = 0; Closed = 5 }
        let result = provisionDashboard tempDir closedOnly

        Assert.That(File.Exists(htmlPath), Is.True, "Should keep file when closed issues exist")
        Assert.That(result, Is.EqualTo(None))
