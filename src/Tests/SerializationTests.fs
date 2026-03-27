module Tests.SerializationTests

open NUnit.Framework
open Newtonsoft.Json
open Shared

let private converter = Fable.Remoting.Json.FableJsonConverter()

let private roundTrip<'T> (value: 'T) : 'T =
    let json = JsonConvert.SerializeObject(value, converter)
    JsonConvert.DeserializeObject<'T>(json, converter)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WrapperTypeSerializationTests() =

    [<Test>]
    member _.``WorktreePath survives JSON round-trip``() =
        let original = WorktreePath @"Q:\code\my-project"
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))

    [<Test>]
    member _.``BranchName survives JSON round-trip``() =
        let original = BranchName.create "feature/my-branch"
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))

    [<Test>]
    member _.``RepoId survives JSON round-trip``() =
        let original = RepoId "my-repo"
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))

    [<Test>]
    member _.``LaunchRequest with WorktreePath survives JSON round-trip``() =
        let original =
            { Path = WorktreePath @"Q:\code\test"
              Prompt = "run tests" }

        let result = roundTrip original
        Assert.That(result.Path, Is.EqualTo(original.Path))
        Assert.That(result.Prompt, Is.EqualTo(original.Prompt))

    [<Test>]
    member _.``ActionRequest with FixPr survives JSON round-trip``() =
        let original: ActionRequest =
            { Path = WorktreePath @"Q:\code\test"
              Action = FixPr "https://dev.azure.com/org/proj/_git/repo/pullrequest/42" }

        let result = roundTrip original
        Assert.That(result.Path, Is.EqualTo(original.Path))
        Assert.That(result.Action, Is.EqualTo(original.Action))

    [<Test>]
    member _.``ActionRequest with FixBuild survives JSON round-trip``() =
        let original: ActionRequest =
            { Path = WorktreePath @"Q:\code\test"
              Action = FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123" }

        let result = roundTrip original
        Assert.That(result.Action, Is.EqualTo(original.Action))

    [<Test>]
    member _.``ActionRequest with CreatePr survives JSON round-trip``() =
        let original: ActionRequest =
            { Path = WorktreePath @"Q:\code\test"
              Action = CreatePr }

        let result = roundTrip original
        Assert.That(result.Action, Is.EqualTo(original.Action))
        let original =
            { RepoId = "my-repo"
              BranchName = BranchName.create "feature/new"
              BaseBranch = BranchName.create "main" }

        let result = roundTrip original
        Assert.That(result.BranchName, Is.EqualTo(original.BranchName))
        Assert.That(result.BaseBranch, Is.EqualTo(original.BaseBranch))
