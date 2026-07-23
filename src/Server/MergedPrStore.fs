module Server.MergedPrStore

open System.IO
open Shared

/// One persisted merged-PR fact — the minimal fields the merged badge renders (`Id`/`Title`/`Url`).
/// Volatile PR data (builds, comments, conflicts, draft) is deliberately never stored; only the
/// terminal "merged" fact survives the bounded GitHub fetch window and server restarts.
type MergedPrRecord =
    { Id: int
      Title: string
      Url: string }

/// Reconstructs the full `PrInfo` a persisted record stands in for. Only the terminal merged fact
/// is stored, so the volatile fields (`IsDraft`/`Comments`/`Builds`/`HasConflicts`) are filled with
/// inert defaults. The merged badge renders from `IsMerged`/`Title`/`Url` alone, so this is
/// indistinguishable from a live merged PR to the client (Decision #7).
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

/// Pure reconciliation (no I/O) between the live PR map, the persisted merged-PR records, and the
/// repo's currently known branches. The effectful store calls this and persists `newPersisted` only
/// when it differs from what it loaded (Decision #6), so this stays unit-testable in isolation.
///
/// `knownBranches` gates pruning (Decision #8):
///  - `Some branches`: the caller (via `pruneScope`) has proven a COMPLETE, non-empty live-worktree
///    enumeration, so records for branches outside `branches` are pruned, bounding the store by live
///    worktrees (Decision #4). `pruneScope` never yields `Some ∅`, so an automatic refresh cannot
///    prune the whole store to empty.
///  - `None`: the enumeration is unavailable or incomplete (git-data collection unready, a
///    `collectWorktreeGitData` timeout, or a transient short worktree list that dropped GitData).
///    Pruning is SKIPPED so an empty/partial set can never wipe just-loaded merged-PR facts
///    (review F7). Upserts and the overlay still run; the store is re-bounded on a later refresh
///    once a complete enumeration is available.
///
/// Returns:
///  - `effectiveMap`: `livePrMap` with a reconstructed merged `HasPr` overlaid for every persisted
///    branch the live fetch did NOT return as `HasPr`. A live `HasPr` (merged or not) is never
///    overridden — the overlay is fallback-only (Decision #3).
///  - `newPersisted`: `persisted` with every live `HasPr { IsMerged = true }` upserted (keeping only
///    `Id`/`Title`/`Url`) and, when `knownBranches` is `Some`, every branch outside it pruned. Equals
///    `persisted` when nothing moved, so the caller skips the write.
let reconcileMergedPrs
    (livePrMap: Map<string, PrStatus>)
    (persisted: Map<string, MergedPrRecord>)
    (knownBranches: Set<string> option)
    : Map<string, PrStatus> * Map<string, MergedPrRecord> =

    // Record every branch observed as merged in the live map. This is always safe and purely
    // additive: a live `HasPr { IsMerged = true }` is provider ground truth, independent of local
    // git state, so it is upserted regardless of whether pruning is currently trustworthy.
    let upserted =
        livePrMap
        |> Map.fold
            (fun acc branch status ->
                match status with
                | HasPr pr when pr.IsMerged ->
                    acc |> Map.add branch { Id = pr.Id; Title = pr.Title; Url = pr.Url }
                | _ -> acc)
            persisted

    // Prune to the known branches ONLY against a trustworthy enumeration (`Some`). `None` leaves the
    // store intact (review F7): an empty/partial known-branch set must never destroy persisted facts.
    let newPersisted =
        match knownBranches with
        | Some branches -> upserted |> Map.filter (fun branch _ -> Set.contains branch branches)
        | None -> upserted

    // Overlay a reconstructed merged PR for each persisted branch the live map is missing as
    // `HasPr`. A live `HasPr` — even a non-merged one — always wins.
    let effectiveMap =
        newPersisted
        |> Map.fold
            (fun acc branch record ->
                match Map.tryFind branch acc with
                | Some(HasPr _) -> acc
                | _ -> acc |> Map.add branch (toMergedPrStatus record))
            livePrMap

    effectiveMap, newPersisted

