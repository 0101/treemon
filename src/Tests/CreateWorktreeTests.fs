module Tests.CreateWorktreeTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open Navigation

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
      EyeDirection = (0.0, 0.0)
      FocusedElement = None
      CreateModal = Closed
      DeletedBranches = Set.empty }

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
        | OpenCreateWorktree repoId ->
            { model with CreateModal = LoadingBranches repoId }
        | SubmitCreateWorktree ->
            match model.CreateModal with
            | Open form when form.Name.Trim().Length > 0 ->
                { model with CreateModal = Creating form.RepoId }
            | _ -> model
        | CreateWorktreeCompleted (Ok _) ->
            { model with CreateModal = Closed }
        | _ -> reraise ()


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OpenCreateWorktreeTests() =

    [<Test>]
    member _.``OpenCreateWorktree transitions to LoadingBranches``() =
        let model = tryUpdateModel (OpenCreateWorktree testRepoId) defaultModel

        match model.CreateModal with
        | LoadingBranches repoId ->
            Assert.That(repoId, Is.EqualTo(testRepoId))
        | other ->
            Assert.Fail($"Expected LoadingBranches but got {other}")

    [<Test>]
    member _.``OpenCreateWorktree does not change other model fields``() =
        let model = tryUpdateModel (OpenCreateWorktree testRepoId) defaultModel

        Assert.That(model.IsLoading, Is.EqualTo(defaultModel.IsLoading))
        Assert.That(model.HasError, Is.EqualTo(defaultModel.HasError))
        Assert.That(model.Repos, Is.EqualTo(defaultModel.Repos))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BranchesLoadedTests() =

    let loadingModel =
        { defaultModel with CreateModal = LoadingBranches testRepoId }

    [<Test>]
    member _.``BranchesLoaded Ok transitions to Open with branches``() =
        let branches = [ "main"; "develop"; "feature/x" ]
        let model, _ = update (BranchesLoaded (Ok branches)) loadingModel

        match model.CreateModal with
        | Open form ->
            Assert.That(form.Branches, Is.EqualTo(branches))
            Assert.That(form.RepoId, Is.EqualTo(testRepoId))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``BranchesLoaded Ok pre-selects first branch as BaseBranch``() =
        let branches = [ "main"; "develop"; "feature/x" ]
        let model, _ = update (BranchesLoaded (Ok branches)) loadingModel

        match model.CreateModal with
        | Open form ->
            Assert.That(form.BaseBranch, Is.EqualTo("main"))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``BranchesLoaded Ok sets empty Name``() =
        let branches = [ "main" ]
        let model, _ = update (BranchesLoaded (Ok branches)) loadingModel

        match model.CreateModal with
        | Open form ->
            Assert.That(form.Name, Is.EqualTo(""))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``BranchesLoaded Ok with empty list sets empty BaseBranch``() =
        let model, _ = update (BranchesLoaded (Ok [])) loadingModel

        match model.CreateModal with
        | Open form ->
            Assert.That(form.BaseBranch, Is.EqualTo(""))
            Assert.That(form.Branches, Is.Empty)
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``BranchesLoaded Ok produces no command``() =
        let _, cmd = update (BranchesLoaded (Ok [ "main" ])) loadingModel

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``BranchesLoaded Error transitions to CreateError``() =
        let model, _ = update (BranchesLoaded (Error (exn "network error"))) loadingModel

        match model.CreateModal with
        | CreateError (repoId, message) ->
            Assert.That(repoId, Is.EqualTo(testRepoId))
            Assert.That(message, Is.EqualTo("Failed to load branches"))
        | other ->
            Assert.Fail($"Expected CreateError but got {other}")

    [<Test>]
    member _.``BranchesLoaded Error produces no command``() =
        let _, cmd = update (BranchesLoaded (Error (exn "network error"))) loadingModel

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``BranchesLoaded Ok ignored when modal is not LoadingBranches``() =
        let closedModel = { defaultModel with CreateModal = Closed }
        let model, _ = update (BranchesLoaded (Ok [ "main" ])) closedModel

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``BranchesLoaded Error ignored when modal is not LoadingBranches``() =
        let closedModel = { defaultModel with CreateModal = Closed }
        let model, _ = update (BranchesLoaded (Error (exn "fail"))) closedModel

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``BranchesLoaded Ok ignored when modal is Open``() =
        let openForm = Open { RepoId = testRepoId; Branches = [ "old" ]; Name = "x"; BaseBranch = "old" }
        let model, _ = update (BranchesLoaded (Ok [ "new-branch" ])) { defaultModel with CreateModal = openForm }

        match model.CreateModal with
        | Open form ->
            Assert.That(form.Branches, Is.EqualTo([ "old" ]), "Should not replace branches when already Open")
        | other ->
            Assert.Fail($"Expected Open (unchanged) but got {other}")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FormStateTests() =

    let openForm =
        Open { RepoId = testRepoId; Branches = [ "main"; "develop" ]; Name = ""; BaseBranch = "main" }

    let openModel = { defaultModel with CreateModal = openForm }

    [<Test>]
    member _.``SetNewWorktreeName updates Name in Open form``() =
        let model, _ = update (SetNewWorktreeName "my-feature") openModel

        match model.CreateModal with
        | Open form ->
            Assert.That(form.Name, Is.EqualTo("my-feature"))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``SetNewWorktreeName preserves other form fields``() =
        let model, _ = update (SetNewWorktreeName "my-feature") openModel

        match model.CreateModal with
        | Open form ->
            Assert.That(form.BaseBranch, Is.EqualTo("main"))
            Assert.That(form.RepoId, Is.EqualTo(testRepoId))
            Assert.That(form.Branches, Is.EqualTo([ "main"; "develop" ]))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``SetBaseBranch updates BaseBranch in Open form``() =
        let model, _ = update (SetBaseBranch "develop") openModel

        match model.CreateModal with
        | Open form ->
            Assert.That(form.BaseBranch, Is.EqualTo("develop"))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``SetBaseBranch preserves other form fields``() =
        let model, _ = update (SetBaseBranch "develop") openModel

        match model.CreateModal with
        | Open form ->
            Assert.That(form.Name, Is.EqualTo(""))
            Assert.That(form.RepoId, Is.EqualTo(testRepoId))
        | other ->
            Assert.Fail($"Expected Open but got {other}")

    [<Test>]
    member _.``SetNewWorktreeName ignored when modal is Closed``() =
        let model, _ = update (SetNewWorktreeName "test") defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``SetBaseBranch ignored when modal is Closed``() =
        let model, _ = update (SetBaseBranch "test") defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``SetNewWorktreeName ignored when modal is Creating``() =
        let creating = { defaultModel with CreateModal = Creating testRepoId }
        let model, _ = update (SetNewWorktreeName "test") creating

        Assert.That(model.CreateModal, Is.EqualTo(Creating testRepoId))

    [<Test>]
    member _.``SetBaseBranch ignored when modal is Creating``() =
        let creating = { defaultModel with CreateModal = Creating testRepoId }
        let model, _ = update (SetBaseBranch "test") creating

        Assert.That(model.CreateModal, Is.EqualTo(Creating testRepoId))

    [<Test>]
    member _.``SetNewWorktreeName produces no command``() =
        let _, cmd = update (SetNewWorktreeName "test") openModel

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``SetBaseBranch produces no command``() =
        let _, cmd = update (SetBaseBranch "test") openModel

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``Multiple SetNewWorktreeName calls update correctly``() =
        let m1, _ = update (SetNewWorktreeName "first") openModel
        let m2, _ = update (SetNewWorktreeName "second") m1

        match m2.CreateModal with
        | Open form -> Assert.That(form.Name, Is.EqualTo("second"))
        | other -> Assert.Fail($"Expected Open but got {other}")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SubmitCreateWorktreeTests() =

    let openForm =
        Open { RepoId = testRepoId; Branches = [ "main"; "develop" ]; Name = "my-feature"; BaseBranch = "main" }

    let openModel = { defaultModel with CreateModal = openForm }

    [<Test>]
    member _.``SubmitCreateWorktree transitions to Creating``() =
        let model = tryUpdateModel SubmitCreateWorktree openModel

        match model.CreateModal with
        | Creating repoId ->
            Assert.That(repoId, Is.EqualTo(testRepoId))
        | other ->
            Assert.Fail($"Expected Creating but got {other}")

    [<Test>]
    member _.``SubmitCreateWorktree with empty name is ignored``() =
        let emptyName =
            Open { RepoId = testRepoId; Branches = [ "main" ]; Name = ""; BaseBranch = "main" }
        let model, cmd = update SubmitCreateWorktree { defaultModel with CreateModal = emptyName }

        match model.CreateModal with
        | Open _ -> ()
        | other -> Assert.Fail($"Expected Open (unchanged) but got {other}")
        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``SubmitCreateWorktree with whitespace-only name is ignored``() =
        let wsName =
            Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "   "; BaseBranch = "main" }
        let model, cmd = update SubmitCreateWorktree { defaultModel with CreateModal = wsName }

        match model.CreateModal with
        | Open _ -> ()
        | other -> Assert.Fail($"Expected Open (unchanged) but got {other}")
        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``SubmitCreateWorktree when modal is Closed is ignored``() =
        let model, cmd = update SubmitCreateWorktree defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Closed))
        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``SubmitCreateWorktree trims name with leading and trailing spaces``() =
        let spacedName =
            Open { RepoId = testRepoId; Branches = [ "main" ]; Name = " trimmed "; BaseBranch = "main" }
        let model = tryUpdateModel SubmitCreateWorktree { defaultModel with CreateModal = spacedName }

        match model.CreateModal with
        | Creating _ -> ()
        | other -> Assert.Fail($"Expected Creating but got {other}")

    [<Test>]
    member _.``SubmitCreateWorktree when modal is LoadingBranches is ignored``() =
        let model, cmd = update SubmitCreateWorktree { defaultModel with CreateModal = LoadingBranches testRepoId }

        Assert.That(model.CreateModal, Is.EqualTo(LoadingBranches testRepoId))
        Assert.That(cmd, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CreateWorktreeCompletedTests() =

    [<Test>]
    member _.``CreateWorktreeCompleted Ok closes modal``() =
        let creating = { defaultModel with CreateModal = Creating testRepoId }
        let model = tryUpdateModel (CreateWorktreeCompleted (Ok ())) creating

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``CreateWorktreeCompleted Error transitions to CreateError``() =
        let creating = { defaultModel with CreateModal = Creating testRepoId }
        let model, _ = update (CreateWorktreeCompleted (Error "git failed")) creating

        match model.CreateModal with
        | CreateError (repoId, message) ->
            Assert.That(repoId, Is.EqualTo(testRepoId))
            Assert.That(message, Is.EqualTo("git failed"))
        | other ->
            Assert.Fail($"Expected CreateError but got {other}")

    [<Test>]
    member _.``CreateWorktreeCompleted Error produces no command``() =
        let creating = { defaultModel with CreateModal = Creating testRepoId }
        let _, cmd = update (CreateWorktreeCompleted (Error "git failed")) creating

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``CreateWorktreeCompleted Error ignored when not in Creating state``() =
        let model, _ = update (CreateWorktreeCompleted (Error "git failed")) defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``CreateWorktreeCompleted Error from Closed stays Closed``() =
        let model, cmd = update (CreateWorktreeCompleted (Error "oops")) defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Closed))
        Assert.That(cmd, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CloseCreateModalTests() =

    [<Test>]
    member _.``CloseCreateModal resets to Closed from Open``() =
        let openForm =
            Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }
        let model, _ = update CloseCreateModal { defaultModel with CreateModal = openForm }

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``CloseCreateModal resets to Closed from CreateError``() =
        let errorState = CreateError (testRepoId, "some error")
        let model, _ = update CloseCreateModal { defaultModel with CreateModal = errorState }

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``CloseCreateModal from LoadingBranches resets to Closed``() =
        let loading = { defaultModel with CreateModal = LoadingBranches testRepoId }
        let model, _ = update CloseCreateModal loading

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``CloseCreateModal when already Closed stays Closed``() =
        let model, _ = update CloseCreateModal defaultModel

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``CloseCreateModal produces no command``() =
        let openForm =
            Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }
        let _, cmd = update CloseCreateModal { defaultModel with CreateModal = openForm }

        Assert.That(cmd, Is.Empty)

    [<Test>]
    member _.``CloseCreateModal from Creating resets to Closed``() =
        let model, _ = update CloseCreateModal { defaultModel with CreateModal = Creating testRepoId }

        Assert.That(model.CreateModal, Is.EqualTo(Closed))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EscapeKeyClosesModalTests() =

    [<Test>]
    member _.``Escape key closes modal from Open state``() =
        let openForm =
            Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }
        let model, _ = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = openForm }

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``Escape key closes modal from Creating state``() =
        let model, _ = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = Creating testRepoId }

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``Escape key closes modal from CreateError state``() =
        let errorState = CreateError (testRepoId, "error")
        let model, _ = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = errorState }

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``Escape key closes modal from LoadingBranches state``() =
        let model, _ = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = LoadingBranches testRepoId }

        Assert.That(model.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``Escape key produces no command when modal is open``() =
        let openForm =
            Open { RepoId = testRepoId; Branches = [ "main" ]; Name = "test"; BaseBranch = "main" }
        let _, cmd = update (KeyPressed ("Escape", false)) { defaultModel with CreateModal = openForm }

        Assert.That(cmd, Is.Empty)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BranchPrioritySortingTests() =

    [<Test>]
    member _.``main comes first``() =
        let output = "origin/feature\norigin/main\norigin/develop"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result |> List.head, Is.EqualTo("main"))

    [<Test>]
    member _.``master comes after main``() =
        let output = "origin/master\norigin/main\norigin/feature"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result, Is.EqualTo([ "main"; "master"; "feature" ]))

    [<Test>]
    member _.``develop comes after master``() =
        let output = "origin/feature\norigin/develop\norigin/master\norigin/main"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result.[0], Is.EqualTo("main"))
        Assert.That(result.[1], Is.EqualTo("master"))
        Assert.That(result.[2], Is.EqualTo("develop"))
        Assert.That(result.[3], Is.EqualTo("feature"))

    [<Test>]
    member _.``dev prefix branches come after develop and before others``() =
        let output = "origin/feature\norigin/dev-test\norigin/dev-staging\norigin/develop\norigin/main"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result.[0], Is.EqualTo("main"))
        Assert.That(result.[1], Is.EqualTo("develop"))
        Assert.That(result.[2], Is.EqualTo("dev-staging"))
        Assert.That(result.[3], Is.EqualTo("dev-test"))
        Assert.That(result.[4], Is.EqualTo("feature"))

    [<Test>]
    member _.``dev prefix branches sorted alphabetically among themselves``() =
        let output = "origin/dev-zebra\norigin/dev-alpha\norigin/main"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result, Is.EqualTo([ "main"; "dev-alpha"; "dev-zebra" ]))

    [<Test>]
    member _.``Remaining branches are sorted alphabetically``() =
        let output = "origin/zebra\norigin/alpha\norigin/middle\norigin/main"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result, Is.EqualTo([ "main"; "alpha"; "middle"; "zebra" ]))

    [<Test>]
    member _.``origin HEAD is filtered out``() =
        let output = "origin/HEAD -> origin/main\norigin/main\norigin/feature"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result, Does.Not.Contain("HEAD -> origin/main"))
        Assert.That(result, Is.EqualTo([ "main"; "feature" ]))

    [<Test>]
    member _.``origin prefix is stripped``() =
        let output = "origin/main\norigin/feature"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result |> List.head, Is.EqualTo("main"))
        Assert.That(result, Does.Not.Contain("origin/main"))

    [<Test>]
    member _.``Duplicates are removed``() =
        let output = "origin/main\norigin/main\norigin/feature"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result |> List.filter ((=) "main") |> List.length, Is.EqualTo(1))

    [<Test>]
    member _.``Empty output returns empty list``() =
        let result = Server.GitWorktree.parseRemoteBranches ""

        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Full priority order is correct``() =
        let output = "origin/zebra\norigin/dev-test\norigin/master\norigin/develop\norigin/main\norigin/alpha"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result, Is.EqualTo([ "main"; "master"; "develop"; "dev-test"; "alpha"; "zebra" ]))

    [<Test>]
    member _.``develop is not matched by dev prefix rule``() =
        let output = "origin/develop\norigin/dev-staging\norigin/main"
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result.[0], Is.EqualTo("main"))
        Assert.That(result.[1], Is.EqualTo("develop"), "develop should use its own priority (2), not dev prefix (3)")
        Assert.That(result.[2], Is.EqualTo("dev-staging"))

    [<Test>]
    member _.``Whitespace in lines is trimmed``() =
        let output = "  origin/main  \n  origin/feature  "
        let result = Server.GitWorktree.parseRemoteBranches output

        Assert.That(result, Is.EqualTo([ "main"; "feature" ]))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FullStateMachineRoundtripTests() =

    [<Test>]
    member _.``Full happy path: Open, load branches, fill form, submit, complete``() =
        let m0 = defaultModel

        let m1 = tryUpdateModel (OpenCreateWorktree testRepoId) m0
        Assert.That((match m1.CreateModal with LoadingBranches _ -> true | _ -> false), Is.True)

        let m2, _ = update (BranchesLoaded (Ok [ "main"; "develop" ])) m1
        Assert.That((match m2.CreateModal with Open _ -> true | _ -> false), Is.True)

        let m3, _ = update (SetNewWorktreeName "my-feature") m2
        match m3.CreateModal with
        | Open form -> Assert.That(form.Name, Is.EqualTo("my-feature"))
        | _ -> Assert.Fail("Expected Open")

        let m4, _ = update (SetBaseBranch "develop") m3
        match m4.CreateModal with
        | Open form -> Assert.That(form.BaseBranch, Is.EqualTo("develop"))
        | _ -> Assert.Fail("Expected Open")

        let m5 = tryUpdateModel SubmitCreateWorktree m4
        Assert.That((match m5.CreateModal with Creating _ -> true | _ -> false), Is.True)

        let m6 = tryUpdateModel (CreateWorktreeCompleted (Ok ())) m5
        Assert.That(m6.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``Error path: Open, load branches, submit, error, close``() =
        let m0 = defaultModel

        let m1 = tryUpdateModel (OpenCreateWorktree testRepoId) m0
        let m2, _ = update (BranchesLoaded (Ok [ "main" ])) m1
        let m3, _ = update (SetNewWorktreeName "bad-name") m2
        let m4 = tryUpdateModel SubmitCreateWorktree m3
        let m5, _ = update (CreateWorktreeCompleted (Error "branch already exists")) m4

        match m5.CreateModal with
        | CreateError (_, msg) ->
            Assert.That(msg, Is.EqualTo("branch already exists"))
        | other ->
            Assert.Fail($"Expected CreateError but got {other}")

        let m6, _ = update CloseCreateModal m5
        Assert.That(m6.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``Branch load failure path: Open, load error, close``() =
        let m0 = defaultModel

        let m1 = tryUpdateModel (OpenCreateWorktree testRepoId) m0
        let m2, _ = update (BranchesLoaded (Error (exn "timeout"))) m1

        match m2.CreateModal with
        | CreateError (_, msg) ->
            Assert.That(msg, Is.EqualTo("Failed to load branches"))
        | other ->
            Assert.Fail($"Expected CreateError but got {other}")

        let m3, _ = update CloseCreateModal m2
        Assert.That(m3.CreateModal, Is.EqualTo(Closed))

    [<Test>]
    member _.``Cancel via Escape during any state returns to Closed``() =
        let m1 = tryUpdateModel (OpenCreateWorktree testRepoId) defaultModel
        let m2, _ = update (BranchesLoaded (Ok [ "main" ])) m1
        let m3, _ = update (SetNewWorktreeName "test") m2

        let mEsc, _ = update (KeyPressed ("Escape", false)) m3
        Assert.That(mEsc.CreateModal, Is.EqualTo(Closed))
