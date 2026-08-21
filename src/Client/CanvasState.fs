module CanvasState

open Shared
open Navigation
open CanvasTypes
open CanvasAwareness

/// The canvas pane's slice of the dashboard model. Grouped out of App.Model so the
/// canvas state and its pure helpers live together, away from core worktree/repo concerns
/// (mirrors how CreateModal/ConfirmModal nest their sub-component state into Model).
type CanvasState =
    { CanvasPaneOpen: bool
      CanvasPosition: CanvasPosition
      CanvasSize: CanvasSize
      // An explicit pane target used by card-level SystemView actions such as Diff. None keeps the
      // normal behavior where the pane follows FocusedElement; selecting a card clears the override.
      TargetWorktree: string option
      ActiveCanvasDoc: Map<string, string>
      VisitedCanvasDocs: Map<string, string list>
      // Content hash currently loaded by each mounted AgentDoc iframe. Comparing this with the
      // refreshed CanvasDoc hash determines whether a morph is needed, including while the pane is
      // collapsed. Fresh mounts seed the current hash; successful morphs advance it.
      MountedAgentDocHashes: Map<string * string, string>
      LastViewedHashes: Map<string, Map<string, string>>
      PreviousCanvasHashes: Map<string, Map<string, string>>
      CanvasEvents: Map<string, CanvasEvent list>
      CanvasSendState: CanvasSendState
      // Latest doc-scoped banner error, stamped with the doc it is attributed to. Two producers: a
      // doc-side JS error from a focused AgentDoc's iframe (its filename carried in the postMessage and
      // validated against the focused worktree's docs), and a malformed/unroutable doc message (no
      // usable string `action`) attributed to the active visible doc (DocJsError). The banner is shown
      // only while that same doc is focused (CanvasPane gates on it), so navigating to another doc/card
      // auto-hides a stale error — doc-scoped without a clear in every focus reducer. SelectCanvasDoc
      // additionally clears it so a tab switch (and switch back) never re-shows it. Distinct from
      // CanvasSendState.Failed, which models pane→session message-delivery failures.
      DocError: DocJsError option
      // Dismissible notice for a completed rich-link share. Path-copy success uses PathCopyState;
      // failures reuse CanvasSendState.Failed.
      ClipboardNotice: string option
      PathCopyState: CanvasPathCopyState
      // Scoped identity and phase of the single in-flight share. The phase keeps the pane locked
      // through clipboard settlement, while the identity lets stale async results be ignored.
      ShareState: CanvasShareState
      BridgeLiveness: Map<string, BridgeLiveness> }

/// Initial canvas state: pane closed on the right, all maps empty, send state idle.
/// First-load values from the server (pane open/position) are applied in DataLoaded.
let empty : CanvasState =
    { CanvasPaneOpen = false
      CanvasPosition = CanvasPosition.Right
      CanvasSize = CanvasSize.Ratio1To1
      TargetWorktree = None
      ActiveCanvasDoc = Map.empty
      VisitedCanvasDocs = Map.empty
      MountedAgentDocHashes = Map.empty
      LastViewedHashes = Map.empty
      PreviousCanvasHashes = Map.empty
      CanvasEvents = Map.empty
      CanvasSendState = CanvasSendState.Idle
      DocError = None
      ClipboardNotice = None
      PathCopyState = CanvasPathCopyState.Idle 0
      ShareState = CanvasShareState.Idle
      BridgeLiveness = Map.empty }

[<Literal>]
let WorktreeDiffFilename = "diff.html"

let isWorktreeDiffFilename (filename: string) =
    filename.Equals(WorktreeDiffFilename, System.StringComparison.OrdinalIgnoreCase)

let preferredAutomaticDoc (worktree: WorktreeStatus) =
    worktree.CanvasDocs
    |> List.tryFind (fun doc -> not (isWorktreeDiffFilename doc.Filename))
    |> Option.orElseWith (fun () -> worktree.CanvasDocs |> List.tryHead)

let [<Literal>] private MaxLiveIframes = 3

/// Move filename to front of visited list (LRU order, most recent first), capped at MaxLiveIframes.
let touchVisitedDoc (scopedKey: string) (filename: string) (visited: Map<string, string list>) =
    let current = visited |> Map.tryFind scopedKey |> Option.defaultValue []
    let updated = filename :: (current |> List.filter (fun f -> f <> filename))
    let capped = if updated.Length > MaxLiveIframes then updated |> List.take MaxLiveIframes else updated
    visited |> Map.add scopedKey capped

