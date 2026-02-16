module App

open Shared
open Elmish
open Feliz
open Fable.Remoting.Client

type SortMode =
    | ByName
    | ByActivity

type Model =
    { Worktrees: WorktreeStatus list
      RootFolderName: string
      IsLoading: bool
      HasError: bool
      SortMode: SortMode
      IsCompact: bool
      SyncTimes: SyncTimes option
      BranchEvents: Map<string, CardEvent list> }

type Msg =
    | DataLoaded of WorktreeResponse
    | DataFailed of exn
    | ToggleSort
    | ToggleCompact
    | Tick
    | OpenTerminal of string
    | StartSync of string
    | SyncStarted of Result<unit, string>
    | SyncStatusUpdate of Map<string, CardEvent list>
    | CancelSync of string
    | SyncTick

let worktreeApi =
    Remoting.createApi ()
    |> Remoting.buildProxy<IWorktreeApi>

let fetchWorktrees () =
    Cmd.OfAsync.either worktreeApi.getWorktrees () DataLoaded DataFailed

let fetchSyncStatus () =
    Cmd.OfAsync.perform worktreeApi.getSyncStatus () SyncStatusUpdate

let hasSyncRunning (events: Map<string, CardEvent list>) =
    events
    |> Map.exists (fun _ evts ->
        evts
        |> List.exists (fun e ->
            e.Status = Some StepStatus.Running))

let init () =
    { Worktrees = []
      RootFolderName = ""
      IsLoading = true
      HasError = false
      SortMode = ByName
      IsCompact = false
      SyncTimes = None
      BranchEvents = Map.empty },
    fetchWorktrees ()

let sortWorktrees sortMode worktrees =
    match sortMode with
    | ByName ->
        worktrees |> List.sortBy (fun wt -> wt.Branch)
    | ByActivity ->
        worktrees |> List.sortByDescending (fun wt -> wt.LastCommitTime)

let update msg model =
    match msg with
    | DataLoaded response ->
        { model with
            Worktrees = sortWorktrees model.SortMode response.Worktrees
            RootFolderName = response.RootFolderName
            IsLoading = false
            HasError = false
            SyncTimes = Some response.SyncTimes },
        Cmd.none

    | DataFailed _ ->
        { model with
            IsLoading = false
            HasError = true },
        Cmd.none

    | ToggleSort ->
        let newSort =
            match model.SortMode with
            | ByName -> ByActivity
            | ByActivity -> ByName
        { model with
            SortMode = newSort
            Worktrees = sortWorktrees newSort model.Worktrees },
        Cmd.none

    | ToggleCompact ->
        { model with IsCompact = not model.IsCompact }, Cmd.none

    | OpenTerminal path ->
        model, Cmd.OfAsync.attempt worktreeApi.openTerminal path (fun _ -> Tick)

    | Tick ->
        model, fetchWorktrees ()

    | StartSync branch ->
        model, Cmd.OfAsync.perform worktreeApi.startSync branch SyncStarted

    | SyncStarted _ ->
        model, fetchSyncStatus ()

    | SyncStatusUpdate events ->
        { model with BranchEvents = events }, Cmd.none

    | CancelSync branch ->
        model, Cmd.OfAsync.attempt worktreeApi.cancelSync branch (fun _ -> Tick)

    | SyncTick ->
        model, fetchSyncStatus ()

let pollingSubscription (model: Model) : Sub<Msg> =
    let worktreePolling (dispatch: Dispatch<Msg>) =
        let intervalId =
            Fable.Core.JS.setInterval (fun () -> dispatch Tick) 15000
        { new System.IDisposable with
            member _.Dispose() = Fable.Core.JS.clearInterval intervalId }

    let syncPolling (dispatch: Dispatch<Msg>) =
        let intervalId =
            Fable.Core.JS.setInterval (fun () -> dispatch SyncTick) 2000
        { new System.IDisposable with
            member _.Dispose() = Fable.Core.JS.clearInterval intervalId }

    match hasSyncRunning model.BranchEvents with
    | true ->
        [ [ "polling" ], worktreePolling
          [ "sync-polling" ], syncPolling ]
    | false ->
        [ [ "polling" ], worktreePolling ]

