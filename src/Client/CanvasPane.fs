module CanvasPane

open Shared
open Feliz
open Browser

let [<Literal>] private CanvasOrigin = "http://127.0.0.1:5002"
let [<Literal>] private MaxPayloadBytes = 64_000

let iframeSrc (wt: WorktreeStatus) (doc: CanvasDoc) =
    let encodedPath = Fable.Core.JS.encodeURIComponent (WorktreePath.value wt.Path)
    $"{CanvasOrigin}/{encodedPath}/{doc.Filename}"

let view (isOpen: bool) (focusedDoc: (WorktreeStatus * CanvasDoc) option) =
    let content =
        match focusedDoc with
        | Some (wt, doc) ->
            Html.iframe [
                prop.className "canvas-iframe"
                prop.src (iframeSrc wt doc)
                prop.custom ("sandbox", "allow-scripts allow-same-origin allow-forms")
            ]
        | None ->
            Html.div [
                prop.className "canvas-empty"
                prop.text "No canvas doc"
            ]
    Html.div [
        prop.className (if isOpen then "canvas-pane open" else "canvas-pane")
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
                if payload.Length <= MaxPayloadBytes
                then dispatch payload

    Dom.window.addEventListener ("message", handler)

    { new System.IDisposable with
        member _.Dispose() =
            Dom.window.removeEventListener ("message", handler) }