/// Look up a canvas doc's kind by scoped key + filename. Used to gate session-document
/// machinery (morph signaling, idle auto-display focus-steal) to AgentDoc only: a SystemView
/// (e.g. the beads dashboard) drives its own refresh and must neither be morphed (a morph stomps
/// the live, JS-rendered dashboard back to the empty template shell) nor steal focus on change.
let canvasDocKind (repos: RepoModel list) (scopedKey: string) (filename: string) : CanvasDocKind option =
    findWorktreeByScopedKey repos scopedKey
    |> Option.bind (fun wt -> wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = filename))
    |> Option.map _.Kind

let hasSystemView (filename: string) (worktree: WorktreeStatus) =
    worktree.CanvasDocs
    |> List.exists (fun doc -> doc.Filename = filename && doc.Kind = SystemView)

let isKnownSystemView (repos: RepoModel list) (scopedKey: string) (filename: string) =
    findWorktreeByScopedKey repos scopedKey
    |> Option.exists (hasSystemView filename)

/// The worktree currently driving the canvas pane. Explicit SystemView actions can temporarily
/// target a worktree without changing card focus; otherwise the pane follows the focused card.
let activeCanvasWorktree (focused: FocusTarget option) (targetWorktree: string option) =
    targetWorktree
    |> Option.orElseWith (fun () ->
        match focused with
        | Some (Card scopedKey) -> Some scopedKey
        | _ -> None)

/// The (scopedKey, filename) of the doc currently shown for the active canvas worktree: its
/// ActiveCanvasDoc selection if it still names a real doc, else the preferred automatic doc.
/// The worktree diff is explicit-only when another doc exists, but remains the fallback when it is
/// the worktree's sole canvas document.
/// Pure over the slices it reads rather than the whole Model.
let activeVisibleDoc (repos: RepoModel list) (focused: FocusTarget option) (targetWorktree: string option) (activeCanvasDoc: Map<string, string>) : (string * string) option =
    activeCanvasWorktree focused targetWorktree
    |> Option.bind (fun scopedKey ->
        findWorktreeByScopedKey repos scopedKey
        |> Option.bind (fun wt ->
            let doc =
                match activeCanvasDoc |> Map.tryFind scopedKey with
                | Some name ->
                    match wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = name) with
                    | Some selected -> Some selected
                    | None when targetWorktree.IsSome -> None
                    | None -> preferredAutomaticDoc wt
                | None -> preferredAutomaticDoc wt
            doc |> Option.map (fun d -> scopedKey, d.Filename)))

let agentDocHash (repos: RepoModel list) (scopedKey: string) (filename: string) =
    findWorktreeByScopedKey repos scopedKey
    |> Option.bind (fun wt ->
        wt.CanvasDocs
        |> List.tryFind (fun doc -> doc.Filename = filename && doc.Kind = AgentDoc))
    |> Option.map _.ContentHash

/// Current on-disk hashes for the AgentDoc iframes the pane actually renders. The pane keeps this
/// subtree mounted while collapsed, but renders no iframes on overview/no-focus and only one
/// worktree's visited LRU plus its active doc.
let renderedAgentDocHashes
    (repos: RepoModel list)
    (focused: FocusTarget option)
    (targetWorktree: string option)
    (activeCanvasDoc: Map<string, string>)
    (visitedCanvasDocs: Map<string, string list>) =
    activeVisibleDoc repos focused targetWorktree activeCanvasDoc
    |> Option.bind (fun (scopedKey, activeFilename) ->
        findWorktreeByScopedKey repos scopedKey
        |> Option.map (fun wt ->
            let renderedFilenames =
                visitedCanvasDocs
                |> Map.tryFind scopedKey
                |> Option.defaultValue []
                |> Set.ofList
                |> Set.add activeFilename
            wt.CanvasDocs
            |> List.choose (fun doc ->
                if doc.Kind = AgentDoc && Set.contains doc.Filename renderedFilenames then
                    Some ((scopedKey, doc.Filename), doc.ContentHash)
                else
                    None)
            |> Map.ofList))
    |> Option.defaultValue Map.empty

/// Preserve loaded hashes only for iframes rendered both before and after a transition. Newly
/// rendered docs are fresh mounts and therefore start synchronized to their current on-disk hash.
let reconcileMountedAgentDocHashes
    (previousRendered: Map<string * string, string>)
    (currentRendered: Map<string * string, string>)
    (mounted: Map<string * string, string>) =
    currentRendered
    |> Map.map (fun key currentHash ->
        if Map.containsKey key previousRendered then
            mounted |> Map.tryFind key |> Option.defaultValue currentHash
        else
            currentHash)

let isMounted (mounted: Map<string * string, string>) scopedKey filename =
    Map.containsKey (scopedKey, filename) mounted

let needsMorph
    (rendered: Map<string * string, string>)
    (mounted: Map<string * string, string>)
    key =
    match Map.tryFind key rendered, Map.tryFind key mounted with
    | Some currentHash, Some loadedHash -> currentHash <> loadedHash
    | _ -> false
