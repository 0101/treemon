module CanvasDocStrip

open Shared
open Feliz
open BrowserObserverInterop

let orderedDocs (docs: CanvasDoc list) =
    docs
    |> CanvasAwareness.agentDocs
    |> List.sortWith (fun left right ->
        match compare right.LastModified left.LastModified with
        | 0 -> compare left.Filename right.Filename
        | order -> order)

type Fit =
    { VisibleCount: int
      LastWidth: float }

let fit availableWidth gap minimumLastWidth (widths: float list) (overflowWidths: float list) =
    let total = widths.Length
    let rec choose count =
        if count = 0 then
            { VisibleCount = 0; LastWidth = 0.0 }
        else
            let hidden = total - count
            let budget = availableWidth - (if hidden = 0 then 0.0 else overflowWidths[hidden - 1])
            let prefixWidth = (widths |> List.take (count - 1) |> List.sum) + gap * float (count - 1)
            let required = prefixWidth + min widths[count - 1] minimumLastWidth
            if count = 1 || required <= budget then
                { VisibleCount = count
                  LastWidth = min widths[count - 1] (max 0.0 (budget - prefixWidth)) }
            else
                choose (count - 1)
    choose total

let private docChip now (lastWidth: float option) (doc: CanvasDoc) =
    Html.span [
        prop.key doc.Filename
        prop.className "canvas-tab canvas-card-doc"
        prop.custom ("data-filename", doc.Filename)
        prop.title $"{doc.Filename} - modified {Components.relativeTime now doc.LastModified}"
        match lastWidth with
        | Some width -> prop.style [ style.maxWidth (length.px width) ]
        | None -> ()
        prop.children [
            Html.span [
                prop.className "canvas-card-doc-title"
                prop.text (doc.Filename.Substring(0, doc.Filename.Length - 5))
            ]
            Html.span [
                prop.className "canvas-card-doc-age"
                prop.text (Components.relativeTimeCompact now doc.LastModified)
            ]
        ]
    ]

[<ReactComponent>]
let private Strip (now: System.DateTimeOffset) (docs: CanvasDoc list) =
    let root = React.useRef<Browser.Types.Element option>(None)
    let layout, updateLayout =
        React.useStateWithUpdater { VisibleCount = docs.Length; LastWidth = 180.0 }

    let measure () =
        root.current
        |> Option.filter (fun element -> element.clientWidth > 0.0)
        |> Option.iter (fun element ->
            let gap = cssPixelValue element "column-gap"
            let available =
                element.clientWidth
                - cssPixelValue element "padding-left"
                - cssPixelValue element "padding-right"
                - (elementWidths element ".canvas-card-icon" |> Array.sum)
                - gap
            let widths = elementWidths element ".canvas-card-measures .canvas-card-doc" |> Array.toList
            let overflowWidths =
                elementWidths element ".canvas-card-measures .canvas-card-overflow"
                |> Array.map ((+) gap)
                |> Array.toList
            let next =
                fit available
                    (cssPixelValue element "--canvas-card-gap")
                    (cssPixelValue element "--canvas-card-min-width")
                    widths overflowWidths
            updateLayout (fun current -> if next = current then current else next))

    React.useEffect(fun () -> measure ())
    React.useEffect(
        (fun () ->
            match root.current with
            | Some element ->
                let observer = createResizeObserver (fun _ -> measure ())
                observeElement observer element
                fun () -> disconnectObserver observer
            | None -> fun () -> ()),
        [||])

    let visibleCount = min docs.Length layout.VisibleCount
    let hiddenCount = docs.Length - visibleCount

    Html.div [
        prop.className "canvas-tab-bar canvas-card-strip"
        prop.role "group"
        prop.ref (fun element -> root.current <- Option.ofObj element)
        prop.ariaLabel $"{docs.Length} canvas documents, most recently changed first"
        prop.children [
            Html.div [
                prop.className "canvas-card-docs"
                prop.children (
                    docs
                    |> List.truncate visibleCount
                    |> List.mapi (fun index doc ->
                        docChip now (if index = visibleCount - 1 then Some layout.LastWidth else None) doc))
            ]
            if hiddenCount > 0 then
                Html.span [
                    prop.className "canvas-card-overflow"
                    prop.ariaLabel $"{hiddenCount} more canvas documents"
                    prop.text $"+{hiddenCount}"
                    prop.title (
                        docs
                        |> List.skip visibleCount
                        |> List.map (fun doc -> $"{doc.Filename} - {Components.relativeTimeCompact now doc.LastModified}")
                        |> String.concat "\n")
                ]
            Components.canvasIcon "canvas-card-icon"
            Html.div [
                prop.className "canvas-card-measures"
                prop.ariaHidden true
                prop.children [
                    yield! docs |> List.map (docChip now None)
                    for count in 1 .. docs.Length - 1 do
                        Html.span [
                            prop.key $"overflow-{count}"
                            prop.className "canvas-card-overflow"
                            prop.text $"+{count}"
                        ]
                ]
            ]
        ]
    ]

let view now docs =
    match orderedDocs docs with
    | [] -> Html.none
    | documents -> Strip now documents
