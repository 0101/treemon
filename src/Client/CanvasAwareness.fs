module CanvasAwareness

open Shared
open Navigation
open CanvasTypes

/// SystemViews self-refresh from data rather than authored content, so they contribute neither
/// awareness signals nor authored-document inventory on worktree cards.
let agentDocs (docs: CanvasDoc list) : CanvasDoc list =
    docs |> List.filter (fun d -> d.Kind = AgentDoc)

/// The worktree in `repos` whose path matches `scopedKey` (None if none). The single repos-level
/// worktree lookup, shared by the awareness pickers and CanvasState's doc helpers.
let findWorktreeByScopedKey (repos: RepoModel list) (scopedKey: string) =
    repos |> List.tryPick (fun r -> r.Worktrees |> List.tryFind (fun wt -> WorktreePath.value wt.Path = scopedKey))

/// Record the current hash for a known canvas doc. Unknown worktrees/docs and already-recorded
/// hashes leave the map unchanged.
let markDocViewed
    (repos: RepoModel list)
    (lastViewedHashes: Map<string, Map<string, string>>)
    (scopedKey: string)
    (filename: string)
    =
    findWorktreeByScopedKey repos scopedKey
    |> Option.bind (fun wt ->
        wt.CanvasDocs
        |> List.tryFind (fun doc -> doc.Filename = filename)
        |> Option.map _.ContentHash)
    |> Option.map (fun hash ->
        let viewedHashes =
            lastViewedHashes
            |> Map.tryFind scopedKey
            |> Option.defaultValue Map.empty
            |> Map.add filename hash
        lastViewedHashes |> Map.add scopedKey viewedHashes)
    |> Option.defaultValue lastViewedHashes

let seedLastViewedHashes (repos: RepoModel list) (hashes: Map<string, Map<string, string>>) =
    repos
    |> List.fold (fun acc r ->
        r.Worktrees
        |> List.fold (fun acc2 wt ->
            let scopedKey = WorktreePath.value wt.Path
            let existing = acc2 |> Map.tryFind scopedKey |> Option.defaultValue Map.empty
            let withSeeded =
                agentDocs wt.CanvasDocs
                |> List.fold (fun inner doc ->
                    if inner |> Map.containsKey doc.Filename then inner
                    else inner |> Map.add doc.Filename doc.ContentHash) existing
            if withSeeded = existing then acc2
            else acc2 |> Map.add scopedKey withSeeded) acc) hashes

/// A doc is unviewed when its current ContentHash differs from the last viewed hash for that
/// filename (a missing entry means it was never viewed). This is the single unviewed predicate,
/// shared by badge counts, focus retargeting, and worktree-card document previews.
let isUnviewed (viewedHashes: Map<string, string>) (doc: CanvasDoc) : bool =
    match viewedHashes |> Map.tryFind doc.Filename with
    | Some hash -> hash <> doc.ContentHash
    | None -> true

let unviewedDocsByScopedKey (repos: RepoModel list) (lastViewedHashes: Map<string, Map<string, string>>) : Map<string, string list> =
    repos
    |> List.collect (fun r ->
        r.Worktrees
        |> List.choose (fun wt ->
            let scopedKey = WorktreePath.value wt.Path
            let viewedHashes =
                lastViewedHashes
                |> Map.tryFind scopedKey
                |> Option.defaultValue Map.empty
            let unviewed =
                agentDocs wt.CanvasDocs
                |> List.filter (isUnviewed viewedHashes)
                |> List.map _.Filename
            if List.isEmpty unviewed then None
            else Some (scopedKey, unviewed)))
    |> Map.ofList

/// Filename of the worktree's most recently modified *unviewed* AgentDoc (None if none).
/// Drives the active-user "select the worktree surfaces the newly published/updated doc" path:
/// on a focus transition onto a card we retarget its ActiveCanvasDoc to this doc instead of the
/// sticky last-open one. SystemView docs are excluded (via agentDocs) and viewed docs are
/// filtered out (via isUnviewed), so a SystemView or an already-seen doc never wins.
let mostRecentUnviewedDoc (repos: RepoModel list) (lastViewedHashes: Map<string, Map<string, string>>) (scopedKey: string) : string option =
    findWorktreeByScopedKey repos scopedKey
    |> Option.bind (fun wt ->
        let viewedHashes = lastViewedHashes |> Map.tryFind scopedKey |> Option.defaultValue Map.empty
        agentDocs wt.CanvasDocs
        |> List.filter (isUnviewed viewedHashes)
        |> List.sortByDescending _.LastModified
        |> List.tryHead
        |> Option.map _.Filename)