let relativeTime (dt: System.DateTimeOffset) =
    let now = System.DateTimeOffset.Now
    let diff = now - dt
    match diff with
    | d when d.TotalMinutes < 1.0 -> "just now"
    | d when d.TotalMinutes < 60.0 -> sprintf "%dm ago" (int d.TotalMinutes)
    | d when d.TotalHours < 24.0 -> sprintf "%dh ago" (int d.TotalHours)
    | d -> sprintf "%dd ago" (int d.TotalDays)

let syncAgeText (label: string) (time: System.DateTimeOffset option) =
    match time with
    | Some dt ->
        let diff = System.DateTimeOffset.Now - dt
        let age =
            match diff with
            | d when d.TotalSeconds < 60.0 -> sprintf "%ds ago" (int d.TotalSeconds |> max 0)
            | d when d.TotalMinutes < 60.0 -> sprintf "%dm ago" (int d.TotalMinutes)
            | d -> sprintf "%dh ago" (int d.TotalHours)
        sprintf "%s %s" label age
    | None -> sprintf "%s --" label

let syncFooter (syncTimes: SyncTimes) =
    Html.div [
        prop.className "sync-footer"
        prop.children [
            Html.span [ prop.className "sync-source"; prop.text (syncAgeText "Git" syncTimes.Git) ]
            Html.span [ prop.className "sync-sep"; prop.text "\u00b7" ]
            Html.span [ prop.className "sync-source"; prop.text (syncAgeText "PR" syncTimes.Pr) ]
            Html.span [ prop.className "sync-sep"; prop.text "\u00b7" ]
            Html.span [ prop.className "sync-source"; prop.text (syncAgeText "Claude" syncTimes.Claude) ]
            Html.span [ prop.className "sync-sep"; prop.text "\u00b7" ]
            Html.span [ prop.className "sync-source"; prop.text (syncAgeText "Beads" syncTimes.Beads) ]
        ]
    ]

let ccClassName =
    function
    | Active  -> "active"
    | Recent  -> "recent"
    | Idle    -> "idle"
    | Unknown -> "unknown"

let cardClassName (wt: WorktreeStatus) =
    let cc = ccClassName wt.Claude
    match wt.IsStale with
    | true  -> sprintf "wt-card cc-%s stale" cc
    | false -> sprintf "wt-card cc-%s" cc

let beadsTotal (b: BeadsSummary) = b.Open + b.InProgress + b.Closed

let segmentPct count total =
    match total with
    | 0 -> 0
    | _ -> (count * 100) / total

let beadsCounts (b: BeadsSummary) =
    Html.div [
        prop.className "beads-counts"
        prop.children [
            Html.span [ prop.className "beads-open"; prop.text (string b.Open) ]
            Html.span [ prop.className "beads-sep"; prop.text "/" ]
            Html.span [ prop.className "beads-inprogress"; prop.text (string b.InProgress) ]
            Html.span [ prop.className "beads-sep"; prop.text "/" ]
            Html.span [ prop.className "beads-closed"; prop.text (string b.Closed) ]
        ]
    ]

let beadsProgressBar (b: BeadsSummary) =
    let total = beadsTotal b
    Html.div [
        prop.className "progress-bar"
        prop.children [
            Html.div [
                prop.className "progress-segment seg-open"
                prop.style [ style.width (length.percent (segmentPct b.Open total)) ]
            ]
            Html.div [
                prop.className "progress-segment seg-inprogress"
                prop.style [ style.width (length.percent (segmentPct b.InProgress total)) ]
            ]
            Html.div [
                prop.className "progress-segment seg-closed"
                prop.style [ style.width (length.percent (segmentPct b.Closed total)) ]
            ]
        ]
    ]

let mainBehindIndicator (count: int) =
    match count with
    | 0 ->
        Html.span [
            prop.className "main-behind up-to-date"
            prop.text "up to date"
        ]
    | n ->
        Html.span [
            prop.className (match n > 20 with true -> "main-behind behind-warning" | false -> "main-behind")
            prop.text (sprintf "%d behind main" n)
        ]

