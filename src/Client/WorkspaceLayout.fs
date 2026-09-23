module WorkspaceLayout

open Browser
open BrowserObserverInterop

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
      ActivePane: Pane }

let empty =
    { Mode = Mode.Desktop
      ActivePane = Pane.Worktrees }

let panes =
    [ Pane.Worktrees
      Pane.Terminal
      Pane.Canvas ]

[<Literal>]
let PhoneMaxWidth = 900

let modeForViewportWidth width =
    if width <= float PhoneMaxWidth then Mode.OnePane
    else Mode.Desktop

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

let focusedPane () =
    Dom.document.activeElement
    |> Option.ofObj
    |> Option.bind (fun focused ->
        panes
        |> List.tryFind (fun pane ->
            Dom.document.getElementById(paneId pane)
            |> Option.ofObj
            |> Option.exists (fun element -> element.contains(focused))))

let observeMode dispatch =
    let media = matchMedia $"(max-width: {PhoneMaxWidth}px)"
    let report () =
        viewportWidth ()
        |> modeForViewportWidth
        |> dispatch
    let handler = fun (_: obj) -> report ()

    addMediaChangeListener media handler
    report ()

    { new System.IDisposable with
        member _.Dispose() =
            removeMediaChangeListener media handler }
