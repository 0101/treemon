module WorkspaceLayout

open Browser

[<RequireQualifiedAccess>]
type Mode =
    | Desktop
    | OnePane

[<RequireQualifiedAccess>]
type Pane =
    | Worktrees
    | Terminal
    | Canvas

type State =
    { Mode: Mode
      ActivePane: Pane
      PreferenceError: string option }

let empty =
    { Mode = Mode.Desktop
      ActivePane = Pane.Worktrees
      PreferenceError = None }

let isVisible pane desktopOpen state =
    match state.Mode with
    | Mode.Desktop -> desktopOpen
    | Mode.OnePane -> state.ActivePane = pane

let label = function
    | Pane.Worktrees -> "Worktrees"
    | Pane.Terminal -> "Terminal"
    | Pane.Canvas -> "Canvas"

let paneId = function
    | Pane.Worktrees -> "workspace-worktrees"
    | Pane.Terminal -> "workspace-terminal"
    | Pane.Canvas -> "workspace-canvas"

let tabId pane = $"{paneId pane}-tab"

let navigate key pane =
    match key, pane with
    | "Home", _
    | "ArrowRight", Pane.Canvas
    | "ArrowLeft", Pane.Terminal -> Some Pane.Worktrees
    | "ArrowRight", Pane.Worktrees
    | "ArrowLeft", Pane.Canvas -> Some Pane.Terminal
    | "End", _
    | "ArrowRight", Pane.Terminal
    | "ArrowLeft", Pane.Worktrees -> Some Pane.Canvas
    | _ -> None

let focusTab pane =
    Dom.document.getElementById(tabId pane)
    |> Option.ofObj
    |> Option.iter _.focus()

let private preferenceKey = "treemon.workspace.mode"

let readMode () =
    match Dom.window.sessionStorage.getItem preferenceKey |> Option.ofObj with
    | None
    | Some "desktop" -> Mode.Desktop
    | Some "one-pane" -> Mode.OnePane
    | Some _ -> invalidOp "The saved workspace layout is not recognized."

let saveMode mode =
    let value =
        match mode with
        | Mode.Desktop -> "desktop"
        | Mode.OnePane -> "one-pane"
    Dom.window.sessionStorage.setItem (preferenceKey, value)
