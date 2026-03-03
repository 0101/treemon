module App

open Shared
open Shared.EventUtils
open Navigation
open Elmish
open Feliz
open Fable.Remoting.Client
open Browser
open Fable.Core.JsInterop

type Model =
    { Repos: RepoModel list
      IsLoading: bool
      HasError: bool
      SortMode: SortMode
      IsCompact: bool
      SchedulerEvents: CardEvent list
      LatestByCategory: Map<string, CardEvent>
      BranchEvents: Map<string, CardEvent list>
      SyncPending: Set<string>
      AppVersion: string option
      EyeDirection: float * float
      FocusedElement: FocusTarget option
      DeletedBranches: Set<string> }

type Msg =
    | DataLoaded of DashboardResponse
    | DataFailed of exn
    | ToggleSort
    | ToggleCompact
    | ToggleCollapse of repoId: RepoId
    | Tick
    | OpenTerminal of string
    | StartSync of branch: string * scopedKey: string
    | SyncStarted of key: string * Result<unit, string>
    | SyncStatusUpdate of Map<string, CardEvent list>
    | CancelSync of string
    | SyncTick
    | DeleteWorktree of string
    | DeleteCompleted of Result<unit, string>
    | FocusSession of path: string
    | OpenNewTab of path: string
    | SessionResult of Result<unit, string>
    | KeyPressed of key: string * hasModifier: bool
    | SetFocus of FocusTarget option

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
    { Repos = []
      IsLoading = true
      HasError = false
      SortMode = ByActivity
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      SyncPending = Set.empty
      AppVersion = None
      EyeDirection = (0.0, 0.0)
      FocusedElement = None
      DeletedBranches = Set.empty },
    Cmd.batch [ fetchWorktrees (); fetchSyncStatus () ]

let rng = System.Random()

let randomEyeDirection () =
    let dx = rng.NextDouble() * 3.0 - 1.5
    let dy = rng.NextDouble() * 2.0 - 1.0
    (dx, dy)

let filterDeletedBranches (deleted: Set<string>) (repos: RepoModel list) =
    if Set.isEmpty deleted then repos
    else
        repos
        |> List.map (fun r ->
            { r with Worktrees = r.Worktrees |> List.filter (fun wt -> not (Set.contains wt.Branch deleted)) })

let findWorktree (scopedKey: string) (model: Model) =
    let parts = scopedKey.Split('/', 2)
    if parts.Length < 2 then None
    else
        let repoId, branch = parts[0], parts[1]
        model.Repos
        |> List.tryFind (fun r -> RepoId.value r.RepoId = repoId)
        |> Option.bind (fun r -> r.Worktrees |> List.tryFind (fun wt -> wt.Branch = branch))

let terminalAction (wt: WorktreeStatus) =
    if wt.HasActiveSession then FocusSession wt.Path else OpenTerminal wt.Path

let keyBinding (focused: FocusTarget) (key: string) (model: Model) : Msg option =
    match focused with
    | Card scopedKey ->
        match key with
        | "Enter" -> findWorktree scopedKey model |> Option.map terminalAction
        | "s" -> findWorktree scopedKey model |> Option.map (fun wt -> StartSync (wt.Branch, scopedKey))
        | "+" -> findWorktree scopedKey model |> Option.bind (fun wt -> if wt.HasActiveSession then Some (OpenNewTab wt.Path) else None)
        | _ -> None
    | RepoHeader repoId ->
        match key with
        | "Enter" -> Some (ToggleCollapse repoId)
        | _ -> None

