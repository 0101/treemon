module ArchiveViews

open Shared
open Elmish
open Feliz

type Msg =
    | Archive of string
    | Unarchive of string
    | OpCompleted of Result<unit, string>

type UpdateResult = { RefreshWorktrees: bool }

let update (api: Lazy<IWorktreeApi>) msg : UpdateResult * Cmd<Msg> =
    match msg with
    | Archive branch ->
        { RefreshWorktrees = false },
        Cmd.OfAsync.perform (fun () -> api.Value.archiveWorktree branch) () OpCompleted
    | Unarchive branch ->
        { RefreshWorktrees = false },
        Cmd.OfAsync.perform (fun () -> api.Value.unarchiveWorktree branch) () OpCompleted
    | OpCompleted (Ok _) ->
        { RefreshWorktrees = true }, Cmd.none
    | OpCompleted (Error _) ->
        { RefreshWorktrees = false }, Cmd.none

let relativeTime (dt: System.DateTimeOffset) =
    let now = System.DateTimeOffset.Now
    let diff = now - dt
    match diff with
    | d when d.TotalMinutes < 1.0 -> "just now"
    | d when d.TotalMinutes < 60.0 -> $"{int d.TotalMinutes}m ago"
    | d when d.TotalHours < 24.0 -> $"{int d.TotalHours}h ago"
    | d -> $"{int d.TotalDays}d ago"

let workMetricsView (metrics: WorkMetrics option) =
    match metrics with
    | None -> Html.none
    | Some m when m.CommitCount = 0 -> Html.none
    | Some m ->
        let displayCount = min m.CommitCount 90
        let overflow = m.CommitCount - displayCount
        Html.span [
            prop.className "work-metrics"
            prop.children [
                Html.span [
                    prop.className "commit-grid"
                    prop.children (
                        List.init displayCount (fun _ ->
                            Html.span [ prop.className "commit-square" ])
                    )
                ]
                if overflow > 0 then
                    Html.span [ prop.className "commit-overflow"; prop.text $"+{overflow}" ]
                match m.LinesAdded, m.LinesRemoved with
                | 0, 0 -> Html.none
                | added, removed ->
                    Html.span [
                        prop.className "diff-stats"
                        prop.children [
                            Html.span [ prop.className "diff-added"; prop.text $"+{added}" ]
                            Html.text " "
                            Html.span [ prop.className "diff-removed"; prop.text $"-{removed}" ]
                        ]
                    ]
            ]
        ]

let archiveButton dispatch (wt: WorktreeStatus) =
    Html.button [
        prop.className "archive-btn"
        prop.title "Archive worktree"
        prop.onClick (fun e -> e.stopPropagation(); dispatch (Archive wt.Branch))
        prop.text "\u2193\u2502"
    ]

let archiveCard dispatch (wt: WorktreeStatus) =
    Html.div [
        prop.key wt.Branch
        prop.className "archive-card"
        prop.children [
            Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
            workMetricsView wt.WorkMetrics
            Html.span [ prop.className "commit-time"; prop.text (relativeTime wt.LastCommitTime) ]
            Html.button [
                prop.className "unarchive-btn"
                prop.title "Unarchive worktree"
                prop.onClick (fun e -> e.stopPropagation(); dispatch (Unarchive wt.Branch))
                prop.text "\u2191\u2502"
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
