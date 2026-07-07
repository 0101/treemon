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
            reconcileMergedPrs Map.empty persisted (Some(Set.ofList [ "feature/x" ]))

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

        let effective, _ = reconcileMergedPrs live persisted (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" effective, Is.EqualTo(Some(reconstructed record)),
            "a live NoPr is not a HasPr, so the persisted record must be overlaid")

    // (b) never override a live HasPr — even a non-merged one beats a persisted merged record.
    [<Test>]
    member _.``never overrides a live HasPr — even an open PR beats a persisted merged record``() =
        let persisted =
            Map.ofList [ "feature/x", mk 12 "merged long ago" "https://example.test/pull/12" ]
        let liveOpen = openLivePr 55

        let effective, _ =
            reconcileMergedPrs (Map.ofList [ "feature/x", liveOpen ]) persisted (Some(Set.ofList [ "feature/x" ]))

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
            reconcileMergedPrs Map.empty persisted (Some(Set.ofList [ "feature/x" ]))

        Assert.That(newPersisted |> Map.containsKey "feature/gone", Is.False,
            "a branch outside knownBranches must be pruned from the store")
        Assert.That(newPersisted |> Map.containsKey "feature/x", Is.True,
            "a still-known branch's record must survive pruning")
        Assert.That(effective |> Map.containsKey "feature/gone", Is.False,
            "a pruned branch must not be overlaid into the effective map")

    // (d) upsert newly-observed merged PRs, keeping only Id/Title/Url.
    [<Test>]
    member _.``upserts a newly observed live merged PR, persisting only Id/Title/Url``() =
        let live =
            Map.ofList [ "feature/x", mergedLivePr 77 "Freshly merged" "https://example.test/pull/77" ]

        let _, newPersisted = reconcileMergedPrs live Map.empty (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" newPersisted,
            Is.EqualTo(Some(mk 77 "Freshly merged" "https://example.test/pull/77")),
            "a live merged PR must be recorded, dropping every volatile field")

    // (d, variant) the "up" in upsert: refresh an existing record from the latest live merged PR.
    [<Test>]
    member _.``upsert refreshes an existing record from the live merged PR``() =
        let persisted = Map.ofList [ "feature/x", mk 1 "stale" "https://example.test/pull/1" ]
        let live = Map.ofList [ "feature/x", mergedLivePr 2 "renamed" "https://example.test/pull/2" ]

        let _, newPersisted = reconcileMergedPrs live persisted (Some(Set.ofList [ "feature/x" ]))

        Assert.That(Map.tryFind "feature/x" newPersisted,
            Is.EqualTo(Some(mk 2 "renamed" "https://example.test/pull/2")),
            "an already-persisted branch must be updated to the latest live merged PR")

    // (e, canonical) a still-live merged PR re-reported identically each refresh must be a no-op
    // write: the upsert path runs but re-adds an equal record, so the store stays unchanged and the
    // live PR (with its volatile fields) still wins over the persisted reconstruction.
    [<Test>]
    member _.``a re-reported live merged PR identical to its persisted record is a no-op write``() =
        let record = mk 12 "Add X" "https://example.test/pull/12"
        let persisted = Map.ofList [ "feature/x", record ]
        let live = Map.ofList [ "feature/x", mergedLivePr 12 "Add X" "https://example.test/pull/12" ]

        let effective, newPersisted = reconcileMergedPrs live persisted (Some(Set.ofList [ "feature/x" ]))

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

        let effective, newPersisted = reconcileMergedPrs live persisted (Some known)

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

        let effective, newPersisted = reconcileMergedPrs Map.empty persisted None

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

        let _, newPersisted = reconcileMergedPrs live Map.empty None

        Assert.That(Map.tryFind "feature/new" newPersisted,
            Is.EqualTo(Some(mk 88 "Just merged" "https://example.test/pull/88")),
            "a live merged PR must still be recorded even when pruning is skipped")

