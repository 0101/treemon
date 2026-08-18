module TerminalPane

open System
open Feliz
open Shared

let private isPath path (tab: EmbeddedTerminalTab) =
    Shared.PathUtils.pathEquals
        (WorktreePath.value path)
        (WorktreePath.value tab.Worktree)

let tryFindTab path snapshot =
    snapshot.Tabs |> List.tryFind (isPath path)

let private upsert lifecycle path snapshot =
    match tryFindTab path snapshot with
    | Some _ ->
        { Tabs =
            snapshot.Tabs
            |> List.map (fun tab ->
                if isPath path tab then
                    { tab with Lifecycle = lifecycle }
                else
                    tab) }
    | None ->
        { Tabs =
            snapshot.Tabs
            @ [ { Worktree = path
                  Lifecycle = lifecycle } ] }

let snapshotWhenOpened path snapshot =
    match tryFindTab path snapshot |> Option.map _.Lifecycle with
    | Some EmbeddedTerminalLifecycle.Starting
    | Some (EmbeddedTerminalLifecycle.Running _) ->
        snapshot
    | Some (EmbeddedTerminalLifecycle.Failed _)
    | None ->
        upsert EmbeddedTerminalLifecycle.Starting path snapshot

let snapshotWithFailure path error snapshot =
    upsert (EmbeddedTerminalLifecycle.Failed error) path snapshot

let activePath current snapshot =
    current
    |> Option.filter (fun path -> tryFindTab path snapshot |> Option.isSome)
    |> Option.orElseWith (fun () -> snapshot.Tabs |> List.tryHead |> Option.map _.Worktree)

let activeTab current snapshot =
    current |> Option.bind (fun path -> tryFindTab path snapshot)

let nextActiveAfterClose closed before snapshot =
    let closedIndex =
        before.Tabs
        |> List.tryFindIndex (isPath closed)
        |> Option.defaultValue 0

    snapshot.Tabs
    |> List.tryItem closedIndex
    |> Option.orElseWith (fun () -> snapshot.Tabs |> List.tryLast)
    |> Option.map _.Worktree

let paneOpenForSnapshot snapshot =
    snapshot.Tabs |> List.isEmpty |> not

let hasLiveTabs snapshot =
    snapshot.Tabs
    |> List.exists (fun tab ->
        match tab.Lifecycle with
        | EmbeddedTerminalLifecycle.Starting
        | EmbeddedTerminalLifecycle.Running _ ->
            true
        | EmbeddedTerminalLifecycle.Failed _ ->
            false)

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

let view tab close =
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

    match tab with
    | None ->
        Html.div [
            prop.className "terminal-pane"
            prop.hidden true
        ]
    | Some { Worktree = path; Lifecycle = EmbeddedTerminalLifecycle.Starting } ->
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
    | Some
        { Worktree = path
          Lifecycle = EmbeddedTerminalLifecycle.Running endpoint } ->
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
    | Some
        { Worktree = path
          Lifecycle = EmbeddedTerminalLifecycle.Failed error } ->
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
