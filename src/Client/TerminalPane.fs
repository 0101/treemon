module TerminalPane

open System
open Feliz
open Shared

type TerminalPaneState =
    { IsOpen: bool
      Snapshot: EmbeddedTerminalSnapshot
      ActiveWorktree: WorktreePath option
      SelectedWorktree: WorktreePath option }

type TerminalPaneCallbacks =
    { SelectTab: WorktreePath -> unit
      CloseTab: WorktreePath -> unit
      StartTerminal: WorktreePath -> unit
      HidePane: unit -> unit }

let private samePath left right =
    Shared.PathUtils.pathEquals
        (WorktreePath.value left)
        (WorktreePath.value right)

let private isPath path (tab: EmbeddedTerminalTab) =
    samePath path tab.Worktree

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

let activeAfterSnapshot preferred current before snapshot =
    match current, before.Tabs with
    | Some path, _ -> activePath (Some path) snapshot
    | None, [] ->
        preferred
        |> Option.bind (fun path ->
            tryFindTab path snapshot
            |> Option.map _.Worktree)
        |> Option.orElseWith (fun () -> activePath None snapshot)
    | None, _ -> None

let projectWorktreeSelection paneOpen selected current snapshot =
    if paneOpen then
        selected
        |> Option.bind (fun path ->
            tryFindTab path snapshot
            |> Option.map _.Worktree)
    else
        current

let nextActiveAfterClose closed before snapshot =
    let closedIndex =
        before.Tabs
        |> List.tryFindIndex (isPath closed)
        |> Option.defaultValue 0

    snapshot.Tabs
    |> List.tryItem closedIndex
    |> Option.orElseWith (fun () -> snapshot.Tabs |> List.tryLast)
    |> Option.map _.Worktree

let activeAfterClose current closed before snapshot =
    match current with
    | Some path
        when samePath path closed
             && tryFindTab closed snapshot |> Option.isSome ->
        tryFindTab closed snapshot
        |> Option.map _.Worktree
    | Some path when samePath path closed ->
        nextActiveAfterClose closed before snapshot
    | Some path ->
        activePath (Some path) snapshot
    | None ->
        None

let paneOpenForSnapshot snapshot =
    snapshot.Tabs |> List.isEmpty |> not

let isOpen desiredOpen snapshot =
    desiredOpen && paneOpenForSnapshot snapshot

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

let private lifecyclePresentation lifecycle =
    match lifecycle with
    | EmbeddedTerminalLifecycle.Starting -> "starting", "Starting", "…"
    | EmbeddedTerminalLifecycle.Running _ -> "running", "Running", "●"
    | EmbeddedTerminalLifecycle.Failed _ -> "failed", "Failed", "!"

let private terminalTab (callbacks: TerminalPaneCallbacks) activeWorktree (tab: EmbeddedTerminalTab) =
    let path = tab.Worktree
    let name = WorktreePath.displayName path
    let isActive = activeWorktree |> Option.exists (samePath path)
    let lifecycleClass, lifecycleLabel, lifecycleGlyph =
        lifecyclePresentation tab.Lifecycle

    Html.div [
        prop.key (WorktreePath.value path)
        prop.className (
            [ "terminal-tab"
              lifecycleClass
              if isActive then "selected" ]
            |> String.concat " ")
        prop.role "tab"
        prop.ariaSelected isActive
        prop.ariaLabel $"{name}, {lifecycleLabel}"
        prop.tabIndex (if isActive then 0 else -1)
        prop.title $"{name} — {lifecycleLabel}"
        prop.onClick (fun _ -> callbacks.SelectTab path)
        prop.onKeyDown (fun e ->
            if e.key = "Enter" || e.key = " " then
                e.preventDefault ()
                callbacks.SelectTab path)
        prop.children [
            Html.span [
                prop.className "terminal-tab-state"
                prop.ariaHidden true
                prop.text lifecycleGlyph
            ]
            Html.span [
                prop.className "terminal-tab-label"
                prop.text name
            ]
            Html.button [
                prop.className "terminal-tab-close"
                prop.ariaLabel $"Close {name} terminal"
                prop.title "Close this terminal"
                prop.onKeyDown (fun e ->
                    if e.key = "Enter" || e.key = " " then
                        e.stopPropagation ())
                prop.onClick (fun e ->
                    e.stopPropagation ()
                    callbacks.CloseTab path)
                prop.text "×"
            ]
        ]
    ]

