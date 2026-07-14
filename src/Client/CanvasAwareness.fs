module CanvasAwareness

open Shared
open Navigation
open CanvasTypes

type CanvasEventKind = NewDoc | UpdatedDoc

type CanvasEvent =
    { Filename: string
      Timestamp: System.DateTimeOffset
      Kind: CanvasEventKind }

/// Content-hash awareness (unviewed badges, canvas events, seed hashes, idle auto-display)
/// applies only to AgentDocs. A SystemView (the beads dashboard) has a stable file hash while
/// its underlying data changes, so it would never signal on real change and would signal
/// spuriously on a template/deploy edit; beads "newness" is already surfaced on the worktree
/// card via BeadsSummary. Filtering SystemView docs out here is the single chokepoint that keeps
/// every awareness function (and detectCanvasEvents, fed by canvasHashesByScopedKey) consistent.
let private awarenessDocs (docs: CanvasDoc list) : CanvasDoc list =
    docs |> List.filter (fun d -> d.Kind = AgentDoc)

/// The worktree in `repos` whose path matches `scopedKey` (None if none). The single repos-level
/// worktree lookup, shared by the awareness pickers and CanvasState's doc helpers.
let findWorktreeByScopedKey (repos: RepoModel list) (scopedKey: string) =
    repos |> List.tryPick (fun r -> r.Worktrees |> List.tryFind (fun wt -> WorktreePath.value wt.Path = scopedKey))

let seedLastViewedHashes (repos: RepoModel list) (hashes: Map<string, Map<string, string>>) =
    repos
    |> List.fold (fun acc r ->
        r.Worktrees
        |> List.fold (fun acc2 wt ->
            let scopedKey = WorktreePath.value wt.Path
            let existing = acc2 |> Map.tryFind scopedKey |> Option.defaultValue Map.empty
            let withSeeded =
                awarenessDocs wt.CanvasDocs
                |> List.fold (fun inner doc ->
                    if inner |> Map.containsKey doc.Filename then inner
                    else inner |> Map.add doc.Filename doc.ContentHash) existing
            if withSeeded = existing then acc2
            else acc2 |> Map.add scopedKey withSeeded) acc) hashes

/// A doc is unviewed when its current ContentHash differs from the last viewed hash for that
/// filename (a missing entry means it was never viewed). This is the single unviewed predicate,
/// shared by unviewedDocsByScopedKey (badge counts) and mostRecentUnviewedDoc (focus retarget).
let private isUnviewed (viewedHashes: Map<string, string>) (doc: CanvasDoc) : bool =
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
                awarenessDocs wt.CanvasDocs
                |> List.filter (isUnviewed viewedHashes)
                |> List.map _.Filename
            if List.isEmpty unviewed then None
            else Some (scopedKey, unviewed)))
    |> Map.ofList

/// Filename of the worktree's most recently modified *unviewed* AgentDoc (None if none).
/// Drives the active-user "select the worktree surfaces the newly published/updated doc" path:
/// on a focus transition onto a card we retarget its ActiveCanvasDoc to this doc instead of the
/// sticky last-open one. SystemView docs are excluded (via awarenessDocs) and viewed docs are
/// filtered out (via isUnviewed), so a SystemView or an already-seen doc never wins.
let mostRecentUnviewedDoc (repos: RepoModel list) (lastViewedHashes: Map<string, Map<string, string>>) (scopedKey: string) : string option =
    findWorktreeByScopedKey repos scopedKey
    |> Option.bind (fun wt ->
        let viewedHashes = lastViewedHashes |> Map.tryFind scopedKey |> Option.defaultValue Map.empty
        awarenessDocs wt.CanvasDocs
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
                awarenessDocs wt.CanvasDocs
                |> List.map (fun d -> d.Filename, project d)
                |> Map.ofList
            if Map.isEmpty fields then None
            else Some (WorktreePath.value wt.Path, fields)))
    |> Map.ofList

/// filename -> ContentHash per worktree scopedKey (AgentDocs only). Stored as PreviousCanvasHashes
/// and diffed by detectCanvasEvents to find appeared/changed docs.
let canvasHashesByScopedKey (repos: RepoModel list) : Map<string, Map<string, string>> =
    repos |> canvasDocFieldByScopedKey _.ContentHash

/// filename -> real file LastModified per worktree scopedKey (AgentDocs only). Feeds the freshness
/// gate so a doc's *actual* write time — not merely its (re)appearance in a poll — decides whether
/// a detected change counts as a genuine publish/update.
let canvasModifiedByScopedKey (repos: RepoModel list) : Map<string, Map<string, System.DateTimeOffset>> =
    repos |> canvasDocFieldByScopedKey _.LastModified

let canvasEventExpiryMs = 24.0 * 60.0 * 60.0 * 1000.0

