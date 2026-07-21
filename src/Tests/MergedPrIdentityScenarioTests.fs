module Tests.MergedPrIdentityScenarioTests

// Verification scenario (task tm-pr-recency-window-z4d, spec docs/spec/merged-pr-persistence.md
// Decision #11). Proves BRANCH IDENTITY: a worktree shows the correct merge data for the branch it
// ACTUALLY holds, regardless of any past same-named branch. Each numbered member maps 1:1 to a
// falsifiable step in the task scope and exercises the REAL store path end-to-end —
// `reconcileMergedPrs` for the reconcile decision plus the path-injectable `persistAtPath` /
// `loadAtPath` for genuine disk round-trips (steps 1 & 3), with a legacy JSON file loaded off disk
// for step 4. These are NOT config-only checks: the merged fact is written, reloaded, and gated on
// real 40-hex worktree tip SHAs.

open System.IO
open System.Security.Cryptography
open System.Text
open NUnit.Framework
open Shared
open Server.MergedPrStore
open Tests.TestUtils

// A genuine 40-hex-char lowercase SHA-1, so the identity gate is exercised on real commit-shaped
// SHAs (not the "sha-X" toys used by the pure unit tests). Distinct seeds => distinct incarnations.
let private sha (seed: string) : string =
    use h = SHA1.Create()
    seed
    |> Encoding.UTF8.GetBytes
    |> h.ComputeHash
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""

let private shaX = sha "feature/x @ original merged incarnation"
let private shaY = sha "feature/x @ reused-name unrelated incarnation"
let private shaZ = sha "feature/x @ legacy record current tip"

let private repo = RepoId "C:/code/repo-a"
let private branch = "feature/x"
let private known b = Some(Set.ofList [ b ])

// A live MERGED PR carrying volatile fields, to prove reconcile stores only Id/Title/Url + HeadSha.
let private liveMerged =
    HasPr
        { Id = 42
          Title = "Add X"
          Url = "https://example.test/pull/42"
          IsDraft = true
          Comments = WithResolution(1, 2)
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

let private withTempDir (action: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), $"treemon-mergedpr-identity-{System.Guid.NewGuid()}")
    Directory.CreateDirectory(tempDir) |> ignore
    try action tempDir
    finally try Directory.Delete(tempDir, recursive = true) with _ -> ()

