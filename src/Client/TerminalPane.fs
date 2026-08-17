module TerminalPane

open System
open Feliz
open Shared

let stateWhenOpened path state =
    match state with
    | EmbeddedTerminalState.Starting _
    | EmbeddedTerminalState.Running _ -> state
    | EmbeddedTerminalState.Closed
    | EmbeddedTerminalState.Failed _ -> EmbeddedTerminalState.Starting path

let paneOpenForState state =
    match state with
    | EmbeddedTerminalState.Closed -> false
    | EmbeddedTerminalState.Starting _
    | EmbeddedTerminalState.Running _
    | EmbeddedTerminalState.Failed _ -> true

let safeEndpoint (endpoint: string) =
    let prefix = "http://127.0.0.1:"

    if not (endpoint.StartsWith(prefix, StringComparison.Ordinal)) then
        None
    else
        let authorityAndPath = endpoint.Substring(prefix.Length)
        let separator = authorityAndPath.IndexOf('/')
        let portText =
            if separator < 0 then authorityAndPath
            else authorityAndPath.Substring(0, separator)

        match Int32.TryParse portText with
        | true, port when port > 0 && port <= 65535 && port <> 5000 -> Some endpoint
        | _ -> None

let private closeButton close =
    Html.button [
        prop.className "terminal-close-btn"
        prop.title "Close embedded terminal"
        prop.onClick (fun _ -> close ())
        prop.text "×"
    ]

let view state close =
    let paneContent path body =
        Html.div [
            prop.className "terminal-pane-shell"
            prop.children [
                Html.div [
                    prop.className "terminal-pane-header"
                    prop.children [
                        Html.span [
                            prop.className "terminal-pane-title"
                            prop.text (WorktreePath.displayName path)
                        ]
                        closeButton close
                    ]
                ]
                body
            ]
        ]

    match state with
    | EmbeddedTerminalState.Closed ->
        Html.div [
            prop.className "terminal-pane"
            prop.hidden true
        ]
    | EmbeddedTerminalState.Starting path ->
        Html.div [
            prop.className "terminal-pane open"
            prop.children [
                paneContent path (
                    Html.div [
                        prop.className "terminal-pane-status"
                        prop.text "Starting embedded terminal…"
                    ])
            ]
        ]
    | EmbeddedTerminalState.Running(path, endpoint) ->
        Html.div [
            prop.className "terminal-pane open"
            prop.children [
                paneContent path (
                    match safeEndpoint endpoint with
                    | Some src ->
                        Html.iframe [
                            prop.className "terminal-iframe"
                            prop.title $"Embedded terminal for {WorktreePath.displayName path}"
                            prop.src src
                            prop.custom ("sandbox", "allow-scripts allow-same-origin")
                            prop.custom ("referrerpolicy", "no-referrer")
                        ]
                    | None ->
                        Html.div [
                            prop.className "terminal-pane-error"
                            prop.text "The terminal server returned an unsafe endpoint. Close the pane and try again."
                        ])
            ]
        ]
    | EmbeddedTerminalState.Failed(path, error) ->
        Html.div [
            prop.className "terminal-pane open"
            prop.children [
                paneContent path (
                    Html.div [
                        prop.className "terminal-pane-error"
                        prop.text error
                    ])
            ]
        ]