let private canvasDocFieldByScopedKey (project: CanvasDoc -> 'a) (repos: RepoModel list) : Map<string, Map<string, 'a>> =
    repos
    |> List.collect (fun r ->
        r.Worktrees
        |> List.choose (fun wt ->
            let fields =
                agentDocs wt.CanvasDocs
                |> List.map (fun d -> d.Filename, project d)
                |> Map.ofList
            if Map.isEmpty fields then None
            else Some (WorktreePath.value wt.Path, fields)))
    |> Map.ofList

/// filename -> ContentHash per worktree scopedKey (AgentDocs only). Stored as PreviousCanvasHashes
/// and diffed by detectChangedCanvasDocs to find appeared/changed docs.
let canvasHashesByScopedKey (repos: RepoModel list) : Map<string, Map<string, string>> =
    repos |> canvasDocFieldByScopedKey _.ContentHash

/// filename -> real file LastModified per worktree scopedKey (AgentDocs only). Feeds the freshness
/// gate so a doc's *actual* write time — not merely its (re)appearance in a poll — decides whether
/// a detected change counts as a genuine publish/update.
let canvasModifiedByScopedKey (repos: RepoModel list) : Map<string, Map<string, System.DateTimeOffset>> =
    repos |> canvasDocFieldByScopedKey _.LastModified

let canvasDocFreshnessMs = 5.0 * 60.0 * 1000.0

/// A pre-existing doc that reappears after an incomplete restart snapshot must not steal focus.
let isCanvasDocFresh (now: System.DateTimeOffset) (currentModified: Map<string, Map<string, System.DateTimeOffset>>) (scopedKey: string) (filename: string) : bool =
    let cutoff = now.AddMilliseconds(-canvasDocFreshnessMs)
    currentModified
    |> Map.tryFind scopedKey
    |> Option.bind (Map.tryFind filename)
    |> Option.exists (fun lastModified -> lastModified > cutoff)

let detectChangedCanvasDocs (previous: Map<string, Map<string, string>>) (current: Map<string, Map<string, string>>) : (string * string) list =
    current
    |> Map.toList
    |> List.collect (fun (scopedKey, docs) ->
        let previousDocs = previous |> Map.tryFind scopedKey |> Option.defaultValue Map.empty
        docs
        |> Map.toList
        |> List.choose (fun (filename, hash) ->
            if previousDocs |> Map.tryFind filename = Some hash then None
            else Some (scopedKey, filename)))

let findMostRecentChangedDoc (repos: RepoModel list) (changedDocs: (string * string) list) =
    changedDocs
    |> List.choose (fun (scopedKey, filename) ->
        findWorktreeByScopedKey repos scopedKey
        |> Option.bind (fun wt -> agentDocs wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = filename))
        |> Option.map (fun doc -> scopedKey, filename, doc.LastModified))
    |> List.sortByDescending (fun (_, _, lastModified) -> lastModified)
    |> List.tryHead
    |> Option.map (fun (scopedKey, filename, _) -> scopedKey, filename)

/// Delivery signal for a queued canvas message. A queued message is genuinely delivered to the
/// server-side queue and drained when its *target* session registers, so Waiting must never be
/// reported as a failure. We clear Waiting -> Idle once an agent-authored doc *in the target
/// worktree* changes content — what a resumed/launched session does in response. The clear is
/// scoped to the queued message's scopedKey: an unrelated worktree's doc change (common when
/// several sessions are live across worktrees) must NOT dismiss the banner, which would falsely
/// signal delivery while the queued message is in fact still waiting (and may silently expire).
let clearWaitingOnDelivery (sendState: CanvasSendState) (agentChangedDocs: (string * string) list) : CanvasSendState =
    match sendState with
    | CanvasSendState.Waiting scopedKey when agentChangedDocs |> List.exists (fun (sk, _) -> sk = scopedKey) ->
        CanvasSendState.Idle
    | other -> other
