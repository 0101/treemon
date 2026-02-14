module App

open Shared
open Elmish
open Feliz
open Fable.Remoting.Client

type SortMode =
    | ByName
    | ByActivity

type Model =
    { Worktrees: WorktreeStatus list
      IsLoading: bool
      HasError: bool
      SortMode: SortMode
      IsCompact: bool }

type Msg =
    | DataLoaded of WorktreeStatus list
    | DataFailed of exn
    | ToggleSort
    | ToggleCompact
    | Tick

let worktreeApi =
    Remoting.createApi ()
    |> Remoting.buildProxy<IWorktreeApi>

let fetchWorktrees () =
    Cmd.OfAsync.either worktreeApi.getWorktrees () DataLoaded DataFailed

let init () =
    { Worktrees = []
      IsLoading = true
      HasError = false
      SortMode = ByName
      IsCompact = false },
    fetchWorktrees ()

let sortWorktrees sortMode worktrees =
    match sortMode with
    | ByName ->
        worktrees |> List.sortBy (fun wt -> wt.Branch)
    | ByActivity ->
        worktrees |> List.sortByDescending (fun wt -> wt.LastCommitTime)

let update msg model =
    match msg with
    | DataLoaded worktrees ->
        { model with
            Worktrees = sortWorktrees model.SortMode worktrees
            IsLoading = false
            HasError = false },
        Cmd.none

    | DataFailed _ ->
        { model with
            IsLoading = false
            HasError = true },
        Cmd.none

    | ToggleSort ->
        let newSort =
            match model.SortMode with
            | ByName -> ByActivity
            | ByActivity -> ByName
        { model with
            SortMode = newSort
            Worktrees = sortWorktrees newSort model.Worktrees },
        Cmd.none

    | ToggleCompact ->
        { model with IsCompact = not model.IsCompact }, Cmd.none

    | Tick ->
        model, fetchWorktrees ()

let pollingSubscription _model : Sub<Msg> =
    let pollingEffect (dispatch: Dispatch<Msg>) =
        let intervalId =
            Fable.Core.JS.setInterval (fun () -> dispatch Tick) 15000
        { new System.IDisposable with
            member _.Dispose() = Fable.Core.JS.clearInterval intervalId }
    [ [ "polling" ], pollingEffect ]

let relativeTime (dt: System.DateTimeOffset) =
    let now = System.DateTimeOffset.Now
    let diff = now - dt
    match diff with
    | d when d.TotalMinutes < 1.0 -> "just now"
    | d when d.TotalMinutes < 60.0 -> sprintf "%dm ago" (int d.TotalMinutes)
    | d when d.TotalHours < 24.0 -> sprintf "%dh ago" (int d.TotalHours)
    | d -> sprintf "%dd ago" (int d.TotalDays)

let ccClassName =
    function
    | Active  -> "active"
    | Recent  -> "recent"
    | Idle    -> "idle"
    | Unknown -> "unknown"

let cardClassName (wt: WorktreeStatus) =
    let cc = ccClassName wt.Claude
    match wt.IsStale with
    | true  -> sprintf "wt-card cc-%s stale" cc
    | false -> sprintf "wt-card cc-%s" cc

let voteText (vote: int) =
    match vote with
    | 10 -> "Approved"
    | 5  -> "Approved w/ suggestions"
    | -5 -> "Waiting"
    | -10 -> "Rejected"
    | _ -> "No vote"

let voteSummary (votes: Map<string, int>) =
    votes
    |> Map.toList
    |> List.map (fun (name, v) -> sprintf "%s: %s" name (voteText v))
    |> String.concat ", "

let beadsTotal (b: BeadsSummary) = b.Open + b.InProgress + b.Closed

let beadsProgressPct (b: BeadsSummary) =
    let total = beadsTotal b
    match total with
    | 0 -> 0
    | _ -> (b.Closed * 100) / total

let buildBadge (bs: BuildStatus) =
    match bs with
    | NoBuild -> Html.none
    | Building -> Html.span [ prop.className "build-badge building"; prop.text "Building" ]
    | Succeeded -> Html.span [ prop.className "build-badge succeeded"; prop.text "Passed" ]
    | Failed -> Html.span [ prop.className "build-badge failed"; prop.text "Failed" ]
    | PartiallySucceeded -> Html.span [ prop.className "build-badge partial"; prop.text "Partial" ]
    | Canceled -> Html.span [ prop.className "build-badge canceled"; prop.text "Canceled" ]

