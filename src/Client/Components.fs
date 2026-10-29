module Components

open Shared
open Feliz
open Fable.Core
open Fable.Core.JsInterop
open BrowserObserverInterop

/// Shared time-bucketing ladder for relativeTime / relativeTimeCompact: the same four thresholds and
/// the same int truncation, parameterised only by the sub-minute label and the per-bucket suffix.
let private formatTimeDelta (subMinute: string) (suffix: string) (now: System.DateTimeOffset) (dt: System.DateTimeOffset) =
    let diff = now - dt
    match diff with
    | d when d.TotalMinutes < 1.0 -> subMinute
    | d when d.TotalMinutes < 60.0 -> $"{int d.TotalMinutes}m{suffix}"
    | d when d.TotalHours < 24.0 -> $"{int d.TotalHours}h{suffix}"
    | d -> $"{int d.TotalDays}d{suffix}"

let relativeTime (now: System.DateTimeOffset) (dt: System.DateTimeOffset) =
    formatTimeDelta "just now" " ago" now dt

/// Compact sibling of relativeTime for tight UI like the canvas tab strip: renders
/// "now"/"3m"/"2h"/"2d" (sub-minute is "now", not "just now", and the buckets carry no " ago" suffix).
let relativeTimeCompact (now: System.DateTimeOffset) (dt: System.DateTimeOffset) =
    formatTimeDelta "now" "" now dt

let cardTitle (wt: WorktreeStatus) =
    if wt.Branch = WorktreeStatus.DetachedBranchName then WorktreePath.displayName wt.Path
    else wt.Branch

let canvasIcon (className: string) =
    Svg.svg [
        svg.className className
        svg.viewBox (0, 0, 297, 297)
        svg.fill "currentColor"
        svg.children [
            Svg.title "Canvas"
            Svg.path [
                svg.d "M247.5,188H241V43h6.5c5.523,0,10-4.478,10-10s-4.477-10-10-10h-89V10c0-5.523-4.477-10-10-10s-10,4.477-10,10v13h-89c-5.522,0-10,4.478-10,10s4.478,10,10,10H56v145h-6.5c-5.522,0-10,4.477-10,10s4.478,10,10,10h53.829l-30.114,75.283c-2.051,5.128,0.443,10.947,5.571,12.999c1.218,0.487,2.475,0.718,3.711,0.718c3.969,0,7.724-2.379,9.288-6.289l33.001-82.5c0.027-0.07,0.046-0.141,0.072-0.211H138.5v78.997c0,5.522,4.477,10,10,10s10-4.478,10-10V208h13.642c0.026,0.07,0.045,0.141,0.072,0.211l33.001,82.5c1.564,3.91,5.319,6.289,9.288,6.289c1.236,0,2.493-0.231,3.711-0.718c5.128-2.052,7.622-7.871,5.571-12.999L193.671,208H247.5c5.523,0,10-4.477,10-10S253.023,188,247.5,188z M76,43h145v145H76V43z"
            ]
        ]
    ]

let refreshIcon () =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 24, 24)
        svg.fill "none"
        svg.stroke "currentColor"
        svg.custom ("strokeWidth", "2")
        svg.custom ("strokeLinecap", "round")
        svg.custom ("strokeLinejoin", "round")
        svg.children [
            Svg.path [ svg.d "M3 12a9 9 0 0 1 14.65-7" ]
            Svg.path [ svg.d "M21 12a9 9 0 0 1-14.65 7" ]
            Svg.path [ svg.d "M17 2v4h-4" ]
            Svg.path [ svg.d "M7 22v-4h4" ]
        ]
    ]

let stepStatusClassName (status: StepStatus option) =
    match status with
    | Some StepStatus.Running -> "event-status running"
    | Some StepStatus.Succeeded -> "event-status success"
    | Some (StepStatus.Failed _) -> "event-status failed"
    | Some StepStatus.Pending -> "event-status"
    | None -> "event-status"

let stepStatusText (status: StepStatus option) =
    match status with
    | Some StepStatus.Running -> "running"
    | Some StepStatus.Succeeded -> "success"
    | Some (StepStatus.Failed msg) -> match msg with "" -> "failed" | _ -> $"failed: {msg}"
    | _ -> ""

let relativeEventTime (dt: System.DateTimeOffset) =
    let diff = System.DateTimeOffset.Now - dt
    match diff with
    | d when d.TotalSeconds < 60.0 -> $"{int d.TotalSeconds |> max 0}s ago"
    | d when d.TotalMinutes < 60.0 -> $"{int d.TotalMinutes}m ago"
    | d when d.TotalHours < 24.0 -> $"{int d.TotalHours}h ago"
    | d -> $"{int d.TotalDays}d ago"

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
            fun () -> disconnectObserver observer
        | _ ->
            fun () -> ()
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
    React.Fragment [
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
    | Some m ->
        [
            if m.CommitCount > 0 then
                commitGridElement m
            if m.LinesAdded <> 0 || m.LinesRemoved <> 0 then
                diffStatsElement m.LinesAdded m.LinesRemoved
        ]