let isBranchSyncing (events: CardEvent list) =
    events |> List.exists (fun e -> e.Status = Some StepStatus.Running)

let syncButton dispatch (wt: WorktreeStatus) (branchEvents: CardEvent list) =
    let syncing = isBranchSyncing branchEvents
    let claudeBlocked = wt.Claude = Active || wt.Claude = Recent
    let disabled = syncing || claudeBlocked
    match syncing with
    | true ->
        Html.button [
            prop.className "sync-cancel-btn"
            prop.onClick (fun e -> e.stopPropagation(); dispatch (CancelSync wt.Branch))
            prop.text "Cancel"
        ]
    | false ->
        Html.button [
            prop.className (match disabled with true -> "sync-btn disabled" | false -> "sync-btn")
            prop.disabled disabled
            prop.onClick (fun e -> e.stopPropagation(); dispatch (StartSync wt.Branch))
            prop.title (match claudeBlocked with true -> "Claude is active" | false -> "Sync with main")
            prop.text "Sync"
        ]

let mainBehindWithSync dispatch (wt: WorktreeStatus) (branchEvents: CardEvent list) =
    match wt.MainBehindCount with
    | 0 ->
        Html.div [
            prop.className "main-behind-row"
            prop.children [
                Html.span [
                    prop.className "main-behind up-to-date"
                    prop.text "up to date"
                ]
            ]
        ]
    | n ->
        Html.div [
            prop.className "main-behind-row"
            prop.children [
                Html.span [
                    prop.className (match n > 20 with true -> "main-behind behind-warning" | false -> "main-behind")
                    prop.text (sprintf "%d behind main" n)
                ]
                syncButton dispatch wt branchEvents
            ]
        ]

let stepStatusClassName (status: StepStatus option) =
    match status with
    | Some StepStatus.Running -> "event-status running"
    | Some StepStatus.Succeeded -> "event-status success"
    | Some (StepStatus.Failed _) -> "event-status failed"
    | Some StepStatus.Cancelled -> "event-status cancelled"
    | Some StepStatus.Pending -> "event-status"
    | None -> "event-status"

let stepStatusText (status: StepStatus option) =
    match status with
    | Some StepStatus.Running -> "running"
    | Some StepStatus.Succeeded -> "success"
    | Some (StepStatus.Failed msg) -> match msg with "" -> "failed" | _ -> sprintf "failed: %s" msg
    | Some StepStatus.Cancelled -> "cancelled"
    | _ -> ""

let eventLogEntry (evt: CardEvent) =
    Html.div [
        prop.className "event-entry"
        prop.children [
            Html.span [ prop.className "event-source"; prop.text evt.Source ]
            Html.span [ prop.className "event-message"; prop.text evt.Message ]
            match evt.Status with
            | Some _ ->
                Html.span [
                    prop.className (stepStatusClassName evt.Status)
                    prop.text (stepStatusText evt.Status)
                ]
            | None -> Html.none
        ]
    ]

let eventLog (events: CardEvent list) =
    match events with
    | [] -> Html.none
    | evts ->
        Html.div [
            prop.className "event-log"
            prop.children (evts |> List.map eventLogEntry)
        ]

let abbreviatePipelineName (repoName: string) (name: string) =
    let stripped =
        match repoName.Length > 0 && name.Length >= repoName.Length && name.[..repoName.Length-1].ToLowerInvariant() = repoName.ToLowerInvariant() with
        | true -> name.[repoName.Length..].TrimStart()
        | false -> name
    match stripped.Length >= 5 && stripped.[stripped.Length-5..].ToLowerInvariant() = " - pr" with
    | true -> stripped.[..stripped.Length-6].TrimEnd()
    | false -> stripped

