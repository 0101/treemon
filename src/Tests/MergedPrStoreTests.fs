module Tests.MergedPrStoreTests

open System.IO
open NUnit.Framework
open Shared
open Server.MergedPrStore
open Tests.TestUtils

let private mk id title url : MergedPrRecord =
    { Id = id
      Title = title
      Url = url
      HeadSha = $"sha-{id}" }

let private reconcileMergedPrs live persisted worktreeHeads knownBranches =
    Server.MergedPrStore.reconcileMergedPrs live Map.empty persisted worktreeHeads knownBranches

let private reconcileObserved live liveHeadShas persisted worktreeHeads knownBranches =
    Server.MergedPrStore.reconcileMergedPrs live liveHeadShas persisted worktreeHeads knownBranches

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
        withTempDir "treemon-mergedpr-test" (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            runAsync (persistAtPath path sampleStore)

            let loaded = loadAtPath path
            Assert.That(loaded, Is.EqualTo(sampleStore),
                "loaded store must equal what was persisted, across repos and branches"))

    [<Test>]
    member _.``persist then load preserves a record's head_sha``() =
        withTempDir "treemon-mergedpr-test" (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")

            let stamped =
                Map.ofList
                    [ RepoId "C:/code/repo-a",
                      Map.ofList
                          [ "feature/x",
                            { mk 12 "Add X" "https://example.test/pull/12" with HeadSha = "abc123def456" } ] ]

            runAsync (persistAtPath path stamped)

            Assert.That(loadAtPath path, Is.EqualTo(stamped),
                "a non-empty head_sha must survive a persist -> load round-trip"))

    [<Test>]
    member _.``load discards a record without head_sha``() =
        withTempDir "treemon-mergedpr-test" (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            File.WriteAllText(
                path,
                """{ "C:/code/repo-a": { "feature/x": { "id": 12, "title": "Add X", "url": "https://example.test/pull/12" } } }""")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "a record without a provider identity must not bypass identity checks"))

    [<Test>]
    member _.``load of an absent file returns an empty store``() =
        withTempDir "treemon-mergedpr-test" (fun dir ->
            let path = Path.Combine(dir, "does-not-exist.json")
            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "a missing file must load as empty, not throw"))

    [<Test>]
    member _.``load of a corrupt file returns an empty store without throwing``() =
        withTempDir "treemon-mergedpr-test" (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            File.WriteAllText(path, "{ this is not valid json ")

            Assert.That(Map.isEmpty (loadAtPath path), Is.True,
                "an unparseable file must fall back to empty (server startup must not crash)"))

    [<Test>]
    member _.``persist creates the target directory when missing``() =
        withTempDir "treemon-mergedpr-test" (fun dir ->
            let path = Path.Combine(dir, "nested", "merged-prs.json")
            runAsync (persistAtPath path sampleStore)

            Assert.That(File.Exists(path), Is.True, "persist must create the parent directory")
            Assert.That(loadAtPath path, Is.EqualTo(sampleStore)))

    [<Test>]
    member _.``successful persist leaves no temp file behind``() =
        withTempDir "treemon-mergedpr-test" (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            runAsync (persistAtPath path sampleStore)

            Assert.That(File.Exists(path + ".tmp"), Is.False,
                "the atomic move must consume the temp file"))

    [<Test>]
    member _.``persisting an empty store yields an empty-object file that loads as empty``() =
        withTempDir "treemon-mergedpr-test" (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            runAsync (persistAtPath path Map.empty)

            Assert.That(File.Exists(path), Is.True)
            Assert.That(Map.isEmpty (loadAtPath path), Is.True))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MergedPrStoreAgentTests() =

    let repoId = RepoId "/test/merged"

    let createStore dir =
        let path = Path.Combine(dir, "merged-prs.json")
        let store = create path
        store.Load()
        path, store

    [<Test>]
    member _.``stores with different paths are isolated``() =
        withTempDir "treemon-mergedpr-agent" (fun dir ->
            let firstPath = Path.Combine(dir, "first.json")
            let secondPath = Path.Combine(dir, "second.json")
            let first = create firstPath
            let second = create secondPath
            first.Load()
            second.Load()

            let firstRecords = Map.ofList [ "feature/x", mk 1 "First" "https://example.test/pull/1" ]
            let secondRecords = Map.ofList [ "feature/x", mk 2 "Second" "https://example.test/pull/2" ]
            first.SetForRepo repoId firstRecords
            second.SetForRepo repoId secondRecords
            runAsync (first.Flush()) |> fun result -> assertOk result "first store"
            runAsync (second.Flush()) |> fun result -> assertOk result "second store"

            Assert.That(loadAtPath firstPath |> Map.find repoId, Is.EqualTo(firstRecords))
            Assert.That(loadAtPath secondPath |> Map.find repoId, Is.EqualTo(secondRecords)))

    [<Test>]
    member _.``setForRepo then getForRepo round-trips a repo's records``() =
        withTempDir "treemon-mergedpr-agent" (fun dir ->
            let _, store = createStore dir
            let records = Map.ofList [ "feature/x", mk 21 "X" "https://example.test/pull/21" ]

            store.SetForRepo repoId records
            let got = runAsync (store.GetForRepo repoId)

            Assert.That(got, Is.EqualTo(records),
                "getForRepo must return exactly what setForRepo stored"))

    [<Test>]
    member _.``setForRepo with an empty map drops the repo from memory and disk``() =
        withTempDir "treemon-mergedpr-agent" (fun dir ->
            let persistedPath, store = createStore dir
            let records = Map.ofList [ "feature/y", mk 9 "Y" "https://example.test/pull/9" ]

            store.SetForRepo repoId records
            runAsync (store.Flush()) |> fun result -> assertOk result "initial persist"
            store.SetForRepo repoId Map.empty
            runAsync (store.Flush()) |> fun result -> assertOk result "clearing persist"

            Assert.That(Map.isEmpty (runAsync (store.GetForRepo repoId)), Is.True,
                "getForRepo must be empty after the repo is cleared")
            Assert.That(loadAtPath persistedPath |> Map.containsKey repoId, Is.False,
                "an emptied repo must be dropped from the persisted file, keeping it minimal"))

    [<Test>]
    member _.``an unchanged setForRepo does not rewrite the file``() =
        withTempDir "treemon-mergedpr-agent" (fun dir ->
            let persistedPath, store = createStore dir
            let records = Map.ofList [ "feature/z", mk 3 "Z" "https://example.test/pull/3" ]

            store.SetForRepo repoId records
            runAsync (store.Flush()) |> fun result -> assertOk result "initial persist"
            File.Delete(persistedPath) // delete the persisted file as a write sentinel

            store.SetForRepo repoId records
            runAsync (store.Flush()) |> fun result -> assertOk result "no-op flush"

            Assert.That(File.Exists(persistedPath), Is.False,
                "a no-op setForRepo must not touch the disk (persist only when the store changes)"))

    [<Test>]
    member _.``a changed setForRepo rewrites the file with the new records``() =
        withTempDir "treemon-mergedpr-agent" (fun dir ->
            let persistedPath, store = createStore dir
            let records = Map.ofList [ "feature/z", mk 3 "Z" "https://example.test/pull/3" ]

            store.SetForRepo repoId records
            runAsync (store.Flush()) |> fun result -> assertOk result "initial persist"
            File.Delete(persistedPath)

            let updated = records |> Map.add "feature/w" (mk 4 "W" "https://example.test/pull/4")
            store.SetForRepo repoId updated
            runAsync (store.Flush()) |> fun result -> assertOk result "updated persist"

            Assert.That(File.Exists(persistedPath), Is.True, "a changed setForRepo must persist")
            Assert.That(loadAtPath persistedPath |> Map.tryFind repoId, Is.EqualTo(Some updated),
                "the persisted file must reflect the updated records"))

