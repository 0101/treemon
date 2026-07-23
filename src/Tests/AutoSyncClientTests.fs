module Tests.AutoSyncClientTests

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
      Activity = ActivityState.empty
      Mascot = MascotState.empty
      Canvas = CanvasState.empty
      OverviewPanelOpen = false
      SelectedOverviewGroup = None }

let private enabled (model: Model) =
    findWorktree scopedKey model |> Option.map _.AutoSyncEnabled

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
            Assert.That(cmd, Is.Not.Empty))

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.``API error rolls the optimistic toggle back``(previous: bool) =
        let optimistic = model (not previous)
        let updated, cmd = update (AutoSyncToggleResult(path, previous, Error "persist failed")) optimistic
        Assert.Multiple(fun () ->
            Assert.That(enabled updated, Is.EqualTo(Some previous))
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``Successful API result keeps the optimistic state``() =
        let optimistic = model true
        let updated, cmd = update (AutoSyncToggleResult(path, false, Ok ())) optimistic
        Assert.Multiple(fun () ->
            Assert.That(enabled updated, Is.EqualTo(Some true))
            Assert.That(cmd, Is.Empty))

    [<Test>]
    member _.``S key toggles auto-sync for the focused card``() =
        Assert.That(keyBinding (Card scopedKey) "s" (model false), Is.EqualTo(Some(ToggleAutoSync path)))
