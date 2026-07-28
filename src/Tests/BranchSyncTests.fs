module Tests.BranchSyncTests

open System.IO
open NUnit.Framework
open Server
open Server.GitBranchSync
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

/// The sync request a scratch repo's `feature` worktree is normally asked for: its own tree, the
/// shared `origin`, and the base and observed branches that go with them.
let private syncRequest (repoDir: string) (upstreamRemote: string) : BranchSyncRequest =
    { WorktreePath = repoDir
      UpstreamRemote = upstreamRemote
      BaseBranch = "main"
      Branch = "feature" }

let private sync (repoDir: string) = syncWithBase (syncRequest repoDir "origin") |> runAsync

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

            let outcome = syncWithBase (syncRequest repoDir "missing-remote") |> runAsync

            Assert.That(outcome, Is.EqualTo(BranchSyncOutcome.CommandFailed))
            Assert.That(headOf repoDir, Is.EqualTo(headBefore))
            Assert.That(mergeInProgress repoDir, Is.False))

    [<Test>]
    member _.``a sync whose branch is no longer checked out merges into nothing else``() =
        withTempDir "treemon-branch-sync-branch-changed" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            advanceBase baseDir "base-work.txt" "base work"
            // The request still names `feature`, so this stands in for a checkout that landed after
            // the observation: the probe and the fetch run, and the merge must not.
            gitOk repoDir [ "switch"; "-c"; "other" ]
            let otherHeadBefore = headOf repoDir
            let featureHeadBefore = gitText repoDir [ "rev-parse"; "feature" ]

            let outcome = sync repoDir

            Assert.Multiple(fun () ->
                Assert.That(outcome, Is.EqualTo(BranchSyncOutcome.BranchChanged))
                Assert.That(headOf repoDir, Is.EqualTo(otherHeadBefore))
                Assert.That(gitText repoDir [ "rev-parse"; "feature" ], Is.EqualTo(featureHeadBefore))
                Assert.That(
                    File.Exists(Path.Combine(repoDir, "base-work.txt")),
                    Is.False,
                    "the base must not reach the tree of a branch nobody observed")
                Assert.That(mergeInProgress repoDir, Is.False)))

/// A `feature` branch already published to the shared `origin`: a configured upstream and a remote
/// ref to advance, which is the state a worktree with an open pull request is in.
let private publishedFeature (tempDir: string) =
    let repoDir, originDir = initRepoWithOrigin tempDir
    gitOk repoDir [ "switch"; "-c"; "feature" ]
    gitOk repoDir [ "push"; "--set-upstream"; "origin"; "feature" ]
    repoDir, originDir

let private push (repoDir: string) (branch: string) = pushSyncedBranch repoDir branch |> runAsync

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

            let outcome = push repoDir "feature"

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

            let outcome = push repoDir "feature"

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

            let outcome = push repoDir "unpublished"

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.PushFailed))
            // No upstream means no guessed default: the branch must not appear on the remote at all.
            Assert.That(remoteRef repoDir originDir "refs/heads/unpublished", Is.EqualTo(None)))

    [<Test>]
    member _.``a detached head is not the named branch and publishes nothing``() =
        withTempDir "treemon-branch-push-detached" (fun tempDir ->
            let repoDir, originDir = publishedFeature tempDir
            let publishedHead = headOf repoDir
            commitFile repoDir "feature-work.txt" "feature work"
            gitOk repoDir [ "checkout"; "--detach"; "HEAD" ]

            let outcome = push repoDir "feature"

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.BranchChanged))
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

            let outcome = push repoDir "feature"

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.PushFailed))
            Assert.That(remoteRef repoDir originDir "refs/heads/feature", Is.EqualTo(Some publishedHead)))

    [<Test>]
    member _.``an option-like configured remote never reaches the push command``() =
        withTempDir "treemon-branch-push-option-remote" (fun tempDir ->
            let repoDir, originDir = publishedFeature tempDir
            let publishedHead = headOf repoDir
            commitFile repoDir "feature-work.txt" "feature work"
            // The remote is the one push argument that has to precede `--`, so a configured value
            // git would read as an option instead of a destination — `--receive-pack` names the
            // program a local push runs — is refused by Treemon rather than left to whatever git's
            // own parsing makes of the rest of the command.
            gitOk repoDir [ "config"; "branch.feature.remote"; "--receive-pack=echo" ]

            let outcome = push repoDir "feature"

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.PushFailed))
            Assert.That(remoteRef repoDir originDir "refs/heads/feature", Is.EqualTo(Some publishedHead)))

    [<Test>]
    member _.``a remote name containing a dash still publishes the branch``() =
        withTempDir "treemon-branch-push-dashed-remote" (fun tempDir ->
            let repoDir, originDir = publishedFeature tempDir
            // Only a leading dash makes a value option-like: a dash anywhere else is an ordinary
            // remote name and must keep pushing.
            gitOk repoDir [ "remote"; "add"; "my-origin"; originDir ]
            gitOk repoDir [ "push"; "--set-upstream"; "my-origin"; "feature" ]
            commitFile repoDir "feature-work.txt" "feature work"

            let outcome = push repoDir "feature"

            Assert.That(outcome, Is.EqualTo(BranchPushOutcome.Pushed))
            Assert.That(remoteRef repoDir originDir "refs/heads/feature", Is.EqualTo(Some(headOf repoDir))))