let buildBadge (repoName: string) (build: BuildInfo) =
    let statusText =
        match build.Status with
        | NoBuild -> None
        | Building -> Some "Building"
        | Succeeded -> Some "Passed"
        | Failed -> Some "Failed"
        | PartiallySucceeded -> Some "Partial"
        | Canceled -> Some "Canceled"
    match statusText with
    | None -> Html.none
    | Some status ->
        let abbreviated = abbreviatePipelineName repoName build.Name
        let text =
            match build.Failure with
            | Some f -> sprintf "%s: %s" f.StepName status
            | None ->
                match abbreviated with
                | "" -> status
                | name -> sprintf "%s: %s" name status
        let className =
            match build.Status with
            | Building -> "build-badge building"
            | Succeeded -> "build-badge succeeded"
            | Failed -> "build-badge failed"
            | PartiallySucceeded -> "build-badge partial"
            | Canceled -> "build-badge canceled"
            | NoBuild -> "build-badge"
        let tooltip =
            match build.Failure with
            | Some f when f.Log.Length > 0 -> Some f.Log
            | _ -> None
        match build.Url with
        | Some url ->
            Interop.createElement "a" [
                prop.className className
                prop.text text
                prop.href url
                prop.target "_blank"
                match tooltip with
                | Some t -> prop.title t
                | None -> ()
            ]
        | None ->
            Html.span [
                prop.className className
                prop.text text
                match tooltip with
                | Some t -> prop.title t
                | None -> ()
            ]

let buildBadges (repoName: string) (builds: BuildInfo list) =
    React.fragment (builds |> List.map (buildBadge repoName))

let terminalButton dispatch (wt: WorktreeStatus) =
    Html.button [
        prop.className "terminal-btn"
        prop.title "Open terminal"
        prop.onClick (fun e -> e.stopPropagation(); dispatch (OpenTerminal wt.Path))
        prop.text ">"
    ]

let compactWorktreeCard dispatch (repoName: string) (wt: WorktreeStatus) =
    Html.div [
        prop.className (cardClassName wt + " compact")
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className (sprintf "cc-dot %s" (ccClassName wt.Claude)) ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime wt.LastCommitTime) ]
                    terminalButton dispatch wt
                ]
            ]
            Html.div [
                prop.className "compact-detail"
                prop.children [
                    Html.span [
                        prop.className "beads-inline"
                        prop.children [
                            Html.span [ prop.className "beads-open"; prop.text (string wt.Beads.Open) ]
                            Html.span [ prop.className "beads-sep"; prop.text "/" ]
                            Html.span [ prop.className "beads-inprogress"; prop.text (string wt.Beads.InProgress) ]
                            Html.span [ prop.className "beads-sep"; prop.text "/" ]
                            Html.span [ prop.className "beads-closed"; prop.text (string wt.Beads.Closed) ]
                        ]
                    ]
                    mainBehindIndicator wt.MainBehindCount
                    match wt.Pr with
                    | NoPr -> Html.none
                    | HasPr pr ->
                        match pr.IsMerged with
                        | true ->
                            Interop.createElement "a" [
                                prop.className "pr-badge merged"
                                prop.title pr.Title
                                prop.href pr.Url
                                prop.target "_blank"
                                prop.text "Merged"
                            ]
                        | false ->
                            Interop.createElement "a" [
                                prop.className (match pr.IsDraft with true -> "pr-badge draft" | false -> "pr-badge")
                                prop.title pr.Title
                                prop.href pr.Url
                                prop.target "_blank"
                                prop.text (sprintf "PR #%d" pr.Id)
                            ]
                            match pr.ThreadCounts.Total with
                            | 0 -> Html.none
                            | total ->
                                Html.span [
                                    prop.className (match pr.ThreadCounts.Unresolved with 0 -> "thread-badge dimmed" | _ -> "thread-badge")
                                    prop.text (sprintf "%d/%d threads" pr.ThreadCounts.Unresolved total)
                                ]
                            buildBadges repoName pr.Builds
                ]
            ]
        ]
    ]

