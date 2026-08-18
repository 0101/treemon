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
    CanvasState.activeVisibleDoc model.Repos model.FocusedElement model.Canvas.TargetWorktree model.Canvas.ActiveCanvasDoc

/// True when `filename` names a real CanvasDoc of the worktree `scopedKey`. Gates in-doc link
/// navigation (NavigateCanvasDoc), whose filename arrives via an untrusted in-iframe postMessage:
/// only a filename that matches a known doc may be committed to ActiveCanvasDoc, otherwise
/// activeVisibleDoc would silently fall back to the first doc (wrong tab) — e.g. a filename still
/// carrying a ?query/#hash suffix that no bare CanvasDoc.Filename can match.
let isKnownCanvasDoc (model: Model) (scopedKey: string) (filename: string) : bool =
    findWorktree scopedKey model
    |> Option.map (fun wt -> wt.CanvasDocs |> List.exists (fun d -> d.Filename = filename))
    |> Option.defaultValue false

let private renderedAgentDocHashes (model: Model) =
    CanvasState.renderedAgentDocHashes
        model.Repos
        model.FocusedElement
        model.Canvas.TargetWorktree
        model.Canvas.ActiveCanvasDoc
        model.Canvas.VisitedCanvasDocs

let reconcileMountedDocs (previous: Model) (current: Model) =
    let mounted =
        CanvasState.reconcileMountedAgentDocHashes
            (renderedAgentDocHashes previous)
            (renderedAgentDocHashes current)
            previous.Canvas.MountedAgentDocHashes
    { current with Canvas.MountedAgentDocHashes = mounted }

let visibleDocSyncMsg (model: Model) =
    activeVisibleDoc model
    |> Option.map (fun (scopedKey, filename) ->
        let key = scopedKey, filename
        let rendered = renderedAgentDocHashes model
        if CanvasState.needsMorph rendered model.Canvas.MountedAgentDocHashes key then
            MorphActiveDoc
                { ScopedKey = scopedKey
                  Filename = filename
                  ContentHash = rendered[key] }
        else
            MarkDocViewed key)

let syncVisibleDocCmd model =
    visibleDocSyncMsg model |> Option.map Cmd.ofMsg |> Option.defaultValue Cmd.none

type private RevealMode =
    | Visible
    | Hidden

let private revealCanvasDoc
    mode
    (scopedKey: string)
    (filename: string)
    (previous: Model)
    (current: Model) =
    let visitedWithPrevious =
        match activeVisibleDoc previous with
        | Some (previousScopedKey, previousFilename)
            when previousScopedKey = scopedKey
                 && isKnownCanvasDoc current previousScopedKey previousFilename ->
            CanvasState.touchVisitedDoc
                scopedKey
                previousFilename
                current.Canvas.VisitedCanvasDocs
        | _ -> current.Canvas.VisitedCanvasDocs
    let selected =
        { current with
            Canvas.DocError = None
            Canvas.ActiveCanvasDoc = current.Canvas.ActiveCanvasDoc |> Map.add scopedKey filename
            Canvas.VisitedCanvasDocs =
                CanvasState.touchVisitedDoc scopedKey filename visitedWithPrevious }
        |> reconcileMountedDocs previous
    selected,
    match mode with
    | Visible -> syncVisibleDocCmd selected
    | Hidden -> Cmd.none

let launchCanvasSession (scopedKey: string) (model: Model) =
    match findWorktree scopedKey model with
    | Some wt ->
        let wtPath = WorktreePath.value wt.Path
        let prompt =
            activeVisibleDoc model
            |> Option.map (fun (_, filename) -> CanvasSessionPrompt.forAgentDoc wtPath filename)
            |> Option.defaultValue ""
        let action = CanvasSession prompt
        model, Cmd.OfAsync.perform worktreeApi.Value.launchAction { Path = wt.Path; Action = action } LaunchActionResult
    | None ->
        model, Cmd.none

let toggleCanvasPane (model: Model) =
    let newState = not model.Canvas.CanvasPaneOpen
    let updated =
        { model with Canvas.CanvasPaneOpen = newState }
        |> reconcileMountedDocs model
    updated,
    Cmd.batch [
        Cmd.OfAsync.attempt worktreeApi.Value.saveCanvasPaneOpen newState (fun _ -> NoOp)
        if newState then syncVisibleDocCmd updated else Cmd.none
    ]

