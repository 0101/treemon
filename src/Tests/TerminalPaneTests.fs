module Tests.TerminalPaneTests

open NUnit.Framework
open Shared
open TerminalPane

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalPaneStateTests() =

    let first = WorktreePath @"Q:\code\first"
    let second = WorktreePath @"Q:\code\second"

    [<Test>]
    member _.``Opening a closed terminal binds its starting state to the selected worktree``() =
        Assert.That(
            stateWhenOpened first EmbeddedTerminalState.Closed,
            Is.EqualTo(EmbeddedTerminalState.Starting first))

    [<Test>]
    member _.``Opening from another worktree does not replace a running terminal``() =
        let running = EmbeddedTerminalState.Running(first, "http://127.0.0.1:61234/")
        Assert.That(stateWhenOpened second running, Is.EqualTo(running))

    [<Test>]
    member _.``Failed terminal can be retried for another worktree``() =
        let failed = EmbeddedTerminalState.Failed(first, "ttyd failed")
        Assert.That(
            stateWhenOpened second failed,
            Is.EqualTo(EmbeddedTerminalState.Starting second))

    [<TestCase("http://127.0.0.1:61234/", true)>]
    [<TestCase("http://127.0.0.1:61234/client?arg=value", true)>]
    [<TestCase("https://127.0.0.1:61234/", false)>]
    [<TestCase("http://localhost:61234/", false)>]
    [<TestCase("javascript:alert(1)", false)>]
    [<TestCase("http://127.0.0.1:5000/", false)>]
    [<TestCase("http://127.0.0.1:70000/", false)>]
    [<TestCase("http://127.0.0.1:not-a-port/", false)>]
    member _.``Only loopback non-production ttyd endpoints are rendered``(endpoint: string, expectedSafe: bool) =
        Assert.That(safeEndpoint endpoint |> Option.isSome, Is.EqualTo(expectedSafe))

    [<TestCaseSource("terminalStates")>]
    member _.``Pane visibility follows terminal lifecycle``(state: EmbeddedTerminalState, expectedOpen: bool) =
        Assert.That(paneOpenForState state, Is.EqualTo(expectedOpen))

    static member terminalStates =
        let path = WorktreePath @"Q:\code\worktree"
        [| TestCaseData(EmbeddedTerminalState.Closed, false)
           TestCaseData(EmbeddedTerminalState.Starting path, true)
           TestCaseData(EmbeddedTerminalState.Running(path, "http://127.0.0.1:61234/"), true)
           TestCaseData(EmbeddedTerminalState.Failed(path, "failed"), true) |]
