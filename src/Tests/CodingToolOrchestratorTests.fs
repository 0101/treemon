module Tests.CodingToolOrchestratorTests

open System
open System.IO
open NUnit.Framework
open Server.CodingToolStatus
open Shared

let private baseTime = DateTimeOffset(2026, 2, 23, 12, 0, 0, TimeSpan.Zero)

let private makeResult provider status mtime : ProviderResult =
    { Provider = provider; Status = status; Mtime = mtime }


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PickActiveProviderTests() =

    [<Test>]
    member _.``Returns None when all providers are Idle``() =
        let results =
            [ makeResult Claude Idle (Some baseTime)
              makeResult Copilot Idle (Some (baseTime.AddMinutes(5.0))) ]

        let picked = pickActiveProvider results

        Assert.That(picked, Is.EqualTo(None))

    [<Test>]
    member _.``Returns the single non-Idle provider``() =
        let results =
            [ makeResult Claude Working (Some baseTime)
              makeResult Copilot Idle None ]

        let picked = pickActiveProvider results

        Assert.That(picked.IsSome, Is.True)
        Assert.That(picked.Value.Provider, Is.EqualTo(Claude))

    [<Test>]
    member _.``Prefers provider with more recent mtime when both non-Idle``() =
        let results =
            [ makeResult Claude Working (Some baseTime)
              makeResult Copilot Working (Some (baseTime.AddMinutes(10.0))) ]

        let picked = pickActiveProvider results

        Assert.That(picked.IsSome, Is.True)
        Assert.That(picked.Value.Provider, Is.EqualTo(Copilot))

    [<Test>]
    member _.``Prefers provider with mtime over provider with None mtime``() =
        let results =
            [ makeResult Claude Done None
              makeResult Copilot Done (Some baseTime) ]

        let picked = pickActiveProvider results

        Assert.That(picked.IsSome, Is.True)
        Assert.That(picked.Value.Provider, Is.EqualTo(Copilot))

    [<Test>]
    member _.``Returns None for empty results list``() =
        let picked = pickActiveProvider []

        Assert.That(picked, Is.EqualTo(None))

    [<Test>]
    member _.``Prefers WaitingForUser over Idle regardless of mtime``() =
        let results =
            [ makeResult Claude Idle (Some (baseTime.AddHours(1.0)))
              makeResult Copilot WaitingForUser (Some baseTime) ]

        let picked = pickActiveProvider results

        Assert.That(picked.IsSome, Is.True)
        Assert.That(picked.Value.Provider, Is.EqualTo(Copilot))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ResolveStatusTests() =

    [<Test>]
    member _.``No config and no active provider returns Idle with no provider``() =
        let results =
            [ makeResult Claude Idle None
              makeResult Copilot Idle None ]

        let status, provider = resolveStatus None results

        Assert.That(status, Is.EqualTo(Idle))
        Assert.That(provider, Is.EqualTo(None))

    [<Test>]
    member _.``No config picks most recently active non-Idle provider``() =
        let results =
            [ makeResult Claude Done (Some baseTime)
              makeResult Copilot Working (Some (baseTime.AddMinutes(5.0))) ]

        let status, provider = resolveStatus None results

        Assert.That(status, Is.EqualTo(Working))
        Assert.That(provider, Is.EqualTo(Some Copilot))

    [<Test>]
    member _.``Config override forces Claude even when Copilot is more recent``() =
        let results =
            [ makeResult Claude Done (Some baseTime)
              makeResult Copilot Working (Some (baseTime.AddMinutes(5.0))) ]

        let status, provider = resolveStatus (Some Claude) results

        Assert.That(status, Is.EqualTo(Done))
        Assert.That(provider, Is.EqualTo(Some Claude))

    [<Test>]
    member _.``Config override forces Copilot even when Claude is active``() =
        let results =
            [ makeResult Claude Working (Some (baseTime.AddMinutes(10.0)))
              makeResult Copilot Idle None ]

        let status, provider = resolveStatus (Some Copilot) results

        Assert.That(status, Is.EqualTo(Idle))
        Assert.That(provider, Is.EqualTo(Some Copilot))

    [<Test>]
    member _.``Config override returns Idle when forced provider has no result``() =
        let results =
            [ makeResult Claude Working (Some baseTime) ]

        let status, provider = resolveStatus (Some Copilot) results

        Assert.That(status, Is.EqualTo(Idle))
        Assert.That(provider, Is.EqualTo(Some Copilot))

    [<Test>]
    member _.``Auto-detect returns Claude when only Claude is active``() =
        let results =
            [ makeResult Claude Working (Some baseTime)
              makeResult Copilot Idle None ]

        let status, provider = resolveStatus None results

        Assert.That(status, Is.EqualTo(Working))
        Assert.That(provider, Is.EqualTo(Some Claude))

    [<Test>]
    member _.``Auto-detect returns Copilot when only Copilot is active``() =
        let results =
            [ makeResult Claude Idle None
              makeResult Copilot WaitingForUser (Some baseTime) ]

        let status, provider = resolveStatus None results

        Assert.That(status, Is.EqualTo(WaitingForUser))
        Assert.That(provider, Is.EqualTo(Some Copilot))

    [<Test>]
    member _.``Empty provider results with no config returns Idle``() =
        let status, provider = resolveStatus None []

        Assert.That(status, Is.EqualTo(Idle))
        Assert.That(provider, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ReadConfiguredProviderTests() =

    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-test-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        try Directory.Delete(tempDir, true) with _ -> ()

    [<Test>]
    member _.``Returns None when no .treemon.json exists``() =
        let result = readConfiguredProvider tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Returns Claude when codingTool is claude``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "claude"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(Some Claude))

    [<Test>]
    member _.``Returns Copilot when codingTool is copilot``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "copilot"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(Some Copilot))

    [<Test>]
    member _.``Returns None for unknown codingTool value``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "cursor"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Returns None when codingTool field is absent``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"testSolution": "tests.sln"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Is case insensitive for codingTool value``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "Copilot"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(Some Copilot))

    [<Test>]
    member _.``Returns None for invalid JSON``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), "not json")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(None))
