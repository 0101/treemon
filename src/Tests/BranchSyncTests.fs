module Tests.BranchSyncTests

open System.IO
open NUnit.Framework
open Server.GitWorktree
open Tests.GitTestHelpers
open Tests.TestUtils

/// A `feature` worktree tracking a shared `origin`, plus a second clone standing in for the machine
/// that advances the base branch. The base only ever moves on `origin`, so the sync under test has
/// to fetch before it can see it — exactly the production shape.
let private scratchRepos (tempDir: string) =
    let repoDir, originDir = initRepoWithOrigin tempDir
    File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "shared start")
    gitOk repoDir [ "add"; "--"; "shared.txt" ]
    gitOk repoDir [ "commit"; "-m"; "shared start" ]
    gitOk repoDir [ "push"; "origin"; "main" ]

    let baseDir = Path.Combine(tempDir, "base")
    gitOk tempDir [ "clone"; originDir; baseDir ]
    gitOk baseDir [ "config"; "user.name"; "base" ]
    gitOk baseDir [ "config"; "user.email"; "base@test.com" ]
    // The bare origin's HEAD still points at git's default branch name, so the clone checks out
    // nothing until main is picked explicitly.
    gitOk baseDir [ "checkout"; "-B"; "main"; "origin/main" ]

    gitOk repoDir [ "switch"; "-c"; "feature" ]
    repoDir, baseDir

let private commitFile (dir: string) (name: string) (content: string) =
    File.WriteAllText(Path.Combine(dir, name), content)
    gitOk dir [ "add"; "--"; name ]
    gitOk dir [ "commit"; "-m"; $"change {name}" ]

let private advanceBase (baseDir: string) (name: string) (content: string) =
    commitFile baseDir name content
    gitOk baseDir [ "push"; "origin"; "main" ]

let private headOf (repoDir: string) = gitText repoDir [ "rev-parse"; "HEAD" ]

let private mergeCommitCount (repoDir: string) =
    gitText repoDir [ "rev-list"; "--count"; "--merges"; "HEAD" ]

let private mergeInProgress (repoDir: string) =
    let exitCode, _, _ =
        runGitArgs repoDir [ "rev-parse"; "--verify"; "--quiet"; "MERGE_HEAD" ]

    exitCode = 0

let private sync (repoDir: string) = syncWithBase repoDir "origin" "main" |> runAsync

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BranchSyncTests() =

    [<Test>]
    member _.``a behind worktree fast-forwards onto the freshly fetched base``() =
        withTempDir "treemon-branch-sync-ff" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            advanceBase baseDir "base-work.txt" "base work"

            let outcome = sync repoDir

            Assert.That(outcome, Is.EqualTo(BranchSyncOutcome.FastForwarded))
            Assert.That(getMainBehindCount repoDir "origin/main" |> runAsync, Is.EqualTo(0))
            Assert.That(File.Exists(Path.Combine(repoDir, "base-work.txt")), Is.True)
            // A fast-forward moves the branch onto the base rather than committing a merge.
            Assert.That(mergeCommitCount repoDir, Is.EqualTo("0")))

    [<Test>]
    member _.``divergent history without conflicts is merged and keeps the local commit``() =
        withTempDir "treemon-branch-sync-merge" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            commitFile repoDir "feature-work.txt" "feature work"
            advanceBase baseDir "base-work.txt" "base work"

            let outcome = sync repoDir

            Assert.That(outcome, Is.EqualTo(BranchSyncOutcome.Merged))
            Assert.That(getMainBehindCount repoDir "origin/main" |> runAsync, Is.EqualTo(0))
            Assert.That(mergeCommitCount repoDir, Is.EqualTo("1"))
            Assert.That(File.Exists(Path.Combine(repoDir, "feature-work.txt")), Is.True)
            Assert.That(File.Exists(Path.Combine(repoDir, "base-work.txt")), Is.True))

    [<Test>]
    member _.``a conflicting merge is aborted and the pre-merge tree and index are restored``() =
        withTempDir "treemon-branch-sync-conflict" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            commitFile repoDir "shared.txt" "feature version"
            advanceBase baseDir "shared.txt" "base version"
            let headBefore = headOf repoDir

            let outcome = sync repoDir

            Assert.That(outcome, Is.EqualTo(BranchSyncOutcome.Conflicted))
            Assert.That(mergeInProgress repoDir, Is.False)
            Assert.That(headOf repoDir, Is.EqualTo(headBefore))
            Assert.That(File.ReadAllText(Path.Combine(repoDir, "shared.txt")), Is.EqualTo("feature version"))
            // An unstaged conflict resolution or a half-applied index would show up here.
            Assert.That(gitOutput repoDir [ "status"; "--porcelain" ], Is.Empty))

    [<Test>]
    member _.``a dirty worktree is refused without fetching or moving anything``() =
        withTempDir "treemon-branch-sync-dirty" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            advanceBase baseDir "base-work.txt" "base work"
            File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "uncommitted local work")
            let headBefore = headOf repoDir
            let knownBaseBefore = gitText repoDir [ "rev-parse"; "origin/main" ]

            let outcome = sync repoDir

            Assert.That(outcome, Is.EqualTo(BranchSyncOutcome.RefusedDirty))
            Assert.That(headOf repoDir, Is.EqualTo(headBefore))
            Assert.That(gitText repoDir [ "rev-parse"; "origin/main" ], Is.EqualTo(knownBaseBefore))
            Assert.That(
                File.ReadAllText(Path.Combine(repoDir, "shared.txt")),
                Is.EqualTo("uncommitted local work"))
            Assert.That(File.Exists(Path.Combine(repoDir, "base-work.txt")), Is.False))

    [<Test>]
    member _.``a worktree that already contains the base is left untouched``() =
        withTempDir "treemon-branch-sync-current" (fun tempDir ->
            let repoDir, _ = scratchRepos tempDir
            commitFile repoDir "feature-work.txt" "feature work"
            let headBefore = headOf repoDir

            let outcome = sync repoDir

            Assert.That(outcome, Is.EqualTo(BranchSyncOutcome.AlreadyCurrent))
            Assert.That(headOf repoDir, Is.EqualTo(headBefore))
            // No empty merge commit for a base that is already an ancestor.
            Assert.That(mergeCommitCount repoDir, Is.EqualTo("0")))

    [<Test>]
    member _.``a failed fetch reports a command failure and mutates nothing``() =
        withTempDir "treemon-branch-sync-failure" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            advanceBase baseDir "base-work.txt" "base work"
            let headBefore = headOf repoDir

            let outcome = syncWithBase repoDir "missing-remote" "main" |> runAsync

            Assert.That(outcome, Is.EqualTo(BranchSyncOutcome.CommandFailed))
            Assert.That(headOf repoDir, Is.EqualTo(headBefore))
            Assert.That(mergeInProgress repoDir, Is.False))
