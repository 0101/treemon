module Tests.CreateWorktreeServerTests

open System
open System.IO
open System.Runtime.InteropServices
open NUnit.Framework
open Server.GitWorktree
open Tests.GitTestHelpers


/// Writes an OS-appropriate `post-fork` hook that records its branch + repo-root
/// arguments to `pf-args.txt` inside the worktree, so tests can prove it ran there.
let private writePostForkArgsScript (repoDir: string) =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        let script = "param($wt, $root, $baseRef, $branch)\n\"$branch|$root\" | Out-File -FilePath (Join-Path $wt 'pf-args.txt')"
        File.WriteAllText(Path.Combine(repoDir, "post-fork.ps1"), script)
    else
        let script = "#!/usr/bin/env bash\nprintf '%s|%s' \"$4\" \"$2\" > \"$1/pf-args.txt\"\n"
        File.WriteAllText(Path.Combine(repoDir, "post-fork.sh"), script)


// ─── listWorktrees: git-failure signal against real git repos ───

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ListWorktreesTests() =
    /// NUnit lifecycle field — reassigned per test by [<SetUp>]/[<TearDown>].
    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-listwt-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            try Directory.Delete(tempDir, recursive = true)
            with _ -> ()

    [<Test>]
    member _.``returns Some with the main worktree for a real repo``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        match listWorktrees repoDir |> Async.RunSynchronously with
        | None -> Assert.Fail("Expected Some worktree list for a real repo but got None")
        | Some worktrees ->
            Assert.That(worktrees.Length, Is.EqualTo(1), "a fresh repo has exactly its main worktree")
            Assert.That(worktrees[0].Branch, Is.EqualTo(Some "main"))
            Assert.That(Path.GetFileName(worktrees[0].Path), Is.EqualTo("repo"))

    [<Test>]
    member _.``returns None when the path is not a git repository``() =
        let notARepo = Path.Combine(tempDir, "not-a-repo")
        Directory.CreateDirectory(notARepo) |> ignore

        let result = listWorktrees notARepo |> Async.RunSynchronously
        Assert.That(result, Is.EqualTo(None), "a git failure must surface as None, not an empty list")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ResolveWorktreeCommandTests() =

    [<Test>]
    member _.``forks the new branch from the base ref``() =
        let _, args, _ = resolveWorktreeCommand "Q:\\code\\repo" "origin/main" "my-branch"
        Assert.That(args, Does.Contain("worktree add -b \"my-branch\""))
        Assert.That(args, Does.Contain("\"origin/main\""), "base ref must be passed as the fork point")

    [<Test>]
    member _.``runs git against the repo root``() =
        let fileName, args, _ = resolveWorktreeCommand "Q:\\code\\repo" "main" "my-branch"
        Assert.That(fileName, Is.EqualTo("git"))
        Assert.That(args, Does.Contain("-C \"Q:\\code\\repo\""))

    [<Test>]
    member _.``places the worktree as a tm-prefixed sibling of the repo root``() =
        let _, args, worktreePath = resolveWorktreeCommand "Q:\\code\\repo" "main" "my-branch"
        Assert.That(args, Does.Contain("tm-my-branch"))
        Assert.That(worktreePath, Does.EndWith("tm-my-branch"))

    [<Test>]
    member _.``slashes in the branch name become dashes in the worktree dir``() =
        let _, args, worktreePath = resolveWorktreeCommand "Q:\\code\\repo" "main" "feature/foo"
        Assert.That(worktreePath, Does.EndWith("tm-feature-foo"))
        Assert.That(args, Does.Contain("worktree add -b \"feature/foo\""), "branch keeps its slash")


