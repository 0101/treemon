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
    member _.``Claude provider produces claude command``() =
        let result = (build (Some CodingToolProvider.Claude) (Interactive "/pr https://dev.azure.com/org/proj/_git/repo/pullrequest/42")).AsShellString
        Assert.That(result, Is.EqualTo("claude --dangerously-skip-permissions '/pr https://dev.azure.com/org/proj/_git/repo/pullrequest/42'"))

    [<Test>]
    member _.``Copilot provider produces copilot -i command``() =
        let result = (build (Some CodingToolProvider.Copilot) (Interactive "use pr skill with https://github.com/org/repo/pull/7")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --yolo -i 'use pr skill with https://github.com/org/repo/pull/7'"))

    [<Test>]
    member _.``None provider falls back to Copilot``() =
        let result = (build None (Interactive "create a pull request")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --yolo -i 'create a pull request'"))

    [<Test>]
    member _.``single quotes in prompt are escaped``() =
        let result = (build (Some CodingToolProvider.Claude) (Interactive "fix the user's profile page")).AsShellString
        Assert.That(result, Is.EqualTo("claude --dangerously-skip-permissions 'fix the user''s profile page'"))

    [<Test>]
    member _.``single quotes in prompt are escaped for Copilot``() =
        let result = (build (Some CodingToolProvider.Copilot) (Interactive "it's broken")).AsShellString
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
    member _.``Claude Resume with id includes permission flag``() =
        let inv = build (Some CodingToolProvider.Claude) (Resume (Some "abc-123"))
        Assert.That(inv.AsShellString, Is.EqualTo("claude --dangerously-skip-permissions --resume abc-123"))

    [<Test>]
    member _.``Claude Resume without id uses --continue with permission flag``() =
        let inv = build (Some CodingToolProvider.Claude) (Resume None)
        Assert.That(inv.AsShellString, Is.EqualTo("claude --dangerously-skip-permissions --continue"))

    [<Test>]
    member _.``Copilot Resume with id includes yolo flag``() =
        let inv = build (Some CodingToolProvider.Copilot) (Resume (Some "abc-123"))
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --yolo --resume abc-123"))

    [<Test>]
    member _.``Copilot Resume without id uses --continue with yolo flag``() =
        let inv = build (Some CodingToolProvider.Copilot) (Resume None)
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --yolo --continue"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type NonInteractiveCommandTests() =

    [<Test>]
    member _.``Claude NonInteractive produces conflict command``() =
        let inv = build (Some CodingToolProvider.Claude) (NonInteractive "/conflict")
        Assert.That(inv.Executable, Is.EqualTo("claude"))
        Assert.That(inv.Args, Is.EqualTo("""-p "/conflict" --dangerously-skip-permissions"""))

    [<Test>]
    member _.``Copilot NonInteractive produces conflict command``() =
        let inv = build (Some CodingToolProvider.Copilot) (NonInteractive "use conflict skill to resolve conflicts")
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
            yield [| box CodingToolProvider.Claude; box "--dangerously-skip-permissions"; box (Interactive "hello") |]
            yield [| box CodingToolProvider.Claude; box "--dangerously-skip-permissions"; box (Resume (Some "abc")) |]
            yield [| box CodingToolProvider.Claude; box "--dangerously-skip-permissions"; box (Resume None) |]
            yield [| box CodingToolProvider.Copilot; box "--yolo"; box (Interactive "hello") |]
            yield [| box CodingToolProvider.Copilot; box "--yolo"; box (Resume (Some "abc")) |]
            yield [| box CodingToolProvider.Copilot; box "--yolo"; box (Resume None) |]
        }

    [<TestCaseSource("InvariantCases")>]
    member _.``Interactive and Resume always include permission-skip flag``
            (provider: CodingToolProvider, permFlag: string, mode: InvocationMode) =
        let inv = build (Some provider) mode
        Assert.That(inv.Args, Does.Contain(permFlag))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ActionPromptTests() =

    [<Test>]
    member _.``FixPr with Claude produces slash command``() =
        let result = actionPrompt (Some CodingToolProvider.Claude) (FixPr "https://dev.azure.com/org/proj/_git/repo/pullrequest/42")
        Assert.That(result, Is.EqualTo("/pr https://dev.azure.com/org/proj/_git/repo/pullrequest/42"))

    [<Test>]
    member _.``FixPr with Copilot produces skill invocation``() =
        let result = actionPrompt (Some CodingToolProvider.Copilot) (FixPr "https://github.com/org/repo/pull/7")
        Assert.That(result, Is.EqualTo("use pr skill with https://github.com/org/repo/pull/7"))

    [<Test>]
    member _.``FixBuild with Claude produces slash command``() =
        let result = actionPrompt (Some CodingToolProvider.Claude) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123")
        Assert.That(result, Is.EqualTo("/fix-build https://dev.azure.com/org/proj/_build/results?buildId=123"))

    [<Test>]
    member _.``FixBuild with Copilot produces skill invocation``() =
        let result = actionPrompt (Some CodingToolProvider.Copilot) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123")
        Assert.That(result, Is.EqualTo("use fix-build skill with https://dev.azure.com/org/proj/_build/results?buildId=123"))

    [<Test>]
    member _.``CreatePr produces same prompt for both providers``() =
        let claude = actionPrompt (Some CodingToolProvider.Claude) CreatePr
        let copilot = actionPrompt (Some CodingToolProvider.Copilot) CreatePr
        Assert.That(claude, Is.EqualTo("Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"))
        Assert.That(copilot, Is.EqualTo(claude))

    [<Test>]
    member _.``None provider falls back to Copilot for FixPr``() =
        let result = actionPrompt None (FixPr "https://example.com/pr/1")
        Assert.That(result, Is.EqualTo("use pr skill with https://example.com/pr/1"))
