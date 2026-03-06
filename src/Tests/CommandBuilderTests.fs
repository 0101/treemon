module Tests.CommandBuilderTests

open NUnit.Framework
open Shared
open Server.SessionManager

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
        let result = buildInteractiveCommand (Some CodingToolProvider.Copilot) "/pr https://github.com/org/repo/pull/7"
        Assert.That(result, Is.EqualTo("copilot --yolo -i '/pr https://github.com/org/repo/pull/7'"))

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
