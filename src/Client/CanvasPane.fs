module CanvasPane

open Shared
open Navigation
open CanvasTypes
open Feliz
open Browser

// The canvas-doc server origin for THIS build, injected by Vite's `define` (see vite.config.js).
// Prod/dev default to 127.0.0.1:5002; the E2E test stack overrides CANVAS_PORT so its iframe origin
// matches its own (dynamically chosen) canvas-doc port and never collides with a running production
// instance on 5002. The .NET fallback keeps the module loadable from the (Fable-referencing) test
// assembly, where the value is never exercised (that happens only in the browser).
#if FABLE_COMPILER
let CanvasOrigin: string = Fable.Core.JsInterop.emitJsExpr () "__CANVAS_ORIGIN__"
#else
let [<Literal>] CanvasOrigin = "http://127.0.0.1:5002"
#endif
// Doc→pane message size cap, in UTF-16 code units (JS String.length): the listener below drops a
// message when JSON.stringify(me.data).length exceeds this. The injected window.canvasSend helper
// enforces the SAME cap doc-side (var MAX=64000 in canvasSendScript, src/Server/CanvasDocServer.fs)
// so its accept/drop verdict matches this check — keep the two literals in sync if you change this
// value (CanvasDocServerTests pins the helper's copy).
let [<Literal>] private MaxPayloadBytes = 64_000

let private isDocAlive (bridgeLiveness: Map<string, BridgeLiveness>) (doc: CanvasDoc) =
    match doc.OwnerSessionId with
    | None -> false
    | Some ownerId ->
        bridgeLiveness
        |> Map.values
        |> Seq.exists (fun bl -> bl.SessionId = Some ownerId && bl.IsAlive)

let private livenessDot (isAlive: bool) =
    Html.span [
        prop.className (if isAlive then "canvas-liveness-dot alive" else "canvas-liveness-dot")
        prop.title (if isAlive then "Session alive" else "No active session")
    ]

/// The Share-button glyph (the standard three-node share icon), styled like `ArchiveViews.archiveIcon`
/// (`btn-icon`, 24×24, `currentColor`) so the Share and Archive buttons sit uniformly in the header.
let private shareIcon =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 24, 24)
        svg.fill "currentColor"
        svg.children [
            Svg.path [
                svg.d "M18 16.08c-.76 0-1.44.3-1.96.77L8.91 12.7c.05-.23.09-.46.09-.7s-.04-.47-.09-.7l7.05-4.11c.54.5 1.25.81 2.04.81 1.66 0 3-1.34 3-3s-1.34-3-3-3-3 1.34-3 3c0 .24.04.47.09.7L8.04 9.81C7.5 9.31 6.79 9 6 9c-1.66 0-3 1.34-3 3s1.34 3 3 3c.79 0 1.5-.31 2.04-.81l7.12 4.16c-.05.21-.08.43-.08.65 0 1.61 1.31 2.92 2.92 2.92s2.92-1.31 2.92-2.92-1.31-2.92-2.92-2.92z"
            ]
        ]
    ]

/// Render the liveness dot only for AgentDocs. A SystemView (e.g. the beads dashboard) is
/// server-generated and has no owner session, so liveness is meaningless and the dot is omitted.
let private livenessDotFor (bridgeLiveness: Map<string, BridgeLiveness>) (doc: CanvasDoc) =
    match doc.Kind with
    | AgentDoc -> livenessDot (isDocAlive bridgeLiveness doc)
    | SystemView -> Html.none

/// Total beads issues for a worktree (Open + InProgress + Blocked + Closed). This is the label of
/// the SystemView entry, replacing the beads dashboard's former top-bar "N issues" meta.
let private beadsTotal (b: BeadsSummary) =
    b.Open + b.InProgress + b.Blocked + b.Closed

/// Render the SystemView (e.g. the beads dashboard) entry for the tab strip. It is deliberately NOT
/// a normal agent-doc tab: it uses a distinct CSS class, carries no liveness dot, and labels itself
/// with the worktree's beads issue count shown as a badge next to a "BD" glyph.
let private systemViewTab (wt: WorktreeStatus) (isActive: bool) (selectDoc: string -> unit) (doc: CanvasDoc) =
    Html.button [
        prop.className (if isActive then "canvas-system-tab active" else "canvas-system-tab")
        prop.onClick (fun _ -> selectDoc doc.Filename)
        prop.title "Beads issues"
        prop.children [
            Html.span [
                prop.className "canvas-system-tab-glyph"
                prop.text "BD"
            ]
            Html.span [
                prop.className "canvas-system-tab-count"
                prop.text (string (beadsTotal wt.Beads))
            ]
        ]
    ]