/// How recently a doc's file must have been written for a detected hash change to count as a real
/// publish/update. Deliberately short and *separate* from canvasEventExpiryMs (how long a shown
/// event lingers, 24 h): detectCanvasEvents fires on hash presence deltas between polls, so a
/// pre-existing doc that merely reappears after an incomplete snapshot — e.g. right after a server
/// restart, before the canvas scan has populated CanvasData — would otherwise be misreported as
/// "just published". Gating on the real mtime suppresses those phantoms.
let canvasEventFreshnessMs = 5.0 * 60.0 * 1000.0

let detectCanvasEvents (now: System.DateTimeOffset) (previousHashes: Map<string, Map<string, string>>) (currentHashes: Map<string, Map<string, string>>) : Map<string, CanvasEvent list> =
    currentHashes
    |> Map.toList
    |> List.choose (fun (scopedKey, currentDocs) ->
        let prevDocs =
            previousHashes
            |> Map.tryFind scopedKey
            |> Option.defaultValue Map.empty
        let events =
            currentDocs
            |> Map.toList
            |> List.choose (fun (filename, hash) ->
                match prevDocs |> Map.tryFind filename with
                | None -> Some { Filename = filename; Timestamp = now; Kind = NewDoc }
                | Some prevHash when prevHash <> hash -> Some { Filename = filename; Timestamp = now; Kind = UpdatedDoc }
                | _ -> None)
        if List.isEmpty events then None
        else Some (scopedKey, events))
    |> Map.ofList

/// Keep only the events whose doc was genuinely written within the freshness window, restamping
/// each survivor with its real LastModified so the card shows a truthful time. detectCanvasEvents
/// fires on hash *presence* deltas between polls, which misreports a pre-existing doc as new
/// whenever the previous snapshot was incomplete (notably the empty-CanvasDocs window right after a
/// server restart). This gate is where those phantom "published/updated" entries are dropped —
/// see canvasEventFreshnessMs.
let gateCanvasEventsByFreshness (now: System.DateTimeOffset) (currentModified: Map<string, Map<string, System.DateTimeOffset>>) (events: Map<string, CanvasEvent list>) : Map<string, CanvasEvent list> =
    let cutoff = now.AddMilliseconds(-canvasEventFreshnessMs)
    events
    |> Map.toList
    |> List.choose (fun (scopedKey, evts) ->
        let modifiedForKey = currentModified |> Map.tryFind scopedKey |> Option.defaultValue Map.empty
        let fresh =
            evts
            |> List.choose (fun e ->
                modifiedForKey
                |> Map.tryFind e.Filename
                |> Option.filter (fun lastModified -> lastModified > cutoff)
                |> Option.map (fun lastModified -> { e with Timestamp = lastModified }))
        if List.isEmpty fresh then None
        else Some (scopedKey, fresh))
    |> Map.ofList

/// Whether a doc's file was written within the freshness window — the changedDocs (idle
/// auto-display) counterpart to gateCanvasEventsByFreshness, so a reappearing pre-existing doc
/// never focus-steals on restart.
let isCanvasDocFresh (now: System.DateTimeOffset) (currentModified: Map<string, Map<string, System.DateTimeOffset>>) (scopedKey: string) (filename: string) : bool =
    let cutoff = now.AddMilliseconds(-canvasEventFreshnessMs)
    currentModified
    |> Map.tryFind scopedKey
    |> Option.bind (Map.tryFind filename)
    |> Option.exists (fun lastModified -> lastModified > cutoff)

let mergeCanvasEvents (existing: Map<string, CanvasEvent list>) (newEvents: Map<string, CanvasEvent list>) : Map<string, CanvasEvent list> =
    newEvents
    |> Map.fold (fun acc scopedKey evts ->
        let current = acc |> Map.tryFind scopedKey |> Option.defaultValue []
        let merged = current @ evts
        let deduped =
            merged
            |> List.groupBy _.Filename
            |> List.map (fun (_, group) -> group |> List.maxBy _.Timestamp)
        acc |> Map.add scopedKey deduped) existing

let expireCanvasEvents (now: System.DateTimeOffset) (events: Map<string, CanvasEvent list>) : Map<string, CanvasEvent list> =
    let cutoff = now.AddMilliseconds(-canvasEventExpiryMs)
    events
    |> Map.map (fun _ evts -> evts |> List.filter (fun e -> e.Timestamp > cutoff))
    |> Map.filter (fun _ evts -> not (List.isEmpty evts))

let detectChangedCanvasDocs (now: System.DateTimeOffset) (previous: Map<string, Map<string, string>>) (current: Map<string, Map<string, string>>) : (string * string) list =
    detectCanvasEvents now previous current
    |> Map.toList
    |> List.collect (fun (scopedKey, events) ->
        events |> List.map (fun e -> scopedKey, e.Filename))

let findMostRecentChangedDoc (repos: RepoModel list) (changedDocs: (string * string) list) =
    changedDocs
    |> List.choose (fun (scopedKey, filename) ->
        findWorktreeByScopedKey repos scopedKey
        |> Option.bind (fun wt -> awarenessDocs wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = filename))
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
