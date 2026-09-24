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
      SessionActivityAt = None
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
      TerminalHostUpdate = TerminalHostUpdateModel.initial
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
      Workspace = WorkspaceLayout.empty
      TerminalPaneOpen = false
      TerminalPaneTarget = None
      EmbeddedTerminals = EmbeddedTerminalSnapshot.empty
      DismissedEmbeddedTerminals = Set.empty
      ActiveEmbeddedTerminals = Map.empty
      EmbeddedTerminalStarts = Map.empty
      EmbeddedTerminalViewStates = Map.empty
      Canvas = CanvasState.empty
      OverviewPanelOpen = false
      OverviewAgentsStuck = false
      SelectedOverviewGroup = None
      OverviewHistoryWindow = None
      OverviewHistory = None
      OverviewHistoryRequestedAt = System.DateTimeOffset.Now
      OverviewHistoryRequestInFlight = None
      WorktreeSearch = WorktreeSearch.initial
      EmbeddedTerminalPollInFlight = false }
let private updateModel msg model = update msg model |> fst

let private dashboardResponse repos : DashboardResponse =
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
      OverviewPanelOpen = false
      WorkspaceWidth = WorkspaceWidth.EqualThirds
      TerminalHostUpdate = TerminalHostUpdateState.Unavailable }




[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DeleteWithSessionSequencingTests() =

    let modelWithConfirmDelete =
        { defaultModel with ConfirmModal = ConfirmModal.ConfirmDelete ("feature-branch", testPath, true) }

    [<Test>]
    member _.``Worktree search does not stack over confirmation modal``() =
        let model =
            updateModel
                (WorktreeSearchMsg WorktreeSearch.Msg.Open)
                modelWithConfirmDelete

        Assert.Multiple(fun () ->
            Assert.That(model.WorktreeSearch, Is.EqualTo(WorktreeSearch.State.Closed))
            Assert.That(model.ConfirmModal, Is.EqualTo(modelWithConfirmDelete.ConfirmModal)))

    [<Test>]
    member _.``Create modal does not stack over confirmation modal``() =
        let model =
            updateModel
                (ModalMsg (
                    CreateWorktreeModal.OpenCreateWorktree(
                        RepoId "repo",
                        []
                    )
                ))
                modelWithConfirmDelete

        Assert.Multiple(fun () ->
            Assert.That(model.CreateModal, Is.EqualTo(CreateWorktreeModal.Closed))
            Assert.That(model.ConfirmModal, Is.EqualTo(modelWithConfirmDelete.ConfirmModal)))

    [<Test>]
    member _.``Confirmation modal does not stack over worktree search``() =
        let searchOpen =
            updateModel
                (WorktreeSearchMsg WorktreeSearch.Msg.Open)
                defaultModel
        let model =
            updateModel
                (ConfirmDeleteWorktree (WorktreePath.value testPath))
                searchOpen

        Assert.Multiple(fun () ->
            Assert.That(WorktreeSearch.isOpen model.WorktreeSearch, Is.True)
            Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm)))

    [<Test>]
    member _.``Confirmation modal does not stack over create modal``() =
        let openForm =
            CreateWorktreeModal.Open
                { RepoId = RepoId "repo"
                  Branches = [ "main" ]
                  Name = ""
                  BaseBranch = "main"
                  Prompt = ""
                  AvailableSkills = []
                  Skill = None }
        let model =
            updateModel
                (ConfirmDeleteWorktree (WorktreePath.value testPath))
                { defaultModel with CreateModal = openForm }

        Assert.Multiple(fun () ->
            Assert.That(model.CreateModal, Is.EqualTo(openForm))
            Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm)))

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
    member _.``failed delete stays hidden and requests authoritative refresh``() =
        let pending =
            updateModel
                (ConfirmMsg(ConfirmModal.DeleteWorktree testPath))
                modelWithConfirmDelete

        let recovered, refresh =
            update
                (DeleteCompleted(Error "TerminalHost update is in progress"))
                pending

        Assert.Multiple(fun () ->
            Assert.That(
                recovered.DeletedPaths,
                Does.Contain(WorktreePath.value testPath),
                "confirmation keeps the worktree hidden for the browser session"
            )

            Assert.That(
                refresh,
                Is.Not.Empty,
                "failure must request the authoritative worktree snapshot"
            ))

    [<Test>]
    member _.``delete transport failure is dispatched as an explicit result``() =
        task {
            let api =
                { Server.WorktreeApi.readOnlyApi
                      "test"
                      (fun () -> failwith "unused")
                      (fun () -> failwith "unused") with
                    deleteWorktree =
                        fun _ ->
                            async {
                                return
                                    raise (
                                        InvalidOperationException(
                                            "simulated transport failure"
                                        )
                                    )
                            } }

            let dispatched =
                System.Threading.Tasks.TaskCompletionSource<Msg>(
                    System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                )

            deleteWorktreeCmd (lazy api) testPath
            |> List.iter (fun effect ->
                effect (fun message ->
                    dispatched.TrySetResult message |> ignore))

            let! message =
                dispatched.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            Assert.That(
                message,
                Is.EqualTo(
                    DeleteCompleted(Error "simulated transport failure")
                )
            )
        }

    [<Test>]
    member _.``ConfirmMsg DeleteAfterKillSession immediately removes worktree from model``() =
        let model = updateModel (ConfirmMsg (ConfirmModal.DeleteAndCloseSession testPath)) modelWithConfirmDelete

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Not.Contain("feature-branch"))
        Assert.That(
            model.DeletedPaths,
            Does.Contain(WorktreePath.value testPath)
        )

    [<Test>]
    member _.``stale response cannot restore a confirmed deletion``() =
        let deleting =
            updateModel
                (ConfirmMsg(ConfirmModal.DeleteWorktree testPath))
                modelWithConfirmDelete

        let absent =
            updateModel
                (DataLoaded(
                    dashboardResponse [
                        makeRepo "repo" [ makeWorktree "main" false ]
                    ],
                    DateTimeOffset.UnixEpoch
                ))
                deleting

        let stale =
            updateModel
                (DataLoaded(
                    dashboardResponse defaultModel.Repos,
                    DateTimeOffset.UnixEpoch
                ))
                absent

        Assert.That(
            stale.Repos
            |> List.collect _.Worktrees
            |> List.map _.Path,
            Does.Not.Contain(testPath)
        )

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
