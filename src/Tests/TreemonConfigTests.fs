module Tests.TreemonConfigTests

open System
open System.IO
open NUnit.Framework
open Server.TreemonConfig
open Shared

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ReadTests() =

    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-config-test-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        try Directory.Delete(tempDir, true) with _ -> ()

    [<Test>]
    member _.``Returns empty config when no .treemon.json exists``() =
        let config = read tempDir

        Assert.That(config.CodingTool, Is.EqualTo(None))
        Assert.That(config.TestSolution, Is.EqualTo(None))

    [<Test>]
    member _.``Reads both codingTool and testSolution``() =
        File.WriteAllText(Path.Combine(tempDir, "test.sln"), "")
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "claude", "testSolution": "test.sln"}""")

        let config = read tempDir

        Assert.That(config.CodingTool, Is.EqualTo(Some Claude))
        Assert.That(config.TestSolution, Is.EqualTo(Some "test.sln"))

    [<Test>]
    member _.``Returns testSolution for .slnx extension``() =
        File.WriteAllText(Path.Combine(tempDir, "test.slnx"), "")
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"testSolution": "test.slnx"}""")

        let config = read tempDir

        Assert.That(config.TestSolution, Is.EqualTo(Some "test.slnx"))

    [<Test>]
    member _.``Rejects testSolution with wrong extension``() =
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "")
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"testSolution": "test.csproj"}""")

        let config = read tempDir

        Assert.That(config.TestSolution, Is.EqualTo(None))

    [<Test>]
    member _.``Rejects testSolution that does not exist on disk``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"testSolution": "missing.sln"}""")

        let config = read tempDir

        Assert.That(config.TestSolution, Is.EqualTo(None))

    [<Test>]
    member _.``Rejects testSolution with path traversal``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"testSolution": "../escape.sln"}""")

        let config = read tempDir

        Assert.That(config.TestSolution, Is.EqualTo(None))

    [<Test>]
    member _.``Returns testSolution in subdirectory``() =
        let subDir = Path.Combine(tempDir, "sub")
        Directory.CreateDirectory(subDir) |> ignore
        File.WriteAllText(Path.Combine(subDir, "nested.sln"), "")
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"testSolution": "sub/nested.sln"}""")

        let config = read tempDir

        Assert.That(config.TestSolution, Is.EqualTo(Some "sub/nested.sln"))

    [<Test>]
    member _.``Returns empty config for invalid JSON``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), "not json")

        let config = read tempDir

        Assert.That(config.CodingTool, Is.EqualTo(None))
        Assert.That(config.TestSolution, Is.EqualTo(None))

    [<Test>]
    member _.``Returns None testSolution when property is absent``() =
        File.WriteAllText(Path.Combine(tempDir, ".treemon.json"), """{"codingTool": "copilot"}""")

        let config = read tempDir

        Assert.That(config.TestSolution, Is.EqualTo(None))
        Assert.That(config.CodingTool, Is.EqualTo(Some Copilot))
