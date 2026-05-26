module Components

open Shared
open Feliz
open Fable.Core
open Fable.Core.JsInterop

let relativeTime (now: System.DateTimeOffset) (dt: System.DateTimeOffset) =
    let diff = now - dt
    match diff with
    | d when d.TotalMinutes < 1.0 -> "just now"
    | d when d.TotalMinutes < 60.0 -> $"{int d.TotalMinutes}m ago"
    | d when d.TotalHours < 24.0 -> $"{int d.TotalHours}h ago"
    | d -> $"{int d.TotalDays}d ago"

// ResizeObserver interop
[<Emit("new ResizeObserver($0)")>]
let private createResizeObserver (callback: obj -> unit) : obj = jsNative

[<Emit("$0.observe($1)")>]
let private observeElement (observer: obj) (el: Browser.Types.Element) : unit = jsNative

[<Emit("$0.disconnect()")>]
let private disconnectObserver (observer: obj) : unit = jsNative

[<Emit("$0.scrollWidth > $0.clientWidth")>]
let private hasOverflow (el: Browser.Types.Element) : bool = jsNative

[<Emit("$0.children[$1]")>]
let private childAt (el: Browser.Types.Element) (i: int) : Browser.Types.Element = jsNative

[<Emit("$0.children.length")>]
let private childCount (el: Browser.Types.Element) : int = jsNative

/// Wrapper that progressively hides children when they cause the parent to overflow.
/// Items are hidden from first (lowest priority) to last (highest priority).
/// Parent must have overflow:hidden for scrollWidth detection to work.
[<ReactComponent>]
let FitOrHide (items: ReactElement list) =
    let elRef = React.useRef<Browser.Types.Element option>(None)

    let checkOverflow () =
        match elRef.current with
        | Some el when not (isNull el.parentElement) ->
            let parent = el.parentElement
            let count = childCount el
            Seq.init count id |> Seq.iter (fun i -> (childAt el i)?style?display <- "")
            let rec hideUntilFits i =
                if hasOverflow parent && i < count then
                    (childAt el i)?style?display <- "none"
                    hideUntilFits (i + 1)
            hideUntilFits 0
        | _ -> ()

    React.useEffect(fun () -> checkOverflow ())

    React.useEffect((fun () ->
        match elRef.current with
        | Some el when not (isNull el.parentElement) ->
            let observer = createResizeObserver (fun _ -> checkOverflow ())
            observeElement observer el.parentElement
            React.createDisposable (fun () -> disconnectObserver observer)
        | _ ->
            React.createDisposable ignore
    ), [| |])

    Html.span [
        prop.ref (fun el -> elRef.current <- if isNull el then None else Some el)
        prop.className "fit-or-hide"
        prop.children (
            items |> List.mapi (fun i item ->
                Html.span [ prop.key i; prop.children [ item ] ])
        )
    ]

let private commitGridElement (m: WorkMetrics) =
    let displayCount = min m.CommitCount 90
    let overflow = m.CommitCount - displayCount
    React.fragment [
        Html.span [
            prop.className "commit-grid"
            prop.children (List.init displayCount (fun _ -> Html.span [ prop.className "commit-square" ]))
        ]
        if overflow > 0 then
            Html.span [ prop.className "commit-overflow"; prop.text $"+{overflow}" ]
    ]

let private diffStatsElement added removed =
    Html.span [
        prop.className "diff-stats"
        prop.children [
            Html.span [ prop.className "diff-added"; prop.text $"+{added}" ]
            Html.text " "
            Html.span [ prop.className "diff-removed"; prop.text $"-{removed}" ]
        ]
    ]

let workMetricsItems (metrics: WorkMetrics option) : ReactElement list =
    match metrics with
    | None -> []
    | Some m when m.CommitCount = 0 -> []
    | Some m ->
        [
            commitGridElement m
            if m.LinesAdded <> 0 || m.LinesRemoved <> 0 then
                diffStatsElement m.LinesAdded m.LinesRemoved
        ]

let workMetricsView (metrics: WorkMetrics option) =
    match metrics with
    | None -> Html.none
    | Some m when m.CommitCount = 0 -> Html.none
    | Some m ->
        Html.span [
            prop.className "work-metrics"
            prop.children [
                commitGridElement m
                match m.LinesAdded, m.LinesRemoved with
                | 0, 0 -> Html.none
                | added, removed -> diffStatsElement added removed
            ]
        ]
