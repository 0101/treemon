module Tests.CanvasInteractionOwnershipTests

open System
open System.IO
open NUnit.Framework
open Shared
open Server.CanvasInteractionOwnership
open Tests.TestUtils

let private withOwnershipFile action =
    let dir = Path.Combine(Path.GetTempPath(), $"treemon-interaction-owners-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore

    try
        action dir (Path.Combine(dir, "owners.json"))
    finally
        try Directory.Delete(dir, recursive = true)
        with _ -> ()

let private createDeleteContext repoRoot worktree =
    let agent = Server.RefreshScheduler.createAgent ()
    let repoId = Server.PathUtils.toRepoId repoRoot
    let worktreeInfo : Server.GitWorktree.WorktreeInfo =
        { Path = Server.PathUtils.normalizePath worktree
          Head = "abc123"
          Branch = Some "feature" }
    agent.Post(Server.RefreshScheduler.UpdateWorktreeList(repoId, [ worktreeInfo ]))
    agent, repoId, worktreeInfo, Server.RefreshScheduler.buildRootPaths [ repoRoot ]

let private seedDiffIdentity
    (store: Server.WorktreeDiffApi.DiffIdentityStore)
    worktree
    =
    let viewer = Guid.NewGuid()
    let identity = "issued-before-delete"

    let entry: Server.WorktreeDiff.WorktreeDiffEntry =
        { Path = "changed.txt"
          OldPath = None
          Status = Server.WorktreeDiff.Modified }

    let file: DiffFileSummary =
        { Identity = identity
          DisplayPath = entry.Path
          OldDisplayPath = None
          Change = DiffChangeKind.Modified }

    runAsync (
        store.Replace(
            worktree,
            viewer,
            "merge-base",
            [ file, entry ]
        )
    )

    viewer, identity

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PersistenceTests() =

    [<Test>]
    member _.``ownership survives store restart and explicit reassignment``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let first = createStore filePath

            runAsync (first.Assign(worktree, "DIFF.HTML", "session-a"))
            Assert.That(runAsync (first.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a"))

            let restarted = createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(worktree + string Path.DirectorySeparatorChar, "Diff.Html")),
                Is.EqualTo(Some "session-a"),
                "Normalized worktree and filename keys must survive restart")

            runAsync (restarted.Assign(worktree, "diff.html", "session-b"))

            let restartedAgain = createStore filePath
            Assert.That(
                runAsync (restartedAgain.GetOwner(worktree, "diff.html")),
                Is.EqualTo(Some "session-b"),
                "Explicit reassignment must replace and persist the prior interaction owner"))

    [<Test>]
    member _.``first identified session claims pending view and later sessions cannot steal it``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath

            Assert.That(runAsync (store.BeginClaim(worktree, "diff.html")), Is.EqualTo(None: string option))
            Assert.That(store.ClaimPending(worktree, "session-a"), Is.EqualTo([ "diff.html" ]))
            Assert.That(runAsync (store.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a"))

            Assert.That(
                runAsync (store.BeginClaim(worktree, "diff.html")),
                Is.EqualTo(Some "session-a"),
                "An owned view must not become pending again")
            Assert.That(store.ClaimPending(worktree, "session-b"), Is.Empty)
            Assert.That(runAsync (store.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a")))

    [<Test>]
    member _.``cancelled launch removes pending claim without assigning an owner``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath

            Assert.That(runAsync (store.BeginClaim(worktree, "diff.html")), Is.EqualTo(None: string option))
            runAsync (store.CancelClaim(worktree, "diff.html"))

            Assert.That(store.ClaimPending(worktree, "unrelated-session"), Is.Empty)
            Assert.That(runAsync (store.GetOwner(worktree, "diff.html")), Is.EqualTo(None: string option)))

    [<Test>]
    member _.``pending reassignment preserves the old owner until a new session claims atomically``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath
            runAsync (store.Assign(worktree, "diff.html", "session-a"))

            let reassignment =
                match runAsync (store.BeginReassignment(worktree, "diff.html")) with
                | Ok value -> value
                | Error err -> failwith err

            Assert.That(
                runAsync (store.GetOwner(worktree, "diff.html")),
                Is.EqualTo(Some "session-a"),
                "Starting recovery must preserve affinity until the replacement registers")

            Assert.That(store.ClaimPending(worktree, "session-b"), Is.EqualTo([ "diff.html" ]))
            Assert.That(runAsync (store.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-b"))

            runAsync (store.CancelReassignment(worktree, "diff.html", reassignment.Token))
            let restarted = createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(worktree, "diff.html")),
                Is.EqualTo(Some "session-b"),
                "The claim must persist the replacement owner before queue delivery"))

    [<Test>]
    member _.``failed reassignment can be cancelled and retried without losing the old owner``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath
            runAsync (store.Assign(worktree, "diff.html", "session-a"))

            let first =
                match runAsync (store.BeginReassignment(worktree, "diff.html")) with
                | Ok value -> value
                | Error err -> failwith err

            runAsync (store.CancelReassignment(worktree, "diff.html", first.Token))
            Assert.That(runAsync (store.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a"))

            let retry = runAsync (store.BeginReassignment(worktree, "diff.html"))
            Assert.That(Result.isOk retry, Is.True, "A failed start-fresh attempt must be retryable")
            Assert.That(runAsync (store.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a")))

    [<Test>]
    member _.``resume failure and timeout surface recovery while a retry can succeed``() =
        let spawnFailure =
            Server.WorktreeApi.resumeSystemViewOwnerWith
                (fun () -> async { return Error "resume rejected" })
                (fun () -> async { return true })
                "diff.html"
            |> runAsync

        let registrationTimeout =
            Server.WorktreeApi.resumeSystemViewOwnerWith
                (fun () -> async { return Ok () })
                (fun () -> async { return false })
                "diff.html"
            |> runAsync

        let retry =
            Server.WorktreeApi.resumeSystemViewOwnerWith
                (fun () -> async { return Ok () })
                (fun () -> async { return true })
                "diff.html"
            |> runAsync

        match spawnFailure, registrationTimeout with
        | CanvasMessageResult.OwnerUnavailable _, CanvasMessageResult.OwnerUnavailable _ -> ()
        | other -> Assert.Fail($"Expected recoverable owner failures, got {other}")

        Assert.That(retry, Is.EqualTo(CanvasMessageResult.Queued))

    [<Test>]
    member _.``start fresh rolls back its pending claim on launch failure and persists replacement on retry``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath
            runAsync (store.Assign(worktree, "diff.html", "session-a"))

            let run launch waitForReplacement =
                Server.WorktreeApi.startFreshSystemViewWith
                    (fun () -> store.BeginReassignment(worktree, "diff.html"))
                    (fun token -> store.CancelReassignment(worktree, "diff.html", token))
                    launch
                    waitForReplacement
                    "diff.html"
                |> runAsync

            let failed =
                run
                    (fun () -> async { return Error "terminal failed" })
                    (fun _ -> async { return None })

            match failed with
            | Error "Could not start a fresh interaction session for diff.html: terminal failed" -> ()
            | other -> Assert.Fail($"Expected the launch failure to surface, got {other}")
            Assert.That(runAsync (store.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a"))

            let succeeded =
                run
                    (fun () -> async { return Ok () })
                    (fun _ ->
                        async {
                            Assert.That(store.ClaimPending(worktree, "session-b"), Is.EqualTo([ "diff.html" ]))
                            return! store.GetOwner(worktree, "diff.html")
                        })

            match succeeded with
            | Ok () -> ()
            | Error err -> Assert.Fail($"Expected start-fresh retry to succeed, got {err}")
            let restarted = createStore filePath
            Assert.That(runAsync (restarted.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-b")))

    [<Test>]
    member _.``view and worktree cleanup remove persisted interaction ownership``() =
        withOwnershipFile (fun dir filePath ->
            let firstWorktree = Path.Combine(dir, "first")
            let secondWorktree = Path.Combine(dir, "second")
            let store = createStore filePath

            runAsync (store.Assign(firstWorktree, "diff.html", "session-a"))
            runAsync (store.Assign(firstWorktree, "beads.html", "session-a"))
            runAsync (store.Assign(secondWorktree, "diff.html", "session-b"))

            runAsync (store.RemoveView(firstWorktree, "diff.html"))
            Assert.That(runAsync (store.GetOwner(firstWorktree, "diff.html")), Is.EqualTo(None: string option))
            Assert.That(runAsync (store.GetOwner(firstWorktree, "beads.html")), Is.EqualTo(Some "session-a"))

            runAsync (store.RemoveWorktree(firstWorktree))
            let restarted = createStore filePath
            Assert.That(runAsync (restarted.GetOwner(firstWorktree, "beads.html")), Is.EqualTo(None: string option))
            Assert.That(runAsync (restarted.GetOwner(secondWorktree, "diff.html")), Is.EqualTo(Some "session-b")))

    [<Test>]
    member _.``successful worktree deletion removes scheduler ownership and diff identity state``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath
            let diffStore = Server.WorktreeDiffApi.createIdentityStore ()
            let agent, repoId, _, rootPaths = createDeleteContext dir worktree
            let viewer, identity = seedDiffIdentity diffStore worktree
            runAsync (store.Assign(worktree, "diff.html", "session-a"))
            Assert.That(runAsync (store.BeginClaim(worktree, "beads.html")), Is.EqualTo(None: string option))

            let removeWorktreeState path =
                async {
                    do! store.RemoveWorktree path
                    do! diffStore.RemoveWorktree path
                }

            let result =
                Server.WorktreeApi.deleteWorktreeWith
                    (fun _ _ _ -> async { return Ok () })
                    removeWorktreeState
                    agent
                    rootPaths
                    (Server.PathUtils.toWorktreePath worktree)
                |> runAsync

            assertOk result "Worktree deletion should succeed"
            let state = runAsync (agent.PostAndAsyncReply(Server.RefreshScheduler.GetState))
            Assert.That(state.Repos[repoId].WorktreeList, Is.Empty)
            let restarted = createStore filePath
            Assert.That(runAsync (restarted.GetOwner(worktree, "diff.html")), Is.EqualTo(None: string option))
            Assert.That(store.ClaimPending(worktree, "session-b"), Is.Empty)
            Assert.That(
                runAsync (diffStore.Resolve(worktree, viewer, identity)),
                Is.EqualTo(
                    None:
                        (string
                         * DiffFileSummary
                         * Server.WorktreeDiff.WorktreeDiffEntry) option
                )
            ))

    [<Test>]
    member _.``failed worktree deletion preserves scheduler ownership and diff identity state``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath
            let diffStore = Server.WorktreeDiffApi.createIdentityStore ()
            let agent, repoId, worktreeInfo, rootPaths = createDeleteContext dir worktree
            let viewer, identity = seedDiffIdentity diffStore worktree
            runAsync (store.Assign(worktree, "diff.html", "session-a"))
            Assert.That(runAsync (store.BeginClaim(worktree, "beads.html")), Is.EqualTo(None: string option))

            let removeWorktreeState path =
                async {
                    do! store.RemoveWorktree path
                    do! diffStore.RemoveWorktree path
                }

            let result =
                Server.WorktreeApi.deleteWorktreeWith
                    (fun _ _ _ -> async { return Error "remove failed" })
                    removeWorktreeState
                    agent
                    rootPaths
                    (Server.PathUtils.toWorktreePath worktree)
                |> runAsync

            match result with
            | Error "remove failed" -> ()
            | other -> Assert.Fail($"Expected deletion failure but got: {other}")

            let state = runAsync (agent.PostAndAsyncReply(Server.RefreshScheduler.GetState))
            Assert.That(state.Repos[repoId].WorktreeList, Is.EqualTo([ worktreeInfo ]))
            let restarted = createStore filePath
            Assert.That(runAsync (restarted.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a"))
            Assert.That(store.ClaimPending(worktree, "session-b"), Is.EqualTo([ "beads.html" ]))
            Assert.That(
                runAsync (diffStore.Resolve(worktree, viewer, identity))
                |> Option.map (fun (mergeBase, file, entry) ->
                    mergeBase,
                    file.Identity,
                    entry.Path),
                Is.EqualTo(Some("merge-base", identity, "changed.txt"))
            ))

    [<Test>]
    member _.``startup prune removes missing views and worktrees while preserving existing views``() =
        withOwnershipFile (fun dir filePath ->
            let knownWorktree = Path.Combine(dir, "known")
            let removedWorktree = Path.Combine(dir, "removed")
            let canvasDir = Path.Combine(knownWorktree, ".agents", "canvas")
            Directory.CreateDirectory(canvasDir) |> ignore
            File.WriteAllText(Path.Combine(canvasDir, "diff.html"), "<html></html>")

            let store = createStore filePath
            runAsync (store.Assign(knownWorktree, "diff.html", "session-a"))
            runAsync (store.Assign(knownWorktree, "beads.html", "session-a"))
            runAsync (store.Assign(removedWorktree, "diff.html", "session-b"))

            runAsync (store.Prune(Set.singleton knownWorktree))

            let restarted = createStore filePath
            Assert.That(runAsync (restarted.GetOwner(knownWorktree, "diff.html")), Is.EqualTo(Some "session-a"))
            Assert.That(runAsync (restarted.GetOwner(knownWorktree, "beads.html")), Is.EqualTo(None: string option))
            Assert.That(runAsync (restarted.GetOwner(removedWorktree, "diff.html")), Is.EqualTo(None: string option)))

    [<Test>]
    member _.``prune preserves an existing view shared by owner and pending state``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let canvasDir = Path.Combine(worktree, ".agents", "canvas")
            let viewPath = Path.Combine(canvasDir, "diff.html")
            Directory.CreateDirectory(canvasDir) |> ignore
            File.WriteAllText(viewPath, "<html></html>")

            let store = createStore filePath

            runAsync (store.Assign(worktree, "diff.html", "session-a"))
            Assert.That(Result.isOk (runAsync (store.BeginReassignment(worktree, "diff.html"))), Is.True)

            runAsync (store.Prune(Set.singleton worktree))

            Assert.That(runAsync (store.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a"))
            Assert.That(store.ClaimPending(worktree, "session-b"), Is.EqualTo([ "diff.html" ])))

    [<Test>]
    member _.``prune removes a pending claim when its view is missing``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath

            Assert.That(runAsync (store.BeginClaim(worktree, "diff.html")), Is.EqualTo(None: string option))
            runAsync (store.Prune(Set.singleton worktree))

            Assert.That(store.ClaimPending(worktree, "session-a"), Is.Empty))
