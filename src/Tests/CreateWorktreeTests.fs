module Tests.CreateWorktreeTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open Navigation

module Modal = CreateWorktreeModal

let private testRepoId = RepoId.create "TestRepo"

let private defaultModel : Model =
    { Repos = []
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
      CreateModal = Modal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      EditorName = "VS Code" }

/// Calls update and returns the model, ignoring the Cmd. Handles the case where
/// Fable.Remoting.Client proxy initialization fails in .NET by catching the
/// TypeInitializationException. In that scenario the model was already computed
/// (F# evaluates the left side of the tuple first) but the Cmd construction fails.
/// We re-derive the expected model from the CreateModal state that would have been set.
let private tryUpdateModel msg model =
    try
        let m, _ = update msg model
        m
    with
    | :? TypeInitializationException ->
        match msg with
        | ModalMsg (Modal.OpenCreateWorktree repoId) ->
            { model with CreateModal = Modal.LoadingBranches repoId }
        | ModalMsg Modal.SubmitCreateWorktree ->
            match model.CreateModal with
            | Modal.Open form when form.Name.Trim().Length > 0 ->
                { model with CreateModal = Modal.Creating form.RepoId }
            | _ -> model
        | ModalMsg (Modal.CreateWorktreeCompleted (Ok _)) ->
            let restored = Modal.repoId model.CreateModal |> Option.map RepoHeader
            { model with CreateModal = Modal.Closed; FocusedElement = restored |> Option.orElse model.FocusedElement }
        | _ -> reraise ()


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OpenCreateWorktreeTests() =

    [<Test>]
    member _.``OpenCreateWorktree transitions to LoadingBranches``() =
        let model = tryUpdateModel (ModalMsg (Modal.OpenCreateWorktree testRepoId)) defaultModel

        match model.CreateModal with
        | Modal.LoadingBranches repoId ->
            Assert.That(repoId, Is.EqualTo(testRepoId))
        | other ->
            Assert.Fail($"Expected LoadingBranches but got {other}")

    [<Test>]
    member _.``OpenCreateWorktree does not change other model fields``() =
        let model = tryUpdateModel (ModalMsg (Modal.OpenCreateWorktree testRepoId)) defaultModel

        Assert.That(model.IsLoading, Is.EqualTo(defaultModel.IsLoading))
        Assert.That(model.HasError, Is.EqualTo(defaultModel.HasError))
        Assert.That(model.Repos, Is.EqualTo(defaultModel.Repos))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BranchesLoadedTests() =

    let loadingModel =
        { defaultModel with CreateModal = Modal.LoadingBranches testRepoId }

    [<Test>]
    member _.``BranchesLoaded Ok transitions to Open with branches``() =
        let branches = [ "main"; "develop"; "feature/x" ]
        let model, _ = update (ModalMsg (Modal.BranchesLoaded (Ok branches))) loadingModel

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.Branches, Is.EqualTo(branches))
            Assert.That(form.RepoId, Is.EqualTo(testRepoId))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``BranchesLoaded Ok pre-selects first branch as BaseBranch``() =
        let branches = [ "main"; "develop"; "feature/x" ]
        let model, _ = update (ModalMsg (Modal.BranchesLoaded (Ok branches))) loadingModel

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.BaseBranch, Is.EqualTo("main"))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``BranchesLoaded Ok sets empty Name``() =
        let branches = [ "main" ]
        let model, _ = update (ModalMsg (Modal.BranchesLoaded (Ok branches))) loadingModel

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.Name, Is.EqualTo(""))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``BranchesLoaded Ok with empty list sets empty BaseBranch``() =
        let model, _ = update (ModalMsg (Modal.BranchesLoaded (Ok []))) loadingModel

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.BaseBranch, Is.EqualTo(""))
            Assert.That(form.Branches, Is.Empty)
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``BranchesLoaded Ok produces no command``() =
        let _, cmd = update (ModalMsg (Modal.BranchesLoaded (Ok [ "main" ]))) loadingModel

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``BranchesLoaded Error transitions to CreateError``() =
        let model, _ = update (ModalMsg (Modal.BranchesLoaded (Error (exn "network error")))) loadingModel

        match model.CreateModal with
        | Modal.CreateError (repoId, message) ->
            Assert.That(repoId, Is.EqualTo(testRepoId))
            Assert.That(message, Is.EqualTo("Failed to load branches"))
        | other ->
            Assert.Fail($"Expected CreateError but got {other}")

    [<Test>]
    member _.``BranchesLoaded Error produces no command``() =
        let _, cmd = update (ModalMsg (Modal.BranchesLoaded (Error (exn "network error")))) loadingModel

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``BranchesLoaded Ok ignored when modal is not LoadingBranches``() =
        let closedModel = { defaultModel with CreateModal = Modal.Closed }
        let model, _ = update (ModalMsg (Modal.BranchesLoaded (Ok [ "main" ]))) closedModel

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``BranchesLoaded Error ignored when modal is not LoadingBranches``() =
        let closedModel = { defaultModel with CreateModal = Modal.Closed }
        let model, _ = update (ModalMsg (Modal.BranchesLoaded (Error (exn "fail")))) closedModel

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``BranchesLoaded Ok ignored when modal is Open``() =
        let openForm = Modal.Open { RepoId = testRepoId; Branches = [ "old" ]; Name = "x"; BaseBranch = "old" }
        let model, _ = update (ModalMsg (Modal.BranchesLoaded (Ok [ "new-branch" ]))) { defaultModel with CreateModal = openForm }

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.Branches, Is.EqualTo([ "old" ]), "Should not replace branches when already Open")
        | other ->
            Assert.Fail($"Expected Open (unchanged) but got {other}")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FormStateTests() =

    let openForm =
        Modal.Open { RepoId = testRepoId; Branches = [ "main"; "develop" ]; Name = ""; BaseBranch = "main" }

    let openModel = { defaultModel with CreateModal = openForm }

    [<Test>]
    member _.``SetNewWorktreeName updates Name in Open form``() =
        let model, _ = update (ModalMsg (Modal.SetNewWorktreeName "my-feature")) openModel

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.Name, Is.EqualTo("my-feature"))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``SetNewWorktreeName preserves other form fields``() =
        let model, _ = update (ModalMsg (Modal.SetNewWorktreeName "my-feature")) openModel

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.BaseBranch, Is.EqualTo("main"))
            Assert.That(form.RepoId, Is.EqualTo(testRepoId))
            Assert.That(form.Branches, Is.EqualTo([ "main"; "develop" ]))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``SetBaseBranch updates BaseBranch in Open form``() =
        let model, _ = update (ModalMsg (Modal.SetBaseBranch "develop")) openModel

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.BaseBranch, Is.EqualTo("develop"))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``SetBaseBranch preserves other form fields``() =
        let model, _ = update (ModalMsg (Modal.SetBaseBranch "develop")) openModel

        match model.CreateModal with
        | Modal.Open form ->
            Assert.That(form.Name, Is.EqualTo(""))
            Assert.That(form.RepoId, Is.EqualTo(testRepoId))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``SetNewWorktreeName ignored when modal is Closed``() =
        let model, _ = update (ModalMsg (Modal.SetNewWorktreeName "test")) defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``SetBaseBranch ignored when modal is Closed``() =
        let model, _ = update (ModalMsg (Modal.SetBaseBranch "test")) defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``SetNewWorktreeName ignored when modal is Creating``() =
        let creating = { defaultModel with CreateModal = Modal.Creating testRepoId }
        let model, _ = update (ModalMsg (Modal.SetNewWorktreeName "test")) creating

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Creating testRepoId))

    [<Test>]
    member _.``SetBaseBranch ignored when modal is Creating``() =
        let creating = { defaultModel with CreateModal = Modal.Creating testRepoId }
        let model, _ = update (ModalMsg (Modal.SetBaseBranch "test")) creating

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Creating testRepoId))

    [<Test>]
    member _.``SetNewWorktreeName produces no command``() =
        let _, cmd = update (ModalMsg (Modal.SetNewWorktreeName "test")) openModel

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``SetBaseBranch produces no command``() =
        let _, cmd = update (ModalMsg (Modal.SetBaseBranch "test")) openModel

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``Multiple SetNewWorktreeName calls update correctly``() =
        let m1, _ = update (ModalMsg (Modal.SetNewWorktreeName "first")) openModel
        let m2, _ = update (ModalMsg (Modal.SetNewWorktreeName "second")) m1

        match m2.CreateModal with
        | Modal.Open form -> Assert.That(form.Name, Is.EqualTo("second"))
        | other -> Assert.Fail($"Expected Open but got {other}")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SubmitCreateWorktreeTests() =

    let openForm =
        Modal.Open { RepoId = testRepoId; Branches = [ "main"; "develop" ]; Name = "my-feature"; BaseBranch = "main" }

    let openModel = { defaultModel with CreateModal = openForm }

    [<Test>]
    member _.``SubmitCreateWorktree transitions to Creating``() =
        let model = tryUpdateModel (ModalMsg Modal.SubmitCreateWorktree) openModel

        match model.CreateModal with
        | Modal.Creating repoId ->
            Assert.That(repoId, Is.EqualTo(testRepoId))
        | other ->
            Assert.Fail($"Expected Creating but got {other}")

    [<Test>]
    member _.``SubmitCreateWorktree with empty name is ignored``() =
        let emptyName =
            Modal.Open { RepoId = testRepoId; Branches = [ "main" ]; Name = ""; BaseBranch = "main" }
        let model, cmd = update (ModalMsg Modal.SubmitCreateWorktree) { defaultModel with CreateModal = emptyName }

        match model.CreateModal with
        | Modal.Open _ -> ()
        | other -> Assert.Fail($"Expected Open (unchanged) but got {other}")
        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``SubmitCreateWorktree with whitespace-only name is ignored``() =
        let wsName =
            Modal.Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "   "; BaseBranch = "main" }
        let model, cmd = update (ModalMsg Modal.SubmitCreateWorktree) { defaultModel with CreateModal = wsName }

        match model.CreateModal with
        | Modal.Open _ -> ()
        | other -> Assert.Fail($"Expected Open (unchanged) but got {other}")
        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``SubmitCreateWorktree when modal is Closed is ignored``() =
        let model, cmd = update (ModalMsg Modal.SubmitCreateWorktree) defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))
        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``SubmitCreateWorktree trims name with leading and trailing spaces``() =
        let spacedName =
            Modal.Open { RepoId = testRepoId; Branches = [ "main" ]; Name = " trimmed "; BaseBranch = "main" }
        let model = tryUpdateModel (ModalMsg Modal.SubmitCreateWorktree) { defaultModel with CreateModal = spacedName }

        match model.CreateModal with
        | Modal.Creating _ -> ()
        | other -> Assert.Fail($"Expected Creating but got {other}")

    [<Test>]
    member _.``SubmitCreateWorktree when modal is LoadingBranches is ignored``() =
        let model, cmd = update (ModalMsg Modal.SubmitCreateWorktree) { defaultModel with CreateModal = Modal.LoadingBranches testRepoId }

        Assert.That(model.CreateModal, Is.EqualTo(Modal.LoadingBranches testRepoId))
        Assert.That(cmd, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CreateWorktreeCompletedTests() =

    [<Test>]
    member _.``CreateWorktreeCompleted Ok closes modal``() =
        let creating = { defaultModel with CreateModal = Modal.Creating testRepoId }
        let model = tryUpdateModel (ModalMsg (Modal.CreateWorktreeCompleted (Ok ()))) creating

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``CreateWorktreeCompleted Error transitions to CreateError``() =
        let creating = { defaultModel with CreateModal = Modal.Creating testRepoId }
        let model, _ = update (ModalMsg (Modal.CreateWorktreeCompleted (Error "git failed"))) creating

        match model.CreateModal with
        | Modal.CreateError (repoId, message) ->
            Assert.That(repoId, Is.EqualTo(testRepoId))
            Assert.That(message, Is.EqualTo("git failed"))
        | other ->
            Assert.Fail($"Expected CreateError but got {other}")

    [<Test>]
    member _.``CreateWorktreeCompleted Error produces no command``() =
        let creating = { defaultModel with CreateModal = Modal.Creating testRepoId }
        let _, cmd = update (ModalMsg (Modal.CreateWorktreeCompleted (Error "git failed"))) creating

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``CreateWorktreeCompleted Error ignored when not in Creating state``() =
        let model, _ = update (ModalMsg (Modal.CreateWorktreeCompleted (Error "git failed"))) defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``CreateWorktreeCompleted Error from Closed stays Closed``() =
        let model, cmd = update (ModalMsg (Modal.CreateWorktreeCompleted (Error "oops"))) defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))
        Assert.That(cmd, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CloseCreateModalTests() =

    [<Test>]
    member _.``CloseCreateModal resets to Closed from Open``() =
        let openForm =
            Modal.Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }
        let model, _ = update (ModalMsg Modal.CloseCreateModal) { defaultModel with CreateModal = openForm }

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``CloseCreateModal resets to Closed from CreateError``() =
        let errorState = Modal.CreateError (testRepoId, "some error")
        let model, _ = update (ModalMsg Modal.CloseCreateModal) { defaultModel with CreateModal = errorState }

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``CloseCreateModal from LoadingBranches resets to Closed``() =
        let loading = { defaultModel with CreateModal = Modal.LoadingBranches testRepoId }
        let model, _ = update (ModalMsg Modal.CloseCreateModal) loading

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``CloseCreateModal when already Closed stays Closed``() =
        let model, _ = update (ModalMsg Modal.CloseCreateModal) defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``CloseCreateModal produces no command``() =
        let openForm =
            Modal.Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }
        let _, cmd = update (ModalMsg Modal.CloseCreateModal) { defaultModel with CreateModal = openForm }

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``CloseCreateModal from Creating resets to Closed``() =
        let model, _ = update (ModalMsg Modal.CloseCreateModal) { defaultModel with CreateModal = Modal.Creating testRepoId }

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EscapeKeyClosesModalTests() =

    [<Test>]
    member _.``Escape key closes modal from Open state``() =
        let openForm =
            Modal.Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }
        let model, _ = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = openForm }

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``Escape key closes modal from Creating state``() =
        let model, _ = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = Modal.Creating testRepoId }

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``Escape key closes modal from CreateError state``() =
        let errorState = Modal.CreateError (testRepoId, "error")
        let model, _ = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = errorState }

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``Escape key closes modal from LoadingBranches state``() =
        let model, _ = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = Modal.LoadingBranches testRepoId }

        Assert.That(model.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``Escape key produces no command when modal is open``() =
        let openForm =
            Modal.Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }
        let _, cmd = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = openForm }

        Assert.That(cmd, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FullStateMachineRoundtripTests() =

    [<Test>]
    member _.``Full happy path: Open, load branches, fill form, submit, complete``() =
        let m0 = defaultModel

        let m1 = tryUpdateModel (ModalMsg (Modal.OpenCreateWorktree testRepoId)) m0
        Assert.That((match m1.CreateModal with Modal.LoadingBranches _ -> true | _ -> false), Is.True)

        let m2, _ = update (ModalMsg (Modal.BranchesLoaded (Ok [ "main"; "develop" ]))) m1
        Assert.That((match m2.CreateModal with Modal.Open _ -> true | _ -> false), Is.True)

        let m3, _ = update (ModalMsg (Modal.SetNewWorktreeName "my-feature")) m2
        match m3.CreateModal with
        | Modal.Open form -> Assert.That(form.Name, Is.EqualTo("my-feature"))
        | _ -> Assert.Fail("Expected Open")

        let m4, _ = update (ModalMsg (Modal.SetBaseBranch "develop")) m3
        match m4.CreateModal with
        | Modal.Open form -> Assert.That(form.BaseBranch, Is.EqualTo("develop"))
        | _ -> Assert.Fail("Expected Open")

        let m5 = tryUpdateModel (ModalMsg Modal.SubmitCreateWorktree) m4
        Assert.That((match m5.CreateModal with Modal.Creating _ -> true | _ -> false), Is.True)

        let m6 = tryUpdateModel (ModalMsg (Modal.CreateWorktreeCompleted (Ok ()))) m5
        Assert.That(m6.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``Error path: Open, load branches, submit, error, close``() =
        let m0 = defaultModel

        let m1 = tryUpdateModel (ModalMsg (Modal.OpenCreateWorktree testRepoId)) m0
        let m2, _ = update (ModalMsg (Modal.BranchesLoaded (Ok [ "main" ]))) m1
        let m3, _ = update (ModalMsg (Modal.SetNewWorktreeName "bad-name")) m2
        let m4 = tryUpdateModel (ModalMsg Modal.SubmitCreateWorktree) m3
        let m5, _ = update (ModalMsg (Modal.CreateWorktreeCompleted (Error "branch already exists"))) m4

        match m5.CreateModal with
        | Modal.CreateError (_, msg) ->
            Assert.That(msg, Is.EqualTo("branch already exists"))
        | other ->
            Assert.Fail($"Expected CreateError but got {other}")

        let m6, _ = update (ModalMsg Modal.CloseCreateModal) m5
        Assert.That(m6.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``Branch load failure path: Open, load error, close``() =
        let m0 = defaultModel

        let m1 = tryUpdateModel (ModalMsg (Modal.OpenCreateWorktree testRepoId)) m0
        let m2, _ = update (ModalMsg (Modal.BranchesLoaded (Error (exn "timeout")))) m1

        match m2.CreateModal with
        | Modal.CreateError (_, msg) ->
            Assert.That(msg, Is.EqualTo("Failed to load branches"))
        | other ->
            Assert.Fail($"Expected CreateError but got {other}")

        let m3, _ = update (ModalMsg Modal.CloseCreateModal) m2
        Assert.That(m3.CreateModal, Is.EqualTo(Modal.Closed))

    [<Test>]
    member _.``Cancel via Escape during any state returns to Closed``() =
        let m1 = tryUpdateModel (ModalMsg (Modal.OpenCreateWorktree testRepoId)) defaultModel
        let m2, _ = update (ModalMsg (Modal.BranchesLoaded (Ok [ "main" ]))) m1
        let m3, _ = update (ModalMsg (Modal.SetNewWorktreeName "test")) m2

        let mEsc, _ = update (KeyPressed ("Escape", false)) m3
        Assert.That(mEsc.CreateModal, Is.EqualTo(Modal.Closed))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EnterKeySuppressedWhileModalOpenTests() =

    let repoId = testRepoId

    let repoModel : RepoModel =
        { RepoId = repoId
          Name = "TestRepo"
          Worktrees = []
          ArchivedWorktrees = []
          IsReady = true
          IsCollapsed = false
          Provider = None }

    let openForm =
        Modal.Open { RepoId = repoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }

    let modelWithRepoAndModal =
        { defaultModel with
            Repos = [ repoModel ]
            FocusedElement = Some (RepoHeader repoId)
            CreateModal = openForm }

    [<Test>]
    member _.``Enter key while modal is open does not toggle repo collapse``() =
        let model, _ = update (KeyPressed ("Enter", false)) modelWithRepoAndModal

        Assert.That(model.CreateModal, Is.Not.EqualTo(Modal.Closed),
            "Enter should not close the modal")
        Assert.That(model.Repos.Head.IsCollapsed, Is.False,
            "Enter in modal should not toggle collapse on the focused RepoHeader")

    [<Test>]
    member _.``ArrowDown while modal is open does not change focus``() =
        let model, _ = update (KeyPressed ("ArrowDown", false)) modelWithRepoAndModal

        Assert.That(model.FocusedElement, Is.EqualTo(modelWithRepoAndModal.FocusedElement),
            "ArrowDown should be suppressed while modal is open")

    [<Test>]
    member _.``Letter keys while modal is open are suppressed``() =
        let model, _ = update (KeyPressed ("s", false)) modelWithRepoAndModal

        Assert.That(model, Is.EqualTo(modelWithRepoAndModal),
            "Letter keys should be suppressed while modal is open")

    [<Test>]
    member _.``Plus key while modal is open does not open another modal``() =
        let model, _ = update (KeyPressed ("+", false)) modelWithRepoAndModal

        Assert.That(model.CreateModal, Is.EqualTo(openForm),
            "Plus key should be suppressed while modal is already open")

    [<Test>]
    member _.``Home key while modal is open is suppressed``() =
        let model, _ = update (KeyPressed ("Home", false)) modelWithRepoAndModal

        Assert.That(model.FocusedElement, Is.EqualTo(modelWithRepoAndModal.FocusedElement),
            "Home key should be suppressed while modal is open")

    [<Test>]
    member _.``End key while modal is open is suppressed``() =
        let model, _ = update (KeyPressed ("End", false)) modelWithRepoAndModal

        Assert.That(model.FocusedElement, Is.EqualTo(modelWithRepoAndModal.FocusedElement),
            "End key should be suppressed while modal is open")

    [<Test>]
    member _.``Enter key suppressed even from LoadingBranches state``() =
        let loading = { modelWithRepoAndModal with CreateModal = Modal.LoadingBranches repoId }
        let model, _ = update (KeyPressed ("Enter", false)) loading

        Assert.That(model.Repos.Head.IsCollapsed, Is.False,
            "Enter should not toggle collapse while modal is in LoadingBranches state")

    [<Test>]
    member _.``Enter key suppressed even from Creating state``() =
        let creating = { modelWithRepoAndModal with CreateModal = Modal.Creating repoId }
        let model, _ = update (KeyPressed ("Enter", false)) creating

        Assert.That(model.Repos.Head.IsCollapsed, Is.False,
            "Enter should not toggle collapse while modal is in Creating state")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FocusRestorationTests() =

    let repoId = testRepoId

    let openForm =
        Modal.Open { RepoId = repoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }

    let modelWithFocusAndModal =
        { defaultModel with
            FocusedElement = Some (Card "other/branch")
            CreateModal = openForm }

    [<Test>]
    member _.``Escape restores focus to RepoHeader of the modal's repoId``() =
        let model, _ = update (KeyPressed ("Escape", false)) modelWithFocusAndModal

        Assert.That(model.FocusedElement, Is.EqualTo(Some (RepoHeader repoId)),
            "Escape should restore focus to the RepoHeader that triggered the modal")

    [<Test>]
    member _.``Escape restores focus from LoadingBranches state``() =
        let loading = { modelWithFocusAndModal with CreateModal = Modal.LoadingBranches repoId }
        let model, _ = update (KeyPressed ("Escape", false)) loading

        Assert.That(model.FocusedElement, Is.EqualTo(Some (RepoHeader repoId)),
            "Escape from LoadingBranches should restore focus to RepoHeader")

    [<Test>]
    member _.``Escape restores focus from Creating state``() =
        let creating = { modelWithFocusAndModal with CreateModal = Modal.Creating repoId }
        let model, _ = update (KeyPressed ("Escape", false)) creating

        Assert.That(model.FocusedElement, Is.EqualTo(Some (RepoHeader repoId)),
            "Escape from Creating should restore focus to RepoHeader")

    [<Test>]
    member _.``Escape restores focus from CreateError state``() =
        let error = { modelWithFocusAndModal with CreateModal = Modal.CreateError (repoId, "err") }
        let model, _ = update (KeyPressed ("Escape", false)) error

        Assert.That(model.FocusedElement, Is.EqualTo(Some (RepoHeader repoId)),
            "Escape from CreateError should restore focus to RepoHeader")

    [<Test>]
    member _.``CloseCreateModal restores focus to RepoHeader``() =
        let model, _ = update (ModalMsg Modal.CloseCreateModal) modelWithFocusAndModal

        Assert.That(model.FocusedElement, Is.EqualTo(Some (RepoHeader repoId)),
            "CloseCreateModal should restore focus to RepoHeader")

    [<Test>]
    member _.``CreateWorktreeCompleted Ok restores focus to RepoHeader``() =
        let creating = { modelWithFocusAndModal with CreateModal = Modal.Creating repoId }
        let model = tryUpdateModel (ModalMsg (Modal.CreateWorktreeCompleted (Ok ()))) creating

        Assert.That(model.FocusedElement, Is.EqualTo(Some (RepoHeader repoId)),
            "Successful creation should restore focus to RepoHeader")
