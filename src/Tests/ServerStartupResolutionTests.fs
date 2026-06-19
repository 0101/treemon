module Tests.ServerStartupResolutionTests

open System
open System.IO
open System.Text.Json.Nodes
open NUnit.Framework

// `parseArgs`/`resolveWorktreeRoots` live in the server entry-point file's implicit `Program`
// module; `Server` exposes internals to `Tests` (InternalsVisibleTo), so `resolveWorktreeRoots`
// (internal) is reachable here.
open Program

/// Isolates the machine-level config dir to a throwaway temp dir via the TREEMON_CONFIG_DIR
/// override (mirrors WorktreeRootsConfigTests). Required, not convenient: on Windows
/// Environment.GetFolderPath(UserProfile) ignores USERPROFILE/HOME, so this is the only way to
/// keep the in-process resolver off the real ~/.treemon. Both the global config read/write and the
/// orphan roots.json lookup resolve under this dir.
let private withTempConfigDir (action: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), $"treemon-startup-test-{Guid.NewGuid()}")
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
        withTempConfigDir (fun _ ->
            let cliRoots = [ @"C:\code\one"; @"C:\code\two" ]
            let resolved = resolveWorktreeRoots cliRoots
            Assert.That(resolved, Is.EqualTo(cliRoots))
            // First-time persistence: the arg-provided set becomes the durable source of truth.
            Assert.That(Server.WorktreeApi.readWorktreeRootsConfig (), Is.EqualTo(cliRoots)))

    [<Test>]
    member _.``resolveWorktreeRoots prefers CLI args but does not overwrite a populated config``() =
        withTempConfigDir (fun _ ->
            let configured = [ @"C:\code\configured" ]
            match Server.WorktreeApi.writeWorktreeRoots configured with
            | Ok () -> ()
            | Error msg -> Assert.Fail $"setup write failed: {msg}"

            let resolved = resolveWorktreeRoots [ @"C:\code\arg" ]
            // CLI args win for this run...
            Assert.That(resolved, Is.EqualTo([ @"C:\code\arg" ]))
            // ...but a populated config is an ephemeral override, not clobbered.
            Assert.That(Server.WorktreeApi.readWorktreeRootsConfig (), Is.EqualTo(configured)))

    [<Test>]
    member _.``resolveWorktreeRoots reads global config when no CLI args``() =
        withTempConfigDir (fun _ ->
            let configured = [ @"C:\code\x"; @"C:\code\y" ]
            match Server.WorktreeApi.writeWorktreeRoots configured with
            | Ok () -> ()
            | Error msg -> Assert.Fail $"setup write failed: {msg}"

            let resolved = resolveWorktreeRoots []
            Assert.That(resolved, Is.EqualTo(configured)))

    [<Test>]
    member _.``resolveWorktreeRoots imports orphan roots.json then persists and deletes it``() =
        withTempConfigDir (fun tempDir ->
            let orphanRoots = [ @"C:\code\orphan-a"; @"C:\code\orphan-b" ]
            writeOrphan tempDir orphanRoots

            let resolved = resolveWorktreeRoots []

            Assert.That(resolved, Is.EqualTo(orphanRoots))
            // Migrated set persisted into the global config...
            Assert.That(Server.WorktreeApi.readWorktreeRootsConfig (), Is.EqualTo(orphanRoots))
            // ...and the orphan file is consumed (read-then-delete).
            Assert.That(File.Exists(Path.Combine(tempDir, "roots.json")), Is.False))

    [<Test>]
    member _.``resolveWorktreeRoots returns empty when no args, no config, no orphan``() =
        withTempConfigDir (fun _ ->
            let resolved = resolveWorktreeRoots []
            Assert.That(resolved, Is.Empty)
            // Nothing to persist, so the config stays absent/empty.
            Assert.That(Server.WorktreeApi.readWorktreeRootsConfig (), Is.Empty))
