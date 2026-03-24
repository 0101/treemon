module Tests.CommandBuilderTests

open NUnit.Framework
open Shared
open Server.CodingToolStatus

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BuildInteractiveCommandTests() =

    [<Test>]
    member _.``Claude provider produces claude command``() =
        let result = buildInteractiveCommand (Some CodingToolProvider.Claude) "/pr https://dev.azure.com/org/proj/_git/repo/pullrequest/42"
        Assert.That(result, Is.EqualTo("claude --dangerously-skip-permissions '/pr https://dev.azure.com/org/proj/_git/repo/pullrequest/42'"))

    [<Test>]
    member _.``Copilot provider produces copilot -i command``() =
        let result = buildInteractiveCommand (Some CodingToolProvider.Copilot) "use pr skill with https://github.com/org/repo/pull/7"
        Assert.That(result, Is.EqualTo("copilot --yolo -i 'use pr skill with https://github.com/org/repo/pull/7'"))

    [<Test>]
    member _.``None provider falls back to Claude``() =
        let result = buildInteractiveCommand None "create a pull request"
        Assert.That(result, Is.EqualTo("claude --dangerously-skip-permissions 'create a pull request'"))

    [<Test>]
    member _.``single quotes in prompt are escaped``() =
        let result = buildInteractiveCommand (Some CodingToolProvider.Claude) "fix the user's profile page"
        Assert.That(result, Is.EqualTo("claude --dangerously-skip-permissions 'fix the user''s profile page'"))

    [<Test>]
    member _.``single quotes in prompt are escaped for Copilot``() =
        let result = buildInteractiveCommand (Some CodingToolProvider.Copilot) "it's broken"
        Assert.That(result, Is.EqualTo("copilot --yolo -i 'it''s broken'"))

    [<Test>]
    member _.``prompt with special characters is preserved``() =
        let result = buildInteractiveCommand None "/fix-build https://dev.azure.com/org/proj/_build/results?buildId=123&view=logs"
        Assert.That(result, Is.EqualTo("claude --dangerously-skip-permissions '/fix-build https://dev.azure.com/org/proj/_build/results?buildId=123&view=logs'"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ActionPromptTests() =

    [<Test>]
    member _.``FixPr with Claude produces slash command``() =
        let result = actionPrompt (Some CodingToolProvider.Claude) (FixPr "https://dev.azure.com/org/proj/_git/repo/pullrequest/42") None
        Assert.That(result, Is.EqualTo("/pr https://dev.azure.com/org/proj/_git/repo/pullrequest/42"))

    [<Test>]
    member _.``FixPr with Copilot produces skill invocation``() =
        let result = actionPrompt (Some CodingToolProvider.Copilot) (FixPr "https://github.com/org/repo/pull/7") None
        Assert.That(result, Is.EqualTo("use pr skill with https://github.com/org/repo/pull/7"))

    [<Test>]
    member _.``FixBuild with Claude produces slash command``() =
        let result = actionPrompt (Some CodingToolProvider.Claude) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123") None
        Assert.That(result, Is.EqualTo("/fix-build https://dev.azure.com/org/proj/_build/results?buildId=123"))

    [<Test>]
    member _.``FixBuild with Copilot produces skill invocation``() =
        let result = actionPrompt (Some CodingToolProvider.Copilot) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123") None
        Assert.That(result, Is.EqualTo("use fix-build skill with https://dev.azure.com/org/proj/_build/results?buildId=123"))

    [<Test>]
    member _.``CreatePr produces same prompt for both providers``() =
        let claude = actionPrompt (Some CodingToolProvider.Claude) CreatePr None
        let copilot = actionPrompt (Some CodingToolProvider.Copilot) CreatePr None
        Assert.That(claude, Is.EqualTo("Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"))
        Assert.That(copilot, Is.EqualTo(claude))

    [<Test>]
    member _.``None provider falls back to Claude for FixPr``() =
        let result = actionPrompt None (FixPr "https://example.com/pr/1") None
        Assert.That(result, Is.EqualTo("/pr https://example.com/pr/1"))
