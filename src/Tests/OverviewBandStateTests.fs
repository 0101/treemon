module Tests.OverviewBandStateTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open Navigation
open OverviewData
open OverviewPresentation
open OverviewBand
open AppTypes
open App
open Tests.WorktreeFixtures

let private taskOnlyWorktree =
    { baseWt with
        CodingTool = CodingToolStatus.NoSession
        Planning = { BeadsPlanning.zero with Planned = 1 } }

let private reviewingWorktree =
    { baseWt with
        CodingTool = CodingToolStatus.Working
        CurrentSkill = Some "review"
        Sessions =
            [ { InstanceId = SessionInstanceId "reviewing"
                Status = CodingToolStatus.Working
                Skill = Some "review"
                ContextUsage = None } ] }

let private repo worktree : RepoModel =
    { RepoId = RepoId "repo"
      Name = "repo"
      Worktrees = [ worktree ]
      ArchivedWorktrees = []
      IsReady = true
      IsCollapsed = false
      Provider = None
      BaseBranch = "main" }

let private modelWith repos =
    { Repos = repos
      IsLoading = false
      HasError = false
      SortMode = ByActivity
      FocusWindow = WorktreeFocus.Window.Off
      Now = DateTimeOffset.UnixEpoch
      TerminalHostUpdate = TerminalHostUpdateModel.initial
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      AutoSyncPending = Set.empty
      AppVersion = Some "1.0"
      EditorName = "VS Code"
      WorktreeSkills = []
      FocusedElement = None
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      DeployBranch = None
      SystemMetrics = None
      ActionCooldowns = Set.empty
      Activity = ActivityState.empty
      Mascot = MascotState.empty
      Workspace = WorkspaceLayout.empty
      TerminalPaneOpen = false
      TerminalPaneTarget = None
      EmbeddedTerminals = EmbeddedTerminalSnapshot.empty
      DismissedEmbeddedTerminals = Set.empty
      ActiveEmbeddedTerminals = Map.empty
      EmbeddedTerminalStarts = Map.empty
      EmbeddedTerminalViewStates = Map.empty
      Canvas = CanvasState.empty
      OverviewPanelOpen = true
      OverviewAgentsStuck = false
      SelectedOverviewGroup = None
      OverviewHistoryWindow = None
      OverviewHistory = None
      OverviewHistoryRequestedAt = DateTimeOffset.Now
      OverviewHistoryRequestInFlight = None
      WorktreeSearch = WorktreeSearch.initial
      EmbeddedTerminalPollInFlight = false }

let private response repos =
    { Repos = repos |> List.map toRepoWorktrees
      SchedulerEvents = []
      LatestByCategory = Map.empty
      AppVersion = "1.0"
      DeployBranch = None
      SystemMetrics = None
      EditorName = "VS Code"
      WorktreeSkills = []
      CollapsedRepos = Set.empty
      TerminalPaneOpen = false
      CanvasPaneOpen = false
      OverviewPanelOpen = true
      WorkspaceWidth = WorkspaceWidth.EqualThirds
      TerminalHostUpdate = TerminalHostUpdateState.Unavailable }

let private subscriptionKeys model =
    appSubscriptions model |> List.map (fst >> String.concat "/")