let update msg model =
    match msg with
    | DataLoaded response ->
        match model.AppVersion with
        | Some v when v <> response.AppVersion ->
            model, Cmd.ofEffect (fun _ -> Dom.window.location.reload ())
        | _ ->
            let existingCollapse =
                model.Repos
                |> List.map (fun r -> r.RepoId, r.IsCollapsed)
                |> Map.ofList
            let serverBranches =
                response.Repos
                |> List.collect (fun r -> r.Worktrees |> List.map (fun wt -> wt.Branch))
                |> Set.ofList
            let stillPending = Set.intersect model.DeletedBranches serverBranches
            let repos =
                response.Repos
                |> List.map (fun r ->
                    { RepoId = r.RepoId
                      Name = r.RootFolderName
                      Worktrees = sortWorktrees model.SortMode r.Worktrees
                      IsReady = r.IsReady
                      IsCollapsed = existingCollapse |> Map.tryFind r.RepoId |> Option.defaultValue false
                      Provider = r.Provider })
                |> filterDeletedBranches stillPending
            { model with
                Repos = repos
                IsLoading = false
                HasError = false
                SchedulerEvents = response.SchedulerEvents
                LatestByCategory = response.LatestByCategory
                AppVersion = Some response.AppVersion
                EyeDirection = randomEyeDirection ()
                DeletedBranches = stillPending }
            |> (fun m -> { m with FocusedElement = adjustFocusForVisibility m.Repos m.FocusedElement }),
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
            Repos = model.Repos |> List.map (fun r -> { r with Worktrees = sortWorktrees newSort r.Worktrees }) },
        Cmd.none

    | ToggleCompact ->
        { model with IsCompact = not model.IsCompact }, Cmd.none

    | ToggleCollapse repoId ->
        let isCollapsing =
            model.Repos
            |> List.tryFind (fun r -> r.RepoId = repoId)
            |> Option.map (fun r -> not r.IsCollapsed)
            |> Option.defaultValue false
        let updatedModel =
            { model with
                Repos = model.Repos |> List.map (fun r ->
                    if r.RepoId = repoId then { r with IsCollapsed = not r.IsCollapsed }
                    else r) }
        let focusAdjusted =
            if isCollapsing then adjustFocusAfterCollapse repoId updatedModel.FocusedElement
            else updatedModel.FocusedElement
        { updatedModel with FocusedElement = focusAdjusted },
        Cmd.none

    | OpenTerminal path ->
        model, Cmd.OfAsync.attempt worktreeApi.openTerminal path (fun _ -> Tick)

    | Tick ->
        model, Cmd.batch [ fetchWorktrees (); fetchSyncStatus () ]

    | StartSync (branch, key) ->
        let syntheticEvent =
            { Source = "Sync"
              Message = "Sync starting"
              Timestamp = System.DateTimeOffset.Now
              Status = Some StepStatus.Running
              Duration = None }
        let updatedEvents =
            model.BranchEvents
            |> Map.add key [ syntheticEvent ]
        { model with
            SyncPending = model.SyncPending |> Set.add key
            BranchEvents = updatedEvents },
        Cmd.OfAsync.perform worktreeApi.startSync branch (fun r -> SyncStarted (key, r))

    | SyncStarted (key, Ok _) ->
        { model with SyncPending = model.SyncPending |> Set.remove key }, fetchSyncStatus ()

    | SyncStarted (key, Error _) ->
        { model with
            SyncPending = model.SyncPending |> Set.remove key
            BranchEvents = model.BranchEvents |> Map.remove key },
        Cmd.none

    | SyncStatusUpdate events ->
        { model with BranchEvents = events }, Cmd.none

    | CancelSync branch ->
        model, Cmd.OfAsync.attempt worktreeApi.cancelSync branch (fun _ -> Tick)

    | SyncTick ->
        model, fetchSyncStatus ()

    | DeleteWorktree branch ->
        let updatedRepos =
            model.Repos
            |> List.map (fun r ->
                { r with Worktrees = r.Worktrees |> List.filter (fun wt -> wt.Branch <> branch) })
        let updatedModel =
            { model with
                Repos = updatedRepos
                DeletedBranches = model.DeletedBranches |> Set.add branch }
        { updatedModel with FocusedElement = adjustFocusForVisibility updatedModel.Repos updatedModel.FocusedElement },
        Cmd.OfAsync.perform worktreeApi.deleteWorktree branch DeleteCompleted

    | DeleteCompleted (Ok _) ->
        model, fetchWorktrees ()

    | DeleteCompleted (Error _) ->
        { model with DeletedBranches = Set.empty }, fetchWorktrees ()

    | FocusSession path ->
        model, Cmd.OfAsync.perform worktreeApi.focusSession path SessionResult

    | OpenNewTab path ->
        model, Cmd.OfAsync.perform worktreeApi.openNewTab path SessionResult

    | SessionResult _ ->
        model, fetchWorktrees ()

    | SetFocus target ->
        { model with FocusedElement = target }, Cmd.none

    | KeyPressed (key, hasModifier) ->
        let scrollToFocus oldFocus newFocus =
            let useCenter = isLargeJump model.Repos oldFocus newFocus
            Cmd.ofEffect (fun _ -> scrollFocusedIntoView useCenter newFocus)
        match key with
        | "ArrowDown" | "ArrowUp" | "ArrowLeft" | "ArrowRight" ->
            let cols = getColumnCount ()
            let newFocus, navAction = navigateSpatial key cols model.Repos model.FocusedElement
            let actionCmd =
                match navAction with
                | NoAction -> Cmd.none
                | CollapseRepo repoId -> Cmd.ofMsg (ToggleCollapse repoId)
                | ExpandRepo repoId -> Cmd.ofMsg (ToggleCollapse repoId)
            { model with FocusedElement = newFocus },
            Cmd.batch [ actionCmd; scrollToFocus model.FocusedElement newFocus ]
        | "Home" ->
            let newFocus = navigateToFirst model.Repos
            { model with FocusedElement = newFocus },
            scrollToFocus model.FocusedElement newFocus
        | "End" ->
            let newFocus = navigateToLast model.Repos
            { model with FocusedElement = newFocus },
            scrollToFocus model.FocusedElement newFocus
        | _ when hasModifier ->
            model, Cmd.none
        | _ ->
            match model.FocusedElement with
            | None -> model, Cmd.none
            | Some focused ->
                match keyBinding focused key model with
                | Some action -> model, Cmd.ofMsg action
                | None -> model, Cmd.none

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