let setWorkspaceWidth (width: WorkspaceWidth) (model: Model) =
    { model with Canvas.WorkspaceWidth = width },
    Cmd.OfAsync.attempt worktreeApi.Value.saveWorkspaceWidth width (fun _ -> NoOp)

let selectCanvasDoc (scopedKey: string) (filename: string) (model: Model) =
    let targeted =
        { model with
            Canvas.TargetWorktree =
                if model.Canvas.TargetWorktree.IsSome then Some scopedKey
                else None }
    revealCanvasDoc
        (if model.Canvas.CanvasPaneOpen then Visible else Hidden)
        scopedKey
        filename
        model
        targeted

/// The single chokepoint for setting `FocusedElement`. While the terminal pane is visible, every
/// card selection also projects to that worktree's existing terminal tab (or clears the active tab
/// for the pane's explicit start state); a hidden pane keeps its selection unchanged. When
/// `retarget` is set and focus selects a worktree card, that card's active doc is retargeted to its most recently
/// published *unviewed* AgentDoc (the "select the worktree shows THAT doc" path) — a no-op when the
/// card was already focused or nothing is unviewed, except that a sticky worktree diff is replaced
/// by another available doc because Diff is explicit-only. An open pane reveals and synchronizes
/// the doc; a closed pane selects it without marking it viewed. The idle auto-display passes
/// `retarget = false` so it never steals its own target. See docs/spec/canvas-pane.md.
let applyFocus (retarget: bool) (newFocus: FocusTarget option) (model: Model) : Model * Cmd<Msg> =
    let previousFocus = model.FocusedElement
    let activeTerminal =
        match newFocus with
        | Some (Card scopedKey) ->
            let selectedWorktree =
                findWorktree scopedKey model
                |> Option.map _.Path

            TerminalPane.projectWorktreeSelection
                (TerminalPane.isOpen model.TerminalPaneOpen model.EmbeddedTerminals)
                selectedWorktree
                model.ActiveEmbeddedTerminal
                model.EmbeddedTerminals
        | _ ->
            model.ActiveEmbeddedTerminal
    let focused =
        { model with
            FocusedElement = newFocus
            ActiveEmbeddedTerminal = activeTerminal
            Canvas.TargetWorktree = None }
    match retarget, newFocus with
    | true, Some (Card scopedKey) ->
        let unviewedDoc =
            if previousFocus <> Some (Card scopedKey) then
                CanvasAwareness.mostRecentUnviewedDoc focused.Repos focused.Canvas.LastViewedHashes scopedKey
            else
                None
        let nonDiffFallback =
            match focused.Canvas.ActiveCanvasDoc |> Map.tryFind scopedKey with
            | Some filename when CanvasState.isWorktreeDiffFilename filename ->
                findWorktree scopedKey focused
                |> Option.bind CanvasState.preferredAutomaticDoc
                |> Option.filter (fun doc -> not (CanvasState.isWorktreeDiffFilename doc.Filename))
                |> Option.map _.Filename
            | _ -> None
        match unviewedDoc |> Option.orElse nonDiffFallback, focused.Canvas.CanvasPaneOpen with
        | Some filename, true -> revealCanvasDoc Visible scopedKey filename model focused
        | Some filename, false -> revealCanvasDoc Hidden scopedKey filename model focused
        | None, _ -> reconcileMountedDocs model focused, Cmd.none
    | _ -> reconcileMountedDocs model focused, Cmd.none

let openCanvasDoc (scopedKey: string) (filename: string) (model: Model) =
    let openPane = not model.Canvas.CanvasPaneOpen
    let repos, expanded = expandRepoOwning scopedKey model.Repos
    let focused =
        let withRepos =
            { model with Repos = repos }
        let activeTerminal =
            findWorktree scopedKey withRepos
            |> Option.map _.Path
            |> fun selected ->
                TerminalPane.projectWorktreeSelection
                    (TerminalPane.isOpen
                        withRepos.TerminalPaneOpen
                        withRepos.EmbeddedTerminals)
                    selected
                    withRepos.ActiveEmbeddedTerminal
                    withRepos.EmbeddedTerminals

        { withRepos with
            FocusedElement = Some (Card scopedKey)
            ActiveEmbeddedTerminal = activeTerminal
            Canvas.CanvasPaneOpen = true
            Canvas.TargetWorktree = None }
    let opened, revealCmd =
        revealCanvasDoc
            Visible
            scopedKey
            filename
            model
            focused
    opened,
    Cmd.batch [
        if openPane then Cmd.OfAsync.attempt worktreeApi.Value.saveCanvasPaneOpen true (fun _ -> NoOp)
        if expanded then saveCollapsedReposCmd repos
        revealCmd
    ]

