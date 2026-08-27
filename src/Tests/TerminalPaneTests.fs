module Tests.TerminalPaneTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open Navigation
open AppTypes
open TerminalPane

let private terminalId value =
    EmbeddedTerminalId value

let private first = WorktreePath @"Q:\code\first"
let private second = WorktreePath @"Q:\code\second"
let private third = WorktreePath @"Q:\code\third"
let private firstOne = terminalId "first-1"
let private firstTwo = terminalId "first-2"
let private secondOne = terminalId "second-1"
let private thirdOne = terminalId "third-1"

let private tab terminalId path lifecycle =
    { Id = terminalId
      Worktree = path
      Lifecycle = lifecycle }

let private running terminalId path port =
    tab
        terminalId
        path
        (EmbeddedTerminalLifecycle.Running
            $"http://127.0.0.1:{port}/")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalPaneStateTests() =

    [<Test>]
    member _.``Visible tabs are limited to the selected worktree``() =
        let snapshot =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61232
                  running firstTwo first 61233 ] }

        Assert.That(
            snapshot
            |> tabsForWorktree first
            |> List.map _.Id,
            Is.EqualTo([ firstOne; firstTwo ])
        )

    [<Test>]
    member _.``Each worktree keeps an independent active terminal``() =
        let snapshot =
            { Tabs =
                [ running firstOne first 61231
                  running firstTwo first 61232
                  running secondOne second 61233 ] }

        let selections =
            Map.ofList [
                first, firstTwo
                second, secondOne
            ]

        Assert.Multiple(fun () ->
            Assert.That(
                activeTerminalId (Some first) selections snapshot,
                Is.EqualTo(Some firstTwo)
            )

            Assert.That(
                activeTerminalId (Some second) selections snapshot,
                Is.EqualTo(Some secondOne)
            ))

    [<Test>]
    member _.``A worktree without an explicit selection uses its first terminal``() =
        let snapshot =
            { Tabs =
                [ running firstOne first 61231
                  running firstTwo first 61232 ] }

        Assert.That(
            activeTerminalId (Some first) Map.empty snapshot,
            Is.EqualTo(Some firstOne)
        )

    [<Test>]
    member _.``Selecting a tab changes only its worktree selection``() =
        let snapshot =
            { Tabs =
                [ running firstOne first 61231
                  running firstTwo first 61232
                  running secondOne second 61233 ] }

        let selections =
            Map.ofList [
                first, firstOne
                second, secondOne
            ]

        let updated =
            selections
            |> selectTerminal firstTwo snapshot

        Assert.That(
            updated,
            Is.EqualTo(
                Map.ofList [
                    first, firstTwo
                    second, secondOne
                ])
        )

    [<Test>]
    member _.``A completed start identifies the new terminal within its worktree``() =
        let before =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61232 ] }

        let after =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61232
                  running firstTwo first 61233 ] }

        Assert.That(
            startedTerminalId first before after,
            Is.EqualTo(Some firstTwo)
        )

    [<Test>]
    member _.``Closing the active tab selects its same-worktree neighbour``() =
        let before =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61232
                  running firstTwo first 61233
                  running thirdOne first 61234 ] }

        let after =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61232
                  running thirdOne first 61234 ] }

        let updated =
            Map.ofList [
                first, firstTwo
                second, secondOne
            ]
            |> reconcileSelections before after

        Assert.That(
            updated,
            Is.EqualTo(
                Map.ofList [
                    first, thirdOne
                    second, secondOne
                ])
        )

    [<Test>]
    member _.``Host replacement preserves the selected ordinal within a worktree``() =
        let replacementFirst = terminalId "replacement-first"
        let replacementSecond = terminalId "replacement-second"

        let before =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61232
                  running firstTwo first 61233 ] }

        let after =
            { Tabs =
                [ running replacementFirst first 61241
                  running (terminalId "replacement-other") second 61242
                  running replacementSecond first 61243 ] }

        let updated =
            Map.ofList [ first, firstTwo ]
            |> reconcileSelections before after

        Assert.That(
            updated,
            Is.EqualTo(Map.ofList [ first, replacementSecond ])
        )

    [<Test>]
    member _.``Closing the last terminal clears only that worktree selection``() =
        let before =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61232 ] }

        let after =
            { Tabs = [ running secondOne second 61232 ] }

        let updated =
            Map.ofList [
                first, firstOne
                second, secondOne
            ]
            |> reconcileSelections before after

        Assert.That(
            updated,
            Is.EqualTo(Map.ofList [ second, secondOne ])
        )

    [<Test>]
    member _.``Start progress and errors are scoped by worktree``() =
        let states =
            Map.empty
            |> setStartState first TerminalStartState.Starting
            |> setStartState second (TerminalStartState.Failed "ttyd failed")

        Assert.Multiple(fun () ->
            Assert.That(isStarting first states, Is.True)
            Assert.That(isStarting second states, Is.False)
            Assert.That(
                tryStartState second states,
                Is.EqualTo(Some(TerminalStartState.Failed "ttyd failed"))
            )
            Assert.That(
                states |> clearStartState first |> tryStartState first,
                Is.EqualTo(None)
            ))

    [<Test>]
    member _.``Interrupted tabs keep polling enabled for host recovery``() =
        let interrupted =
            { Tabs =
                [ tab
                      firstOne
                      first
                      (EmbeddedTerminalLifecycle.Interrupted
                          "host exited") ] }

        Assert.That(hasLiveTabs interrupted, Is.True)

    [<TestCase("http://127.0.0.1:61234/", true)>]
    [<TestCase("http://127.0.0.1:61234/client?arg=value", true)>]
    [<TestCase("https://127.0.0.1:61234/", false)>]
    [<TestCase("http://localhost:61234/", false)>]
    [<TestCase("javascript:alert(1)", false)>]
    [<TestCase("http://127.0.0.1:5000/", false)>]
    [<TestCase("http://127.0.0.1:70000/", false)>]
    [<TestCase("http://127.0.0.1:not-a-port/", false)>]
    member _.``Only loopback non-production ttyd endpoints are rendered``(endpoint: string, expectedSafe: bool) =
        Assert.That(
            safeEndpoint endpoint |> Option.isSome,
            Is.EqualTo(expectedSafe)
        )