// pruneScope decides whether the live-derived branch enumeration is trustworthy enough to prune
// against (review F7). Pure, so these run parallel with no setup.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MergedPrPruneScopeTests() =

    // Complete: every known worktree path has collected git data -> trust the enumeration.
    [<Test>]
    member _.``trusts the enumeration when every known worktree has collected git data``() =
        let known = Set.ofList [ "/wt/a"; "/wt/b" ]
        let collected = Set.ofList [ "/wt/a"; "/wt/b" ]
        let branches = Set.ofList [ "feature/a"; "feature/b" ]

        Assert.That(pruneScope known collected branches, Is.EqualTo(Some branches),
            "a fully collected worktree set proves the branch enumeration - prune against it")

    // Partial: a worktree's git data is missing (a RefreshGit timeout never posted UpdateGit).
    [<Test>]
    member _.``refuses to prune when a known worktree has no collected git data``() =
        let known = Set.ofList [ "/wt/a"; "/wt/b" ]
        let collected = Set.ofList [ "/wt/a" ] // /wt/b's git data never arrived
        let branches = Set.ofList [ "feature/a" ]

        Assert.That(pruneScope known collected branches |> Option.isNone, Is.True,
            "a partially collected worktree set must not be used to prune (review F7)")

    // Unready or a transient empty worktree list: no known worktrees -> never prune.
    [<Test>]
    member _.``refuses to prune when there are no known worktrees``() =
        Assert.That(pruneScope Set.empty Set.empty Set.empty |> Option.isNone, Is.True,
            "an empty worktree set (unready or a transient empty list) must not prune the store")

    // Correlated upstream-read failure: every worktree is collected (paths complete) yet resolves
    // NO branch, so the enumeration collapses to empty. Pruning against it would wipe the WHOLE
    // store, so an empty enumeration must be refused even when path-completeness holds (review F7).
    [<Test>]
    member _.``refuses to prune when the worktree set is complete but resolves no branches``() =
        let known = Set.ofList [ "/wt/a"; "/wt/b" ]
        let collected = Set.ofList [ "/wt/a"; "/wt/b" ] // all collected...
        let branches = Set.empty // ...but every upstream read returned nothing

        Assert.That(pruneScope known collected branches |> Option.isNone, Is.True,
            "a complete worktree set that resolved zero branches must NOT prune - it would wipe the whole store (review F7)")

