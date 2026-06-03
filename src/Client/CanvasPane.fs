module CanvasPane

open Shared
open Navigation
open Feliz
open Browser

let [<Literal>] private CanvasOrigin = "http://127.0.0.1:5002"
let [<Literal>] private MaxPayloadBytes = 64_000

let private livenessDot (isAlive: bool) =
    Html.span [
        prop.className (if isAlive then "canvas-liveness-dot alive" else "canvas-liveness-dot")
        prop.title (if isAlive then "Session alive" else "No active session")
    ]

let iframeSrc (wt: WorktreeStatus) (doc: CanvasDoc) =
    let encodedPath = Fable.Core.JS.encodeURIComponent (WorktreePath.value wt.Path)
    let encodedFilename = Fable.Core.JS.encodeURIComponent doc.Filename
    $"{CanvasOrigin}/{encodedPath}/{encodedFilename}?v={doc.ContentHash}"

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
                let scopedKey = $"{RepoId.value repo.RepoId}/{wt.Branch}"
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
                            let wtPath = WorktreePath.value wt.Path
                            let isAlive =
                                bridgeLiveness
                                |> Map.tryFind wtPath
                                |> Option.map _.IsAlive
                                |> Option.defaultValue false
                            Html.div [
                                prop.className "canvas-overview-entry"
                                prop.children [
                                    Html.span [
                                        prop.className "canvas-overview-branch"
                                        prop.onClick (fun _ -> onClickEntry scopedKey)
                                        prop.children [
                                            livenessDot isAlive
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
                                                    prop.text (doc.Filename.Replace(".html", ""))
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

let view (isOpen: bool) (position: CanvasPosition) (focusedDoc: (WorktreeStatus * CanvasDoc) option) (allRepos: RepoModel list) (messageError: string option) (messageWaiting: bool) (bridgeLiveness: Map<string, BridgeLiveness>) (setPosition: CanvasPosition -> unit) (selectDoc: string -> unit) (onOverviewClick: string -> unit) (onOverviewDocClick: string -> string -> unit) (archiveDoc: string -> unit) (dismissError: unit -> unit) (launchSession: unit -> unit) =
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

    let headerBar (tabs: Fable.React.ReactElement list) (activeFilename: string option) (showLaunchBtn: bool) =
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
                        match activeFilename with
                        | Some filename ->
                            Html.button [
                                prop.className "canvas-archive-btn"
                                prop.onClick (fun _ -> archiveDoc filename)
                                prop.title "Archive this doc"
                                prop.children [ ArchiveViews.archiveIcon ]
                            ]
                        | None -> ()
                        positionButtons
                    ]
                ]
            ]
        ]

    let errorBanner =
        match messageError with
        | Some msg ->
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
        | None -> Html.none

    let waitingBanner =
        if messageWaiting then
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
        else
            Html.none

    let content =
        match focusedDoc with
        | Some (wt, doc) ->
            let wtPath = WorktreePath.value wt.Path
            let isWtAlive =
                bridgeLiveness
                |> Map.tryFind wtPath
                |> Option.map _.IsAlive
                |> Option.defaultValue false
            let tabs =
                if wt.CanvasDocs.Length > 1
                then wt.CanvasDocs |> List.map (fun d ->
                    Html.button [
                        prop.className (if d.Filename = doc.Filename then "canvas-tab active" else "canvas-tab")
                        prop.onClick (fun _ -> selectDoc d.Filename)
                        prop.title d.Filename
                        prop.children [
                            livenessDot isWtAlive
                            Html.text (d.Filename.Replace(".html", ""))
                        ]
                    ])
                else []
            React.fragment [
                headerBar tabs (Some doc.Filename) (not isWtAlive)
                errorBanner
                waitingBanner
                Html.iframe [
                    prop.className "canvas-iframe"
                    prop.src (iframeSrc wt doc)
                    prop.custom ("sandbox", "allow-scripts allow-same-origin allow-forms")
                ]
            ]
        | None ->
            React.fragment [
                headerBar [] None false
                errorBanner
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

let messageListener (dispatch: string -> unit) =
    let handler =
        fun (e: Browser.Types.Event) ->
            let me = e :?> Browser.Types.MessageEvent
            if me.origin = CanvasOrigin
               && Fable.Core.JsInterop.emitJsExpr<bool> me.data "$0 != null && typeof $0 === 'object' && typeof $0.action === 'string'"
            then
                let payload = Fable.Core.JS.JSON.stringify me.data
                let action = Fable.Core.JsInterop.emitJsExpr<string> me.data "$0.action"
                Fable.Core.JS.console.log ($"[canvas] postMessage received: origin={me.origin}, action={action}, payload length={payload.Length}")
                if payload.Length <= MaxPayloadBytes
                then dispatch payload
                else Fable.Core.JS.console.warn ($"[canvas] postMessage DROPPED: payload too large ({payload.Length} > {MaxPayloadBytes})")

    Dom.window.addEventListener ("message", handler)

    { new System.IDisposable with
        member _.Dispose() =
            Dom.window.removeEventListener ("message", handler) }
