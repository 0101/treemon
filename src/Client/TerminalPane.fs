module TerminalPane

open System
open Browser.Types
open Feliz
open Shared
open Navigation

[<RequireQualifiedAccess>]
type TerminalStartState =
    | Starting
    | Failed of error: string

type TerminalPaneState =
    { IsOpen: bool
      Snapshot: EmbeddedTerminalSnapshot
      ActiveTerminal: EmbeddedTerminalId option
      SelectedWorktree: WorktreePath option
      StartState: TerminalStartState option }

type TerminalPaneCallbacks =
    { SelectTab: EmbeddedTerminalId -> unit
      CloseTab: EmbeddedTerminalId -> unit
      StartTerminal: WorktreePath -> unit }

let private samePath left right =
    Shared.PathUtils.pathEquals
        (WorktreePath.value left)
        (WorktreePath.value right)

let private tryPathValue path entries =
    entries
    |> Map.toList
    |> List.tryPick (fun (candidate, value) ->
        if samePath path candidate then Some value else None)

let private removePath path entries =
    entries
    |> Map.filter (fun candidate _ ->
        not (samePath path candidate))

let private setPathValue path value entries =
    entries
    |> removePath path
    |> Map.add path value

let tabsForWorktree path snapshot =
    snapshot.Tabs
    |> List.filter (fun tab -> samePath path tab.Worktree)

let tryFindTab terminalId snapshot =
    snapshot.Tabs
    |> List.tryFind (fun tab -> tab.Id = terminalId)

let activeTerminalId selectedWorktree selections snapshot =
    selectedWorktree
    |> Option.bind (fun path ->
        let tabs = tabsForWorktree path snapshot

        tryPathValue path selections
        |> Option.bind (fun terminalId ->
            tabs
            |> List.tryFind (fun tab -> tab.Id = terminalId)
            |> Option.map _.Id)
        |> Option.orElseWith (fun () ->
            tabs |> List.tryHead |> Option.map _.Id))

let selectTerminal terminalId snapshot selections =
    tryFindTab terminalId snapshot
    |> Option.map (fun tab ->
        setPathValue tab.Worktree terminalId selections)
    |> Option.defaultValue selections

let private replacementSelection path terminalId before after =
    let afterTabs = tabsForWorktree path after

    afterTabs
    |> List.tryFind (fun tab -> tab.Id = terminalId)
    |> Option.map _.Id
    |> Option.orElseWith (fun () ->
        let previousIndex =
            before
            |> tabsForWorktree path
            |> List.tryFindIndex (fun tab -> tab.Id = terminalId)

        previousIndex
        |> Option.bind (fun index ->
            afterTabs
            |> List.tryItem index
            |> Option.orElseWith (fun () -> afterTabs |> List.tryLast))
        |> Option.orElseWith (fun () -> afterTabs |> List.tryHead)
        |> Option.map _.Id)

let reconcileSelections before after selections =
    selections
    |> Map.toList
    |> List.choose (fun (path, terminalId) ->
        replacementSelection path terminalId before after
        |> Option.map (fun replacement -> path, replacement))
    |> Map.ofList

let tryStartState path states =
    tryPathValue path states

let setStartState path state states =
    setPathValue path state states

let clearStartState path states =
    removePath path states

let isStarting path states =
    match tryStartState path states with
    | Some TerminalStartState.Starting -> true
    | Some (TerminalStartState.Failed _)
    | None -> false

let selectedWorktree targetWorktree focusedElement =
    targetWorktree
    |> Option.orElseWith (fun () ->
        match focusedElement with
        | Some (Card scopedKey) -> Some (WorktreePath scopedKey)
        | _ -> None)

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
        | true, port when port > 0 && port <= 65535 && port <> 5000 ->
            Some endpoint
        | _ -> None

