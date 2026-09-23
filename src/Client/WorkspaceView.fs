module WorkspaceView

open Feliz
open AppTypes
open WorkspaceLayout

let tabs model dispatch =
    Html.div [
        prop.className "workspace-tabs"
        prop.role "tablist"
        prop.ariaLabel "Workspace panes"
        prop.onKeyDown (fun event ->
            match navigate event.key model.Workspace.ActivePane with
            | Some pane ->
                event.preventDefault()
                dispatch (SelectWorkspacePane pane)
            | None -> ())
        prop.children (
            panes
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
