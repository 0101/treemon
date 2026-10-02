module Tests.CanvasDocOwnershipTests

open System
open System.IO
open System.Text.Json.Nodes
open NUnit.Framework
open Server
open Server.SessionActivity
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

let private sessionId value = SessionId value

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PersistenceTests() =

    [<Test>]
    member _.``Invalid persisted owners are dropped so fallback attribution can repair them``() =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let views = JsonObject()
            views["report.html"] <- JsonValue.Create("bad session")
            let root = JsonObject()
            root[worktree] <- views
            File.WriteAllText(filePath, root.ToJsonString())

            let store = CanvasDocOwnership.createStore filePath
            Assert.That(runAsync (store.GetOwner(worktree, "report.html")), Is.EqualTo(None: SessionId option))

            requirePersisted (store.Attribute(worktree, "report.html", sessionId "recovered-owner"))

            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(worktree, "report.html")),
                Is.EqualTo(Some(sessionId "recovered-owner"))))

    [<Test>]
    member _.``Failed assignment preserves memory and disk and the identical retry succeeds``() =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = CanvasDocOwnership.createStore filePath
            Assert.That(runAsync (store.Assign(worktree, "Review.html", sessionId "old-owner")), Is.EqualTo(Ok(): Result<unit, CanvasDocOwnership.PersistenceFailure>))
            let previous = File.ReadAllText filePath
            let blocked = filePath + ".tmp"
            Directory.CreateDirectory blocked |> ignore
            let failed = runAsync (store.Assign(worktree, "Review.html", sessionId "new-owner"))
            Assert.Multiple(fun () ->
                Assert.That(failed, Is.EqualTo(Error CanvasDocOwnership.PersistenceFailure.SaveFailed: Result<unit, CanvasDocOwnership.PersistenceFailure>))
                Assert.That(runAsync (store.GetOwner(worktree, "Review.html")), Is.EqualTo(Some(sessionId "old-owner")))
                Assert.That(File.ReadAllText filePath, Is.EqualTo previous))
            Directory.Delete blocked
            Assert.That(runAsync (store.Assign(worktree, "Review.html", sessionId "new-owner")), Is.EqualTo(Ok(): Result<unit, CanvasDocOwnership.PersistenceFailure>))
            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(runAsync (restarted.GetOwner(worktree, "Review.html")), Is.EqualTo(Some(sessionId "new-owner"))))

    [<TestCase("view")>]
    [<TestCase("worktree")>]
    [<TestCase("prune")>]
    member _.``Failed removal or prune retains the prior durable owner``(operation: string) =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = CanvasDocOwnership.createStore filePath
            runAsync (store.Assign(worktree, "report.html", sessionId "owner"))
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
            Assert.That(runAsync (store.GetOwner(worktree, "report.html")), Is.EqualTo(Some(sessionId "owner")))
            Assert.That(File.ReadAllText filePath, Is.EqualTo previous)
            Directory.Delete(filePath + ".tmp")
            Assert.That(runAsync (remove ()), Is.EqualTo(Ok(): Result<unit, CanvasDocOwnership.PersistenceFailure>))
            Assert.That(runAsync ((CanvasDocOwnership.createStore filePath).GetOwner(worktree, "report.html")), Is.EqualTo(None: SessionId option)))

    [<Test>]
    member _.``filename case persists while worktree paths stay normalized``() =
        withOwnershipFiles (fun dir filePath ->
            let worktree = Path.Combine(dir, "worktree")
            let store = CanvasDocOwnership.createStore filePath

            requirePersisted (store.Assign(Path.Combine(worktree, "."), "Review.html", sessionId "agent-session"))
            requirePersisted (store.Assign(worktree, "diff.html", sessionId "system-session"))

            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(worktree, "Review.html")),
                Is.EqualTo(Some(sessionId "agent-session")))
            Assert.That(
                runAsync (restarted.GetOwner(worktree, "diff.html")),
                Is.EqualTo(Some(sessionId "system-session")))
            Assert.That(
                runAsync (restarted.GetOwner(worktree, "review.html")),
                Is.EqualTo(None: SessionId option),
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
            requirePersisted (store.Assign(knownWorktree, "notes.html", sessionId "author"))
            requirePersisted (store.Assign(knownWorktree, "deleted.html", sessionId "stale-author"))
            requirePersisted (store.Assign(removedWorktree, "notes.html", sessionId "removed-session"))

            requirePersisted (store.Prune(Set.singleton knownWorktree))

            let pruned = CanvasDocOwnership.createStore filePath

            Assert.Multiple(fun () ->
                Assert.That(
                    runAsync (pruned.GetOwner(knownWorktree, "notes.html")),
                    Is.EqualTo(Some(sessionId "author")),
                    "An existing document keeps its author")
                Assert.That(
                    runAsync (pruned.GetOwner(knownWorktree, "deleted.html")),
                    Is.EqualTo(None: SessionId option),
                    "A deleted document releases its entry — the only per-document reclaim path")
                Assert.That(
                    runAsync (pruned.GetOwner(removedWorktree, "notes.html")),
                    Is.EqualTo(None: SessionId option),
                    "An unknown worktree is pruned entirely"))

            requirePersisted (pruned.RemoveWorktree(knownWorktree))
            let restarted = CanvasDocOwnership.createStore filePath
            Assert.That(
                runAsync (restarted.GetOwner(knownWorktree, "notes.html")),
                Is.EqualTo(None: SessionId option)))