// A live open (non-merged) PR with every volatile field populated — used to prove the fallback
// overlay never displaces live data and that non-merged PRs are neither persisted nor overlaid.
let private openLivePr id : PrStatus =
    HasPr
        { Id = id
          Title = $"live #{id}"
          Url = $"https://example.test/pull/{id}"
          IsDraft = true
          Comments = WithResolution(2, 5)
          Builds = [ { Name = "ci"; Status = Building; Url = None; Failure = None } ]
          IsMerged = false
          HasConflicts = true }

// A live MERGED PR carrying volatile fields, to prove reconcile persists only Id/Title/Url.
let private mergedLivePr id title url : PrStatus =
    HasPr
        { Id = id
          Title = title
          Url = url
          IsDraft = true
          Comments = WithResolution(1, 3)
          Builds = [ { Name = "ci"; Status = Succeeded; Url = Some "https://example.test/ci"; Failure = None } ]
          IsMerged = true
          HasConflicts = true }

// The exact inert reconstruction the spec mandates for a persisted record overlaid as fallback.
let private reconstructed (r: MergedPrRecord) : PrStatus =
    HasPr
        { Id = r.Id
          Title = r.Title
          Url = r.Url
          IsDraft = false
          Comments = WithResolution(0, 0)
          Builds = []
          IsMerged = true
          HasConflicts = false }

