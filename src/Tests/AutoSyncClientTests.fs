module Tests.AutoSyncClientTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open Navigation
open AppTypes
open App
open Tests.WorktreeFixtures

let private path = WorktreePath "/wt"
let private scopedKey = WorktreePath.value path

let private model enabled : Model =
    let wt = { baseWt with Path = path; AutoSyncEnabled = enabled }
    let repo =
        { RepoId = RepoId "repo"
          Name = "repo"
          Worktrees = [ wt ]
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
      FocusedElement = Some(Card scopedKey)
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      DeployBranch = None
      SystemMetrics = None
      ActionCooldowns = Set.empty
      AutoSyncPending = Set.empty
      Activity = ActivityState.empty
      Mascot = MascotState.empty
      TerminalPaneOpen = false
      EmbeddedTerminals = EmbeddedTerminalSnapshot.empty
      ActiveEmbeddedTerminals = Map.empty
      EmbeddedTerminalStarts = Map.empty
      Canvas = CanvasState.empty
      OverviewPanelOpen = false
      OverviewAgentsStuck = false
      SelectedOverviewGroup = None
      OverviewHistoryWindow = None
      OverviewHistory = None
      OverviewHistoryRequestedAt = DateTimeOffset.MinValue
      OverviewHistoryRequestInFlight = None }

let private enabled (model: Model) =
    findWorktree scopedKey model |> Option.map _.AutoSyncEnabled

let private pending (model: Model) =
    model.AutoSyncPending.Contains path

let private response enabled : DashboardResponse =
    { Repos =
        [ { RepoId = RepoId "repo"
            RootFolderName = "repo"
            Worktrees = [ { baseWt with Path = path; AutoSyncEnabled = enabled } ]
            IsReady = true
            Provider = None
            BaseBranch = "main" } ]
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
      WorkspaceWidth = WorkspaceWidth.EqualThirds }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncMvuTests() =

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.``ToggleAutoSync updates the card optimistically``(previous: bool) =
        let updated, cmd = update (ToggleAutoSync path) (model previous)
        Assert.Multiple(fun () ->
            Assert.That(enabled updated, Is.EqualTo(Some(not previous)))
            Assert.That(pending updated, Is.True)
            Assert.That(cmd, Is.Not.Empty))

    [<Test>]
    member _.``Second toggle is ignored while the first request is pending``() =
        let first, _ = update (ToggleAutoSync path) (model false)
        let second, cmd = update (ToggleAutoSync path) first
        Assert.Multiple(fun () ->
            Assert.That(enabled second, Is.EqualTo(Some true))
            Assert.That(pending second, Is.True)
            Assert.That(cmd, Is.Empty))

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.``API error rolls the optimistic toggle back``(previous: bool) =
        let optimistic = { model (not previous) with AutoSyncPending = Set.singleton path }
        let updated, cmd = update (AutoSyncToggleResult(path, previous, Error "persist failed")) optimistic
        Assert.Multiple(fun () ->
            Assert.That(enabled updated, Is.EqualTo(Some previous))
            Assert.That(pending updated, Is.False)
            Assert.That(updated.HasError, Is.True)
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Successful API result keeps the optimistic state``() =
        let optimistic = { model true with AutoSyncPending = Set.singleton path }
        let updated, cmd = update (AutoSyncToggleResult(path, false, Ok ())) optimistic
        Assert.Multiple(fun () ->
            Assert.That(enabled updated, Is.EqualTo(Some true))
            Assert.That(pending updated, Is.False)
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Stale poll preserves pending optimistic auto-sync until success``() =
        let optimistic = { model true with AutoSyncPending = Set.singleton path }
        let afterPoll, _ =
            update
                (DataLoaded(response false, DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero)))
                optimistic
        let afterSuccess, cmd =
            update (AutoSyncToggleResult(path, false, Ok ())) afterPoll

        Assert.Multiple(fun () ->
            Assert.That(enabled afterPoll, Is.EqualTo(Some true))
            Assert.That(pending afterPoll, Is.True)
            Assert.That(enabled afterSuccess, Is.EqualTo(Some true))
            Assert.That(pending afterSuccess, Is.False)
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``S key toggles auto-sync for the focused card``() =
        Assert.That(keyBinding (Card scopedKey) "s" (model false), Is.EqualTo(Some(ToggleAutoSync path)))
