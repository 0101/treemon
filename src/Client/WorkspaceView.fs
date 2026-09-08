module WorkspaceView

open Feliz
open Shared
open AppTypes
open WorkspaceLayout

let context model =
    let path =
        match model.Workspace.ActivePane with
        | Pane.Terminal ->
            TerminalPane.selectedWorktree model.TerminalPaneTarget model.FocusedElement
            |> Option.map WorktreePath.value
        | Pane.Canvas ->
            CanvasState.activeCanvasWorktree model.FocusedElement model.Canvas.TargetWorktree
        | Pane.Worktrees ->
            match model.FocusedElement with
            | Some (Navigation.Card key) -> Some key
            | _ -> None
    let title =
        path
        |> Option.bind (fun key ->
            model.Repos
            |> List.tryPick (fun repo ->
                repo.Worktrees
                |> List.tryFind (fun worktree -> WorktreePath.value worktree.Path = key)
                |> Option.map (fun worktree -> $"{repo.Name} / {Components.cardTitle worktree}")))
        |> Option.defaultValue (label model.Workspace.ActivePane)
    Html.span [
        prop.className "workspace-context"
        prop.title (path |> Option.defaultValue title)
        prop.text title
    ]

let modeButton model dispatch =
    let onePane = model.Workspace.Mode = Mode.OnePane
    Html.button [
        prop.className "ctrl-btn workspace-mode-toggle"
        prop.ariaLabel (if onePane then "Use desktop layout" else "Use one-pane layout")
        prop.title (if onePane then "Restore the desktop layout" else "Show one workspace pane at a time")
        prop.text (if onePane then "Desktop" else "One pane")
        prop.onClick (fun _ -> dispatch (SetWorkspaceMode (if onePane then Mode.Desktop else Mode.OnePane)))
    ]

let tabs model dispatch =
    Html.div [
        prop.className "workspace-tabs"
        prop.hidden (model.Workspace.Mode = Mode.Desktop)
        prop.role "tablist"
        prop.ariaLabel "Workspace panes"
        prop.onKeyDown (fun event ->
            match navigate event.key model.Workspace.ActivePane with
            | Some pane ->
                event.preventDefault()
                dispatch (SelectWorkspacePane pane)
            | None -> ())
        prop.children (
            [ Pane.Worktrees; Pane.Terminal; Pane.Canvas ]
            |> List.map (fun pane ->
                let selected = model.Workspace.ActivePane = pane
                Html.button [
                    prop.id (tabId pane)
                    prop.className (if selected then "ctrl-btn workspace-tab active" else "ctrl-btn workspace-tab")
                    prop.role "tab"
                    prop.ariaSelected selected
                    prop.ariaControls (paneId pane)
                    prop.tabIndex (if selected then 0 else -1)
                    prop.onClick (fun _ -> dispatch (SelectWorkspacePane pane))
                    prop.children [
                        Html.text (label pane)
                        if pane = Pane.Canvas then
                            let count =
                                CanvasAwareness.unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes
                                |> Map.values
                                |> Seq.sumBy List.length
                            if count > 0 then
                                Html.span [
                                    prop.className "canvas-badge"
                                    prop.ariaLabel $"{count} unread canvas documents"
                                    prop.text (string count)
                                ]
                    ]
                ]))
    ]