let openWorktreeDiff (scopedKey: string) (model: Model) =
    let filename = CanvasState.WorktreeDiffFilename
    if CanvasState.isKnownSystemView model.Repos scopedKey filename then
        let openPane = not model.Canvas.CanvasPaneOpen
        let opened, revealCmd =
            revealCanvasDoc
                Visible
                scopedKey
                filename
                model
                { model with
                    Canvas.CanvasPaneOpen = true
                    Canvas.TargetWorktree = Some scopedKey }
        opened,
        Cmd.batch [
            if openPane then Cmd.OfAsync.attempt worktreeApi.Value.saveCanvasPaneOpen true (fun _ -> NoOp)
            revealCmd
        ]
    else
        model, Cmd.none

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
        let visitedDocs =
            let current = model.Canvas.VisitedCanvasDocs |> Map.tryFind scopedKey |> Option.defaultValue []
            let filtered = current |> List.filter (fun f -> f <> filename)
            if List.isEmpty filtered then model.Canvas.VisitedCanvasDocs |> Map.remove scopedKey
            else model.Canvas.VisitedCanvasDocs |> Map.add scopedKey filtered
        let withoutArchived =
            { model with
                Repos = repos
                Canvas.ActiveCanvasDoc = model.Canvas.ActiveCanvasDoc |> Map.remove scopedKey
                Canvas.VisitedCanvasDocs = visitedDocs }
        match remainingDocs with
        | Some (first :: _) ->
            revealCanvasDoc
                (if model.Canvas.CanvasPaneOpen then Visible else Hidden)
                scopedKey
                first.Filename
                model
                withoutArchived
        | _ -> reconcileMountedDocs model withoutArchived, Cmd.none
    | Error msg ->
        Fable.Core.JS.console.error ("Archive canvas doc error:", msg)
        model, Cmd.none

// The share success/error handling + the rich-link clipboard payload. The server publishes the doc
// and returns a CanvasShareResult { Url; Title }; on Ok the client copies BOTH clipboard formats and
// raises the success banner, on Error it reuses the delivery error banner.

/// The two clipboard formats written on a successful share (see `buildClipboardPayload`).
type ClipboardPayload =
    { /// `text/html` — a titled `<a>` so rich targets (Teams, Slack, Outlook, Gmail, Word) render a
      /// hyperlink whose visible text is the doc title, hiding the long SAS URL.
      Html: string
      /// `text/plain` — the raw SAS URL, for plain targets (the VS Code editor, a terminal, Notepad).
      Text: string }

/// Escape the four characters that would otherwise break the rich `<a href="…">…</a>` — used for
/// both the href value and the anchor text so a title or URL containing `&`/`<`/`>`/`"` can't inject
/// markup or truncate the link. `&` is replaced first so the `&`-prefixed entities aren't re-escaped.
let private htmlEscape (s: string) : string =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

/// Build the dual-format clipboard payload for a shared doc: the titled HTML anchor (`text/html`)
/// and the raw URL (`text/plain`). The title is the server-resolved `CanvasShareResult.Title`, which
/// `WorktreeApi.shareCanvasDocImpl` always populates via `CanvasExport.resolveTitle` (the doc's
/// `<title>`, else a prettified filename) — so it is never blank and needs no client-side fallback.
/// Both the href and the anchor text are HTML-escaped. Pure so the payload is unit-testable without
/// a browser clipboard.
let buildClipboardPayload (result: CanvasShareResult) : ClipboardPayload =
    { Html = $"<a href=\"{htmlEscape result.Url}\">{htmlEscape result.Title}</a>"
      Text = result.Url }

