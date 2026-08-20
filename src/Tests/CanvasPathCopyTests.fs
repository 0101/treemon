module Tests.CanvasPathCopyTests

open NUnit.Framework
open Shared
open CanvasUpdate

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasPathCopyTests() =

    [<TestCase(@"Q:\code\demo", "report.html", @"Q:\code\demo\.agents\canvas\report.html")>]
    [<TestCase(@"Q:\code\demo\", "report.html", @"Q:\code\demo\.agents\canvas\report.html")>]
    [<TestCase("Q:/code/demo", "report.html", "Q:/code/demo/.agents/canvas/report.html")>]
    [<TestCase("/work/demo/", "report.html", "/work/demo/.agents/canvas/report.html")>]
    member _.``Canvas doc disk path preserves the worktree separator``
        (worktreePath: string, filename: string, expected: string) =
        Assert.That(
            canvasDocDiskPath (WorktreePath worktreePath) filename,
            Is.EqualTo(expected))
