module CanvasUpdate

// Canvas update-arm bodies and the shared canvas helpers, extracted from `App.fs`.
// Each function here is the body of a canvas `update` arm; `App.fs` delegates to it
// as a one-line arm. This is body extraction only — `update` remains a single
// function in `App.fs` (no sub-`Msg`/`Cmd.map` split). Compiled after `AppTypes.fs`
// (which holds `Model`/`Msg` + shared plumbing) and before `App.fs`, so the canvas
// logic lifts out without a cyclic reference. See docs/spec/canvas-pane.md.

open Shared
open Navigation
open CanvasTypes
open Elmish
open Browser
open AppTypes

let activeVisibleDoc (model: Model) : (string * string) option =
    CanvasState.activeVisibleDoc model.Repos model.FocusedElement model.Canvas.ActiveCanvasDoc

/// True when `filename` names a real CanvasDoc of the worktree `scopedKey`. Gates in-doc link
/// navigation (NavigateCanvasDoc), whose filename arrives via an untrusted in-iframe postMessage:
/// only a filename that matches a known doc may be committed to ActiveCanvasDoc, otherwise
/// activeVisibleDoc would silently fall back to the first doc (wrong tab) — e.g. a filename still
/// carrying a ?query/#hash suffix that no bare CanvasDoc.Filename can match.
let isKnownCanvasDoc (model: Model) (scopedKey: string) (filename: string) : bool =
    findWorktree scopedKey model
    |> Option.map (fun wt -> wt.CanvasDocs |> List.exists (fun d -> d.Filename = filename))
    |> Option.defaultValue false

let markVisibleDocCmd (model: Model) : Cmd<Msg> =
    CanvasState.markVisibleDocCmd MarkDocViewed model.Repos model.FocusedElement model.Canvas.ActiveCanvasDoc

let launchCanvasSession (scopedKey: string) (model: Model) =
    match findWorktree scopedKey model with
    | Some wt ->
        let wtPath = WorktreePath.value wt.Path
        let prompt =
            activeVisibleDoc model
            |> Option.map (fun (_, filename) -> CanvasPrompt.continueWorking wtPath filename)
            |> Option.defaultValue ""
        let action = CanvasSession prompt
        model, Cmd.OfAsync.perform worktreeApi.Value.launchAction { Path = wt.Path; Action = action } LaunchActionResult
    | None ->
        model, Cmd.none

let toggleCanvasPane (model: Model) =
    let newState = not model.Canvas.CanvasPaneOpen
    { model with Canvas = { model.Canvas with CanvasPaneOpen = newState } },
    Cmd.batch [
        Cmd.OfAsync.attempt worktreeApi.Value.saveCanvasPaneOpen newState (fun _ -> NoOp)
        if newState then markVisibleDocCmd model else Cmd.none
    ]

let setCanvasPosition (position: CanvasPosition) (model: Model) =
    { model with Canvas = { model.Canvas with CanvasPosition = position } },
    Cmd.OfAsync.attempt worktreeApi.Value.saveCanvasPosition position (fun _ -> NoOp)

let setCanvasSize (size: CanvasSize) (model: Model) =
    { model with Canvas = { model.Canvas with CanvasSize = size } },
    Cmd.OfAsync.attempt worktreeApi.Value.saveCanvasSize size (fun _ -> NoOp)

let selectCanvasDoc (scopedKey: string) (filename: string) (model: Model) =
    let wasAlreadyVisited =
        model.Canvas.VisitedCanvasDocs
        |> Map.tryFind scopedKey
        |> Option.defaultValue []
        |> List.contains filename
    { model with
        Canvas =
            { model.Canvas with
                // Doc-scoped error: a tab switch must never carry a stale error from the doc we're
                // leaving into the one we're showing, so clear it here (it reappears only if the new
                // doc throws again).
                DocError = None
                ActiveCanvasDoc = model.Canvas.ActiveCanvasDoc |> Map.add scopedKey filename
                VisitedCanvasDocs = CanvasState.touchVisitedDoc scopedKey filename model.Canvas.VisitedCanvasDocs } },
    Cmd.batch [
        Cmd.ofMsg (MarkDocViewed (scopedKey, filename))
        // When switching to a previously hidden iframe, morph it in case content changed while
        // hidden — but only for AgentDocs. A SystemView (beads dashboard) self-refreshes and is
        // served without a morph controller, so a morph signal is meaningless for it.
        if wasAlreadyVisited && CanvasState.canvasDocKind model.Repos scopedKey filename = Some AgentDoc then Cmd.ofMsg MorphActiveDoc
    ]

