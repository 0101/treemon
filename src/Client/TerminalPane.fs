module TerminalPane

open System
open Browser
open Browser.Types
open Feliz
open Fable.Core.JsInterop
open Shared
open Navigation

[<RequireQualifiedAccess>]
type TerminalStartState =
    | Starting
    | StartingAndFocus
    | StartingWithQueuedAgents of focusOnCompletion: bool * queuedCount: int
    | Failed of error: string

type TerminalViewState =
    { Generation: int
      FocusAfterLoad: bool }

[<RequireQualifiedAccess>]
type TerminalVisibilitySignal =
    | Activate
    | Loaded
    | Deactivate

type TerminalPaneState =
    { IsOpen: bool
      Snapshot: EmbeddedTerminalSnapshot
      ActiveTerminal: EmbeddedTerminalId option
      SelectedWorktree: WorktreePath option
      StartState: TerminalStartState option
      ViewStates: Map<EmbeddedTerminalId, TerminalViewState> }

type TerminalPaneCallbacks =
    { SelectTab: EmbeddedTerminalId -> unit
      CloseTab: EmbeddedTerminalId -> unit
      StartTerminal: WorktreePath -> unit
      ReconnectView: EmbeddedTerminalId -> unit
      ViewLoaded: EmbeddedTerminalId -> int -> unit }

[<RequireQualifiedAccess>]
type CycleDirection =
    | Next
    | Previous

[<RequireQualifiedAccess>]
type TerminalShortcut =
    | OpenWorktreeSearch of EmbeddedTerminalId
    | CycleTerminal of EmbeddedTerminalId * CycleDirection
    | CloseTerminal of EmbeddedTerminalId
    | StartTerminal of EmbeddedTerminalId

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

let withoutTerminals (terminalIds: Set<EmbeddedTerminalId>) snapshot =
    { snapshot with
        Tabs =
            snapshot.Tabs
            |> List.filter (fun tab ->
                not (terminalIds.Contains tab.Id)) }

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

let cycleTerminal direction selectedWorktree snapshot selections =
    selectedWorktree
    |> Option.bind (fun path ->
        let tabs = tabsForWorktree path snapshot

        if tabs.Length < 2 then
            None
        else
            let currentIndex =
                activeTerminalId (Some path) selections snapshot
                |> Option.bind (fun terminalId ->
                    tabs |> List.tryFindIndex (fun tab -> tab.Id = terminalId))
                |> Option.defaultValue 0

            let offset =
                match direction with
                | CycleDirection.Next -> 1
                | CycleDirection.Previous -> -1

            let nextIndex = (currentIndex + offset + tabs.Length) % tabs.Length
            Some(selectTerminal tabs[nextIndex].Id snapshot selections))
    |> Option.defaultValue selections

let cycleTerminalFrom terminalId direction snapshot selections =
    tryFindTab terminalId snapshot
    |> Option.bind (fun tab ->
        if
            activeTerminalId
                (Some tab.Worktree)
                selections
                snapshot
            = Some terminalId
        then
            let updated =
                cycleTerminal
                    direction
                    (Some tab.Worktree)
                    snapshot
                    selections

            Some(
                updated,
                activeTerminalId
                    (Some tab.Worktree)
                    updated
                    snapshot
            )
        else
            None)

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

let setStarting path focusOnCompletion states =
    let state =
        match tryStartState path states with
        | Some (TerminalStartState.StartingWithQueuedAgents(_, count)) ->
            TerminalStartState.StartingWithQueuedAgents(focusOnCompletion, count)
        | _ when focusOnCompletion ->
            TerminalStartState.StartingAndFocus
        | _ ->
            TerminalStartState.Starting

    setStartState path state states

