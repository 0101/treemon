module Tests.OverviewBandStateTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open Navigation
open OverviewData
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
            [ { Status = CodingToolStatus.Working
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
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      SyncPending = Set.empty
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
      Canvas = CanvasState.empty
      OverviewPanelOpen = true
      OverviewAgentsStuck = false
      SelectedOverviewGroup = None }

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
      CanvasPaneOpen = false
      OverviewPanelOpen = true
      CanvasPosition = CanvasPosition.Right
      CanvasSize = CanvasSize.Ratio1To1 }

let private subscriptionKeys model =
    appSubscriptions model |> List.map (fst >> String.concat "/")

let private reviewing =
    OverviewSelection.Agents (AgentGroupKind.Activity CurrentActivity.Reviewing)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewBandStateTests() =

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