let ctClassName =
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
    let ct = ctClassName wt.CodingTool
    let session = if wt.HasActiveSession then " has-session" else ""
    if isMerged wt then $"wt-card ct-{ct} merged{session}" else $"wt-card ct-{ct}{session}"

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

let private providerDisplayName (provider: CodingToolProvider option) =
    match provider with
    | Some Claude -> "Claude"
    | Some Copilot -> "Copilot"
    | None -> "Coding tool"

let noFocusProps = [
    prop.tabIndex -1
    prop.onMouseDown (fun e -> e.preventDefault())
    prop.onKeyDown (fun e -> if e.key = "Enter" || e.key = " " then e.preventDefault())
]

let syncButton dispatch (wt: WorktreeStatus) (branchEvents: CardEvent list) (isPending: bool) (scopedKey: string) =
    if isPending then
        Html.button [
            prop.className "sync-starting-btn"
            prop.disabled true
            yield! noFocusProps
            prop.text "Sync starting"
        ]
    else
        let syncing = isBranchSyncing branchEvents
        let claudeBlocked = wt.CodingTool = Working || wt.CodingTool = WaitingForUser
        let disabled = syncing || claudeBlocked
        if syncing then
            Html.button [
                prop.className "sync-cancel-btn"
                yield! noFocusProps
                prop.onClick (fun e -> e.stopPropagation(); dispatch (CancelSync wt.Branch))
                prop.text "Cancel"
            ]
        else
            Html.button [
                prop.className (if disabled then "sync-btn disabled" else "sync-btn")
                prop.disabled disabled
                yield! noFocusProps
                prop.onClick (fun e -> e.stopPropagation(); dispatch (StartSync (wt.Branch, scopedKey)))
                prop.title (if claudeBlocked then $"{providerDisplayName wt.CodingToolProvider} is active" else "Sync with main")
                prop.text "Sync"
            ]