let private focusModel : Model =
    let worktree path =
        { Tests.WorktreeFixtures.baseWt with
            Path = path
            Branch = WorktreePath.displayName path }

    let repo =
        { RepoId = RepoId "repo"
          Name = "repo"
          Worktrees =
            [ worktree first
              worktree second
              worktree third ]
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
      FocusedElement = Some (Card (WorktreePath.value first))
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      DeployBranch = None
      SystemMetrics = None
      ActionCooldowns = Set.empty
      AutoSyncPending = Set.empty
      Activity = ActivityState.empty
      Mascot = MascotState.empty
      TerminalPaneOpen = true
      TerminalPaneTarget = None
      EmbeddedTerminals =
        { Tabs =
            [ running firstOne first 61231
              running firstTwo first 61232
              running secondOne second 61233 ] }
      ActiveEmbeddedTerminals =
        Map.ofList [
            first, firstTwo
            second, secondOne
        ]
      EmbeddedTerminalStarts = Map.empty
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

    [<Test>]
    member _.``Card focus changes visible terminals without overwriting worktree selections``() =
        let updated, _ =
            focusModel
            |> CanvasUpdate.applyFocus
                true
                (Some (Card (WorktreePath.value second)))

        Assert.Multiple(fun () ->
            Assert.That(
                updated.FocusedElement,
                Is.EqualTo(Some (Card (WorktreePath.value second)))
            )
            Assert.That(
                updated.ActiveEmbeddedTerminals,
                Is.EqualTo(focusModel.ActiveEmbeddedTerminals)
            )
            Assert.That(
                activeTerminalId
                    (Some second)
                    updated.ActiveEmbeddedTerminals
                    updated.EmbeddedTerminals,
                Is.EqualTo(Some secondOne)
            ))

    [<Test>]
    member _.``Opening an embedded terminal targets its worktree without moving the canvas``() =
        let canvasDoc filename kind =
            { Filename = filename
              ContentHash = filename
              LastModified = DateTimeOffset.MinValue
              OwnerSessionId = None
              Kind = kind }

        let repos =
            focusModel.Repos
            |> List.map (fun repo ->
                { repo with
                    Worktrees =
                        repo.Worktrees
                        |> List.map (fun worktree ->
                            match worktree.Path with
                            | path when path = first ->
                                { worktree with
                                    CanvasDocs =
                                        [ canvasDoc "beads.html" SystemView
                                          canvasDoc "status.html" AgentDoc ] }
                            | path when path = second ->
                                { worktree with
                                    CanvasDocs = [ canvasDoc "beads.html" SystemView ] }
                            | _ -> worktree) })

        let model =
            { focusModel with
                Repos = repos
                Canvas.CanvasPaneOpen = true
                Canvas.ActiveCanvasDoc =
                    Map.ofList [
                        WorktreePath.value first, "status.html"
                    ] }

        let updated, _ =
            App.beginEmbeddedTerminalStart second model

        Assert.Multiple(fun () ->
            Assert.That(updated.FocusedElement, Is.EqualTo(model.FocusedElement))
            Assert.That(updated.TerminalPaneTarget, Is.EqualTo(Some second))
            Assert.That(
                selectedWorktree
                    updated.TerminalPaneTarget
                    updated.FocusedElement,
                Is.EqualTo(Some second)
            )
            Assert.That(
                CanvasUpdate.activeVisibleDoc updated,
                Is.EqualTo(
                    Some (
                        WorktreePath.value first,
                        "status.html"))
            ))

    [<Test>]
    member _.``Selecting a card restores terminal focus following``() =
        let updated, _ =
            { focusModel with TerminalPaneTarget = Some third }
            |> CanvasUpdate.applyFocus
                true
                (Some (Card (WorktreePath.value second)))

        Assert.That(updated.TerminalPaneTarget, Is.EqualTo(None))

    [<Test>]
    member _.``Automatic canvas focus preserves an explicit terminal target``() =
        let updated, _ =
            App.update
                (SetFocusNoRetarget
                    (Some (Card (WorktreePath.value second))))
                { focusModel with TerminalPaneTarget = Some third }

        Assert.That(updated.TerminalPaneTarget, Is.EqualTo(Some third))

    [<Test>]
    member _.``Card focus with no terminals renders no active terminal``() =
        let updated, _ =
            focusModel
            |> CanvasUpdate.applyFocus
                true
                (Some (Card (WorktreePath.value third)))

        Assert.That(
            activeTerminalId
                (Some third)
                updated.ActiveEmbeddedTerminals
                updated.EmbeddedTerminals,
            Is.EqualTo(None)
        )

    [<Test>]
    member _.``Polling a closed terminal advances only its worktree selection``() =
        let snapshot =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61233 ] }

        let updated, _ =
            App.update
                (EmbeddedTerminalSnapshotChanged snapshot)
                focusModel

        Assert.That(
            updated.ActiveEmbeddedTerminals,
            Is.EqualTo(
                Map.ofList [
                    first, firstOne
                    second, secondOne
                ])
        )

    [<Test>]
    member _.``Opening a canvas doc switches the visible terminal worktree``() =
        let doc =
            { Filename = "status.html"
              ContentHash = "h1"
              LastModified = DateTimeOffset.UtcNow
              OwnerSessionId = None
              Kind = AgentDoc }

        let repos =
            focusModel.Repos
            |> List.map (fun repo ->
                { repo with
                    Worktrees =
                        repo.Worktrees
                        |> List.map (fun worktree ->
                            if worktree.Path = second then
                                { worktree with CanvasDocs = [ doc ] }
                            else
                                worktree) })

        let updated, _ =
            CanvasUpdate.openCanvasDoc
                (WorktreePath.value second)
                doc.Filename
                { focusModel with
                    Repos = repos
                    Canvas.CanvasPaneOpen = true }

        Assert.Multiple(fun () ->
            Assert.That(
                updated.FocusedElement,
                Is.EqualTo(
                    Some (Card (WorktreePath.value second)))
            )
            Assert.That(
                activeTerminalId
                    (Some second)
                    updated.ActiveEmbeddedTerminals
                    updated.EmbeddedTerminals,
                Is.EqualTo(Some secondOne)
            ))
