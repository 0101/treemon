module Tests.SerializationTests

open System
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

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasDocKindSerializationTests() =

    let makeDoc kind : CanvasDoc =
        { Filename = "doc.html"
          ContentHash = "hash-001"
          LastModified = DateTimeOffset(2026, 2, 22, 13, 0, 0, TimeSpan.Zero)
          OwnerSessionId = Some "session-1"
          Kind = kind }

    [<Test>]
    member _.``CanvasDoc with AgentDoc survives JSON round-trip``() =
        let original = makeDoc AgentDoc
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))
        Assert.That(result.Kind, Is.EqualTo(AgentDoc))

    [<Test>]
    member _.``CanvasDoc with SystemView survives JSON round-trip``() =
        let original = makeDoc SystemView
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))
        Assert.That(result.Kind, Is.EqualTo(SystemView))

    // The demo/test fixture (src/Tests/fixtures/worktrees.json) is hand-written and deserialized by
    // Newtonsoft, where nullary DU cases appear as bare JSON strings (cf. "CodingTool": "Working").
    // These guard that exact shape so the fixture and the Fable.Remoting client agree on the wire format.
    [<Test>]
    member _.``CanvasDocKind serializes as a bare JSON string``() =
        let json = JsonConvert.SerializeObject(SystemView, converter)
        Assert.That(json, Is.EqualTo("\"SystemView\""))

    [<Test>]
    member _.``CanvasDoc Kind deserializes from a bare JSON string (fixture shape)``() =
        let json =
            """{"Filename":"beads.html","ContentHash":"h","LastModified":"2026-02-22T13:00:00+00:00","OwnerSessionId":null,"Kind":"SystemView"}"""
        let result = JsonConvert.DeserializeObject<CanvasDoc>(json, converter)
        Assert.That(result.Kind, Is.EqualTo(SystemView))
        Assert.That(result.Filename, Is.EqualTo("beads.html"))

    // End-to-end guard: the real fixture file must parse through the production load path with both
    // kinds present, so downstream tasks have a SystemView (beads.html) doc to assert against.
    [<Test>]
    member _.``Real fixture file parses with both AgentDoc and SystemView docs``() =
        let fixturePath =
            System.IO.Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "worktrees.json")
        match Server.WorktreeApi.loadFixtures fixturePath with
        | Error msg -> Assert.Fail($"Fixture failed to load: {msg}")
        | Ok data ->
            let allDocs =
                data.Worktrees.Repos
                |> List.collect (fun r -> r.Worktrees)
                |> List.collect (fun wt -> wt.CanvasDocs)
            Assert.That(allDocs |> List.exists (fun d -> d.Kind = SystemView && d.Filename = "beads.html"),
                        Is.True, "Fixture should contain a beads.html SystemView doc")
            Assert.That(allDocs |> List.exists (fun d -> d.Kind = AgentDoc),
                        Is.True, "Fixture should contain at least one AgentDoc")
