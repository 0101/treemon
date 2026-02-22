module App

open Shared
open Shared.EventUtils
open Elmish
open Feliz
open Fable.Remoting.Client
open Browser

type Model =
    { Worktrees: WorktreeStatus list
      RootFolderName: string
      IsLoading: bool
      HasError: bool
      IsReady: bool
      SortMode: SortMode
      IsCompact: bool
      SchedulerEvents: CardEvent list
      LatestByCategory: Map<string, CardEvent>
      BranchEvents: Map<string, CardEvent list>
      AppVersion: string option
      EyeDirection: float * float }

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
    | DeleteWorktree of string
    | DeleteCompleted of Result<unit, string>

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
      IsReady = false
      SortMode = ByActivity
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      AppVersion = None
      EyeDirection = (0.0, 0.0) },
    Cmd.batch [ fetchWorktrees (); fetchSyncStatus () ]

let rng = System.Random()

let randomEyeDirection () =
    let dx = rng.NextDouble() * 3.0 - 1.5
    let dy = rng.NextDouble() * 2.0 - 1.0
    (dx, dy)

let update msg model =
    match msg with
    | DataLoaded response ->
        match model.AppVersion with
        | Some v when v <> response.AppVersion ->
            model, Cmd.ofEffect (fun _ -> Dom.window.location.reload ())
        | _ ->
            { model with
                Worktrees = sortWorktrees model.SortMode response.Worktrees
                RootFolderName = response.RootFolderName
                IsLoading = false
                HasError = false
                IsReady = response.IsReady
                SchedulerEvents = response.SchedulerEvents
                LatestByCategory = response.LatestByCategory
                AppVersion = Some response.AppVersion
                EyeDirection = randomEyeDirection () },
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
        model, Cmd.batch [ fetchWorktrees (); fetchSyncStatus () ]

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

    | DeleteWorktree branch ->
        model, Cmd.OfAsync.perform worktreeApi.deleteWorktree branch DeleteCompleted

    | DeleteCompleted (Ok _) ->
        model, fetchWorktrees ()

    | DeleteCompleted (Error _) ->
        model, Cmd.none

let pollingSubscription (model: Model) : Sub<Msg> =
    let worktreePolling (dispatch: Dispatch<Msg>) =
        let intervalId =
            Fable.Core.JS.setInterval (fun () -> dispatch Tick) 1000
        { new System.IDisposable with
            member _.Dispose() = Fable.Core.JS.clearInterval intervalId }

    let syncPolling (dispatch: Dispatch<Msg>) =
        let intervalId =
            Fable.Core.JS.setInterval (fun () -> dispatch SyncTick) 2000
        { new System.IDisposable with
            member _.Dispose() = Fable.Core.JS.clearInterval intervalId }

    if hasSyncRunning model.BranchEvents then
        [ [ "polling" ], worktreePolling
          [ "sync-polling" ], syncPolling ]
    else
        [ [ "polling" ], worktreePolling ]

let relativeTime (dt: System.DateTimeOffset) =
    let now = System.DateTimeOffset.Now
    let diff = now - dt
    match diff with
    | d when d.TotalMinutes < 1.0 -> "just now"
    | d when d.TotalMinutes < 60.0 -> $"{int d.TotalMinutes}m ago"
    | d when d.TotalHours < 24.0 -> $"{int d.TotalHours}h ago"
    | d -> $"{int d.TotalDays}d ago"

let ccClassName =
    function
    | Working        -> "working"
    | WaitingForUser -> "waiting"
    | Done           -> "done"
    | Idle           -> "idle"

let isMerged (wt: WorktreeStatus) =
    match wt.Pr with
    | HasPr pr -> pr.IsMerged
    | NoPr -> false

let cardClassName (wt: WorktreeStatus) =
    let cc = ccClassName wt.Claude
    if isMerged wt then $"wt-card cc-{cc} merged" else $"wt-card cc-{cc}"

let beadsTotal (b: BeadsSummary) = b.Open + b.InProgress + b.Closed

let segmentPct count total =
    match total with
    | 0 -> 0.0
    | _ -> (float count * 100.0) / float total

