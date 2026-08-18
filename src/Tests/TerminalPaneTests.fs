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
    let third = WorktreePath @"Q:\code\third"
    let tab path lifecycle =
        { Worktree = path
          Lifecycle = lifecycle }

    [<Test>]
    member _.``Opening an absent terminal appends its starting tab``() =
        Assert.That(
            snapshotWhenOpened first EmbeddedTerminalSnapshot.empty,
            Is.EqualTo(
                { Tabs =
                    [ tab first EmbeddedTerminalLifecycle.Starting ] }
            )
        )

    [<Test>]
    member _.``Opening another worktree preserves the running tab``() =
        let running =
            tab
                first
                (EmbeddedTerminalLifecycle.Running "http://127.0.0.1:61234/")

        Assert.That(
            snapshotWhenOpened second { Tabs = [ running ] },
            Is.EqualTo(
                { Tabs =
                    [ running
                      tab second EmbeddedTerminalLifecycle.Starting ] }
            )
        )

    [<Test>]
    member _.``Failed terminal retry keeps its tab position``() =
        let other = tab second EmbeddedTerminalLifecycle.Starting
        let failed = tab first (EmbeddedTerminalLifecycle.Failed "ttyd failed")

        Assert.That(
            snapshotWhenOpened first { Tabs = [ failed; other ] },
            Is.EqualTo(
                { Tabs =
                    [ tab first EmbeddedTerminalLifecycle.Starting
                      other ] }
            )

    [<Test>]
    member _.``Close selects the same-index neighbour from the captured tab order``() =
            let running path =
                tab
                    path
                    (EmbeddedTerminalLifecycle.Running "http://127.0.0.1:61234/")

            let before =
                { Tabs = [ running first; running second; running third ] }

            let after =
                { Tabs = [ running first; running third ] }

            Assert.That(
                nextActiveAfterClose second before after,
                Is.EqualTo(Some third)
            )
        )

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

    [<Test>]
    member _.``Pane visibility follows whether the registry has tabs``() =
        Assert.Multiple(fun () ->
            Assert.That(
                paneOpenForSnapshot EmbeddedTerminalSnapshot.empty,
                Is.False
            )

            Assert.That(
                paneOpenForSnapshot
                    { Tabs =
                        [ tab
                              first
                              (EmbeddedTerminalLifecycle.Failed "failed") ] },
                Is.True
            ))
