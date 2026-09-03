module Tests.ConfigWriterTests

open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open NUnit.Framework
open Shared
open Server.GlobalConfig
open Tests.TestUtils

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
        withTempDir "treemon-config-test" (fun dir ->
            let configPath = Path.Combine(dir, "config.json")

            assertOk (updateConfigAtPath configPath [ "editor", str "vim" ]) "first write"
            assertOk (updateConfigAtPath configPath [ "editorName", str "Neovim" ]) "second write"

            let root = readStringMap configPath
            Assert.That(Map.tryFind "editor" root, Is.EqualTo(Some "vim"),
                "first key must survive the second write")
            Assert.That(Map.tryFind "editorName" root, Is.EqualTo(Some "Neovim")))

    [<Test>]
    member _.``unparseable file is backed up, not destroyed``() =
        withTempDir "treemon-config-test" (fun dir ->
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
        withTempDir "treemon-config-test" (fun dir ->
            let configPath = Path.Combine(dir, "config.json")
            assertOk (updateConfigAtPath configPath [ "editor", str "vim" ]) "write"
            Assert.That(File.Exists(configPath + ".tmp"), Is.False,
                "atomic move must consume the temp file"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type WorkspaceWidthConfigTests() =

    let writeConfig (dir: string) (json: string) =
        File.WriteAllText(Path.Combine(dir, "config.json"), json)

    [<Test>]
    member _.``Absent config reads as equal thirds``() =
        withTempConfigDir "treemon-width-test" (fun _ ->
            Assert.That(readWorkspaceWidth (), Is.EqualTo(WorkspaceWidth.EqualThirds)))

    [<Test>]
    member _.``Workspace width round-trips``() =
        withTempConfigDir "treemon-width-test" (fun _ ->
            writeWorkspaceWidth WorkspaceWidth.WideCanvas
            Assert.That(readWorkspaceWidth (), Is.EqualTo(WorkspaceWidth.WideCanvas))
            writeWorkspaceWidth WorkspaceWidth.EqualThirds
            Assert.That(readWorkspaceWidth (), Is.EqualTo(WorkspaceWidth.EqualThirds)))

    [<Test>]
    member _.``Legacy two-to-one canvas size migrates to wide canvas``() =
        withTempConfigDir "treemon-width-test" (fun dir ->
            writeConfig dir """{"canvasSize":"2to1","canvasPosition":"bottom"}"""
            Assert.That(readWorkspaceWidth (), Is.EqualTo(WorkspaceWidth.WideCanvas)))

    [<Test>]
    member _.``Explicit workspace width wins over legacy canvas size``() =
        withTempConfigDir "treemon-width-test" (fun dir ->
            writeConfig dir """{"workspaceWidth":"thirds","canvasSize":"2to1"}"""
            Assert.That(readWorkspaceWidth (), Is.EqualTo(WorkspaceWidth.EqualThirds)))