let beadsCounts (className: string) (b: BeadsSummary) =
    Html.span [
        prop.className className
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
    if count = 0 then
        Html.span [
            prop.className "main-behind up-to-date"
            prop.text "up to date"
        ]
    else
        Html.span [
            prop.className (if count > 20 then "main-behind behind-warning" else "main-behind")
            prop.text ($"{count} behind main")
        ]

let isBranchSyncing (events: CardEvent list) =
    events |> List.exists (fun e -> e.Status = Some StepStatus.Running)

let syncButton dispatch (wt: WorktreeStatus) (branchEvents: CardEvent list) =
    let syncing = isBranchSyncing branchEvents
    let claudeBlocked = wt.Claude = Working || wt.Claude = WaitingForUser
    let disabled = syncing || claudeBlocked
    if syncing then
        Html.button [
            prop.className "sync-cancel-btn"
            prop.onClick (fun e -> e.stopPropagation(); dispatch (CancelSync wt.Branch))
            prop.text "Cancel"
        ]
    else
        Html.button [
            prop.className (if disabled then "sync-btn disabled" else "sync-btn")
            prop.disabled disabled
            prop.onClick (fun e -> e.stopPropagation(); dispatch (StartSync wt.Branch))
            prop.title (if claudeBlocked then "Claude is active" else "Sync with main")
            prop.text "Sync"
        ]

let mainBehindWithSync dispatch (wt: WorktreeStatus) (branchEvents: CardEvent list) =
    Html.div [
        prop.className "main-behind-row"
        prop.children [
            mainBehindIndicator wt.MainBehindCount
            if wt.MainBehindCount > 0 then
                if wt.IsDirty then
                    Html.span [
                        prop.className "dirty-warning"
                        prop.text "uncommitted changes"
                    ]
                else syncButton dispatch wt branchEvents
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
    | Some (StepStatus.Failed msg) -> match msg with "" -> "failed" | _ -> $"failed: {msg}"
    | Some StepStatus.Cancelled -> "cancelled"
    | _ -> ""

let relativeEventTime (dt: System.DateTimeOffset) =
    let diff = System.DateTimeOffset.Now - dt
    match diff with
    | d when d.TotalSeconds < 60.0 -> $"{int d.TotalSeconds |> max 0}s ago"
    | d when d.TotalMinutes < 60.0 -> $"{int d.TotalMinutes}m ago"
    | d when d.TotalHours < 24.0 -> $"{int d.TotalHours}h ago"
    | d -> $"{int d.TotalDays}d ago"

let eventLogEntry (evt: CardEvent) =
    Html.div [
        prop.className "event-entry"
        prop.children [
            Html.span [ prop.className "event-time"; prop.text (relativeEventTime evt.Timestamp) ]
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

let knownCategories =
    [ "WorktreeList"; "GitRefresh"; "BeadsRefresh"; "ClaudeRefresh"; "PrFetch"; "GitFetch" ]

let statusOverviewRow (latestBySource: Map<string, CardEvent>) (category: string) =
    match Map.tryFind category latestBySource with
    | None ->
        Html.div [
            prop.className "status-row pending"
            prop.children [
                Html.span [ prop.className "status-category"; prop.text category ]
                Html.span [ prop.className "status-target" ]
                Html.span [ prop.className "status-duration" ]
                Html.span [ prop.className "status-time" ]
                Html.span [ prop.className "status-badge pending"; prop.text "pending" ]
            ]
        ]
    | Some evt ->
        let target = extractBranchName evt.Message |> Option.defaultValue ""
        Html.div [
            prop.className "status-row"
            prop.children [
                Html.span [ prop.className "status-category"; prop.text category ]
                Html.span [ prop.className "status-target"; prop.text target ]
                match evt.Duration with
                | Some d -> Html.span [ prop.className "status-duration"; prop.text (sprintf "%.1fs" d.TotalSeconds) ]
                | None -> Html.span [ prop.className "status-duration" ]
                Html.span [ prop.className "status-time"; prop.text (relativeEventTime evt.Timestamp) ]
                match evt.Status with
                | Some _ ->
                    Html.span [
                        prop.className (stepStatusClassName evt.Status)
                        prop.text (stepStatusText evt.Status)
                    ]
                | None -> Html.none
            ]
        ]

let pinnedErrorEntry (evt: CardEvent) =
    Html.div [
        prop.className "event-entry pinned-error"
        prop.children [
            Html.span [ prop.className "event-time"; prop.text (relativeEventTime evt.Timestamp) ]
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

let schedulerFooter (events: CardEvent list) (latestByCategory: Map<string, CardEvent>) =
    let errors = pinnedErrors events
    Html.div [
        prop.className "scheduler-footer"
        prop.children [
            match errors with
            | [] -> Html.none
            | errs ->
                Html.div [
                    prop.className "pinned-errors"
                    prop.children (errs |> List.map pinnedErrorEntry)
                ]
            Html.div [
                prop.className "status-overview"
                prop.children (knownCategories |> List.map (statusOverviewRow latestByCategory))
            ]
        ]
    ]

let abbreviatePipelineName (repoName: string) (name: string) =
    let stripped =
        if name.Length >= repoName.Length && name.StartsWith(repoName, System.StringComparison.OrdinalIgnoreCase)
        then name.[repoName.Length..].TrimStart()
        else name
    if stripped.EndsWith(" - pr", System.StringComparison.OrdinalIgnoreCase)
    then stripped.[..stripped.Length-6].TrimEnd()
    else stripped

let buildBadge (repoName: string) (build: BuildInfo) =
    let statusText =
        match build.Status with
        | Building -> "Building"
        | Succeeded -> "Passed"
        | Failed -> "Failed"
        | PartiallySucceeded -> "Partial"
        | Canceled -> "Canceled"
    let abbreviated = abbreviatePipelineName repoName build.Name
    let text =
        match build.Failure with
        | Some f -> $"{f.StepName}: {statusText}"
        | None -> if abbreviated = "" then statusText else $"{abbreviated}: {statusText}"
    let className =
        match build.Status with
        | Building -> "build-badge building"
        | Succeeded -> "build-badge succeeded"
        | Failed -> "build-badge failed"
        | PartiallySucceeded -> "build-badge partial"
        | Canceled -> "build-badge canceled"
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

let deleteButton dispatch (wt: WorktreeStatus) =
    Html.button [
        prop.className "delete-btn"
        prop.title "Remove worktree"
        prop.onClick (fun e ->
            e.stopPropagation()
            let confirmed =
                Dom.window.confirm (
                    $"Remove worktree {wt.Branch}? This will delete the worktree folder and local branch.")
            match confirmed with
            | true -> dispatch (DeleteWorktree wt.Branch)
            | false -> ())
        prop.text "\u2715"
    ]

let prBadgeContent (repoName: string) (pr: PrInfo) =
    React.fragment [
        if pr.IsMerged then
            Interop.createElement "a" [
                prop.className "pr-badge merged"
                prop.title pr.Title
                prop.href pr.Url
                prop.target "_blank"
                prop.text "Merged"
            ]
        else
            Interop.createElement "a" [
                prop.className (if pr.IsDraft then "pr-badge draft" else "pr-badge")
                prop.title pr.Title
                prop.href pr.Url
                prop.target "_blank"
                prop.text ($"PR #{pr.Id}")
            ]
            if pr.ThreadCounts.Total > 0 then
                Html.span [
                    prop.className (if pr.ThreadCounts.Unresolved = 0 then "thread-badge dimmed" else "thread-badge")
                    prop.text ($"{pr.ThreadCounts.Unresolved}/{pr.ThreadCounts.Total} threads")
                ]
            buildBadges repoName pr.Builds
    ]

let prSection (repoName: string) (wt: WorktreeStatus) =
    match wt.Pr with
    | NoPr -> Html.none
    | HasPr pr -> prBadgeContent repoName pr

let prRow (repoName: string) (wt: WorktreeStatus) =
    match wt.Pr with
    | NoPr -> Html.none
    | HasPr pr ->
        Html.div [
            prop.className "pr-row"
            prop.children [ prBadgeContent repoName pr ]
        ]

let workMetricsView (metrics: WorkMetrics option) =
    match metrics with
    | None -> Html.none
    | Some m when m.CommitCount = 0 -> Html.none
    | Some m ->
        let displayCount = min m.CommitCount 90
        let overflow = m.CommitCount - displayCount
        Html.span [
            prop.className "work-metrics"
            prop.children [
                Html.span [
                    prop.className "commit-grid"
                    prop.children (
                        List.init displayCount (fun _ ->
                            Html.span [ prop.className "commit-square" ])
                    )
                ]
                if overflow > 0 then
                    Html.span [ prop.className "commit-overflow"; prop.text $"+{overflow}" ]
                match m.LinesAdded, m.LinesRemoved with
                | 0, 0 -> Html.none
                | added, removed ->
                    Html.span [
                        prop.className "diff-stats"
                        prop.children [
                            Html.span [ prop.className "diff-added"; prop.text $"+{added}" ]
                            Html.text " "
                            Html.span [ prop.className "diff-removed"; prop.text $"-{removed}" ]
                        ]
                    ]
            ]
        ]

let compactWorktreeCard dispatch (repoName: string) (wt: WorktreeStatus) =
    Html.div [
        prop.key wt.Branch
        prop.className (cardClassName wt + " compact")
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className ($"cc-dot {ccClassName wt.Claude}") ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                    workMetricsView wt.WorkMetrics
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime wt.LastCommitTime) ]
                    terminalButton dispatch wt
                    deleteButton dispatch wt
                ]
            ]
            Html.div [
                prop.className "compact-detail"
                prop.children [
                    beadsCounts "beads-inline" wt.Beads
                    mainBehindIndicator wt.MainBehindCount
                    prSection repoName wt
                ]
            ]
        ]
    ]

let worktreeCard dispatch (repoName: string) (branchEvents: CardEvent list) (wt: WorktreeStatus) =
    Html.div [
        prop.key wt.Branch
        prop.className (cardClassName wt)
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className ($"cc-dot {ccClassName wt.Claude}") ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                    workMetricsView wt.WorkMetrics
                    terminalButton dispatch wt
                    deleteButton dispatch wt
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
                    beadsCounts "beads-counts" wt.Beads
                    beadsProgressBar wt.Beads
                ]
            ]

            mainBehindWithSync dispatch wt branchEvents

            prRow repoName wt

            eventLog branchEvents
        ]
    ]