// Pure reconcileMergedPrs: no I/O, so these run parallel with no CWD/agent setup.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ReconcileMergedPrsTests() =

    // (a) inject a persisted merged PR when the live map lacks the branch entirely.
    [<Test>]
    member _.``injects a persisted merged PR for a known branch missing from the live map``() =
        let record = mk 12 "Add X" "https://example.test/pull/12"
        let persisted = Map.ofList [ "feature/x", record ]

        let effective, newPersisted =
            reconcileMergedPrs Map.empty persisted Map.empty (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" effective, Is.EqualTo(Some(reconstructed record)),
            "a known branch absent from the live map must be overlaid with the reconstructed merged PR")
        Assert.That(newPersisted, Is.EqualTo(persisted),
            "a pure overlay must not mutate the persisted store")

    // (a, variant) NoPr counts as "not HasPr", so the overlay still applies.
    [<Test>]
    member _.``overlays a persisted merged PR when the live status is NoPr``() =
        let record = mk 12 "Add X" "https://example.test/pull/12"
        let persisted = Map.ofList [ "feature/x", record ]
        let live = Map.ofList [ "feature/x", NoPr ]

        let effective, _ = reconcileMergedPrs live persisted Map.empty (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" effective, Is.EqualTo(Some(reconstructed record)),
            "a live NoPr is not a HasPr, so the persisted record must be overlaid")

    // (b) never override a live HasPr — even a non-merged one beats a persisted merged record.
    [<Test>]
    member _.``never overrides a live HasPr — even an open PR beats a persisted merged record``() =
        let persisted =
            Map.ofList [ "feature/x", mk 12 "merged long ago" "https://example.test/pull/12" ]
        let liveOpen = openLivePr 55

        let effective, _ =
            reconcileMergedPrs (Map.ofList [ "feature/x", liveOpen ]) persisted Map.empty (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" effective, Is.EqualTo(Some liveOpen),
            "live HasPr always wins; the overlay only fills branches the live map is missing")

    // (c) prune records for branches no longer known.
    [<Test>]
    member _.``prunes persisted records for branches no longer known``() =
        let persisted =
            Map.ofList
                [ "feature/x", mk 1 "X" "https://example.test/pull/1"
                  "feature/gone", mk 2 "Gone" "https://example.test/pull/2" ]

        let effective, newPersisted =
            reconcileMergedPrs Map.empty persisted Map.empty (Some(Set.ofList [ "feature/x" ]))

        Assert.That(newPersisted |> Map.containsKey "feature/gone", Is.False,
            "a branch outside knownBranches must be pruned from the store")
        Assert.That(newPersisted |> Map.containsKey "feature/x", Is.True,
            "a still-known branch's record must survive pruning")
        Assert.That(effective |> Map.containsKey "feature/gone", Is.False,
            "a pruned branch must not be overlaid into the effective map")

    [<Test>]
    member _.``upserts a newly observed live merged PR with its provider head SHA``() =
        let live =
            Map.ofList [ "feature/x", mergedLivePr 77 "Freshly merged" "https://example.test/pull/77" ]
        let providerHeads = Map.ofList [ "feature/x", "sha-77" ]
        let worktreeHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-77" ] ]

        let _, newPersisted =
            reconcileObserved live providerHeads Map.empty worktreeHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" newPersisted,
            Is.EqualTo(Some { mk 77 "Freshly merged" "https://example.test/pull/77" with HeadSha = "sha-77" }),
            "a live merged PR must retain its immutable provider identity and drop volatile fields")

    [<Test>]
    member _.``upsert refreshes an existing record from the live merged PR``() =
        let persisted = Map.ofList [ "feature/x", mk 1 "stale" "https://example.test/pull/1" ]
        let live = Map.ofList [ "feature/x", mergedLivePr 2 "renamed" "https://example.test/pull/2" ]
        let providerHeads = Map.ofList [ "feature/x", "sha-2" ]
        let worktreeHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-2" ] ]

        let _, newPersisted =
            reconcileObserved live providerHeads persisted worktreeHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" newPersisted,
            Is.EqualTo(Some { mk 2 "renamed" "https://example.test/pull/2" with HeadSha = "sha-2" }),
            "an already-persisted branch must be updated to the latest live merged PR")

    // (e, canonical) a still-live merged PR re-reported identically each refresh must be a no-op
    // write: the upsert path runs but re-adds an equal record, so the store stays unchanged and the
    // live PR (with its volatile fields) still wins over the persisted reconstruction.
    [<Test>]
    member _.``a re-reported live merged PR identical to its persisted record is a no-op write``() =
        let record = { mk 12 "Add X" "https://example.test/pull/12" with HeadSha = "sha-12" }
        let persisted = Map.ofList [ "feature/x", record ]
        let live = Map.ofList [ "feature/x", mergedLivePr 12 "Add X" "https://example.test/pull/12" ]
        let providerHeads = Map.ofList [ "feature/x", "sha-12" ]
        let worktreeHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-12" ] ]

        let effective, newPersisted =
            reconcileObserved live providerHeads persisted worktreeHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(newPersisted, Is.EqualTo(persisted),
            "re-upserting an identical merged PR must leave the store structurally unchanged (Decision #6)")
        Assert.That(Map.tryFind "feature/x" effective, Is.EqualTo(live |> Map.tryFind "feature/x"),
            "the live merged PR wins over the persisted reconstruction (volatile fields preserved)")

    // (e) report the persisted store unchanged when nothing moved.
    [<Test>]
    member _.``leaves the persisted store unchanged across a steady-state refresh``() =
        let persisted =
            Map.ofList
                [ "feature/x", mk 1 "X" "https://example.test/pull/1" // aged out of live -> overlaid
                  "feature/y", mk 2 "Y" "https://example.test/pull/2" ] // now shows a live open PR
        let live = Map.ofList [ "feature/y", openLivePr 9 ]
        let known = Set.ofList [ "feature/x"; "feature/y" ]

        let effective, newPersisted = reconcileMergedPrs live persisted Map.empty (Some known)

        Assert.That(newPersisted, Is.EqualTo(persisted),
            "no new merged PRs and nothing to prune -> the store is unchanged (Decision #6 no-op write)")
        Assert.That(Map.tryFind "feature/x" effective,
            Is.EqualTo(Some(reconstructed (mk 1 "X" "https://example.test/pull/1"))),
            "the aged-out branch is overlaid from the store")
        Assert.That(Map.tryFind "feature/y" effective, Is.EqualTo(Some(openLivePr 9)),
            "the branch with a live PR stays live")

    // (F7) An untrusted enumeration (`None`) — the empty/partial `knownBranches` the buggy path
    // produced whenever git-data collection was unready — must NEVER prune. Before this fix a
    // non-empty persisted map reconciled against an empty set became `Map.empty`, whose change
    // fired `setForRepo Map.empty` and wiped data/merged-prs.json permanently.
    [<Test>]
    member _.``preserves the whole store when the branch enumeration is untrusted (review F7)``() =
        let persisted =
            Map.ofList
                [ "feature/x", mk 1 "X" "https://example.test/pull/1"
                  "feature/y", mk 2 "Y" "https://example.test/pull/2" ]

        let effective, newPersisted = reconcileMergedPrs Map.empty persisted Map.empty None

        Assert.That(newPersisted, Is.EqualTo(persisted),
            "None must skip pruning entirely - the just-loaded store must survive intact, never wiped (review F7)")
        Assert.That(Map.tryFind "feature/x" effective,
            Is.EqualTo(Some(reconstructed (mk 1 "X" "https://example.test/pull/1"))),
            "the store must still overlay merged badges while the enumeration is untrusted")
        Assert.That(Map.tryFind "feature/y" effective,
            Is.EqualTo(Some(reconstructed (mk 2 "Y" "https://example.test/pull/2"))),
            "every persisted record is overlaid; none is dropped under an untrusted enumeration")

    // (F7) Upserts stay additive under `None`: a newly observed live merged PR is still recorded
    // (provider ground truth), so the fix never sacrifices recording merges to protect the store.
    [<Test>]
    member _.``still upserts a live merged PR when the enumeration is untrusted (review F7)``() =
        let live =
            Map.ofList [ "feature/new", mergedLivePr 88 "Just merged" "https://example.test/pull/88" ]
        let providerHeads = Map.ofList [ "feature/new", "sha-88" ]
        let worktreeHeads = Map.ofList [ "feature/new", Set.ofList [ "sha-88" ] ]

        let _, newPersisted = reconcileObserved live providerHeads Map.empty worktreeHeads None

        Assert.That(Map.tryFind "feature/new" newPersisted,
            Is.EqualTo(Some { mk 88 "Just merged" "https://example.test/pull/88" with HeadSha = "sha-88" }),
            "a live merged PR must still be recorded even when pruning is skipped")

    // (Decision #11) Identity gate — EVICT: a record whose branch has a PRESENT but DIFFERENT
    // worktree tip is a reused-name incarnation. It must be dropped from the store AND never overlaid,
    // so a recreated branch never resurrects a prior branch's merged badge.
    [<Test>]
    member _.``evicts a persisted record when the branch's worktree tip proves a different incarnation (Decision #11)``() =
        let record = { mk 12 "Add X" "https://example.test/pull/12" with HeadSha = "sha-X" }
        let persisted = Map.ofList [ "feature/x", record ]
        let worktreeHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-Y" ] ] // present tip differs from the record

        let effective, newPersisted =
            reconcileMergedPrs Map.empty persisted worktreeHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(newPersisted |> Map.containsKey "feature/x", Is.False,
            "a confirmed mismatch (non-empty HeadSha vs a present differing tip) must evict the record")
        Assert.That(effective |> Map.containsKey "feature/x", Is.False,
            "an evicted reused-name record must never be overlaid into the effective map")

    // (Decision #11) Identity gate — KEEP on exact tip match: an aged-out branch (no live PR) still
    // sitting on its recorded tip is the same incarnation, so its badge is overlaid and the record kept.
    [<Test>]
    member _.``keeps and overlays a persisted record when the worktree tip matches exactly (Decision #11)``() =
        let record = { mk 12 "Add X" "https://example.test/pull/12" with HeadSha = "sha-X" }
        let persisted = Map.ofList [ "feature/x", record ]
        let worktreeHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-X" ] ] // present tip == the record's HeadSha

        let effective, newPersisted =
            reconcileMergedPrs Map.empty persisted worktreeHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" newPersisted, Is.EqualTo(Some record),
            "an exact tip match is the same incarnation, so the record survives the identity gate")
        Assert.That(Map.tryFind "feature/x" effective, Is.EqualTo(Some(reconstructed record)),
            "a tip-matched aged-out record is overlaid as its reconstructed merged PR")

    [<Test>]
    member _.``keeps and overlays a record whose HeadSha matches one of two observed tips for the branch (review F2)``() =
        let record = { mk 12 "Add X" "https://example.test/pull/12" with HeadSha = "sha-X1" }
        let persisted = Map.ofList [ "feature/x", record ]
        // Two worktrees track feature/x at different tips; the record matches the SECOND one.
        let worktreeHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-X2"; "sha-X1" ] ]

        let effective, newPersisted =
            reconcileMergedPrs Map.empty persisted worktreeHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" newPersisted, Is.EqualTo(Some record),
            "a record matching ANY observed tip of a multi-worktree branch must survive the identity gate (review F2)")
        Assert.That(Map.tryFind "feature/x" effective, Is.EqualTo(Some(reconstructed record)),
            "a still-valid multi-tip record must be overlaid, not lost to an arbitrary Map.ofSeq collapse (review F2)")

    [<Test>]
    member _.``provider identity survives removal of a sibling worktree at a different tip``() =
        let live =
            Map.ofList [ "feature/x", mergedLivePr 77 "Freshly merged" "https://example.test/pull/77" ]
        let providerHeads = Map.ofList [ "feature/x", "sha-X2" ]
        let initialHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-X1"; "sha-X2" ] ]

        let _, observed =
            reconcileObserved live providerHeads Map.empty initialHeads (Some(Set.ofList [ "feature/x" ]))

        let remainingHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-X2" ] ]
        let effective, persisted =
            reconcileMergedPrs Map.empty observed remainingHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(persisted |> Map.tryFind "feature/x" |> Option.map _.HeadSha, Is.EqualTo(Some "sha-X2"))
        Assert.That(Map.containsKey "feature/x" effective, Is.True)

    [<Test>]
    member _.``evicts a record whose HeadSha matches none of the branch's observed tips (review F2)``() =
        let record = { mk 12 "Add X" "https://example.test/pull/12" with HeadSha = "sha-OLD" }
        let persisted = Map.ofList [ "feature/x", record ]
        let worktreeHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-X1"; "sha-X2" ] ] // neither matches sha-OLD

        let effective, newPersisted =
            reconcileMergedPrs Map.empty persisted worktreeHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(newPersisted |> Map.containsKey "feature/x", Is.False,
            "a non-empty HeadSha absent from a present, non-empty tip set is a confirmed mismatch -> evict (review F2)")
        Assert.That(effective |> Map.containsKey "feature/x", Is.False,
            "an evicted multi-tip mismatch must never be overlaid into the effective map (review F2)")

    [<Test>]
    member _.``records provider identity even when the local worktree tip is temporarily unavailable``() =
        let live =
            Map.ofList [ "feature/x", mergedLivePr 77 "Freshly merged" "https://example.test/pull/77" ]
        let providerHeads = Map.ofList [ "feature/x", "sha-provider" ]

        let _, newPersisted =
            reconcileObserved live providerHeads Map.empty Map.empty (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" newPersisted,
            Is.EqualTo(Some { mk 77 "Freshly merged" "https://example.test/pull/77" with HeadSha = "sha-provider" }))

    [<Test>]
    member _.``does not record a live merged PR whose provider head SHA is unavailable``() =
        let live =
            Map.ofList [ "feature/x", mergedLivePr 77 "Freshly merged" "https://example.test/pull/77" ]
        let _, newPersisted =
            reconcileMergedPrs live Map.empty Map.empty (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.containsKey "feature/x" newPersisted, Is.False,
            "a merge without immutable provider identity must not enter the durable fallback")

    [<Test>]
    member _.``does not overwrite an existing verified record when a re-observed merge has no provider SHA``() =
        let existing = { mk 12 "Add X" "https://example.test/pull/12" with HeadSha = "sha-X" }
        let persisted = Map.ofList [ "feature/x", existing ]
        let live = Map.ofList [ "feature/x", mergedLivePr 99 "reobserved" "https://example.test/pull/99" ]
        let _, newPersisted =
            reconcileMergedPrs live persisted Map.empty (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" newPersisted, Is.EqualTo(Some existing),
            "a re-observed merge without provider identity must leave the verified record intact")

    [<Test>]
    member _.``does not rebind an old merged PR to later unmerged work on the same branch``() =
        let existing = { mk 42 "Merged X" "https://example.test/pull/42" with HeadSha = "sha-X" }
        let persisted = Map.ofList [ "feature/x", existing ]
        let live = Map.ofList [ "feature/x", mergedLivePr 42 "Merged X" "https://example.test/pull/42" ]
        let providerHeads = Map.ofList [ "feature/x", "sha-X" ]
        let worktreeHeads = Map.ofList [ "feature/x", Set.ofList [ "sha-Y" ] ]

        let _, newPersisted =
            reconcileObserved live providerHeads persisted worktreeHeads (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.containsKey "feature/x" newPersisted, Is.False,
            "later local commit Y must not be stamped as proof that merged PR #42 covers it")