let tryQueueAgentStart path states =
    let queued =
        match tryStartState path states with
        | Some TerminalStartState.Starting ->
            Some(TerminalStartState.StartingWithQueuedAgents(false, 1))
        | Some TerminalStartState.StartingAndFocus ->
            Some(TerminalStartState.StartingWithQueuedAgents(true, 1))
        | Some (TerminalStartState.StartingWithQueuedAgents(focus, count)) ->
            Some(TerminalStartState.StartingWithQueuedAgents(focus, count + 1))
        | Some (TerminalStartState.Failed _)
        | None -> None

    queued
    |> Option.map (fun state -> setStartState path state states)

let shouldFocusStartedTerminal path states =
    match tryStartState path states with
    | Some TerminalStartState.StartingAndFocus
    | Some (TerminalStartState.StartingWithQueuedAgents(true, _)) -> true
    | _ -> false

let tryStartQueuedAgent path states =
    match tryStartState path states with
    | Some (TerminalStartState.StartingWithQueuedAgents(_, count)) ->
        let next =
            if count > 1 then
                TerminalStartState.StartingWithQueuedAgents(true, count - 1)
            else
                TerminalStartState.StartingAndFocus

        states
        |> setStartState path next
        |> Some
    | _ -> None

let viewGeneration terminalId states =
    states
    |> Map.tryFind terminalId
    |> Option.map _.Generation
    |> Option.defaultValue 0

let reconnectView terminalId states =
    let generation = viewGeneration terminalId states + 1

    states
    |> Map.add
        terminalId
        { Generation = generation
          FocusAfterLoad = true }

let completeViewLoad terminalId generation states =
    match states |> Map.tryFind terminalId with
    | Some state
        when state.Generation = generation
             && state.FocusAfterLoad ->
        let updated =
            states
            |> Map.add
                terminalId
                { state with FocusAfterLoad = false }

        updated, true
    | Some _
    | None -> states, false

let cancelViewFocus terminalId states =
    match states |> Map.tryFind terminalId with
    | Some state when state.FocusAfterLoad ->
        states
        |> Map.add
            terminalId
            { state with FocusAfterLoad = false }
    | Some _
    | None -> states

let cancelAllViewFocus states =
    states
    |> Map.map (fun _ state ->
        { state with FocusAfterLoad = false })

let cancelOtherViewFocus selectedTerminal states =
    states
    |> Map.map (fun terminalId state ->
        if terminalId = selectedTerminal then
            state
        else
            { state with FocusAfterLoad = false })

let private startInFlight state =
    match state with
    | TerminalStartState.Starting
    | TerminalStartState.StartingAndFocus
    | TerminalStartState.StartingWithQueuedAgents _ -> true
    | TerminalStartState.Failed _ -> false

let isStarting path states =
    tryStartState path states
    |> Option.exists startInFlight

let selectedWorktree targetWorktree focusedElement =
    targetWorktree
    |> Option.orElseWith (fun () ->
        match focusedElement with
        | Some (Card scopedKey) -> Some (WorktreePath scopedKey)
        | _ -> None)

let private trySafeEndpointOrigin (endpoint: string) =
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
            Some(prefix + portText)
        | _ -> None

let isSafeEndpoint endpoint =
    trySafeEndpointOrigin endpoint |> Option.isSome

let private trySafeRunningEndpoint tab =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Running endpoint ->
        endpoint
        |> trySafeEndpointOrigin
        |> Option.map (fun origin ->
            {| Endpoint = endpoint
               Origin = origin |})
    | EmbeddedTerminalLifecycle.Interrupted _ -> None

let visibleRunningTerminal isOpen activeTerminal snapshot =
    if not isOpen then
        None
    else
        activeTerminal
        |> Option.bind (fun terminalId ->
            snapshot
            |> tryFindTab terminalId
            |> Option.bind (fun tab ->
                tab
                |> trySafeRunningEndpoint
                |> Option.map (fun endpoint ->
                    terminalId, endpoint.Origin)))

let tryReconnectableTab activeTerminal snapshot =
    activeTerminal
    |> Option.bind (fun terminalId ->
        tryFindTab terminalId snapshot)
    |> Option.filter (trySafeRunningEndpoint >> Option.isSome)

