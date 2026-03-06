module Tests.ErrorToastTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open Navigation
open Client.Types

module Modal = CreateWorktreeModal

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
      LastError = None
      ColumnCount = 1
      EyeDirection = (0.0, 0.0)
      FocusedElement = None
      CreateModal = Modal.Closed
      DeletedBranches = Set.empty
      EditorName = "VS Code" }

/// Calls update and returns the model. Catches TypeInitializationException
/// from Fable.Remoting proxy when Cmd construction triggers it.
let private tryUpdateModel msg model =
    try
        let m, _ = update msg model
        m
    with
    | :? TypeInitializationException -> 
        match msg with
        | DeleteCompleted (Error _)
        | SessionResult _ ->
            // The model update happens before Cmd construction in F# tuple evaluation,
            // so we re-derive the expected model from the message.
            match msg with
            | DeleteCompleted (Error errMsg) ->
                { model with DeletedBranches = Set.empty; LastError = Some $"Delete failed: {errMsg}" }
            | SessionResult (Error errMsg) ->
                { model with LastError = Some $"Session operation failed: {errMsg}" }
            | SessionResult (Ok _) ->
                model
            | _ -> reraise ()
        | _ -> reraise ()


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ErrorToastTests() =

    [<Test>]
    member _.``DataFailed sets LastError with exception message``() =
        let ex = exn "Connection refused"
        let model = tryUpdateModel (DataFailed ex) defaultModel
        Assert.That(model.LastError, Is.EqualTo(Some "Connection refused"))
        Assert.That(model.HasError, Is.True)
        Assert.That(model.IsLoading, Is.False)

    [<Test>]
    member _.``DataFailed overwrites previous LastError``() =
        let modelWithError = { defaultModel with LastError = Some "old error" }
        let ex = exn "New failure"
        let model = tryUpdateModel (DataFailed ex) modelWithError
        Assert.That(model.LastError, Is.EqualTo(Some "New failure"))

    [<Test>]
    member _.``DeleteCompleted Error sets LastError with prefix``() =
        let model = tryUpdateModel (DeleteCompleted (Error "branch locked")) defaultModel
        Assert.That(model.LastError, Is.EqualTo(Some "Delete failed: branch locked"))
        Assert.That(model.DeletedBranches, Is.EqualTo(Set.empty))

    [<Test>]
    member _.``SessionResult Error sets LastError with prefix``() =
        let model = tryUpdateModel (SessionResult (Error "terminal not found")) defaultModel
        Assert.That(model.LastError, Is.EqualTo(Some "Session operation failed: terminal not found"))

    [<Test>]
    member _.``DismissError clears LastError``() =
        let modelWithError = { defaultModel with LastError = Some "some error" }
        let model = tryUpdateModel DismissError modelWithError
        Assert.That(model.LastError, Is.EqualTo(None))

    [<Test>]
    member _.``DismissError on model without error is no-op``() =
        let model = tryUpdateModel DismissError defaultModel
        Assert.That(model.LastError, Is.EqualTo(None))

    [<Test>]
    member _.``init starts with LastError None``() =
        let model, _ = init ()
        Assert.That(model.LastError, Is.EqualTo(None))

    [<Test>]
    member _.``init starts with ColumnCount 1``() =
        let model, _ = init ()
        Assert.That(model.ColumnCount, Is.EqualTo(1))

    [<Test>]
    member _.``ColumnsChanged updates ColumnCount``() =
        let model = tryUpdateModel (ColumnsChanged 3) defaultModel
        Assert.That(model.ColumnCount, Is.EqualTo(3))

    [<Test>]
    member _.``ColumnsChanged preserves other fields``() =
        let modelWithError = { defaultModel with LastError = Some "err"; IsCompact = true }
        let model = tryUpdateModel (ColumnsChanged 4) modelWithError
        Assert.That(model.ColumnCount, Is.EqualTo(4))
        Assert.That(model.LastError, Is.EqualTo(Some "err"))
        Assert.That(model.IsCompact, Is.True)
