module Tests.ConfirmModalTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open AppTypes
open Navigation
open CanvasState

let private testPath = WorktreePath "/repo/feature-branch"

let private makeWorktree branch hasSession : WorktreeStatus =
    { Path = WorktreePath $"/repo/{branch}"
      Branch = branch
      LastCommitMessage = "msg"
      LastCommitTime = DateTimeOffset.UtcNow
      Beads = BeadsSummary.zero
      Planning = BeadsPlanning.zero
      CodingTool = CodingToolStatus.Idle
      CodingToolProvider = None
      CodingToolSince = None
      CurrentSkill = None
      AgentActivity = None
      Sessions = []
      LastUserMessage = None
      LastAssistantMessage = None
      Pr = PrStatus.NoPr
      MainBehindCount = 0
      AutoSyncEnabled = false
      IsDirty = false
      HasDiff = false
      WorkMetrics = None
      HasActiveSession = hasSession
      IsMainWorktree = false
      IsArchived = false
      CanvasDocs = [] }

let private makeRepo repoId worktrees : RepoModel =
    { RepoId = RepoId repoId
      Name = repoId
      Worktrees = worktrees
      ArchivedWorktrees = []
      IsReady = true
      IsCollapsed = false
      Provider = None
      BaseBranch = "main" }

let private defaultModel : Model =
    { Repos = [ makeRepo "repo" [ makeWorktree "feature-branch" true; makeWorktree "main" false ] ]
      IsLoading = false
      HasError = false
      SortMode = ByActivity
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      AppVersion = Some "1.0"
      DeployBranch = None
      SystemMetrics = None
      FocusedElement = None
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      EditorName = "VS Code"
      WorktreeSkills = []
      ActionCooldowns = Set.empty
      AutoSyncPending = Set.empty
      Activity = ActivityState.empty
      Mascot = MascotState.empty
      TerminalPaneOpen = false
      EmbeddedTerminals = EmbeddedTerminalSnapshot.empty
      ActiveEmbeddedTerminal = None
      ClosingEmbeddedTerminals = Map.empty
      Canvas = CanvasState.empty
      OverviewPanelOpen = false
      OverviewAgentsStuck = false
      SelectedOverviewGroup = None
      OverviewHistoryWindow = None
      OverviewHistory = None
      OverviewHistoryRequestedAt = System.DateTimeOffset.Now
      OverviewHistoryRequestInFlight = None }
let private updateModel msg model = update msg model |> fst




[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DeleteWithSessionSequencingTests() =

    let modelWithConfirmDelete =
        { defaultModel with ConfirmModal = ConfirmModal.ConfirmDelete ("feature-branch", testPath, true) }

    [<Test>]
    member _.``ConfirmMsg Delete immediately removes worktree from model``() =
        let model = updateModel (ConfirmMsg (ConfirmModal.DeleteWorktree testPath)) modelWithConfirmDelete

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Not.Contain("feature-branch"),
            "Worktree should be removed optimistically from model on direct Delete")
        Assert.That(model.DeletedPaths, Does.Contain(WorktreePath.value testPath),
            "Path should be added to DeletedPaths for ghost suppression")
        Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm),
            "Confirming deletion should dismiss the modal")

    [<Test>]
    member _.``ConfirmMsg DeleteAfterKillSession does NOT remove worktree from model``() =
        let model = updateModel (ConfirmMsg (ConfirmModal.DeleteAndCloseSession testPath)) modelWithConfirmDelete

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Contain("feature-branch"),
            "Worktree should NOT be removed yet — must wait for session kill to succeed")
        Assert.That(model.DeletedPaths, Is.Empty,
            "DeletedPaths should remain empty until session is confirmed killed")

    [<Test>]
    member _.``SessionKilledForDelete removes worktree from model``() =
        let model = updateModel (SessionKilledForDelete testPath) defaultModel

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Not.Contain("feature-branch"),
            "Worktree should be removed after session kill confirmed")
        Assert.That(model.DeletedPaths, Does.Contain(WorktreePath.value testPath))

    [<Test>]
    member _.``ConfirmMsg DismissConfirm preserves model repos``() =
        let model = updateModel (ConfirmMsg ConfirmModal.DismissConfirm) modelWithConfirmDelete

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Contain("feature-branch"),
            "Worktree should remain when user cancels")
        Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm),
            "Modal should be dismissed")

    [<Test>]
    member _.``ConfirmMsg DeleteAfterKillSession dismisses modal``() =
        let model = updateModel (ConfirmMsg (ConfirmModal.DeleteAndCloseSession testPath)) modelWithConfirmDelete
        Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm))

    [<Test>]
    member _.``Escape while confirm modal open dismisses it without deleting``() =
        let model = updateModel (KeyPressed ("Escape", false)) modelWithConfirmDelete

        Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm),
            "Escape should dismiss the confirm modal")

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Contain("feature-branch"),
            "Worktree should remain after Escape")
