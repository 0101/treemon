module Tests.CodingToolOrchestratorTests

open System
open System.IO
open NUnit.Framework
open Server.CodingToolStatus
open Shared


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
    member _.``Returns None when codingTool is claude (no longer supported)``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "claude"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Returns Copilot when codingTool is copilot``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "copilot"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(Some CopilotCli))

    [<Test>]
    member _.``Returns None for unknown codingTool value``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "cursor"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Returns None when codingTool field is absent``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"testCommand": "dotnet test tests.sln"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Is case insensitive for codingTool value``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "Copilot"}""")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(Some CopilotCli))

    [<Test>]
    member _.``Returns None for invalid JSON``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), "not json")

        let result = readConfiguredProvider tempDir

        Assert.That(result, Is.EqualTo(None))
