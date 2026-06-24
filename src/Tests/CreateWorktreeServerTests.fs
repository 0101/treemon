module Tests.CreateWorktreeServerTests

open System
open System.IO
open System.Runtime.InteropServices
open NUnit.Framework
open Server.GitWorktree
open Tests.GitTestHelpers


// ─── resolveWorktreeCommand: pure command construction ───

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


// ─── createWorktree: end-to-end against real git repos ───

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

        let result = createWorktree repoDir "main" "resolve-model-slugs" |> Async.RunSynchronously
        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

        let newWt = Path.Combine(tempDir, "tm-resolve-model-slugs")
        Assert.That(Directory.Exists(newWt), Is.True, "new worktree should be created as a sibling of the repo")
        Assert.That(gitOut newWt "rev-parse --abbrev-ref HEAD", Is.EqualTo("resolve-model-slugs"))
        Assert.That(gitOut newWt "rev-parse HEAD", Is.EqualTo(gitOut repoDir "rev-parse main"), "forked from main's tip")

    [<Test>]
    member _.``forks from the remote base when only the remote has the base branch``() =
        let repoDir, _ = initRepoWithOrigin tempDir
        gitAssert repoDir "checkout -b feature"
        gitAssert repoDir "branch -D main"

        let result = createWorktree repoDir "main" "from-origin" |> Async.RunSynchronously
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

        let result = createWorktree repoDir "main" "feat" |> Async.RunSynchronously
        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

        let newWt = Path.Combine(tempDir, "tm-feat")
        Assert.That(gitOut newWt "rev-parse HEAD", Is.EqualTo(upstreamTip), "should fork from the upstream tip, not stale local main")

    [<Test>]
    member _.``preserves the happy path when the base is checked out``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        let result = createWorktree repoDir "main" "happy-branch" |> Async.RunSynchronously
        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

        let newWt = Path.Combine(tempDir, "tm-happy-branch")
        Assert.That(Directory.Exists(newWt), Is.True)
        Assert.That(gitOut newWt "rev-parse --abbrev-ref HEAD", Is.EqualTo("happy-branch"))

    [<Test>]
    member _.``returns Error and creates nothing when the base branch is missing``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        gitAssert repoDir "checkout -b feature"

        let result = createWorktree repoDir "no-such-base" "should-not-exist" |> Async.RunSynchronously
        Assert.That(Result.isError result, Is.True, $"Expected Error but got: {result}")
        Assert.That(Directory.Exists(Path.Combine(tempDir, "tm-should-not-exist")), Is.False)

    [<Test>]
    member _.``warns but still creates the worktree when a legacy fork script is present``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        let scriptName = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "fork.ps1" else "fork.sh"
        File.WriteAllText(Path.Combine(repoDir, scriptName), "# legacy fork script")

        match createWorktree repoDir "main" "with-legacy" |> Async.RunSynchronously with
        | Error e -> Assert.Fail($"Expected Ok but got Error: {e}")
        | Ok warnings ->
            Assert.That(Directory.Exists(Path.Combine(tempDir, "tm-with-legacy")), Is.True, "worktree should still be created")
            Assert.That(warnings |> List.exists _.Contains("no longer used"), Is.True, $"Expected a legacy-fork-script warning but got: {warnings}")

    [<Test>]
    member _.``runs the post-fork script inside the new worktree``() =
        if not (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) then
            Assert.Ignore("post-fork execution test targets Windows/pwsh")

        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        let script = "param($wt, $root, $baseRef, $branch)\n\"$branch|$root\" | Out-File -FilePath (Join-Path $wt 'pf-args.txt')"
        File.WriteAllText(Path.Combine(repoDir, "post-fork.ps1"), script)

        let result = createWorktree repoDir "main" "with-postfork" |> Async.RunSynchronously
        Assert.That(Result.isOk result, Is.True, $"Expected Ok but got: {result}")

        let newWt = Path.Combine(tempDir, "tm-with-postfork")
        let argsFile = Path.Combine(newWt, "pf-args.txt")
        Assert.That(File.Exists(argsFile), Is.True, "post-fork script should run in the new worktree")
        let contents = File.ReadAllText(argsFile)
        Assert.That(contents, Does.Contain("with-postfork"), "post-fork should receive the branch name")
        Assert.That(contents, Does.Contain("repo"), "post-fork should receive the source repo root")
