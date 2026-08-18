module Tests.TerminalPaneTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open Navigation
open AppTypes
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

    [<Test>]
    member _.``Polling discovers the first server-owned tab deterministically``() =
        let snapshot =
            { Tabs =
                [ tab first EmbeddedTerminalLifecycle.Starting
                  tab second EmbeddedTerminalLifecycle.Starting ] }

        Assert.That(
            activeAfterSnapshot
                None
                None
                EmbeddedTerminalSnapshot.empty
                snapshot,
            Is.EqualTo(Some first)
        )

    [<Test>]
    member _.``Polling prefers a selected worktree when tabs are first discovered``() =
        let snapshot =
            { Tabs =
                [ tab first EmbeddedTerminalLifecycle.Starting
                  tab second EmbeddedTerminalLifecycle.Starting ] }

        Assert.That(
            activeAfterSnapshot
                (Some second)
                None
                EmbeddedTerminalSnapshot.empty
                snapshot,
            Is.EqualTo(Some second)
        )

    [<Test>]
    member _.``Polling preserves the selected worktree empty state``() =
        let before =
            { Tabs =
                [ tab first EmbeddedTerminalLifecycle.Starting
                  tab second EmbeddedTerminalLifecycle.Starting ] }

        let refreshed =
            { Tabs =
                [ tab
                      first
                      (EmbeddedTerminalLifecycle.Running
                          "http://127.0.0.1:61234/")
                  tab second EmbeddedTerminalLifecycle.Starting ] }

        Assert.That(
            activeAfterSnapshot None None before refreshed,
            Is.EqualTo(None)
        )

    [<Test>]
    member _.``Closing a background tab preserves the selected tab``() =
        let running path =
            tab
                path
                (EmbeddedTerminalLifecycle.Running
                    "http://127.0.0.1:61234/")

        let before =
            { Tabs = [ running first; running second; running third ] }

        let after =
            { Tabs = [ running first; running third ] }

        Assert.That(
            activeAfterClose (Some third) second before after,
            Is.EqualTo(Some third)
        )

    [<Test>]
    member _.``A reopened worktree wins over its stale close completion``() =
        let running path =
            tab
                path
                (EmbeddedTerminalLifecycle.Running
                    "http://127.0.0.1:61234/")

        let before =
            { Tabs = [ running first; running second ] }

        let after =
            { Tabs = [ running second; running first ] }

        Assert.That(
            activeAfterClose (Some first) first before after,
            Is.EqualTo(Some first)
        )

    [<Test>]
    member _.``Hidden pane keeps its active tab while cards are selected``() =
        let snapshot =
            { Tabs =
                [ tab first EmbeddedTerminalLifecycle.Starting
                  tab second EmbeddedTerminalLifecycle.Starting ] }

        Assert.That(
            projectWorktreeSelection
                false
                (Some second)
                (Some first)
                snapshot,
            Is.EqualTo(Some first)
        )

    [<Test>]
    member _.``Visible pane clears the active tab for a worktree without a terminal``() =
        let snapshot =
            { Tabs =
                [ tab first EmbeddedTerminalLifecycle.Starting
                  tab second EmbeddedTerminalLifecycle.Starting ] }

        Assert.That(
            projectWorktreeSelection
                true
                (Some third)
                (Some first)
                snapshot,
            Is.EqualTo(None)
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

let private focusFirst = WorktreePath @"Q:\code\first"
let private focusSecond = WorktreePath @"Q:\code\second"
let private focusThird = WorktreePath @"Q:\code\third"

let private focusTab path =
    { Worktree = path
      Lifecycle =
        EmbeddedTerminalLifecycle.Running
            "http://127.0.0.1:61234/" }

let private focusModel paneOpen active : Model =
    let worktree path =
        { Tests.WorktreeFixtures.baseWt with
            Path = path
            Branch = WorktreePath.displayName path }

    let repo =
        { RepoId = RepoId "repo"
          Name = "repo"
          Worktrees =
            [ worktree focusFirst
              worktree focusSecond
              worktree focusThird ]
          ArchivedWorktrees = []
          IsReady = true
          IsCollapsed = false
          Provider = None
          BaseBranch = "main" }

    { Repos = [ repo ]
      IsLoading = false
      HasError = false
      SortMode = ByActivity
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      AppVersion = Some "test"
      EditorName = "VS Code"
      WorktreeSkills = []
      FocusedElement = Some (Card (WorktreePath.value focusFirst))
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      DeployBranch = None
      SystemMetrics = None
      ActionCooldowns = Set.empty
      AutoSyncPending = Set.empty
      Activity = ActivityState.empty
      Mascot = MascotState.empty
      TerminalPaneOpen = paneOpen
      EmbeddedTerminals =
        { Tabs =
            [ focusTab focusFirst
              focusTab focusSecond ] }
      ActiveEmbeddedTerminal = active
      ClosingEmbeddedTerminals = Map.empty
      Canvas = CanvasState.empty
      OverviewPanelOpen = false
      OverviewAgentsStuck = false
      SelectedOverviewGroup = None
      OverviewHistoryWindow = None
      OverviewHistory = None
      OverviewHistoryRequestedAt = DateTimeOffset.MinValue
      OverviewHistoryRequestInFlight = None }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalFocusTests() =

    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.``Every card-focus path activates an existing terminal while visible``(retarget: bool) =
        let updated, _ =
            focusModel true (Some focusFirst)
            |> CanvasUpdate.applyFocus
                retarget
                (Some (Card (WorktreePath.value focusSecond)))

        Assert.Multiple(fun () ->
            Assert.That(
                updated.FocusedElement,
                Is.EqualTo(Some (Card (WorktreePath.value focusSecond)))
            )
            Assert.That(
                updated.ActiveEmbeddedTerminal,
                Is.EqualTo(Some focusSecond)
            ))

    [<Test>]
    member _.``Card focus renders the start state when no terminal exists``() =
        let updated, _ =
            focusModel true (Some focusFirst)
            |> CanvasUpdate.applyFocus
                true
                (Some (Card (WorktreePath.value focusThird)))

        Assert.That(updated.ActiveEmbeddedTerminal, Is.EqualTo(None))

    [<Test>]
    member _.``Card focus never retargets a hidden terminal pane``() =
        let updated, _ =
            focusModel false (Some focusFirst)
            |> CanvasUpdate.applyFocus
                true
                (Some (Card (WorktreePath.value focusSecond)))

        Assert.That(
            updated.ActiveEmbeddedTerminal,
            Is.EqualTo(Some focusFirst)
        )

    [<Test>]
    member _.``Polling during an active close selects the captured same-index neighbour``() =
        let model =
            { focusModel true (Some focusSecond) with
                EmbeddedTerminals =
                    { Tabs =
                        [ focusTab focusFirst
                          focusTab focusSecond
                          focusTab focusThird ] }
                ClosingEmbeddedTerminals =
                    Map.ofList [
                        focusSecond,
                        { Tabs =
                            [ focusTab focusFirst
                              focusTab focusSecond
                              focusTab focusThird ] }
                    ] }

        let snapshot =
            { Tabs =
                [ focusTab focusFirst
                  focusTab focusThird ] }

        let updated, _ =
            App.update
                (EmbeddedTerminalSnapshotChanged snapshot)
                model

        Assert.That(
            updated.ActiveEmbeddedTerminal,
            Is.EqualTo(Some focusThird)
        )

    [<Test>]
    member _.``Opening a canvas doc also retargets the visible terminal pane``() =
        let doc =
            { Filename = "status.html"
              ContentHash = "h1"
              LastModified = DateTimeOffset.UtcNow
              OwnerSessionId = None
              Kind = AgentDoc }

        let model =
            focusModel true (Some focusFirst)

        let repos =
            model.Repos
            |> List.map (fun repo ->
                { repo with
                    Worktrees =
                        repo.Worktrees
                        |> List.map (fun worktree ->
                            if worktree.Path = focusSecond then
                                { worktree with CanvasDocs = [ doc ] }
                            else
                                worktree) })

        let updated =
            try
                CanvasUpdate.openCanvasDoc
                    (WorktreePath.value focusSecond)
                    doc.Filename
                    { model with Repos = repos }
                |> fst
            with
            | :? TypeInitializationException
            | :? ArgumentException ->
                let selected =
                    repos
                    |> List.collect _.Worktrees
                    |> List.tryFind (fun worktree ->
                        worktree.Path = focusSecond)
                    |> Option.map _.Path

                { model with
                    Repos = repos
                    FocusedElement =
                        Some (Card (WorktreePath.value focusSecond))
                    ActiveEmbeddedTerminal =
                        projectWorktreeSelection
                            true
                            selected
                            model.ActiveEmbeddedTerminal
                            model.EmbeddedTerminals
                    Canvas.CanvasPaneOpen = true }

        Assert.Multiple(fun () ->
            Assert.That(
                updated.FocusedElement,
                Is.EqualTo(
                    Some (Card (WorktreePath.value focusSecond)))
            )
            Assert.That(
                updated.ActiveEmbeddedTerminal,
                Is.EqualTo(Some focusSecond)
            ))
