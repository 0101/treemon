module Tests.MergedPrStoreTests

open System
open System.IO
open NUnit.Framework
open Shared
open Server.MergedPrStore
open Tests.TestUtils

let private withTempDir (action: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), $"treemon-mergedpr-test-{Guid.NewGuid()}")
    Directory.CreateDirectory(tempDir) |> ignore
    try action tempDir
    finally try Directory.Delete(tempDir, recursive = true) with _ -> ()

let private mk id title url : MergedPrRecord = { Id = id; Title = title; Url = url }

let private sampleStore =
    Map.ofList
        [ RepoId "C:/code/repo-a",
          Map.ofList
              [ "feature/x", mk 12 "Add X" "https://example.test/pull/12"
                "feature/y", mk 34 "Add Y" "https://example.test/pull/34" ]
          RepoId "C:/code/repo-b", Map.ofList [ "main", mk 7 "Merge main" "https://example.test/pull/7" ] ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MergedPrStorePersistenceTests() =

    [<Test>]
    member _.``persist then load round-trips the whole store``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            runAsync (persistAtPath path sampleStore)

            let loaded = loadAtPath path
            Assert.That(loaded, Is.EqualTo(sampleStore),
                "loaded store must equal what was persisted, across repos and branches"))

    [<Test>]
    member _.``load of an absent file returns an empty store``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "does-not-exist.json")
            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "a missing file must load as empty, not throw"))

    [<Test>]
    member _.``load of a corrupt file returns an empty store without throwing``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            File.WriteAllText(path, "{ this is not valid json ")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "an unparseable file must fall back to empty (server startup must not crash)"))

    [<Test>]
    member _.``persist creates the target directory when missing``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "nested", "merged-prs.json")
            runAsync (persistAtPath path sampleStore)

            Assert.That(File.Exists(path), Is.True, "persist must create the parent directory")
            Assert.That(loadAtPath path, Is.EqualTo(sampleStore)))

    [<Test>]
    member _.``successful persist leaves no temp file behind``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            runAsync (persistAtPath path sampleStore)

            Assert.That(File.Exists(path + ".tmp"), Is.False,
                "the atomic move must consume the temp file"))

    [<Test>]
    member _.``persisting an empty store yields an empty-object file that loads as empty``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            runAsync (persistAtPath path Map.empty)

            Assert.That(File.Exists(path), Is.True)
            Assert.That(Map.isEmpty (loadAtPath path), Is.True))

// The public store API (getForRepo/setForRepo/load) drives a process-global MailboxProcessor that
// persists data/merged-prs.json relative to CWD, so these run under a throwaway CWD and are
// NonParallelizable (the CWD swap and the agent are process-global). Each test uses a unique RepoId
// so the singleton agent never leaks in-memory state between tests; assertions are scoped to that
// RepoId because the agent rewrites its whole state (other tests' repos included) on every persist.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type MergedPrStoreAgentTests() =

    // Relative on purpose: resolved against the withTempCwd-swapped CWD at each I/O call.
    let persistedPath = Path.Combine("data", "merged-prs.json")

    let uniqueRepo () =
        let id = Guid.NewGuid().ToString("N")[..7]
        RepoId $"/test/merged/{id}"

    // getForRepo round-trips through the agent only AFTER the preceding message is drained, so
    // awaiting it is the sync barrier that guarantees a prior setForRepo's persist has completed.
    let barrier repoId = runAsync (getForRepo repoId) |> ignore

    [<Test>]
    member _.``setForRepo then getForRepo round-trips a repo's records``() =
        withTempCwd (fun () ->
            let repoId = uniqueRepo ()
            let records = Map.ofList [ "feature/x", mk 21 "X" "https://example.test/pull/21" ]

            setForRepo repoId records
            let got = runAsync (getForRepo repoId)

            Assert.That(got, Is.EqualTo(records),
                "getForRepo must return exactly what setForRepo stored"))

    [<Test>]
    member _.``setForRepo with an empty map drops the repo from memory and disk``() =
        withTempCwd (fun () ->
            let repoId = uniqueRepo ()
            let records = Map.ofList [ "feature/y", mk 9 "Y" "https://example.test/pull/9" ]

            setForRepo repoId records
            barrier repoId
            setForRepo repoId Map.empty
            barrier repoId

            Assert.That(Map.isEmpty (runAsync (getForRepo repoId)), Is.True,
                "getForRepo must be empty after the repo is cleared")
            Assert.That(loadAtPath persistedPath |> Map.containsKey repoId, Is.False,
                "an emptied repo must be dropped from the persisted file, keeping it minimal"))

    [<Test>]
    member _.``an unchanged setForRepo does not rewrite the file``() =
        withTempCwd (fun () ->
            let repoId = uniqueRepo ()
            let records = Map.ofList [ "feature/z", mk 3 "Z" "https://example.test/pull/3" ]

            setForRepo repoId records
            barrier repoId
            File.Delete(persistedPath) // delete the persisted file as a write sentinel

            setForRepo repoId records // identical records -> Decision #6: must not persist
            barrier repoId

            Assert.That(File.Exists(persistedPath), Is.False,
                "a no-op setForRepo must not touch the disk (persist only when the store changes)"))

    [<Test>]
    member _.``a changed setForRepo rewrites the file with the new records``() =
        withTempCwd (fun () ->
            let repoId = uniqueRepo ()
            let records = Map.ofList [ "feature/z", mk 3 "Z" "https://example.test/pull/3" ]

            setForRepo repoId records
            barrier repoId
            File.Delete(persistedPath)

            let updated = records |> Map.add "feature/w" (mk 4 "W" "https://example.test/pull/4")
            setForRepo repoId updated
            barrier repoId

            Assert.That(File.Exists(persistedPath), Is.True, "a changed setForRepo must persist")
            Assert.That(loadAtPath persistedPath |> Map.tryFind repoId, Is.EqualTo(Some updated),
                "the persisted file must reflect the updated records"))
