module Server.MergedPrStore

open System.IO
open System.Text.Json
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
/// Returns:
///  - `effectiveMap`: `livePrMap` with a reconstructed merged `HasPr` overlaid for every known
///    branch the live fetch did NOT return as `HasPr` but which has a persisted record. A live
///    `HasPr` (merged or not) is never overridden — the overlay is fallback-only (Decision #3).
///  - `newPersisted`: `persisted` with every live `HasPr { IsMerged = true }` upserted (keeping only
///    `Id`/`Title`/`Url`) and every branch outside `knownBranches` pruned (Decision #4), bounding the
///    store by live worktrees. Equals `persisted` when nothing moved, so the caller skips the write.
let reconcileMergedPrs
    (livePrMap: Map<string, PrStatus>)
    (persisted: Map<string, MergedPrRecord>)
    (knownBranches: Set<string>)
    : Map<string, PrStatus> * Map<string, MergedPrRecord> =

    // Record every branch observed as merged in the live map, then prune to the known branches.
    let newPersisted =
        livePrMap
        |> Map.fold
            (fun acc branch status ->
                match status with
                | HasPr pr when pr.IsMerged ->
                    acc |> Map.add branch { Id = pr.Id; Title = pr.Title; Url = pr.Url }
                | _ -> acc)
            persisted
        |> Map.filter (fun branch _ -> Set.contains branch knownBranches)

    // Overlay a reconstructed merged PR for each persisted (already pruned to known) branch the live
    // map is missing as `HasPr`. A live `HasPr` — even a non-merged one — always wins.
    let effectiveMap =
        newPersisted
        |> Map.fold
            (fun acc branch record ->
                match Map.tryFind branch acc with
                | Some(HasPr _) -> acc
                | _ -> acc |> Map.add branch (toMergedPrStatus record))
            livePrMap

    effectiveMap, newPersisted

/// Default on-disk location: gitignored server runtime state, NOT the user-authored `config.json`.
/// Matches `data/canvas-owners.json` (`CanvasDocOwnership.fs`) and `data/sessions.json`.
let private filePath = Path.Combine("data", "merged-prs.json")

type private Msg =
    | GetForRepo of repoId: RepoId * AsyncReplyChannel<Map<string, MergedPrRecord>>
    | SetForRepo of repoId: RepoId * records: Map<string, MergedPrRecord>
    | Load of state: Map<RepoId, Map<string, MergedPrRecord>>

/// Atomically writes the whole store to `path` (temp file + `File.Move`). The path is explicit so
/// tests can point at a temp dir; the agent always calls it with the module default `filePath`.
/// Mirrors `CanvasDocOwnership.persistImpl`: never throws — a persist failure is logged, not raised.
let internal persistAtPath (path: string) (state: Map<RepoId, Map<string, MergedPrRecord>>) =
    async {
        try
            let dir = Path.GetDirectoryName(path)
            if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore

            let options = JsonWriterOptions(Indented = true)
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, options)
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

            writer.WriteEndObject()
            writer.Flush()

            let json = System.Text.Encoding.UTF8.GetString(stream.ToArray())
            let tempPath = path + ".tmp"
            do! File.WriteAllTextAsync(tempPath, json) |> Async.AwaitTask
            File.Move(tempPath, path, overwrite = true)
        with ex ->
            Log.log "MergedPrStore" $"Failed to persist: {ex.Message}"
    }

/// Loads the store from `path`. An absent or corrupt file loads as an empty store and NEVER throws
/// (this runs at server startup, mirroring the template's try/with). The path is explicit so tests
/// can point at a temp dir; `load ()` calls it with the module default `filePath`.
let internal loadAtPath (path: string) : Map<RepoId, Map<string, MergedPrRecord>> =
    try
        if File.Exists(path) then
            let json = File.ReadAllText(path)
            use doc = JsonDocument.Parse(json)

            doc.RootElement.EnumerateObject()
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
                Map.empty
        else
            Map.empty
    with ex ->
        Log.log "MergedPrStore" $"Failed to load: {ex.Message}"
        Map.empty

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