let private lifecyclePresentation lifecycle =
    match lifecycle with
    | EmbeddedTerminalLifecycle.Running _ -> "running", "Running", "●"
    | EmbeddedTerminalLifecycle.Interrupted _ ->
        "failed", "Interrupted", "!"

let tabLabel index tab =
    tab.ReportedActivity
    |> Option.map _.Trim()
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.defaultValue $"Terminal {index + 1}"

let private terminalTab callbacks activeTerminal index tab =
    let terminalId = tab.Id
    let worktreeName = WorktreePath.displayName tab.Worktree
    let label = tabLabel index tab
    let isActive = activeTerminal = Some terminalId
    let lifecycleClass, lifecycleLabel, lifecycleGlyph =
        lifecyclePresentation tab.Lifecycle

    Html.div [
        prop.key (EmbeddedTerminalId.value terminalId)
        prop.className (
            [ "terminal-tab"
              lifecycleClass
              if isActive then "selected" ]
            |> String.concat " ")
        prop.role "tab"
        prop.ariaSelected isActive
        prop.ariaLabel $"{label} for {worktreeName}, {lifecycleLabel}"
        prop.tabIndex (if isActive then 0 else -1)
        prop.title $"{label} for {worktreeName} — {lifecycleLabel}"
        prop.onClick (fun _ -> callbacks.SelectTab terminalId)
        prop.onKeyDown (fun e ->
            if e.key = "Enter" || e.key = " " then
                e.preventDefault ()
                callbacks.SelectTab terminalId)
        prop.children [
            Html.span [
                prop.className "terminal-tab-state"
                prop.ariaHidden true
                prop.text lifecycleGlyph
            ]
            Html.span [
                prop.className "terminal-tab-label"
                prop.text label
            ]
            Html.button [
                prop.className "terminal-tab-close"
                prop.ariaLabel $"Close {label} for {worktreeName}"
                prop.title "Close this terminal"
                prop.onKeyDown (fun e ->
                    if e.key = "Enter" || e.key = " " then
                        e.stopPropagation ())
                prop.onClick (fun e ->
                    e.stopPropagation ()
                    callbacks.CloseTab terminalId)
                prop.text "×"
            ]
        ]
    ]

let private tryNextTabIndex key current count =
    match key with
    | "Home" -> Some 0
    | "End" -> Some (count - 1)
    | "ArrowRight" -> Some ((current + 1) % count)
    | "ArrowLeft" -> Some ((current - 1 + count) % count)
    | _ -> None

let private navigateTabs (e: KeyboardEvent) =
    match e.key with
    | "ArrowLeft"
    | "ArrowRight"
    | "Home"
    | "End" ->
        e.preventDefault ()
        let tabList = e.currentTarget :?> Element
        let target = e.target :?> Element
        let tabs =
            tabList.querySelectorAll(":scope > [role=\"tab\"]")

        target.closest("[role=\"tab\"]")
        |> Option.bind (fun current ->
            [ 0 .. tabs.length - 1 ]
            |> List.tryFind (fun index ->
                tabs[index].isSameNode current))
        |> Option.bind (fun current ->
            tryNextTabIndex e.key current tabs.length)
        |> Option.iter (fun next ->
            let tab = tabs[next] :?> HTMLElement
            tab.focus ()
            tab.click ())
    | _ -> ()

