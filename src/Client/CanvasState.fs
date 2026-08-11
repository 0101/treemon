module CanvasState

open Shared
open Navigation
open CanvasTypes
open CanvasAwareness
open Elmish

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
      // Mounted-but-hidden AgentDocs whose on-disk content changed while they were hidden, keyed by
      // (scopedKey, filename). A hidden iframe is not morphed on change (only the active visible doc
      // is — see App.fs DataLoaded), so it falls out of sync silently. This records the docs that
      // need a catch-up morph on their next reveal. `selectCanvasDoc` morphs a doc on switch-back
      // ONLY when it is in this set, then clears it — so an ordinary tab switch (nothing changed on
      // disk) morphs nothing and the mounted iframe's live form input survives.
      StaleHiddenDocs: Set<string * string>
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
      // Transient success banner shown after a canvas doc is shared and its rich link copied to the
      // clipboard (the message text, e.g. "Shared — link copied", or None when nothing to show).
      // A share *failure* reuses the CanvasSendState.Failed error banner per spec, so only the
      // success path needs its own state; kept distinct from CanvasSendState (message delivery) and
      // DocError (doc JS errors) so the banners never overwrite each other.
      ShareNotice: string option
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
      StaleHiddenDocs = Set.empty
      LastViewedHashes = Map.empty
      PreviousCanvasHashes = Map.empty
      CanvasEvents = Map.empty
      CanvasSendState = CanvasSendState.Idle
      DocError = None
      ShareNotice = None
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

/// True when (scopedKey, filename) currently has a mounted iframe — i.e. it is in the visited
/// (LRU-capped) set for its scoped key. `StaleHiddenDocs` marks are only meaningful for such docs.
let private isMounted (visited: Map<string, string list>) (scopedKey: string) (filename: string) =
    visited |> Map.tryFind scopedKey |> Option.defaultValue [] |> List.contains filename

/// Record AgentDocs that changed on disk while mounted-but-hidden, so their next reveal gets a
/// catch-up morph. Only a doc that actually has a mounted, hidden iframe earns a mark: it must be
/// in `visited` (so an unmounted, never-opened or LRU-evicted doc is excluded) and must not be the
/// active visible doc (which is morphed in place, never via StaleHiddenDocs). Marking anything else
/// would leave a mark with no live iframe behind it, which later drives a spurious switch-back morph.
let markStale
    (changed: (string * string) list)
    (activeVisible: (string * string) option)
    (visited: Map<string, string list>)
    (stale: Set<string * string>) : Set<string * string> =
    changed
    |> List.filter (fun (scopedKey, filename) ->
        Some (scopedKey, filename) <> activeVisible && isMounted visited scopedKey filename)
    |> List.fold (fun acc d -> Set.add d acc) stale

/// Drop stale marks for docs that no longer have a mounted iframe (evicted past the LRU cap,
/// archived, or gone from the repo). Once an iframe unmounts, a later fresh mount already loads
/// current disk content, so the mark is obsolete; keeping it would morph the fresh mount needlessly.
/// This is the single garbage-collector for the `StaleHiddenDocs` invariant "a mark implies a
/// mounted hidden iframe".
let pruneStaleToMounted (visited: Map<string, string list>) (stale: Set<string * string>) : Set<string * string> =
    stale |> Set.filter (fun (scopedKey, filename) -> isMounted visited scopedKey filename)

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

/// Command to mark the currently visible doc as viewed. `markViewed` builds the host app's
/// message from (scopedKey, filename), keeping this module free of any concrete Msg type.
let markVisibleDocCmd (markViewed: string * string -> 'msg) (repos: RepoModel list) (focused: FocusTarget option) (targetWorktree: string option) (activeCanvasDoc: Map<string, string>) : Cmd<'msg> =
    activeVisibleDoc repos focused targetWorktree activeCanvasDoc
    |> Option.map (fun (sk, fn) -> Cmd.ofMsg (markViewed (sk, fn)))
    |> Option.defaultValue Cmd.none
