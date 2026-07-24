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
    member _.``prune drops worktrees that are no longer known and keeps the rest``() =
        withOwnershipFiles (fun dir filePath ->
            let knownWorktree = Path.Combine(dir, "known")
            let removedWorktree = Path.Combine(dir, "removed")

            let store = CanvasDocOwnership.createStore filePath
            runAsync (store.Assign(knownWorktree, "report.html", "agent-session"))
            runAsync (store.Assign(knownWorktree, "notes.html", "other-session"))
            runAsync (store.Assign(removedWorktree, "report.html", "removed-session"))

            runAsync (store.RemoveView(knownWorktree, "report.html"))
            runAsync (store.Prune(Set.singleton knownWorktree))

            let pruned = CanvasDocOwnership.createStore filePath

            Assert.Multiple(fun () ->
                Assert.That(
                    runAsync (pruned.GetOwner(knownWorktree, "notes.html")),
                    Is.EqualTo(Some "other-session"),
                    "A known worktree's ownership survives pruning")
                Assert.That(
                    runAsync (pruned.GetOwner(knownWorktree, "report.html")),
                    Is.EqualTo(None: string option),
                    "RemoveView drops the entry it was given")
                Assert.That(
                    runAsync (pruned.GetOwner(removedWorktree, "report.html")),
                    Is.EqualTo(None: string option),
                    "An unknown worktree is pruned entirely"))

            runAsync (pruned.RemoveWorktree(knownWorktree))
            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(knownWorktree, "notes.html")),
                Is.EqualTo(None: string option)))