let reconcileViewStates snapshot states =
    let reconnectableIds =
        snapshot.Tabs
        |> List.choose (fun tab ->
            tab
            |> trySafeRunningEndpoint
            |> Option.map (fun _ -> tab.Id))
        |> Set.ofList

    states
    |> Map.filter (fun terminalId _ ->
        reconnectableIds.Contains terminalId)

let private terminalFrameId terminalId =
    $"terminal-iframe-{EmbeddedTerminalId.value terminalId}"

let private frameMatchesGeneration generation (frame: HTMLElement) =
    frame.getAttribute("data-terminal-view-generation")
    |> Option.ofObj
    |> Option.contains (string generation)

let private frameIsActiveAndVisible (frame: HTMLElement) =
    frame.classList.contains("terminal-iframe-active")
    && not (frame.hasAttribute("hidden"))
    && (frame.closest(".terminal-pane")
        |> Option.exists (fun pane ->
            not (pane.hasAttribute("hidden"))))

let private documentIsForeground () =
    emitJsExpr<bool> () "document.visibilityState==='visible'&&document.hasFocus()"

let private withTerminalFrame terminalId acceptsFrame action onMissing =
    let rec tryResolve remainingAttempts =
        Dom.window?requestAnimationFrame(fun (_: float) ->
            match
                Dom.document.getElementById(terminalFrameId terminalId)
                |> Option.ofObj
            with
            | Some frame when acceptsFrame frame -> action frame
            | _ when remainingAttempts > 1 ->
                tryResolve (remainingAttempts - 1)
            | _ -> onMissing ())
        |> ignore

    tryResolve 2

let private focusTerminalFrame frame =
    emitJsExpr<unit>
        (frame, TerminalPageMessage.FocusTerminal)
        "(function(f,a){f.focus();f.contentWindow.postMessage({action:a},new URL(f.src,document.baseURI).origin)})($0,$1)"

let focusTerminal terminalId =
    withTerminalFrame
        terminalId
        frameIsActiveAndVisible
        focusTerminalFrame
        ignore

let focusTerminalOrElse terminalId onMissing =
    withTerminalFrame
        terminalId
        frameIsActiveAndVisible
        focusTerminalFrame
        onMissing

let focusTerminalView terminalId generation =
    withTerminalFrame
        terminalId
        (fun frame ->
            frameIsActiveAndVisible frame
            && frameMatchesGeneration generation frame)
        focusTerminalFrame
        ignore

let focusTerminalWhenReady terminalId generation =
    withTerminalFrame
        terminalId
        (fun frame ->
            frameIsActiveAndVisible frame
            && frameMatchesGeneration generation frame)
        (fun frame ->
            let focusOnLoad (_: Event) =
                focusTerminalView terminalId generation

            frame?addEventListener(
                "load",
                focusOnLoad,
                createObj [ "once" ==> true ])

            Fable.Core.JS.setTimeout
                (fun () ->
                    frame?removeEventListener("load", focusOnLoad))
                10_000
            |> ignore

            focusTerminalView terminalId generation)
        ignore

let notifyTerminalVisibility terminalId origin signal =
    let active, loaded =
        match signal with
        | TerminalVisibilitySignal.Activate -> true, false
        | TerminalVisibilitySignal.Loaded -> true, true
        | TerminalVisibilitySignal.Deactivate -> false, false

    let rec tryNotify remainingAttempts =
        Dom.window?requestAnimationFrame(fun (_: float) ->
            let notified =
                Dom.document.getElementById(terminalFrameId terminalId)
                |> Option.ofObj
                |> Option.exists (fun frame ->
                    if
                        active
                        && (not (documentIsForeground ())
                            || not (frameIsActiveAndVisible frame))
                    then
                        false
                    else
                        Fable.Core.JsInterop.emitJsExpr<bool>
                            (frame, origin, TerminalPageMessage.TerminalVisible, active, loaded)
                            "(function(f,origin,action,active,loaded){if(!f.contentWindow)return false;f.contentWindow.postMessage({action:action,active:active,loaded:loaded},origin);return true})($0,$1,$2,$3,$4)")

            if not notified && remainingAttempts > 1 then
                tryNotify (remainingAttempts - 1))
        |> ignore

    tryNotify (if active then 3 else 2)

