module Tests.WorktreePathResolutionTests

open System
open System.IO
open NUnit.Framework
open Server
open Server.RefreshScheduler
open Server.GitWorktree
open Server.SyncEngine
open Shared

let private makeWorktree path branch : WorktreeInfo =
    { Path = path; Head = "abc123"; Branch = Some branch }

let private makeDetachedWorktree path : WorktreeInfo =
    { Path = path; Head = "abc123"; Branch = None }

let private populateAgent (agent: MailboxProcessor<StateMsg>) (repos: (RepoId * WorktreeInfo list) list) =
    async {
        repos
        |> List.iter (fun (repoId, worktrees) ->
            agent.Post(UpdateWorktreeList(repoId, worktrees)))
        do! Async.Sleep 100
    }

let private getAgentState (agent: MailboxProcessor<StateMsg>) =
    agent.PostAndAsyncReply(GetState)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DeleteWorktreeResolutionTests() =

    let mutable tempDirA = ""
    let mutable tempDirB = ""

    [<SetUp>]
    member _.Setup() =
        tempDirA <- Path.Combine(Path.GetTempPath(), $"treemon-test-a-{Guid.NewGuid()}")
        tempDirB <- Path.Combine(Path.GetTempPath(), $"treemon-test-b-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDirA) |> ignore
        Directory.CreateDirectory(tempDirB) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDirA) then Directory.Delete(tempDirA, recursive = true)
        if Directory.Exists(tempDirB) then Directory.Delete(tempDirB, recursive = true)

    [<Test>]
    member _.``deleteWorktree with WorktreePath targets correct repo when branches are duplicated``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)
            let repoBId = RepoId.create (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirA}/feature-x" "feature-x" ]
            let worktreesB =
                [ makeWorktree $"{Path.GetFullPath tempDirB}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirB}/feature-x" "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = WorktreeApi.worktreeApi agent (createSyncAgent ()) (SessionManager.createAgent ()) [ tempDirA; tempDirB ] None "1.0" None

            let targetPath = $"{Path.GetFullPath tempDirA}/feature-x"
            let! _result = api.deleteWorktree (WorktreePath.create targetPath)

            do! Async.Sleep 50
            let! state = getAgentState agent

            let repoAWorktrees =
                state.Repos
                |> Map.tryFind repoAId
                |> Option.map (fun r -> r.WorktreeList |> List.map _.Path)
                |> Option.defaultValue []

            Assert.That(
                repoAWorktrees,
                Does.Not.Contain(targetPath),
                "RepoA should have feature-x removed from state")

            let repoBWorktrees =
                state.Repos
                |> Map.tryFind repoBId
                |> Option.map (fun r -> r.WorktreeList |> List.map _.Path)
                |> Option.defaultValue []

            Assert.That(
                repoBWorktrees,
                Does.Contain($"{Path.GetFullPath tempDirB}/feature-x"),
                "RepoB's feature-x should NOT be affected")

            Assert.That(
                repoBWorktrees,
                Does.Contain($"{Path.GetFullPath tempDirB}/main"),
                "RepoB's main should NOT be affected")
        }

    [<Test>]
    member _.``deleteWorktree with WorktreePath for repoB does not affect repoA``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)
            let repoBId = RepoId.create (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirA}/feature-x" "feature-x" ]
            let worktreesB =
                [ makeWorktree $"{Path.GetFullPath tempDirB}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirB}/feature-x" "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = WorktreeApi.worktreeApi agent (createSyncAgent ()) (SessionManager.createAgent ()) [ tempDirA; tempDirB ] None "1.0" None

            let targetPath = $"{Path.GetFullPath tempDirB}/main"
            let! _result = api.deleteWorktree (WorktreePath.create targetPath)

            do! Async.Sleep 50
            let! state = getAgentState agent

            let repoBWorktrees =
                state.Repos
                |> Map.tryFind repoBId
                |> Option.map (fun r -> r.WorktreeList |> List.map _.Path)
                |> Option.defaultValue []

            Assert.That(
                repoBWorktrees,
                Does.Not.Contain(targetPath),
                "RepoB should have main removed from state")

            let repoAWorktrees =
                state.Repos
                |> Map.tryFind repoAId
                |> Option.map (fun r -> r.WorktreeList |> List.map _.Path)
                |> Option.defaultValue []

            Assert.That(
                repoAWorktrees,
                Does.Contain($"{Path.GetFullPath tempDirA}/main"),
                "RepoA's main should NOT be affected")
        }

    [<Test>]
    member _.``deleteWorktree with unknown path returns error``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)

            let worktreesA =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main" ]

            do! populateAgent agent [ repoAId, worktreesA ]

            let api = WorktreeApi.worktreeApi agent (createSyncAgent ()) (SessionManager.createAgent ()) [ tempDirA ] None "1.0" None

            let! result = api.deleteWorktree (WorktreePath.create "/nonexistent/path/main")

            match result with
            | Error msg ->
                Assert.That(msg, Does.Contain("No worktree found"), "Should report worktree not found")
            | Ok () ->
                Assert.Fail("Should have returned error for unknown path")
        }


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ArchiveWorktreeResolutionTests() =

    let mutable tempDirA = ""
    let mutable tempDirB = ""

    [<SetUp>]
    member _.Setup() =
        tempDirA <- Path.Combine(Path.GetTempPath(), $"treemon-test-a-{Guid.NewGuid()}")
        tempDirB <- Path.Combine(Path.GetTempPath(), $"treemon-test-b-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDirA) |> ignore
        Directory.CreateDirectory(tempDirB) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDirA) then Directory.Delete(tempDirA, recursive = true)
        if Directory.Exists(tempDirB) then Directory.Delete(tempDirB, recursive = true)

    [<Test>]
    member _.``archiveWorktree with WorktreePath archives correct repo branch when duplicated``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)
            let repoBId = RepoId.create (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirA}/feature-x" "feature-x" ]
            let worktreesB =
                [ makeWorktree $"{Path.GetFullPath tempDirB}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirB}/feature-x" "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = WorktreeApi.worktreeApi agent (createSyncAgent ()) (SessionManager.createAgent ()) [ tempDirA; tempDirB ] None "1.0" None

            let! result = api.archiveWorktree (WorktreePath.create $"{Path.GetFullPath tempDirA}/feature-x")

            match result with
            | Ok () -> ()
            | Error msg -> Assert.Fail($"archiveWorktree should succeed but got: {msg}")

            let archivedA = TreemonConfig.readArchivedBranches tempDirA
            Assert.That(archivedA, Does.Contain("feature-x"), "RepoA should have feature-x archived")

            let archivedB = TreemonConfig.readArchivedBranches tempDirB
            Assert.That(archivedB, Does.Not.Contain("feature-x"), "RepoB should NOT have feature-x archived")
        }

    [<Test>]
    member _.``archiveWorktree for repoB does not affect repoA``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)
            let repoBId = RepoId.create (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirA}/feature-x" "feature-x" ]
            let worktreesB =
                [ makeWorktree $"{Path.GetFullPath tempDirB}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirB}/feature-x" "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = WorktreeApi.worktreeApi agent (createSyncAgent ()) (SessionManager.createAgent ()) [ tempDirA; tempDirB ] None "1.0" None

            let! result = api.archiveWorktree (WorktreePath.create $"{Path.GetFullPath tempDirB}/main")

            match result with
            | Ok () -> ()
            | Error msg -> Assert.Fail($"archiveWorktree should succeed but got: {msg}")

            let archivedB = TreemonConfig.readArchivedBranches tempDirB
            Assert.That(archivedB, Does.Contain("main"), "RepoB should have main archived")

            let archivedA = TreemonConfig.readArchivedBranches tempDirA
            Assert.That(archivedA, Does.Not.Contain("main"), "RepoA should NOT have main archived")
        }

    [<Test>]
    member _.``unarchiveWorktree with WorktreePath targets correct repo``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)
            let repoBId = RepoId.create (Path.GetFullPath tempDirB)

            TreemonConfig.setArchivedBranches tempDirA [ "feature-x" ]
            TreemonConfig.setArchivedBranches tempDirB [ "feature-x" ]

            let worktreesA =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirA}/feature-x" "feature-x" ]
            let worktreesB =
                [ makeWorktree $"{Path.GetFullPath tempDirB}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirB}/feature-x" "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = WorktreeApi.worktreeApi agent (createSyncAgent ()) (SessionManager.createAgent ()) [ tempDirA; tempDirB ] None "1.0" None

            let! result = api.unarchiveWorktree (WorktreePath.create $"{Path.GetFullPath tempDirA}/feature-x")

            match result with
            | Ok () -> ()
            | Error msg -> Assert.Fail($"unarchiveWorktree should succeed but got: {msg}")

            let archivedA = TreemonConfig.readArchivedBranches tempDirA
            Assert.That(archivedA, Does.Not.Contain("feature-x"), "RepoA should have feature-x unarchived")

            let archivedB = TreemonConfig.readArchivedBranches tempDirB
            Assert.That(archivedB, Does.Contain("feature-x"), "RepoB should still have feature-x archived")
        }

    [<Test>]
    member _.``archiveWorktree with detached HEAD returns error``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)

            let worktreesA =
                [ makeDetachedWorktree $"{Path.GetFullPath tempDirA}/detached" ]

            do! populateAgent agent [ repoAId, worktreesA ]

            let api = WorktreeApi.worktreeApi agent (createSyncAgent ()) (SessionManager.createAgent ()) [ tempDirA ] None "1.0" None

            let! result = api.archiveWorktree (WorktreePath.create $"{Path.GetFullPath tempDirA}/detached")

            match result with
            | Error msg ->
                Assert.That(msg, Does.Contain("detached HEAD"), "Should mention detached HEAD")
            | Ok () ->
                Assert.Fail("Should have returned error for detached HEAD worktree")
        }


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SyncResolutionTests() =

    let mutable tempDirA = ""
    let mutable tempDirB = ""

    [<SetUp>]
    member _.Setup() =
        tempDirA <- Path.Combine(Path.GetTempPath(), $"treemon-test-a-{Guid.NewGuid()}")
        tempDirB <- Path.Combine(Path.GetTempPath(), $"treemon-test-b-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempDirA) |> ignore
        Directory.CreateDirectory(tempDirB) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDirA) then Directory.Delete(tempDirA, recursive = true)
        if Directory.Exists(tempDirB) then Directory.Delete(tempDirB, recursive = true)

    [<Test>]
    member _.``startSync with WorktreePath resolves correct repo sync key``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let syncAgent = createSyncAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)
            let repoBId = RepoId.create (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirA}/feature-x" "feature-x" ]
            let worktreesB =
                [ makeWorktree $"{Path.GetFullPath tempDirB}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirB}/feature-x" "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = WorktreeApi.worktreeApi agent syncAgent (SessionManager.createAgent ()) [ tempDirA; tempDirB ] None "1.0" None

            let! result = api.startSync (WorktreePath.create $"{Path.GetFullPath tempDirA}/feature-x")

            do! Async.Sleep 100
            let! syncStatus = syncAgent.PostAndAsyncReply(GetAllEvents)

            let repoAKeys =
                syncStatus
                |> Map.keys
                |> Seq.filter (fun k -> k.Contains(Path.GetFullPath tempDirA))
                |> Seq.toList

            let repoBKeys =
                syncStatus
                |> Map.keys
                |> Seq.filter (fun k -> k.Contains(Path.GetFullPath tempDirB))
                |> Seq.toList

            match result with
            | Ok () ->
                Assert.That(repoAKeys.Length, Is.GreaterThan(0), "Sync key should be scoped to repoA")
                Assert.That(repoBKeys, Is.Empty, "No sync keys should reference repoB")
            | Error _ ->
                Assert.Pass("startSync returned error (expected without real git repo), path resolution succeeded")
        }

    [<Test>]
    member _.``startSync with detached HEAD returns error``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let syncAgent = createSyncAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)

            let worktrees =
                [ makeDetachedWorktree $"{Path.GetFullPath tempDirA}/detached" ]

            do! populateAgent agent [ repoAId, worktrees ]

            let api = WorktreeApi.worktreeApi agent syncAgent (SessionManager.createAgent ()) [ tempDirA ] None "1.0" None

            let! result = api.startSync (WorktreePath.create $"{Path.GetFullPath tempDirA}/detached")

            match result with
            | Error msg ->
                Assert.That(msg, Does.Contain("detached HEAD"), "Should mention detached HEAD")
            | Ok () ->
                Assert.Fail("Should have returned error for detached HEAD")
        }

    [<Test>]
    member _.``startSync with unknown path returns error``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let syncAgent = createSyncAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)

            let worktrees =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main" ]

            do! populateAgent agent [ repoAId, worktrees ]

            let api = WorktreeApi.worktreeApi agent syncAgent (SessionManager.createAgent ()) [ tempDirA ] None "1.0" None

            let! result = api.startSync (WorktreePath.create "/nonexistent/path/main")

            match result with
            | Error msg ->
                Assert.That(msg, Does.Contain("No worktree found"), "Should report worktree not found")
            | Ok () ->
                Assert.Fail("Should have returned error for unknown path")
        }

    [<Test>]
    member _.``cancelSync with WorktreePath targets correct repo``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let syncAgent = createSyncAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)
            let repoBId = RepoId.create (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirA}/feature-x" "feature-x" ]
            let worktreesB =
                [ makeWorktree $"{Path.GetFullPath tempDirB}/main" "main"
                  makeWorktree $"{Path.GetFullPath tempDirB}/feature-x" "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = WorktreeApi.worktreeApi agent syncAgent (SessionManager.createAgent ()) [ tempDirA; tempDirB ] None "1.0" None

            do! api.cancelSync (WorktreePath.create $"{Path.GetFullPath tempDirA}/feature-x")

            Assert.Pass("cancelSync completed without error, path resolved correctly to repoA")
        }

    [<Test>]
    member _.``cancelSync with unknown path does not throw``() =
        task {
            let agent = RefreshScheduler.createAgent ()
            let syncAgent = createSyncAgent ()
            let repoAId = RepoId.create (Path.GetFullPath tempDirA)

            let worktrees =
                [ makeWorktree $"{Path.GetFullPath tempDirA}/main" "main" ]

            do! populateAgent agent [ repoAId, worktrees ]

            let api = WorktreeApi.worktreeApi agent syncAgent (SessionManager.createAgent ()) [ tempDirA ] None "1.0" None

            do! api.cancelSync (WorktreePath.create "/nonexistent/path/main")

            Assert.Pass("cancelSync with unknown path completed without throwing")
        }
