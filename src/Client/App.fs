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

let view model _dispatch =
    Html.div [
        Html.h1 "Worktree Monitor"
        if model.IsLoading && model.Worktrees.IsEmpty then
            Html.p "Loading..."
        if model.HasError then
            Html.p [
                prop.style [ style.color.red ]
                prop.text "Failed to fetch data. Showing last known state."
            ]
        Html.p (sprintf "%d worktrees" (List.length model.Worktrees))
    ]

open Elmish.React

Program.mkProgram init update view
|> Program.withSubscription pollingSubscription
|> Program.withReactSynchronous "app"
|> Program.run
