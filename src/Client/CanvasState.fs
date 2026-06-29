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
      ActiveCanvasDoc: Map<string, string>
      VisitedCanvasDocs: Map<string, string list>
      LastViewedHashes: Map<string, Map<string, string>>
      PreviousCanvasHashes: Map<string, Map<string, string>>
      CanvasEvents: Map<string, CanvasEvent list>
      CanvasSendState: CanvasSendState
      // Latest doc-side JS error from a focused AgentDoc's iframe, stamped with the doc that EMITTED
      // it — its filename is carried in the postMessage and validated against the focused worktree's
      // docs (DocJsError). The banner is shown only while that same doc is focused (CanvasPane gates
      // on it), so navigating to another doc/card auto-hides a stale error — doc-scoped without a
      // clear in every focus reducer. SelectCanvasDoc additionally clears it so a tab switch (and
      // switch back) never re-shows it. Distinct from CanvasSendState.Failed, which models
      // pane→session message-delivery failures.
      DocError: DocJsError option
      BridgeLiveness: Map<string, BridgeLiveness> }

/// Initial canvas state: pane closed on the right, all maps empty, send state idle.
/// First-load values from the server (pane open/position) are applied in DataLoaded.
let empty : CanvasState =
    { CanvasPaneOpen = false
      CanvasPosition = CanvasPosition.Right
      CanvasSize = CanvasSize.Ratio1To1
      ActiveCanvasDoc = Map.empty
      VisitedCanvasDocs = Map.empty
      LastViewedHashes = Map.empty
      PreviousCanvasHashes = Map.empty
      CanvasEvents = Map.empty
      CanvasSendState = CanvasSendState.Idle
      DocError = None
      BridgeLiveness = Map.empty }

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
    repos
    |> List.tryPick (fun r -> r.Worktrees |> List.tryFind (fun wt -> WorktreePath.value wt.Path = scopedKey))
    |> Option.bind (fun wt -> wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = filename))
    |> Option.map _.Kind

/// The (scopedKey, filename) of the doc currently shown for the focused card: the card's
/// ActiveCanvasDoc selection if it still names a real doc, else the worktree's first doc.
/// Pure over the slices it reads (repos, focused element, active-doc map) rather than the whole Model.
let activeVisibleDoc (repos: RepoModel list) (focused: FocusTarget option) (activeCanvasDoc: Map<string, string>) : (string * string) option =
    match focused with
    | Some (Card scopedKey) ->
        repos
        |> List.tryPick (fun r -> r.Worktrees |> List.tryFind (fun wt -> WorktreePath.value wt.Path = scopedKey))
        |> Option.bind (fun wt ->
            let doc =
                activeCanvasDoc
                |> Map.tryFind scopedKey
                |> Option.bind (fun name -> wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = name))
                |> Option.orElseWith (fun () -> wt.CanvasDocs |> List.tryHead)
            doc |> Option.map (fun d -> scopedKey, d.Filename))
    | _ -> None

/// Command to mark the currently visible doc as viewed. `markViewed` builds the host app's
/// message from (scopedKey, filename), keeping this module free of any concrete Msg type.
let markVisibleDocCmd (markViewed: string * string -> 'msg) (repos: RepoModel list) (focused: FocusTarget option) (activeCanvasDoc: Map<string, string>) : Cmd<'msg> =
    activeVisibleDoc repos focused activeCanvasDoc
    |> Option.map (fun (sk, fn) -> Cmd.ofMsg (markViewed (sk, fn)))
    |> Option.defaultValue Cmd.none
