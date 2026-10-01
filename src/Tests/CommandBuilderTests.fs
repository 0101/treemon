module Tests.CommandBuilderTests

open System
open NUnit.Framework
open Shared
open Server.CodingToolStatus
open Server.CodingToolCli

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StartCommandTests() =

    [<Test>]
    member _.``Start produces the exact Copilot agent command``() =
        let result = (build (Some CodingToolProvider.CopilotCli) Start).AsShellString

        Assert.That(result, Is.EqualTo("copilot --yolo"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BuildNewSessionCommandTests() =

    [<Test>]
    member _.``New session selects its durable identity without a shell prompt``() =
        let id = "f68f73bd-b6ef-4598-b64b-346a263244d4"
        let result = (build (Some CodingToolProvider.CopilotCli) (NewSession id)).AsShellString
        Assert.That(result, Is.EqualTo($"copilot --experimental --yolo --session-id='{id}'"))

    [<Test>]
    member _.``None provider falls back to the default``() =
        let result = (build None (NewSession "session-id")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --experimental --yolo --session-id='session-id'"))

    [<Test>]
    member _.``New session quotes its identity at the shell boundary``() =
        let result = (build None (NewSession "$(calc); '")).AsShellString
        Assert.That(result, Is.EqualTo("copilot --experimental --yolo --session-id='$(calc); '''"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ResumeCommandTests() =

    [<Test>]
    member _.``Resume with id uses the direct startup session selector``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Resume (Some "abc-123"))
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --experimental --yolo --session-id='abc-123'"))

    // The resume id is interpolated into the PowerShell command submitted to the terminal, so a
    // hostile owner sessionId must remain inside the single-quoted argument.
    [<Test>]
    member _.``Resume single-quotes and escapes the id (no command injection)``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Resume (Some "$(calc); '"))
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --experimental --yolo --session-id='$(calc); '''"))

    [<Test>]
    member _.``Resume without id uses --continue with yolo flag``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (Resume None)
        Assert.That(inv.AsShellString, Is.EqualTo("copilot --experimental --yolo --continue"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type NonInteractiveCommandTests() =

    [<Test>]
    member _.``NonInteractive produces conflict command``() =
        let inv = build (Some CodingToolProvider.CopilotCli) (NonInteractive "use conflict skill to resolve conflicts")
        Assert.That(inv.Executable, Is.EqualTo("copilot"))
        Assert.That(inv.Args, Is.EqualTo("""--experimental -p "use conflict skill to resolve conflicts" --allow-all --no-ask-user -s --autopilot"""))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PermissionFlagInvariantTests() =

    // NonInteractive is intentionally excluded from this invariant: Copilot
    // non-interactive uses --allow-all --no-ask-user -s --autopilot instead of --yolo.
    static member InvariantCases : obj array seq =
        seq {
            yield [| box CodingToolProvider.CopilotCli; box "--yolo"; box Start |]
            yield [| box CodingToolProvider.CopilotCli; box "--yolo"; box (NewSession "session-id") |]
            yield [| box CodingToolProvider.CopilotCli; box "--yolo"; box (Resume (Some "abc")) |]
            yield [| box CodingToolProvider.CopilotCli; box "--yolo"; box (Resume None) |]
        }

    [<TestCaseSource("InvariantCases")>]
    member _.``NewSession and Resume always include the permission-skip flag``
            (provider: CodingToolProvider, permFlag: string, mode: InvocationMode) =
        let inv = build (Some provider) mode
        Assert.That(inv.Args, Does.Contain(permFlag))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ExtensionDiscoveryFlagInvariantTests() =

    static member InvocationCases : obj array seq =
        seq {
            yield [| box (NewSession "session-id") |]
            yield [| box (Resume (Some "abc")) |]
            yield [| box (Resume None) |]
            yield [| box (NonInteractive "hello") |]
        }

    [<TestCaseSource("InvocationCases")>]
    member _.``Every Copilot invocation enables extension discovery``(mode: InvocationMode) =
        let inv = build (Some CodingToolProvider.CopilotCli) mode
        Assert.That(inv.Args, Does.StartWith("--experimental "))

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