/// Effect that writes BOTH clipboard formats at once via the async Clipboard API — one `ClipboardItem`
/// carrying a `text/html` and a `text/plain` Blob so every paste target self-selects the format it
/// understands — and routes the write's *actual* outcome back into the update as `ClipboardWriteResult`.
/// The write is async and can be rejected (browsers gate `navigator.clipboard.write` behind transient
/// user activation / an active document, both of which can be lost across the share network round-trip;
/// the permission may be revoked; or the API/`ClipboardItem` may be unavailable, which throws
/// synchronously). Every one of those paths dispatches an `Error` so the success banner can correct its
/// "link copied" claim instead of lying (F6). `payload.Text` is the raw SAS URL, threaded into the
/// result so a failed copy can still surface a manually-copyable link.
let private writeClipboardCmd (scopedKey: string) (filename: string) (payload: ClipboardPayload) : Cmd<Msg> =
    Cmd.ofEffect (fun dispatch ->
        let url = payload.Text
        let onCopied () = dispatch (ClipboardWriteResult (scopedKey, filename, url, Ok ()))
        let onFailed (e: string) = dispatch (ClipboardWriteResult (scopedKey, filename, url, Error e))
        Fable.Core.JsInterop.emitJsExpr (payload.Html, payload.Text, onCopied, onFailed)
            "try{navigator.clipboard.write([new ClipboardItem({'text/html': new Blob([$0], {type: 'text/html'}), 'text/plain': new Blob([$1], {type: 'text/plain'})})]).then(function(){ $2() }).catch(function(e){ console.error('[canvas] clipboard write failed', e); $3(String(e)) })}catch(e){ console.error('[canvas] clipboard write failed', e); $3(String(e)) }")

let shareCanvasDoc (scopedKey: string) (filename: string) (model: Model) =
    match model.Canvas.ShareState, findWorktree scopedKey model with
    | CanvasShareState.Idle, Some wt ->
        let request: ShareCanvasDocRequest = { WorktreePath = wt.Path; Filename = filename }
        { model with Canvas.ShareState = CanvasShareState.Publishing (scopedKey, filename) },
        Cmd.OfAsync.either worktreeApi.Value.shareCanvasDoc request (fun r -> ShareCanvasDocResult (scopedKey, filename, r)) (_.Message >> Error >> fun r -> ShareCanvasDocResult (scopedKey, filename, r))
    | _ -> model, Cmd.none

/// Send-state transition for a *failed* share. Mirrors the Ok arm's guard and the banner-XOR model
/// in Decision #10 (docs/spec/canvas-sharing.md): a share failure raises the red delivery-error
/// banner (`Failed`), but a live `Waiting` banner is independent — its queued message may still be
/// delivered (see `clearWaitingOnDelivery`), so Waiting must never be reported as a failure and is
/// preserved. Pure so the invariant is unit-testable without driving the `Error` update arm, whose
/// direct `Fable.Core.JS.console.error` call throws under .NET.
let preserveWaitingOnShareFailure (sendState: CanvasSendState) (message: string) : CanvasSendState =
    match sendState with
    | CanvasSendState.Waiting _ -> sendState
    | _ -> CanvasSendState.Failed message

let shareCanvasDocResult (scopedKey: string) (filename: string) (result: Result<CanvasShareResult, string>) (model: Model) =
    match model.Canvas.ShareState, result with
    | CanvasShareState.Publishing (activeScopedKey, activeFilename), Ok shareResult
        when activeScopedKey = scopedKey && activeFilename = filename ->
        // The share itself succeeded, but the rich-link clipboard write is async and can still be
        // rejected (transient activation / an active document — both can be lost across the share
        // round-trip), so DON'T claim "link copied" here: that would lie if the write later fails.
        // Clear any stale delivery *error* (from a prior failed share or message send) so the red error
        // banner can't linger beside the coming success banner — mirroring how the Error arm clears a
        // stale success notice — and clear any stale ShareNotice; the real banner (copied vs "copy it
        // manually") is raised by ClipboardWriteResult once the write settles (F6). A live Waiting
        // banner is an independent fact and is left untouched.
        let clearedSendState =
            match model.Canvas.CanvasSendState with
            | CanvasSendState.Failed _ -> CanvasSendState.Idle
            | other -> other
        { model with
            Canvas.CanvasSendState = clearedSendState
            Canvas.ShareNotice = None
            Canvas.ShareState = CanvasShareState.WritingClipboard (scopedKey, filename) },
        writeClipboardCmd scopedKey filename (buildClipboardPayload shareResult)
    | CanvasShareState.Publishing (activeScopedKey, activeFilename), Error msg
        when activeScopedKey = scopedKey && activeFilename = filename ->
        // Raise the existing dismissible delivery-error banner and clear any stale success notice so
        // the two never show together. A live Waiting banner is an independent fact and is preserved
        // (see preserveWaitingOnShareFailure) — its queued message may still be delivered, so Waiting
        // must never be reported as a share failure (Decision #10 banner-XOR model).
        { model with
            Canvas.CanvasSendState = preserveWaitingOnShareFailure model.Canvas.CanvasSendState msg
            Canvas.ShareNotice = None
            Canvas.ShareState = CanvasShareState.Idle },
        Cmd.ofEffect (fun _ -> Fable.Core.JS.console.error ($"Share canvas doc error ({scopedKey}/{filename}):", msg))
    | _ -> model, Cmd.none