let openCanvasDoc (scopedKey: string) (filename: string) (model: Model) =
    let openPane = not model.Canvas.CanvasPaneOpen
    let repos, expanded = expandRepoOwning scopedKey model.Repos
    { model with
        Repos = repos
        FocusedElement = Some (Card scopedKey)
        Canvas =
            { model.Canvas with
                CanvasPaneOpen = true
                ActiveCanvasDoc = model.Canvas.ActiveCanvasDoc |> Map.add scopedKey filename
                VisitedCanvasDocs = CanvasState.touchVisitedDoc scopedKey filename model.Canvas.VisitedCanvasDocs } },
    Cmd.batch [
        if openPane then Cmd.OfAsync.attempt worktreeApi.Value.saveCanvasPaneOpen true (fun _ -> NoOp)
        if expanded then saveCollapsedReposCmd repos
        Cmd.ofMsg (MarkDocViewed (scopedKey, filename))
    ]

let archiveCanvasDoc (scopedKey: string) (filename: string) (model: Model) =
    match findWorktree scopedKey model with
    | Some wt ->
        let request: ArchiveCanvasDocRequest = { WorktreePath = wt.Path; Filename = filename }
        model, Cmd.OfAsync.either worktreeApi.Value.archiveCanvasDoc request (fun r -> ArchiveCanvasDocResult (scopedKey, filename, r)) (_.Message >> Error >> fun r -> ArchiveCanvasDocResult (scopedKey, filename, r))
    | None -> model, Cmd.none

let archiveCanvasDocResult (scopedKey: string) (filename: string) (result: Result<unit, string>) (model: Model) =
    match result with
    | Ok _ ->
        let repos =
            model.Repos
            |> List.map (fun r ->
                { r with
                    Worktrees =
                        r.Worktrees
                        |> List.map (fun wt ->
                            let key = WorktreePath.value wt.Path
                            if key = scopedKey
                            then { wt with CanvasDocs = wt.CanvasDocs |> List.filter (fun d -> d.Filename <> filename) }
                            else wt) })
        let remainingDocs =
            repos
            |> List.tryPick (fun r ->
                r.Worktrees
                |> List.tryPick (fun wt ->
                    if WorktreePath.value wt.Path = scopedKey && not (List.isEmpty wt.CanvasDocs)
                    then Some wt.CanvasDocs
                    else None))
        let activeDoc =
            match remainingDocs with
            | Some (first :: _) -> model.Canvas.ActiveCanvasDoc |> Map.add scopedKey first.Filename
            | _ -> model.Canvas.ActiveCanvasDoc |> Map.remove scopedKey
        let visitedDocs =
            let current = model.Canvas.VisitedCanvasDocs |> Map.tryFind scopedKey |> Option.defaultValue []
            let filtered = current |> List.filter (fun f -> f <> filename)
            match remainingDocs with
            | Some (first :: _) -> CanvasState.touchVisitedDoc scopedKey first.Filename (model.Canvas.VisitedCanvasDocs |> Map.add scopedKey filtered)
            | _ ->
                if List.isEmpty filtered then model.Canvas.VisitedCanvasDocs |> Map.remove scopedKey
                else model.Canvas.VisitedCanvasDocs |> Map.add scopedKey filtered
        { model with Repos = repos; Canvas = { model.Canvas with ActiveCanvasDoc = activeDoc; VisitedCanvasDocs = visitedDocs } }, Cmd.none
    | Error msg ->
        Fable.Core.JS.console.error ("Archive canvas doc error:", msg)
        model, Cmd.none

let navigateCanvasDoc (filename: string) (model: Model) =
    match model.FocusedElement with
    | Some (Card scopedKey) ->
        // Defense-in-depth: filename arrives via an in-iframe postMessage (untrusted, '*' origin).
        // Only switch tabs when it names a real CanvasDoc of the focused worktree — committing an
        // unknown filename (e.g. one still carrying a ?query/#hash) to ActiveCanvasDoc would
        // silently fall back to the first doc (see activeVisibleDoc), landing on the wrong tab.
        if isKnownCanvasDoc model scopedKey filename then
            model, Cmd.ofMsg (SelectCanvasDoc (scopedKey, filename))
        else
            Fable.Core.JS.console.warn ($"[canvas] navigate-canvas-doc DROPPED: unknown doc '{filename}'")
            model, Cmd.none
    | _ ->
        Fable.Core.JS.console.warn "[canvas] navigate-canvas-doc DROPPED: no focused card"
        model, Cmd.none

let canvasMessageReceived (payload: string) (model: Model) =
    let visibleDoc = activeVisibleDoc model
    let worktree = visibleDoc |> Option.bind (fun (sk, _) -> findWorktree sk model)
    match visibleDoc, worktree with
    | Some (scopedKey, filename), Some wt ->
        Fable.Core.JS.console.log ($"[canvas] Forwarding message to {WorktreePath.value wt.Path} doc={filename} (payload length={payload.Length})")
        model, Cmd.OfAsync.either worktreeApi.Value.sendCanvasMessage { WorktreePath = wt.Path; Filename = filename; Payload = payload } (fun r -> CanvasSendResult(r, scopedKey)) (fun e -> CanvasSendResult(CanvasMessageResult.Error e.Message, scopedKey))
    | Some (scopedKey, _), None ->
        Fable.Core.JS.console.warn ($"[canvas] Message DROPPED: focused card '{scopedKey}' has no matching worktree")
        model, Cmd.none
    | None, _ ->
        Fable.Core.JS.console.warn "[canvas] Message DROPPED: no active visible doc"
        model, Cmd.none