let private reviewing =
    OverviewSelection.Agents (AgentGroupKind.Activity CurrentActivity.Reviewing)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewBandStateTests() =

    [<Test>]
    member _.``Escape from one-pane Canvas reveals Worktrees instead of closing its hidden drilldown``() =
        let model =
            { modelWith [ repo reviewingWorktree ] with
                Workspace.Mode = WorkspaceLayout.Mode.OnePane
                Workspace.ActivePane = WorkspaceLayout.Pane.Canvas
                SelectedOverviewGroup = Some reviewing }
        let updated, _ = update (KeyPressed("Escape", false)) model

        Assert.That(updated.Workspace.ActivePane, Is.EqualTo(WorkspaceLayout.Pane.Worktrees))
        Assert.That(updated.SelectedOverviewGroup, Is.EqualTo(Some reviewing))

    [<Test>]
    member _.``Sticky boundary requires the sentinel to pass above the dashboard``() =
        Assert.That(isPastStickyBoundary 99.0 100.0, Is.True)
        Assert.That(isPastStickyBoundary 100.0 100.0, Is.False)
        Assert.That(isPastStickyBoundary 101.0 100.0, Is.False)

    [<Test>]
    member _.``Sticky subscription follows agent group presence``() =
        Assert.That(subscriptionKeys (modelWith [ repo taskOnlyWorktree ]), Does.Not.Contain("overview-sticky"))
        Assert.That(subscriptionKeys (modelWith [ repo reviewingWorktree ]), Does.Contain("overview-sticky"))

    [<Test>]
    member _.``Agent group selection scrolls only from the pinned strip``() =
        let model = modelWith [ repo reviewingWorktree ]
        let normalModel, normalCmd = update (SelectOverviewGroup reviewing) model
        let pinnedModel, pinnedCmd =
            update
                (SelectOverviewGroup reviewing)
                { model with OverviewAgentsStuck = true }

        Assert.That(normalModel.SelectedOverviewGroup, Is.EqualTo(Some reviewing))
        Assert.That(normalCmd, Is.Empty)
        Assert.That(pinnedModel.SelectedOverviewGroup, Is.EqualTo(Some reviewing))
        Assert.That(pinnedCmd, Is.Not.Empty)

    [<Test>]
    member _.``Data refresh resets pinned state when agent groups disappear``() =
        let current =
            { modelWith [ repo taskOnlyWorktree ] with
                OverviewAgentsStuck = true
                SelectedOverviewGroup = Some reviewing }
        let updated, _ = update (DataLoaded(response current.Repos, DateTimeOffset.UtcNow)) current

        Assert.That(updated.OverviewAgentsStuck, Is.False)
        Assert.That(updated.SelectedOverviewGroup, Is.EqualTo(None))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WorktreeFocusStateTests() =

    let at = DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)
    let oldPath = WorktreePath "old-worktree"
    let oldKey = WorktreePath.value oldPath
    let recentPath = WorktreePath "recent-worktree"
    let doc =
        { Filename = "focus.html"
          ContentHash = "kept-hash"
          LastModified = at
          OwnerSessionId = None
          Kind = AgentDoc }
    let old =
        { reviewingWorktree with
            Path = oldPath
            SessionActivityAt = Some(at.AddHours(-3.0))
            CanvasDocs = [ doc ] }
    let recent =
        { taskOnlyWorktree with
            Path = recentPath
            SessionActivityAt = Some at }
    let fixture =
        { modelWith [ { repo old with Worktrees = [ old; recent ] } ] with
            Now = at
            Activity.LastActivityTime = float(at.ToUnixTimeMilliseconds())
            Activity.ActivityLevel = ActivityLevel.Active }

    [<Test>]
    member _.``Cycling Focus changes only the projection and wraps back to all work``() =
        let states =
            [ 1 .. 4 ]
            |> List.scan (fun model _ -> update CycleWorktreeFocus model |> fst) fixture

        Assert.Multiple(fun () ->
            Assert.That(states |> List.map (_.FocusWindow >> WorktreeFocus.label),
                Is.EqualTo([ "off"; "2h"; "8h"; "24h"; "off" ]))
            Assert.That((dashboardRepos states[1]).Head.Worktrees |> List.map _.Path, Is.EqualTo([ recentPath ]))
            Assert.That(states |> List.map _.Repos, Is.EqualTo(List.replicate 5 fixture.Repos))
            Assert.That(dashboardRepos (List.last states), Is.EqualTo fixture.Repos))

    [<Test>]
    member _.``Filtering the focused card preserves its pane document terminals and mounted state``() =
        let selected =
            { fixture with
                FocusedElement = Some(Card oldKey)
                Canvas.CanvasPaneOpen = true
                Canvas.ActiveCanvasDoc = Map.ofList [ oldKey, doc.Filename ]
                Canvas.MountedAgentDocHashes = Map.ofList [ (oldKey, doc.Filename), doc.ContentHash ]
                TerminalPaneOpen = true
                ActiveEmbeddedTerminals = Map.ofList [ oldPath, EmbeddedTerminalId "kept-terminal" ]
                SelectedOverviewGroup = Some reviewing
                OverviewAgentsStuck = true }
        let focused, command = update CycleWorktreeFocus selected

        Assert.Multiple(fun () ->
            Assert.That(command, Is.Empty, "Focus is a view change, not a session operation")
            Assert.That(focused.FocusedElement, Is.EqualTo(Some(RepoHeader(RepoId "repo"))))
            Assert.That(focused.Canvas.TargetWorktree, Is.EqualTo(Some oldKey))
            Assert.That(focused.TerminalPaneTarget, Is.EqualTo(Some oldPath))
            Assert.That(CanvasUpdate.activeVisibleDoc focused, Is.EqualTo(Some(oldKey, doc.Filename)))
            Assert.That(focused.Canvas.MountedAgentDocHashes, Is.EqualTo selected.Canvas.MountedAgentDocHashes)
            Assert.That(focused.ActiveEmbeddedTerminals, Is.EqualTo selected.ActiveEmbeddedTerminals)
            Assert.That(focused.SelectedOverviewGroup, Is.EqualTo None)
            Assert.That(focused.OverviewAgentsStuck, Is.False)
            Assert.That(subscriptionKeys focused, Does.Not.Contain("overview-sticky")))

        let navigated, _ = update (SetFocus(Some(Card(WorktreePath.value recentPath)))) focused
        Assert.Multiple(fun () ->
            Assert.That(navigated.Canvas.TargetWorktree, Is.EqualTo None)
            Assert.That(navigated.TerminalPaneTarget, Is.EqualTo None))

    [<Test>]
    member _.``An independent pane target is preserved instead of replaced by the expired card``() =
        let selected =
            { fixture with
                FocusedElement = Some(Card oldKey)
                Canvas.TargetWorktree = Some(WorktreePath.value recentPath)
                TerminalPaneTarget = Some recentPath }
        let focused, _ = update CycleWorktreeFocus selected

        Assert.Multiple(fun () ->
            Assert.That(focused.Canvas.TargetWorktree, Is.EqualTo selected.Canvas.TargetWorktree)
            Assert.That(focused.TerminalPaneTarget, Is.EqualTo selected.TerminalPaneTarget))

    [<Test>]
    member _.``A clock tick expires cards even after a failed data poll``() =
        let expiry = at.AddHours(2.0).AddMilliseconds 1.0
        let eligible =
            { fixture with
                Repos = [ repo { old with SessionActivityAt = Some at } ]
                FocusWindow = WorktreeFocus.Window.Hours2
                FocusedElement = Some(Card oldKey)
                Activity.LastActivityTime = float(expiry.ToUnixTimeMilliseconds()) }
        let failed, _ = update (DataFailed(Exception "fixture failure")) eligible
        let expired, _ = update (Tick(float(expiry.ToUnixTimeMilliseconds()))) failed

        Assert.Multiple(fun () ->
            Assert.That(expired.Now, Is.EqualTo expiry)
            Assert.That((dashboardRepos expired).Head.Worktrees, Is.Empty)
            Assert.That(expired.Repos, Is.EqualTo eligible.Repos)
            Assert.That(expired.HasError, Is.True)
            Assert.That(expired.Canvas.TargetWorktree, Is.EqualTo(Some oldKey))
            Assert.That(expired.TerminalPaneTarget, Is.EqualTo(Some oldPath)))

    [<Test>]
    member _.``DataLoaded updates the Focus clock and restores newly active work without clearing Focus``() =
        let filtered = { fixture with FocusWindow = WorktreeFocus.Window.Hours2 }
        let refreshedAt = at.AddHours 4.0
        let repos = [ repo { old with SessionActivityAt = Some refreshedAt; CanvasDocs = [] } ]
        let refreshed, _ = update (DataLoaded(response repos, refreshedAt)) filtered

        Assert.Multiple(fun () ->
            Assert.That(refreshed.Now, Is.EqualTo refreshedAt)
            Assert.That(refreshed.FocusWindow, Is.EqualTo WorktreeFocus.Window.Hours2)
            Assert.That((dashboardRepos refreshed).Head.Worktrees |> List.map _.Path, Is.EqualTo([ oldPath ])))

    [<Test>]
    member _.``Hidden cards cannot receive focused-card actions while eligible cards still can``() =
        let filtered = { fixture with FocusWindow = WorktreeFocus.Window.Hours2 }
        let keys = [ "Enter"; "t"; "a"; "A"; "s"; "r"; "e"; "c"; "d"; "D"; "Delete" ]

        Assert.Multiple(fun () ->
            Assert.That(keys |> List.map (fun key -> keyBinding (Card oldKey) key filtered),
                Is.EqualTo(List.replicate keys.Length (None: Msg option)))
            Assert.That(keyBinding (Card(WorktreePath.value recentPath)) "e" filtered,
                Is.EqualTo(Some(OpenEditor recentPath))))

    [<Test>]
    member _.``End and Escape navigation use only projected cards and repository headers``() =
        let filtered =
            { fixture with
                Repos = [ { fixture.Repos.Head with Worktrees = [ recent; old ] } ]
                FocusWindow = WorktreeFocus.Window.Hours2
                FocusedElement = Some(Card oldKey) }
        let ended, _ = update (KeyPressed("End", false)) filtered
        let escaped, _ = update (KeyPressed("Escape", false)) filtered

        Assert.Multiple(fun () ->
            Assert.That(ended.FocusedElement, Is.EqualTo(Some(Card(WorktreePath.value recentPath))))
            Assert.That(escaped.FocusedElement, Is.EqualTo(Some(RepoHeader(RepoId "repo")))))

    [<Test>]
    member _.``Ctrl P searches hidden work and choosing it clears Focus before reveal``() =
        let filtered = { fixture with FocusWindow = WorktreeFocus.Window.Hours2 }
        let opened, _ = update (WorktreeSearchMsg WorktreeSearch.Msg.Open) filtered
        let selected, _ =
            update (WorktreeSearchMsg(WorktreeSearch.Msg.ChooseResult oldPath)) opened

        Assert.Multiple(fun () ->
            Assert.That(WorktreeSearch.search filtered.Repos "" |> List.map _.Worktree.Path,
                Does.Contain oldPath)
            Assert.That(selected.FocusWindow, Is.EqualTo WorktreeFocus.Window.Off)
            Assert.That(selected.FocusedElement, Is.EqualTo(Some(Card oldKey)))
            Assert.That(dashboardRepos selected, Is.EqualTo selected.Repos)
            Assert.That(selected.WorktreeSearch, Is.EqualTo WorktreeSearch.State.Closed))

    [<Test>]
    member _.``Canvas overview can explicitly reveal a worktree hidden by Focus``() =
        let filtered =
            { fixture with
                FocusWindow = WorktreeFocus.Window.Hours2
                Canvas.CanvasPaneOpen = true }
        let selected, _ =
            update (FocusOverviewCard oldKey) filtered

        Assert.Multiple(fun () ->
            Assert.That(selected.FocusWindow, Is.EqualTo WorktreeFocus.Window.Off)
            Assert.That(selected.FocusedElement, Is.EqualTo(Some(Card oldKey))))

    [<TestCase("")>]
    [<TestCase("legacy setup warning")>]
    member _.``Successful creation clears Focus including warning results``(warning: string) =
        let creating =
            { fixture with
                FocusWindow = WorktreeFocus.Window.Hours2
                CreateModal = CreateWorktreeModal.Creating(RepoId "repo") }
        let warnings = if warning = "" then [] else [ warning ]
        let created, _ =
            update (ModalMsg(CreateWorktreeModal.CreateWorktreeCompleted(Ok warnings))) creating

        Assert.That(created.FocusWindow, Is.EqualTo WorktreeFocus.Window.Off)

    [<Test>]
    member _.``Failed creation does not change the Focus selection``() =
        let creating =
            { fixture with
                FocusWindow = WorktreeFocus.Window.Hours8
                CreateModal = CreateWorktreeModal.Creating(RepoId "repo") }
        let failed, _ =
            update (ModalMsg(CreateWorktreeModal.CreateWorktreeCompleted(Error "fixture failure"))) creating

        Assert.That(failed.FocusWindow, Is.EqualTo WorktreeFocus.Window.Hours8)

    [<Test>]
    member _.``An empty Focus result preserves global history and rejects stale group notifications``() =
        let history =
            { Window = HistoryWindow.Hours12
              Response = { Anchor = at; Snapshots = [] } }
        let selected =
            { fixture with
                Repos = [ repo old ]
                OverviewHistoryWindow = Some HistoryWindow.Hours12
                OverviewHistory = Some history }
        let filtered, _ = update CycleWorktreeFocus selected
        let stalePinned, _ = update (SetOverviewAgentsStuck true) filtered
        let staleSelection, _ = update (SelectOverviewGroup reviewing) stalePinned

        Assert.Multiple(fun () ->
            Assert.That(staleSelection.OverviewHistoryWindow, Is.EqualTo selected.OverviewHistoryWindow)
            Assert.That(staleSelection.OverviewHistory, Is.EqualTo selected.OverviewHistory)
            Assert.That(staleSelection.SelectedOverviewGroup, Is.EqualTo None)
            Assert.That(staleSelection.OverviewAgentsStuck, Is.False)
            Assert.That(subscriptionKeys staleSelection, Does.Not.Contain("overview-sticky")))