// ─── resolveBaseRef: ref resolution precedence ───

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ResolveBaseRefTests() =
    /// NUnit lifecycle field — reassigned per test by [<SetUp>]/[<TearDown>].
    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-baseref-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            try Directory.Delete(tempDir, recursive = true)
            with _ -> ()

    [<Test>]
    member _.``prefers the remote-tracking ref over a local base branch``() =
        let repoDir, _ = initRepoWithOrigin tempDir

        let result = resolveBaseRef repoDir "origin" "main" |> Async.RunSynchronously
        Assert.That((result = Ok "origin/main"), Is.True, $"Expected Ok \"origin/main\" but got: {result}")

    [<Test>]
    member _.``uses the remote ref when only the remote has the base branch``() =
        let repoDir, _ = initRepoWithOrigin tempDir
        gitAssert repoDir "checkout -b feature"
        gitAssert repoDir "branch -D main"

        let result = resolveBaseRef repoDir "origin" "main" |> Async.RunSynchronously
        Assert.That((result = Ok "origin/main"), Is.True, $"Expected Ok \"origin/main\" but got: {result}")

    [<Test>]
    member _.``falls back to the local base branch when there is no remote-tracking ref``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        let result = resolveBaseRef repoDir "origin" "main" |> Async.RunSynchronously
        Assert.That((result = Ok "main"), Is.True, $"Expected Ok \"main\" but got: {result}")

    [<Test>]
    member _.``errors when the base branch exists neither locally nor on the remote``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        let result = resolveBaseRef repoDir "origin" "does-not-exist" |> Async.RunSynchronously
        Assert.That(Result.isError result, Is.True, $"Expected Error but got: {result}")