let compactWorktreeCard (wt: WorktreeStatus) =
    Html.div [
        prop.className (cardClassName wt + " compact")
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className (sprintf "cc-dot %s" (ccClassName wt.Claude)) ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime wt.LastCommitTime) ]
                ]
            ]
            Html.div [
                prop.className "compact-detail"
                prop.children [
                    Html.span [
                        prop.className "beads-inline"
                        prop.text (sprintf "O:%d P:%d D:%d" wt.Beads.Open wt.Beads.InProgress wt.Beads.Closed)
                    ]
                    match wt.Pr with
                    | NoPr -> Html.none
                    | HasPr pr ->
                        Interop.createElement "a" [
                            prop.className (match pr.IsDraft with true -> "pr-badge draft" | false -> "pr-badge")
                            prop.title pr.Title
                            prop.href pr.Url
                            prop.target "_blank"
                            prop.text (sprintf "PR #%d" pr.Id)
                        ]
                        match pr.UnresolvedThreadCount with
                        | 0 -> Html.none
                        | n ->
                            Html.span [
                                prop.className "thread-badge"
                                prop.text (sprintf "%d" n)
                            ]
                        buildBadge pr.BuildStatus
                ]
            ]
        ]
    ]

let worktreeCard (wt: WorktreeStatus) =
    Html.div [
        prop.className (cardClassName wt)
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className (sprintf "cc-dot %s" (ccClassName wt.Claude)) ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                ]
            ]

            Html.div [
                prop.className "commit-line"
                prop.children [
                    Html.text wt.LastCommitMessage
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime wt.LastCommitTime) ]
                ]
            ]

            Html.div [
                prop.className "beads-row"
                prop.children [
                    Html.div [
                        prop.className "beads-counts"
                        prop.children [
                            Html.span [ prop.text (sprintf "O:%d" wt.Beads.Open) ]
                            Html.span [ prop.text (sprintf "P:%d" wt.Beads.InProgress) ]
                            Html.span [ prop.text (sprintf "D:%d" wt.Beads.Closed) ]
                        ]
                    ]
                    Html.div [
                        prop.className "progress-bar"
                        prop.children [
                            Html.div [
                                prop.className "progress-fill"
                                prop.style [ style.width (length.percent (beadsProgressPct wt.Beads)) ]
                            ]
                        ]
                    ]
                ]
            ]

            match wt.Pr with
            | NoPr -> Html.none
            | HasPr pr ->
                Html.div [
                    prop.className "pr-row"
                    prop.children [
                        Interop.createElement "a" [
                            prop.className (match pr.IsDraft with true -> "pr-badge draft" | false -> "pr-badge")
                            prop.title pr.Title
                            prop.href pr.Url
                            prop.target "_blank"
                            prop.text (sprintf "PR #%d" pr.Id)
                        ]
                        match pr.ReviewerVotes |> Map.isEmpty with
                        | true -> Html.none
                        | false ->
                            Html.span [
                                prop.className "vote-summary"
                                prop.text (voteSummary pr.ReviewerVotes)
                            ]
                        Html.span [
                            prop.className (match pr.UnresolvedThreadCount with 0 -> "thread-badge none" | _ -> "thread-badge")
                            prop.text (sprintf "%d threads" pr.UnresolvedThreadCount)
                        ]
                        buildBadge pr.BuildStatus
                    ]
                ]
        ]
    ]

let renderCard isCompact =
    match isCompact with
    | true -> compactWorktreeCard
    | false -> worktreeCard

let sortLabel =
    function
    | ByName -> "A-Z"
    | ByActivity -> "Recent"

let view model dispatch =
    Html.div [
        prop.className "dashboard"
        prop.children [
            Html.div [
                prop.className "dashboard-header"
                prop.children [
                    Html.div [
                        prop.className "header-top"
                        prop.children [
                            Html.h1 "Worktree Monitor"
                            Html.div [
                                prop.className "header-controls"
                                prop.children [
                                    Html.button [
                                        prop.className "ctrl-btn"
                                        prop.onClick (fun _ -> dispatch ToggleSort)
                                        prop.text (sprintf "Sort: %s" (sortLabel model.SortMode))
                                    ]
                                    Html.button [
                                        prop.className (match model.IsCompact with true -> "ctrl-btn active" | false -> "ctrl-btn")
                                        prop.onClick (fun _ -> dispatch ToggleCompact)
                                        prop.text "Compact"
                                    ]
                                ]
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "status-bar"
                        prop.children [
                            Html.span (sprintf "%d worktrees" (List.length model.Worktrees))
                            if model.IsLoading && model.Worktrees.IsEmpty then
                                Html.span "Loading..."
                        ]
                    ]
                ]
            ]

            if model.HasError then
                Html.div [
                    prop.className "error-bar"
                    prop.text "Failed to fetch data. Showing last known state."
                ]

            Html.div [
                prop.className "card-grid"
                prop.children (model.Worktrees |> List.map (renderCard model.IsCompact))
            ]
        ]
    ]

open Elmish.React

Program.mkProgram init update view
|> Program.withSubscription pollingSubscription
|> Program.withReactSynchronous "app"
|> Program.run