let renderCard dispatch isCompact repoName (branchEvents: Map<string, CardEvent list>) (wt: WorktreeStatus) =
    let events = branchEvents |> Map.tryFind wt.Branch |> Option.defaultValue []
    match isCompact with
    | true -> compactWorktreeCard dispatch repoName wt
    | false -> worktreeCard dispatch repoName events wt

let skeletonCard () =
    Html.div [
        prop.className "wt-card skeleton"
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className "skeleton-dot" ]
                    Html.span [ prop.className "skeleton-bar skeleton-branch" ]
                ]
            ]
            Html.div [ prop.className "skeleton-bar skeleton-commit" ]
            Html.div [ prop.className "skeleton-bar skeleton-beads" ]
        ]
    ]

let skeletonGrid () =
    Html.div [
        prop.className "card-grid"
        prop.children (List.init 6 (fun _ -> skeletonCard ()))
    ]

let sortLabel =
    function
    | ByName -> "A-Z"
    | ByActivity -> "Recent"

let viewEyeLogo (dx: float, dy: float) =
    Svg.svg [
        svg.className "eye-logo"
        svg.viewBox (-2, -2, 44, 24)
        svg.children [
            Svg.path [
                svg.d "M2 10 Q10 0 20 0 Q30 0 38 10 Q30 20 20 20 Q10 20 2 10 Z"
                svg.fill "#e8e8e8"
                svg.stroke "#56b6c2"
                svg.strokeWidth 2.5
            ]
            Svg.g [
                svg.className "eye-iris"
                svg.custom ("transform", $"translate({dx}, {dy})")
                svg.children [
                    Svg.circle [
                        svg.cx 20
                        svg.cy 10
                        svg.r 9
                        svg.fill "#1a1b2e"
                    ]
                    Svg.circle [
                        svg.cx 20
                        svg.cy 10
                        svg.r 6
                        svg.fill "#56b6c2"
                    ]
                    Svg.circle [
                        svg.cx 20
                        svg.cy 10
                        svg.r 3
                        svg.fill "#1a1b2e"
                    ]
                ]
            ]
            Svg.circle [
                svg.cx 23
                svg.cy 5
                svg.r 2
                svg.fill "rgba(255, 255, 255, 0.8)"
            ]
        ]
    ]

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
                                    viewEyeLogo model.EyeDirection
                                ]
                            ]
                            Html.div [
                                prop.className "header-controls"
                                prop.children [
                                    Html.button [
                                        prop.className "ctrl-btn"
                                        prop.onClick (fun _ -> dispatch ToggleSort)
                                        prop.text ($"Sort: {sortLabel model.SortMode}")
                                    ]
                                    Html.button [
                                        prop.className (if model.IsCompact then "ctrl-btn active" else "ctrl-btn")
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
                            if not model.IsReady && model.Worktrees.IsEmpty then
                                Html.span "Waiting for first refresh..."
                        ]
                    ]
                ]
            ]

            if model.HasError then
                Html.div [
                    prop.className "error-bar"
                    prop.text "Failed to fetch data. Showing last known state."
                ]

            if not model.IsReady && model.Worktrees.IsEmpty then
                skeletonGrid ()
            else
                Html.div [
                    prop.className "card-grid"
                    prop.children (model.Worktrees |> List.map (renderCard dispatch model.IsCompact model.RootFolderName model.BranchEvents))
                ]

            schedulerFooter model.SchedulerEvents model.LatestByCategory
        ]
    ]

open Elmish.React

Program.mkProgram init update view
|> Program.withSubscription pollingSubscription
|> Program.withReactSynchronous "app"
|> Program.run