// End-to-end integration (task tm-pr-recency-window-54z, spec docs/spec/merged-pr-persistence.md).
// Exercises the REAL disk I/O of MergedPrStore (persistAtPath/loadAtPath) wired to the pure
// reconcileMergedPrs against a temp data dir. Uses the path-injectable internal persist/load — NOT
// the singleton getForRepo/setForRepo, which are bound to the default data/merged-prs.json — so the
// test only ever writes to a temp file, never the repo data dir. Each numbered member maps 1:1 to a
// falsifiable step in the task's integration scope.
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

    // A live OPEN (non-merged) PR on the SAME branch but Id = 99, for the "live wins" step.
    let liveOpen99 =
        HasPr
            { Id = 99
              Title = "Reopened work"
              Url = "https://example.test/pull/99"
              IsDraft = false
              Comments = WithResolution(0, 0)
              Builds = []
              IsMerged = false
              HasConflicts = false }

    let record42 = mk 42 "Add X" "https://example.test/pull/42"
    let known b = Some(Set.ofList [ b ])
    let persistStore path store = runAsync (persistAtPath path store)
    let recordOnDisk path = loadAtPath path |> Map.tryFind repo |> Option.bind (Map.tryFind branch)

    // (1) Persist-on-observe: a freshly observed live merged PR is upserted and survives to disk as
    // repo -> feature/x -> {Id=42}. FAIL: file or record absent.
    [<Test>]
    member _.``step 1 - persist-on-observe writes repo -> feature/x -> Id 42 to the temp file``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")

            let _, newPersisted = reconcileMergedPrs (Map.ofList [ branch, liveMerged42 ]) Map.empty (known branch)
            persistStore path (Map.ofList [ repo, newPersisted ])

            Assert.That(File.Exists path, Is.True, "step 1 FAIL: persisted file is absent")
            Assert.That(recordOnDisk path, Is.EqualTo(Some record42),
                "step 1 FAIL: temp JSON must contain repo -> feature/x -> {Id=42}")

            let raw = File.ReadAllText path
            Assert.That(raw.Contains "feature/x" && raw.Contains "42", Is.True,
                "step 1 FAIL: raw JSON on disk must carry the branch and PR id"))

    // (2) Fallback after aging out: an empty live map (PR aged out of the bounded fetch) is overlaid
    // from the persisted record read back off disk. FAIL: NoPr/missing.
    [<Test>]
    member _.``step 2 - aged-out branch is overlaid as HasPr merged Id 42 from the persisted file``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            persistStore path (Map.ofList [ repo, Map.ofList [ branch, record42 ] ])

            let persisted = loadAtPath path |> Map.find repo
            let effective, _ = reconcileMergedPrs Map.empty persisted (known branch)

            match Map.tryFind branch effective with
            | Some(HasPr pr) ->
                Assert.That(pr.IsMerged, Is.True, "step 2 FAIL: overlaid PR must be merged")
                Assert.That(pr.Id, Is.EqualTo 42, "step 2 FAIL: overlaid PR must be Id 42")
            | other -> Assert.Fail $"step 2 FAIL: expected HasPr merged Id=42, got {other}")

    // (3) Restart durability: observe -> persist -> DROP in-memory -> reload fresh from disk -> the
    // merged badge is still restored on the next reconcile. The reload is the simulated restart.
    // FAIL: empty after reload.
    [<Test>]
    member _.``step 3 - merged PR survives a persist plus fresh reload (simulated restart)``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")

            let _, observed = reconcileMergedPrs (Map.ofList [ branch, liveMerged42 ]) Map.empty (known branch)
            persistStore path (Map.ofList [ repo, observed ])

            // Simulated restart: nothing kept in memory; the store is re-read from disk.
            let afterRestart = loadAtPath path |> Map.find repo
            let effective, _ = reconcileMergedPrs Map.empty afterRestart (known branch)

            match Map.tryFind branch effective with
            | Some(HasPr pr) ->
                Assert.That(pr.IsMerged && pr.Id = 42, Is.True,
                    "step 3 FAIL: merged PR (Id 42) must be restored after reload")
            | other -> Assert.Fail $"step 3 FAIL: merged PR lost after reload, got {other}")

    // (4) Prune: a branch no longer known is dropped from newPersisted AND the overlay, and the
    // pruned store persisted back no longer carries it on disk. FAIL: still present.
    [<Test>]
    member _.``step 4 - an unknown branch is pruned from the store, overlay, and the file``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            persistStore path (Map.ofList [ repo, Map.ofList [ branch, record42 ] ])

            let persisted = loadAtPath path |> Map.find repo
            let effective, newPersisted = reconcileMergedPrs Map.empty persisted (known "feature/other")

            Assert.That(newPersisted |> Map.containsKey branch, Is.False,
                "step 4 FAIL: pruned branch must be dropped from newPersisted")
            Assert.That(effective |> Map.containsKey branch, Is.False,
                "step 4 FAIL: pruned branch must not be overlaid into effectiveMap")

            persistStore path (Map.ofList [ repo, newPersisted ])
            Assert.That(recordOnDisk path |> Option.isNone, Is.True,
                "step 4 FAIL: pruned record must be gone from the persisted file")
            Assert.That((File.ReadAllText path).Contains "feature/x", Is.False,
                "step 4 FAIL: raw JSON must no longer mention the pruned branch"))

    // (5) Live wins: a persisted merged record must never override a live non-merged PR on the same
    // branch — the overlay is fallback-only (Decision #3). FAIL: overlay overrides live.
    [<Test>]
    member _.``step 5 - a live non-merged PR beats the persisted merged record (Id 99 kept)``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            persistStore path (Map.ofList [ repo, Map.ofList [ branch, record42 ] ])

            let persisted = loadAtPath path |> Map.find repo
            let effective, _ = reconcileMergedPrs (Map.ofList [ branch, liveOpen99 ]) persisted (known branch)

            match Map.tryFind branch effective with
            | Some(HasPr pr) ->
                Assert.That(pr.Id, Is.EqualTo 99, "step 5 FAIL: the live entry (Id 99) must be kept")
                Assert.That(pr.IsMerged, Is.False, "step 5 FAIL: overlay must not flip live to merged")
            | other -> Assert.Fail $"step 5 FAIL: expected live HasPr Id=99, got {other}")

    // (6) Corruption resilience: garbage bytes on disk load as an empty store and never throw — this
    // path runs at server startup, so a throw would crash boot. FAIL: exception propagates or store
    // is non-empty. (Reaching the assertion proves loadAtPath returned rather than threw.)
    [<Test>]
    member _.``step 6 - a corrupt store file loads as empty without throwing``() =
        withTempDir (fun dir ->
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
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")

            // observe + persist
            let _, observed = reconcileMergedPrs (Map.ofList [ branch, liveMerged42 ]) Map.empty (known branch)
            persistStore path (Map.ofList [ repo, observed ])

            // simulated restart + aged-out fallback (empty live map)
            let afterRestart = loadAtPath path |> Map.find repo
            let effective, still = reconcileMergedPrs Map.empty afterRestart (known branch)
            Assert.That(Map.tryFind branch effective, Is.EqualTo(Some(reconstructed record42)),
                "full lifecycle FAIL: aged-out branch must stay merged after reload")

            // prune once the branch is no longer known, persist, confirm the file is empty of it
            let _, pruned = reconcileMergedPrs Map.empty still (known "feature/other")
            persistStore path (Map.ofList [ repo, pruned ])
            Assert.That(recordOnDisk path |> Option.isNone, Is.True,
                "full lifecycle FAIL: pruned branch must be absent from the reloaded file"))
