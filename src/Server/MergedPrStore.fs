module Server.MergedPrStore

open System.IO
open Shared

/// One persisted merged-PR fact — the minimal fields the merged badge renders (`Id`/`Title`/`Url`)
/// plus `HeadSha`, the worktree tip commit at record time (the merged-record identity, Decision #11).
/// Volatile PR data (builds, comments, conflicts, draft) is deliberately never stored; only the
/// terminal "merged" fact survives the bounded GitHub fetch window and server restarts. An empty
/// `HeadSha` means legacy/unverified (pre-existing on-disk data, or a tolerant-load default).
type MergedPrRecord =
    { Id: int
      Title: string
      Url: string
      HeadSha: string }

/// Reconstructs the `PrInfo` a persisted record stands in for. Only the merged fact is stored, so
/// volatile fields get inert defaults; the badge renders from `IsMerged`/`Title`/`Url` (Decision #7).
let private toMergedPrStatus (record: MergedPrRecord) : PrStatus =
    HasPr
        { Id = record.Id
          Title = record.Title
          Url = record.Url
          IsDraft = false
          Comments = WithResolution(0, 0)
          Builds = []
          IsMerged = true
          HasConflicts = false }

/// Pure reconciliation (no I/O) of the live PR map with the persisted merged records. Returns the
/// effective map and the new persisted records (equal to `persisted` when nothing moved, so the
/// caller can skip the write, Decision #6):
///  - upserts every live `HasPr { IsMerged = true }` (keeping only `Id`/`Title`/`Url`, stamping
///    `HeadSha` from `worktreeHeads` — the branch's current worktree tip, Decision #11);
///  - identity-gates by tip BEFORE the name-prune: a record is evicted only on a confirmed mismatch
///    (non-empty `HeadSha` AND a present-but-differing tip = a reused-name incarnation); a match, a
///    missing tip, or an empty (legacy) `HeadSha` is kept (Decision #11);
///  - overlays a reconstructed merged `HasPr` for persisted branches the live map lacks — a live
///    `HasPr` always wins, the overlay is fallback-only, and surviving records are match-or-legacy
///    so the fallback is safe (Decision #3);
///  - prunes to `knownBranches` only when `Some` (a complete, non-empty enumeration). `None` SKIPS
///    pruning so an empty/partial set can never wipe just-loaded facts (review F7 / Decision #8).
/// `worktreeHeads` maps branch -> its current worktree tip SHA (the identity source, Decision #11).
let reconcileMergedPrs
    (livePrMap: Map<string, PrStatus>)
    (persisted: Map<string, MergedPrRecord>)
    (worktreeHeads: Map<string, string>)
    (knownBranches: Set<string> option)
    : Map<string, PrStatus> * Map<string, MergedPrRecord> =

    // Upsert every branch observed as merged — provider ground truth, always safe and additive.
    // Stamp `HeadSha` from the branch's current worktree tip (empty when unknown = legacy/unverified).
    let upserted =
        livePrMap
        |> Map.fold
            (fun acc branch status ->
                match status with
                | HasPr pr when pr.IsMerged ->
                    let headSha = Map.tryFind branch worktreeHeads |> Option.defaultValue ""
                    acc |> Map.add branch { Id = pr.Id; Title = pr.Title; Url = pr.Url; HeadSha = headSha }
                | _ -> acc)
            persisted

    // Identity gate (Decision #11), BEFORE the name-prune: keep a record when its `HeadSha` is empty
    // (legacy/unverified), when its branch has no current tip, or when the tip matches exactly; evict
    // only on a confirmed mismatch (non-empty `HeadSha` AND a present tip that differs = reused name).
    let identityFiltered =
        upserted
        |> Map.filter (fun branch record ->
            if record.HeadSha = "" then
                true
            else
                match Map.tryFind branch worktreeHeads with
                | None -> true
                | Some tip -> record.HeadSha = tip)

    // Prune only against a trustworthy enumeration (`Some`); `None` leaves the store intact (F7).
    let newPersisted =
        match knownBranches with
        | Some branches -> identityFiltered |> Map.filter (fun branch _ -> Set.contains branch branches)
        | None -> identityFiltered

    // Overlay persisted merged PRs for branches the live map lacks as `HasPr`; a live `HasPr` wins.
    let effectiveMap =
        newPersisted
        |> Map.fold
            (fun acc branch record ->
                match Map.tryFind branch acc with
                | Some(HasPr _) -> acc
                | _ -> acc |> Map.add branch (toMergedPrStatus record))
            livePrMap

    effectiveMap, newPersisted

