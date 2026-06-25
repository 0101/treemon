module Tests.RemoveWorktreeTests

open System
open System.IO
open NUnit.Framework
open Server.GitWorktree
open Tests.GitTestHelpers

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RemoveWorktreeTests() =
    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            try Directory.Delete(tempDir, recursive = true)
            with _ -> ()

    [<Test>]
    member _.``removeWorktree prunes and cleans up broken worktree``() =
        let repoDir = Path.Combine(tempDir, "repo")
        let worktreeDir = Path.Combine(tempDir, "wt-broken")

        initRepo repoDir
        File.WriteAllText(Path.Combine(repoDir, "README.md"), "test")
        gitAssert repoDir "add ."
        gitAssert repoDir "commit -m init"
        gitAssert repoDir $"worktree add -b test-branch \"{worktreeDir}\""

        // Break the worktree by removing its .git file (makes it prunable)
        let gitFile = Path.Combine(worktreeDir, ".git")
        if File.Exists(gitFile) then File.Delete(gitFile)

        let result = removeWorktree repoDir worktreeDir (Some "test-branch") |> Async.RunSynchronously

        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")
        Assert.That(Directory.Exists(worktreeDir), Is.False)

    [<Test>]
    member _.``removeWorktree succeeds for normal worktree``() =
        let repoDir = Path.Combine(tempDir, "repo")
        let worktreeDir = Path.Combine(tempDir, "wt-normal")

        initRepo repoDir
        gitAssert repoDir "commit --allow-empty -m init"
        gitAssert repoDir $"worktree add -b normal-branch \"{worktreeDir}\""

        let result = removeWorktree repoDir worktreeDir (Some "normal-branch") |> Async.RunSynchronously

        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")
        Assert.That(Directory.Exists(worktreeDir), Is.False)

    [<Test>]
    member _.``removeWorktree rejects main worktree``() =
        let repoDir = Path.Combine(tempDir, "repo")

        initRepo repoDir
        gitAssert repoDir "commit --allow-empty -m init"

        let result = removeWorktree repoDir repoDir (Some "main") |> Async.RunSynchronously

        Assert.That(Result.isError result, Is.True)
        Assert.That(Directory.Exists(repoDir), Is.True, "Main worktree directory should not be deleted")