let iframeSrc (wt: WorktreeStatus) (doc: CanvasDoc) =
    let encodedPath = Fable.Core.JS.encodeURIComponent (WorktreePath.value wt.Path)
    let encodedFilename = Fable.Core.JS.encodeURIComponent doc.Filename
    $"{CanvasOrigin}/{encodedPath}/{encodedFilename}"

/// Open the doc as a standalone top-level page in a normal browser tab (the same URL the iframe
/// loads). In the pane the doc lives inside a height:100% internal-scroll iframe, which defeats
/// full-page screenshots; as a top-level page it scrolls naturally so the whole doc can be
/// captured (e.g. Edge's "Capture full page"). From an installed PWA this opens the user's
/// default browser, which is exactly where the screenshot tooling works.
let openDocInBrowserTab (wt: WorktreeStatus) (doc: CanvasDoc) : unit =
    Fable.Core.JsInterop.emitJsExpr (iframeSrc wt doc) "window.open($0,'_blank','noopener')"

let private latestDocModified (wt: WorktreeStatus) =
    wt.CanvasDocs
    |> List.map _.LastModified
    |> List.sortDescending
    |> List.tryHead

let private overviewView (repos: RepoModel list) (bridgeLiveness: Map<string, BridgeLiveness>) (onClickEntry: string -> unit) (onClickDoc: string -> string -> unit) =
    let entries =
        repos
        |> List.collect (fun repo ->
            repo.Worktrees
            |> List.filter (fun wt -> not (List.isEmpty wt.CanvasDocs))
            |> List.map (fun wt ->
                let scopedKey = WorktreePath.value wt.Path
                repo.Name, wt, scopedKey))

    let sorted =
        entries
        |> List.sortByDescending (fun (_, wt, _) -> latestDocModified wt)

    let grouped =
        sorted
        |> List.groupBy (fun (repoName, _, _) -> repoName)

    Html.div [
        prop.className "canvas-overview"
        prop.children [
            Html.div [
                prop.className "canvas-overview-title"
                prop.text "Canvas Docs"
            ]
            yield! grouped |> List.map (fun (repoName, worktrees) ->
                Html.div [
                    prop.className "canvas-overview-repo"
                    prop.children [
                        Html.div [
                            prop.className "canvas-overview-repo-name"
                            prop.text repoName
                        ]
                        yield! worktrees |> List.map (fun (_, wt, scopedKey) ->
                            Html.div [
                                prop.className "canvas-overview-entry"
                                prop.children [
                                    Html.span [
                                        prop.className "canvas-overview-branch"
                                        prop.onClick (fun _ -> onClickEntry scopedKey)
                                        prop.children [
                                            Html.text wt.Branch
                                        ]
                                    ]
                                    Html.span [
                                        prop.className "canvas-overview-docs"
                                        prop.children (
                                            wt.CanvasDocs |> List.map (fun doc ->
                                                Html.span [
                                                    prop.className "canvas-overview-doc"
                                                    prop.onClick (fun e ->
                                                        e.stopPropagation ()
                                                        onClickDoc scopedKey doc.Filename)
                                                    prop.children [
                                                        livenessDotFor bridgeLiveness doc
                                                        Html.text (doc.Filename.Replace(".html", ""))
                                                    ]
                                                ]
                                            )
                                        )
                                    ]
                                ]
                            ]
                        )
                    ]
                ]
            )
        ]
    ]

/// Named callbacks the canvas pane raises back to its host. Grouped into a record so the handlers
/// are passed by name: several share the type `string -> unit` (`SelectDoc`, `OnOverviewClick`,
/// `ArchiveDoc`, `ShareDoc`), so positional passing let a silent argument transposition compile and
/// surface only at runtime.
type CanvasPaneCallbacks =
    { SetPosition: CanvasPosition -> unit
      SetSize: CanvasSize -> unit
      SelectDoc: string -> unit
      OnOverviewClick: string -> unit
      OnOverviewDocClick: string -> string -> unit
      ArchiveDoc: string -> unit
      ShareDoc: string -> unit
      DismissError: unit -> unit
      DismissDocError: unit -> unit
      DismissShareNotice: unit -> unit
      LaunchSession: unit -> unit }

