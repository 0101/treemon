module CanvasPane

open Shared
open Navigation
open Feliz
open Browser

let [<Literal>] private CanvasOrigin = "http://127.0.0.1:5002"
let [<Literal>] private MaxPayloadBytes = 64_000

let iframeSrc (wt: WorktreeStatus) (doc: CanvasDoc) =
    let encodedPath = Fable.Core.JS.encodeURIComponent (WorktreePath.value wt.Path)
    $"{CanvasOrigin}/{encodedPath}/{doc.Filename}?v={doc.ContentHash}"

let private tabBar (docs: CanvasDoc list) (activeDoc: CanvasDoc) (selectDoc: string -> unit) =
    Html.div [
        prop.className "canvas-tab-bar"
        prop.children (
            docs |> List.map (fun doc ->
                Html.button [
                    prop.className (if doc.Filename = activeDoc.Filename then "canvas-tab active" else "canvas-tab")
                    prop.onClick (fun _ -> selectDoc doc.Filename)
                    prop.title doc.Filename
                    prop.text (doc.Filename.Replace(".html", ""))
                ]
            )
        )
    ]

let private latestDocModified (wt: WorktreeStatus) =
    wt.CanvasDocs
    |> List.map _.LastModified
    |> List.sortDescending
    |> List.tryHead

let private overviewView (repos: RepoModel list) (onClickEntry: string -> unit) =
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
                            Html.div [
                                prop.className "canvas-overview-entry"
                                prop.onClick (fun _ -> onClickEntry scopedKey)
                                prop.children [
                                    Html.span [
                                        prop.className "canvas-overview-branch"
                                        prop.text wt.Branch
                                    ]
                                    Html.span [
                                        prop.className "canvas-overview-count"
                                        prop.text (
                                            if wt.CanvasDocs.Length = 1 then "1 doc"
                                            else $"{wt.CanvasDocs.Length} docs")
                                    ]
                                ]
                            ]
                        )
                    ]
                ]
            )
        ]
    ]

let view (isOpen: bool) (position: CanvasPosition) (focusedDoc: (WorktreeStatus * CanvasDoc) option) (allRepos: RepoModel list) (setPosition: CanvasPosition -> unit) (selectDoc: string -> unit) (onOverviewClick: string -> unit) =
    let positionButton (canvasPosition: CanvasPosition) (label: string) (title: string) =
        Html.button [
            prop.className (if canvasPosition = position then "canvas-pos-btn active" else "canvas-pos-btn")
            prop.onClick (fun _ -> setPosition canvasPosition)
            prop.title title
            prop.text label
        ]

    let toolbar =
        Html.div [
            prop.className "canvas-toolbar"
            prop.children [
                positionButton CanvasPosition.Left "◧" "Dock left"
                positionButton CanvasPosition.Right "◨" "Dock right"
                positionButton CanvasPosition.Top "⬒" "Dock top"
                positionButton CanvasPosition.Bottom "⬓" "Dock bottom"
            ]
        ]

    let content =
        match focusedDoc with
        | Some (wt, doc) ->
            let tabs =
                if wt.CanvasDocs.Length > 1
                then [ tabBar wt.CanvasDocs doc selectDoc ]
                else []
            React.fragment [
                yield! tabs
                Html.iframe [
                    prop.className "canvas-iframe"
                    prop.src (iframeSrc wt doc)
                    prop.custom ("sandbox", "allow-scripts allow-same-origin allow-forms")
                ]
            ]
        | None ->
            overviewView allRepos onOverviewClick

    Html.div [
        prop.className (if isOpen then "canvas-pane open" else "canvas-pane")
        prop.children [ toolbar; content ]
    ]

let messageListener (dispatch: string -> unit) =
    let handler =
        fun (e: Browser.Types.Event) ->
            let me = e :?> Browser.Types.MessageEvent
            if me.origin = CanvasOrigin
               && Fable.Core.JsInterop.emitJsExpr<bool> me.data "$0 != null && typeof $0 === 'object' && typeof $0.action === 'string'"
            then
                let payload = Fable.Core.JS.JSON.stringify me.data
                if payload.Length <= MaxPayloadBytes
                then dispatch payload

    Dom.window.addEventListener ("message", handler)

    { new System.IDisposable with
        member _.Dispose() =
            Dom.window.removeEventListener ("message", handler) }
