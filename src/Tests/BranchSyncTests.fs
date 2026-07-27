module Tests.BranchSyncTests

open System.IO
open NUnit.Framework
open Server.GitWorktree
open Tests.GitTestHelpers
open Tests.TestUtils

/// A second working clone of the shared `origin`, standing in for another machine that can move a
/// branch on the remote. The bare origin's HEAD still points at git's default branch name, so the
/// clone checks out nothing until the wanted branch is picked explicitly.
let private cloneOf (tempDir: string) (originDir: string) (name: string) (branch: string) =
    let dir = Path.Combine(tempDir, name)
    gitOk tempDir [ "clone"; originDir; dir ]
    gitOk dir [ "config"; "user.name"; name ]
    gitOk dir [ "config"; "user.email"; $"{name}@test.com" ]
    gitOk dir [ "checkout"; "-B"; branch; $"origin/{branch}" ]
    dir

/// A `feature` worktree tracking a shared `origin`, plus a second clone standing in for the machine
/// that advances the base branch. The base only ever moves on `origin`, so the sync under test has
/// to fetch before it can see it — exactly the production shape.
let private scratchRepos (tempDir: string) =
    let repoDir, originDir = initRepoWithOrigin tempDir
    File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "shared start")
    gitOk repoDir [ "add"; "--"; "shared.txt" ]
    gitOk repoDir [ "commit"; "-m"; "shared start" ]
    gitOk repoDir [ "push"; "origin"; "main" ]

    let baseDir = cloneOf tempDir originDir "base" "main"

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

/// The remote's own view of a ref, asked over the transport rather than read out of the bare
/// directory, so the test sees exactly what a push moved without depending on `safe.bareRepository`.
let private remoteRef (repoDir: string) (originDir: string) (ref: string) =
    match gitText repoDir [ "ls-remote"; originDir; ref ] with
    | "" -> None
    | line -> line.Split('\t') |> Array.tryHead

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

/// A `feature` branch already published to the shared `origin`: a configured upstream and a remote
/// ref to advance, which is the state a worktree with an open pull request is in.
let private publishedFeature (tempDir: string) =
    let repoDir, originDir = initRepoWithOrigin tempDir
    gitOk repoDir [ "switch"; "-c"; "feature" ]
    gitOk repoDir [ "push"; "--set-upstream"; "origin"; "feature" ]
    repoDir, originDir

let private push (repoDir: string) = pushCurrentBranch repoDir |> runAsync

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BranchPushTests() =

    [<Test>]
    member _.``a synced branch advances exactly its own remote branch to the local head``() =
        withTempDir "treemon-branch-push-ok" (fun tempDir ->
            let repoDir, originDir = publishedFeature tempDir
            let mainBefore = remoteRef repoDir originDir "refs/heads/main"
            commitFile repoDir "feature-work.txt" "feature work"

            let outcome = push repoDir

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.Pushed))
            Assert.That(remoteRef repoDir originDir "refs/heads/feature", Is.EqualTo(Some(headOf repoDir)))
            // The explicit refspec names one branch, so nothing else on the remote moves.
            Assert.That(remoteRef repoDir originDir "refs/heads/main", Is.EqualTo(mainBefore)))

    [<Test>]
    member _.``a remote branch that moved ahead fails the push and is never forced backwards``() =
        withTempDir "treemon-branch-push-diverged" (fun tempDir ->
            let repoDir, originDir = publishedFeature tempDir
            let otherDir = cloneOf tempDir originDir "other" "feature"
            commitFile otherDir "remote-work.txt" "remote work"
            gitOk otherDir [ "push"; "origin"; "feature" ]
            commitFile repoDir "local-work.txt" "local work"
            let localHead = headOf repoDir
            let remoteHead = headOf otherDir

            let outcome = push repoDir

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.PushFailed))
            // A forced push would have replaced the diverged remote commit with the local one; the
            // remote still holding its own head is the proof that no force option was used.
            Assert.That(remoteRef repoDir originDir "refs/heads/feature", Is.EqualTo(Some remoteHead))
            Assert.That(headOf repoDir, Is.EqualTo(localHead)))

    [<Test>]
    member _.``a branch with no configured upstream fails without publishing it``() =
        withTempDir "treemon-branch-push-no-upstream" (fun tempDir ->
            let repoDir, originDir = publishedFeature tempDir
            gitOk repoDir [ "switch"; "-c"; "unpublished"; "--no-track" ]
            commitFile repoDir "unpublished-work.txt" "unpublished work"

            let outcome = push repoDir

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.PushFailed))
            // No upstream means no guessed default: the branch must not appear on the remote at all.
            Assert.That(remoteRef repoDir originDir "refs/heads/unpublished", Is.EqualTo(None)))

    [<Test>]
    member _.``a detached head fails instead of pushing whatever is checked out``() =
        withTempDir "treemon-branch-push-detached" (fun tempDir ->
            let repoDir, originDir = publishedFeature tempDir
            let publishedHead = headOf repoDir
            commitFile repoDir "feature-work.txt" "feature work"
            gitOk repoDir [ "checkout"; "--detach"; "HEAD" ]

            let outcome = push repoDir

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.PushFailed))
            Assert.That(remoteRef repoDir originDir "refs/heads/feature", Is.EqualTo(Some publishedHead)))

    [<Test>]
    member _.``a failing push command reports the failure without throwing``() =
        withTempDir "treemon-branch-push-command-failure" (fun tempDir ->
            let repoDir, originDir = publishedFeature tempDir
            let publishedHead = headOf repoDir
            commitFile repoDir "feature-work.txt" "feature work"
            // An upstream pointing at a remote that does not exist fails inside git rather than
            // before it, which is the shape an authentication or transport failure also takes.
            gitOk repoDir [ "config"; "branch.feature.remote"; "missing-remote" ]

            let outcome = push repoDir

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.PushFailed))
            Assert.That(remoteRef repoDir originDir "refs/heads/feature", Is.EqualTo(Some publishedHead)))
