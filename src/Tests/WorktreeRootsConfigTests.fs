module Tests.WorktreeRootsConfigTests

open System
open System.IO
open System.Text.Json
open NUnit.Framework
open Server.GlobalConfig
open Tests.TestUtils

/// Compares two roots the way the endpoints do: absolute, trailing separators trimmed,
/// case-insensitive. Lets assertions ignore the exact normalized form of the temp dir.
let private sameRoot (a: string) (b: string) =
    let canon (p: string) =
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    String.Equals(canon a, canon b, StringComparison.OrdinalIgnoreCase)

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
        withTempConfigDir "treemon-roots-test" (fun _ -> Assert.That(readWorktreeRootsConfig (), Is.Empty))

    [<Test>]
    member _.``writeWorktreeRoots then read round-trips roots in order``() =
        withTempConfigDir "treemon-roots-test" (fun _ ->
            let roots = [ @"C:\code\alpha"; @"C:\code\beta" ]
            assertOk (writeWorktreeRoots roots) "writeWorktreeRoots should succeed"
            Assert.That(readWorktreeRootsConfig (), Is.EqualTo(roots)))

    [<Test>]
    member _.``writeWorktreeRoots preserves unrelated config keys (no clobber)``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let configPath = Path.Combine(tempDir, "config.json")
            // Pre-seed config.json with keys owned by other features (collapsedRepos, canvas, editor).
            let seed =
                """{
  "collapsedRepos": [ "repo-one", "repo-two" ],
  "editor": "vim",
  "canvasPaneOpen": true
}"""
            File.WriteAllText(configPath, seed)

            assertOk (writeWorktreeRoots [ @"C:\code\gamma" ]) "writeWorktreeRoots should succeed"

            // New key persisted.
            Assert.That(readWorktreeRootsConfig (), Is.EqualTo([ @"C:\code\gamma" ]))

            // Pre-existing keys survive the read-modify-write.
            use doc = JsonDocument.Parse(File.ReadAllText configPath)
            let root = doc.RootElement
            Assert.That(root.GetProperty("editor").GetString(), Is.EqualTo("vim"))
            Assert.That(root.GetProperty("canvasPaneOpen").GetBoolean(), Is.True)

            let collapsed =
                root.GetProperty("collapsedRepos").EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq

            Assert.That(collapsed, Is.EqualTo([ "repo-one"; "repo-two" ])))

    [<Test>]
    member _.``writeWorktreeRoots overwrites a previously written roots value``() =
        withTempConfigDir "treemon-roots-test" (fun _ ->
            assertOk (writeWorktreeRoots [ @"C:\code\one" ]) "writeWorktreeRoots should succeed"
            assertOk (writeWorktreeRoots [ @"C:\code\two"; @"C:\code\three" ]) "writeWorktreeRoots should succeed"
            Assert.That(readWorktreeRootsConfig (), Is.EqualTo([ @"C:\code\two"; @"C:\code\three" ])))

    [<Test>]
    member _.``writeWorktreeRoots with empty list clears roots (removing last root is valid)``() =
        withTempConfigDir "treemon-roots-test" (fun _ ->
            assertOk (writeWorktreeRoots [ @"C:\code\one" ]) "writeWorktreeRoots should succeed"
            assertOk (writeWorktreeRoots []) "writeWorktreeRoots should succeed"
            Assert.That(readWorktreeRootsConfig (), Is.Empty))

    // ----- addRoot/removeRoot endpoint logic (tm-config-audit-9ow) -----
    // Compares two roots the way the endpoints do (absolute, trailing-separator trimmed,
    // case-insensitive) so assertions don't hinge on the exact normalized form of the temp dir.

    [<Test>]
    member _.``addRootToConfig persists an existing directory``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let root = Path.Combine(tempDir, "alpha")
            Directory.CreateDirectory(root) |> ignore
            assertOk (addRootToConfig root) "addRootToConfig should succeed"
            let roots = readWorktreeRootsConfig ()
            Assert.That(roots.Length, Is.EqualTo(1))
            Assert.That(sameRoot roots[0] root, Is.True))

    [<Test>]
    member _.``addRootToConfig rejects a path that does not exist``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let missing = Path.Combine(tempDir, "does-not-exist")
            match addRootToConfig missing with
            | Error msg -> Assert.That(msg, Is.Not.Empty)
            | Ok () -> Assert.Fail "expected Error for a non-existent path"
            Assert.That(readWorktreeRootsConfig (), Is.Empty))

    [<Test>]
    member _.``addRootToConfig rejects a blank path``() =
        withTempConfigDir "treemon-roots-test" (fun _ ->
            match addRootToConfig "   " with
            | Error msg -> Assert.That(msg, Is.Not.Empty)
            | Ok () -> Assert.Fail "expected Error for a blank path")

    [<Test>]
    member _.``addRootToConfig is an idempotent no-op for an already-watched path``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let root = Path.Combine(tempDir, "alpha")
            Directory.CreateDirectory(root) |> ignore
            assertOk (addRootToConfig root) "addRootToConfig should succeed"
            // Re-adding the same path with a trailing separator normalizes to the same root and
            // must not duplicate it.
            assertOk (addRootToConfig (root + string Path.DirectorySeparatorChar)) "re-adding the same root should succeed"
            Assert.That(readWorktreeRootsConfig().Length, Is.EqualTo(1)))

    [<Test>]
    member _.``addRootToConfig appends roots preserving insertion order``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let alpha = Path.Combine(tempDir, "alpha")
            let beta = Path.Combine(tempDir, "beta")
            Directory.CreateDirectory(alpha) |> ignore
            Directory.CreateDirectory(beta) |> ignore
            assertOk (addRootToConfig alpha) "addRootToConfig should succeed"
            assertOk (addRootToConfig beta) "addRootToConfig should succeed"
            let roots = readWorktreeRootsConfig ()
            Assert.That(roots.Length, Is.EqualTo(2))
            Assert.That(sameRoot roots[0] alpha, Is.True)
            Assert.That(sameRoot roots[1] beta, Is.True))

    [<Test>]
    member _.``addRootToConfig preserves unrelated config keys``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let configPath = Path.Combine(tempDir, "config.json")
            File.WriteAllText(configPath, """{ "editor": "vim" }""")
            let root = Path.Combine(tempDir, "alpha")
            Directory.CreateDirectory(root) |> ignore
            assertOk (addRootToConfig root) "addRootToConfig should succeed"
            use doc = JsonDocument.Parse(File.ReadAllText configPath)
            Assert.That(doc.RootElement.GetProperty("editor").GetString(), Is.EqualTo("vim")))

    [<Test>]
    member _.``removeRootFromConfig removes a watched root``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let root = Path.Combine(tempDir, "alpha")
            Directory.CreateDirectory(root) |> ignore
            assertOk (addRootToConfig root) "addRootToConfig should succeed"
            assertOk (removeRootFromConfig root) "removeRootFromConfig should succeed"
            Assert.That(readWorktreeRootsConfig (), Is.Empty))

    [<Test>]
    member _.``removeRootFromConfig errors when the path is not watched``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let root = Path.Combine(tempDir, "alpha")
            Directory.CreateDirectory(root) |> ignore
            match removeRootFromConfig root with
            | Error msg -> Assert.That(msg, Is.Not.Empty)
            | Ok () -> Assert.Fail "expected Error for an unwatched path")

    [<Test>]
    member _.``removeRootFromConfig removes a root whose directory no longer exists``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            let root = Path.Combine(tempDir, "alpha")
            Directory.CreateDirectory(root) |> ignore
            assertOk (addRootToConfig root) "addRootToConfig should succeed"
            Directory.Delete(root)
            // Removal must not depend on the directory still existing on disk.
            assertOk (removeRootFromConfig root) "removeRootFromConfig should succeed"
            Assert.That(readWorktreeRootsConfig (), Is.Empty))

    [<Test>]
    member _.``addRootToConfig surfaces a persistence failure as Error``() =
        withTempConfigDir "treemon-roots-test" (fun tempDir ->
            // Make config.json an (unwritable) directory so the File.WriteAllText inside the
            // locked writer throws. The endpoint must surface that as Error rather than a false
            // Ok() — the whole point of the Result<unit,string> contract.
            let configPath = Path.Combine(tempDir, "config.json")
            Directory.CreateDirectory(configPath) |> ignore
            let root = Path.Combine(tempDir, "alpha")
            Directory.CreateDirectory(root) |> ignore
            match addRootToConfig root with
            | Error msg -> Assert.That(msg, Is.Not.Empty)
            | Ok () -> Assert.Fail "expected Error when persistence fails")
