module Tests.ErrorToastTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open Client.Types
open Tests.TestUtils.ElmishTestHelpers

module Modal = CreateWorktreeModal

let private tryUpdate =
    tryUpdateModel (fun ex msg model ->
        match msg with
        | DeleteCompleted (Error errMsg) ->
            { model with DeletedBranches = Set.empty; LastError = Some $"Delete failed: {errMsg}" }
        | SessionResult (Error errMsg) ->
            { model with LastError = Some $"Session operation failed: {errMsg}" }
        | SessionResult (Ok _) ->
            model
        | _ -> raise ex)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ErrorToastTests() =

    [<Test>]
    member _.``DataFailed sets LastError with exception message``() =
        let ex = exn "Connection refused"
        let model = tryUpdate (DataFailed ex) defaultModel
        Assert.That(model.LastError, Is.EqualTo(Some "Connection refused"))
        Assert.That(model.HasError, Is.True)
        Assert.That(model.IsLoading, Is.False)

    [<Test>]
    member _.``DataFailed overwrites previous LastError``() =
        let modelWithError = { defaultModel with LastError = Some "old error" }
        let ex = exn "New failure"
        let model = tryUpdate (DataFailed ex) modelWithError
        Assert.That(model.LastError, Is.EqualTo(Some "New failure"))

    [<Test>]
    member _.``DeleteCompleted Error sets LastError with prefix``() =
        let model = tryUpdate (DeleteCompleted (Error "branch locked")) defaultModel
        Assert.That(model.LastError, Is.EqualTo(Some "Delete failed: branch locked"))
        Assert.That(model.DeletedBranches, Is.EqualTo(Set.empty))

    [<Test>]
    member _.``SessionResult Error sets LastError with prefix``() =
        let model = tryUpdate (SessionResult (Error "terminal not found")) defaultModel
        Assert.That(model.LastError, Is.EqualTo(Some "Session operation failed: terminal not found"))

    [<Test>]
    member _.``DismissError clears LastError``() =
        let modelWithError = { defaultModel with LastError = Some "some error" }
        let model = tryUpdate DismissError modelWithError
        Assert.That(model.LastError, Is.EqualTo(None))

    [<Test>]
    member _.``DismissError on model without error is no-op``() =
        let model = tryUpdate DismissError defaultModel
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
        let model = tryUpdate (ColumnsChanged 3) defaultModel
        Assert.That(model.ColumnCount, Is.EqualTo(3))

    [<Test>]
    member _.``ColumnsChanged preserves other fields``() =
        let modelWithError = { defaultModel with LastError = Some "err"; IsCompact = true }
        let model = tryUpdate (ColumnsChanged 4) modelWithError
        Assert.That(model.ColumnCount, Is.EqualTo(4))
        Assert.That(model.LastError, Is.EqualTo(Some "err"))
        Assert.That(model.IsCompact, Is.True)

    [<Test>]
    member _.``ServerInfoLoaded sets EditorName and DeployBranch``() =
        let info : ServerInfo = { EditorName = "Cursor"; DeployBranch = Some "release/v2" }
        let model = tryUpdate (ServerInfoLoaded info) defaultModel
        Assert.That(model.EditorName, Is.EqualTo("Cursor"))
        Assert.That(model.DeployBranch, Is.EqualTo(Some "release/v2"))

    [<Test>]
    member _.``ServerInfoLoaded with None DeployBranch clears it``() =
        let modelWithBranch = { defaultModel with DeployBranch = Some "main" }
        let info : ServerInfo = { EditorName = "VS Code"; DeployBranch = None }
        let model = tryUpdate (ServerInfoLoaded info) modelWithBranch
        Assert.That(model.DeployBranch, Is.EqualTo(None))
        Assert.That(model.EditorName, Is.EqualTo("VS Code"))

    [<Test>]
    member _.``ServerInfoLoaded preserves other model fields``() =
        let modelWithState = { defaultModel with LastError = Some "err"; IsCompact = true; ColumnCount = 3 }
        let info : ServerInfo = { EditorName = "Neovim"; DeployBranch = Some "deploy" }
        let model = tryUpdate (ServerInfoLoaded info) modelWithState
        Assert.That(model.EditorName, Is.EqualTo("Neovim"))
        Assert.That(model.DeployBranch, Is.EqualTo(Some "deploy"))
        Assert.That(model.LastError, Is.EqualTo(Some "err"))
        Assert.That(model.IsCompact, Is.True)
        Assert.That(model.ColumnCount, Is.EqualTo(3))