// pruneScope decides whether the live-derived branch enumeration is trustworthy enough to prune
// against (review F7 / Decision #8): it must be complete, non-empty, AND free of transient upstream
// read failures on known worktrees. Pure, so these run parallel with no setup.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MergedPrPruneScopeTests() =

    // Complete: every known worktree path has collected git data and no read failed -> trust it.
    [<Test>]
    member _.``trusts the enumeration when every known worktree has collected git data``() =
        let known = Set.ofList [ "/wt/a"; "/wt/b" ]
        let collected = Set.ofList [ "/wt/a"; "/wt/b" ]
        let branches = Set.ofList [ "feature/a"; "feature/b" ]

        Assert.That(pruneScope known collected Set.empty branches, Is.EqualTo(Some branches),
            "a fully collected worktree set with no read failures proves the branch enumeration - prune against it")

    // Partial: a worktree's git data is missing (a RefreshGit timeout never posted UpdateGit).
    [<Test>]
    member _.``refuses to prune when a known worktree has no collected git data``() =
        let known = Set.ofList [ "/wt/a"; "/wt/b" ]
        let collected = Set.ofList [ "/wt/a" ] // /wt/b's git data never arrived
        let branches = Set.ofList [ "feature/a" ]

        Assert.That(pruneScope known collected Set.empty branches |> Option.isNone, Is.True,
            "a partially collected worktree set must not be used to prune (review F7)")

    // Unready or a transient empty worktree list: no known worktrees -> never prune.
    [<Test>]
    member _.``refuses to prune when there are no known worktrees``() =
        Assert.That(pruneScope Set.empty Set.empty Set.empty Set.empty |> Option.isNone, Is.True,
            "an empty worktree set (unready or a transient empty list) must not prune the store")

    // Correlated upstream-read failure: every worktree is collected (paths complete) yet resolves
    // NO branch, so the enumeration collapses to empty. Pruning against it would wipe the WHOLE
    // store, so an empty enumeration must be refused even when path-completeness holds (review F7).
    [<Test>]
    member _.``refuses to prune when the worktree set is complete but resolves no branches``() =
        let known = Set.ofList [ "/wt/a"; "/wt/b" ]
        let collected = Set.ofList [ "/wt/a"; "/wt/b" ] // all collected...
        let branches = Set.empty // ...but every upstream read returned nothing

        Assert.That(pruneScope known collected Set.empty branches |> Option.isNone, Is.True,
            "a complete worktree set that resolved zero branches must NOT prune - it would wipe the whole store (review F7)")

    // Partial upstream-read failure (Decision #8 residual): every worktree is collected and the
    // enumeration is non-empty (feature/a resolved), but /wt/b's `git rev-parse @{u}` transiently
    // FAILED, so feature/b never entered the set. Pruning against it would forget feature/b's
    // aged-out merged PR. A read failure on any known worktree must therefore refuse the prune.
    [<Test>]
    member _.``refuses to prune when a known worktree's upstream read failed``() =
        let known = Set.ofList [ "/wt/a"; "/wt/b" ]
        let collected = Set.ofList [ "/wt/a"; "/wt/b" ] // both collected...
        let readFailed = Set.ofList [ "/wt/b" ] // ...but /wt/b's upstream read failed transiently
        let branches = Set.ofList [ "feature/a" ] // so only feature/a resolved

        Assert.That(pruneScope known collected readFailed branches |> Option.isNone, Is.True,
            "a transient upstream read failure on a known worktree must skip the prune (Decision #8 residual)")

    // A read failure on a STALE path (already removed from the known set) is irrelevant - the
    // enumeration is still complete and trustworthy for the worktrees that remain, so prune.
    [<Test>]
    member _.``still prunes when the only read failure is on a stale path outside the known set``() =
        let known = Set.ofList [ "/wt/a"; "/wt/b" ]
        let collected = Set.ofList [ "/wt/a"; "/wt/b" ]
        let readFailed = Set.ofList [ "/wt/gone" ] // a stale path no longer tracked
        let branches = Set.ofList [ "feature/a"; "feature/b" ]

        Assert.That(pruneScope known collected readFailed branches, Is.EqualTo(Some branches),
            "a read failure outside the known worktree set must not block pruning")

