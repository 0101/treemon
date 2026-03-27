module Tests.GitParsingTests

open System
open System.IO
open NUnit.Framework
open Server.GitWorktree
open Shared.PathUtils

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
type ParseDiffStatsTests() =

    [<Test>]
    member _.``Insertions and deletions both present``() =
        let result = parseDiffStats (Some " 5 files changed, 120 insertions(+), 45 deletions(-)")
        Assert.That(result, Is.EqualTo((120, 45)))

    [<Test>]
    member _.``Insertions only``() =
        let result = parseDiffStats (Some " 3 files changed, 80 insertions(+)")
        Assert.That(result, Is.EqualTo((80, 0)))

    [<Test>]
    member _.``Deletions only``() =
        let result = parseDiffStats (Some " 2 files changed, 30 deletions(-)")
        Assert.That(result, Is.EqualTo((0, 30)))

    [<Test>]
    member _.``None input returns zero pair``() =
        let result = parseDiffStats None
        Assert.That(result, Is.EqualTo((0, 0)))

    [<Test>]
    member _.``Empty string returns zero pair``() =
        let result = parseDiffStats (Some "")
        Assert.That(result, Is.EqualTo((0, 0)))

    [<Test>]
    member _.``Whitespace-only string returns zero pair``() =
        let result = parseDiffStats (Some "   ")
        Assert.That(result, Is.EqualTo((0, 0)))

    [<Test>]
    member _.``Single insertion singular form``() =
        let result = parseDiffStats (Some " 1 file changed, 1 insertion(+)")
        Assert.That(result, Is.EqualTo((1, 0)))

    [<Test>]
    member _.``Single deletion singular form``() =
        let result = parseDiffStats (Some " 1 file changed, 1 deletion(-)")
        Assert.That(result, Is.EqualTo((0, 1)))

    [<Test>]
    member _.``Large numbers parsed correctly``() =
        let result = parseDiffStats (Some " 50 files changed, 12345 insertions(+), 6789 deletions(-)")
        Assert.That(result, Is.EqualTo((12345, 6789)))