let private header state callbacks =
    Html.div [
        prop.className "terminal-pane-header"
        prop.children [
            Html.div [
                prop.className "terminal-tabs"
                prop.role "tablist"
                prop.ariaLabel "Worktree terminals"
                prop.onKeyDown (fun e ->
                    if e.key = "ArrowLeft"
                       || e.key = "ArrowRight"
                       || e.key = "Home"
                       || e.key = "End" then
                        e.preventDefault ()
                        Fable.Core.JsInterop.emitJsExpr
                            (e.currentTarget, e.target, e.key)
                            """(function(list,target,key){
                                const tabs = Array.from(list.querySelectorAll(':scope > [role="tab"]'));
                                const current = tabs.indexOf(target.closest('[role="tab"]'));
                                if (tabs.length === 0 || current < 0) return;
                                const next = key === 'Home' ? 0
                                    : key === 'End' ? tabs.length - 1
                                    : key === 'ArrowRight' ? (current + 1) % tabs.length
                                    : (current - 1 + tabs.length) % tabs.length;
                                tabs[next].focus();
                                tabs[next].click();
                            })($0,$1,$2)""")
                prop.children (
                    state.Snapshot.Tabs
                    |> List.map (terminalTab callbacks state.ActiveWorktree))
            ]
            Html.div [
                prop.className "terminal-pane-actions"
                prop.children [
                    Html.button [
                        prop.className "ctrl-btn terminal-hide-btn"
                        prop.ariaLabel "Hide terminal pane"
                        prop.title "Hide terminal pane"
                        prop.onClick (fun _ -> callbacks.HidePane ())
                        prop.text "Hide"
                    ]
                ]
            ]
        ]
    ]

let private terminalAction
    (label: string)
    (path: WorktreePath)
    (startTerminal: WorktreePath -> unit)
    =
    Html.button [
        prop.className "ctrl-btn terminal-start-btn"
        prop.onClick (fun _ -> startTerminal path)
        prop.text label
    ]

let private activeStatus state callbacks =
    match activeTab state.ActiveWorktree state.Snapshot, state.SelectedWorktree with
    | Some { Lifecycle = EmbeddedTerminalLifecycle.Starting }, _ ->
        Html.div [
            prop.className "terminal-pane-status"
            prop.text "Starting embedded terminal…"
        ]
    | Some
        { Lifecycle = EmbeddedTerminalLifecycle.Running endpoint }, _
        when safeEndpoint endpoint |> Option.isNone ->
        Html.div [
            prop.className "terminal-pane-error"
            prop.text "The terminal server returned an unsafe endpoint. Close the tab and try again."
        ]
    | Some { Lifecycle = EmbeddedTerminalLifecycle.Running _ }, _ ->
        Html.none
    | Some
        { Worktree = path
          Lifecycle = EmbeddedTerminalLifecycle.Failed error }, _ ->
        Html.div [
            prop.className "terminal-pane-error terminal-pane-message"
            prop.children [
                Html.span [ prop.text error ]
                terminalAction "Restart terminal" path callbacks.StartTerminal
            ]
        ]
    | None, Some path ->
        Html.div [
            prop.className "terminal-pane-empty terminal-pane-message"
            prop.children [
                Html.span [
                    prop.className "terminal-pane-empty-title"
                    prop.text $"No embedded terminal for {WorktreePath.displayName path}."
                ]
                terminalAction "Start terminal" path callbacks.StartTerminal
            ]
        ]
    | None, None ->
        Html.div [
            prop.className "terminal-pane-empty"
            prop.text "Select a worktree or terminal tab."
        ]

let private runningIframes state =
    state.Snapshot.Tabs
    |> List.choose (fun tab ->
        match tab.Lifecycle with
        | EmbeddedTerminalLifecycle.Running endpoint ->
            safeEndpoint endpoint
            |> Option.map (fun src ->
                let path = tab.Worktree
                let isActive =
                    state.ActiveWorktree
                    |> Option.exists (samePath path)

                Html.iframe [
                    prop.key (WorktreePath.value path)
                    prop.className (
                        if isActive then
                            "terminal-iframe terminal-iframe-active"
                        else
                            "terminal-iframe")
                    prop.hidden (not isActive)
                    prop.title $"Embedded terminal for {WorktreePath.displayName path}"
                    prop.src src
                    prop.custom ("data-terminal-worktree", WorktreePath.value path)
                    prop.custom ("sandbox", "allow-scripts allow-same-origin")
                    prop.custom ("referrerpolicy", "no-referrer")
                    prop.custom ("scrolling", "no")
                ])
        | EmbeddedTerminalLifecycle.Starting
        | EmbeddedTerminalLifecycle.Failed _ ->
            None)

let view state callbacks =
    let paneClass =
        if state.IsOpen then
            "terminal-pane open"
        else
            "terminal-pane"

    Html.div [
        prop.className paneClass
        prop.hidden (not state.IsOpen)
        prop.role "region"
        prop.ariaLabel "Embedded terminals"
        prop.children [
            Html.div [
                prop.className "terminal-pane-shell"
                prop.children [
                    header state callbacks
                    Html.div [
                        prop.className "terminal-pane-body"
                        prop.children [
                            activeStatus state callbacks
                            yield! runningIframes state
                        ]
                    ]
                ]
            ]
        ]
    ]
