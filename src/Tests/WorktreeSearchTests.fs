module Tests.WorktreeSearchTests

open System
open NUnit.Framework
open Shared
open Navigation
open WorktreeSearch
open Tests.WorktreeFixtures

let private worktree path branch =
    { baseWt with
        Path = WorktreePath path
        Branch = branch }

let private repo name worktrees archived =
    { RepoId = RepoId name
      Name = name
      Worktrees = worktrees
      ArchivedWorktrees = archived
      IsReady = true
      IsCollapsed = false
      Provider = None
      BaseBranch = "main" }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WorktreeSearchMatchingTests() =

    [<Test>]
    member _.``compact query spans repository then worktree``() =
        let repos =
            [ repo
                "treemon"
                [ worktree "/treemon/kb-navigation" "kb-navigation"
                  worktree "/treemon/embed-terminal" "embed-terminal" ]
                [] ]

        let results = search repos "tremokb"

        Assert.That(results |> List.map _.Worktree.Branch, Is.EqualTo([ "kb-navigation" ]))
        Assert.Multiple(fun () ->
            Assert.That(results.Head.Matches.Repository, Is.Not.Empty)
            Assert.That(results.Head.Matches.Branch, Is.Not.Empty))

    [<Test>]
    member _.``cross-field fallback preserves repository-first order``() =
        let repos =
            [ repo
                "treemon"
                [ worktree "/treemon/kb-navigation" "kb-navigation" ]
                [] ]

        Assert.That(search repos "kbtremo", Is.Empty)

    [<Test>]
    member _.``compact query can span repository and worktree without spaces``() =
        let repos =
            [ repo
                "AITestAgent"
                [ worktree "/agent/playwright-fixtures" "playwright-fixtures" ]
                [] ]

        let results = search repos "agentplay"

        Assert.That(results |> List.map _.Worktree.Branch, Is.EqualTo([ "playwright-fixtures" ]))

    [<Test>]
    member _.``archived worktrees are excluded because they have no focusable card``() =
        let repos =
            [ repo
                "treemon"
                []
                [ { worktree "/treemon/archived" "archived" with IsArchived = true } ] ]

        Assert.That(search repos "", Is.Empty)

    [<Test>]
    member _.``empty query orders worktrees by recent session activity``() =
        let sessionAt hour worktree =
            { worktree with
                SessionActivityAt =
                    Some(DateTimeOffset(2026, 9, 4, hour, 0, 0, TimeSpan.Zero)) }

        let repos =
            [ repo
                "first"
                [ worktree "/first/no-session" "no-session-first"
                  worktree "/first/older" "older"
                  |> sessionAt 10 ]
                []
              repo
                "second"
                [ worktree "/second/newer" "newer"
                  |> sessionAt 12
                  worktree "/second/no-session" "no-session-second" ]
                [] ]

        Assert.That(
            search repos "" |> List.map _.Worktree.Branch,
            Is.EqualTo(
                [ "newer"
                  "older"
                  "no-session-first"
                  "no-session-second" ]
            )
        )

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WorktreeSearchStateTests() =

    let repos =
        [ repo
            "treemon"
            [ worktree "/treemon/kb-navigation" "kb-navigation"
              worktree "/treemon/embed-terminal" "embed-terminal" ]
            [] ]

    [<Test>]
    member _.``selection wraps and a new query resets it``() =
        let opened, _ = update repos Msg.Open State.Closed
        let queried, _ = update repos (Msg.QueryChanged "tremo") opened
        let wrapped, action =
            update
                repos
                (Msg.MoveSelection SelectionDirection.Up)
                queried

        match wrapped with
        | State.Open state ->
            Assert.That(
                state.SelectedPath,
                Is.EqualTo(Some(WorktreePath "/treemon/embed-terminal"))
            )
        | State.Closed -> Assert.Fail("Search should remain open while moving selection")

        Assert.That(action, Is.EqualTo(Action.RevealSelection))

        let reset, _ = update repos (Msg.QueryChanged "embed") wrapped

        match reset with
        | State.Open state ->
            Assert.That(
                state.SelectedPath,
                Is.EqualTo(Some(WorktreePath "/treemon/embed-terminal"))
            )
        | State.Closed -> Assert.Fail("Search should remain open after changing the query")

    [<Test>]
    member _.``choosing the selected result closes search and returns its worktree``() =
        let opened, _ = update repos Msg.Open State.Closed
        let queried, _ = update repos (Msg.QueryChanged "embed") opened
        let closed, action = update repos Msg.ChooseSelection queried

        Assert.That(closed, Is.EqualTo(State.Closed))

        match action with
        | Action.FocusWorktree path ->
            Assert.That(path, Is.EqualTo(WorktreePath "/treemon/embed-terminal"))
        | _ ->
            Assert.Fail("Choosing a result should request focus for that worktree")

    [<Test>]
    member _.``closing search restores the surface that opened it``() =
        let terminalId = EmbeddedTerminalId "search-origin"
        let dashboardOpen, _ = update repos Msg.Open State.Closed
        let canvasOpen, _ = update repos Msg.OpenFromCanvas State.Closed
        let terminalOpen, _ =
            update repos (Msg.OpenFromTerminal terminalId) State.Closed
        let _, dashboardAction = update repos Msg.Close dashboardOpen
        let _, canvasAction = update repos Msg.Close canvasOpen
        let _, terminalAction = update repos Msg.Close terminalOpen

        Assert.Multiple(fun () ->
            Assert.That(
                dashboardAction,
                Is.EqualTo(Action.RestoreFocus ReturnFocus.Dashboard)
            )
            Assert.That(
                canvasAction,
                Is.EqualTo(Action.RestoreFocus ReturnFocus.Canvas)
            )
            Assert.That(
                terminalAction,
                Is.EqualTo(
                    Action.RestoreFocus (
                        ReturnFocus.EmbeddedTerminal terminalId
                    )
                )
            ))

    [<Test>]
    member _.``selection remains attached to the same worktree when live data reorders``() =
        let opened, _ = update repos Msg.Open State.Closed
        let queried, _ = update repos (Msg.QueryChanged "tremo") opened

        let reordered =
            [ repo
                "treemon"
                [ worktree "/treemon/embed-terminal" "embed-terminal"
                  worktree "/treemon/kb-navigation" "kb-navigation" ]
                [] ]

        let _, action = update reordered Msg.ChooseSelection queried

        match action with
        | Action.FocusWorktree path ->
            Assert.That(path, Is.EqualTo(WorktreePath "/treemon/kb-navigation"))
        | _ ->
            Assert.Fail("Live refreshes should not move selection to a different worktree")

    [<Test>]
    member _.``pointer selection carries worktree identity across live reordering``() =
        let selectedPath = WorktreePath "/treemon/embed-terminal"
        let opened, _ = update repos Msg.Open State.Closed
        let selected, _ = update repos (Msg.SelectResult selectedPath) opened

        let reordered =
            [ repo
                "treemon"
                [ worktree "/treemon/embed-terminal" "embed-terminal"
                  worktree "/treemon/kb-navigation" "kb-navigation" ]
                [] ]

        let _, action = update reordered Msg.ChooseSelection selected

        Assert.That(action, Is.EqualTo(Action.FocusWorktree selectedPath))

    [<Test>]
    member _.``clicking a rendered result does not revalidate stale membership``() =
        let selectedPath = WorktreePath "/treemon/embed-terminal"
        let opened, _ = update repos Msg.Open State.Closed
        let closed, action =
            update
                []
                (Msg.ChooseResult selectedPath)
                opened

        Assert.Multiple(fun () ->
            Assert.That(closed, Is.EqualTo(State.Closed))
            Assert.That(action, Is.EqualTo(Action.FocusWorktree selectedPath)))