/// Banner text for a *settled* clipboard write after a successful share (Decision #10). A landed write
/// confirms the copy ("Shared — link copied"); a rejected write drops the false "copied" claim, tells
/// the user the link is ready, and surfaces the raw SAS URL as selectable text so they can still copy
/// it by hand. Pure so the copied-vs-manual text is unit-testable without a browser clipboard.
let clipboardResultNotice (url: string) (outcome: Result<unit, string>) : string =
    match outcome with
    | Ok () -> "Shared — link copied"
    | Error _ -> $"Shared — link ready, copy it manually: {url}"

/// Route the async clipboard write's real outcome into the success banner so it reflects whether the
/// rich link was actually copied (see `writeClipboardCmd`; F6 / Decision #10). Pure — the rejection is
/// already logged in `writeClipboardCmd`'s `.catch`, so this arm only sets the banner and can be driven
/// through `update` in tests (both arms), unlike the Fable-interop-throwing share `Error` arm.
let clipboardWriteResult (scopedKey: string) (filename: string) (url: string) (outcome: Result<unit, string>) (model: Model) =
    match model.Canvas.ShareState with
    | CanvasShareState.WritingClipboard (activeScopedKey, activeFilename)
        when activeScopedKey = scopedKey && activeFilename = filename ->
        { model with
            Canvas.ShareNotice = Some (clipboardResultNotice url outcome)
            Canvas.ShareState = CanvasShareState.Idle },
        Cmd.none
    | _ -> model, Cmd.none

let dismissShareNotice (model: Model) =
    { model with Canvas = { model.Canvas with ShareNotice = None } }, Cmd.none

let navigateCanvasDoc (filename: string) (model: Model) =
    match CanvasState.activeCanvasWorktree model.FocusedElement model.Canvas.TargetWorktree with
    | Some scopedKey ->
        // Defense-in-depth: filename arrives via an in-iframe postMessage (untrusted, '*' origin).
        // Only switch tabs when it names a real CanvasDoc of the worktree driving the pane —
        // including an explicit SystemView target that differs from card focus. Committing an
        // unknown filename (e.g. one still carrying a ?query/#hash) to ActiveCanvasDoc would
        // silently fall back to the first doc (see activeVisibleDoc), landing on the wrong tab.
        if isKnownCanvasDoc model scopedKey filename then
            model, Cmd.ofMsg (SelectCanvasDoc (scopedKey, filename))
        else
            Fable.Core.JS.console.warn ($"[canvas] navigate-canvas-doc DROPPED: unknown doc '{filename}'")
            model, Cmd.none
    | _ ->
        Fable.Core.JS.console.warn "[canvas] navigate-canvas-doc DROPPED: no active canvas worktree"
        model, Cmd.none

let canvasMessageReceived (payload: string) (model: Model) =
    let visibleDoc = activeVisibleDoc model
    let worktree = visibleDoc |> Option.bind (fun (sk, _) -> findWorktree sk model)
    match visibleDoc, worktree with
    | Some (scopedKey, filename), Some wt ->
        Fable.Core.JS.console.log ($"[canvas] Forwarding message to {WorktreePath.value wt.Path} doc={filename} (payload length={payload.Length})")
        model,
        Cmd.OfAsync.either
            worktreeApi.Value.sendCanvasMessage
            { WorktreePath = wt.Path; Filename = filename; Payload = payload }
            (fun result -> CanvasSendResult(result, scopedKey, filename))
            (fun ex -> CanvasSendResult(CanvasMessageResult.Error ex.Message, scopedKey, filename))
    | Some (scopedKey, _), None ->
        Fable.Core.JS.console.warn ($"[canvas] Message DROPPED: focused card '{scopedKey}' has no matching worktree")
        model, Cmd.none
    | None, _ ->
        Fable.Core.JS.console.warn "[canvas] Message DROPPED: no active visible doc"
        model, Cmd.none

