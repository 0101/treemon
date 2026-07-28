module Server.MergedPrStore

open System.IO
open Shared

/// One persisted merged-PR fact. `HeadSha` is the provider-reported PR source commit, so later
/// commits on a reused or advanced branch cannot inherit the old merged state.
type MergedPrRecord =
    { Id: int
      Title: string
      Url: string
      HeadSha: string }

/// Reconstructs the `PrInfo` a persisted record stands in for. Only the merged fact is stored, so
/// volatile fields get inert defaults; the badge renders from `IsMerged`/`Title`/`Url`.
let private toMergedPrStatus (record: MergedPrRecord) : PrStatus =
    HasPr
        { Id = record.Id
          Title = record.Title
          Url = record.Url
          IsDraft = false
          Comments = WithResolution(0, 0)
          Builds = []
          IsOpen = false
          IsMerged = true
          HasConflicts = false }

/// Reconciles live provider data with durable merged records. Provider head SHAs are immutable PR
/// identities; local worktree tips only decide whether a stored identity still describes any
/// current worktree for that branch.
let reconcileMergedPrs
    (livePrMap: Map<string, PrStatus>)
    (liveHeadShas: Map<string, string>)
    (persisted: Map<string, MergedPrRecord>)
    (worktreeHeads: Map<string, Set<string>>)
    (knownBranches: Set<string> option)
    : Map<string, PrStatus> * Map<string, MergedPrRecord> =

    /// A SHA still describes the branch when it matches any observed worktree tip for it, or when
    /// no worktree currently tracks the branch (nothing local can disprove the identity).
    let describesBranch branch sha =
        match Map.tryFind branch worktreeHeads with
        | Some tips -> Set.contains sha tips
        | None -> true

    let identityValidatedLive =
        livePrMap
        |> Map.filter (fun branch status ->
            match status with
            | HasPr pr when pr.IsMerged ->
                liveHeadShas |> Map.tryFind branch |> Option.forall (describesBranch branch)
            | _ -> true)

    let upserted =
        identityValidatedLive
        |> Map.fold
            (fun acc branch status ->
                match status with
                | HasPr pr when pr.IsMerged ->
                    match liveHeadShas |> Map.tryFind branch |> Option.filter (System.String.IsNullOrWhiteSpace >> not) with
                    | Some headSha ->
                        acc |> Map.add branch { Id = pr.Id; Title = pr.Title; Url = pr.Url; HeadSha = headSha }
                    | _ -> acc
                | _ -> acc)
            persisted

    let identityFiltered =
        upserted |> Map.filter (fun branch record -> describesBranch branch record.HeadSha)

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
            identityValidatedLive

    effectiveMap, newPersisted

/// Decides the enumeration `reconcileMergedPrs` may prune against. Returns `Some knownBranches`
/// only when it is trustworthy:
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

let filePathForPort port = Path.Combine("data", $"merged-prs-{port}.json")

let private writeState (writer: System.Text.Json.Utf8JsonWriter) (state: Map<RepoId, Map<string, MergedPrRecord>>) =
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

    writer.WriteEndObject()

let internal tryPersistAtPath (path: string) (state: Map<RepoId, Map<string, MergedPrRecord>>) =
    JsonStore.tryPersist "MergedPrStore" path (fun writer -> writeState writer state)

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

                            let headSha =
                                match el.TryGetProperty("head_sha") with
                                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String ->
                                    v.GetString() |> Option.ofObj |> Option.filter (System.String.IsNullOrWhiteSpace >> not)
                                | _ -> None

                            headSha
                            |> Option.map (fun sha ->
                                { Id = el.GetProperty("id").GetInt32()
                                  Title = el.GetProperty("title").GetString()
                                  Url = el.GetProperty("url").GetString()
                                  HeadSha = sha })
                            |> Option.map (fun record -> acc |> Map.add branchProp.Name record)
                            |> Option.defaultValue acc)
                        Map.empty

                if Map.isEmpty branchMap then acc
                else acc |> Map.add (RepoId repoProp.Name) branchMap)
            Map.empty)
    |> Option.defaultValue Map.empty

type Store = PersistentStore.Store<RepoId, Map<string, MergedPrRecord>>

let create path : Store =
    PersistentStore.create "MergedPrStore" (tryPersistAtPath path) (fun () -> loadAtPath path)

let getForRepo (store: Store) repoId =
    async {
        let! records = store.Get repoId
        return records |> Option.defaultValue Map.empty
    }

/// An empty record map is stored as absence, so a repo that loses its last merged PR disappears
/// from the persisted document instead of leaving an empty object behind.
let setForRepo (store: Store) repoId records =
    store.Update repoId (fun _ -> if Map.isEmpty records then None else Some records)