let worktreeCard dispatch (repoName: string) (branchEvents: CardEvent list) (wt: WorktreeStatus) =
    Html.div [
        prop.className (cardClassName wt)
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className (sprintf "cc-dot %s" (ccClassName wt.Claude)) ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                    terminalButton dispatch wt
                ]
            ]

            Html.div [
                prop.className "commit-line"
                prop.children [
                    Html.text wt.LastCommitMessage
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime wt.LastCommitTime) ]
                ]
            ]

            Html.div [
                prop.className "beads-row"
                prop.children [
                    beadsCounts wt.Beads
                    beadsProgressBar wt.Beads
                ]
            ]

            mainBehindWithSync dispatch wt branchEvents

            match wt.Pr with
            | NoPr -> Html.none
            | HasPr pr ->
                Html.div [
                    prop.className "pr-row"
                    prop.children [
                        match pr.IsMerged with
                        | true ->
                            Interop.createElement "a" [
                                prop.className "pr-badge merged"
                                prop.title pr.Title
                                prop.href pr.Url
                                prop.target "_blank"
                                prop.text "Merged"
                            ]
                        | false ->
                            Interop.createElement "a" [
                                prop.className (match pr.IsDraft with true -> "pr-badge draft" | false -> "pr-badge")
                                prop.title pr.Title
                                prop.href pr.Url
                                prop.target "_blank"
                                prop.text (sprintf "PR #%d" pr.Id)
                            ]
                            match pr.ThreadCounts.Total with
                            | 0 -> Html.none
                            | total ->
                                Html.span [
                                    prop.className (match pr.ThreadCounts.Unresolved with 0 -> "thread-badge dimmed" | _ -> "thread-badge")
                                    prop.text (sprintf "%d/%d threads" pr.ThreadCounts.Unresolved total)
                                ]
                            buildBadges repoName pr.Builds
                    ]
                ]

            eventLog branchEvents
        ]
    ]

let renderCard dispatch isCompact repoName (branchEvents: Map<string, CardEvent list>) (wt: WorktreeStatus) =
    let events = branchEvents |> Map.tryFind wt.Branch |> Option.defaultValue []
    match isCompact with
    | true -> compactWorktreeCard dispatch repoName wt
    | false -> worktreeCard dispatch repoName events wt

let sortLabel =
    function
    | ByName -> "A-Z"
    | ByActivity -> "Recent"

let view model dispatch =
    Html.div [
        prop.className "dashboard"
        prop.children [
            Html.div [
                prop.className "dashboard-header"
                prop.children [
                    Html.div [
                        prop.className "header-top"
                        prop.children [
                            Html.h1 [
                                prop.children [
                                    Html.text "Treemon"
                                    match model.RootFolderName with
                                    | "" -> Html.none
                                    | name ->
                                        Html.span [
                                            prop.className "folder-accent"
                                            prop.text (sprintf ": %s" name)
                                        ]
                                ]
                            ]
                            Html.div [
                                prop.className "header-controls"
                                prop.children [
                                    Html.button [
                                        prop.className "ctrl-btn"
                                        prop.onClick (fun _ -> dispatch ToggleSort)
                                        prop.text (sprintf "Sort: %s" (sortLabel model.SortMode))
                                    ]
                                    Html.button [
                                        prop.className (match model.IsCompact with true -> "ctrl-btn active" | false -> "ctrl-btn")
                                        prop.onClick (fun _ -> dispatch ToggleCompact)
                                        prop.text "Compact"
                                    ]
                                ]
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "status-bar"
                        prop.children [
                            Html.span (sprintf "%d worktrees" (List.length model.Worktrees))
                            if model.IsLoading && model.Worktrees.IsEmpty then
                                Html.span "Loading..."
                        ]
                    ]
                ]
            ]

            if model.HasError then
                Html.div [
                    prop.className "error-bar"
                    prop.text "Failed to fetch data. Showing last known state."
                ]

            Html.div [
                prop.className "card-grid"
                prop.children (model.Worktrees |> List.map (renderCard dispatch model.IsCompact model.RootFolderName model.BranchEvents))
            ]

            match model.SyncTimes with
            | Some st -> syncFooter st
            | None -> Html.none
        ]
    ]

open Elmish.React

Program.mkProgram init update view
|> Program.withSubscription pollingSubscription
|> Program.withReactSynchronous "app"
|> Program.run
