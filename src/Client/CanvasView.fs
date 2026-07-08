module CanvasView

// The canvas-pane wiring, extracted from `App.fs`'s `view`. Builds the `CanvasPaneCallbacks`
// from `dispatch`, resolves the focused worktree's active doc plus its unviewed/visited slices
// from the model, and calls `CanvasPane.view` to render the pane element. This is the view-layer
// companion to `CanvasUpdate.fs` (whose `activeVisibleDoc` it reuses): pure render wiring, no
// `update` logic. Compiled after `CanvasUpdate.fs` and before `App.fs`, whose `view` now calls
// `CanvasView.view model dispatch` in place of the inlined block.

open Shared
open Navigation
open Elmish
open CanvasAwareness
open AppTypes

/// Resolve the focused worktree and its active CanvasDoc (the doc the pane renders). Returns
/// `None` when there is no focused card, no active doc, or the active filename no longer names a
/// real doc of the worktree.
let focusedWorktreeCanvasDoc (model: Model) =
    CanvasUpdate.activeVisibleDoc model
    |> Option.bind (fun (scopedKey, filename) ->
        findWorktree scopedKey model
        |> Option.bind (fun wt ->
            wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = filename)
            |> Option.map (fun d -> wt, d)))

/// Build the canvas-pane element: derive the focused worktree's doc / unviewed / visited slices
/// from the model, assemble the `CanvasPaneCallbacks` from `dispatch`, and hand them to
/// `CanvasPane.view`. `App.fs`'s `view` calls this for its `canvasEl`.
let view (model: Model) (dispatch: Dispatch<Msg>) =
    let selectCanvasDoc filename =
        match model.FocusedElement with
        | Some (Card scopedKey) -> dispatch (SelectCanvasDoc (scopedKey, filename))
        | _ -> ()

    let onOverviewClick scopedKey =
        dispatch (FocusOverviewCard scopedKey)

    let onOverviewDocClick scopedKey filename =
        dispatch (OpenCanvasDoc (scopedKey, filename))

    let archiveCanvasDoc filename =
        match model.FocusedElement with
        | Some (Card scopedKey) -> dispatch (ArchiveCanvasDoc (scopedKey, filename))
        | _ -> ()

    let shareCanvasDoc filename =
        match model.FocusedElement with
        | Some (Card scopedKey) -> dispatch (ShareCanvasDoc (scopedKey, filename))
        | _ -> ()

    let launchCanvasSession () =
        match model.FocusedElement with
        | Some (Card scopedKey) -> dispatch (LaunchCanvasSession scopedKey)
        | _ -> ()

    let focusedUnviewedFilenames =
        match model.FocusedElement with
        | Some (Card scopedKey) ->
            unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes
            |> Map.tryFind scopedKey
            |> Option.defaultValue []
            |> Set.ofList
        | _ -> Set.empty

    let focusedVisitedDocs =
        match model.FocusedElement with
        | Some (Card scopedKey) ->
            model.Canvas.VisitedCanvasDocs |> Map.tryFind scopedKey |> Option.defaultValue []
        | _ -> []

    let canvasCallbacks: CanvasPane.CanvasPaneCallbacks =
        { SetPosition = SetCanvasPosition >> dispatch
          SetSize = SetCanvasSize >> dispatch
          SelectDoc = selectCanvasDoc
          OnOverviewClick = onOverviewClick
          OnOverviewDocClick = onOverviewDocClick
          ArchiveDoc = archiveCanvasDoc
          ShareDoc = shareCanvasDoc
          DismissError = (fun () -> dispatch DismissCanvasMessageError)
          DismissDocError = (fun () -> dispatch DismissCanvasDocError)
          DismissShareNotice = (fun () -> dispatch DismissShareNotice)
          LaunchSession = launchCanvasSession }

    let canvasState: CanvasPane.CanvasPaneState =
        { IsOpen = model.Canvas.CanvasPaneOpen
          Position = model.Canvas.CanvasPosition
          Size = model.Canvas.CanvasSize
          SendState = model.Canvas.CanvasSendState
          DocError = model.Canvas.DocError
          ShareNotice = model.Canvas.ShareNotice
          BridgeLiveness = model.Canvas.BridgeLiveness }

    CanvasPane.view canvasState (focusedWorktreeCanvasDoc model) model.Repos focusedUnviewedFilenames focusedVisitedDocs canvasCallbacks
