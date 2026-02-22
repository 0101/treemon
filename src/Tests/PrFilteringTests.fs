module Tests.PrFilteringTests

open NUnit.Framework
open Server.PrStatus

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PrFilteringTests() =

    [<Test>]
    member _.``firstPerBranch returns empty for empty list``() =
        let result = firstPerBranch []
        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``filterRelevantPrs returns empty when no branches match``() =
        let result = filterRelevantPrs (Set.ofList [ "some-branch" ]) []
        Assert.That(result, Is.Empty)
