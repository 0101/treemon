module Tests.RemoveWorktreeTests

open System
open System.IO
open NUnit.Framework
open Server.GitWorktree

let private git (workingDir: string) (args: string) =
    let psi =
        Diagnostics.ProcessStartInfo(
            "git",
            args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        )

    use proc = Diagnostics.Process.Start(psi)
    proc.WaitForExit()
    proc.ExitCode

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

        Directory.CreateDirectory(repoDir) |> ignore
        git repoDir "init" |> ignore
        git repoDir "commit --allow-empty -m init" |> ignore
        git repoDir $"worktree add -b test-branch \"{worktreeDir}\"" |> ignore

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

        Directory.CreateDirectory(repoDir) |> ignore
        git repoDir "init" |> ignore
        git repoDir "commit --allow-empty -m init" |> ignore
        git repoDir $"worktree add -b normal-branch \"{worktreeDir}\"" |> ignore

        let result = removeWorktree repoDir worktreeDir (Some "normal-branch") |> Async.RunSynchronously

        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")
        Assert.That(Directory.Exists(worktreeDir), Is.False)
