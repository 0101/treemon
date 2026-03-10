module Tests.VsCodeCopilotDetectorTests

open System
open System.IO
open NUnit.Framework
open Server.VsCodeCopilotDetector
open Shared

let private fixtureDir =
    Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "fixtures", "vscode")
    |> Path.GetFullPath

let private readFixture (name: string) =
    let path = Path.Combine(fixtureDir, name)
    File.ReadAllText(path).Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.toList


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SnapshotTests() =

    [<Test>]
    member _.``Kind 0 snapshot with complete model state returns last request``() =
        let result = reconstructLastRequest (readFixture "snapshot-complete.jsonl")
        Assert.That(result.IsSome, Is.True)
        let req = result.Value
        Assert.That(req.ModelState, Is.EqualTo(Some Complete))
        Assert.That(req.UserText, Is.EqualTo(Some "Write a hello world program"))
        Assert.That(req.ResponseText.IsSome, Is.True)
        Assert.That(req.ResponseText.Value, Does.Contain("hello world"))
        Assert.That(req.CompletedAt.IsSome, Is.True)

    [<Test>]
    member _.``Kind 0 snapshot with in-progress model state``() =
        let result = reconstructLastRequest (readFixture "snapshot-in-progress.jsonl")
        Assert.That(result.IsSome, Is.True)
        let req = result.Value
        Assert.That(req.ModelState, Is.EqualTo(Some InProgress))
        Assert.That(req.UserText, Is.EqualTo(Some "Refactor the authentication module"))
        Assert.That(req.ResponseText.IsSome, Is.True)

    [<Test>]
    member _.``Kind 0 snapshot with empty requests returns None``() =
        let result = reconstructLastRequest (readFixture "empty-requests.jsonl")
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Second snapshot replaces first``() =
        let result = reconstructLastRequest (readFixture "snapshot-then-new-snapshot.jsonl")
        Assert.That(result.IsSome, Is.True)
        let req = result.Value
        Assert.That(req.UserText, Is.EqualTo(Some "New snapshot request"))
        Assert.That(req.ModelState, Is.EqualTo(Some InProgress))

    [<Test>]
    member _.``Snapshot without user message text has None UserText``() =
        let result = reconstructLastRequest (readFixture "snapshot-no-user-text.jsonl")
        Assert.That(result.IsSome, Is.True)
        let req = result.Value
        Assert.That(req.UserText, Is.EqualTo(None))
        Assert.That(req.ModelState, Is.EqualTo(Some Complete))
        Assert.That(req.ResponseText.IsSome, Is.True)

    [<Test>]
    member _.``Empty lines list returns None``() =
        let result = reconstructLastRequest []
        Assert.That(result, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SetValueTests() =

    [<Test>]
    member _.``Kind 1 updates model state from InProgress to Complete``() =
        let result = reconstructLastRequest (readFixture "set-model-state.jsonl")
        Assert.That(result.IsSome, Is.True)
        let req = result.Value
        Assert.That(req.ModelState, Is.EqualTo(Some Complete))
        Assert.That(req.CompletedAt.IsSome, Is.True)
        Assert.That(req.UserText, Is.EqualTo(Some "Fix the failing tests"))

    [<Test>]
    member _.``Kind 1 set with out-of-range index extends request list``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Only request"},"modelState":{"value":0},"response":[]}]}}"""
              """{"kind":1,"k":["requests","2","modelState"],"v":{"value":4,"completedAt":1710100000000}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.ModelState, Is.EqualTo(Some Complete))

    [<Test>]
    member _.``Kind 1 set with non-numeric index is ignored``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Only request"},"modelState":{"value":0},"response":[]}]}}"""
              """{"kind":1,"k":["requests","abc","modelState"],"v":{"value":4,"completedAt":1710100000000}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.ModelState, Is.EqualTo(Some InProgress))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PushSpliceTests() =

    [<Test>]
    member _.``Kind 2 push to requests appends new request``() =
        let result = reconstructLastRequest (readFixture "push-new-request.jsonl")
        Assert.That(result.IsSome, Is.True)
        let req = result.Value
        Assert.That(req.UserText, Is.EqualTo(Some "Second question"))
        Assert.That(req.ModelState, Is.EqualTo(Some InProgress))

    [<Test>]
    member _.``Kind 2 push to response adds response parts``() =
        let result = reconstructLastRequest (readFixture "push-response-parts.jsonl")
        Assert.That(result.IsSome, Is.True)
        let req = result.Value
        Assert.That(req.ResponseText.IsSome, Is.True)
        Assert.That(req.ResponseText.Value, Does.Contain("monad"))
        Assert.That(req.ResponseKinds, Does.Contain("markdownContent"))

    [<Test>]
    member _.``Multi-request session tracks last request through mutations``() =
        let result = reconstructLastRequest (readFixture "multi-request-session.jsonl")
        Assert.That(result.IsSome, Is.True)
        let req = result.Value
        Assert.That(req.UserText, Is.EqualTo(Some "Third request"))
        Assert.That(req.ModelState, Is.EqualTo(Some Complete))
        Assert.That(req.CompletedAt.IsSome, Is.True)
        Assert.That(req.ResponseText.IsSome, Is.True)
        Assert.That(req.ResponseKinds, Does.Contain("inlineReference"))

    [<Test>]
    member _.``Kind 2 push with non-array value is ignored``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Only"},"modelState":{"value":0},"response":[]}]}}"""
              """{"kind":2,"k":["requests"],"v":"not-an-array"}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.UserText, Is.EqualTo(Some "Only"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ModelStateParsingTests() =

    [<Test>]
    member _.``modelStateFromInt 0 yields InProgress``() =
        Assert.That(modelStateFromInt 0, Is.EqualTo(InProgress))

    [<Test>]
    member _.``modelStateFromInt 1 yields Complete``() =
        Assert.That(modelStateFromInt 1, Is.EqualTo(Complete))

    [<Test>]
    member _.``modelStateFromInt 4 yields Complete``() =
        Assert.That(modelStateFromInt 4, Is.EqualTo(Complete))

    [<Test>]
    member _.``modelStateFromInt negative yields Complete``() =
        Assert.That(modelStateFromInt -1, Is.EqualTo(Complete))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StatusDerivationTests() =

    [<Test>]
    member _.``InProgress model state yields Working status from reconstructed request``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Do something"},"modelState":{"value":0},"response":[]}]}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.ModelState, Is.EqualTo(Some InProgress))

    [<Test>]
    member _.``Complete model state from reconstructed request``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Do something"},"modelState":{"value":4,"completedAt":1710000000000},"response":[{"value":"Done"}]}]}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.ModelState, Is.EqualTo(Some Complete))
        Assert.That(result.Value.ResponseText, Is.EqualTo(Some "Done"))

    [<Test>]
    member _.``No model state at all from snapshot``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Something"},"response":[]}]}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.ModelState, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EdgeCaseTests() =

    [<Test>]
    member _.``Invalid JSON lines are skipped``() =
        let lines =
            [ """not valid json at all"""
              """{"kind":0,"v":{"requests":[{"message":{"text":"Valid request"},"modelState":{"value":4,"completedAt":1710000000000},"response":[{"value":"Valid response"}]}]}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.UserText, Is.EqualTo(Some "Valid request"))

    [<Test>]
    member _.``Kind 3 delete is ignored``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"My request"},"modelState":{"value":0},"response":[]}]}}"""
              """{"kind":3,"k":["requests","0","response"]}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.UserText, Is.EqualTo(Some "My request"))
        Assert.That(result.Value.ModelState, Is.EqualTo(Some InProgress))

    [<Test>]
    member _.``Unknown kind values are ignored``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"My request"},"modelState":{"value":0},"response":[]}]}}"""
              """{"kind":99,"k":["foo"],"v":"bar"}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.UserText, Is.EqualTo(Some "My request"))

    [<Test>]
    member _.``Response text with only whitespace is not captured``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Q"},"modelState":{"value":0},"response":[{"value":"   "}]}]}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.ResponseText, Is.EqualTo(None))

    [<Test>]
    member _.``User message with only whitespace is not captured``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"   "},"modelState":{"value":0},"response":[]}]}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.UserText, Is.EqualTo(None))

    [<Test>]
    member _.``CompletedAt timestamp is correctly parsed``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Q"},"modelState":{"value":4,"completedAt":1710000000000},"response":[{"value":"A"}]}]}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        let ts = result.Value.CompletedAt.Value
        Assert.That(ts, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1710000000000L)))

    [<Test>]
    member _.``Duplicate response kinds are deduplicated``() =
        let lines =
            [ """{"kind":0,"v":{"requests":[{"message":{"text":"Q"},"modelState":{"value":0},"response":[{"kind":"markdownContent"},{"kind":"markdownContent"},{"value":"text"}]}]}}""" ]
        let result = reconstructLastRequest lines
        Assert.That(result.IsSome, Is.True)
        let markdownCount =
            result.Value.ResponseKinds |> List.filter (fun k -> k = "markdownContent") |> List.length
        Assert.That(markdownCount, Is.EqualTo(1))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DetachedHeadKeyTests() =

    let scopedBranchKey (repoId: string) (branch: string) = $"{repoId}/{branch}"

    let buildKey (path: string) (branch: string option) =
        let b = branch |> Option.defaultValue $"(detached@{path})"
        scopedBranchKey "myrepo" b

    [<Test>]
    member _.``Multiple detached worktrees produce distinct keys``() =
        let keys =
            [ buildKey @"Q:\code\project\wt1" None
              buildKey @"Q:\code\project\wt2" None
              buildKey @"Q:\code\project\wt3" None ]

        let distinct = keys |> List.distinct
        Assert.That(distinct.Length, Is.EqualTo(3), "All 3 detached worktrees should have distinct keys")

    [<Test>]
    member _.``Mix of branched and detached worktrees all produce distinct keys``() =
        let keys =
            [ buildKey @"Q:\code\project\main" (Some "main")
              buildKey @"Q:\code\project\wt1" None
              buildKey @"Q:\code\project\feature" (Some "feature/x")
              buildKey @"Q:\code\project\wt2" None ]

        let distinct = keys |> List.distinct
        Assert.That(distinct.Length, Is.EqualTo(4), "All worktrees (branched + detached) should have distinct keys")

    [<Test>]
    member _.``Old pattern with plain detached would collide``() =
        let buildOldKey (path: string) (branch: string option) =
            let b = branch |> Option.defaultValue "(detached)"
            scopedBranchKey "myrepo" b

        let oldKeys =
            [ buildOldKey @"Q:\code\project\wt1" None
              buildOldKey @"Q:\code\project\wt2" None ]
        let oldDistinct = oldKeys |> List.distinct
        Assert.That(oldDistinct.Length, Is.EqualTo(1), "Old pattern should produce collision (both map to same key)")

        let newKeys =
            [ buildKey @"Q:\code\project\wt1" None
              buildKey @"Q:\code\project\wt2" None ]
        let newDistinct = newKeys |> List.distinct
        Assert.That(newDistinct.Length, Is.EqualTo(2), "New pattern should produce distinct keys")
