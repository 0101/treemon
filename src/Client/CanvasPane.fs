module CanvasPane

open Shared
open Navigation
open Feliz
open Browser

let [<Literal>] CanvasOrigin = "http://127.0.0.1:5002"
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

/// Named callbacks the canvas pane raises back to its host. Grouped into a record so the seven
/// handlers are passed by name: three share the type `string -> unit`, so positional passing let a
/// silent argument transposition compile and surface only at runtime.
type CanvasPaneCallbacks =
    { SetPosition: CanvasPosition -> unit
      SelectDoc: string -> unit
      OnOverviewClick: string -> unit
      OnOverviewDocClick: string -> string -> unit
      ArchiveDoc: string -> unit
      DismissError: unit -> unit
      DismissDocError: unit -> unit
      LaunchSession: unit -> unit }

let view (isOpen: bool) (position: CanvasPosition) (focusedDoc: (WorktreeStatus * CanvasDoc) option) (allRepos: RepoModel list) (sendState: CanvasSendState) (docError: DocJsError option) (bridgeLiveness: Map<string, BridgeLiveness>) (unviewedFilenames: Set<string>) (visitedDocs: string list) (callbacks: CanvasPaneCallbacks) =
    let { SetPosition = setPosition
          SelectDoc = selectDoc
          OnOverviewClick = onOverviewClick
          OnOverviewDocClick = onOverviewDocClick
          ArchiveDoc = archiveDoc
          DismissError = dismissError
          DismissDocError = dismissDocError
          LaunchSession = launchSession } = callbacks
    let positionButton (canvasPosition: CanvasPosition) (label: string) (title: string) =
        Html.button [
            prop.className (if canvasPosition = position then "canvas-pos-btn active" else "canvas-pos-btn")
            prop.onClick (fun _ -> setPosition canvasPosition)
            prop.title title
            prop.text label
        ]

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
                                prop.className "canvas-archive-btn"
                                prop.onClick (fun _ -> archiveDoc d.Filename)
                                prop.title "Archive this doc"
                                prop.children [ ArchiveViews.archiveIcon ]
                            ]
                        | _ -> ()
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
                    prop.title d.Filename
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
                        prop.style [
                            if not isActive then style.display.none
                        ]
                    ])
            React.fragment [
                headerBar tabs (Some doc) (doc.Kind = AgentDoc && not isFocusedDocAlive)
                errorBanner
                docErrorBanner
                waitingBanner
                yield! iframes
            ]
        | None ->
            React.fragment [
                headerBar [] None false
                errorBanner
                docErrorBanner
                waitingBanner
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
      /// A doc-side JS error arrived: (emitting filename, display message).
      OnDocError: string -> string -> unit }

let messageListener (callbacks: MessageListenerCallbacks) =
    let { Dispatch = dispatch
          SelectDoc = selectDoc
          OnMorphComplete = onMorphComplete
          OnDocError = onDocError } = callbacks
    let handler =
        fun (e: Browser.Types.Event) ->
            let me = e :?> Browser.Types.MessageEvent
            if me.origin = CanvasOrigin
               && Fable.Core.JsInterop.emitJsExpr<bool> me.data "$0 != null && typeof $0 === 'object' && typeof $0.action === 'string'"
            then
                let action = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.action"
                if action = "navigate-canvas-doc" then
                    match Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.filename" |> Option.ofObj with
                    | Some filename when filename <> "" ->
                        Fable.Core.JS.console.log ($"[canvas] navigate-canvas-doc: filename={filename}")
                        selectDoc filename
                    | _ -> ()
                elif action = "morph-complete" then
                    Fable.Core.JS.console.log "[canvas] morph-complete received"
                    onMorphComplete ()
                elif action = "canvas-doc-error" then
                    // Doc-side JS error from the iframe (errorOverlayScript). Pane-internal — surfaced
                    // in the doc-error banner, never forwarded to the session like a normal payload.
                    // The `doc` field is the emitting doc's filename, threaded so the reducer can stamp
                    // the error with the doc that threw (not the active tab); it is re-validated against
                    // the focused worktree's docs there. message/line/col cross an untrusted '*'
                    // boundary, so each field is read with a null-safe String() coercion and the display
                    // string "msg (line N:C)" is assembled here in F#.
                    let filename = Fable.Core.JsInterop.emitJsExpr<string> me.data "typeof $0.doc==='string'?$0.doc:''"
                    let rawMessage = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.message==null?'Unknown error':String($0.message)"
                    let line = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.line==null?'':String($0.line)"
                    let col = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.col==null?'':String($0.col)"
                    let body = if rawMessage.Length > 500 then rawMessage.Substring(0, 500) else rawMessage
                    let message =
                        if line = "" then body
                        elif col = "" then $"{body} (line {line})"
                        else $"{body} (line {line}:{col})"
                    Fable.Core.JS.console.warn ($"[canvas] canvas-doc-error received from {filename}: {message}")
                    onDocError filename message
                else
                    let payload = Fable.Core.JS.JSON.stringify me.data
                    Fable.Core.JS.console.log ($"[canvas] postMessage received: origin={me.origin}, action={action}, payload length={payload.Length}")
                    if payload.Length <= MaxPayloadBytes
                    then dispatch payload
                    else Fable.Core.JS.console.warn ($"[canvas] postMessage DROPPED: payload too large ({payload.Length} > {MaxPayloadBytes})")

    Dom.window.addEventListener ("message", handler)

    { new System.IDisposable with
        member _.Dispose() =
            Dom.window.removeEventListener ("message", handler) }
