module Tests.ErrorToastTests

open NUnit.Framework
open App
open Tests.TestUtils


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ErrorToastTests() =

    [<Test>]
    member _.``DataFailed sets LastError``() =
        let model, _ = update (DataFailed (exn "connection refused")) defaultModel

        Assert.That(model.LastError, Is.EqualTo(Some "connection refused"))

    [<Test>]
    member _.``ActionFailed sets LastError without changing IsLoading``() =
        let loadingModel = { defaultModel with IsLoading = true }
        let model, _ = update (ActionFailed (exn "terminal not found")) loadingModel

        Assert.That(model.LastError, Is.EqualTo(Some "terminal not found"))
        Assert.That(model.IsLoading, Is.True)

    [<Test>]
    member _.``DeleteCompleted Error sets LastError with prefix``() =
        let model = tryUpdateModel (DeleteCompleted (Error "branch not found")) defaultModel

        Assert.That(model.LastError, Is.EqualTo(Some "Delete failed: branch not found"))

    [<Test>]
    member _.``SessionResult Error sets LastError with prefix``() =
        let model = tryUpdateModel (SessionResult (Error "terminal crashed")) defaultModel

        Assert.That(model.LastError, Is.EqualTo(Some "Session failed: terminal crashed"))

    [<Test>]
    member _.``LaunchActionResult Error sets LastError with prefix``() =
        let model = tryUpdateModel (LaunchActionResult (Error "timeout")) defaultModel

        Assert.That(model.LastError, Is.EqualTo(Some "Launch failed: timeout"))

    [<Test>]
    member _.``DismissError clears LastError``() =
        let modelWithError = { defaultModel with LastError = Some "old error" }
        let model, _ = update DismissError modelWithError

        Assert.That(model.LastError, Is.EqualTo(None))

    [<Test>]
    member _.``ToggleCompact preserves LastError``() =
        let modelWithError = { defaultModel with LastError = Some "existing error" }
        let model, _ = update ToggleCompact modelWithError

        Assert.That(model.LastError, Is.EqualTo(Some "existing error"))
        Assert.That(model.IsCompact, Is.True)