/// The sessionless path as one composed operation over a real repository: Treemon's own Git sync
/// followed by the push an open pull request calls for. The PR status comes from the reconciled map
/// the caller already holds; every Git step is the real one.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("AutoSyncVerification")>]
type MechanicalSyncCompositionTests() =

    let openPr: Shared.PrStatus =
        Shared.HasPr
            { Id = 7
              Title = "sync"
              Url = "https://example.test/pull/7"
              IsDraft = false
              Comments = Shared.WithResolution(0, 0)
              Builds = []
              IsOpen = true
              IsMerged = false
              AutoMergeEnabled = false
              HasConflicts = false }

    let refusedDirty: Result<unit, AutoSync.SyncFailure> = Error AutoSync.DirtyWorktree

    let branchChanged: Result<unit, AutoSync.SyncFailure> = Error AutoSync.BranchChanged

    let syncedThrough pushBranch prStatus repoDir =
        AutoSync.mechanicalSync
            syncWithBase
            pushBranch
            { Sync = syncRequest repoDir "origin"
              PrStatus = prStatus }
        |> runAsync

    [<Test>]
    member _.``divergent history is merged and left unpushed when no pull request is open``() =
        withTempDir "treemon-mechanical-merge" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            commitFile repoDir "feature-work.txt" "feature work"
            advanceBase baseDir "base-work.txt" "base work"

            let outcome =
                repoDir
                |> syncedThrough (fun _ _ -> failwith "a branch with no open pull request must not push") Shared.NoPr

            Assert.Multiple(fun () ->
                Assert.That(Result.isOk outcome, Is.True)
                Assert.That(getMainBehindCount repoDir "origin/main" |> runAsync, Is.EqualTo(0))
                Assert.That(mergeCommitCount repoDir, Is.EqualTo("1"))
                Assert.That(File.Exists(Path.Combine(repoDir, "feature-work.txt")), Is.True)))

    [<Test>]
    member _.``an open pull request receives the branch the sync just merged``() =
        withTempDir "treemon-mechanical-push" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            gitOk repoDir [ "push"; "--set-upstream"; "origin"; "feature" ]
            commitFile repoDir "feature-work.txt" "feature work"
            advanceBase baseDir "base-work.txt" "base work"
            let originDir = Path.Combine(tempDir, "origin.git")

            let outcome = repoDir |> syncedThrough pushSyncedBranch openPr

            Assert.Multiple(fun () ->
                Assert.That(Result.isOk outcome, Is.True)
                Assert.That(
                    remoteRef repoDir originDir "refs/heads/feature",
                    Is.EqualTo(Some(headOf repoDir)),
                    "an open pull request must end the sync holding the merged head")))

    [<Test>]
    member _.``a refused sync pushes nothing``() =
        withTempDir "treemon-mechanical-refused" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            advanceBase baseDir "base-work.txt" "base work"
            File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "uncommitted local work")

            let outcome =
                repoDir
                |> syncedThrough (fun _ _ -> failwith "a sync that merged nothing must not push") openPr

            Assert.That(outcome, Is.EqualTo(refusedDirty)))

    [<Test>]
    member _.``a worktree checked out elsewhere since the observation publishes nothing``() =
        withTempDir "treemon-mechanical-checkout" (fun tempDir ->
            let repoDir, baseDir = scratchRepos tempDir
            advanceBase baseDir "base-work.txt" "base work"
            // The observation named `feature`; by the time the merge runs the tree is on something
            // else, and merging the base into it would be work nobody asked for.
            gitOk repoDir [ "switch"; "-c"; "other" ]
            let headBefore = headOf repoDir

            let outcome =
                repoDir
                |> syncedThrough (fun _ _ -> failwith "a run that left its branch must not push") openPr

            Assert.Multiple(fun () ->
                Assert.That(outcome, Is.EqualTo(branchChanged))
                Assert.That(headOf repoDir, Is.EqualTo(headBefore))
                Assert.That(
                    File.Exists(Path.Combine(repoDir, "base-work.txt")),
                    Is.False,
                    "a refused merge leaves the tree it would have written exactly as it was")
                Assert.That(mergeInProgress repoDir, Is.False)))
