module Tests.ServerStartupResolutionTests

open System.IO
open System.Text.Json.Nodes
open NUnit.Framework

// `parseArgs`/`resolveWorktreeRoots` live in the server entry-point file's implicit `Program`
// module; `Server` exposes internals to `Tests` (InternalsVisibleTo), so `resolveWorktreeRoots`
// (internal) is reachable here.
open Program
open Tests.TestUtils

/// Writes an orphan `roots.json` (`{ "WorktreeRoots": [...] }`) into the isolated config dir using
/// the JSON node API, so test paths never need manual backslash escaping.
let private writeOrphan (configDir: string) (roots: string list) =
    let arr = JsonArray()
    roots |> List.iter (fun r -> arr.Add(r))
    let root = JsonObject()
    root["WorktreeRoots"] <- arr
    File.WriteAllText(Path.Combine(configDir, "roots.json"), root.ToJsonString())

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
// resolveWorktreeRoots tests mutate TREEMON_CONFIG_DIR (shared process state); NonParallelizable
// guards against a future assembly-parallel switch racing the env var across fixtures.
[<NonParallelizable>]
type ServerStartupResolutionTests() =

    // ----- parseArgs: zero positional roots is valid in normal mode -----

    [<Test>]
    member _.``parseArgs with no args yields empty roots in normal mode``() =
        let config = parseArgs [||]
        Assert.That(config.WorktreeRoots, Is.Empty)
        Assert.That(config.Demo, Is.False)
        Assert.That(config.Port, Is.EqualTo(5000))

    [<Test>]
    member _.``parseArgs with only --port yields empty roots and the chosen port``() =
        let config = parseArgs [| "--port"; "5050" |]
        Assert.That(config.WorktreeRoots, Is.Empty)
        Assert.That(config.Demo, Is.False)
        Assert.That(config.Port, Is.EqualTo(5050))

    [<Test>]
    member _.``parseArgs with a single root keeps that root``() =
        let config = parseArgs [| @"C:\code\alpha" |]
        Assert.That(config.WorktreeRoots, Is.EqualTo([ @"C:\code\alpha" ]))
        Assert.That(config.Demo, Is.False)

    [<Test>]
    member _.``parseArgs --demo stays demo with empty roots``() =
        let config = parseArgs [| "--demo" |]
        Assert.That(config.Demo, Is.True)
        Assert.That(config.WorktreeRoots, Is.Empty)

    // ----- resolveWorktreeRoots: priority + first-time persistence + orphan migration -----

    [<Test>]
    member _.``resolveWorktreeRoots persists CLI args when config has no roots``() =
        withTempConfigDir "treemon-startup-test" (fun _ ->
            let cliRoots = [ @"C:\code\one"; @"C:\code\two" ]
            let resolution = resolveWorktreeRoots cliRoots
            Assert.That(resolution.Roots, Is.EqualTo(cliRoots))
            // First-time persist decision: the arg-provided set should become the durable source.
            Assert.That(resolution.PersistRoots, Is.True)
            Assert.That(resolution.ConsumeOrphan, Is.False)
            // Applying the resolution at the boundary writes it to the global config.
            persistResolvedRoots resolution
            Assert.That(Server.GlobalConfig.readWorktreeRootsConfig (), Is.EqualTo(cliRoots)))

    [<Test>]
    member _.``resolveWorktreeRoots prefers CLI args but does not overwrite a populated config``() =
        withTempConfigDir "treemon-startup-test" (fun _ ->
            let configured = [ @"C:\code\configured" ]
            match Server.GlobalConfig.writeWorktreeRoots configured with
            | Ok () -> ()
            | Error msg -> Assert.Fail $"setup write failed: {msg}"

            let resolution = resolveWorktreeRoots [ @"C:\code\arg" ]
            // CLI args win for this run...
            Assert.That(resolution.Roots, Is.EqualTo([ @"C:\code\arg" ]))
            // ...and because the config key is present, this is not a first-time persist.
            Assert.That(resolution.PersistRoots, Is.False)
            persistResolvedRoots resolution
            // ...so a populated config is an ephemeral override, not clobbered.
            Assert.That(Server.GlobalConfig.readWorktreeRootsConfig (), Is.EqualTo(configured)))

    [<Test>]
    member _.``resolveWorktreeRoots reads global config when no CLI args``() =
        withTempConfigDir "treemon-startup-test" (fun _ ->
            let configured = [ @"C:\code\x"; @"C:\code\y" ]
            match Server.GlobalConfig.writeWorktreeRoots configured with
            | Ok () -> ()
            | Error msg -> Assert.Fail $"setup write failed: {msg}"

            let resolution = resolveWorktreeRoots []
            Assert.That(resolution.Roots, Is.EqualTo(configured))
            Assert.That(resolution.PersistRoots, Is.False))

    [<Test>]
    member _.``resolveWorktreeRoots imports orphan roots.json then persists and deletes it``() =
        withTempConfigDir "treemon-startup-test" (fun tempDir ->
            let orphanRoots = [ @"C:\code\orphan-a"; @"C:\code\orphan-b" ]
            writeOrphan tempDir orphanRoots

            let resolution = resolveWorktreeRoots []

            Assert.That(resolution.Roots, Is.EqualTo(orphanRoots))
            Assert.That(resolution.PersistRoots, Is.True)
            Assert.That(resolution.ConsumeOrphan, Is.True)
            // Resolution is pure: the orphan is still on disk until the boundary applies it.
            Assert.That(File.Exists(Path.Combine(tempDir, "roots.json")), Is.True)

            persistResolvedRoots resolution

            // Migrated set persisted into the global config...
            Assert.That(Server.GlobalConfig.readWorktreeRootsConfig (), Is.EqualTo(orphanRoots))
            // ...and the orphan file is consumed (deleted) only after a successful persist.
            Assert.That(File.Exists(Path.Combine(tempDir, "roots.json")), Is.False))

    [<Test>]
    member _.``resolveWorktreeRoots returns empty when no args, no config, no orphan``() =
        withTempConfigDir "treemon-startup-test" (fun _ ->
            let resolution = resolveWorktreeRoots []
            Assert.That(resolution.Roots, Is.Empty)
            Assert.That(resolution.PersistRoots, Is.False)
            persistResolvedRoots resolution
            // Nothing to persist, so the config stays absent/empty.
            Assert.That(Server.GlobalConfig.readWorktreeRootsConfig (), Is.Empty))

    // ----- Regression (tm-config-audit-rf1): an explicit `worktreeRoots:[]` must stay empty -----
    // The bug: readWorktreeRootsConfig() returned [] for BOTH a missing key and a present-but-empty
    // one, so resolveWorktreeRoots treated a curated-down-to-zero config like a fresh install and
    // repopulated it from a stale orphan roots.json (or from CLI args). These pin the fix.

    [<Test>]
    member _.``resolveWorktreeRoots leaves an explicit empty config empty despite an orphan roots.json``() =
        withTempConfigDir "treemon-startup-test" (fun tempDir ->
            // The user removed every root: the key is PRESENT but empty (not absent).
            match Server.GlobalConfig.writeWorktreeRoots [] with
            | Ok () -> ()
            | Error msg -> Assert.Fail $"setup write failed: {msg}"
            // A stale orphan roots.json from a legacy upgrade still lingers on disk.
            writeOrphan tempDir [ @"C:\code\removed-a"; @"C:\code\removed-b" ]

            let resolution = resolveWorktreeRoots []

            // The orphan must NOT resurrect the removed roots for this run...
            Assert.That(resolution.Roots, Is.Empty)
            // ...and a present (empty) key means no first-time persist...
            Assert.That(resolution.PersistRoots, Is.False)
            persistResolvedRoots resolution
            // ...the explicit empty config is preserved (present key, still empty — not None, not
            // repopulated with the orphan's roots)...
            match Server.GlobalConfig.tryReadWorktreeRootsConfig () with
            | Some [] -> ()
            | other -> Assert.Fail $"expected the config to stay an explicit empty (Some []), got %A{other}"
            // ...and the unconsumed orphan is left untouched (it is only migrated when the key is absent).
            Assert.That(File.Exists(Path.Combine(tempDir, "roots.json")), Is.True))

    [<Test>]
    member _.``resolveWorktreeRoots does not persist CLI args over an explicit empty config``() =
        withTempConfigDir "treemon-startup-test" (fun _ ->
            match Server.GlobalConfig.writeWorktreeRoots [] with
            | Ok () -> ()
            | Error msg -> Assert.Fail $"setup write failed: {msg}"

            let resolution = resolveWorktreeRoots [ @"C:\code\arg" ]

            // CLI args still win for THIS run (ephemeral override)...
            Assert.That(resolution.Roots, Is.EqualTo([ @"C:\code\arg" ]))
            Assert.That(resolution.PersistRoots, Is.False)
            persistResolvedRoots resolution
            // ...but the explicit empty config is not clobbered, so a restart with no args stays empty.
            match Server.GlobalConfig.tryReadWorktreeRootsConfig () with
            | Some [] -> ()
            | other -> Assert.Fail $"expected the config to stay an explicit empty (Some []), got %A{other}")
