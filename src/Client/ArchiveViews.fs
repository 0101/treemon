module ArchiveViews

open Shared
open Elmish
open Feliz
open Fable.Core
open Fable.Core.JsInterop

type Msg =
    | Archive of WorktreePath
    | Unarchive of WorktreePath
    | OpCompleted of Result<unit, string>

type UpdateResult = { RefreshWorktrees: bool }

let update (api: Lazy<IWorktreeApi>) msg : UpdateResult * Cmd<Msg> =
    match msg with
    | Archive path ->
        { RefreshWorktrees = false },
        Cmd.OfAsync.perform (fun () -> api.Value.archiveWorktree path) () OpCompleted
    | Unarchive path ->
        { RefreshWorktrees = false },
        Cmd.OfAsync.perform (fun () -> api.Value.unarchiveWorktree path) () OpCompleted
    | OpCompleted (Ok _) ->
        { RefreshWorktrees = true }, Cmd.none
    | OpCompleted (Error _) ->
        { RefreshWorktrees = false }, Cmd.none

let relativeTime (now: System.DateTimeOffset) (dt: System.DateTimeOffset) =
    let diff = now - dt
    match diff with
    | d when d.TotalMinutes < 1.0 -> "just now"
    | d when d.TotalMinutes < 60.0 -> $"{int d.TotalMinutes}m ago"
    | d when d.TotalHours < 24.0 -> $"{int d.TotalHours}h ago"
    | d -> $"{int d.TotalDays}d ago"

// ResizeObserver interop for fitOrHide component
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
            // Show all children
            Seq.init count id |> Seq.iter (fun i -> (childAt el i)?style?display <- "")
            // Hide from first (lowest priority) until parent fits
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

let archiveIcon =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 24, 24)
        svg.fill "currentColor"
        svg.children [
            Svg.path [
                svg.d "M2 5C2 4.05719 2 3.58579 2.29289 3.29289C2.58579 3 3.05719 3 4 3H20C20.9428 3 21.4142 3 21.7071 3.29289C22 3.58579 22 4.05719 22 5C22 5.94281 22 6.41421 21.7071 6.70711C21.4142 7 20.9428 7 20 7H4C3.05719 7 2.58579 7 2.29289 6.70711C2 6.41421 2 5.94281 2 5Z"
            ]
            Svg.path [
                svg.custom ("fillRule", "evenodd")
                svg.custom ("clipRule", "evenodd")
                svg.d "M20.0689 8.49993C20.2101 8.49999 20.3551 8.50005 20.5 8.49805V12.9999C20.5 16.7711 20.5 18.6568 19.3284 19.8283C18.1569 20.9999 16.2712 20.9999 12.5 20.9999H11.5C7.72876 20.9999 5.84315 20.9999 4.67157 19.8283C3.5 18.6568 3.5 16.7711 3.5 12.9999V8.49805C3.64488 8.50005 3.78999 8.49999 3.93114 8.49993H20.0689ZM9 11.9999C9 11.5339 9 11.301 9.07612 11.1172C9.17761 10.8722 9.37229 10.6775 9.61732 10.576C9.80109 10.4999 10.0341 10.4999 10.5 10.4999H13.5C13.9659 10.4999 14.1989 10.4999 14.3827 10.576C14.6277 10.6775 14.8224 10.8722 14.9239 11.1172C15 11.301 15 11.5339 15 11.9999C15 12.4658 15 12.6988 14.9239 12.8826C14.8224 13.1276 14.6277 13.3223 14.3827 13.4238C14.1989 13.4999 13.9659 13.4999 13.5 13.4999H10.5C10.0341 13.4999 9.80109 13.4999 9.61732 13.4238C9.37229 13.3223 9.17761 13.1276 9.07612 12.8826C9 12.6988 9 12.4658 9 11.9999Z"
            ]
        ]
    ]

let archiveCard dispatch (wt: WorktreeStatus) =
    Html.div [
        prop.key wt.Branch
        prop.className "archive-card"
        prop.children [
            Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
            workMetricsView wt.WorkMetrics
            Html.span [ prop.className "commit-time"; prop.text (relativeTime System.DateTimeOffset.Now wt.LastCommitTime) ]
            Html.button [
                prop.className "unarchive-btn"
                prop.title "Unarchive worktree"
                prop.onClick (fun e -> e.stopPropagation(); dispatch (Unarchive wt.Path))
                prop.children [ archiveIcon ]
            ]
        ]
    ]

let archiveSection dispatch (archived: WorktreeStatus list) =
    match archived with
    | [] -> Html.none
    | worktrees ->
        Html.div [
            prop.className "archive-section"
            prop.children (worktrees |> List.map (archiveCard dispatch))
        ]