/// Decides the enumeration `reconcileMergedPrs` may prune against (review F7 / Decision #8). Returns
/// `Some knownBranches` only when it is trustworthy:
///  - at least one worktree is known and at least one branch resolved (non-empty);
///  - every known worktree path has collected `GitData` (`knownPaths ⊆ collectedGitPaths`);
///  - no known worktree's upstream read *failed* (`git rev-parse @{u}` timing out / erroring rather
///    than git deterministically reporting no upstream) — such a worktree contributes no branch yet
///    its record could still be live, so pruning against the incomplete set could forget it.
/// Any of these failing yields `None`, so pruning is skipped rather than wiping just-loaded facts.
/// A read failure on a stale path (not in `knownPaths`) is ignored — only tracked worktrees matter.
let pruneScope
    (knownPaths: Set<string>)
    (collectedGitPaths: Set<string>)
    (readFailedPaths: Set<string>)
    (knownBranches: Set<string>)
    : Set<string> option =
    if
        not (Set.isEmpty knownPaths)
        && not (Set.isEmpty knownBranches)
        && Set.isSubset knownPaths collectedGitPaths
        && Set.isEmpty (Set.intersect knownPaths readFailedPaths)
    then
        Some knownBranches
    else
        None

/// Default on-disk location: gitignored server runtime state, NOT the user-authored `config.json`.
/// Matches `data/canvas-owners.json` and `data/sessions.json`.
let private filePath = Path.Combine("data", "merged-prs.json")

/// Serializes the whole store as `repo -> branch -> {id;title;url}` via the shared atomic writer.
/// The path is explicit so tests can target a temp dir.
let internal persistAtPath (path: string) (state: Map<RepoId, Map<string, MergedPrRecord>>) =
    JsonStore.persist "MergedPrStore" path (fun writer ->
        writer.WriteStartObject()

        state
        |> Map.iter (fun (RepoId repoId) branchMap ->
            writer.WritePropertyName(repoId)
            writer.WriteStartObject()

            branchMap
            |> Map.iter (fun branch record ->
                writer.WritePropertyName(branch)
                writer.WriteStartObject()
                writer.WriteNumber("id", record.Id)
                writer.WriteString("title", record.Title)
                writer.WriteString("url", record.Url)
                writer.WriteString("head_sha", record.HeadSha)
                writer.WriteEndObject())

            writer.WriteEndObject())

        writer.WriteEndObject())

/// Loads the store via the shared safe loader; an absent or corrupt file yields an empty store so
/// startup never throws. The path is explicit so tests can target a temp dir.
let internal loadAtPath (path: string) : Map<RepoId, Map<string, MergedPrRecord>> =
    JsonStore.load "MergedPrStore" path (fun root ->
        root.EnumerateObject()
        |> Seq.fold
            (fun acc repoProp ->
                let branchMap =
                    repoProp.Value.EnumerateObject()
                    |> Seq.fold
                        (fun acc branchProp ->
                            let el = branchProp.Value

                            // Read head_sha tolerantly: a missing (legacy file) or non-string
                            // (corrupt) value defaults to "" — an empty HeadSha = legacy/unverified.
                            let headSha =
                                match el.TryGetProperty("head_sha") with
                                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> v.GetString()
                                | _ -> ""

                            let record =
                                { Id = el.GetProperty("id").GetInt32()
                                  Title = el.GetProperty("title").GetString()
                                  Url = el.GetProperty("url").GetString()
                                  HeadSha = headSha }

                            acc |> Map.add branchProp.Name record)
                        Map.empty

                acc |> Map.add (RepoId repoProp.Name) branchMap)
            Map.empty)
    |> Option.defaultValue Map.empty

let private store =
    PersistentStore.create "MergedPrStore" (persistAtPath filePath) (fun () -> loadAtPath filePath)

/// Async read of a repo's persisted merged-PR records (branch -> record); empty when none stored.
let getForRepo (repoId: RepoId) : Async<Map<string, MergedPrRecord>> =
    async {
        let! records = store.Get repoId
        return records |> Option.defaultValue Map.empty
    }

/// Replaces a repo's persisted records, persisting only when they change (Decision #6); an empty
/// map drops the repo key so the file stays minimal.
let setForRepo (repoId: RepoId) (records: Map<string, MergedPrRecord>) =
    store.Update repoId (fun _ -> if Map.isEmpty records then None else Some records)

/// Loads the store at startup. Never throws (absent/corrupt -> empty store).
let load () = store.Load()
