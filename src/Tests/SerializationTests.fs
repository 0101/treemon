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
        let original = WorktreePath.create @"Q:\code\my-project"
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))

    [<Test>]
    member _.``BranchName survives JSON round-trip``() =
        let original = BranchName.create "feature/my-branch"
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))

    [<Test>]
    member _.``RepoId survives JSON round-trip``() =
        let original = RepoId.create "my-repo"
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))

    [<Test>]
    member _.``LaunchRequest with WorktreePath survives JSON round-trip``() =
        let original =
            { Path = WorktreePath.create @"Q:\code\test"
              Prompt = "run tests" }

        let result = roundTrip original
        Assert.That(result.Path, Is.EqualTo(original.Path))
        Assert.That(result.Prompt, Is.EqualTo(original.Prompt))

    [<Test>]
    member _.``CreateWorktreeRequest with BranchName survives JSON round-trip``() =
        let original =
            { RepoId = "my-repo"
              BranchName = BranchName.create "feature/new"
              BaseBranch = BranchName.create "main" }

        let result = roundTrip original
        Assert.That(result.BranchName, Is.EqualTo(original.BranchName))
        Assert.That(result.BaseBranch, Is.EqualTo(original.BaseBranch))