let mainBehindWithSync dispatch (wt: WorktreeStatus) (branchEvents: CardEvent list) (isPending: bool) (scopedKey: string) =
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
                else syncButton dispatch wt branchEvents isPending scopedKey
            Html.span [
                prop.className "git-commit-msg"
                prop.children [
                    Html.text wt.LastCommitMessage
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime wt.LastCommitTime) ]
                ]
            ]
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
    [ "WorktreeList"; "GitRefresh"; "BeadsRefresh"; "CodingToolRefresh"; "PrFetch"; "GitFetch" ]

let categoryDisplayName =
    function
    | "WorktreeList"   -> "Worktree \u2630"
    | "GitRefresh"     -> "Git \u21BB"
    | "BeadsRefresh"   -> "Beads \u21BB"
    | "CodingToolRefresh" -> "Agent \u21BB"
    | "PrFetch"        -> "PR \u2913"
    | "GitFetch"       -> "Git \u2913"
    | other            -> other

let private lastSepIndex (s: string) =
    max (s.LastIndexOf('/')) (s.LastIndexOf('\\'))

let commonPathPrefix (paths: string list) =
    match paths with
    | [] -> ""
    | [ single ] ->
        match lastSepIndex single with
        | -1 -> ""
        | i -> single[..i]
    | first :: rest ->
        let prefixLen =
            rest |> List.fold (fun len path ->
                let maxLen = min len path.Length
                let rec findMismatch i =
                    if i >= maxLen then maxLen
                    elif System.Char.ToLowerInvariant first[i] = System.Char.ToLowerInvariant path[i] then findMismatch (i + 1)
                    else i
                findMismatch 0) first.Length
        let prefix = first[..prefixLen - 1]
        match lastSepIndex prefix with
        | -1 -> ""
        | i -> prefix[..i]

let stripPrefix (prefix: string) (target: string) =
    if prefix.Length > 0 && target.Length >= prefix.Length
       && target[..prefix.Length - 1].ToLowerInvariant() = prefix.ToLowerInvariant()
    then target[prefix.Length..]
    else target

let statusOverviewRow (prefix: string) (latestBySource: Map<string, CardEvent>) (category: string) =
    let label = categoryDisplayName category
    match Map.tryFind category latestBySource with
    | None ->
        Html.div [
            prop.className "status-row pending"
            prop.children [
                Html.span [ prop.className "status-category"; prop.text label ]
                Html.span [ prop.className "status-target" ]
                Html.span [ prop.className "status-duration" ]
                Html.span [ prop.className "status-time" ]
                Html.span [ prop.className "status-badge pending"; prop.text "pending" ]
            ]
        ]
    | Some evt ->
        let target = extractBranchName evt.Message |> Option.defaultValue "" |> stripPrefix prefix
        Html.div [
            prop.className "status-row"
            prop.children [
                Html.span [ prop.className "status-category"; prop.text label ]
                Html.span [ prop.className "status-target"; prop.text target ]
                match evt.Duration with
                | Some d -> Html.span [ prop.className "status-duration"; prop.text $"%.1f{d.TotalSeconds}s" ]
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

let pinnedErrorEntry (prefix: string) (evt: CardEvent) =
    Html.div [
        prop.className "event-entry pinned-error"
        prop.children [
            Html.span [ prop.className "event-time"; prop.text (relativeEventTime evt.Timestamp) ]
            Html.span [ prop.className "event-source"; prop.text evt.Source ]
            Html.span [ prop.className "event-message"; prop.text (stripPrefix prefix evt.Message) ]
            match evt.Status with
            | Some _ ->
                Html.span [
                    prop.className (stepStatusClassName evt.Status)
                    prop.text (stepStatusText evt.Status)
                ]
            | None -> Html.none
        ]
    ]

let schedulerFooter (repos: RepoModel list) (events: CardEvent list) (latestByCategory: Map<string, CardEvent>) =
    let prefix = repos |> List.map (fun r -> RepoId.value r.RepoId) |> commonPathPrefix
    let errors = pinnedErrors events
    Html.div [
        prop.className "scheduler-footer"
        prop.children [
            match errors with
            | [] -> Html.none
            | errs ->
                Html.div [
                    prop.className "pinned-errors"
                    prop.children (errs |> List.map (pinnedErrorEntry prefix))
                ]
            Html.div [
                prop.className "status-overview"
                prop.children (knownCategories |> List.map (statusOverviewRow prefix latestByCategory))
            ]
        ]
    ]

let abbreviatePipelineName (repoName: string) (name: string) =
    let stripped =
        if name.Length >= repoName.Length && name.StartsWith(repoName, System.StringComparison.OrdinalIgnoreCase)
        then name[repoName.Length..].TrimStart()
        else name
    if stripped.EndsWith(" - pr", System.StringComparison.OrdinalIgnoreCase)
    then stripped[..stripped.Length-6].TrimEnd()
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
    let action = if wt.HasActiveSession then FocusSession wt.Path else OpenTerminal wt.Path
    let title = if wt.HasActiveSession then "Focus session window" else "Open terminal"
    Html.button [
        prop.className "terminal-btn"
        prop.title title
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); dispatch action)
        prop.text ">"
    ]

