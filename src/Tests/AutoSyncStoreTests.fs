module Tests.AutoSyncStoreTests

open System
open System.IO
open NUnit.Framework
open Server.AutoSyncStore
open Tests.TestUtils

let private acceptedAt = DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero)

let private record revision : AcceptedSyncRecord =
    { BaseRevision = revision
      AcceptedAt = acceptedAt
      // Fixed rather than fresh so a persist/load round trip can be compared as a whole value.
      Generation = AcceptanceGeneration $"acceptance-{revision}" }

let private persist path state =
    assertOk (runAsync (tryPersistAtPath path state)) "persist"

let private sampleStore =
    Map.ofList
        [ "C:/code/repo-a", record "abc123"
          "C:/code/repo-b", record "def456" ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncStorePersistenceTests() =

    [<Test>]
    member _.``persist then load round-trips every accepted record``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            persist path sampleStore

            Assert.That(loadAtPath path, Is.EqualTo(sampleStore),
                "loaded records must equal what was persisted, across worktree paths"))

    [<Test>]
    member _.``load preserves the acceptance instant across time zones``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            let offsetRecord =
                { BaseRevision = "abc123"
                  AcceptedAt = DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(5.0))
                  Generation = AcceptanceGeneration "acceptance-offset" }
            persist path (Map.ofList [ "C:/code/repo-a", offsetRecord ])

            let loaded = loadAtPath path |> Map.find "C:/code/repo-a"

            Assert.That(loaded.AcceptedAt, Is.EqualTo(offsetRecord.AcceptedAt),
                "the retry window is measured from this instant, so it must survive the round trip"))

    [<Test>]
    member _.``load of an absent file returns an empty store``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "does-not-exist.json")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "a missing file must load as empty, not throw"))

    [<Test>]
    member _.``load of a corrupt file returns an empty store without throwing``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            File.WriteAllText(path, "{ this is not valid json ")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "an unparseable file must fall back to empty (server startup must not crash)"))

    [<Test>]
    member _.``load drops a record without a base revision``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            File.WriteAllText(path, """{ "C:/code/repo-a": { "accepted_at": "2026-03-04T05:06:07.0000000+00:00" } }""")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "a record with no revision cannot match a later observation, so it must not suppress a sync"))

    [<Test>]
    member _.``load drops a record with a blank base revision``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            File.WriteAllText(
                path,
                """{ "C:/code/repo-a": { "base_revision": "   ", "accepted_at": "2026-03-04T05:06:07.0000000+00:00" } }""")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "a blank revision is not a usable deduplication identity"))

    [<Test>]
    member _.``load drops a record with a non-string base revision``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            File.WriteAllText(
                path,
                """{ "C:/code/repo-a": { "base_revision": 42, "accepted_at": "2026-03-04T05:06:07.0000000+00:00" } }""")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "a wrongly typed revision must be dropped rather than throw"))

    [<Test>]
    member _.``load drops a record with an unparseable acceptance time``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            File.WriteAllText(
                path,
                """{ "C:/code/repo-a": { "base_revision": "abc123", "accepted_at": "not-a-timestamp" } }""")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "without a usable acceptance time the retry window cannot be evaluated"))

    [<Test>]
    member _.``load keeps valid records alongside a dropped one``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            File.WriteAllText(
                path,
                """{ "C:/code/repo-a": { "accepted_at": "2026-03-04T05:06:07.0000000+00:00" },
                     "C:/code/repo-b": { "base_revision": "def456", "accepted_at": "2026-03-04T05:06:07.0000000+00:00", "acceptance": "acceptance-def456" } }""")

            Assert.That(loadAtPath path, Is.EqualTo(Map.ofList [ "C:/code/repo-b", record "def456" ]),
                "one malformed record must not discard the others"))

    [<Test>]
    member _.``load gives a record written before generations existed a usable one``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            File.WriteAllText(
                path,
                """{ "C:/code/repo-a": { "base_revision": "abc123", "accepted_at": "2026-03-04T05:06:07.0000000+00:00" } }""")

            let loaded = loadAtPath path |> Map.tryFind "C:/code/repo-a"
            let generation = loaded |> Option.map (fun r -> let (AcceptanceGeneration token) = r.Generation in token)

            Assert.Multiple(fun () ->
                Assert.That(loaded |> Option.map _.BaseRevision, Is.EqualTo(Some "abc123"),
                    "a file written before acceptance generations must keep suppressing its revision")
                Assert.That(generation |> Option.map String.IsNullOrWhiteSpace, Is.EqualTo(Some false),
                    "the pairing token names an acceptance within one process, so a loaded record gets a fresh one")))

    [<Test>]
    member _.``persisting an empty store yields a file that loads as empty``() =
        withTempDir "treemon-autosync-test" (fun dir ->
            let path = Path.Combine(dir, "auto-sync.json")
            persist path Map.empty

            Assert.That(File.Exists(path), Is.True)
            Assert.That(Map.isEmpty (loadAtPath path), Is.True))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncStoreAgentTests() =

    let worktreePath = "C:/code/repo-a"

    let createStore dir =
        let path = Path.Combine(dir, "auto-sync.json")
        let store = create path
        store.Load()
        path, store

    [<Test>]
    member _.``setAccepted then Get round-trips the record``() =
        withTempDir "treemon-autosync-agent" (fun dir ->
            let _, store = createStore dir

            setAccepted store worktreePath (record "abc123")

            Assert.That(runAsync (store.Get worktreePath), Is.EqualTo(Some(record "abc123")),
                "Get must return exactly what setAccepted stored"))

    [<Test>]
    member _.``an accepted record survives recreating the store from disk``() =
        withTempDir "treemon-autosync-agent" (fun dir ->
            let path, store = createStore dir
            setAccepted store worktreePath (record "abc123")
            assertOk (runAsync (store.Flush())) "persist"

            let restarted = create path
            restarted.Load()

            Assert.That(runAsync (restarted.Get worktreePath), Is.EqualTo(Some(record "abc123")),
                "a restart must remember that this revision was already accepted"))

    [<Test>]
    member _.``setAccepted ignores a blank revision and drops the key from disk``() =
        withTempDir "treemon-autosync-agent" (fun dir ->
            let path, store = createStore dir
            setAccepted store worktreePath (record "abc123")
            assertOk (runAsync (store.Flush())) "initial persist"

            setAccepted store worktreePath (record "")
            assertOk (runAsync (store.Flush())) "blank revision persist"

            Assert.That(runAsync (store.Get worktreePath), Is.EqualTo(None))
            Assert.That(loadAtPath path |> Map.containsKey worktreePath, Is.False,
                "a revision that can never match must not linger on disk"))

    [<Test>]
    member _.``clear removes the worktree from memory and disk``() =
        withTempDir "treemon-autosync-agent" (fun dir ->
            let path, store = createStore dir
            setAccepted store worktreePath (record "abc123")
            assertOk (runAsync (store.Flush())) "initial persist"

            clear store worktreePath
            assertOk (runAsync (store.Flush())) "clearing persist"

            Assert.That(runAsync (store.Get worktreePath), Is.EqualTo(None),
                "Get must be empty after the record is cleared")
            Assert.That(loadAtPath path |> Map.containsKey worktreePath, Is.False,
                "a cleared worktree must be dropped from the persisted file, keeping it minimal"))

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``clearAccepted retires only the acceptance it names``() =
        withTempDir "treemon-autosync-agent" (fun dir ->
            let path, store = createStore dir
            setAccepted store worktreePath (record "abc123")
            assertOk (runAsync (store.Flush())) "initial persist"

            // A catch-up that read an earlier acceptance, delayed until after this one was stored.
            clearAccepted store worktreePath (AcceptanceGeneration "an-earlier-acceptance")
            let afterStaleClear = runAsync (store.Get worktreePath)

            clearAccepted store worktreePath (record "abc123").Generation
            assertOk (runAsync (store.Flush())) "clearing persist"

            Assert.Multiple(fun () ->
                Assert.That(afterStaleClear, Is.EqualTo(Some(record "abc123")),
                    "erasing a record another acceptance published would strand that acceptance's claim")
                Assert.That(runAsync (store.Get worktreePath), Is.EqualTo(None),
                    "the acceptance the clear does name is retired")
                Assert.That(loadAtPath path |> Map.containsKey worktreePath, Is.False,
                    "a retired acceptance is dropped from the persisted file too")))

    [<Test>]
    member _.``an unchanged setAccepted does not rewrite the file``() =
        withTempDir "treemon-autosync-agent" (fun dir ->
            let path, store = createStore dir
            setAccepted store worktreePath (record "abc123")
            assertOk (runAsync (store.Flush())) "initial persist"
            File.Delete(path) // delete the persisted file as a write sentinel

            setAccepted store worktreePath (record "abc123")
            assertOk (runAsync (store.Flush())) "no-op flush"

            Assert.That(File.Exists(path), Is.False,
                "re-recording the same revision must not touch the disk"))

    [<Test>]
    member _.``a newer revision rewrites the file``() =
        withTempDir "treemon-autosync-agent" (fun dir ->
            let path, store = createStore dir
            setAccepted store worktreePath (record "abc123")
            assertOk (runAsync (store.Flush())) "initial persist"
            File.Delete(path)

            setAccepted store worktreePath (record "def456")
            assertOk (runAsync (store.Flush())) "updated persist"

            Assert.That(loadAtPath path |> Map.tryFind worktreePath, Is.EqualTo(Some(record "def456")),
                "the persisted file must reflect the newly accepted revision"))

    [<Test>]
    member _.``stores with different paths are isolated``() =
        withTempDir "treemon-autosync-agent" (fun dir ->
            let firstPath = Path.Combine(dir, "first.json")
            let secondPath = Path.Combine(dir, "second.json")
            let first = create firstPath
            let second = create secondPath
            first.Load()
            second.Load()

            setAccepted first worktreePath (record "abc123")
            setAccepted second worktreePath (record "def456")
            assertOk (runAsync (first.Flush())) "first store"
            assertOk (runAsync (second.Flush())) "second store"

            Assert.That(loadAtPath firstPath |> Map.tryFind worktreePath, Is.EqualTo(Some(record "abc123")))
            Assert.That(loadAtPath secondPath |> Map.tryFind worktreePath, Is.EqualTo(Some(record "def456"))))