/// `CanvasSendState` is pane-global, but a send result arrives asynchronously and may belong to a
/// document the user has since navigated away from. Applying it anyway would show "Waiting for
/// session…" over an unrelated worktree, or let a stale `Ok` clear a newer wait. Only the document
/// that is still visible may move the banner.
let canvasSendResult (result: CanvasMessageResult) (scopedKey: string) (filename: string) (model: Model) =
    match activeVisibleDoc model with
    | Some(activeKey, activeFilename) when activeKey = scopedKey && activeFilename = filename ->
        match result with
        | CanvasMessageResult.Error msg ->
            Fable.Core.JS.console.error ("Canvas message error:", msg)
            { model with Canvas.CanvasSendState = CanvasSendState.Failed msg }, Cmd.none
        | CanvasMessageResult.Ok ->
            { model with Canvas.CanvasSendState = CanvasSendState.Idle }, Cmd.none
        | CanvasMessageResult.Queued ->
            Fable.Core.JS.console.log "[canvas] Message queued — waiting for session"
            { model with Canvas.CanvasSendState = CanvasSendState.Waiting scopedKey }, Cmd.none
    | _ ->
        Fable.Core.JS.console.log
            $"[canvas] Send result for '{scopedKey}/{filename}' ignored — that doc is no longer visible"

        model, Cmd.none

let dismissCanvasMessageError (model: Model) =
    { model with Canvas.CanvasSendState = CanvasSendState.Idle }, Cmd.none

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
        { model with Canvas.DocError = Some { ScopedKey = scopedKey; Filename = filename; Message = message } }, Cmd.none
    else
        model, Cmd.none

/// A canvas doc posted a message with no usable top-level string `action`, so CanvasPane could not
/// route it and surfaced it here instead of dropping it silently. Unlike canvasDocError, a malformed
/// message carries no self-identifying wt/doc fields, so it is attributed to the active *visible* doc —
/// which is, by definition, a known doc of a known worktree, satisfying the same banner invariants
/// canvasDocError relies on. With no active visible doc there is nothing to attribute it to, so it is
/// dropped. (Arrival is already logged in CanvasPane.messageListener.)
let canvasMalformedDocMessage (model: Model) =
    match activeVisibleDoc model with
    | Some (scopedKey, filename) ->
        let message =
            "This canvas doc sent a message with no usable \"action\" field, so Treemon ignored it. "
            + "The doc may be out of date — try regenerating it."
        { model with Canvas.DocError = Some { ScopedKey = scopedKey; Filename = filename; Message = message } }, Cmd.none
    | None -> model, Cmd.none

let dismissCanvasDocError (model: Model) =
    { model with Canvas.DocError = None }, Cmd.none

let morphActiveDoc (morph: CanvasMorph) (model: Model) =
    model,
    Cmd.ofEffect (fun _ ->
        Dom.document.querySelector ".canvas-iframe-active"
        |> Option.ofObj
        |> Option.iter (fun iframe ->
            Fable.Core.JsInterop.emitJsExpr
                (iframe, CanvasPane.CanvasOrigin, morph.ScopedKey, morph.Filename, morph.ContentHash)
                "(function(f,origin,scopedKey,filename,contentHash){if(f.getAttribute('data-canvas-scoped-key')===scopedKey&&f.getAttribute('data-canvas-filename')===filename){f.contentWindow.postMessage({action:'content-updated',scopedKey:scopedKey,filename:filename,contentHash:contentHash},origin)}})($0,$1,$2,$3,$4)"))

let morphComplete (morph: CanvasMorph) (model: Model) =
    let key = morph.ScopedKey, morph.Filename
    let rendered = renderedAgentDocHashes model
    match CanvasState.agentDocHash model.Repos morph.ScopedKey morph.Filename with
    | Some currentHash
        when currentHash = morph.ContentHash
             && Map.containsKey key rendered
             && CanvasState.isMounted model.Canvas.MountedAgentDocHashes morph.ScopedKey morph.Filename ->
        let updated =
            { model with
                Canvas.MountedAgentDocHashes =
                    model.Canvas.MountedAgentDocHashes |> Map.add key currentHash }
        let markViewedCmd =
            if updated.Canvas.CanvasPaneOpen && activeVisibleDoc updated = Some key then
                Cmd.ofMsg (MarkDocViewed key)
            else
                Cmd.none
        updated, markViewedCmd
    | _ -> model, Cmd.none

let messageListener (dispatch: Dispatch<Msg>) =
    CanvasPane.messageListener
        { Dispatch = CanvasMessageReceived >> dispatch
          SelectDoc = NavigateCanvasDoc >> dispatch
          OnMorphComplete = MorphComplete >> dispatch
          OnDocError = fun scopedKey filename message -> dispatch (CanvasDocError (scopedKey, filename, message))
          OnMalformedMessage = fun () -> dispatch CanvasMalformedDocMessage
          OnReclaimFocus = fun () -> dispatch (KeyPressed ("Escape", false)) }
