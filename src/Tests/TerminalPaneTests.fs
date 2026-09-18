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
      ReportedActivity = None
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
    member _.``Reported activity replaces the numbered terminal label``() =
        let titled =
            { running firstOne first 61231 with
                ReportedActivity = Some "Investigating terminal title routing" }

        Assert.Multiple(fun () ->
            Assert.That(
                tabLabel 0 titled,
                Is.EqualTo("Investigating terminal title routing")
            )
            Assert.That(
                tabLabel 1 (running firstTwo first 61232),
                Is.EqualTo("Terminal 2")
            ))

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
    member _.``Cycling terminals changes only the selected worktree``() =
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
            |> cycleTerminal
                CycleDirection.Next
                (Some first)
                snapshot

        Assert.That(
            updated,
            Is.EqualTo(
                Map.ofList [
                    first, firstTwo
                    second, secondOne
                ])
        )

    [<Test>]
    member _.``Cycling terminals wraps in both directions``() =
        let snapshot =
            { Tabs =
                [ running firstOne first 61231
                  running firstTwo first 61232 ] }

        let forward =
            Map.empty
            |> cycleTerminal
                CycleDirection.Next
                (Some first)
                snapshot

        let backward =
            Map.empty
            |> cycleTerminal
                CycleDirection.Previous
                (Some first)
                snapshot

        Assert.Multiple(fun () ->
            Assert.That(
                activeTerminalId (Some first) forward snapshot,
                Is.EqualTo(Some firstTwo)
            )
            Assert.That(
                activeTerminalId (Some first) backward snapshot,
                Is.EqualTo(Some firstTwo)
            ))

    [<Test>]
    member _.``Cycling with fewer than two visible terminals is a no-op``() =
        let snapshot =
            { Tabs = [ running firstOne first 61231 ] }

        let selections = Map.ofList [ first, firstOne ]

        Assert.Multiple(fun () ->
            Assert.That(
                selections
                |> cycleTerminal
                    CycleDirection.Next
                    (Some first)
                    snapshot,
                Is.EqualTo(selections)
            )
            Assert.That(
                selections
                |> cycleTerminal
                    CycleDirection.Previous
                    None
                    snapshot,
                Is.EqualTo(selections)
            ))

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
    member _.``Reconnect advances only the selected terminal view generation``() =
        let states =
            Map.ofList [
                firstOne,
                { Generation = 2
                  FocusAfterLoad = false }
                secondOne,
                { Generation = 7
                  FocusAfterLoad = false }
            ]

        let updated =
            states |> reconnectView firstOne

        Assert.Multiple(fun () ->
            Assert.That(viewGeneration firstOne updated, Is.EqualTo(3))
            Assert.That(viewGeneration secondOne updated, Is.EqualTo(7))
            Assert.That(updated[firstOne].FocusAfterLoad, Is.True)
            Assert.That(updated[secondOne], Is.EqualTo(states[secondOne])))

    [<Test>]
    member _.``Only the current reconnect generation completes its focus request``() =
        let pending =
            Map.empty |> reconnectView firstOne
        let generation = viewGeneration firstOne pending

        let stale, staleFocus =
            pending
            |> completeViewLoad firstOne (generation - 1)

        let completed, currentFocus =
            stale
            |> completeViewLoad firstOne generation

        Assert.Multiple(fun () ->
            Assert.That(stale, Is.EqualTo(pending))
            Assert.That(staleFocus, Is.False)
            Assert.That(currentFocus, Is.True)
            Assert.That(completed[firstOne].FocusAfterLoad, Is.False))

    [<Test>]
    member _.``Reconnect eligibility and view state require a safe running terminal``() =
        let interrupted =
            tab
                secondOne
                second
                (EmbeddedTerminalLifecycle.Interrupted "host stopped")

        let unsafe =
            tab
                thirdOne
                third
                (EmbeddedTerminalLifecycle.Running
                    "https://example.com/terminal")

        let snapshot =
            { Tabs =
                [ running firstOne first 61231
                  interrupted
                  unsafe ] }

        let states =
            Map.ofList [
                firstOne,
                { Generation = 1
                  FocusAfterLoad = true }
                secondOne,
                { Generation = 2
                  FocusAfterLoad = true }
                thirdOne,
                { Generation = 3
                  FocusAfterLoad = true }
            ]

        let reconciled =
            states |> reconcileViewStates snapshot

        Assert.Multiple(fun () ->
            Assert.That(
                tryReconnectableTab (Some firstOne) snapshot
                |> Option.map _.Id,
                Is.EqualTo(Some firstOne)
            )
            Assert.That(
                tryReconnectableTab (Some secondOne) snapshot,
                Is.EqualTo(None)
            )
            Assert.That(
                tryReconnectableTab (Some thirdOne) snapshot,
                Is.EqualTo(None)
            )
            Assert.That(
                tryReconnectableTab None snapshot,
                Is.EqualTo(None)
            )
            Assert.That(
                reconciled |> Map.toList |> List.map fst,
                Is.EqualTo([ firstOne ])
            ))

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
            isSafeEndpoint endpoint,
            Is.EqualTo expectedSafe
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
      TerminalHostUpdate = TerminalHostUpdateModel.initial
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
      Workspace = WorkspaceLayout.empty
      TerminalPaneOpen = true
      TerminalPaneTarget = None
      EmbeddedTerminals =
        { Tabs =
            [ running firstOne first 61231
              running firstTwo first 61232
              running secondOne second 61233 ] }
      DismissedEmbeddedTerminals = Set.empty
      ActiveEmbeddedTerminals =
        Map.ofList [
            first, firstTwo
            second, secondOne
        ]
      EmbeddedTerminalStarts = Map.empty
      EmbeddedTerminalViewStates = Map.empty
      Canvas = CanvasState.empty
      OverviewPanelOpen = false
      OverviewAgentsStuck = false
      SelectedOverviewGroup = None
      OverviewHistoryWindow = None
      OverviewHistory = None
      OverviewHistoryRequestedAt = DateTimeOffset.MinValue
      OverviewHistoryRequestInFlight = None
      WorktreeSearch = WorktreeSearch.initial
      EmbeddedTerminalPollInFlight = false }