// End-to-end coverage for the merged-PR persistence lifecycle described in worktree-monitor.md.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MergedPrStoreEndToEndTests() =

    let repo = RepoId "C:/code/repo-a"
    let branch = "feature/x"

    // A live MERGED PR (Id = 42) with every volatile field populated, to prove persistence keeps
    // only Id/Title/Url and the reconstructed overlay is inert.
    let liveMerged42 =
        HasPr
            { Id = 42
              Title = "Add X"
              Url = "https://example.test/pull/42"
              IsDraft = true
              Comments = WithResolution(1, 2)
              Builds = [ { Name = "ci"; Status = Succeeded; Url = Some "https://example.test/ci"; Failure = None } ]
              IsMerged = true
              HasConflicts = true }

    let record42 = mk 42 "Add X" "https://example.test/pull/42"
    let known b = Some(Set.ofList [ b ])
    let persistStore path store = runAsync (persistAtPath path store)
    let recordOnDisk path = loadAtPath path |> Map.tryFind repo |> Option.bind (Map.tryFind branch)

    // (6) Corruption resilience: garbage bytes on disk load as an empty store and never throw — this
    // path runs at server startup, so a throw would crash boot. FAIL: exception propagates or store
    // is non-empty. (Reaching the assertion proves loadAtPath returned rather than threw.)
    [<Test>]
    member _.``step 6 - a corrupt store file loads as empty without throwing``() =
        withTempDir "treemon-mergedpr-e2e" (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            File.WriteAllBytes(path, [| 0uy; 159uy; 146uy; 150uy; 123uy; 34uy; 255uy; 0uy |])

            let loaded = loadAtPath path
            Assert.That(Map.isEmpty loaded, Is.True,
                "step 6 FAIL: a corrupt file must load as an empty store (server startup must not crash)"))

    // Full lifecycle through a SINGLE temp file: the bytes written by the observe step are the very
    // bytes reloaded after the simulated restart, then aged-out fallback, then pruned — proving the
    // steps compose end-to-end on one physical file, not just in isolation.
    [<Test>]
    member _.``full lifecycle - observe, persist, reload, fallback, then prune on one file``() =
        withTempDir "treemon-mergedpr-e2e" (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")

            let providerHeads = Map.ofList [ branch, "sha-42" ]
            let worktreeHeads = Map.ofList [ branch, Set.ofList [ "sha-42" ] ]
            let _, observed =
                reconcileObserved
                    (Map.ofList [ branch, liveMerged42 ])
                    providerHeads
                    Map.empty
                    worktreeHeads
                    (known branch)
            persistStore path (Map.ofList [ repo, observed ])

            // simulated restart + aged-out fallback (empty live map)
            let afterRestart = loadAtPath path |> Map.find repo
            let effective, still = reconcileMergedPrs Map.empty afterRestart Map.empty (known branch)
            Assert.That(Map.tryFind branch effective, Is.EqualTo(Some(reconstructed record42)),
                "full lifecycle FAIL: aged-out branch must stay merged after reload")

            // prune once the branch is no longer known, persist, confirm the file is empty of it
            let _, pruned = reconcileMergedPrs Map.empty still Map.empty (known "feature/other")
            persistStore path (Map.ofList [ repo, pruned ])
            Assert.That(recordOnDisk path |> Option.isNone, Is.True,
                "full lifecycle FAIL: pruned branch must be absent from the reloaded file"))