let newTabButton dispatch (wt: WorktreeStatus) =
    Html.button [
        prop.className "new-tab-btn"
        prop.title "Open new tab in tracked window"
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); dispatch (OpenNewTab wt.Path))
        prop.text "+"
    ]

let deleteButton dispatch (wt: WorktreeStatus) =
    Html.button [
        prop.className "delete-btn"
        prop.title "Remove worktree"
        yield! noFocusProps
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
            match pr.Comments with
            | WithResolution (unresolved, total) when total > 0 ->
                Html.span [
                    prop.className (if unresolved = 0 then "thread-badge dimmed" else "thread-badge")
                    prop.text ($"{unresolved}/{total} threads")
                ]
            | CountOnly total ->
                Html.span [
                    prop.className (if total = 0 then "thread-badge dimmed" else "thread-badge")
                    prop.text ($"{total} comments")
                ]
            | _ -> ()
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

let compactWorktreeCard dispatch (repoName: string) (isFocused: bool) (wt: WorktreeStatus) =
    let baseClass = cardClassName wt + " compact"
    let className = if isFocused then baseClass + " focused" else baseClass
    Html.div [
        prop.key wt.Branch
        prop.className className
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}") ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                    workMetricsView wt.WorkMetrics
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime wt.LastCommitTime) ]
                    terminalButton dispatch wt
                    if wt.HasActiveSession then newTabButton dispatch wt
                    deleteButton dispatch wt
                ]
            ]
            Html.div [
                prop.className "compact-detail"
                prop.children [
                    if beadsTotal wt.Beads > 0 then beadsCounts "beads-inline" wt.Beads
                    mainBehindIndicator wt.MainBehindCount
                    prSection repoName wt
                ]
            ]
        ]
    ]

let worktreeCard dispatch (repoName: string) (branchEvents: CardEvent list) (isPending: bool) (scopedKey: string) (isFocused: bool) (wt: WorktreeStatus) =
    let baseClass = cardClassName wt
    let className = if isFocused then baseClass + " focused" else baseClass
    Html.div [
        prop.key wt.Branch
        prop.className className
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}") ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                    workMetricsView wt.WorkMetrics
                    terminalButton dispatch wt
                    if wt.HasActiveSession then newTabButton dispatch wt
                    deleteButton dispatch wt
                ]
            ]

            match wt.LastUserMessage with
            | Some prompt ->
                Html.div [
                    prop.className "commit-line user-prompt"
                    prop.children [
                        Html.text prompt
                    ]
                ]
            | None -> ()

            if beadsTotal wt.Beads > 0 then
                Html.div [
                    prop.className "beads-row"
                    prop.children [
                        beadsCounts "beads-counts" wt.Beads
                        beadsProgressBar wt.Beads
                    ]
                ]

            mainBehindWithSync dispatch wt branchEvents isPending scopedKey

            prRow repoName wt

            eventLog branchEvents
        ]
    ]

