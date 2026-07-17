module Tests.CommandBuilderTests

open NUnit.Framework
open Shared
open Server.CodingToolStatus
open Server.CodingToolCli

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BuildInteractiveCommandTests() =

    [<Test>]
    member _.``CopilotCli provider produces copilot -i command``() =
        let result = (build (Some CodingToolProvider.CopilotCli) (Interactive "use pr skill with https://github.com/org/repo/pull/7")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --yolo -i 'use pr skill with https://github.com/org/repo/pull/7'"))

    [<Test>]
    member _.``None provider falls back to the default``() =
        let result = (build None (Interactive "create a pull request")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --yolo -i 'create a pull request'"))

    [<Test>]
    member _.``single quotes in prompt are escaped``() =
        let result = (build (Some CodingToolProvider.CopilotCli) (Interactive "it's broken")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --yolo -i 'it''s broken'"))

    [<Test>]
    member _.``prompt with special characters is preserved``() =
        let result = (build None (Interactive "/fix-build https://dev.azure.com/org/proj/_build/results?buildId=123&view=logs")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --yolo -i '/fix-build https://dev.azure.com/org/proj/_build/results?buildId=123&view=logs'"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ResumeCommandTests() =

    [<Test>]
    member _.``Resume with id includes yolo flag``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Resume (Some "abc-123"))
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --yolo --resume 'abc-123'"))

    // F9 (security): the resume id is interpolated into a pwsh -EncodedCommand script, so it must be
    // single-quoted with embedded quotes doubled exactly like the Interactive prompt. Without this a
    // hostile owner sessionId (planted via /api/canvas/attribute) could inject PowerShell.
    [<Test>]
    member _.``Resume single-quotes and escapes the id (no command injection)``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Resume (Some "$(calc); '"))
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --yolo --resume '$(calc); '''"))

    [<Test>]
    member _.``Resume without id uses --continue with yolo flag``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Resume None)
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --yolo --continue"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type NonInteractiveCommandTests() =

    [<Test>]
    member _.``NonInteractive produces conflict command``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (NonInteractive "use conflict skill to resolve conflicts")
        Assert.That(inv.Executable, Is.EqualTo("copilot"))
        Assert.That(inv.Args, Is.EqualTo("""-p "use conflict skill to resolve conflicts" --allow-all --no-ask-user -s --autopilot"""))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PermissionFlagInvariantTests() =

    // NonInteractive is intentionally excluded from this invariant: Copilot
    // non-interactive uses --allow-all --no-ask-user -s --autopilot instead of --yolo.
    static member InvariantCases : obj array seq =
        seq {
            yield [| box CodingToolProvider.CopilotCli; box "--yolo"; box (Interactive "hello") |]
            yield [| box CodingToolProvider.CopilotCli; box "--yolo"; box (Resume (Some "abc")) |]
            yield [| box CodingToolProvider.CopilotCli; box "--yolo"; box (Resume None) |]
        }

    [<TestCaseSource("InvariantCases")>]
    member _.``Interactive and Resume always include the permission-skip flag``
            (provider: CodingToolProvider, permFlag: string, mode: InvocationMode) =
        let inv = build (Some provider) mode
        Assert.That(inv.Args, Does.Contain(permFlag))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ActionPromptTests() =

    [<Test>]
    member _.``FixPr produces skill invocation``() =
        let result = actionPrompt (Some CodingToolProvider.CopilotCli) (FixPr "https://github.com/org/repo/pull/7")
        Assert.That(result, Is.EqualTo("use pr skill with https://github.com/org/repo/pull/7"))

    [<Test>]
    member _.``FixBuild produces skill invocation``() =
        let result = actionPrompt (Some CodingToolProvider.CopilotCli) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123")
        Assert.That(result, Is.EqualTo("use fix-build skill with https://dev.azure.com/org/proj/_build/results?buildId=123"))

    [<Test>]
    member _.``CreatePr produces the fixed create-PR prompt``() =
        let result = actionPrompt (Some CodingToolProvider.CopilotCli) CreatePr
        Assert.That(result, Is.EqualTo("Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"))

    [<Test>]
    member _.``None provider falls back to the default for FixPr``() =
        let result = actionPrompt None (FixPr "https://example.com/pr/1")
        Assert.That(result, Is.EqualTo("use pr skill with https://example.com/pr/1"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SkillInvocationTests() =

    [<Test>]
    member _.``wraps arg as natural-language skill invocation``() =
        let result = skillInvocation (Some CodingToolProvider.CopilotCli) "investigate" "why is the build slow"
        Assert.That(result, Is.EqualTo("use investigate skill with why is the build slow"))

    [<Test>]
    member _.``None provider falls back to the default``() =
        let result = skillInvocation None "investigate" "trace the memory leak"
        Assert.That(result, Is.EqualTo("use investigate skill with trace the memory leak"))

    [<Test>]
    member _.``multi-line arg is preserved verbatim``() =
        let arg = "first line\nsecond line"
        let result = skillInvocation (Some CodingToolProvider.CopilotCli) "investigate" arg
        Assert.That(result, Is.EqualTo("use investigate skill with first line\nsecond line"))

    // Locks the refactor's byte-identical guarantee: actionPrompt's FixPr/FixBuild
    // cases must delegate to skillInvocation with the "pr"/"fix-build" skill names.
    [<Test>]
    member _.``matches actionPrompt FixPr``() =
        let url = "https://github.com/org/repo/pull/7"
        let viaHelper = skillInvocation (Some CodingToolProvider.CopilotCli) "pr" url
        let viaAction = actionPrompt (Some CodingToolProvider.CopilotCli) (FixPr url)
        Assert.That(viaHelper, Is.EqualTo(viaAction))

    [<Test>]
    member _.``matches actionPrompt FixBuild``() =
        let url = "https://dev.azure.com/org/proj/_build/results?buildId=123"
        let viaHelper = skillInvocation (Some CodingToolProvider.CopilotCli) "fix-build" url
        let viaAction = actionPrompt (Some CodingToolProvider.CopilotCli) (FixBuild url)
        Assert.That(viaHelper, Is.EqualTo(viaAction))

// Verification coverage for tm-quicklaunch-nvb (worktree prompt -> investigate launch).
// These reproduce the exact command-construction chain WorktreeApi.createWorktree performs on
// auto-launch: skillInvocation wraps the user's prompt, then CodingToolCli.build renders the
// interactive shell string. Kept as falsifiable, literal-output assertions.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type InvestigateLaunchCommandTests() =

    [<Test>]
    member _.``build wraps the investigate invocation as an interactive shell string``() =
        let wrapped = skillInvocation (Some CodingToolProvider.CopilotCli) "investigate" "clean up auth"
        let cmd = (build (Some CodingToolProvider.CopilotCli) (Interactive wrapped)).AsShellString
        Assert.That(cmd, Is.EqualTo("copilot --yolo -i 'use investigate skill with clean up auth'"))

    // A 3-line prompt survives the whole launch-command construction intact — every line
    // and the newlines between them remain inside the single-quoted argument (not truncated at the
    // first newline, no line dropped).
    [<Test>]
    member _.``multi-line prompt survives launch command construction intact``() =
        let prompt = "line a\nline b\nline c"
        let wrapped = skillInvocation (Some CodingToolProvider.CopilotCli) "investigate" prompt
        let cmd = (build (Some CodingToolProvider.CopilotCli) (Interactive wrapped)).AsShellString
        Assert.That(cmd, Is.EqualTo("copilot --yolo -i 'use investigate skill with line a\nline b\nline c'"))
        Assert.That(cmd, Does.Contain("line a"), "first line must be present")
        Assert.That(cmd, Does.Contain("line b"), "middle line must be present")
        Assert.That(cmd, Does.Contain("line c"), "last line must be present")
        Assert.That(cmd, Does.Contain("line a\nline b\nline c"), "newlines between all three lines must be preserved")
