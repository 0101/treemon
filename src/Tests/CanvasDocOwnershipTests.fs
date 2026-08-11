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
    member _.``prune drops unknown worktrees and documents whose file is gone``() =
        withOwnershipFiles (fun dir filePath ->
            let knownWorktree = Path.Combine(dir, "known")
            let removedWorktree = Path.Combine(dir, "removed")
            let canvasDir = Path.Combine(knownWorktree, ".agents", "canvas")
            Directory.CreateDirectory(canvasDir) |> ignore
            File.WriteAllText(Path.Combine(canvasDir, "notes.html"), "<html></html>")

            let store = CanvasDocOwnership.createStore filePath
            runAsync (store.Assign(knownWorktree, "notes.html", "author"))
            runAsync (store.Assign(knownWorktree, "deleted.html", "stale-author"))
            runAsync (store.Assign(removedWorktree, "notes.html", "removed-session"))

            runAsync (store.Prune(Set.singleton knownWorktree))

            let pruned = CanvasDocOwnership.createStore filePath

            Assert.Multiple(fun () ->
                Assert.That(
                    runAsync (pruned.GetOwner(knownWorktree, "notes.html")),
                    Is.EqualTo(Some "author"),
                    "An existing document keeps its author")
                Assert.That(
                    runAsync (pruned.GetOwner(knownWorktree, "deleted.html")),
                    Is.EqualTo(None: string option),
                    "A deleted document releases its entry — the only per-document reclaim path")
                Assert.That(
                    runAsync (pruned.GetOwner(removedWorktree, "notes.html")),
                    Is.EqualTo(None: string option),
                    "An unknown worktree is pruned entirely"))

            runAsync (pruned.RemoveWorktree(knownWorktree))
            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(knownWorktree, "notes.html")),
                Is.EqualTo(None: string option)))
