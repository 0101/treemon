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
      OverviewHistoryRequestInFlight = None
      EmbeddedTerminalPollInFlight = false }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalFocusTests() =

    [<Test>]
    member _.``T key opens or focuses the embedded terminal for the focused card``() =
        Assert.That(
            App.keyBinding
                (Card (WorktreePath.value first))
                "t"
                focusModel,
            Is.EqualTo(Some(OpenEmbeddedTerminal first))
        )

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
