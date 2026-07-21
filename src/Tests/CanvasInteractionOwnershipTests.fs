module Tests.CanvasInteractionOwnershipTests

open System
open System.IO
open NUnit.Framework
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
    member _.``successful worktree deletion removes persisted owners and pending claims``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath
            runAsync (store.Assign(worktree, "diff.html", "session-a"))
            Assert.That(runAsync (store.BeginClaim(worktree, "beads.html")), Is.EqualTo(None: string option))

            let result =
                Server.WorktreeApi.removeWorktreeAndOwnership
                    (fun _ _ _ -> async { return Ok () })
                    store.RemoveWorktree
                    dir
                    worktree
                    (Some "feature")
                |> runAsync

            assertOk result "Worktree deletion should succeed"
            let restarted = createStore filePath
            Assert.That(runAsync (restarted.GetOwner(worktree, "diff.html")), Is.EqualTo(None: string option))
            Assert.That(store.ClaimPending(worktree, "session-b"), Is.Empty))

    [<Test>]
    member _.``failed worktree deletion preserves persisted owners and pending claims``() =
        withOwnershipFile (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = createStore filePath
            runAsync (store.Assign(worktree, "diff.html", "session-a"))
            Assert.That(runAsync (store.BeginClaim(worktree, "beads.html")), Is.EqualTo(None: string option))

            let result =
                Server.WorktreeApi.removeWorktreeAndOwnership
                    (fun _ _ _ -> async { return Error "remove failed" })
                    store.RemoveWorktree
                    dir
                    worktree
                    (Some "feature")
                |> runAsync

            match result with
            | Error "remove failed" -> ()
            | other -> Assert.Fail($"Expected deletion failure but got: {other}")

            let restarted = createStore filePath
            Assert.That(runAsync (restarted.GetOwner(worktree, "diff.html")), Is.EqualTo(Some "session-a"))
            Assert.That(store.ClaimPending(worktree, "session-b"), Is.EqualTo([ "beads.html" ])))

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
