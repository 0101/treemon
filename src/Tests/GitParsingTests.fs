module Tests.GitParsingTests

open System
open System.IO
open NUnit.Framework
open Server.GitWorktree
open Server.PathUtils
open Tests.GitTestHelpers
open Tests.TestUtils

[<SetUpFixture>]
type LogDirSetup() =
    [<OneTimeSetUp>]
    member _.EnsureLogDir() =
        Path.Combine(Directory.GetCurrentDirectory(), "logs")
        |> Directory.CreateDirectory
        |> ignore

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseWorktreeListTests() =

    [<Test>]
    member _.``Normal output parses two worktrees``() =
        let output =
            String.concat "\n"
                [ "worktree /repo/main"
                  "HEAD abc1234567890abcdef1234567890abcdef123456"
                  "branch refs/heads/main"
                  ""
                  "worktree /repo/feature-branch"
                  "HEAD def4567890abcdef1234567890abcdef12345678"
                  "branch refs/heads/feature-branch"
                  "" ]

        let result = parseWorktreeList output

        Assert.That(result.Length, Is.EqualTo(2))
        Assert.That(result[0].Path, Is.EqualTo(normalizePath "/repo/main"))
        Assert.That(result[0].Head, Is.EqualTo("abc1234567890abcdef1234567890abcdef123456"))
        Assert.That(result[0].Branch, Is.EqualTo(Some "main"))
        Assert.That(result[1].Path, Is.EqualTo(normalizePath "/repo/feature-branch"))
        Assert.That(result[1].Branch, Is.EqualTo(Some "feature-branch"))

    [<Test>]
    member _.``Empty output returns empty list``() =
        let result = parseWorktreeList ""
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Whitespace-only output returns empty list``() =
        let result = parseWorktreeList "   "
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Bare repo entry without branch is parsed with Branch=None``() =
        let output =
            String.concat "\n"
                [ "worktree /repo/bare"
                  "HEAD abc1234567890abcdef1234567890abcdef123456"
                  "bare"
                  "" ]

        let result = parseWorktreeList output

        Assert.That(result.Length, Is.EqualTo(1))
        Assert.That(result[0].Path, Is.EqualTo(normalizePath "/repo/bare"))
        Assert.That(result[0].Head, Is.EqualTo("abc1234567890abcdef1234567890abcdef123456"))
        Assert.That(result[0].Branch, Is.EqualTo(None))

    [<Test>]
    member _.``Detached HEAD entry has Branch=None``() =
        let output =
            String.concat "\n"
                [ "worktree /repo/detached"
                  "HEAD abc1234567890abcdef1234567890abcdef123456"
                  "detached"
                  "" ]

        let result = parseWorktreeList output

        Assert.That(result.Length, Is.EqualTo(1))
        Assert.That(result[0].Branch, Is.EqualTo(None))

    [<Test>]
    member _.``Block missing worktree line is skipped``() =
        let output =
            String.concat "\n"
                [ "HEAD abc1234567890abcdef1234567890abcdef123456"
                  "branch refs/heads/main"
                  "" ]

        let result = parseWorktreeList output
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Block missing HEAD line is skipped``() =
        let output =
            String.concat "\n"
                [ "worktree /repo/main"
                  "branch refs/heads/main"
                  "" ]

        let result = parseWorktreeList output
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Prunable worktree is excluded``() =
        let output =
            String.concat "\n"
                [ "worktree /repo/main"
                  "HEAD abc1234567890abcdef1234567890abcdef123456"
                  "branch refs/heads/main"
                  ""
                  "worktree /repo/stale-branch"
                  "HEAD def4567890abcdef1234567890abcdef12345678"
                  "branch refs/heads/stale-branch"
                  "prunable gitdir file points to non-existent location"
                  "" ]

        let result = parseWorktreeList output

        Assert.That(result.Length, Is.EqualTo(1))
        Assert.That(result[0].Path, Is.EqualTo(normalizePath "/repo/main"))

    [<Test>]
    member _.``All prunable worktrees are excluded``() =
        let output =
            String.concat "\n"
                [ "worktree /repo/stale-a"
                  "HEAD abc1234567890abcdef1234567890abcdef123456"
                  "branch refs/heads/stale-a"
                  "prunable gitdir file points to non-existent location"
                  ""
                  "worktree /repo/stale-b"
                  "HEAD def4567890abcdef1234567890abcdef12345678"
                  "branch refs/heads/stale-b"
                  "prunable gitdir file points to non-existent location"
                  "" ]

        let result = parseWorktreeList output
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Prunable bare worktree is excluded``() =
        let output =
            String.concat "\n"
                [ "worktree /repo/bare"
                  "HEAD abc1234567890abcdef1234567890abcdef123456"
                  "bare"
                  "prunable gitdir file points to non-existent location"
                  "" ]

        let result = parseWorktreeList output
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Multiple blocks separated by Environment.NewLine``() =
        let output =
            String.concat (Environment.NewLine)
                [ "worktree /repo/main"
                  "HEAD abc1234567890abcdef1234567890abcdef123456"
                  "branch refs/heads/main"
                  ""
                  "worktree /repo/dev"
                  "HEAD def4567890abcdef1234567890abcdef12345678"
                  "branch refs/heads/dev"
                  "" ]

        let result = parseWorktreeList output

        Assert.That(result.Length, Is.EqualTo(2))
        Assert.That(result[0].Path, Is.EqualTo(normalizePath "/repo/main"))
        Assert.That(result[1].Path, Is.EqualTo(normalizePath "/repo/dev"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseCommitOutputTests() =

    [<Test>]
    member _.``Valid three-line output parses to CommitInfo``() =
        let output = Some "abc123\nFix the bug\n2025-01-15T10:30:00+01:00"

        let result = parseCommitOutput "test-path" output

        Assert.That(result.IsSome, Is.True)
        let commit = result.Value
        Assert.That(commit.Hash, Is.EqualTo("abc123"))
        Assert.That(commit.Message, Is.EqualTo("Fix the bug"))
        Assert.That(commit.Time.Year, Is.EqualTo(2025))

    [<Test>]
    member _.``None input returns None``() =
        let result = parseCommitOutput "test-path" None
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Empty string returns None``() =
        let result = parseCommitOutput "test-path" (Some "")
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Too few lines returns None``() =
        let result = parseCommitOutput "test-path" (Some "abc123\nFix the bug")
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Too many lines returns None``() =
        let result = parseCommitOutput "test-path" (Some "abc123\nFix the bug\n2025-01-15T10:30:00+01:00\nextra")
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Invalid timestamp returns None``() =
        let result = parseCommitOutput "test-path" (Some "abc123\nFix the bug\nnot-a-date")
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``ISO 8601 timestamp with UTC offset parses correctly``() =
        let output = Some "deadbeef\nAdd feature\n2025-06-20T14:00:00+00:00"

        let result = parseCommitOutput "test-path" output

        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Time.Offset, Is.EqualTo(TimeSpan.Zero))

    [<Test>]
    member _.``Output with Environment.NewLine separators parses correctly``() =
        let output = Some $"abc123{Environment.NewLine}Fix the bug{Environment.NewLine}2025-01-15T10:30:00+01:00"

        let result = parseCommitOutput "test-path" output

        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Hash, Is.EqualTo("abc123"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CollectWorktreeGitDataTests() =

    [<Test>]
    member _.``HeadCommit is the actual merge HEAD rather than the last non-merge display commit``() =
        withTempDir "treemon-head-identity" (fun repoDir ->
            initRepoOnMain repoDir
            gitAssert repoDir "checkout -b side"
            gitAssert repoDir "commit --allow-empty -m side"
            gitAssert repoDir "checkout main"
            gitAssert repoDir "commit --allow-empty -m main-work"
            let nonMergeHead = gitOut repoDir "rev-parse HEAD"
            gitAssert repoDir "merge --no-ff side -m merge"
            let mergeHead = gitOut repoDir "rev-parse HEAD"

            let data = collectWorktreeGitData repoDir (Some "main") "origin" "main" |> runAsync

            Assert.That(data.HeadCommit, Is.EqualTo(mergeHead))
            Assert.That(data.HeadCommit, Is.Not.EqualTo(nonMergeHead)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseDirtyStatusTests() =

    [<TestCase(null, false)>]
    [<TestCase("", false)>]
    [<TestCase("   ", false)>]
    [<TestCase(" M tracked.txt", true)>]
    [<TestCase("?? untracked.txt", true)>]
    member _.``Porcelain status detects any returned change``(output: string, expected: bool) =
        let result = output |> Option.ofObj |> parseDirtyStatus
        Assert.That(result, Is.EqualTo(expected))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseDiffStatsTests() =

    [<Test>]
    member _.``Insertions and deletions both present``() =
        let result = parseDiffStats (Some " 5 files changed, 120 insertions(+), 45 deletions(-)")
        Assert.That(result, Is.EqualTo((HasContent, 120, 45)))

    [<Test>]
    member _.``Insertions only``() =
        let result = parseDiffStats (Some " 3 files changed, 80 insertions(+)")
        Assert.That(result, Is.EqualTo((HasContent, 80, 0)))

    [<Test>]
    member _.``Deletions only``() =
        let result = parseDiffStats (Some " 2 files changed, 30 deletions(-)")
        Assert.That(result, Is.EqualTo((HasContent, 0, 30)))

    [<Test>]
    member _.``A failed diff command is undetermined rather than empty``() =
        let result = parseDiffStats None
        Assert.That(result, Is.EqualTo((Undetermined, 0, 0)))

    [<Test>]
    member _.``Empty string reports no committed diff``() =
        let result = parseDiffStats (Some "")
        Assert.That(result, Is.EqualTo((Clean, 0, 0)))

    [<Test>]
    member _.``Whitespace-only string reports no committed diff``() =
        let result = parseDiffStats (Some "   ")
        Assert.That(result, Is.EqualTo((Clean, 0, 0)))

    [<Test>]
    member _.``Single insertion singular form``() =
        let result = parseDiffStats (Some " 1 file changed, 1 insertion(+)")
        Assert.That(result, Is.EqualTo((HasContent, 1, 0)))

    [<Test>]
    member _.``Single deletion singular form``() =
        let result = parseDiffStats (Some " 1 file changed, 1 deletion(-)")
        Assert.That(result, Is.EqualTo((HasContent, 0, 1)))

    [<Test>]
    member _.``Large numbers parsed correctly``() =
        let result = parseDiffStats (Some " 50 files changed, 12345 insertions(+), 6789 deletions(-)")
        Assert.That(result, Is.EqualTo((HasContent, 12345, 6789)))

    [<Test>]
    member _.``Commits with no net base diff produce no work metrics``() =
        Assert.Multiple(fun () ->
            Assert.That((createWorkMetrics Clean 3 0 0).IsNone, Is.True)
            Assert.That(
                (createWorkMetrics Undetermined 3 10 5).IsNone,
                Is.True,
                "An unreadable comparison must not be reported as measured work"))

// classifyUpstream turns a `git rev-parse --abbrev-ref @{u}` result into the three cases the
// merged-PR prune logic needs (see worktree-monitor.md, Merged-PR Persistence): a
// clean upstream, git's deterministic "no upstream", or a transient read failure that must NOT be
// mistaken for "no upstream". Pure, so no setup.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ClassifyUpstreamTests() =

    [<Test>]
    member _.``a clean read yields Upstream with the full tracking-ref name``() =
        // The remote prefix is stripped by getUpstreamBranch, not by this classification step.
        Assert.That(classifyUpstream (Ok "origin/main"), Is.EqualTo(Upstream "origin/main"))

    [<Test>]
    member _.``a clean read trims surrounding whitespace``() =
        Assert.That(classifyUpstream (Ok "  origin/feature/x \n"), Is.EqualTo(Upstream "origin/feature/x"))

    [<Test>]
    member _.``an anomalous empty success is a read failure, never a no-upstream``() =
        // exit 0 with no output should never happen for @{u}; treat it as unknown, not "no branch".
        Assert.That(classifyUpstream (Ok "   "), Is.EqualTo(UpstreamReadFailed))

    [<Test>]
    member _.``git's no-upstream-configured fatal is a clean NoUpstream``() =
        Assert.That(
            classifyUpstream (Error "fatal: no upstream configured for branch 'main'"),
            Is.EqualTo(NoUpstream))

    [<Test>]
    member _.``a detached HEAD fatal is a clean NoUpstream``() =
        Assert.That(
            classifyUpstream (Error "fatal: HEAD does not point to a branch"),
            Is.EqualTo(NoUpstream))

    [<Test>]
    member _.``an unborn branch (no such branch) is a clean NoUpstream``() =
        // git emits this when HEAD points to a branch with no commits yet - a stable no-branch state
        // carrying no merged-PR record, so it is safe to prune against (unlike "ambiguous argument").
        Assert.That(
            classifyUpstream (Error "fatal: no such branch: 'master'"),
            Is.EqualTo(NoUpstream))

    [<Test>]
    member _.``a configured-but-unresolvable upstream is a read failure, not a no-upstream``() =
        // git emits this when @{u} is configured but its remote-tracking ref is gone (e.g. a
        // merged-then-deleted branch after fetch --prune). The branch is UNKNOWN, not absent, so we
        // must skip pruning and keep its merged-PR record - not mistake it for "no branch".
        Assert.That(
            classifyUpstream (Error "fatal: ambiguous argument '@{u}': unknown revision or path not in the working tree."),
            Is.EqualTo(UpstreamReadFailed))

    [<Test>]
    member _.``no-upstream detection is case-insensitive``() =
        Assert.That(
            classifyUpstream (Error "FATAL: No Upstream Configured For Branch 'main'"),
            Is.EqualTo(NoUpstream))

    [<Test>]
    member _.``a timeout is a transient read failure, not a no-upstream``() =
        Assert.That(classifyUpstream (Error "Timed out after 60000ms"), Is.EqualTo(UpstreamReadFailed))

    [<Test>]
    member _.``an unrecognized git error is a transient read failure``() =
        // An index.lock / IO error must never be read as "no upstream" - that would wrongly prune.
        Assert.That(
            classifyUpstream (Error "fatal: Unable to create '/repo/.git/index.lock': File exists"),
            Is.EqualTo(UpstreamReadFailed))

    [<Test>]
    member _.``configured upstream survives deletion of a differently named remote branch``() =
        let refs =
            String.concat "\n"
                [ "local-name\torigin/provider-name"
                  "main\torigin/main" ]

        Assert.That(parseConfiguredUpstream "local-name" refs, Is.EqualTo(Some "origin/provider-name"))

    [<Test>]
    member _.``configured branch without tracking yields no fallback identity``() =
        Assert.That(parseConfiguredUpstream "local-name" "local-name\t", Is.EqualTo(None))

    [<Test>]
    member _.``missing configured branch yields no fallback identity``() =
        Assert.That(parseConfiguredUpstream "missing" "main\torigin/main", Is.EqualTo(None))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ComparisonContentTests() =

    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.``Content found in any layer wins over an unreadable one``(contentFirst: bool) =
        let combined =
            if contentFirst then ComparisonContent.combine HasContent Undetermined
            else ComparisonContent.combine Undetermined HasContent

        Assert.That(combined, Is.EqualTo(HasContent))

    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.``An unreadable layer keeps the worktree out of the clean verdict``(undeterminedFirst: bool) =
        let combined =
            if undeterminedFirst then ComparisonContent.combine Undetermined Clean
            else ComparisonContent.combine Clean Undetermined

        Assert.That(combined, Is.EqualTo(Undetermined))

    [<Test>]
    member _.``Only every layer reading empty is clean``() =
        Assert.That(ComparisonContent.combine Clean Clean, Is.EqualTo(Clean))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PrBranchNameTests() =

    let gitData branch upstream =
        { Path = "/repo/worktree"
          Branch = branch
          HeadCommit = "abc123"
          LastCommitMessage = "message"
          LastCommitTime = DateTimeOffset.UtcNow
          Upstream = upstream
          MainBehindCount = 0
          BaseRevision = None
          IsDirty = false
          Comparison = Clean
          WorkMetrics = None }

    [<Test>]
    member _.``resolved provider branch wins over the local branch``() =
        Assert.That(prBranchName (gitData "local-name" (Upstream "provider-name")), Is.EqualTo(Some "provider-name"))

    [<Test>]
    member _.``failed read falls back to the local branch``() =
        Assert.That(prBranchName (gitData "feature/deleted" UpstreamReadFailed), Is.EqualTo(Some "feature/deleted"))

    [<Test>]
    member _.``clean no-upstream state has no PR branch identity``() =
        Assert.That(prBranchName (gitData "feature/local" NoUpstream), Is.EqualTo(None))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DeletedUpstreamTests() =

    [<Test>]
    member _.``collect keeps configured provider branch after remote deletion``() =
        withTempDir "treemon-deleted-upstream" (fun tempDir ->
            let repoDir, _ = initRepoWithOrigin tempDir
            gitAssert repoDir "switch -c local-name"
            gitAssert repoDir "commit --allow-empty -m feature"
            gitAssert repoDir "push -u origin HEAD:provider-name"
            gitAssert repoDir "push origin --delete provider-name"
            gitAssert repoDir "fetch --prune origin"

            let gitData =
                collectWorktreeGitData repoDir (Some "local-name") "origin" "main"
                |> runAsync

            Assert.That(gitData.Upstream, Is.EqualTo(Upstream "provider-name"))
            Assert.That(prBranchName gitData, Is.EqualTo(Some "provider-name")))