// ─── forkWorktree / runPostFork: end-to-end against real git repos ───

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CreateWorktreeIntegrationTests() =
    /// NUnit lifecycle field — reassigned per test by [<SetUp>]/[<TearDown>].
    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-newwt-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            try Directory.Delete(tempDir, recursive = true)
            with _ -> ()

    [<Test>]
    member _.``succeeds when no worktree has the base branch checked out``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        // Switch the only worktree off main so nothing has the base checked out.
        gitAssert repoDir "checkout -b canvas-review-report"

        let result = forkWorktree repoDir "main" "resolve-model-slugs" |> Async.RunSynchronously
        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

        let newWt = Path.Combine(tempDir, "tm-resolve-model-slugs")

        match result with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok r -> Assert.That(r.WorktreePath, Is.EqualTo(newWt), "createWorktree should return the new worktree path")

        Assert.That(Directory.Exists(newWt), Is.True, "new worktree should be created as a sibling of the repo")
        Assert.That(gitOut newWt "rev-parse --abbrev-ref HEAD", Is.EqualTo("resolve-model-slugs"))
        Assert.That(gitOut newWt "rev-parse HEAD", Is.EqualTo(gitOut repoDir "rev-parse main"), "forked from main's tip")

    [<Test>]
    member _.``forks from the remote base when only the remote has the base branch``() =
        let repoDir, _ = initRepoWithOrigin tempDir
        gitAssert repoDir "checkout -b feature"
        gitAssert repoDir "branch -D main"

        let result = forkWorktree repoDir "main" "from-origin" |> Async.RunSynchronously
        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

        let newWt = Path.Combine(tempDir, "tm-from-origin")
        Assert.That(Directory.Exists(newWt), Is.True)
        Assert.That(gitOut newWt "rev-parse --abbrev-ref HEAD", Is.EqualTo("from-origin"))
        Assert.That(gitOut newWt "rev-parse HEAD", Is.EqualTo(gitOut repoDir "rev-parse origin/main"))

    [<Test>]
    member _.``forks from the upstream tip when the local base branch is stale``() =
        let repoDir, _ = initRepoWithOrigin tempDir
        // Advance origin/main one commit ahead of the local main, then rewind local main.
        gitAssert repoDir "commit --allow-empty -m second"
        gitAssert repoDir "push origin main"
        gitAssert repoDir "reset --hard HEAD~1"

        let staleLocal = gitOut repoDir "rev-parse main"
        let upstreamTip = gitOut repoDir "rev-parse origin/main"
        Assert.That(upstreamTip, Is.Not.EqualTo(staleLocal), "test setup: origin/main must be ahead of local main")

        let result = forkWorktree repoDir "main" "feat" |> Async.RunSynchronously
        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

        let newWt = Path.Combine(tempDir, "tm-feat")
        Assert.That(gitOut newWt "rev-parse HEAD", Is.EqualTo(upstreamTip), "should fork from the upstream tip, not stale local main")

    [<Test>]
    member _.``preserves the happy path when the base is checked out``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        let result = forkWorktree repoDir "main" "happy-branch" |> Async.RunSynchronously
        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

        let newWt = Path.Combine(tempDir, "tm-happy-branch")
        Assert.That(Directory.Exists(newWt), Is.True)
        Assert.That(gitOut newWt "rev-parse --abbrev-ref HEAD", Is.EqualTo("happy-branch"))

    [<Test>]
    member _.``returns Error and creates nothing when the base branch is missing``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        gitAssert repoDir "checkout -b feature"

        let result = forkWorktree repoDir "no-such-base" "should-not-exist" |> Async.RunSynchronously
        Assert.That(Result.isError result, Is.True, $"Expected Error but got: {result}")
        Assert.That(Directory.Exists(Path.Combine(tempDir, "tm-should-not-exist")), Is.False)

    [<Test>]
    member _.``warns but still creates the worktree when a legacy fork script is present``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        let scriptName = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "fork.ps1" else "fork.sh"
        File.WriteAllText(Path.Combine(repoDir, scriptName), "# legacy fork script")

        match forkWorktree repoDir "main" "with-legacy" |> Async.RunSynchronously with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok fork ->
            Assert.That(Directory.Exists(Path.Combine(tempDir, "tm-with-legacy")), Is.True, "worktree should still be created")
            Assert.That(fork.Warnings |> List.exists _.Contains("no longer used"), Is.True, $"Expected a legacy-fork-script warning but got: {fork.Warnings}")

    [<Test>]
    member _.``forkWorktree returns without running the post-fork script``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        writePostForkArgsScript repoDir

        forkWorktree repoDir "main" "no-postfork" |> Async.RunSynchronously |> ignore

        let argsFile = Path.Combine(tempDir, "tm-no-postfork", "pf-args.txt")
        Assert.That(File.Exists(argsFile), Is.False, "forkWorktree must not run post-fork — it runs in the background after the call returns")

    [<Test>]
    member _.``runPostFork runs the script inside the new worktree``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        writePostForkArgsScript repoDir

        match forkWorktree repoDir "main" "with-postfork" |> Async.RunSynchronously with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok fork ->
            let result = runPostFork repoDir fork.WorktreePath fork.BaseRef "with-postfork" |> Async.RunSynchronously
            Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

            let argsFile = Path.Combine(fork.WorktreePath, "pf-args.txt")
            Assert.That(File.Exists(argsFile), Is.True, "post-fork script should run in the new worktree")
            let contents = File.ReadAllText(argsFile)
            Assert.That(contents, Does.Contain("with-postfork"), "post-fork should receive the branch name")
            Assert.That(contents, Does.Contain("repo"), "post-fork should receive the source repo root")

    [<Test>]
    member _.``runPostFork is a no-op when no post-fork script exists``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        match forkWorktree repoDir "main" "no-script" |> Async.RunSynchronously with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok fork ->
            let result = runPostFork repoDir fork.WorktreePath fork.BaseRef "no-script" |> Async.RunSynchronously
            Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

    [<Test>]
    member _.``runPostFork reports failure when the post-fork script exits non-zero``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            File.WriteAllText(Path.Combine(repoDir, "post-fork.ps1"), "exit 1")
        else
            File.WriteAllText(Path.Combine(repoDir, "post-fork.sh"), "#!/usr/bin/env bash\nexit 1\n")

        match forkWorktree repoDir "main" "postfork-fails" |> Async.RunSynchronously with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok fork ->
            Assert.That(Directory.Exists(fork.WorktreePath), Is.True, "worktree should still be created")
            let result = runPostFork repoDir fork.WorktreePath fork.BaseRef "postfork-fails" |> Async.RunSynchronously
            Assert.That(Result.isError result, Is.True, $"Expected Error but got: {result}")

    [<Test>]
    member _.``runPostForkWithTimeout kills a hung post-fork script and reports a timeout``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            File.WriteAllText(Path.Combine(repoDir, "post-fork.ps1"), "Start-Sleep -Seconds 30")
        else
            File.WriteAllText(Path.Combine(repoDir, "post-fork.sh"), "#!/usr/bin/env bash\nsleep 30\n")

        match forkWorktree repoDir "main" "postfork-hangs" |> Async.RunSynchronously with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok fork ->
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let result = runPostForkWithTimeout 2000 repoDir fork.WorktreePath fork.BaseRef "postfork-hangs" |> Async.RunSynchronously
            sw.Stop()

            match result with
            | Ok () -> Assert.Fail("Expected a timeout Error but the hung script returned Ok")
            | Error msg -> Assert.That(msg, Does.Contain("Timed out"), $"Expected a timeout error but got: {msg}")

            Assert.That(
                sw.Elapsed.TotalSeconds,
                Is.LessThan(15.0),
                "the hung script should have been killed at the timeout, not run to completion")