let canvasSendResult (result: CanvasMessageResult) (scopedKey: string) (model: Model) =
    match result with
    | CanvasMessageResult.Error msg ->
        Fable.Core.JS.console.error ("Canvas message error:", msg)
        { model with Canvas = { model.Canvas with CanvasSendState = CanvasSendState.Failed msg } }, Cmd.none
    | CanvasMessageResult.Ok ->
        { model with Canvas = { model.Canvas with CanvasSendState = CanvasSendState.Idle } }, Cmd.none
    | CanvasMessageResult.Queued ->
        Fable.Core.JS.console.log "[canvas] Message queued — waiting for session"
        { model with Canvas = { model.Canvas with CanvasSendState = CanvasSendState.Waiting scopedKey } }, Cmd.none

let dismissCanvasMessageError (model: Model) =
    { model with Canvas = { model.Canvas with CanvasSendState = CanvasSendState.Idle } }, Cmd.none

/// Record a doc-side JS error (window.onerror / unhandledrejection) forwarded from an AgentDoc
/// iframe. `scopedKey` and `filename` are the EMITTING worktree + doc, carried in the postMessage
/// `wt`/`doc` fields and threaded through the listener, so the error is stamped with the doc that
/// actually threw — independent of the active tab. This matters because visited docs stay mounted as
/// hidden iframes and keep running JS, so an async error from a hidden doc (even in a non-focused
/// worktree) must not be attributed to the focused tab (focused-review A-02, C-06). The emitter is
/// validated against that worktree's docs (isKnownCanvasDoc) before being stored, so a stale/forged
/// identity — e.g. from an archived doc — can never raise a banner. The stamp drives doc-scoped
/// display: the banner shows only while that doc stays focused; navigating to another doc/card hides
/// it (the view gates on the stamp). Kept separate from CanvasSendState so the doc-error and
/// message-delivery banners never overwrite each other; the newest error wins. If the emitter is not
/// a known doc of a known worktree, the error is dropped. (Arrival is already logged in
/// CanvasPane.messageListener.)
let canvasDocError (scopedKey: string) (filename: string) (message: string) (model: Model) =
    if isKnownCanvasDoc model scopedKey filename then
        { model with Canvas = { model.Canvas with DocError = Some { ScopedKey = scopedKey; Filename = filename; Message = message } } }, Cmd.none
    else
        model, Cmd.none

/// A canvas doc posted a message with no top-level string `action`, so CanvasPane could not route it
/// and surfaced it here instead of dropping it silently (the focused-review regression). Unlike
/// canvasDocError, a malformed message carries no self-identifying wt/doc fields, so it is attributed
/// to the active *visible* doc — which is, by definition, a known doc of a known worktree, satisfying
/// the same banner invariants canvasDocError relies on. With no active visible doc there is nothing to
/// attribute it to, so it is dropped. (Arrival + keys are already logged in CanvasPane.messageListener.)
let canvasMalformedDocMessage (keys: string) (model: Model) =
    match activeVisibleDoc model with
    | Some (scopedKey, filename) ->
        let message =
            "This canvas doc sent a message with no \"action\" field, so Treemon ignored it. "
            + "The doc may be out of date — try regenerating it."
        { model with Canvas = { model.Canvas with DocError = Some { ScopedKey = scopedKey; Filename = filename; Message = message } } }, Cmd.none
    | None -> model, Cmd.none

let dismissCanvasDocError (model: Model) =
    { model with Canvas = { model.Canvas with DocError = None } }, Cmd.none

let morphActiveDoc (model: Model) =
    model,
    Cmd.ofEffect (fun _ ->
        Dom.document.querySelector ".canvas-iframe-active"
        |> Option.ofObj
        |> Option.iter (fun iframe ->
            Fable.Core.JsInterop.emitJsExpr (iframe, CanvasPane.CanvasOrigin) "$0.contentWindow.postMessage({action:'content-updated'},$1)"))

let morphComplete (model: Model) =
    model, markVisibleDocCmd model

let messageListener (dispatch: Dispatch<Msg>) =
    CanvasPane.messageListener
        { Dispatch = CanvasMessageReceived >> dispatch
          SelectDoc = NavigateCanvasDoc >> dispatch
          OnMorphComplete = fun () -> dispatch MorphComplete
          OnDocError = fun scopedKey filename message -> dispatch (CanvasDocError (scopedKey, filename, message))
          OnMalformedMessage = fun keys -> dispatch (CanvasMalformedDocMessage keys) }