let private header state callbacks =
    let visibleTabs =
        state.SelectedWorktree
        |> Option.map (fun path ->
            state.Snapshot
            |> tabsForWorktree path
            |> List.mapi (terminalTab callbacks state.ActiveTerminal))
        |> Option.defaultValue []

    let newTerminalButton =
        match state.SelectedWorktree with
        | None -> Html.none
        | Some path ->
            let starting =
                state.StartState = Some TerminalStartState.Starting

            Html.button [
                prop.className "ctrl-btn terminal-new-btn"
                prop.disabled starting
                prop.ariaLabel $"Start another terminal for {WorktreePath.displayName path}"
                prop.title (
                    if starting then
                        "A terminal is already starting"
                    else
                        "Start another terminal for this worktree")
                prop.onClick (fun _ -> callbacks.StartTerminal path)
                prop.text (if starting then "Starting…" else "New")
            ]

    Html.div [
        prop.className "terminal-pane-header"
        prop.children [
            Html.div [
                prop.className "terminal-tabs"
                prop.role "tablist"
                prop.ariaLabel "Terminals for the selected worktree"
                prop.onKeyDown navigateTabs
                prop.children visibleTabs
            ]
            Html.div [
                prop.className "terminal-pane-actions"
                prop.children [ newTerminalButton ]
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

let private startFeedback state callbacks =
    match state.SelectedWorktree, state.StartState with
    | Some _, Some TerminalStartState.Starting
        when state.ActiveTerminal.IsNone ->
        Html.div [
            prop.className "terminal-pane-status"
            prop.text "Starting embedded terminal…"
        ]
    | Some path, Some (TerminalStartState.Failed error) ->
        Html.div [
            prop.className "terminal-pane-error terminal-pane-message"
            prop.children [
                Html.span [ prop.text error ]
                terminalAction "Try again" path callbacks.StartTerminal
            ]
        ]
    | _ -> Html.none

let private activeStatus state callbacks =
    match
        state.ActiveTerminal
        |> Option.bind (fun terminalId ->
            tryFindTab terminalId state.Snapshot),
        state.SelectedWorktree
    with
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
          Lifecycle = EmbeddedTerminalLifecycle.Interrupted error }, _ ->
        Html.div [
            prop.className "terminal-pane-error terminal-pane-message"
            prop.children [
                Html.span [ prop.text error ]
                terminalAction "Start another terminal" path callbacks.StartTerminal
            ]
        ]
    | None, Some _ when state.StartState.IsSome ->
        Html.none
    | None, Some path ->
        Html.div [
            prop.className "terminal-pane-empty terminal-pane-message"
            prop.children [
                Html.span [
                    prop.className "terminal-pane-empty-title"
                    prop.text $"No embedded terminals for {WorktreePath.displayName path}."
                ]
                terminalAction "Start terminal" path callbacks.StartTerminal
            ]
        ]
    | None, None ->
        Html.div [
            prop.className "terminal-pane-empty"
            prop.text "Select a worktree to view its terminals."
        ]

let private runningIframes state =
    state.Snapshot.Tabs
    |> List.choose (fun tab ->
        match tab.Lifecycle with
        | EmbeddedTerminalLifecycle.Running endpoint ->
            safeEndpoint endpoint
            |> Option.map (fun src ->
                let terminalId = tab.Id
                let isActive =
                    state.ActiveTerminal = Some terminalId

                let terminalIndex =
                    state.Snapshot
                    |> tabsForWorktree tab.Worktree
                    |> List.tryFindIndex (fun candidate ->
                        candidate.Id = terminalId)
                    |> Option.defaultValue 0

                let label = tabLabel terminalIndex tab

                Html.iframe [
                    prop.key (EmbeddedTerminalId.value terminalId)
                    prop.className (
                        if isActive then
                            "terminal-iframe terminal-iframe-active"
                        else
                            "terminal-iframe")
                    prop.hidden (not isActive)
                    prop.title $"{label} for {WorktreePath.displayName tab.Worktree}"
                    prop.src src
                    prop.custom ("data-terminal-id", EmbeddedTerminalId.value terminalId)
                    prop.custom ("data-terminal-worktree", WorktreePath.value tab.Worktree)
                    prop.custom ("sandbox", "allow-scripts allow-same-origin")
                    prop.custom ("referrerpolicy", "no-referrer")
                    prop.custom ("scrolling", "no")
                ])
        | EmbeddedTerminalLifecycle.Interrupted _ ->
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
                            startFeedback state callbacks
                            activeStatus state callbacks
                            yield! runningIframes state
                        ]
                    ]
                ]
            ]
        ]
    ]