/// The subset of the canvas `CanvasState` that `view` renders from, bundled into one record so the
/// pane takes a single state value instead of a long, order-dependent positional list. Grouped for
/// the same reason as `CanvasPaneCallbacks`: the signature had been growing one positional param
/// per feature (`docError` → `size` → `shareNotice`), which invited a silent argument
/// transposition that would compile and only surface at runtime. Built by `CanvasView.fs` from
/// `model.Canvas.*`, mirroring how `canvasCallbacks` is assembled. Deliberately a subset record
/// rather than `CanvasState` itself: `CanvasPane.fs` compiles before `CanvasState.fs` in
/// `Client.fsproj`, so that type isn't nameable here.
type CanvasPaneState =
    { IsOpen: bool
      Position: CanvasPosition
      Size: CanvasSize
      SendState: CanvasSendState
      DocError: DocJsError option
      ShareNotice: string option
      BridgeLiveness: Map<string, BridgeLiveness> }

let view (state: CanvasPaneState) (focusedDoc: (WorktreeStatus * CanvasDoc) option) (allRepos: RepoModel list) (unviewedFilenames: Set<string>) (visitedDocs: string list) (callbacks: CanvasPaneCallbacks) =
    let { IsOpen = isOpen
          Position = position
          Size = size
          SendState = sendState
          DocError = docError
          ShareNotice = shareNotice
          BridgeLiveness = bridgeLiveness } = state
    let { SetPosition = setPosition
          SetSize = setSize
          SelectDoc = selectDoc
          OnOverviewClick = onOverviewClick
          OnOverviewDocClick = onOverviewDocClick
          ArchiveDoc = archiveDoc
          ShareDoc = shareDoc
          DismissError = dismissError
          DismissDocError = dismissDocError
          DismissShareNotice = dismissShareNotice
          LaunchSession = launchSession } = callbacks
    let toggleButton (baseClass: string) (isActive: bool) (onClick: unit -> unit) (label: string) (title: string) =
        Html.button [
            prop.className (if isActive then $"{baseClass} active" else baseClass)
            prop.onClick (fun _ -> onClick ())
            prop.title title
            prop.text label
        ]

    let positionButton (canvasPosition: CanvasPosition) label title =
        toggleButton "canvas-pos-btn" (canvasPosition = position) (fun () -> setPosition canvasPosition) label title

    let positionButtons =
        Html.div [
            prop.className "canvas-pos-group"
            prop.children [
                positionButton CanvasPosition.Left "◧" "Dock left"
                positionButton CanvasPosition.Right "◨" "Dock right"
                positionButton CanvasPosition.Top "⬒" "Dock top"
                positionButton CanvasPosition.Bottom "⬓" "Dock bottom"
            ]
        ]

    let sizeButton (canvasSize: CanvasSize) label title =
        toggleButton "canvas-size-btn" (canvasSize = size) (fun () -> setSize canvasSize) label title

    let sizeButtons =
        Html.div [
            prop.className "canvas-size-group"
            prop.children [
                sizeButton CanvasSize.Ratio1To1 "1:1" "Canvas same size as dashboard"
                sizeButton CanvasSize.Ratio2To1 "2:1" "Make the canvas twice the size of the dashboard"
            ]
        ]

    let headerBar (tabs: Fable.React.ReactElement list) (activeDoc: CanvasDoc option) (showLaunchBtn: bool) =
        Html.div [
            prop.className "canvas-tab-bar"
            prop.children [
                Html.div [
                    prop.className "canvas-tab-group"
                    prop.children tabs
                ]
                Html.div [
                    prop.className "canvas-header-actions"
                    prop.children [
                        if showLaunchBtn then
                            Html.button [
                                prop.className "canvas-launch-btn"
                                prop.onClick (fun _ -> launchSession ())
                                prop.title "Start a session to work on the selected canvas doc"
                                prop.text "▶ Start session"
                            ]
                        match activeDoc with
                        | Some d when d.Kind = AgentDoc ->
                            Html.button [
                                prop.className "canvas-share-btn"
                                prop.onClick (fun _ -> shareDoc d.Filename)
                                prop.title "Share this doc — copies a rich link to the clipboard"
                                prop.children [ shareIcon ]
                            ]
                        | _ -> ()
                        match activeDoc with
                        | Some d when d.Kind = AgentDoc ->
                            Html.button [
                                prop.className "canvas-archive-btn"
                                prop.onClick (fun _ -> archiveDoc d.Filename)
                                prop.title "Archive this doc"
                                prop.children [ ArchiveViews.archiveIcon ]
                            ]
                        | _ -> ()
                        sizeButtons
                        positionButtons
                    ]
                ]
            ]
        ]

    let errorBanner =
        match sendState with
        | CanvasSendState.Failed msg ->
            Html.div [
                prop.className "canvas-error-banner"
                prop.children [
                    Html.span [ prop.text msg ]
                    Html.button [
                        prop.className "canvas-error-dismiss"
                        prop.onClick (fun _ -> dismissError ())
                        prop.text "✕"
                    ]
                ]
            ]
        | _ -> Html.none

    // Doc-side JS error banner — a distinct source from CanvasSendState.Failed (which is a
    // pane→session *delivery* failure). Rendered as a normal flex child of the column-layout pane
    // (never absolutely positioned), so it pushes the iframe down instead of covering doc content.
    // Doc-scoped: shown ONLY when the stored error's stamp matches the currently focused doc, so a
    // stale error from a doc you navigated away from (tab, card, or keyboard focus) is never shown
    // over a different doc — and never over the overview (focusedDoc = None).
    let docErrorBanner =
        match docError, focusedDoc with
        | Some err, Some (wt, doc) when WorktreePath.value wt.Path = err.ScopedKey && doc.Filename = err.Filename ->
            Html.div [
                prop.className "canvas-doc-error-banner"
                prop.children [
                    Html.span [
                        prop.className "canvas-doc-error-text"
                        prop.title err.Message
                        prop.text $"Doc error: {err.Message}"
                    ]
                    Html.button [
                        prop.className "canvas-doc-error-dismiss"
                        prop.onClick (fun _ -> dismissDocError ())
                        prop.text "✕"
                    ]
                ]
            ]
        | _ -> Html.none

    let waitingBanner =
        match sendState with
        | CanvasSendState.Waiting _ ->
            Html.div [
                prop.className "canvas-waiting-banner"
                prop.children [
                    Html.span [ prop.text "Waiting for session…" ]
                    Html.button [
                        prop.className "canvas-waiting-dismiss"
                        prop.onClick (fun _ -> dismissError ())
                        prop.text "✕"
                    ]
                ]
            ]
        | _ -> Html.none

    // Success banner shown after a doc is shared and its rich link copied to the clipboard. A share
    // *failure* reuses the red errorBanner (CanvasSendState.Failed) above; this green notice is the
    // Ok path and is dismissible like the others.
    let shareBanner =
        match shareNotice with
        | Some msg ->
            Html.div [
                prop.className "canvas-share-banner"
                prop.children [
                    Html.span [ prop.text msg ]
                    Html.button [
                        prop.className "canvas-share-dismiss"
                        prop.onClick (fun _ -> dismissShareNotice ())
                        prop.text "✕"
                    ]
                ]
            ]
        | None -> Html.none

    let content =
        match focusedDoc with
        | Some (wt, doc) ->
            let isFocusedDocAlive = isDocAlive bridgeLiveness doc
            // The SystemView (beads) entry gets a distinct affordance pinned to the far left of the
            // strip; AgentDocs keep the normal tab treatment. The strip always renders the active
            // doc's tab — including a lone AgentDoc (so it gets a labeled tab instead of a bare
            // iframe) and a lone SystemView (so its beads-count badge stays visible).
            let agentTab (d: CanvasDoc) =
                let isActive = d.Filename = doc.Filename
                let isViewed = not (Set.contains d.Filename unviewedFilenames)
                let cls =
                    [ "canvas-tab"
                      if isActive then "active"
                      if isViewed && not isActive then "canvas-tab-viewed" ]
                    |> String.concat " "
                Html.button [
                    prop.className cls
                    prop.onClick (fun _ -> selectDoc d.Filename)
                    prop.onDoubleClick (fun _ -> openDocInBrowserTab wt d)
                    prop.title $"{d.Filename} — double-click to open in a browser tab (for full-page screenshots)"
                    prop.children [
                        livenessDotFor bridgeLiveness d
                        Html.text (d.Filename.Replace(".html", ""))
                        // On-disk freshness of the authored file, refreshed on the pane's existing
                        // render cadence. Scoped to AgentDoc tabs (a SystemView is server-generated
                        // and carries no authored-file age).
                        Html.span [
                            prop.className "canvas-tab-age"
                            prop.text (Components.relativeTimeCompact System.DateTimeOffset.Now d.LastModified)
                        ]
                    ]
                ]
            // Always render the active doc's tab — no lone-AgentDoc suppression — while preserving
            // the SystemView-first ordering (so the beads "BD" tab stays pinned left when present).
            let tabs =
                wt.CanvasDocs
                |> List.sortBy (fun d -> match d.Kind with SystemView -> 0 | AgentDoc -> 1)
                |> List.map (fun d ->
                    match d.Kind with
                    | SystemView -> systemViewTab wt (d.Filename = doc.Filename) selectDoc d
                    | AgentDoc -> agentTab d)
            // Render iframes for all visited docs; active is visible, others are hidden.
            // Ensure the active doc is always included even if not yet in visitedDocs.
            let docsToRender =
                if visitedDocs |> List.contains doc.Filename then visitedDocs
                else doc.Filename :: visitedDocs
            let iframes =
                docsToRender
                |> List.choose (fun filename ->
                    wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = filename)
                    |> Option.map (fun d -> d, filename = doc.Filename))
                |> List.map (fun (d, isActive) ->
                    Html.iframe [
                        prop.key (WorktreePath.value wt.Path + "/" + d.Filename)
                        prop.className (if isActive then "canvas-iframe canvas-iframe-active" else "canvas-iframe")
                        prop.src (iframeSrc wt d)
                        prop.custom ("sandbox", "allow-scripts allow-same-origin allow-forms allow-popups")
                        prop.custom ("allow", "clipboard-write")
                        prop.style [
                            if not isActive then style.display.none
                        ]
                    ])
            React.fragment [
                headerBar tabs (Some doc) (doc.Kind = AgentDoc && not isFocusedDocAlive)
                errorBanner
                docErrorBanner
                waitingBanner
                shareBanner
                yield! iframes
            ]
        | None ->
            React.fragment [
                headerBar [] None false
                errorBanner
                docErrorBanner
                waitingBanner
                shareBanner
                overviewView allRepos bridgeLiveness onOverviewClick onOverviewDocClick
            ]

    let paneClass =
        [ "canvas-pane"
          if isOpen then "open" ]
        |> String.concat " "

    Html.div [
        prop.className paneClass
        prop.children [ content ]
    ]

