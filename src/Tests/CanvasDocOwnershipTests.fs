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

let private requirePersisted operation =
    runAsync operation |> Result.defaultWith (fun _ -> failwith "ownership persistence failed")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PersistenceTests() =

    [<Test>]
    member _.``Failed assignment preserves memory and disk and the identical retry succeeds``() =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = CanvasDocOwnership.createStore filePath
            Assert.That(runAsync (store.Assign(worktree, "Review.html", "old-owner")), Is.EqualTo(Ok(): Result<unit, CanvasDocOwnership.PersistenceFailure>))
            let previous = File.ReadAllText filePath
            let blocked = filePath + ".tmp"
            Directory.CreateDirectory blocked |> ignore
            let failed = runAsync (store.Assign(worktree, "Review.html", "new-owner"))
            Assert.Multiple(fun () ->
                Assert.That(failed, Is.EqualTo(Error CanvasDocOwnership.PersistenceFailure.SaveFailed: Result<unit, CanvasDocOwnership.PersistenceFailure>))
                Assert.That(runAsync (store.GetOwner(worktree, "Review.html")), Is.EqualTo(Some "old-owner"))
                Assert.That(File.ReadAllText filePath, Is.EqualTo previous))
            Directory.Delete blocked
            Assert.That(runAsync (store.Assign(worktree, "Review.html", "new-owner")), Is.EqualTo(Ok(): Result<unit, CanvasDocOwnership.PersistenceFailure>))
            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(runAsync (restarted.GetOwner(worktree, "Review.html")), Is.EqualTo(Some "new-owner")))

    [<TestCase("view")>]
    [<TestCase("worktree")>]
    [<TestCase("prune")>]
    member _.``Failed removal or prune retains the prior durable owner``(operation: string) =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = CanvasDocOwnership.createStore filePath
            runAsync (store.Assign(worktree, "report.html", "owner"))
            |> Result.defaultWith (fun _ -> failwith "initial owner save failed")
            let previous = File.ReadAllText filePath
            Directory.CreateDirectory(filePath + ".tmp") |> ignore
            let remove () =
                match operation with
                | "view" -> store.RemoveView(worktree, "report.html")
                | "worktree" -> store.RemoveWorktree worktree
                | "prune" -> store.Prune Set.empty
                | _ -> failwith "unknown removal scenario"
            Assert.That(runAsync (remove ()), Is.EqualTo(Error CanvasDocOwnership.PersistenceFailure.SaveFailed: Result<unit, CanvasDocOwnership.PersistenceFailure>))
            Assert.That(runAsync (store.GetOwner(worktree, "report.html")), Is.EqualTo(Some "owner"))
            Assert.That(File.ReadAllText filePath, Is.EqualTo previous)
            Directory.Delete(filePath + ".tmp")
            Assert.That(runAsync (remove ()), Is.EqualTo(Ok(): Result<unit, CanvasDocOwnership.PersistenceFailure>))
            Assert.That(runAsync ((CanvasDocOwnership.createStore filePath).GetOwner(worktree, "report.html")), Is.EqualTo(None: string option)))

    [<Test>]
    member _.``filename case persists while worktree paths stay normalized``() =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = CanvasDocOwnership.createStore filePath

            requirePersisted (store.Assign(Path.Combine(worktree, "."), "Review.html", "agent-session"))
            requirePersisted (store.Assign(worktree, "diff.html", "system-session"))

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
            requirePersisted (store.Assign(knownWorktree, "notes.html", "author"))
            requirePersisted (store.Assign(knownWorktree, "deleted.html", "stale-author"))
            requirePersisted (store.Assign(removedWorktree, "notes.html", "removed-session"))

            requirePersisted (store.Prune(Set.singleton knownWorktree))

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

            requirePersisted (pruned.RemoveWorktree(knownWorktree))
            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(knownWorktree, "notes.html")),
                Is.EqualTo(None: string option)))
