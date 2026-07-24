module Tests.CanvasDocOwnershipTests

open System
open System.IO
open NUnit.Framework
open Server
open Tests.TestUtils

let private withOwnershipFiles action =
    let dir = Path.Combine(Path.GetTempPath(), $"treemon-canvas-owners-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore

    try
        action
            dir
            (Path.Combine(dir, "canvas-owners.json"))
    finally
        try Directory.Delete(dir, recursive = true)
        with _ -> ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PersistenceTests() =

    [<Test>]
    member _.``filename case persists while worktree paths stay normalized``() =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = CanvasDocOwnership.createStore filePath

            runAsync (store.Assign(Path.Combine(worktree, "."), "Review.html", "agent-session"))
            runAsync (store.Assign(worktree, "diff.html", "system-session"))

            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(worktree, "Review.html")),
                Is.EqualTo(Some "agent-session"))
            Assert.That(
                runAsync (restarted.GetOwner(worktree, "diff.html")),
                Is.EqualTo(Some "system-session"))
            Assert.That(
                runAsync (restarted.GetOwner(worktree, "review.html")),
                Is.EqualTo(None: string option),
                "Filename identity must retain the on-disk casing")
            Assert.That(
                runAsync (restarted.GetAll(worktree)) |> Map.keys,
                Is.EquivalentTo([ "Review.html"; "diff.html" ])))

    [<Test>]
    member _.``conditional assignment replaces only the expected owner``() =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = CanvasDocOwnership.createStore filePath

            runAsync (store.Assign(worktree, "Review.html", "author-a"))

            let replaced =
                store.AssignIfCurrentOwner(
                    worktree,
                    "Review.html",
                    Some "author-a",
                    "replacement")
                |> runAsync

            let staleReplacement =
                store.AssignIfCurrentOwner(
                    worktree,
                    "Review.html",
                    Some "author-a",
                    "stale-replacement")
                |> runAsync

            Assert.That(replaced, Is.True)
            Assert.That(staleReplacement, Is.False)
            Assert.That(
                runAsync (store.GetOwner(worktree, "Review.html")),
                Is.EqualTo(Some "replacement")))

    [<Test>]
    member _.``view worktree and prune cleanup persist for both document kinds``() =
        withOwnershipFiles (fun dir filePath ->
            let knownWorktree = Path.Combine(dir, "known")
            let removedWorktree = Path.Combine(dir, "removed")
            let canvasDir = Path.Combine(knownWorktree, ".agents", "canvas")
            Directory.CreateDirectory(canvasDir) |> ignore
            File.WriteAllText(Path.Combine(canvasDir, "diff.html"), "<html></html>")

            let store = CanvasDocOwnership.createStore filePath
            runAsync (store.Assign(knownWorktree, "report.html", "agent-session"))
            runAsync (store.Assign(knownWorktree, "diff.html", "system-session"))
            runAsync (store.Assign(knownWorktree, "beads.html", "missing-system-session"))
            runAsync (store.Assign(removedWorktree, "diff.html", "removed-session"))

            runAsync (store.RemoveView(knownWorktree, "report.html"))
            runAsync (store.Prune(Set.singleton knownWorktree))

            let pruned = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (pruned.GetOwner(knownWorktree, "diff.html")),
                Is.EqualTo(Some "system-session"))
            Assert.That(
                runAsync (pruned.GetOwner(knownWorktree, "report.html")),
                Is.EqualTo(None: string option))
            Assert.That(
                runAsync (pruned.GetOwner(knownWorktree, "beads.html")),
                Is.EqualTo(None: string option))
            Assert.That(
                runAsync (pruned.GetOwner(removedWorktree, "diff.html")),
                Is.EqualTo(None: string option))

            runAsync (pruned.RemoveWorktree(knownWorktree))
            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(knownWorktree, "diff.html")),
                Is.EqualTo(None: string option)))
