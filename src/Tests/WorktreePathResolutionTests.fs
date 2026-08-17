module Tests.WorktreePathResolutionTests

open System
open System.IO
open NUnit.Framework
open Server
open Server.RefreshScheduler
open Server.SchedulerState
open Server.GitWorktree
open Shared

let private normPath = Server.PathUtils.normalizePath

let private worktreePath root name =
    Path.Combine(Path.GetFullPath root, name) |> normPath

let private makeWorktree path branch : WorktreeInfo =
    { Path = normPath path; Head = "abc123"; Branch = Some branch }

let private makeDetachedWorktree path : WorktreeInfo =
    { Path = normPath path; Head = "abc123"; Branch = None }

let private getAgentState (agent: MailboxProcessor<StateMsg>) =
    agent.PostAndAsyncReply(GetState)

let private populateAgent (agent: MailboxProcessor<StateMsg>) (repos: (RepoId * WorktreeInfo list) list) =
    async {
        repos
        |> List.iter (fun (repoId, worktrees) ->
            agent.Post(UpdateWorktreeList(repoId, worktrees)))
        do! getAgentState agent |> Async.Ignore
    }

let private createApi agent roots =
    WorktreeApi.worktreeApi
        { Agent = agent
          CardLog = CardEventLog.createAgent ()
          SessionAgent = SessionManager.createAgent ()
          EmbeddedTerminal = EmbeddedTerminal.create ()
          ActivityStore = None
          SnapshotStore = None
          AutoSyncStore = None
          WorktreeRoots = roots
          TestFixtures = None
          AppVersion = "1.0"
          DeployBranch = None }

let private deleteWorktree agent worktreeRoots wtPath =
    WorktreeApi.deleteWorktreeWith
        (fun _ _ _ -> async { return Ok () })
        (fun _ -> async { return () })
        agent
        (RefreshScheduler.buildRootPaths worktreeRoots)
        wtPath

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DeleteWorktreeResolutionTests() =

    // NUnit setup assigns fresh directories for each test.
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
            let agent = SchedulerState.createAgent ()
            let repoAId = PathUtils.toRepoId (Path.GetFullPath tempDirA)
            let repoBId = PathUtils.toRepoId (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree (worktreePath tempDirA "main") "main"
                  makeWorktree (worktreePath tempDirA "feature-x") "feature-x" ]
            let worktreesB =
                [ makeWorktree (worktreePath tempDirB "main") "main"
                  makeWorktree (worktreePath tempDirB "feature-x") "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let targetPath = worktreePath tempDirA "feature-x"
            let! _result = deleteWorktree agent [ tempDirA; tempDirB ] (PathUtils.toWorktreePath targetPath)

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
                Does.Contain(worktreePath tempDirB "feature-x"),
                "RepoB's feature-x should NOT be affected")

            Assert.That(
                repoBWorktrees,
                Does.Contain(worktreePath tempDirB "main"),
                "RepoB's main should NOT be affected")
        }

    [<Test>]
    member _.``deleteWorktree with WorktreePath for repoB does not affect repoA``() =
        task {
            let agent = SchedulerState.createAgent ()
            let repoAId = PathUtils.toRepoId (Path.GetFullPath tempDirA)
            let repoBId = PathUtils.toRepoId (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree (worktreePath tempDirA "main") "main"
                  makeWorktree (worktreePath tempDirA "feature-x") "feature-x" ]
            let worktreesB =
                [ makeWorktree (worktreePath tempDirB "main") "main"
                  makeWorktree (worktreePath tempDirB "feature-x") "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let targetPath = worktreePath tempDirB "main"
            let! _result = deleteWorktree agent [ tempDirA; tempDirB ] (PathUtils.toWorktreePath targetPath)

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
                Does.Contain(worktreePath tempDirA "main"),
                "RepoA's main should NOT be affected")
        }

    [<Test>]
    member _.``deleteWorktree with unknown path returns error``() =
        task {
            let agent = SchedulerState.createAgent ()
            let repoAId = PathUtils.toRepoId (Path.GetFullPath tempDirA)

            let worktreesA =
                [ makeWorktree (worktreePath tempDirA "main") "main" ]

            do! populateAgent agent [ repoAId, worktreesA ]

            let unknownPath = worktreePath tempDirA "missing"
            let! result = deleteWorktree agent [ tempDirA ] (PathUtils.toWorktreePath unknownPath)

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

    // NUnit setup assigns fresh directories for each test.
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
            let agent = SchedulerState.createAgent ()
            let repoAId = PathUtils.toRepoId (Path.GetFullPath tempDirA)
            let repoBId = PathUtils.toRepoId (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree (worktreePath tempDirA "main") "main"
                  makeWorktree (worktreePath tempDirA "feature-x") "feature-x" ]
            let worktreesB =
                [ makeWorktree (worktreePath tempDirB "main") "main"
                  makeWorktree (worktreePath tempDirB "feature-x") "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = createApi agent [ tempDirA; tempDirB ]

            let! result =
                api.archiveWorktree (PathUtils.toWorktreePath (worktreePath tempDirA "feature-x"))

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
            let agent = SchedulerState.createAgent ()
            let repoAId = PathUtils.toRepoId (Path.GetFullPath tempDirA)
            let repoBId = PathUtils.toRepoId (Path.GetFullPath tempDirB)

            let worktreesA =
                [ makeWorktree (worktreePath tempDirA "main") "main"
                  makeWorktree (worktreePath tempDirA "feature-x") "feature-x" ]
            let worktreesB =
                [ makeWorktree (worktreePath tempDirB "main") "main"
                  makeWorktree (worktreePath tempDirB "feature-x") "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = createApi agent [ tempDirA; tempDirB ]

            let! result =
                api.archiveWorktree (PathUtils.toWorktreePath (worktreePath tempDirB "main"))

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
            let agent = SchedulerState.createAgent ()
            let repoAId = PathUtils.toRepoId (Path.GetFullPath tempDirA)
            let repoBId = PathUtils.toRepoId (Path.GetFullPath tempDirB)

            TreemonConfig.setArchivedBranches tempDirA [ "feature-x" ]
            TreemonConfig.setArchivedBranches tempDirB [ "feature-x" ]

            let worktreesA =
                [ makeWorktree (worktreePath tempDirA "main") "main"
                  makeWorktree (worktreePath tempDirA "feature-x") "feature-x" ]
            let worktreesB =
                [ makeWorktree (worktreePath tempDirB "main") "main"
                  makeWorktree (worktreePath tempDirB "feature-x") "feature-x" ]

            do! populateAgent agent [ repoAId, worktreesA; repoBId, worktreesB ]

            let api = createApi agent [ tempDirA; tempDirB ]

            let! result =
                api.unarchiveWorktree (PathUtils.toWorktreePath (worktreePath tempDirA "feature-x"))

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
            let agent = SchedulerState.createAgent ()
            let repoAId = PathUtils.toRepoId (Path.GetFullPath tempDirA)

            let worktreesA =
                [ makeDetachedWorktree (worktreePath tempDirA "detached") ]

            do! populateAgent agent [ repoAId, worktreesA ]

            let api = createApi agent [ tempDirA ]

            let! result =
                api.archiveWorktree (PathUtils.toWorktreePath (worktreePath tempDirA "detached"))

            match result with
            | Error msg ->
                Assert.That(msg, Does.Contain("detached HEAD"), "Should mention detached HEAD")
            | Ok () ->
                Assert.Fail("Should have returned error for detached HEAD worktree")
        }