/// Decides the branch enumeration `reconcileMergedPrs` may safely prune against (review F7).
/// `knownBranches` is derived from live git data, which is only trustworthy once EVERY known
/// worktree has a collected `GitData` entry and at least one worktree exists. Even then the set is
/// rejected when it is EMPTY: a collected worktree can still contribute no branch — a transient
/// `git rev-parse @{u}` failure (a shared-`.git` lock, `git gc` repacking refs) degrades to
/// `UpstreamBranch = None`, and if that hits every worktree at once, path-completeness would hold
/// while the enumeration collapsed to ∅, pruning the WHOLE store to empty. An empty enumeration is
/// never a trustworthy basis to drop records (the live fetch also returned nothing to reconcile
/// against), so it yields `None`. Returns `Some knownBranches` only when the enumeration is complete
/// AND non-empty, else `None` (skip pruning, keep the store intact).
let pruneScope
    (knownPaths: Set<string>)
    (collectedGitPaths: Set<string>)
    (knownBranches: Set<string>)
    : Set<string> option =
    if
        not (Set.isEmpty knownPaths)
        && not (Set.isEmpty knownBranches)
        && Set.isSubset knownPaths collectedGitPaths
    then
        Some knownBranches
    else
        None

/// Default on-disk location: gitignored server runtime state, NOT the user-authored `config.json`.
/// Matches `data/canvas-owners.json` (`CanvasDocOwnership.fs`) and `data/sessions.json`.
let private filePath = Path.Combine("data", "merged-prs.json")

type private Msg =
    | GetForRepo of repoId: RepoId * AsyncReplyChannel<Map<string, MergedPrRecord>>
    | SetForRepo of repoId: RepoId * records: Map<string, MergedPrRecord>
    | Load of state: Map<RepoId, Map<string, MergedPrRecord>>

/// Serializes the whole store as `repo -> branch -> {id;title;url}` and hands it to the shared
/// atomic writer (`JsonStore.persist`: temp file + `File.Move`, never throws — a persist failure is
/// logged, not raised). The path is explicit so tests can point at a temp dir; the agent always
/// calls it with the module default `filePath`.
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
                writer.WriteEndObject())

            writer.WriteEndObject())

        writer.WriteEndObject())

/// Loads the store from `path` via the shared safe loader (`JsonStore.load`), keeping only the
/// nested `repo -> branch -> record` fold; an absent or corrupt file yields `None`, defaulted to an
/// empty store so server startup NEVER throws. The path is explicit so tests can point at a temp
/// dir; `load ()` calls it with the module default `filePath`.
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

                            let record =
                                { Id = el.GetProperty("id").GetInt32()
                                  Title = el.GetProperty("title").GetString()
                                  Url = el.GetProperty("url").GetString() }

                            acc |> Map.add branchProp.Name record)
                        Map.empty

                acc |> Map.add (RepoId repoProp.Name) branchMap)
            Map.empty)
    |> Option.defaultValue Map.empty

let private agent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (state: Map<RepoId, Map<string, MergedPrRecord>>) =
            async {
                let! msg = inbox.Receive()

                match msg with
                | GetForRepo(repoId, reply) ->
                    state
                    |> Map.tryFind repoId
                    |> Option.defaultValue Map.empty
                    |> reply.Reply

                    return! loop state

                | SetForRepo(repoId, records) ->
                    let current =
                        state |> Map.tryFind repoId |> Option.defaultValue Map.empty

                    // Change-persisting: only touch the disk when this repo's records actually
                    // moved (Decision #6). An empty map drops the repo key so the file stays minimal.
                    if records = current then
                        return! loop state
                    else
                        let state' =
                            if Map.isEmpty records then
                                state |> Map.remove repoId
                            else
                                state |> Map.add repoId records

                        do! persistAtPath filePath state'
                        return! loop state'

                | Load loaded ->
                    Log.log "MergedPrStore" $"Loaded merged PRs for {Map.count loaded} repo(s)"
                    return! loop loaded
            }

        loop Map.empty)

/// Async read of a repo's persisted merged-PR records (branch -> record); empty when none stored.
let getForRepo (repoId: RepoId) =
    agent.PostAndAsyncReply(fun reply -> GetForRepo(repoId, reply))

/// Replaces a repo's persisted records, persisting to disk only when they differ from the current
/// store (fire-and-forget; serialized through the agent).
let setForRepo (repoId: RepoId) (records: Map<string, MergedPrRecord>) =
    agent.Post(SetForRepo(repoId, records))

/// Loads the store from the default path at startup. Never throws (absent/corrupt -> empty store).
let load () =
    agent.Post(Load(loadAtPath filePath))
