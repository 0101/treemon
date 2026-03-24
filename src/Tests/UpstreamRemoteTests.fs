module Tests.UpstreamRemoteTests

open System
open System.IO
open NUnit.Framework
open Server.TreemonConfig
open Server.RefreshScheduler
open Server.GitWorktree
open Shared

// ─── TreemonConfig: readUpstreamRemote ───

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ReadUpstreamRemoteTests() =

    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-test-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, recursive = true)

    [<Test>]
    member _.``readUpstreamRemote returns None when file does not exist``() =
        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote returns None when file has no upstreamRemote field``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": ["a"] }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote returns Some when upstreamRemote is set``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": "upstream" }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(Some "upstream"))

    [<Test>]
    member _.``readUpstreamRemote returns custom remote name``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": "my-fork-remote" }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(Some "my-fork-remote"))

    [<Test>]
    member _.``readUpstreamRemote returns None for empty string``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": "" }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote returns None for whitespace-only string``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": "   " }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote returns None for malformed JSON``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """not valid json""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote returns None when upstreamRemote is not a string``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": 42 }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote returns None when upstreamRemote is null``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": null }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote coexists with other fields``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "archivedBranches": ["old"], "upstreamRemote": "upstream", "testCommand": "dotnet test test.sln" }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(Some "upstream"))

        let archived = readArchivedBranches tempDir
        Assert.That(archived, Is.EqualTo([ "old" ]), "Other fields should still be readable")

    [<Test>]
    member _.``readUpstreamRemote rejects value starting with double dash``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": "--upload-pack=evil" }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote rejects value with spaces``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": "origin; rm -rf /" }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``readUpstreamRemote accepts hyphenated remote name``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": "my-upstream" }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(Some "my-upstream"))

    [<Test>]
    member _.``readUpstreamRemote accepts dotted remote name``() =
        File.WriteAllText(
            Path.Combine(tempDir, ".treemon.json"),
            """{ "upstreamRemote": "origin.backup" }""")

        let result = readUpstreamRemote tempDir
        Assert.That(result, Is.EqualTo(Some "origin.backup"))


// ─── Git command construction: mainRef, fetch, merge targets ───

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type GitCommandConstructionTests() =

    [<Test>]
    member _.``mainRef with origin produces origin/main``() =
        Assert.That(mainRef "origin", Is.EqualTo("origin/main"))

    [<Test>]
    member _.``mainRef with upstream produces upstream/main``() =
        Assert.That(mainRef "upstream", Is.EqualTo("upstream/main"))

    [<Test>]
    member _.``mainRef with custom remote produces custom/main``() =
        Assert.That(mainRef "my-fork", Is.EqualTo("my-fork/main"))

    [<Test>]
    member _.``SyncEngine buildFetchArgs with origin produces fetch origin``() =
        Assert.That(Server.SyncEngine.buildFetchArgs "origin", Is.EqualTo("fetch origin"))

    [<Test>]
    member _.``SyncEngine buildFetchArgs with upstream produces fetch upstream``() =
        Assert.That(Server.SyncEngine.buildFetchArgs "upstream", Is.EqualTo("fetch upstream"))

    [<Test>]
    member _.``SyncEngine buildFetchArgs with custom remote produces fetch custom``() =
        Assert.That(Server.SyncEngine.buildFetchArgs "my-fork", Is.EqualTo("fetch my-fork"))

    [<Test>]
    member _.``PrStatus buildRemoteUrlArgs queries the specified remote``() =
        let args = Server.PrStatus.buildRemoteUrlArgs "/repo/root" "upstream"
        Assert.That(args, Does.Contain("remote get-url upstream"))

    [<Test>]
    member _.``PrStatus buildRemoteUrlArgs with origin queries origin``() =
        let args = Server.PrStatus.buildRemoteUrlArgs "/repo/root" "origin"
        Assert.That(args, Does.Contain("remote get-url origin"))

    [<Test>]
    member _.``PrStatus buildRemoteUrlArgs includes repo root path``() =
        let args = Server.PrStatus.buildRemoteUrlArgs "Q:\\code\\myproject" "upstream"
        Assert.That(args, Does.Contain("Q:\\code\\myproject"))
        Assert.That(args, Does.Contain("remote get-url upstream"))


// ─── RefreshScheduler: PerRepoState defaults and UpdateUpstreamRemote ───

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SchedulerUpstreamTests() =

    let testRepoId = RepoId "TestRepo"

    let waitForAgent (agent: MailboxProcessor<StateMsg>) =
        agent.PostAndAsyncReply(GetState) |> Async.Ignore

    [<Test>]
    member _.``PerRepoState.empty defaults UpstreamRemote to origin``() =
        Assert.That(PerRepoState.empty.UpstreamRemote, Is.EqualTo("origin"))

    [<Test>]
    member _.``UpdateUpstreamRemote sets upstream on new repo``() =
        async {
            let agent = createAgent ()

            agent.Post(UpdateWorktreeList(testRepoId, []))
            do! waitForAgent agent

            agent.Post(UpdateUpstreamRemote(testRepoId, "upstream"))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.find testRepoId

            Assert.That(repo.UpstreamRemote, Is.EqualTo("upstream"))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``UpdateUpstreamRemote overwrites previous value``() =
        async {
            let agent = createAgent ()

            agent.Post(UpdateWorktreeList(testRepoId, []))
            do! waitForAgent agent

            agent.Post(UpdateUpstreamRemote(testRepoId, "upstream"))
            do! waitForAgent agent

            agent.Post(UpdateUpstreamRemote(testRepoId, "my-remote"))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.find testRepoId

            Assert.That(repo.UpstreamRemote, Is.EqualTo("my-remote"))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``UpdateUpstreamRemote on unknown repo creates repo entry``() =
        async {
            let agent = createAgent ()
            let unknownRepo = RepoId "Unknown"

            agent.Post(UpdateUpstreamRemote(unknownRepo, "upstream"))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.find unknownRepo

            Assert.That(repo.UpstreamRemote, Is.EqualTo("upstream"))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``UpdateUpstreamRemote preserves existing worktree data``() =
        async {
            let agent = createAgent ()

            let worktrees =
                [ { WorktreeInfo.Path = "/repo/main"
                    Head = "abc123"
                    Branch = Some "main" } ]

            agent.Post(UpdateWorktreeList(testRepoId, worktrees))
            do! waitForAgent agent

            let gitData : GitData =
                { Path = "/repo/main"
                  Branch = "main"
                  LastCommitMessage = "init"
                  LastCommitTime = DateTimeOffset.UtcNow
                  UpstreamBranch = None
                  MainBehindCount = 0
                  IsDirty = false
                  WorkMetrics = None }

            agent.Post(UpdateGit(testRepoId, "/repo/main", gitData))
            do! waitForAgent agent

            agent.Post(UpdateUpstreamRemote(testRepoId, "upstream"))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.find testRepoId

            Assert.That(repo.UpstreamRemote, Is.EqualTo("upstream"))
            Assert.That(repo.WorktreeList.Length, Is.EqualTo(1), "Worktree list should be preserved")
            Assert.That(repo.GitData.ContainsKey("/repo/main"), Is.True, "Git data should be preserved")
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Multiple repos have independent upstream remotes``() =
        async {
            let agent = createAgent ()
            let repo1 = RepoId "Repo1"
            let repo2 = RepoId "Repo2"

            agent.Post(UpdateWorktreeList(repo1, []))
            agent.Post(UpdateWorktreeList(repo2, []))
            do! waitForAgent agent

            agent.Post(UpdateUpstreamRemote(repo1, "upstream"))
            agent.Post(UpdateUpstreamRemote(repo2, "my-fork"))

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That((state.Repos |> Map.find repo1).UpstreamRemote, Is.EqualTo("upstream"))
            Assert.That((state.Repos |> Map.find repo2).UpstreamRemote, Is.EqualTo("my-fork"))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Repo without UpdateUpstreamRemote keeps default origin``() =
        async {
            let agent = createAgent ()

            agent.Post(UpdateWorktreeList(testRepoId, []))

            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.find testRepoId

            Assert.That(repo.UpstreamRemote, Is.EqualTo("origin"))
        }
        |> Async.RunSynchronously
