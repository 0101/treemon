module Tests.CanvasDocOwnershipTests

open System
open System.IO
open System.Text.Json
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
            (Path.Combine(dir, "canvas-interaction-owners.json"))
    finally
        try Directory.Delete(dir, recursive = true)
        with _ -> ()

let private writeOwners (filePath: string) (entries: (string * string * string) list) =
    use stream = File.Create(filePath)
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartObject()

    entries
    |> List.groupBy (fun (worktree, _, _) -> worktree)
    |> List.iter (fun (worktree, views) ->
        writer.WritePropertyName(worktree)
        writer.WriteStartObject()
        views
        |> List.iter (fun (_, filename, sessionId) ->
            writer.WriteString(filename, sessionId))
        writer.WriteEndObject())

    writer.WriteEndObject()
    writer.Flush()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PersistenceTests() =

    [<Test>]
    member _.``filename case persists while worktree paths stay normalized``() =
        withOwnershipFiles (fun dir filePath _ ->
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
    member _.``legacy migration imports only missing SystemViews once``() =
        withOwnershipFiles (fun dir filePath legacyPath ->
            let worktree = Path.Combine(dir, "worktree")

            writeOwners filePath [
                worktree, "report.html", "agent-session"
                worktree, "diff.html", "unified-diff"
            ]

            writeOwners legacyPath [
                worktree, "old-report.html", "legacy-agent"
                worktree, "DIFF.HTML", "legacy-diff"
                worktree, "beads.html", "legacy-beads"
            ]

            let migrated = runAsync (CanvasDocOwnership.loadStore filePath legacyPath)
            Assert.That(
                runAsync (migrated.GetOwner(worktree, "report.html")),
                Is.EqualTo(Some "agent-session"))
            Assert.That(
                runAsync (migrated.GetOwner(worktree, "diff.html")),
                Is.EqualTo(Some "unified-diff"),
                "An existing unified target must win over the legacy value")
            Assert.That(
                runAsync (migrated.GetOwner(worktree, "DIFF.HTML")),
                Is.EqualTo(None: string option),
                "Case variants of an existing SystemView must not create duplicate targets")
            Assert.That(
                runAsync (migrated.GetOwner(worktree, "beads.html")),
                Is.EqualTo(Some "legacy-beads"))
            Assert.That(
                runAsync (migrated.GetOwner(worktree, "old-report.html")),
                Is.EqualTo(None: string option),
                "Legacy AgentDoc entries must not be imported")
            Assert.That(File.Exists legacyPath, Is.False, "A successful one-time import consumes the legacy file")

            let afterFirstLoad = File.ReadAllText(filePath)
            let loadedAgain = runAsync (CanvasDocOwnership.loadStore filePath legacyPath)

            Assert.That(File.ReadAllText(filePath), Is.EqualTo(afterFirstLoad))
            Assert.That(
                runAsync (loadedAgain.GetOwner(worktree, "beads.html")),
                Is.EqualTo(Some "legacy-beads")))

    [<Test>]
    member _.``view worktree and prune cleanup persist for both document kinds``() =
        withOwnershipFiles (fun dir filePath _ ->
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
