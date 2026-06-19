module Tests.WorktreeRootsConfigTests

open System
open System.IO
open System.Text.Json
open NUnit.Framework
open Server.WorktreeApi

/// Isolates the machine-level Treemon config dir to a throwaway temp dir via the
/// TREEMON_CONFIG_DIR override. This is required, not merely convenient: on Windows
/// Environment.GetFolderPath(UserProfile) ignores the USERPROFILE/HOME env vars, so the
/// override is the only way to keep these in-process tests off the real ~/.treemon.
let private withTempConfigDir (action: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), $"treemon-roots-test-{Guid.NewGuid()}")
    Directory.CreateDirectory(tempDir) |> ignore
    let original = Environment.GetEnvironmentVariable("TREEMON_CONFIG_DIR")
    Environment.SetEnvironmentVariable("TREEMON_CONFIG_DIR", tempDir)

    try
        action tempDir
    finally
        Environment.SetEnvironmentVariable("TREEMON_CONFIG_DIR", original)

        try
            Directory.Delete(tempDir, recursive = true)
        with _ ->
            ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
// withTempConfigDir mutates the TREEMON_CONFIG_DIR env var (shared process state) so the
// global-config read/write helpers target a throwaway dir instead of the real ~/.treemon.
// Safe only because NUnit runs sequentially today; NonParallelizable guards against a future
// assembly-parallel switch racing the env var across fixtures.
[<NonParallelizable>]
type WorktreeRootsConfigTests() =

    [<Test>]
    member _.``readWorktreeRootsConfig returns empty when config file is absent``() =
        withTempConfigDir (fun _ -> Assert.That(readWorktreeRootsConfig (), Is.Empty))

    [<Test>]
    member _.``writeWorktreeRoots then read round-trips roots in order``() =
        withTempConfigDir (fun _ ->
            let roots = [ @"C:\code\alpha"; @"C:\code\beta" ]
            writeWorktreeRoots roots
            Assert.That(readWorktreeRootsConfig (), Is.EqualTo(roots)))

    [<Test>]
    member _.``writeWorktreeRoots preserves unrelated config keys (no clobber)``() =
        withTempConfigDir (fun tempDir ->
            let configPath = Path.Combine(tempDir, "config.json")
            // Pre-seed config.json with keys owned by other features (collapsedRepos, canvas, editor).
            let seed =
                """{
  "collapsedRepos": [ "repo-one", "repo-two" ],
  "editor": "vim",
  "canvasPaneOpen": true
}"""
            File.WriteAllText(configPath, seed)

            writeWorktreeRoots [ @"C:\code\gamma" ]

            // New key persisted.
            Assert.That(readWorktreeRootsConfig (), Is.EqualTo([ @"C:\code\gamma" ]))

            // Pre-existing keys survive the read-modify-write.
            use doc = JsonDocument.Parse(File.ReadAllText configPath)
            let root = doc.RootElement
            Assert.That(root.GetProperty("editor").GetString(), Is.EqualTo("vim"))
            Assert.That(root.GetProperty("canvasPaneOpen").GetBoolean(), Is.True)

            let collapsed =
                root.GetProperty("collapsedRepos").EnumerateArray()
                |> Seq.map (fun e -> e.GetString())
                |> List.ofSeq

            Assert.That(collapsed, Is.EqualTo([ "repo-one"; "repo-two" ])))

    [<Test>]
    member _.``writeWorktreeRoots overwrites a previously written roots value``() =
        withTempConfigDir (fun _ ->
            writeWorktreeRoots [ @"C:\code\one" ]
            writeWorktreeRoots [ @"C:\code\two"; @"C:\code\three" ]
            Assert.That(readWorktreeRootsConfig (), Is.EqualTo([ @"C:\code\two"; @"C:\code\three" ])))

    [<Test>]
    member _.``writeWorktreeRoots with empty list clears roots (removing last root is valid)``() =
        withTempConfigDir (fun _ ->
            writeWorktreeRoots [ @"C:\code\one" ]
            writeWorktreeRoots []
            Assert.That(readWorktreeRootsConfig (), Is.Empty))