let observeVisibleTerminal terminalId notify =
    let loadHandler =
        fun (event: Event) ->
            let loadedFrameId =
                Fable.Core.JsInterop.emitJsExpr<string> event
                    "($0.target&&$0.target.id)||''"

            if loadedFrameId = terminalFrameId terminalId then
                notify TerminalVisibilitySignal.Loaded

    let visibilityHandler =
        fun (_: Event) ->
            if Fable.Core.JsInterop.emitJsExpr<bool> () "document.visibilityState==='visible'" then
                notify TerminalVisibilitySignal.Activate
            else
                notify TerminalVisibilitySignal.Deactivate

    let focusHandler =
        fun (_: Event) ->
            notify TerminalVisibilitySignal.Activate

    let blurHandler =
        fun (_: Event) ->
            notify TerminalVisibilitySignal.Deactivate

    notify TerminalVisibilitySignal.Activate

    Dom.document.addEventListener("load", loadHandler, true)
    Dom.document.addEventListener("visibilitychange", visibilityHandler)
    Dom.window.addEventListener("focus", focusHandler)
    Dom.window.addEventListener("blur", blurHandler)

    { new IDisposable with
        member _.Dispose() =
            Dom.document.removeEventListener("load", loadHandler, true)
            Dom.document.removeEventListener("visibilitychange", visibilityHandler)
            Dom.window.removeEventListener("focus", focusHandler)
            Dom.window.removeEventListener("blur", blurHandler)
            notify TerminalVisibilitySignal.Deactivate }

