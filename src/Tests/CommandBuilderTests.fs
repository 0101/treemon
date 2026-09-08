module Tests.CommandBuilderTests

open System
open System.Text
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
    member _.``prompt with special characters is preserved``() =
        let result = (build None (Interactive "/fix-build https://dev.azure.com/org/proj/_build/results?buildId=123&view=logs")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --yolo -i '/fix-build https://dev.azure.com/org/proj/_build/results?buildId=123&view=logs'"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ClaudeCodeCommandTests() =

    [<Test>]
    member _.``an interactive prompt is positional``() =
        let result = (build (Some CodingToolProvider.ClaudeCode) (Interactive "/pr https://github.com/org/repo/pull/7")).AsShellString
        Assert.That(result, Is.EqualTo("claude --permission-mode bypassPermissions '/pr https://github.com/org/repo/pull/7'"))

    [<Test>]
    member _.``a non-interactive prompt goes behind -p``() =
        let result = (build (Some CodingToolProvider.ClaudeCode) (NonInteractive "summarise the diff")).AsShellString
        Assert.That(result, Is.EqualTo("claude --permission-mode bypassPermissions -p 'summarise the diff'"))

    [<Test>]
    member _.``resume by id, and by --continue without one``() =
        Assert.Multiple(fun () ->
            Assert.That(
                (build (Some CodingToolProvider.ClaudeCode) (Resume(Some "abc-123"))).AsShellString,
                Is.EqualTo("claude --permission-mode bypassPermissions --resume 'abc-123'"))

            Assert.That(
                (build (Some CodingToolProvider.ClaudeCode) (Resume None)).AsShellString,
                Is.EqualTo("claude --permission-mode bypassPermissions --continue")))

    // A session id reaches this from stored activity, which arrives over HTTP, so it stays inside
    // the quoted argument rather than becoming a second command.
    [<Test>]
    member _.``a hostile resume id stays inside the quoted argument``() =
        let inv = build (Some CodingToolProvider.ClaudeCode) (Resume(Some "$(calc); '"))

        let expected =
            if OperatingSystem.IsWindows() then
                "claude --permission-mode bypassPermissions --resume '$(calc); '''"
            else
                @"claude --permission-mode bypassPermissions --resume '$(calc); '\'''"

        Assert.That(inv.AsShellString, Is.EqualTo expected)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ResumeCommandTests() =

    [<Test>]
    member _.``Resume with id includes yolo flag``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Resume (Some "abc-123"))
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --yolo --resume 'abc-123'"))

    // The resume id is interpolated into the PowerShell command submitted to the terminal, so a
    // hostile owner sessionId must remain inside the single-quoted argument.
    [<Test>]
    member _.``Resume single-quotes and escapes the id (no command injection)``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Resume (Some "$(calc); '"))

        let expected =
            if OperatingSystem.IsWindows() then
                "copilot --yolo --resume '$(calc); '''"
            else
                @"copilot --yolo --resume '$(calc); '\'''"

        Assert.That(inv.AsShellString, Is.EqualTo expected)

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
        Assert.That(inv.Args, Is.EqualTo("""-p 'use conflict skill to resolve conflicts' --allow-all --no-ask-user -s --autopilot"""))

    // The prompt is submitted to whichever shell the embedded terminal runs, and the two disagree on
    // how a quote is escaped, so the emitted form has to follow the platform rather than be fixed.
    [<Test>]
    member _.``an apostrophe is escaped for the terminal's own shell``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Interactive "it's broken")

        let expected =
            if OperatingSystem.IsWindows() then
                "copilot --yolo -i 'it''s broken'"
            else
                @"copilot --yolo -i 'it'\''s broken'"

        Assert.That(inv.AsShellString, Is.EqualTo expected)

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

    [<Test>]
    member _.``Claude Code invokes a skill as a slash command``() =
        let result = skillInvocation (Some CodingToolProvider.ClaudeCode) "investigate" "why is the build slow"
        Assert.That(result, Is.EqualTo("/investigate why is the build slow"))

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

    [<Test>]
    member _.``multi-line prompt becomes one control-free command with its UTF-8 payload intact``() =
        let prompt = "line a\r\nline b\nline c"
        let wrapped = skillInvocation (Some CodingToolProvider.CopilotCli) "investigate" prompt
        let cmd = (build (Some CodingToolProvider.CopilotCli) (Interactive wrapped)).AsShellString
        // The payload is carried as base64 either way; only the shell that decodes it differs.
        let prefix, suffix =
            if OperatingSystem.IsWindows() then
                "copilot --yolo -i ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('",
                "')))"
            else
                "copilot --yolo -i \"$(printf %s '", "' | base64 -d)\""

        Assert.That(cmd, Does.StartWith prefix)
        Assert.That(cmd, Does.EndWith suffix)

        let payload =
            cmd.Substring(
                prefix.Length,
                cmd.Length - prefix.Length - suffix.Length
            )
        let decoded =
            payload
            |> Convert.FromBase64String
            |> Encoding.UTF8.GetString

        Assert.Multiple(fun () ->
            Assert.That(decoded, Is.EqualTo wrapped)
            Assert.That(cmd |> Seq.exists Char.IsControl, Is.False)
            Assert.That(
                Server.TerminalHostClient.validateTerminalCommand cmd,
                Is.EqualTo(Ok cmd : Result<string, string>)
            ))
