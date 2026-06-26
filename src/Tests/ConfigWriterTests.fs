module Tests.ConfigWriterTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open NUnit.Framework
open Server.GlobalConfig
open Tests.TestUtils

let private withTempDir (action: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), $"treemon-config-test-{Guid.NewGuid()}")
    Directory.CreateDirectory(tempDir) |> ignore
    try action tempDir
    finally try Directory.Delete(tempDir, recursive = true) with _ -> ()

let private str (value: string) : JsonNode = JsonValue.Create(value) :> JsonNode

let private readStringMap (path: string) : Map<string, string> =
    use doc = JsonDocument.Parse(File.ReadAllText(path))
    doc.RootElement.EnumerateObject()
    |> Seq.choose (fun p ->
        if p.Value.ValueKind = JsonValueKind.String then Some(p.Name, p.Value.GetString())
        else None)
    |> Map.ofSeq

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type UpdateConfigAtPathTests() =

    [<Test>]
    member _.``sequential writes of different keys preserve both``() =
        withTempDir (fun dir ->
            let configPath = Path.Combine(dir, "config.json")

            assertOk (updateConfigAtPath configPath [ "editor", str "vim" ]) "first write"
            assertOk (updateConfigAtPath configPath [ "editorName", str "Neovim" ]) "second write"

            let root = readStringMap configPath
            Assert.That(Map.tryFind "editor" root, Is.EqualTo(Some "vim"),
                "first key must survive the second write")
            Assert.That(Map.tryFind "editorName" root, Is.EqualTo(Some "Neovim")))

    [<Test>]
    member _.``unparseable file is backed up, not destroyed``() =
        withTempDir (fun dir ->
            let configPath = Path.Combine(dir, "config.json")
            let corruptContent = "{ this is not valid json "
            File.WriteAllText(configPath, corruptContent)

            assertOk (updateConfigAtPath configPath [ "editor", str "vim" ]) "write over corrupt file"

            let backups = Directory.GetFiles(dir, "config.json.corrupt-*")
            Assert.That(backups.Length, Is.EqualTo(1), "exactly one timestamped backup of the corrupt file")
            Assert.That(File.ReadAllText(backups[0]), Is.EqualTo(corruptContent),
                "backup must contain the original bytes, nothing lost")

            let root = readStringMap configPath
            Assert.That(Map.tryFind "editor" root, Is.EqualTo(Some "vim"),
                "after recovery the file is valid and holds the new key"))

    [<Test>]
    member _.``successful write leaves no temp file behind``() =
        withTempDir (fun dir ->
            let configPath = Path.Combine(dir, "config.json")
            assertOk (updateConfigAtPath configPath [ "editor", str "vim" ]) "write"
            Assert.That(File.Exists(configPath + ".tmp"), Is.False,
                "atomic move must consume the temp file"))