let renderCard dispatch isCompact (focusedElement: FocusTarget option) repoId repoName (branchEvents: Map<string, CardEvent list>) (syncPending: Set<string>) (wt: WorktreeStatus) =
    let scopedKey = $"{repoId}/{wt.Branch}"
    let events = branchEvents |> Map.tryFind scopedKey |> Option.defaultValue []
    let isPending = syncPending |> Set.contains scopedKey
    let isFocused = focusedElement = Some (Card scopedKey)
    if isCompact then compactWorktreeCard dispatch repoName isFocused wt
    else worktreeCard dispatch repoName events isPending scopedKey isFocused wt

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

let viewEyeOpen (dx: float, dy: float) =
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

let viewEyeRolledBack =
    Svg.svg [
        svg.className "eye-logo"
        svg.viewBox (-2, -2, 44, 24)
        svg.children [
            Svg.defs [
                Svg.clipPath [
                    svg.id "eye-shape"
                    svg.children [
                        Svg.path [
                            svg.d "M2 10 Q10 0 20 0 Q30 0 38 10 Q30 20 20 20 Q10 20 2 10 Z"
                        ]
                    ]
                ]
            ]
            Svg.path [
                svg.d "M2 10 Q10 0 20 0 Q30 0 38 10 Q30 20 20 20 Q10 20 2 10 Z"
                svg.fill "#e8e8e8"
                svg.stroke "#56b6c2"
                svg.strokeWidth 2.5
            ]
            Svg.g [
                svg.custom ("clipPath", "url(#eye-shape)")
                svg.children [
                    Svg.g [
                        svg.custom ("transform", "translate(0, -9)")
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
                                svg.fill "#888"
                            ]
                            Svg.circle [
                                svg.cx 20
                                svg.cy 10
                                svg.r 3
                                svg.fill "#1a1b2e"
                            ]
                        ]
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

let viewEyeClosed =
    Svg.svg [
        svg.className "eye-logo eye-closed"
        svg.viewBox (-2, -2, 44, 24)
        svg.children [
            Svg.path [
                svg.d "M2 10 Q10 4 20 4 Q30 4 38 10 Q30 16 20 16 Q10 16 2 10 Z"
                svg.fill "#e8e8e8"
                svg.stroke "#56b6c2"
                svg.strokeWidth 2.5
            ]
            Svg.line [
                svg.x1 4
                svg.y1 10
                svg.x2 36
                svg.y2 10
                svg.stroke "#56b6c2"
                svg.strokeWidth 2.0
            ]
        ]
    ]

let hasAnyWorking (repos: RepoModel list) =
    repos |> List.exists (fun r -> r.Worktrees |> List.exists (fun wt -> wt.CodingTool = Working))

let anyRepoReady (repos: RepoModel list) =
    repos |> List.exists _.IsReady

let allWorktreesEmpty (repos: RepoModel list) =
    repos |> List.forall _.Worktrees.IsEmpty

let providerIcon (provider: RepoProvider option) =
    match provider with
    | None -> Html.none
    | Some GitHubProvider ->
        Svg.svg [
            svg.className "provider-icon"
            svg.viewBox (0, 0, 24, 24)
            svg.children [
                Svg.path [
                    svg.d "M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"
                    svg.fill "currentColor"
                ]
            ]
        ]
    | Some AzDoProvider ->
        Svg.svg [
            svg.className "provider-icon"
            svg.viewBox (0, 0, 18, 18)
            svg.children [
                Svg.path [
                    svg.d "M17 4v9.74l-4 3.28-6.2-2.26V17l-3.51-4.59 10.23.8V4.44zm-3.41.49L7.85 1v2.29L2.58 4.84 1 6.87v4.61l2.26 1V6.57z"
                    svg.fill "currentColor"
                ]
            ]
        ]

let repoSectionHeader dispatch (focusedElement: FocusTarget option) (repo: RepoModel) =
    let arrow = if repo.IsCollapsed then "\u25B6" else "\u25BC"
    let isFocused = focusedElement = Some (RepoHeader repo.RepoId)
    let baseClass = if repo.IsCollapsed then "repo-header collapsed" else "repo-header"
    let className = if isFocused then baseClass + " focused" else baseClass
    Html.div [
        prop.className className
        prop.onClick (fun _ -> dispatch (ToggleCollapse repo.RepoId))
        prop.children [
            Html.span [ prop.className "collapse-arrow"; prop.text arrow ]
            Html.span [ prop.className "repo-name"; prop.text repo.Name ]
            providerIcon repo.Provider
            if repo.IsCollapsed then
                Html.span [
                    prop.className "repo-ct-dots"
                    prop.children (
                        repo.Worktrees
                        |> List.map (fun wt ->
                            Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}") ]))
                ]
        ]
    ]

let repoSection dispatch isCompact (focusedElement: FocusTarget option) (branchEvents: Map<string, CardEvent list>) (syncPending: Set<string>) (repo: RepoModel) =
    Html.div [
        prop.key (RepoId.value repo.RepoId)
        prop.className "repo-section"
        prop.children [
            repoSectionHeader dispatch focusedElement repo
            if not repo.IsCollapsed then
                if not repo.IsReady && repo.Worktrees.IsEmpty then
                    skeletonGrid ()
                else
                    Html.div [
                        prop.className "card-grid"
                        prop.children (repo.Worktrees |> List.map (renderCard dispatch isCompact focusedElement (RepoId.value repo.RepoId) repo.Name branchEvents syncPending))
                    ]
        ]
    ]

let view model dispatch =
    Html.div [
        prop.className "dashboard"
        prop.tabIndex 0
        prop.autoFocus true
        prop.onKeyDown (fun e ->
            match e.key with
            | "ArrowDown" | "ArrowUp" | "ArrowLeft" | "ArrowRight" | "Home" | "End" ->
                e.preventDefault()
                dispatch (KeyPressed (e.key, false))
            | key ->
                let hasModifier = e.ctrlKey || e.altKey || e.metaKey
                dispatch (KeyPressed (key, hasModifier)))
        prop.children [
            Html.div [
                prop.className "dashboard-header"
                prop.children [
                    Html.div [
                        prop.className "header-top"
                        prop.children [
                            Html.h1 [
                                prop.children [
                                    if model.HasError then viewEyeRolledBack
                                    elif hasAnyWorking model.Repos then viewEyeOpen model.EyeDirection
                                    else viewEyeClosed
                                ]
                            ]
                            Html.div [
                                prop.className "header-controls"
                                prop.children [
                                    Html.button [
                                        prop.className "ctrl-btn"
                                        yield! noFocusProps
                                        prop.onClick (fun _ -> dispatch ToggleSort)
                                        prop.text ($"Sort: {sortLabel model.SortMode}")
                                    ]
                                    Html.button [
                                        prop.className (if model.IsCompact then "ctrl-btn active" else "ctrl-btn")
                                        yield! noFocusProps
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
                            if not (anyRepoReady model.Repos) && allWorktreesEmpty model.Repos then
                                Html.span "Waiting for first refresh..."
                        ]
                    ]
                ]
            ]

            if not (anyRepoReady model.Repos) && allWorktreesEmpty model.Repos then
                skeletonGrid ()
            else
                Html.div [
                    prop.className "repo-list"
                    prop.children (model.Repos |> List.map (repoSection dispatch model.IsCompact model.FocusedElement model.BranchEvents model.SyncPending))
                ]

            schedulerFooter model.Repos model.SchedulerEvents model.LatestByCategory
        ]
    ]

open Elmish.React

Program.mkProgram init update view
|> Program.withSubscription pollingSubscription
|> Program.withReactSynchronous "app"
|> Program.run