/// Callbacks the pane-internal `messageListener` raises for each recognized doc→pane message.
/// Grouped into a record (mirroring CanvasPaneCallbacks) so they are passed by name: Dispatch and
/// SelectDoc both share the type `string -> unit`, so positional passing let a silent argument
/// transposition compile and surface only at runtime.
type MessageListenerCallbacks =
    { /// Forward an unrecognized (normal) doc payload on to the session.
      Dispatch: string -> unit
      /// Switch the active tab to the named doc (navigate-canvas-doc).
      SelectDoc: string -> unit
      /// The active doc finished an idiomorph (morph-complete).
      OnMorphComplete: unit -> unit
      /// A doc-side JS error arrived: (emitting worktree scopedKey, emitting filename, display message).
      OnDocError: string -> string -> string -> unit
      /// A canvas-origin object message arrived with no usable top-level string `action`, from the
      /// active (non-hidden) doc — surfaced instead of silently dropped.
      OnMalformedMessage: unit -> unit }

let messageListener (callbacks: MessageListenerCallbacks) =
    let { Dispatch = dispatch
          SelectDoc = selectDoc
          OnMorphComplete = onMorphComplete
          OnDocError = onDocError
          OnMalformedMessage = onMalformedMessage } = callbacks
    let handler =
        fun (e: Browser.Types.Event) ->
            let me = e :?> Browser.Types.MessageEvent
            if me.origin = CanvasOrigin
               && Fable.Core.JsInterop.emitJsExpr<bool> me.data "$0 != null && typeof $0 === 'object'"
            then
                // True when THIS message came from a mounted-but-HIDDEN canvas iframe (a visited doc that
                // stays mounted and keeps running JS) rather than the active one. The origin check above
                // already proves the sender is a canvas doc iframe, so a hidden-iframe match means a
                // background/co-resident doc is posting. Session forwarding and navigate-canvas-doc honor
                // only the active doc, so such a message is dropped — a hidden doc can't inject a payload
                // (or force a tab switch) attributed to the active doc's owner session. The per-doc error
                // path is exempt: it self-identifies via wt/doc and may legitimately report from any iframe.
                let isFromHiddenCanvasIframe () =
                    Fable.Core.JsInterop.emitJsExpr<bool> me "Array.prototype.some.call(document.querySelectorAll('.canvas-iframe:not(.canvas-iframe-active)'), function(f){return f.contentWindow === $0.source})"
                if Fable.Core.JsInterop.emitJsExpr<bool> me.data "typeof $0.action === 'string'" then
                    let action = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.action"
                    if action = "navigate-canvas-doc" then
                        match Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.filename" |> Option.ofObj with
                        | Some filename when filename <> "" ->
                            if isFromHiddenCanvasIframe () then
                                Fable.Core.JS.console.warn "[canvas] navigate-canvas-doc DROPPED: from a hidden background doc iframe"
                            else
                                Fable.Core.JS.console.log ($"[canvas] navigate-canvas-doc: filename={filename}")
                                selectDoc filename
                        | _ -> ()
                    elif action = "morph-complete" then
                        Fable.Core.JS.console.log "[canvas] morph-complete received"
                        onMorphComplete ()
                    elif action = "canvas-doc-error" then
                        // Doc-side JS error from the iframe (errorOverlayScript). Pane-internal — surfaced
                        // in the doc-error banner, never forwarded to the session like a normal payload.
                        // The `wt`/`doc` fields are the emitting worktree + filename, so the reducer stamps
                        // the error with the doc that threw (not the active tab); they are re-validated
                        // against that worktree's docs there. wt/message/line/col cross an untrusted '*'
                        // boundary, so each field is read with a null-safe String() coercion and the display
                        // string "msg (line N:C)" is assembled here in F#.
                        let scopedKey = Fable.Core.JsInterop.emitJsExpr<string> me.data "typeof $0.wt==='string'?$0.wt:''"
                        let filename = Fable.Core.JsInterop.emitJsExpr<string> me.data "typeof $0.doc==='string'?$0.doc:''"
                        let rawMessage = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.message==null?'Unknown error':String($0.message)"
                        let line = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.line==null?'':String($0.line)"
                        let col = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.col==null?'':String($0.col)"
                        let body = if rawMessage.Length > 500 then rawMessage.Substring(0, 500) else rawMessage
                        let message =
                            if line = "" then body
                            elif col = "" then $"{body} (line {line})"
                            else $"{body} (line {line}:{col})"
                        Fable.Core.JS.console.warn ($"[canvas] canvas-doc-error received from {scopedKey}/{filename}: {message}")
                        onDocError scopedKey filename message
                    else
                        let payload = Fable.Core.JS.JSON.stringify me.data
                        Fable.Core.JS.console.log ($"[canvas] postMessage received: origin={me.origin}, action={action}, payload length={payload.Length}")
                        if isFromHiddenCanvasIframe () then
                            Fable.Core.JS.console.warn ($"[canvas] postMessage DROPPED: from a hidden background doc iframe (action={action})")
                        elif payload.Length <= MaxPayloadBytes then
                            dispatch payload
                        else
                            Fable.Core.JS.console.warn ($"[canvas] postMessage DROPPED: payload too large ({payload.Length} > {MaxPayloadBytes})")
                elif not (isFromHiddenCanvasIframe ()) then
                    // Canvas-origin, valid object, but no usable top-level string `action`. Surface it
                    // (banner + warn) only for the active doc; a hidden background iframe must stay silent
                    // so the banner only ever shows for the doc the user is looking at. Other origins are
                    // already filtered out by the outer guard.
                    let keys = Fable.Core.JsInterop.emitJsExpr<string> me.data "Object.keys($0).join(', ')"
                    Fable.Core.JS.console.warn ($"[canvas] message ignored: no usable string 'action' (keys: {keys})")
                    onMalformedMessage ()

    Dom.window.addEventListener ("message", handler)

    { new System.IDisposable with
        member _.Dispose() =
            Dom.window.removeEventListener ("message", handler) }