let private dashboardResponse terminalHostUpdate =
    { Repos = []
      SchedulerEvents = []
      LatestByCategory = Map.empty
      AppVersion = "test"
      DeployBranch = None
      SystemMetrics = None
      EditorName = "VS Code"
      WorktreeSkills = []
      CollapsedRepos = Set.empty
      TerminalPaneOpen = false
      CanvasPaneOpen = false
      OverviewPanelOpen = false
      WorkspaceWidth = WorkspaceWidth.EqualThirds
      TerminalHostUpdate = terminalHostUpdate }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalHostUpdateStateTests() =

    [<Test>]
    member _.``Stale unavailable refresh cannot settle a locally requested update``() =
        let ready =
            { focusModel with
                TerminalHostUpdate =
                    TerminalHostUpdateModel.Observed
                        TerminalHostUpdateState.Available }

        let started, _ =
            App.update UpdateTerminalHost ready

        let refreshed, _ =
            App.update
                (DataLoaded(
                    dashboardResponse
                        TerminalHostUpdateState.Unavailable,
                    DateTimeOffset.UtcNow
                ))
                started

        Assert.That(
            refreshed.TerminalHostUpdate,
            Is.EqualTo(
                TerminalHostUpdateModel.RequestInFlight
            )
        )

    [<Test>]
    member _.``Server-observed update settles from an unavailable refresh``() =
        let updating =
            { focusModel with
                TerminalHostUpdate =
                    TerminalHostUpdateModel.Observed
                        TerminalHostUpdateState.Updating }

        let refreshed, _ =
            App.update
                (DataLoaded(
                    dashboardResponse
                        TerminalHostUpdateState.Unavailable,
                    DateTimeOffset.UtcNow
                ))
                updating

        Assert.That(
            refreshed.TerminalHostUpdate,
            Is.EqualTo TerminalHostUpdateModel.initial
        )

    [<Test>]
    member _.``Immediate acknowledgement installs observed Updating and blocks duplicates``() =
        let acknowledged, _ =
            App.update
                (TerminalHostUpdateCompleted(
                    TerminalHostUpdateState.Updating
                ))
                { focusModel with
                    TerminalHostUpdate =
                        TerminalHostUpdateModel.RequestInFlight }

        let duplicate, command =
            App.update UpdateTerminalHost acknowledged

        Assert.Multiple(fun () ->
            Assert.That(
                acknowledged.TerminalHostUpdate,
                Is.EqualTo(
                    TerminalHostUpdateModel.Observed
                        TerminalHostUpdateState.Updating
                )
            )
            Assert.That(
                duplicate,
                Is.EqualTo acknowledged
            )
            Assert.That(command, Is.Empty))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalFocusTests() =

    let subscriptionKeys model =
        App.appSubscriptions model
        |> List.map (fst >> String.concat "/")

    [<Test>]
    member _.``One-pane navigation preserves targets selections and saved desktop visibility``() =
        let model =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane
                TerminalPaneOpen = false
                TerminalPaneTarget = Some second
                Canvas.CanvasPaneOpen = true
                Canvas.TargetWorktree = Some (WorktreePath.value third)
                Canvas.WorkspaceWidth = WorkspaceWidth.WideCanvas }

        [ WorkspaceLayout.Pane.Terminal
          WorkspaceLayout.Pane.Canvas
          WorkspaceLayout.Pane.Worktrees ]
        |> List.iter (fun pane ->
            let updated, _ = App.update (SelectWorkspacePane pane) model
            Assert.That(updated, Is.EqualTo({ model with Workspace.ActivePane = pane })))

    [<Test>]
    member _.``One-pane visibility ignores desktop flags without changing them``() =
        let panes =
            [ WorkspaceLayout.Pane.Worktrees
              WorkspaceLayout.Pane.Terminal
              WorkspaceLayout.Pane.Canvas ]
        let visible state =
            panes
            |> List.filter (fun pane -> WorkspaceLayout.isVisible pane true state)
        let results =
            panes
            |> List.map (fun pane ->
                { WorkspaceLayout.empty with
                    Mode = WorkspaceLayout.Mode.OnePane
                    ActivePane = pane }
                |> visible)

        Assert.That(results, Is.EqualTo(panes |> List.map List.singleton))
        Assert.That(visible WorkspaceLayout.empty, Is.EqualTo(panes))

    [<Test>]
    member _.``Layout round trip restores desktop settings and remembers the chosen pane``() =
        let model =
            { focusModel with
                Workspace.ActivePane = WorkspaceLayout.Pane.Terminal
                TerminalPaneOpen = false
                Canvas.WorkspaceWidth = WorkspaceWidth.WideCanvas }
        let phone, _ = App.update (SetWorkspaceMode WorkspaceLayout.Mode.OnePane) model
        let desktop, _ = App.update (SetWorkspaceMode WorkspaceLayout.Mode.Desktop) phone

        Assert.That(desktop, Is.EqualTo(model))

    [<Test>]
    member _.``One-pane terminal launch reveals its target without saving desktop visibility``() =
        let model =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane
                TerminalPaneOpen = false }
        let updated, cmd = App.update (OpenEmbeddedTerminal third) model

        Assert.Multiple(fun () ->
            Assert.That(updated.Workspace.ActivePane, Is.EqualTo(WorkspaceLayout.Pane.Terminal))
            Assert.That(updated.TerminalPaneOpen, Is.False)
            Assert.That(updated.TerminalPaneTarget, Is.EqualTo(Some third))
            Assert.That(updated.FocusedElement, Is.EqualTo(model.FocusedElement))
            Assert.That(cmd.Length, Is.EqualTo(1), "Only the launch request remains; desktop visibility is not persisted."))

    [<Test>]
    member _.``Terminal start finishing after a pane switch cannot request browser focus``() =
        let model =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane }
        let starting, _ = App.update (StartEmbeddedTerminal first) model
        let hidden, _ = App.update (SelectWorkspacePane WorkspaceLayout.Pane.Worktrees) starting
        let exact = terminalId "late-start"
        let snapshot = { Tabs = model.EmbeddedTerminals.Tabs @ [ running exact first 61241 ] }
        let updated, cmd =
            App.update
                (EmbeddedTerminalStarted(first, Ok { Snapshot = snapshot; TerminalId = exact }))
                hidden

        Assert.Multiple(fun () ->
            Assert.That(updated.Workspace.ActivePane, Is.EqualTo(WorkspaceLayout.Pane.Worktrees))
            Assert.That(updated.ActiveEmbeddedTerminals[first], Is.EqualTo(exact))
            Assert.That(updated.EmbeddedTerminalStarts, Is.Empty)
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Agent start finishing after a pane switch cannot request browser focus``() =
        let model =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane }
        let starting, _ = App.update (StartAgent first) model
        let hidden, _ = App.update (SelectWorkspacePane WorkspaceLayout.Pane.Worktrees) starting
        let exact = terminalId "late-agent"
        let snapshot = { Tabs = model.EmbeddedTerminals.Tabs @ [ running exact first 61241 ] }
        let updated, cmd =
            App.update
                (AgentStarted(first, Ok { Snapshot = snapshot; TerminalId = exact }))
                hidden

        Assert.Multiple(fun () ->
            Assert.That(updated.Workspace.ActivePane, Is.EqualTo(WorkspaceLayout.Pane.Worktrees))
            Assert.That(updated.ActiveEmbeddedTerminals[first], Is.EqualTo(exact))
            Assert.That(updated.EmbeddedTerminalStarts, Is.Empty)
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Canvas Escape reveals Worktrees before reclaiming dashboard focus``() =
        let model =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane
                Workspace.ActivePane = WorkspaceLayout.Pane.Canvas }
        let updated, cmd = App.update (KeyPressed("Escape", false)) model

        Assert.That(updated.Workspace.ActivePane, Is.EqualTo(WorkspaceLayout.Pane.Worktrees))
        Assert.That(cmd, Is.Not.Empty)

    [<Test>]
    member _.``One-pane search reveals its chosen worktree without opening another terminal``() =
        let model =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane
                Workspace.ActivePane = WorkspaceLayout.Pane.Terminal
                TerminalPaneOpen = false
                TerminalPaneTarget = Some first }
        let searching, _ =
            App.update
                (WorktreeSearchMsg (WorktreeSearch.Msg.OpenFromTerminal firstTwo))
                model
        let selected, _ =
            App.update
                (WorktreeSearchMsg (WorktreeSearch.Msg.ChooseResult second))
                searching

        Assert.Multiple(fun () ->
            Assert.That(WorktreeSearch.isOpen selected.WorktreeSearch, Is.False)
            Assert.That(selected.Workspace.ActivePane, Is.EqualTo(WorkspaceLayout.Pane.Worktrees))
            Assert.That(selected.FocusedElement, Is.EqualTo(Some (Card (WorktreePath.value second))))
            Assert.That(selected.TerminalPaneTarget, Is.EqualTo(None))
            Assert.That(selected.TerminalPaneOpen, Is.False)
            Assert.That(selected.EmbeddedTerminals, Is.EqualTo(model.EmbeddedTerminals))
            Assert.That(selected.ActiveEmbeddedTerminals, Is.EqualTo(model.ActiveEmbeddedTerminals)))

    [<Test>]
    member _.``Terminal start completing behind worktree search does not request input focus``() =
        let model =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane }
        let starting, _ = App.update (StartEmbeddedTerminal first) model
        let searching, _ =
            App.update
                (WorktreeSearchMsg (WorktreeSearch.Msg.OpenFromTerminal firstTwo))
                starting
        let exact = terminalId "search-pending-start"
        let snapshot = { Tabs = model.EmbeddedTerminals.Tabs @ [ running exact first 61241 ] }
        let completed, cmd =
            App.update
                (EmbeddedTerminalStarted(first, Ok { Snapshot = snapshot; TerminalId = exact }))
                searching

        Assert.Multiple(fun () ->
            Assert.That(WorktreeSearch.isOpen completed.WorktreeSearch, Is.True)
            Assert.That(completed.ActiveEmbeddedTerminals[first], Is.EqualTo(exact))
            Assert.That(completed.EmbeddedTerminalStarts, Is.Empty)
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``TerminalHost update lock ignores every user terminal entry point``() =
        let locked =
            { focusModel with
                TerminalHostUpdate =
                    TerminalHostUpdateModel.RequestInFlight }

        let terminalActions =
            [ UpdateTerminalHost
              OpenTerminal first
              OpenEmbeddedTerminal first
              StartEmbeddedTerminal first
              StartAgent first
              StartEmbeddedTerminalFromTab firstTwo
              SelectEmbeddedTerminal firstOne
              ReconnectEmbeddedTerminalView firstTwo
              NotifyEmbeddedTerminalVisibility(
                  firstTwo,
                  "http://127.0.0.1:61232",
                  TerminalVisibilitySignal.Activate
              )
              CycleEmbeddedTerminal(firstTwo, CycleDirection.Next)
              CloseEmbeddedTerminal firstTwo
              ToggleTerminalPane
              FocusSession first
              ResumeSession first
              LaunchAction(first, ActionKind.CreatePr)
              KeyPressed("t", false)
              KeyPressed("Enter", false) ]

        let subscriptions = subscriptionKeys locked
        let _, deactivateCommand =
            App.update
                (NotifyEmbeddedTerminalVisibility(
                    firstTwo,
                    "http://127.0.0.1:61232",
                    TerminalVisibilitySignal.Deactivate
                ))
                locked

        Assert.Multiple(fun () ->
            terminalActions
            |> List.iter (fun action ->
                let updated, command = App.update action locked
                let context = $"Terminal action %A{action}"

                Assert.That(updated, Is.EqualTo locked, context)
                Assert.That(command, Is.Empty, context))

            Assert.That(
                subscriptions,
                Has.None.EqualTo("terminal-shortcuts")
            )
            Assert.That(
                subscriptions,
                Has.None.StartsWith("terminal-visible/")
            )
            Assert.That(
                subscriptions,
                Does.Contain("global-keyboard/blocked")
            )
            Assert.That(
                List.length deactivateCommand,
                Is.EqualTo 1,
                "locking must still deactivate the previously visible terminal"
            ))

    [<Test>]
    member _.``TerminalHost update lock suppresses queued launch and late terminal focus``() =
        let exact = terminalId "completed-during-update"
        let snapshot =
            { Tabs =
                focusModel.EmbeddedTerminals.Tabs
                @ [ running exact first 61241 ] }

        let locked =
            { focusModel with
                TerminalHostUpdate =
                    TerminalHostUpdateModel.RequestInFlight
                EmbeddedTerminalStarts =
                    Map.ofList [
                        first,
                        TerminalStartState.StartingWithQueuedAgents(
                            true,
                            1
                        )
                    ]
                EmbeddedTerminalViewStates =
                    Map.ofList [
                        firstTwo,
                        { Generation = 1
                          FocusAfterLoad = true }
                    ] }

        let completed, completionCommand =
            App.update
                (AgentStarted(
                    first,
                    Ok
                        { Snapshot = snapshot
                          TerminalId = exact }
                ))
                locked

        let loaded, focusCommand =
            App.update
                (EmbeddedTerminalViewLoaded(firstTwo, 1))
                locked

        let failed, failureCommand =
            App.update
                (EmbeddedTerminalRequestFailed(
                    first,
                    "request failed during update"
                ))
                locked

        Assert.Multiple(fun () ->
            Assert.That(completionCommand, Is.Empty)
            Assert.That(
                tryStartState first completed.EmbeddedTerminalStarts,
                Is.EqualTo None
            )
            Assert.That(focusCommand, Is.Empty)
            Assert.That(
                loaded.EmbeddedTerminalViewStates[firstTwo].FocusAfterLoad,
                Is.False
            )
            Assert.That(failureCommand, Is.Empty)
            Assert.That(
                tryStartState first failed.EmbeddedTerminalStarts,
                Is.EqualTo None
            ))

    [<Test>]
    member _.``T key opens or focuses the embedded terminal for the focused card``() =
        Assert.That(
            App.keyBinding
                (Card (WorktreePath.value first))
                "t"
                focusModel,
            Is.EqualTo(Some(OpenEmbeddedTerminal first))
        )

    [<TestCase("a")>]
    [<TestCase("A")>]
    member _.``Agent key starts a fresh Copilot terminal for the focused card``(key: string) =
        Assert.That(
            App.keyBinding
                (Card (WorktreePath.value first))
                key
                focusModel,
            Is.EqualTo(Some(StartAgent first))
        )

    [<Test>]
    member _.``Cycle message updates the current worktree terminal only``() =
        let updated, cmd =
            App.update
                (CycleEmbeddedTerminal(firstTwo, CycleDirection.Next))
                focusModel

        Assert.Multiple(fun () ->
            Assert.That(
                activeTerminalId
                    (Some first)
                    updated.ActiveEmbeddedTerminals
                    updated.EmbeddedTerminals,
                Is.EqualTo(Some firstOne)
            )
            Assert.That(
                activeTerminalId
                    (Some second)
                    updated.ActiveEmbeddedTerminals
                    updated.EmbeddedTerminals,
                Is.EqualTo(Some secondOne)
            )
            Assert.That(
                List.length cmd,
                Is.EqualTo(1),
                "cycling should refocus the newly active terminal"
            ))

    [<Test>]
    member _.``Cycle message from a stale terminal is ignored``() =
        let updated, cmd =
            App.update
                (CycleEmbeddedTerminal(firstOne, CycleDirection.Next))
                focusModel

        Assert.Multiple(fun () ->
            Assert.That(
                updated.ActiveEmbeddedTerminals,
                Is.EqualTo(focusModel.ActiveEmbeddedTerminals)
            )
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``New terminal shortcut targets the active terminal worktree``() =
        let updated, cmd =
            App.update
                (StartEmbeddedTerminalFromTab firstTwo)
                focusModel

        Assert.Multiple(fun () ->
            Assert.That(updated.TerminalPaneTarget, Is.EqualTo(Some first))
            Assert.That(
                tryStartState first updated.EmbeddedTerminalStarts,
                Is.EqualTo(Some TerminalStartState.StartingAndFocus)
            )
            Assert.That(List.length cmd, Is.EqualTo(2)))

    [<Test>]
    member _.``New terminal shortcut from a stale terminal is ignored``() =
        let updated, cmd =
            App.update
                (StartEmbeddedTerminalFromTab (terminalId "missing"))
                focusModel

        Assert.Multiple(fun () ->
            Assert.That(updated, Is.EqualTo(focusModel))
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Terminal pane toggle preserves its target while changing visibility``() =
        let model =
            { focusModel with
                TerminalPaneTarget = Some second }

        let hidden, hideCmd =
            App.update ToggleTerminalPane model

        let shown, showCmd =
            App.update ToggleTerminalPane hidden

        Assert.Multiple(fun () ->
            Assert.That(hidden.TerminalPaneOpen, Is.False)
            Assert.That(hidden.TerminalPaneTarget, Is.EqualTo(Some second))
            Assert.That(List.length hideCmd, Is.EqualTo(1))
            Assert.That(shown.TerminalPaneOpen, Is.True)
            Assert.That(shown.TerminalPaneTarget, Is.EqualTo(Some second))
            Assert.That(List.length showCmd, Is.EqualTo(1)))

    [<Test>]
    member _.``Terminal visibility subscription follows the active safe iframe``() =
        let initial = subscriptionKeys focusModel

        let targeted =
            subscriptionKeys
                { focusModel with
                    TerminalPaneTarget = Some second }

        let closed =
            subscriptionKeys
                { focusModel with
                    TerminalPaneOpen = false }

        let hiddenOnePane =
            subscriptionKeys
                { focusModel with
                    Workspace.Mode = WorkspaceLayout.Mode.OnePane
                    Workspace.ActivePane = WorkspaceLayout.Pane.Worktrees }

        let visibleOnePane =
            subscriptionKeys
                { focusModel with
                    Workspace.Mode = WorkspaceLayout.Mode.OnePane
                    Workspace.ActivePane = WorkspaceLayout.Pane.Terminal
                    TerminalPaneOpen = false }

        Assert.Multiple(fun () ->
            Assert.That(
                initial,
                Does.Contain(
                    $"terminal-visible/{EmbeddedTerminalId.value firstTwo}/http://127.0.0.1:61232"
                )
            )
            Assert.That(
                targeted,
                Does.Contain(
                    $"terminal-visible/{EmbeddedTerminalId.value secondOne}/http://127.0.0.1:61233"
                )
            )
            Assert.That(
                closed,
                Has.None.StartsWith("terminal-visible/")
            )
            Assert.That(
                hiddenOnePane,
                Has.None.StartsWith("terminal-visible/")
            )
            Assert.That(
                visibleOnePane,
                Does.Contain(
                    $"terminal-visible/{EmbeddedTerminalId.value firstTwo}/http://127.0.0.1:61232"
                )
            ))

    [<Test>]
    member _.``Terminal visibility notification is routed through one command``() =
        let updated, cmd =
            App.update
                (NotifyEmbeddedTerminalVisibility(
                    firstTwo,
                    "http://127.0.0.1:61232",
                    TerminalVisibilitySignal.Activate
                ))
                focusModel

        Assert.Multiple(fun () ->
            Assert.That(updated, Is.EqualTo(focusModel))
            Assert.That(List.length cmd, Is.EqualTo(1)))

    [<Test>]
    member _.``Open embedded terminal reuses the selected worktree terminal``() =
        let model =
            { focusModel with
                TerminalPaneOpen = false
                TerminalPaneTarget = None }

        let updated, cmd =
            App.update
                (OpenEmbeddedTerminal first)
                model

        Assert.Multiple(fun () ->
            Assert.That(updated.TerminalPaneOpen, Is.True)
            Assert.That(updated.TerminalPaneTarget, Is.EqualTo(Some first))
            Assert.That(updated.EmbeddedTerminals, Is.EqualTo(model.EmbeddedTerminals))
            Assert.That(updated.ActiveEmbeddedTerminals, Is.EqualTo(model.ActiveEmbeddedTerminals))
            Assert.That(isStarting first updated.EmbeddedTerminalStarts, Is.False)
            Assert.That(List.length cmd, Is.EqualTo(2)))

    [<Test>]
    member _.``Open embedded terminal starts one when the worktree has none``() =
        let updated, cmd =
            App.update
                (OpenEmbeddedTerminal third)
                focusModel

        Assert.Multiple(fun () ->
            Assert.That(updated.TerminalPaneOpen, Is.True)
            Assert.That(updated.TerminalPaneTarget, Is.EqualTo(Some third))
            Assert.That(isStarting third updated.EmbeddedTerminalStarts, Is.True)
            Assert.That(
                tryStartState third updated.EmbeddedTerminalStarts,
                Is.EqualTo(Some TerminalStartState.StartingAndFocus)
            )
            Assert.That(List.length cmd, Is.EqualTo(2)))

    [<Test>]
    member _.``Start embedded terminal always creates another tab``() =
        let updated, cmd =
            App.update
                (StartEmbeddedTerminal first)
                focusModel

        Assert.Multiple(fun () ->
            Assert.That(updated.TerminalPaneOpen, Is.True)
            Assert.That(updated.TerminalPaneTarget, Is.EqualTo(Some first))
            Assert.That(isStarting first updated.EmbeddedTerminalStarts, Is.True)
            Assert.That(
                tryStartState first updated.EmbeddedTerminalStarts,
                Is.EqualTo(Some TerminalStartState.StartingAndFocus)
            )
            Assert.That(List.length cmd, Is.EqualTo(2)))

    [<Test>]
    member _.``Agent actions queue behind an in-flight start without coalescing``() =
        let current = terminalId "current-start"
        let firstAgent = terminalId "first-agent"
        let secondAgent = terminalId "second-agent"
        let startState model =
            tryStartState first model.EmbeddedTerminalStarts
        let queued focus count =
            Some(TerminalStartState.StartingWithQueuedAgents(focus, count))
        let complete message terminal snapshot model =
            let result =
                Ok
                    { Snapshot = snapshot
                      TerminalId = terminal }

            App.update
                (message (first, result))
                model
        let starting =
            { focusModel with
                EmbeddedTerminalStarts =
                    Map.ofList [
                        first, TerminalStartState.Starting
                    ] }

        let queuedOnce, firstQueueCmd =
            App.update (StartAgent first) starting

        let queuedTwice, secondQueueCmd =
            App.update (StartAgent first) queuedOnce

        let currentSnapshot =
            { Tabs =
                focusModel.EmbeddedTerminals.Tabs
                @ [ running current first 61241 ] }

        let afterCurrent, currentCmd =
            complete EmbeddedTerminalStarted current currentSnapshot queuedTwice

        let firstAgentSnapshot =
            { Tabs =
                currentSnapshot.Tabs
                @ [ running firstAgent first 61242 ] }

        let afterFirstAgent, firstAgentCmd =
            complete AgentStarted firstAgent firstAgentSnapshot afterCurrent

        let secondAgentSnapshot =
            { Tabs =
                firstAgentSnapshot.Tabs
                @ [ running secondAgent first 61243 ] }

        let afterSecondAgent, secondAgentCmd =
            complete AgentStarted secondAgent secondAgentSnapshot afterFirstAgent

        Assert.Multiple(fun () ->
            Assert.That(
                startState queuedOnce,
                Is.EqualTo(queued false 1)
            )
            Assert.That(
                startState queuedTwice,
                Is.EqualTo(queued false 2)
            )
            Assert.That(
                startState afterCurrent,
                Is.EqualTo(queued true 1)
            )
            Assert.That(
                startState afterFirstAgent,
                Is.EqualTo(Some TerminalStartState.StartingAndFocus)
            )
            Assert.That(
                startState afterSecondAgent,
                Is.EqualTo(None)
            )
            Assert.That(
                activeTerminalId
                    (Some first)
                    afterSecondAgent.ActiveEmbeddedTerminals
                    afterSecondAgent.EmbeddedTerminals,
                Is.EqualTo(Some secondAgent)
            )
            Assert.That(List.length firstQueueCmd, Is.EqualTo(1))
            Assert.That(List.length secondQueueCmd, Is.EqualTo(1))
            Assert.That(List.length currentCmd, Is.EqualTo(2))
            Assert.That(List.length firstAgentCmd, Is.EqualTo(3))
            Assert.That(List.length secondAgentCmd, Is.EqualTo(1)))

    [<Test>]
    member _.``Queued Agent starts after the current launch fails``() =
        let starting =
            { focusModel with
                EmbeddedTerminalStarts =
                    Map.ofList [
                        first, TerminalStartState.Starting
                    ] }

        let queued, _ =
            App.update (StartAgent first) starting

        let updated, cmd =
            App.update
                (EmbeddedTerminalStarted(first, Error "current failed"))
                queued

        Assert.Multiple(fun () ->
            Assert.That(
                tryStartState first updated.EmbeddedTerminalStarts,
                Is.EqualTo(Some TerminalStartState.StartingAndFocus)
            )
            Assert.That(List.length cmd, Is.EqualTo(2)))

    [<Test>]
    member _.``Reconnect view preserves terminal state and focuses after the current load``() =
        let reconnecting, reconnectCmd =
            App.update
                (ReconnectEmbeddedTerminalView firstTwo)
                focusModel

        let selectedAgain, _ =
            App.update
                (SelectEmbeddedTerminal firstTwo)
                reconnecting

        let loaded, focusCmd =
            App.update
                (EmbeddedTerminalViewLoaded(firstTwo, 1))
                selectedAgain

        Assert.Multiple(fun () ->
            Assert.That(reconnectCmd, Is.Empty)
            Assert.That(reconnecting.EmbeddedTerminals, Is.EqualTo(focusModel.EmbeddedTerminals))
            Assert.That(
                reconnecting.ActiveEmbeddedTerminals,
                Is.EqualTo(focusModel.ActiveEmbeddedTerminals)
            )
            Assert.That(
                selectedAgain.EmbeddedTerminalViewStates[firstTwo],
                Is.EqualTo(
                    { Generation = 1
                      FocusAfterLoad = true }
                )
            )
            Assert.That(
                viewGeneration
                    firstOne
                    reconnecting.EmbeddedTerminalViewStates,
                Is.Zero
            )
            Assert.That(
                viewGeneration
                    secondOne
                    reconnecting.EmbeddedTerminalViewStates,
                Is.Zero
            )
            Assert.That(
                loaded.EmbeddedTerminalViewStates[firstTwo].FocusAfterLoad,
                Is.False
            )
            Assert.That(List.length focusCmd, Is.EqualTo(1)))

    [<Test>]
    member _.``Reconnect ignores a terminal that is no longer selected or visible``() =
        let nonSelected, nonSelectedCmd =
            App.update
                (ReconnectEmbeddedTerminalView firstOne)
                focusModel

        let hidden, hiddenCmd =
            App.update
                (ReconnectEmbeddedTerminalView firstTwo)
                { focusModel with TerminalPaneOpen = false }

        let hiddenOnePaneModel =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane
                Workspace.ActivePane = WorkspaceLayout.Pane.Worktrees }

        let hiddenOnePane, hiddenOnePaneCmd =
            App.update
                (ReconnectEmbeddedTerminalView firstTwo)
                hiddenOnePaneModel

        Assert.Multiple(fun () ->
            Assert.That(nonSelected, Is.EqualTo(focusModel))
            Assert.That(nonSelectedCmd, Is.Empty)
            Assert.That(
                hidden,
                Is.EqualTo({ focusModel with TerminalPaneOpen = false })
            )
            Assert.That(hiddenCmd, Is.Empty)
            Assert.That(hiddenOnePane, Is.EqualTo(hiddenOnePaneModel))
            Assert.That(hiddenOnePaneCmd, Is.Empty))

    [<Test>]
    member _.``One-pane navigation cancels pending reconnect focus when the terminal becomes hidden``() =
        let model =
            { focusModel with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane
                Workspace.ActivePane = WorkspaceLayout.Pane.Terminal }

        let reconnecting, _ =
            App.update
                (ReconnectEmbeddedTerminalView firstTwo)
                model

        let hidden, _ =
            App.update
                (SelectWorkspacePane WorkspaceLayout.Pane.Worktrees)
                reconnecting

        let loaded, cmd =
            App.update
                (EmbeddedTerminalViewLoaded(firstTwo, 1))
                hidden

        Assert.Multiple(fun () ->
            Assert.That(
                hidden.EmbeddedTerminalViewStates[firstTwo].FocusAfterLoad,
                Is.False
            )
            Assert.That(
                loaded.EmbeddedTerminalViewStates[firstTwo].FocusAfterLoad,
                Is.False
            )
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Late reconnect load cannot focus after another terminal is selected``() =
        let reconnecting, _ =
            App.update
                (ReconnectEmbeddedTerminalView firstTwo)
                focusModel

        let selected, _ =
            App.update
                (SelectEmbeddedTerminal firstOne)
                reconnecting

        let loaded, cmd =
            App.update
                (EmbeddedTerminalViewLoaded(firstTwo, 1))
                selected

        Assert.Multiple(fun () ->
            Assert.That(
                activeTerminalId
                    (Some first)
                    loaded.ActiveEmbeddedTerminals
                    loaded.EmbeddedTerminals,
                Is.EqualTo(Some firstOne)
            )
            Assert.That(
                loaded.EmbeddedTerminalViewStates[firstTwo].FocusAfterLoad,
                Is.False
            )
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Late reconnect load cannot focus a closed terminal``() =
        let reconnecting, _ =
            App.update
                (ReconnectEmbeddedTerminalView firstTwo)
                focusModel

        let after =
            { Tabs =
                [ running firstOne first 61231
                  running secondOne second 61233 ] }

        let closed, _ =
            App.update
                (EmbeddedTerminalClosed after)
                reconnecting

        let loaded, cmd =
            App.update
                (EmbeddedTerminalViewLoaded(firstTwo, 1))
                closed

        Assert.Multiple(fun () ->
            Assert.That(
                loaded.EmbeddedTerminalViewStates.ContainsKey firstTwo,
                Is.False
            )
            Assert.That(
                activeTerminalId
                    (Some first)
                    loaded.ActiveEmbeddedTerminals
                    loaded.EmbeddedTerminals,
                Is.EqualTo(Some firstOne)
            )
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Polled terminal before start response still schedules exact focus``() =
        let exact = terminalId "focused-start"
        let snapshot =
            { Tabs =
                focusModel.EmbeddedTerminals.Tabs
                @ [ running exact third 61241 ] }
        let starting =
            { focusModel with
                TerminalPaneTarget = Some third
                EmbeddedTerminalStarts =
                    Map.ofList [
                        third, TerminalStartState.StartingAndFocus
                    ] }

        let polled, pollCmd =
            App.update
                (EmbeddedTerminalSnapshotChanged snapshot)
                starting

        let started, startCmd =
            App.update
                (EmbeddedTerminalStarted(
                    third,
                    Ok
                        { Snapshot = snapshot
                          TerminalId = exact }
                ))
                polled

        Assert.Multiple(fun () ->
            Assert.That(pollCmd, Is.Empty)
            Assert.That(
                tryStartState third started.EmbeddedTerminalStarts,
                Is.EqualTo(None)
            )
            Assert.That(List.length startCmd, Is.EqualTo(1)))

    [<Test>]
    member _.``Terminal disappearance after focused start leaves no pending state``() =
        let exact = terminalId "disappearing-start"
        let snapshot =
            { Tabs =
                focusModel.EmbeddedTerminals.Tabs
                @ [ running exact third 61241 ] }
        let starting =
            { focusModel with
                TerminalPaneTarget = Some third
                EmbeddedTerminalStarts =
                    Map.ofList [
                        third, TerminalStartState.StartingAndFocus
                    ] }

        let started, _ =
            App.update
                (EmbeddedTerminalStarted(
                    third,
                    Ok
                        { Snapshot = snapshot
                          TerminalId = exact }
                ))
                starting

        let removed, cmd =
            App.update
                (EmbeddedTerminalSnapshotChanged focusModel.EmbeddedTerminals)
                started

        Assert.Multiple(fun () ->
            Assert.That(
                tryStartState third removed.EmbeddedTerminalStarts,
                Is.EqualTo(None)
            )
            Assert.That(
                activeTerminalId
                    (Some third)
                    removed.ActiveEmbeddedTerminals
                    removed.EmbeddedTerminals,
                Is.EqualTo(None)
            )
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Repeated Resume keeps one in-flight launch without an action cooldown``() =
        let model =
            { focusModel with
                TerminalPaneOpen = false
                TerminalPaneTarget = None }

        let started, firstCmd =
            App.update
                (ResumeSession first)
                model

        let repeated, repeatedCmd =
            App.update
                (ResumeSession first)
                started

        Assert.Multiple(fun () ->
            Assert.That(started.TerminalPaneOpen, Is.True)
            Assert.That(started.TerminalPaneTarget, Is.EqualTo(Some first))
            Assert.That(isStarting first started.EmbeddedTerminalStarts, Is.True)
            Assert.That(List.length firstCmd, Is.EqualTo(2))
            Assert.That(repeated.TerminalPaneOpen, Is.True)
            Assert.That(repeated.TerminalPaneTarget, Is.EqualTo(Some first))
            Assert.That(isStarting first repeated.EmbeddedTerminalStarts, Is.True)
            Assert.That(
                List.length repeatedCmd,
                Is.EqualTo(1),
                "the repeated update should retain only pane-open persistence"
            )
            Assert.That(repeated.ActionCooldowns, Is.EqualTo(model.ActionCooldowns)))

    [<Test>]
    member _.``Completed launch selects the exact server-returned terminal``() =
        let exact = terminalId "exact-start"
        let competing = terminalId "competing-start"
        let snapshot =
            { Tabs =
                focusModel.EmbeddedTerminals.Tabs
                @ [ running exact first 61241
                    running competing first 61242 ] }
        let model =
            { focusModel with
                EmbeddedTerminalStarts =
                    Map.ofList [
                        first, TerminalStartState.Starting
                    ] }

        let updated, _ =
            App.update
                (EmbeddedTerminalStarted(
                    first,
                    Ok
                        { Snapshot = snapshot
                          TerminalId = exact }
                ))
                model

        Assert.Multiple(fun () ->
            Assert.That(
                activeTerminalId
                    (Some first)
                    updated.ActiveEmbeddedTerminals
                    updated.EmbeddedTerminals,
                Is.EqualTo(Some exact),
                "a concurrent terminal appended later must not replace the exact launch result"
            )
            Assert.That(
                tryStartState first updated.EmbeddedTerminalStarts,
                Is.EqualTo(None)
            ))

    [<Test>]
    member _.``Terminal close immediately removes the tab before teardown completes``() =
        let updated, cmd =
            App.update
                (CloseEmbeddedTerminal firstTwo)
                focusModel

        Assert.Multiple(fun () ->
            Assert.That(
                updated.EmbeddedTerminals.Tabs
                |> List.map _.Id,
                Is.EqualTo([ firstOne; secondOne ])
            )
            Assert.That(
                activeTerminalId
                    (Some first)
                    updated.ActiveEmbeddedTerminals
                    updated.EmbeddedTerminals,
                Is.EqualTo(Some firstOne)
            )
            Assert.That(
                updated.DismissedEmbeddedTerminals,
                Does.Contain(firstTwo)
            )
            Assert.That(
                List.length cmd,
                Is.EqualTo(2),
                "closing the active tab should close it and focus its replacement"
            ))

    [<Test>]
    member _.``Polling cannot restore a terminal while its close is unresolved``() =
        let closing, _ =
            App.update
                (CloseEmbeddedTerminal firstTwo)
                focusModel

        let updated, cmd =
            App.update
                (EmbeddedTerminalSnapshotChanged focusModel.EmbeddedTerminals)
                closing

        Assert.Multiple(fun () ->
            Assert.That(
                updated.EmbeddedTerminals.Tabs
                |> List.map _.Id,
                Is.EqualTo([ firstOne; secondOne ])
            )
            Assert.That(
                updated.DismissedEmbeddedTerminals,
                Does.Contain(firstTwo)
            )
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Confirmed teardown stops tracking a dismissed terminal``() =
        let closing, _ =
            App.update
                (CloseEmbeddedTerminal firstTwo)
                focusModel

        let torndown =
            { Tabs =
                focusModel.EmbeddedTerminals.Tabs
                |> List.filter (fun tab -> tab.Id <> firstTwo) }

        let updated, _ =
            App.update
                (EmbeddedTerminalSnapshotChanged torndown)
                closing

        Assert.That(
            updated.DismissedEmbeddedTerminals,
            Is.Empty,
            "a registry read without the terminal proves teardown, so its dismissal must not be kept for the session"
        )

    [<Test>]
    member _.``Completed terminal close immediately refreshes worktree status``() =
        let after =
            { Tabs =
                focusModel.EmbeddedTerminals.Tabs
                |> List.filter (fun tab -> tab.Id <> firstTwo) }

        let closing, _ =
            App.update
                (CloseEmbeddedTerminal firstTwo)
                focusModel

        let updated, cmd =
            App.update
                (EmbeddedTerminalClosed after)
                closing

        Assert.Multiple(fun () ->
            Assert.That(updated.EmbeddedTerminals, Is.EqualTo(after))
            Assert.That(
                updated.DismissedEmbeddedTerminals,
                Does.Contain(firstTwo)
            )
            Assert.That(
                updated.ActiveEmbeddedTerminals,
                Is.EqualTo(
                    Map.ofList [
                        first, firstOne
                        second, secondOne
                    ]
                )
            )
            Assert.That(
                List.length cmd,
                Is.EqualTo(1),
                "the close result must fetch fresh card state without waiting for the next tick"
            ))

    [<Test>]
    member _.``Failed terminal close permits authoritative refresh to restore the tab``() =
        let closing, _ =
            App.update
                (CloseEmbeddedTerminal firstTwo)
                focusModel

        let failed, cmd =
            App.update
                (EmbeddedTerminalCloseFailed firstTwo)
                closing

        let refreshed, _ =
            App.update
                (EmbeddedTerminalSnapshotChanged focusModel.EmbeddedTerminals)
                failed

        Assert.Multiple(fun () ->
            Assert.That(
                failed.DismissedEmbeddedTerminals,
                Does.Not.Contain(firstTwo)
            )
            Assert.That(
                refreshed.EmbeddedTerminals,
                Is.EqualTo(focusModel.EmbeddedTerminals)
            )
            Assert.That(
                List.length cmd,
                Is.EqualTo(2),
                "partial cleanup may change both terminal and exact-session state"
            ))

    [<Test>]
    member _.``Launch errors preserve exact terminal state and stay scoped``() =
        let model =
            { focusModel with
                EmbeddedTerminalStarts =
                    Map.ofList [
                        first, TerminalStartState.Starting
                        second, TerminalStartState.Starting
                    ] }

        [ EmbeddedTerminalStarted(first, Error "resume rejected"), "resume rejected"
          EmbeddedTerminalRequestFailed(first, "request failed"), "request failed" ]
        |> List.iter (fun (message, expectedError) ->
            let updated, cmd =
                App.update message model

            Assert.Multiple(fun () ->
                Assert.That(updated.EmbeddedTerminals, Is.EqualTo(model.EmbeddedTerminals))
                Assert.That(
                    updated.ActiveEmbeddedTerminals,
                    Is.EqualTo(model.ActiveEmbeddedTerminals)
                )
                Assert.That(
                    tryStartState first updated.EmbeddedTerminalStarts,
                    Is.EqualTo(Some(TerminalStartState.Failed expectedError))
                )
                Assert.That(
                    tryStartState second updated.EmbeddedTerminalStarts,
                    Is.EqualTo(Some TerminalStartState.Starting)
                )
                Assert.That(cmd, Is.Empty)))

    [<Test>]
    member _.``Repeated ticks keep an empty-snapshot terminal poll single-flight``() =
        let model =
            { focusModel with
                TerminalPaneOpen = false
                TerminalPaneTarget = None
                EmbeddedTerminals = EmbeddedTerminalSnapshot.empty
                ActiveEmbeddedTerminals = Map.empty
                EmbeddedTerminalPollInFlight = false }

        let firstPoll, firstCmd =
            App.update
                (Tick 1_000.0)
                model

        let repeatedTick, repeatedCmd =
            App.update
                (Tick 2_000.0)
                firstPoll

        Assert.Multiple(fun () ->
            Assert.That(firstPoll.EmbeddedTerminalPollInFlight, Is.True)
            Assert.That(repeatedTick.EmbeddedTerminalPollInFlight, Is.True)
            Assert.That(
                List.length firstCmd,
                Is.EqualTo(List.length repeatedCmd + 1),
                "the first tick should add one terminal request and later ticks should not"
            ))

    [<Test>]
    member _.``Failed terminal poll permits the next tick to retry``() =
        let failed, failureCmd =
            App.update
                EmbeddedTerminalPollFailed
                { focusModel with EmbeddedTerminalPollInFlight = true }

        let retry, retryCmd =
            App.update
                (Tick 1_000.0)
                failed

        let repeatedTick, repeatedCmd =
            App.update
                (Tick 2_000.0)
                retry

        Assert.Multiple(fun () ->
            Assert.That(failed.EmbeddedTerminalPollInFlight, Is.False)
            Assert.That(failureCmd, Is.Empty)
            Assert.That(retry.EmbeddedTerminalPollInFlight, Is.True)
            Assert.That(repeatedTick.EmbeddedTerminalPollInFlight, Is.True)
            Assert.That(
                List.length retryCmd,
                Is.EqualTo(List.length repeatedCmd + 1),
                "the first tick after a failure should issue a new terminal request"
            ))

    [<Test>]
    member _.``First polled terminal remains background state until the user opens the pane``() =
        let discovered =
            { Tabs = [ running firstOne first 61231 ] }
        let model =
            { focusModel with
                TerminalPaneOpen = false
                TerminalPaneTarget = None
                EmbeddedTerminals = EmbeddedTerminalSnapshot.empty
                ActiveEmbeddedTerminals = Map.empty
                EmbeddedTerminalPollInFlight = true }

        let updated, cmd =
            App.update
                (EmbeddedTerminalSnapshotChanged discovered)
                model

        Assert.Multiple(fun () ->
            Assert.That(updated.EmbeddedTerminals, Is.EqualTo(discovered))
            Assert.That(updated.TerminalPaneOpen, Is.False)
            Assert.That(updated.TerminalPaneTarget, Is.EqualTo(None))
            Assert.That(updated.FocusedElement, Is.EqualTo(model.FocusedElement))
            Assert.That(updated.Repos, Is.EqualTo(model.Repos))
            Assert.That(updated.EmbeddedTerminalPollInFlight, Is.False)
            Assert.That(
                activeTerminalId
                    (Some first)
                    updated.ActiveEmbeddedTerminals
                    updated.EmbeddedTerminals,
                Is.EqualTo(Some firstOne),
                "the discovered terminal should be attachable when its worktree is selected"
            )
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Explicit Canvas session builds the direct action launch``() =
        let filename = "status.html"
        let doc =
            { Filename = filename
              ContentHash = "hash"
              LastModified = DateTimeOffset.UtcNow
              OwnerSessionId = None
              Kind = AgentDoc }
        let model =
            { focusModel with
                Repos =
                    focusModel.Repos
                    |> List.map (fun repo ->
                        { repo with
                            Worktrees =
                                repo.Worktrees
                                |> List.map (fun worktree ->
                                    if worktree.Path = first then
                                        { worktree with CanvasDocs = [ doc ] }
                                    else
                                        worktree) })
                Canvas.ActiveCanvasDoc =
                    Map.ofList [
                        WorktreePath.value first, filename
                    ] }
        let action =
            CanvasUpdate.canvasSessionAction
                (WorktreePath.value first)
                model

        Assert.That(
            action,
            Is.EqualTo(
                Some(
                    first,
                    CanvasSession(
                        CanvasSessionPrompt.forAgentDoc
                            (WorktreePath.value first)
                            filename
                    )
                )
            )
        )

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
    member _.``Worktree selection permanently cancels pending reconnect focus``() =
        let reconnecting, _ =
            App.update
                (ReconnectEmbeddedTerminalView firstTwo)
                focusModel

        let selectedOther, _ =
            App.update
                (SetFocus (Some (Card (WorktreePath.value second))))
                reconnecting

        let selectedOriginal, _ =
            App.update
                (SetFocus (Some (Card (WorktreePath.value first))))
                selectedOther

        let loaded, cmd =
            App.update
                (EmbeddedTerminalViewLoaded(firstTwo, 1))
                selectedOriginal

        Assert.Multiple(fun () ->
            Assert.That(
                loaded.EmbeddedTerminalViewStates[firstTwo].FocusAfterLoad,
                Is.False
            )
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Automatic canvas focus preserves an explicit terminal target``() =
        let viewStates =
            Map.empty |> reconnectView firstTwo

        let updated, _ =
            App.update
                (SetFocusNoRetarget
                    (Some (Card (WorktreePath.value second))))
                { focusModel with
                    TerminalPaneTarget = Some third
                    EmbeddedTerminalViewStates = viewStates }

        Assert.Multiple(fun () ->
            Assert.That(updated.TerminalPaneTarget, Is.EqualTo(Some third))
            Assert.That(
                updated.EmbeddedTerminalViewStates,
                Is.EqualTo(viewStates)
            ))

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
