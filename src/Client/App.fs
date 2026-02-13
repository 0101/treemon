module App

open Elmish
open Feliz

type Model = { Message: string }

type Msg = | NoOp

let init () = { Message = "Worktree Monitor" }, Cmd.none

let update msg model =
    match msg with
    | NoOp -> model, Cmd.none

let view model dispatch =
    Html.div [
        Html.h1 model.Message
    ]

open Elmish.React

Program.mkProgram init update view
|> Program.withReactSynchronous "app"
|> Program.run
