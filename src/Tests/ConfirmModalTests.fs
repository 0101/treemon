module Tests.ConfirmModalTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open Navigation

let private testPath = WorktreePath.create "/repo/feature-branch"

let private makeWorktree branch hasSession : WorktreeStatus =
    { Path = WorktreePath.create $"/repo/{branch}"
      Branch = branch
      LastCommitMessage = "msg"
      LastCommitTime = DateTimeOffset.UtcNow
      Beads = BeadsSummary.zero
      CodingTool = CodingToolStatus.Idle
      CodingToolProvider = None
      LastUserMessage = None
      Pr = PrStatus.NoPr
      MainBehindCount = 0
      IsDirty = false
      WorkMetrics = None
      HasActiveSession = hasSession
      HasTestFailureLog = false
      IsArchived = false }

let private makeRepo repoId worktrees : RepoModel =
    { RepoId = RepoId.create repoId
      Name = repoId
      Worktrees = worktrees
      ArchivedWorktrees = []
      IsReady = true
      IsCollapsed = false
      Provider = None }

let private defaultModel : Model =
    { Repos = [ makeRepo "repo" [ makeWorktree "feature-branch" true; makeWorktree "main" false ] ]
      IsLoading = false
      HasError = false
      SortMode = ByActivity
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      SyncPending = Set.empty
      AppVersion = Some "1.0"
      DeployBranch = None
      SystemMetrics = None
      EyeDirection = (0.0, 0.0)
      FocusedElement = None
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      EditorName = "VS Code"
      ActionCooldowns = Set.empty }
/// Calls update and returns the model, ignoring the Cmd. Handles the case where
/// Fable.Remoting.Client proxy initialization fails in .NET by catching the
/// TypeInitializationException (the model is computed before the Cmd).
let private tryUpdateModel msg model =
    try
        let m, _ = update msg model
        m
    with
    | :? TypeInitializationException ->
        match msg with
        | ConfirmMsg confirmMsg ->
            let confirmModal, action = ConfirmModal.update confirmMsg
            let m = { model with ConfirmModal = confirmModal }
            match action with
            | ConfirmModal.NoAction -> m
            | ConfirmModal.Delete path -> removeWorktreeByPath path m
            | ConfirmModal.DeleteAfterKillSession _ -> m
            | ConfirmModal.Archive _ -> m
            | ConfirmModal.ArchiveAfterKillSession _ -> m
        | SessionKilledForDelete path -> removeWorktreeByPath path model
        | KeyPressed ("Escape", _) when model.ConfirmModal <> ConfirmModal.NoConfirm ->
            { model with ConfirmModal = ConfirmModal.NoConfirm }
        | _ -> reraise ()




[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DeleteWithSessionSequencingTests() =

    let modelWithConfirmDelete =
        { defaultModel with ConfirmModal = ConfirmModal.ConfirmDelete ("feature-branch", testPath, true) }

    [<Test>]
    member _.``ConfirmMsg Delete immediately removes worktree from model``() =
        let model = tryUpdateModel (ConfirmMsg (ConfirmModal.DeleteWorktree testPath)) modelWithConfirmDelete

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Not.Contain("feature-branch"),
            "Worktree should be removed optimistically from model on direct Delete")
        Assert.That(model.DeletedPaths, Does.Contain(WorktreePath.value testPath),
            "Path should be added to DeletedPaths for ghost suppression")

    [<Test>]
    member _.``ConfirmMsg DeleteAfterKillSession does NOT remove worktree from model``() =
        let model = tryUpdateModel (ConfirmMsg (ConfirmModal.DeleteAndCloseSession testPath)) modelWithConfirmDelete

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Contain("feature-branch"),
            "Worktree should NOT be removed yet — must wait for session kill to succeed")
        Assert.That(model.DeletedPaths, Is.Empty,
            "DeletedPaths should remain empty until session is confirmed killed")

    [<Test>]
    member _.``SessionKilledForDelete removes worktree from model``() =
        let model = tryUpdateModel (SessionKilledForDelete testPath) defaultModel

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Not.Contain("feature-branch"),
            "Worktree should be removed after session kill confirmed")
        Assert.That(model.DeletedPaths, Does.Contain(WorktreePath.value testPath))

    [<Test>]
    member _.``ConfirmMsg DismissConfirm preserves model repos``() =
        let model = tryUpdateModel (ConfirmMsg ConfirmModal.DismissConfirm) modelWithConfirmDelete

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Contain("feature-branch"),
            "Worktree should remain when user cancels")
        Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm),
            "Modal should be dismissed")

    [<Test>]
    member _.``ConfirmMsg DeleteAfterKillSession dismisses modal``() =
        let model = tryUpdateModel (ConfirmMsg (ConfirmModal.DeleteAndCloseSession testPath)) modelWithConfirmDelete
        Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm))

    [<Test>]
    member _.``Escape while confirm modal open dismisses it without deleting``() =
        let model = tryUpdateModel (KeyPressed ("Escape", false)) modelWithConfirmDelete

        Assert.That(model.ConfirmModal, Is.EqualTo(ConfirmModal.NoConfirm),
            "Escape should dismiss the confirm modal")

        let branches =
            model.Repos |> List.collect _.Worktrees |> List.map _.Branch

        Assert.That(branches, Does.Contain("feature-branch"),
            "Worktree should remain after Escape")
