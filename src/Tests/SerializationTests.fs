module Tests.SerializationTests

open System
open NUnit.Framework
open Newtonsoft.Json
open Shared
open OverviewData

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
              BaseBranch = BranchName.create "main"
              Prompt = None
              Skill = Some "investigate" }

        let result = roundTrip original
        Assert.That(result.BranchName, Is.EqualTo(original.BranchName))
        Assert.That(result.BaseBranch, Is.EqualTo(original.BaseBranch))
        Assert.That(result.Skill, Is.EqualTo(original.Skill))

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
                |> List.collect _.Worktrees
                |> List.collect _.CanvasDocs
            Assert.That(allDocs |> List.exists (fun d -> d.Kind = SystemView && d.Filename = "beads.html"),
                        Is.True, "Fixture should contain a beads.html SystemView doc")
            Assert.That(allDocs |> List.exists (fun d -> d.Kind = AgentDoc),
                        Is.True, "Fixture should contain at least one AgentDoc")

    // Regression guard: the hand-written fixture omits the (non-optional) Planning record, so Newtonsoft
    // leaves it null. loadFixtures must default it to BeadsPlanning.zero — a null Planning is
    // un-deserializable by the Fable.Remoting client and silently breaks the dashboard's first load
    // (every card falls back to a skeleton, which manifests as E2E timeouts waiting for .branch-name).
    [<Test>]
    member _.``loadFixtures defaults null Planning to zero on every worktree``() =
        let fixturePath =
            System.IO.Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "worktrees.json")
        match Server.WorktreeApi.loadFixtures fixturePath with
        | Error msg -> Assert.Fail($"Fixture failed to load: {msg}")
        | Ok data ->
            let worktrees = data.Worktrees.Repos |> List.collect _.Worktrees
            Assert.That(worktrees, Is.Not.Empty, "fixture should contain worktrees")
            worktrees
            |> List.iter (fun wt ->
                Assert.That(obj.ReferenceEquals(wt.Planning, null), Is.False,
                            $"worktree '{wt.Branch}' must have a non-null Planning after load"))

// The count-only snapshots, explicit requested windows, and anchored response all cross the
// Fable.Remoting boundary and must survive its converter unchanged.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewSnapshotSerializationTests() =

    let sample: OverviewSnapshot =
        { Timestamp = DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero)
          Tasks =
            [ { Kind = TaskBucketKind.Planned; Count = 3 }
              { Kind = TaskBucketKind.InProgress; Count = 2 }
              { Kind = TaskBucketKind.Unattended; Count = 1 } ]
          Agents =
            [ { Kind = AgentGroupKind.Activity CurrentActivity.Executing; Count = 4 }
              { Kind = AgentGroupKind.Waiting; Count = 1 } ] }

    [<Test>]
    member _.``OverviewSnapshot survives JSON round-trip``() =
        let result = roundTrip sample
        Assert.That(result, Is.EqualTo(sample))

    [<Test>]
    member _.``OverviewSnapshot list survives JSON round-trip``() =
        let original = [ sample; { sample with Tasks = []; Agents = [] } ]
        let result = roundTrip original
        Assert.That(result, Is.EqualTo(original))

    [<Test>]
    member _.``all Overview history windows survive JSON round-trip``() =
        let original =
            [ HistoryWindow.Hours12
              HistoryWindow.Hours24
              HistoryWindow.Hours72 ]

        Assert.That(roundTrip original, Is.EqualTo(original))

    [<Test>]
    member _.``anchored Overview history response survives JSON round-trip``() =
        let original =
            { Anchor = DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero)
              Snapshots = [ sample ] }

        Assert.That(roundTrip original, Is.EqualTo(original))