let messageListener (dispatch: TerminalShortcut -> unit) =
    let tryActiveTerminalId (message: MessageEvent) =
        let value =
            emitJsExpr<string>
                message
                "(function(f){return f&&f.contentWindow===$0.source&&new URL(f.src,document.baseURI).origin===$0.origin?(f.getAttribute('data-terminal-id')||''):''})(document.querySelector('.terminal-iframe-active'))"

        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(EmbeddedTerminalId value)

    let handler =
        fun (event: Event) ->
            let message = event :?> MessageEvent
            let isObject =
                Fable.Core.JsInterop.emitJsExpr<bool>
                    message.data
                    "$0 != null && typeof $0 === 'object'"

            match isObject, tryActiveTerminalId message with
            | true, Some terminalId ->
                let action =
                    emitJsExpr<string>
                        message.data
                        "typeof $0.action === 'string' ? $0.action : ''"

                match action with
                | TerminalPageMessage.OpenWorktreeSearch ->
                    dispatch (TerminalShortcut.OpenWorktreeSearch terminalId)
                | TerminalPageMessage.CloseTerminal ->
                    dispatch (TerminalShortcut.CloseTerminal terminalId)
                | TerminalPageMessage.StartTerminal ->
                    dispatch (TerminalShortcut.StartTerminal terminalId)
                | TerminalPageMessage.CycleTerminal ->
                    match
                        emitJsExpr<string>
                            message.data
                            "typeof $0.direction === 'string' ? $0.direction : ''"
                    with
                    | TerminalPageMessage.NextDirection ->
                        dispatch (
                            TerminalShortcut.CycleTerminal(
                                terminalId,
                                CycleDirection.Next
                            )
                        )
                    | TerminalPageMessage.PreviousDirection ->
                        dispatch (
                            TerminalShortcut.CycleTerminal(
                                terminalId,
                                CycleDirection.Previous
                            )
                        )
                    | _ -> ()
                | _ -> ()
            | _ -> ()

    Browser.Dom.window.addEventListener ("message", handler)

    { new IDisposable with
        member _.Dispose() =
            Browser.Dom.window.removeEventListener ("message", handler) }

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
            [ "terminal-tab-item"
              lifecycleClass
              if isActive then "selected" ]
            |> String.concat " ")
        prop.children [
            Html.div [
                prop.className (
                    [ "terminal-tab"; lifecycleClass; if isActive then "selected" ]
                    |> String.concat " ")
                prop.role "tab"
                prop.ariaSelected isActive
                prop.ariaLabel $"{label} for {worktreeName}, {lifecycleLabel}"
                prop.tabIndex (if isActive then 0 else -1)
                prop.title $"{label} for {worktreeName} — {lifecycleLabel}"
                prop.onMouseUp (fun e ->
                    if e.button = 1 then
                        e.preventDefault ()
                        e.stopPropagation ()
                        callbacks.CloseTab terminalId)
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
                ]
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
            tabList.querySelectorAll(":scope > .terminal-tab-item > [role=\"tab\"]")

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

    let reconnectViewButton =
        match
            state.Snapshot
            |> tryReconnectableTab state.ActiveTerminal
        with
        | None -> Html.none
        | Some tab ->
            let terminalIndex =
                state.Snapshot
                |> tabsForWorktree tab.Worktree
                |> List.tryFindIndex (fun candidate ->
                    candidate.Id = tab.Id)
                |> Option.defaultValue 0

            let label = tabLabel terminalIndex tab
            let worktreeName = WorktreePath.displayName tab.Worktree

            Html.button [
                prop.className "ctrl-btn terminal-reconnect-btn"
                prop.ariaLabel $"Reconnect view for {label} in {worktreeName}"
                prop.title "Reload this terminal view without restarting its shell or agent."
                prop.onClick (fun _ ->
                    callbacks.ReconnectView tab.Id)
                prop.text "Reconnect view"
            ]

    let newTerminalButton =
        match state.SelectedWorktree with
        | None -> Html.none
        | Some path ->
            let starting =
                state.StartState |> Option.exists startInFlight

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
                prop.children [
                    reconnectViewButton
                    newTerminalButton
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

let private startFeedback state callbacks =
    match state.SelectedWorktree, state.StartState with
    | Some _, Some startState
        when startInFlight startState
             && state.ActiveTerminal.IsNone ->
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
        when not (isSafeEndpoint endpoint) ->
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

let private runningIframes state callbacks =
    state.Snapshot.Tabs
    |> List.choose (fun tab ->
        tab
        |> trySafeRunningEndpoint
        |> Option.map (fun endpoint ->
                let terminalId = tab.Id
                let isActive =
                    state.ActiveTerminal = Some terminalId
                let viewState =
                    state.ViewStates
                    |> Map.tryFind terminalId
                let generation =
                    viewState
                    |> Option.map _.Generation
                    |> Option.defaultValue 0

                let terminalIndex =
                    state.Snapshot
                    |> tabsForWorktree tab.Worktree
                    |> List.tryFindIndex (fun candidate ->
                        candidate.Id = terminalId)
                    |> Option.defaultValue 0

                let label = tabLabel terminalIndex tab

                Html.iframe [
                    prop.key $"{EmbeddedTerminalId.value terminalId}:{generation}"
                    prop.id (terminalFrameId terminalId)
                    prop.className (
                        if isActive then
                            "terminal-iframe terminal-iframe-active"
                        else
                            "terminal-iframe")
                    prop.hidden (not isActive)
                    prop.title $"{label} for {WorktreePath.displayName tab.Worktree}"
                    prop.src endpoint.Endpoint
                    prop.custom ("data-terminal-id", EmbeddedTerminalId.value terminalId)
                    prop.custom ("data-terminal-worktree", WorktreePath.value tab.Worktree)
                    prop.custom ("data-terminal-view-generation", string generation)
                    prop.custom ("sandbox", "allow-scripts allow-same-origin")
                    prop.custom ("referrerpolicy", "no-referrer")
                    prop.custom ("scrolling", "no")
                    if viewState |> Option.exists _.FocusAfterLoad then
                        prop.onLoad (fun _ ->
                            callbacks.ViewLoaded terminalId generation)
                ]))

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
                            yield! runningIframes state callbacks
                        ]
                    ]
                ]
            ]
        ]
    ]
