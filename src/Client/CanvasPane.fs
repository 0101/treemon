module CanvasPane

open Shared
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

let view (isOpen: bool) (position: CanvasPosition) (focusedDoc: (WorktreeStatus * CanvasDoc) option) (setPosition: CanvasPosition -> unit) (selectDoc: string -> unit) =
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
            Html.div [
                prop.className "canvas-empty"
                prop.text "No canvas doc"
            ]

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