let private persistStore path store = runAsync (persistAtPath path store)
let private recordOnDisk path = loadAtPath path |> Map.tryFind repo |> Option.bind (Map.tryFind branch)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MergedPrIdentityScenarioTests() =

    // Step 1 — RECORD. Reconcile a live merged PR for feature/x at tip X (worktreeHeads[feature/x]=X,
    // empty persisted, knownBranches Some{feature/x}) -> persistAtPath -> loadAtPath.
    // PASS: reloaded feature/x record has HeadSha = X. FAIL: HeadSha empty/absent or record missing.
    [<Test>]
    member _.``step 1 - records feature/x stamped with worktree tip X, surviving persist and reload``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            let worktreeHeads = Map.ofList [ branch, Set.ofList [ shaX ] ]

            let _, observed =
                reconcileMergedPrs (Map.ofList [ branch, liveMerged ]) Map.empty worktreeHeads (known branch)

            persistStore path (Map.ofList [ repo, observed ])

            // The 40-hex tip must be genuinely durable: assert on the bytes RELOADED from disk.
            let reloaded = recordOnDisk path
            Assert.That(reloaded.IsSome, Is.True,
                "step 1 FAIL: the reloaded store must still contain a feature/x record")
            Assert.That(reloaded |> Option.map (fun r -> r.HeadSha), Is.EqualTo(Some shaX),
                "step 1 FAIL: reloaded feature/x record must carry HeadSha = X (the worktree tip)")
            Assert.That(shaX.Length, Is.EqualTo(40),
                "step 1 sanity: X must be a real 40-hex SHA"))

    // Step 2 — AGE OUT, SAME INCARNATION. livePrMap empty, persisted = step-1 record (HeadSha=X),
    // worktreeHeads[feature/x]=X, knownBranches Some{feature/x} -> reconcile.
    // PASS: effective map has feature/x = HasPr{IsMerged=true} AND newPersisted still contains it.
    // FAIL: no overlay OR record dropped.
    [<Test>]
    member _.``step 2 - overlays merged badge and keeps the record when the aged-out tip still matches X``() =
        let record = { Id = 42; Title = "Add X"; Url = "https://example.test/pull/42"; HeadSha = shaX }
        let persisted = Map.ofList [ branch, record ]
        let worktreeHeads = Map.ofList [ branch, Set.ofList [ shaX ] ]

        let effective, newPersisted = reconcileMergedPrs Map.empty persisted worktreeHeads (known branch)

        Assert.That(Map.tryFind branch effective, Is.EqualTo(Some(reconstructed record)),
            "step 2 FAIL: the aged-out branch must be overlaid as an inert HasPr{IsMerged=true}")
        Assert.That(Map.containsKey branch newPersisted, Is.True,
            "step 2 FAIL: a matching-tip record must be retained in the persisted store")
        Assert.That(newPersisted |> Map.tryFind branch |> Option.map (fun r -> r.HeadSha), Is.EqualTo(Some shaX),
            "step 2 FAIL: the retained record must keep its stamped identity X")

    // Step 3 — BRANCH REUSE. livePrMap empty, persisted feature/x HeadSha=X, worktreeHeads[feature/x]=Y
    // (Y<>X), knownBranches Some{feature/x} -> reconcile -> persistAtPath -> loadAtPath.
    // PASS: effective map has NO HasPr overlay for feature/x (NoPr/absent) AND reloaded store has no
    // feature/x record (evicted). FAIL: stale HasPr overlay OR record still on disk.
    [<Test>]
    member _.``step 3 - evicts the record and drops the overlay when the tip is a different incarnation Y``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            let record = { Id = 42; Title = "Add X"; Url = "https://example.test/pull/42"; HeadSha = shaX }
            let persisted = Map.ofList [ branch, record ]
            let worktreeHeads = Map.ofList [ branch, Set.ofList [ shaY ] ] // reused name, unrelated tip

            let effective, newPersisted = reconcileMergedPrs Map.empty persisted worktreeHeads (known branch)

            let hasOverlay =
                match Map.tryFind branch effective with
                | Some(HasPr _) -> true
                | _ -> false
            Assert.That(hasOverlay, Is.False,
                "step 3 FAIL: a reused-name branch must NOT resurrect a stale merged badge")

            // Persist the reconciled store and confirm the evicted record is gone from the RELOADED file.
            persistStore path (Map.ofList [ repo, newPersisted ])
            Assert.That(recordOnDisk path |> Option.isNone, Is.True,
                "step 3 FAIL: the different-incarnation record must be evicted from the store on disk")
            Assert.That(shaX <> shaY, Is.True, "step 3 sanity: X and Y must be distinct SHAs"))

    // Step 4 — LEGACY FALLBACK. persisted feature/x with HeadSha='' (as LOADED from legacy JSON that
    // lacks head_sha), worktreeHeads[feature/x]=Z, livePrMap empty -> reconcile.
    // PASS: effective map overlays HasPr{IsMerged=true} for feature/x (fallback shown). FAIL: legacy
    // badge dropped.
    [<Test>]
    member _.``step 4 - keeps overlaying a legacy empty-HeadSha record regardless of the current tip Z``() =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "merged-prs.json")
            // A legacy file predating HeadSha: its record carries only id/title/url, no head_sha.
            File.WriteAllText(
                path,
                """{ "C:/code/repo-a": { "feature/x": { "id": 42, "title": "Add X", "url": "https://example.test/pull/42" } } }""")

            let persisted = loadAtPath path |> Map.find repo
            Assert.That(persisted |> Map.tryFind branch |> Option.map (fun r -> r.HeadSha), Is.EqualTo(Some ""),
                "step 4 setup: the legacy record must load with an empty (unverified) HeadSha")

            // A present, differing tip must NOT evict a legacy/unverified record.
            let worktreeHeads = Map.ofList [ branch, Set.ofList [ shaZ ] ]
            let effective, newPersisted = reconcileMergedPrs Map.empty persisted worktreeHeads (known branch)

            let record = { Id = 42; Title = "Add X"; Url = "https://example.test/pull/42"; HeadSha = "" }
            Assert.That(Map.tryFind branch effective, Is.EqualTo(Some(reconstructed record)),
                "step 4 FAIL: a legacy empty-HeadSha record must still be overlaid (badge must not vanish on deploy)")
            Assert.That(Map.containsKey branch newPersisted, Is.True,
                "step 4 FAIL: a legacy record must be retained, not evicted, by the identity gate"))
