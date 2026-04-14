module App

open Shared
open Shared.EventUtils
open Navigation
open Elmish
open Feliz
open Fable.Remoting.Client
open Browser
open Fable.Core.JsInterop
open ActionButtons

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
      EditorName: string
      EyeDirection: float * float
      FocusedElement: FocusTarget option
      CreateModal: CreateWorktreeModal.ModalState
      ConfirmModal: ConfirmModal.ConfirmModal
      DeletedPaths: Set<string>
      DeployBranch: string option
      SystemMetrics: SystemMetrics option
      ActionCooldowns: Set<WorktreePath> }

type Msg =
    | DataLoaded of DashboardResponse
    | DataFailed of exn
    | ToggleSort
    | ToggleCompact
    | ToggleCollapse of repoId: RepoId
    | Tick
    | OpenTerminal of WorktreePath
    | OpenEditor of WorktreePath
    | StartSync of path: WorktreePath * scopedKey: string
    | SyncStarted of key: string * Result<unit, string>
    | SyncStatusUpdate of Map<string, CardEvent list>
    | CancelSync of WorktreePath
    | SyncTick
    | ConfirmDeleteWorktree of scopedKey: string
    | ConfirmArchiveWorktree of scopedKey: string
    | ConfirmMsg of ConfirmModal.Msg
    | SessionKilledForDelete of path: WorktreePath
    | SessionKilledForArchive of path: WorktreePath
    | DeleteCompleted of Result<unit, string>
    | FocusSession of path: WorktreePath
    | OpenNewTab of path: WorktreePath
    | SessionResult of Result<unit, string>
    | KeyPressed of key: string * hasModifier: bool
    | SetFocus of FocusTarget option
    | ArchiveMsg of ArchiveViews.Msg
    | LaunchAction of path: WorktreePath * action: ActionKind
    | LaunchActionResult of Result<unit, string>
    | ClearActionCooldown of WorktreePath
    | ModalMsg of CreateWorktreeModal.Msg

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
      EditorName = "VS Code"
      EyeDirection = (0.0, 0.0)
      FocusedElement = None
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      DeployBranch = None
      SystemMetrics = None
      ActionCooldowns = Set.empty },
    Cmd.batch [ fetchWorktrees (); fetchSyncStatus () ]

let rng = System.Random()

let randomEyeDirection () =
    let dx = rng.NextDouble() * 3.0 - 1.5
    let dy = rng.NextDouble() * 2.0 - 1.0
    (dx, dy)

let filterDeletedPaths (deleted: Set<string>) (repos: RepoModel list) =
    if Set.isEmpty deleted then repos
    else
        repos
        |> List.map (fun r ->
            { r with Worktrees = r.Worktrees |> List.filter (fun wt -> not (Set.contains (WorktreePath.value wt.Path) deleted)) })

let findWorktree (scopedKey: string) (model: Model) =
    let parts = scopedKey.Split('/', 2)
    if parts.Length < 2 then None
    else
        let repoId, branch = parts[0], parts[1]
        model.Repos
        |> List.tryFind (fun r -> RepoId.value r.RepoId = repoId)
        |> Option.bind (fun r -> r.Worktrees |> List.tryFind (fun wt -> wt.Branch = branch))

let removeFromRepos (path: WorktreePath) (repos: RepoModel list) =
    let pathStr = WorktreePath.value path
    repos
    |> List.map (fun r ->
        { r with Worktrees = r.Worktrees |> List.filter (fun wt -> WorktreePath.value wt.Path <> pathStr) })

let markDeleted (path: WorktreePath) (deletedPaths: Set<string>) =
    deletedPaths |> Set.add (WorktreePath.value path)

let removeWorktreeByPath (path: WorktreePath) (model: Model) =
    let updatedRepos = removeFromRepos path model.Repos
    let updatedModel =
        { model with
            Repos = updatedRepos
            DeletedPaths = markDeleted path model.DeletedPaths }
    { updatedModel with FocusedElement = adjustFocusForVisibility updatedModel.Repos updatedModel.FocusedElement }

let terminalAction (wt: WorktreeStatus) =
    if wt.HasActiveSession then FocusSession wt.Path else OpenTerminal wt.Path

let keyBinding (focused: FocusTarget) (key: string) (model: Model) : Msg option =
    match focused, key with
    | Card scopedKey, "Enter" -> findWorktree scopedKey model |> Option.map terminalAction
    | Card scopedKey, "s" -> findWorktree scopedKey model |> Option.map (fun wt -> StartSync (wt.Path, scopedKey))
    | Card scopedKey, "+" -> findWorktree scopedKey model |> Option.bind (fun wt -> if wt.HasActiveSession then Some (OpenNewTab wt.Path) else None)
    | Card scopedKey, "e" -> findWorktree scopedKey model |> Option.map (fun wt -> OpenEditor wt.Path)
    | Card scopedKey, "a" -> findWorktree scopedKey model |> Option.map (fun _ -> ConfirmArchiveWorktree scopedKey)
    | Card scopedKey, "Delete" -> findWorktree scopedKey model |> Option.bind (fun wt -> if not wt.IsMainWorktree then Some (ConfirmDeleteWorktree scopedKey) else None)
    | RepoHeader repoId, "Enter" -> Some (ToggleCollapse repoId)
    | RepoHeader repoId, "+" -> Some (ModalMsg (CreateWorktreeModal.OpenCreateWorktree repoId))
    | _ -> None

let update msg model =
    match msg with
    | DataLoaded response ->
        match model.AppVersion with
        | Some v when v <> response.AppVersion ->
            model, Cmd.ofEffect (fun _ -> Dom.window.location.reload ())
        | _ ->
            let isFirstLoad = List.isEmpty model.Repos
            let existingCollapse =
                model.Repos
                |> List.map (fun r -> r.RepoId, r.IsCollapsed)
                |> Map.ofList
            let serverPaths =
                response.Repos
                |> List.collect (fun r -> r.Worktrees |> List.map (fun wt -> WorktreePath.value wt.Path))
                |> Set.ofList
            let stillPending = Set.intersect model.DeletedPaths serverPaths
            let repos =
                response.Repos
                |> List.map (fun r ->
                    let active, archived = r.Worktrees |> List.partition (fun wt -> not wt.IsArchived)
                    { RepoId = r.RepoId
                      Name = r.RootFolderName
                      Worktrees = sortWorktrees model.SortMode active
                      ArchivedWorktrees = archived
                      IsReady = r.IsReady
                      IsCollapsed =
                          if isFirstLoad then response.CollapsedRepos |> Set.contains (RepoId.value r.RepoId)
                          else existingCollapse |> Map.tryFind r.RepoId |> Option.defaultValue false
                      Provider = r.Provider })
                |> filterDeletedPaths stillPending
            { model with
                Repos = repos
                IsLoading = false
                HasError = false
                SchedulerEvents = response.SchedulerEvents
                LatestByCategory = response.LatestByCategory
                AppVersion = Some response.AppVersion
                EditorName = response.EditorName
                EyeDirection = randomEyeDirection ()
                DeletedPaths = stillPending
                DeployBranch = response.DeployBranch
                SystemMetrics = response.SystemMetrics }
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
        let collapsedRepos =
            updatedModel.Repos
            |> List.filter _.IsCollapsed
            |> List.map (_.RepoId >> RepoId.value)
        { updatedModel with FocusedElement = focusAdjusted },
        Cmd.OfAsync.attempt worktreeApi.saveCollapsedRepos collapsedRepos (fun _ -> Tick)

    | OpenTerminal path ->
        model, Cmd.OfAsync.attempt worktreeApi.openTerminal path (fun _ -> Tick)
    | OpenEditor path ->
        model, Cmd.OfAsync.attempt worktreeApi.openEditor path (fun _ -> Tick)

    | Tick ->
        model, Cmd.batch [ fetchWorktrees (); fetchSyncStatus () ]

    | StartSync (path, key) ->
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
        Cmd.OfAsync.perform worktreeApi.startSync path (fun r -> SyncStarted (key, r))

    | SyncStarted (key, Ok _) ->
        { model with SyncPending = model.SyncPending |> Set.remove key }, fetchSyncStatus ()

    | SyncStarted (key, Error _) ->
        { model with
            SyncPending = model.SyncPending |> Set.remove key
            BranchEvents = model.BranchEvents |> Map.remove key },
        Cmd.none

    | SyncStatusUpdate events ->
        { model with BranchEvents = events }, Cmd.none

    | CancelSync path ->
        model, Cmd.OfAsync.attempt worktreeApi.cancelSync path (fun _ -> Tick)

    | SyncTick ->
        model, fetchSyncStatus ()

    | ConfirmDeleteWorktree scopedKey ->
        match findWorktree scopedKey model with
        | Some wt ->
            { model with ConfirmModal = ConfirmModal.ConfirmDelete (wt.Branch, wt.Path, wt.HasActiveSession) }, Cmd.none
        | None -> model, Cmd.none

    | ConfirmArchiveWorktree scopedKey ->
        match findWorktree scopedKey model with
        | Some wt when wt.HasActiveSession ->
            { model with ConfirmModal = ConfirmModal.ConfirmArchive (wt.Branch, wt.Path) }, Cmd.none
        | Some wt ->
            model, Cmd.ofMsg (ArchiveMsg (ArchiveViews.Archive wt.Path))
        | None -> model, Cmd.none

    | ConfirmMsg confirmMsg ->
        let confirmModal, action = ConfirmModal.update confirmMsg
        let model = { model with ConfirmModal = confirmModal }
        match action with
        | ConfirmModal.NoAction ->
            model,
            Cmd.ofEffect (fun _ ->
                Dom.document.querySelector ".dashboard"
                |> Option.ofObj
                |> Option.iter (fun el -> el?focus()))
        | ConfirmModal.Delete path ->
            removeWorktreeByPath path model,
            Cmd.OfAsync.perform worktreeApi.deleteWorktree path DeleteCompleted
        | ConfirmModal.DeleteAfterKillSession path ->
            model, Cmd.OfAsync.perform worktreeApi.killSession path (function
                | Ok () -> SessionKilledForDelete path
                | Error _ -> Tick)
        | ConfirmModal.Archive path ->
            model, Cmd.ofMsg (ArchiveMsg (ArchiveViews.Archive path))
        | ConfirmModal.ArchiveAfterKillSession path ->
            model, Cmd.OfAsync.perform worktreeApi.killSession path (function
                | Ok () -> SessionKilledForArchive path
                | Error _ -> Tick)

    | DeleteCompleted (Ok _) ->
        model, fetchWorktrees ()

    | DeleteCompleted (Error _) ->
        { model with DeletedPaths = Set.empty }, fetchWorktrees ()

    | SessionKilledForDelete path ->
        removeWorktreeByPath path model,
        Cmd.OfAsync.perform worktreeApi.deleteWorktree path DeleteCompleted

    | SessionKilledForArchive path ->
        model, Cmd.ofMsg (ArchiveMsg (ArchiveViews.Archive path))

    | FocusSession path ->
        model, Cmd.OfAsync.perform worktreeApi.focusSession path SessionResult

    | OpenNewTab path ->
        model, Cmd.OfAsync.perform worktreeApi.openNewTab path SessionResult

    | SessionResult _ ->
        model, fetchWorktrees ()

    | LaunchAction (path, action) ->
        if model.ActionCooldowns.Contains path then
            model, Cmd.none
        else
            let clearAfter =
                Cmd.ofEffect (fun dispatch ->
                    Fable.Core.JS.setTimeout (fun () -> dispatch (ClearActionCooldown path)) 10_000 |> ignore)
            { model with ActionCooldowns = model.ActionCooldowns.Add path },
            Cmd.batch [
                Cmd.OfAsync.perform worktreeApi.launchAction { Path = path; Action = action } LaunchActionResult
                clearAfter
            ]

    | LaunchActionResult _ ->
        model, fetchWorktrees ()

    | ClearActionCooldown path ->
        { model with ActionCooldowns = model.ActionCooldowns.Remove path }, Cmd.none

    | SetFocus target ->
        { model with FocusedElement = target }, Cmd.none

    | ArchiveMsg archiveMsg ->
        let result, archiveCmd = ArchiveViews.update (lazy worktreeApi) archiveMsg
        let refreshCmd = if result.RefreshWorktrees then fetchWorktrees () else Cmd.none
        model, Cmd.batch [ Cmd.map ArchiveMsg archiveCmd; refreshCmd ]

    | ModalMsg modalMsg ->
        let result, modalCmd = CreateWorktreeModal.update (lazy worktreeApi) modalMsg model.CreateModal
        let focus = result.RestoredFocus |> Option.orElse model.FocusedElement
        let refreshCmd = if result.RefreshWorktrees then fetchWorktrees () else Cmd.none
        { model with CreateModal = result.Modal; FocusedElement = focus },
        Cmd.batch [ Cmd.map ModalMsg modalCmd; refreshCmd ]

    | KeyPressed (key, hasModifier) ->
        let scrollToFocus hint newFocus =
            Cmd.ofEffect (fun _ -> scrollFocusedIntoView hint newFocus)
        if model.ConfirmModal <> ConfirmModal.NoConfirm then
            match key with
            | "Escape" ->
                { model with ConfirmModal = ConfirmModal.NoConfirm },
                Cmd.ofEffect (fun _ ->
                    Dom.document.querySelector ".dashboard"
                    |> Option.ofObj
                    |> Option.iter (fun el -> el?focus()))
            | _ -> model, Cmd.none
        elif CreateWorktreeModal.isOpen model.CreateModal then
            match key with
            | "Escape" ->
                let restoredFocus =
                    CreateWorktreeModal.repoId model.CreateModal
                    |> Option.map RepoHeader
                    |> Option.orElse model.FocusedElement
                { model with CreateModal = CreateWorktreeModal.Closed; FocusedElement = restoredFocus },
                Cmd.none
            | _ -> model, Cmd.none
        else
        match key with
        | "ArrowDown" | "ArrowUp" | "ArrowLeft" | "ArrowRight" ->
            let cols = getColumnCount ()
            let newFocus, navAction, scrollHint = navigateSpatial key cols model.Repos model.FocusedElement
            let actionCmd =
                match navAction with
                | NoAction -> Cmd.none
                | CollapseRepo repoId -> Cmd.ofMsg (ToggleCollapse repoId)
                | ExpandRepo repoId -> Cmd.ofMsg (ToggleCollapse repoId)
            { model with FocusedElement = newFocus },
            Cmd.batch [ actionCmd; scrollToFocus scrollHint newFocus ]
        | "Home" ->
            let newFocus = navigateToFirst model.Repos
            { model with FocusedElement = newFocus },
            scrollToFocus ScrollToTop newFocus
        | "End" ->
            let newFocus = navigateToLast model.Repos
            { model with FocusedElement = newFocus },
            scrollToFocus ScrollToBottom newFocus
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

let relativeTime = ArchiveViews.relativeTime

let ctClassName =
    function
    | Working        -> "working"
    | WaitingForUser -> "waiting"
    | Done           -> "done"
    | Idle           -> "idle"

let ctTooltip =
    function
    | Working        -> "Working"
    | WaitingForUser -> "Waiting for user"
    | Done           -> "Done"
    | Idle           -> "Idle"

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
        let codingToolBusy = wt.CodingTool = Working || wt.CodingTool = WaitingForUser
        let disabled = syncing || codingToolBusy
        if syncing then
            Html.button [
                prop.className "sync-cancel-btn"
                yield! noFocusProps
                prop.onClick (fun e -> e.stopPropagation(); dispatch (CancelSync wt.Path))
                prop.text "Cancel"
            ]
        else
            Html.button [
                prop.className (if disabled then "sync-btn disabled" else "sync-btn")
                prop.disabled disabled
                yield! noFocusProps
                prop.onClick (fun e -> e.stopPropagation(); dispatch (StartSync (wt.Path, scopedKey)))
                prop.title (if codingToolBusy then $"{providerDisplayName wt.CodingToolProvider} is active" else "Sync with main (S)")
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
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime System.DateTimeOffset.Now wt.LastCommitTime) ]
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
    | Some StepStatus.NotConfigured -> "event-status not-configured"
    | Some StepStatus.Pending -> "event-status"
    | None -> "event-status"

let stepStatusText (status: StepStatus option) =
    match status with
    | Some StepStatus.Running -> "running"
    | Some StepStatus.Succeeded -> "success"
    | Some (StepStatus.Failed msg) -> match msg with "" -> "failed" | _ -> $"failed: {msg}"
    | Some StepStatus.Cancelled -> "cancelled"
    | Some StepStatus.NotConfigured -> "not configured"
    | _ -> ""

let relativeEventTime (dt: System.DateTimeOffset) =
    let diff = System.DateTimeOffset.Now - dt
    match diff with
    | d when d.TotalSeconds < 60.0 -> $"{int d.TotalSeconds |> max 0}s ago"
    | d when d.TotalMinutes < 60.0 -> $"{int d.TotalMinutes}m ago"
    | d when d.TotalHours < 24.0 -> $"{int d.TotalHours}h ago"
    | d -> $"{int d.TotalDays}d ago"

let eventLogEntry (onFixTests: (unit -> unit) option) (onConfigureTests: (unit -> unit) option) (evt: CardEvent) =
    let isTestFailure =
        evt.Source = EventSource.Test && (match evt.Status with Some (StepStatus.Failed _) -> true | _ -> false)
    let isTestNotConfigured =
        evt.Source = EventSource.Test && evt.Status = Some StepStatus.NotConfigured
    let isClickable = (isTestFailure && onFixTests.IsSome) || (isTestNotConfigured && onConfigureTests.IsSome)
    Html.div [
        prop.className "event-entry"
        prop.children [
            Html.span [ prop.className "event-time"; prop.text (relativeEventTime evt.Timestamp) ]
            Html.span [ prop.className "event-source"; prop.text evt.Source ]
            Html.span [ prop.className "event-message"; prop.text evt.Message ]
            match evt.Status with
            | Some _ ->
                Html.span [
                    prop.className (
                        if isClickable
                        then stepStatusClassName evt.Status + " clickable"
                        else stepStatusClassName evt.Status)
                    prop.text (stepStatusText evt.Status)
                    if isTestFailure then
                        match onFixTests with
                        | Some handler ->
                            prop.title "Click to fix with coding tool"
                            prop.onClick (fun e -> e.stopPropagation(); handler())
                        | None -> ()
                    elif isTestNotConfigured then
                        match onConfigureTests with
                        | Some handler ->
                            prop.title "Click to configure test command"
                            prop.onClick (fun e -> e.stopPropagation(); handler())
                        | None -> ()
                ]
            | None -> Html.none
        ]
    ]

let eventLog dispatch (cooldowns: Set<WorktreePath>) (wtPath: WorktreePath) (hasTestFailureLog: bool) (events: CardEvent list) =
    match events with
    | [] -> Html.none
    | evts ->
        let onFixTests =
            if not hasTestFailureLog || cooldowns.Contains wtPath then None
            else Some (fun () -> dispatch (LaunchAction (wtPath, FixTests)))
        let onConfigureTests =
            if cooldowns.Contains wtPath then None
            else Some (fun () -> dispatch (LaunchAction (wtPath, ConfigureTests)))
        Html.div [
            prop.className "event-log"
            prop.children (evts |> List.map (eventLogEntry onFixTests onConfigureTests))
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

let terminalButton dispatch (wt: WorktreeStatus) =
    let action = if wt.HasActiveSession then FocusSession wt.Path else OpenTerminal wt.Path
    let title = if wt.HasActiveSession then "Focus session window (Enter)" else "Open terminal (Enter)"
    Html.button [
        prop.className "terminal-btn"
        prop.title title
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); dispatch action)
        prop.text ">"
    ]

let editorIcon =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 16, 16)
        svg.fill "currentColor"
        svg.children [
            Svg.path [ svg.d "M5.002 10L12 3l2 2-7 7H5z" ]
            Svg.path [ svg.d "M1.094 0C.525 0 0 .503 0 1.063v13.874C0 15.498.525 16 1.094 16h10.812c.558 0 1.074-.485 1.094-1.031V8l-2 2v4H2V2h5l2 2 1.531-1.531L8.344.344A1.12 1.12 0 007.563 0z" ]
            Svg.path [ svg.d "M14.19 1.011a.513.513 0 00-.364.152l-1.162 1.16 2.004 2.005 1.163-1.162a.514.514 0 000-.728l-1.277-1.275a.514.514 0 00-.364-.152z" ]
        ]
    ]

let editorButton dispatch editorName (wt: WorktreeStatus) =
    Html.button [
        prop.className "editor-btn"
        prop.title $"Open in {editorName} (E)"
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); dispatch (OpenEditor wt.Path))
        prop.children [ editorIcon ]
    ]

let newTabButton dispatch (wt: WorktreeStatus) =
    Html.button [
        prop.className "new-tab-btn"
        prop.title "Open new tab in tracked window (+)"
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); dispatch (OpenNewTab wt.Path))
        prop.text "+"
    ]

let binIcon =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 24, 24)
        svg.fill "none"
        svg.stroke "currentColor"
        svg.custom ("strokeWidth", "1.5")
        svg.custom ("strokeLinecap", "round")
        svg.children [
            Svg.path [ svg.d "M20.5001 6H3.5" ]
            Svg.path [ svg.d "M9.5 11L10 16" ]
            Svg.path [ svg.d "M14.5 11L14 16" ]
            Svg.path [
                svg.d "M6.5 6C6.55588 6 6.58382 6 6.60915 5.99936C7.43259 5.97849 8.15902 5.45491 8.43922 4.68032C8.44784 4.65649 8.45667 4.62999 8.47434 4.57697L8.57143 4.28571C8.65431 4.03708 8.69575 3.91276 8.75071 3.8072C8.97001 3.38607 9.37574 3.09364 9.84461 3.01877C9.96213 3 10.0932 3 10.3553 3H13.6447C13.9068 3 14.0379 3 14.1554 3.01877C14.6243 3.09364 15.03 3.38607 15.2493 3.8072C15.3043 3.91276 15.3457 4.03708 15.4286 4.28571L15.5257 4.57697C15.5433 4.62992 15.5522 4.65651 15.5608 4.68032C15.841 5.45491 16.5674 5.97849 17.3909 5.99936C17.4162 6 17.4441 6 17.5 6"
                svg.custom ("strokeLinecap", "butt")
            ]
            Svg.path [ svg.d "M18.3735 15.3991C18.1965 18.054 18.108 19.3815 17.243 20.1907C16.378 21 15.0476 21 12.3868 21H11.6134C8.9526 21 7.6222 21 6.75719 20.1907C5.89218 19.3815 5.80368 18.054 5.62669 15.3991L5.16675 8.5M18.8334 8.5L18.6334 11.5" ]
        ]
    ]

let deleteButton dispatch scopedKey (wt: WorktreeStatus) =
    Html.button [
        prop.className "delete-btn"
        prop.title "Remove worktree (Del)"
        yield! noFocusProps
        prop.onClick (fun e ->
            e.stopPropagation()
            dispatch (ConfirmDeleteWorktree scopedKey))
        prop.children [ binIcon ]
    ]

let archiveButton dispatch scopedKey (wt: WorktreeStatus) =
    Html.button [
        prop.className "archive-btn"
        prop.title "Archive worktree (A)"
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); dispatch (ConfirmArchiveWorktree scopedKey))
        prop.children [ ArchiveViews.archiveIcon ]
    ]

let conflictIcon =
    Svg.svg [
        svg.className "conflict-icon"
        svg.viewBox (0, 0, 1920, 1920)
        svg.custom ("role", "img")
        svg.children [
            Svg.title "Merge conflicts"
            Svg.path [
                svg.d "m1359.36 1279.51-79.85 79.85L960 1039.85l-319.398 319.51-79.85-79.85L880.152 960 560.753 640.602l79.85-79.85L960 880.152l319.51-319.398 79.85 79.85L1039.85 960l319.51 319.51ZM960 0C430.645 0 0 430.645 0 960s430.645 960 960 960 960-430.645 960-960S1489.355 0 960 0Z"
                svg.custom ("fillRule", "evenodd")
            ]
        ]
    ]

let prActionButton dispatch (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) (action: ActionKind) (title: string) (icon: ReactElement) =
    let onCooldown = cooldowns.Contains wt.Path
    Html.button [
        prop.className (if onCooldown then "action-btn disabled" else "action-btn")
        prop.disabled onCooldown
        yield! noFocusProps
        prop.title (if onCooldown then "Action already triggered" else title)
        prop.onClick (fun e -> e.stopPropagation(); if not onCooldown then dispatch (LaunchAction (wt.Path, action)))
        prop.children [ icon ]
    ]

let prBadgeContent dispatch (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) (repoName: string) (pr: PrInfo) =
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
                prop.children [
                    Html.text $"PR #{pr.Id}"
                    if pr.HasConflicts then conflictIcon
                ]
            ]
            match pr.Comments with
            | WithResolution (unresolved, total) when total > 0 ->
                Html.span [
                    prop.className (if unresolved = 0 then "thread-badge dimmed" else "thread-badge")
                    prop.text ($"{unresolved}/{total} threads")
                ]
                if unresolved > 0 then
                    prActionButton dispatch cooldowns wt (FixPr pr.Url) "Fix PR comments" commentIcon
            | _ -> ()
            yield! pr.Builds |> List.collect (fun build -> [
                    buildBadge repoName build
                    if build.Status = Failed then
                        match build.Url with
                        | Some url -> prActionButton dispatch cooldowns wt (FixBuild url) "Fix build" wrenchIcon
                        | None -> ()
                ])
    ]

let prSection dispatch (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) (repoName: string) =
    match wt.Pr with
    | NoPr -> Html.none
    | HasPr pr -> prBadgeContent dispatch cooldowns wt repoName pr

let prRow dispatch (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) (repoName: string) =
    match wt.Pr, wt.Branch with
    | NoPr, ("main" | "master") -> Html.none
    | NoPr, _ ->
        Html.div [
            prop.className "pr-row"
            prop.children [
                prActionButton dispatch cooldowns wt CreatePr "Create PR" createPrIcon
            ]
        ]
    | HasPr pr, _ ->
        Html.div [
            prop.className "pr-row"
            prop.children [ prBadgeContent dispatch cooldowns wt repoName pr ]
        ]

let workMetricsView = ArchiveViews.workMetricsView

let compactWorktreeCard dispatch editorName (repoName: string) (cooldowns: Set<WorktreePath>) (scopedKey: string) (isFocused: bool) (wt: WorktreeStatus) =
    let baseClass = cardClassName wt + " compact"
    let className = if isFocused then baseClass + " focused" else baseClass
    Html.div [
        prop.key wt.Branch
        prop.className className
        prop.onClick (fun _ -> dispatch (SetFocus (Some (Card scopedKey))))
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}"); prop.title (ctTooltip wt.CodingTool) ]
                    Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                    workMetricsView wt.WorkMetrics
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime System.DateTimeOffset.Now wt.LastCommitTime) ]
                    terminalButton dispatch wt
                    if wt.HasActiveSession then newTabButton dispatch wt
                    editorButton dispatch editorName wt
                    archiveButton dispatch scopedKey wt
                    if not wt.IsMainWorktree then deleteButton dispatch scopedKey wt
                ]
            ]
            Html.div [
                prop.className "compact-detail"
                prop.children [
                    if beadsTotal wt.Beads > 0 then beadsCounts "beads-inline" wt.Beads
                    mainBehindIndicator wt.MainBehindCount
                    prSection dispatch cooldowns wt repoName
                ]
            ]
        ]
    ]

let worktreeCard dispatch editorName (repoName: string) (cooldowns: Set<WorktreePath>) (branchEvents: CardEvent list) (isPending: bool) (scopedKey: string) (isFocused: bool) (wt: WorktreeStatus) =
    let baseClass = cardClassName wt
    let className = if isFocused then baseClass + " focused" else baseClass
    let hasContent = wt.LastUserMessage.IsSome || (not (List.isEmpty branchEvents))
    let footerClass = if hasContent then "card-footer has-content" else "card-footer"
    Html.div [
        prop.key wt.Branch
        prop.className className
        prop.onClick (fun _ -> dispatch (SetFocus (Some (Card scopedKey))))
        prop.children [
            Html.div [
                prop.className "card-body"
                prop.children [
                    Html.div [
                        prop.className "card-header"
                        prop.children [
                            Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}"); prop.title (ctTooltip wt.CodingTool) ]
                            Html.span [ prop.className "branch-name"; prop.text wt.Branch ]
                            workMetricsView wt.WorkMetrics
                            terminalButton dispatch wt
                            if wt.HasActiveSession then newTabButton dispatch wt
                            editorButton dispatch editorName wt
                            archiveButton dispatch scopedKey wt
                            if not wt.IsMainWorktree then deleteButton dispatch scopedKey wt
                        ]
                    ]

                    if beadsTotal wt.Beads > 0 then
                        Html.div [
                            prop.className "beads-row"
                            prop.children [
                                beadsCounts "beads-counts" wt.Beads
                                beadsProgressBar wt.Beads
                            ]
                        ]

                    mainBehindWithSync dispatch wt branchEvents isPending scopedKey

                    prRow dispatch cooldowns wt repoName
                ]
            ]

            Html.div [
                prop.className footerClass
                prop.children [
                    match wt.LastUserMessage with
                    | Some (prompt, ts) ->
                        Html.div [
                            prop.className "user-prompt"
                            prop.children [
                                Html.span [ prop.className "event-time"; prop.text (relativeEventTime ts) ]
                                Html.span [ prop.text prompt ]
                            ]
                        ]
                    | None -> ()

                    eventLog dispatch cooldowns wt.Path wt.HasTestFailureLog branchEvents
                ]
            ]
        ]
    ]

let renderCard dispatch editorName isCompact (focusedElement: FocusTarget option) repoId repoName (branchEvents: Map<string, CardEvent list>) (syncPending: Set<string>) (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) =
    let scopedKey = $"{repoId}/{wt.Branch}"
    let events = branchEvents |> Map.tryFind scopedKey |> Option.defaultValue []
    let isPending = syncPending |> Set.contains scopedKey
    let isFocused = focusedElement = Some (Card scopedKey)
    if isCompact then compactWorktreeCard dispatch editorName repoName cooldowns scopedKey isFocused wt
    else worktreeCard dispatch editorName repoName cooldowns events isPending scopedKey isFocused wt

let archiveSection dispatch = ArchiveViews.archiveSection (ArchiveMsg >> dispatch)

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

let viewEyeOpen (pupilColor: string) (dx: float, dy: float) =
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
                        svg.fill pupilColor
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

let hasAnyActive (repos: RepoModel list) =
    repos |> List.exists (fun r ->
        r.Worktrees |> List.exists (fun wt ->
            wt.CodingTool = Working || wt.CodingTool = WaitingForUser))

let hasAnyWaiting (repos: RepoModel list) =
    repos |> List.exists (fun r ->
        r.Worktrees |> List.exists (fun wt -> wt.CodingTool = WaitingForUser))

let anyRepoReady (repos: RepoModel list) =
    repos |> List.exists _.IsReady

let allWorktreesEmpty (repos: RepoModel list) =
    repos |> List.forall _.Worktrees.IsEmpty

let providerIcon (provider: RepoProvider option) =
    let icon viewBox (svgPath: string) =
        Svg.svg [
            svg.className "provider-icon"
            svg.viewBox viewBox
            svg.children [ Svg.path [ svg.d svgPath; svg.fill "currentColor" ] ]
        ]

    let githubPath = "M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"
    let azdoPath = "M17 4v9.74l-4 3.28-6.2-2.26V17l-3.51-4.59 10.23.8V4.44zm-3.41.49L7.85 1v2.29L2.58 4.84 1 6.87v4.61l2.26 1V6.57z"

    match provider with
    | None | Some UnknownProvider -> Html.none
    | Some(GitHubProvider url) ->
        Html.a [
            prop.className "provider-link"
            prop.href url
            prop.target "_blank"
            prop.onClick (fun e -> e.stopPropagation())
            prop.children [ icon (0, 0, 24, 24) githubPath ]
        ]
    | Some(AzDoProvider url) ->
        Html.a [
            prop.className "provider-link"
            prop.href url
            prop.target "_blank"
            prop.onClick (fun e -> e.stopPropagation())
            prop.children [ icon (0, 0, 18, 18) azdoPath ]
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
                            Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}"); prop.title (ctTooltip wt.CodingTool) ]))
                ]
            Html.button [
                prop.className "create-wt-btn"
                prop.title "Create worktree"
                prop.onClick (fun e -> e.stopPropagation(); dispatch (ModalMsg (CreateWorktreeModal.OpenCreateWorktree repo.RepoId)))
                prop.text "+"
            ]
        ]
    ]

let repoSection dispatch editorName isCompact (focusedElement: FocusTarget option) (branchEvents: Map<string, CardEvent list>) (syncPending: Set<string>) (cooldowns: Set<WorktreePath>) (repo: RepoModel) =
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
                        prop.children (repo.Worktrees |> List.map (renderCard dispatch editorName isCompact focusedElement (RepoId.value repo.RepoId) repo.Name branchEvents syncPending cooldowns))
                    ]
                    archiveSection dispatch repo.ArchivedWorktrees
        ]
    ]

let barColor (pct: float) =
    if pct >= 80.0 then "#f38ba8"
    elif pct >= 50.0 then "#f9e2af"
    else "#6c7086"

let labelColor (pct: float) =
    if pct >= 80.0 then Some "#f38ba8"
    elif pct >= 50.0 then Some "#f9e2af"
    else None

let viewMetricBar (pct: float) (label: string) =
    Html.div [
        prop.className "metric-bar-row"
        prop.children [
            Html.div [
                prop.className "metric-bar-track"
                prop.children [
                    Html.div [
                        prop.className "metric-bar-fill"
                        prop.style [
                            style.width (length.percent (min pct 100.0))
                            style.backgroundColor (barColor pct)
                        ]
                    ]
                ]
            ]
            Html.span [
                prop.className "metric-bar-label"
                match labelColor pct with
                | Some c -> prop.style [ style.color c ]
                | None -> ()
                prop.text label
            ]
        ]
    ]

let viewSystemMetrics (metrics: SystemMetrics option) =
    match metrics with
    | None -> Html.none
    | Some m ->
        let memPct = float m.MemoryUsedMb / float m.MemoryTotalMb * 100.0
        Html.div [
            prop.className "system-metrics"
            prop.children [
                viewMetricBar m.CpuPercent "CPU"
                viewMetricBar memPct "RAM"
            ]
        ]

let viewAppHeader model dispatch =
    Html.div [
        prop.className "app-header"
        prop.children [
            Html.div [
                prop.className "header-left"
                prop.children [
                    viewSystemMetrics model.SystemMetrics
                ]
            ]
            Html.div [
                prop.className "header-center"
                prop.children [
                    if model.HasError then viewEyeRolledBack
                    elif hasAnyActive model.Repos then
                        let pupilColor = if hasAnyWaiting model.Repos then "#f9e2af" else "#1a1b2e"
                        viewEyeOpen pupilColor model.EyeDirection
                    else viewEyeClosed
                ]
            ]
            Html.div [
                prop.className "header-right"
                prop.children [
                    match model.DeployBranch with
                    | Some branch ->
                        Html.span [ prop.className "deploy-branch"; prop.text branch ]
                    | None -> ()
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
        ]
    ]

let view model dispatch =
    React.fragment [
        viewAppHeader model dispatch
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
                if not (anyRepoReady model.Repos) && allWorktreesEmpty model.Repos then
                    Html.div [
                        prop.className "status-bar"
                        prop.children [ Html.span "Waiting for first refresh..." ]
                    ]
                    skeletonGrid ()
                else
                    Html.div [
                        prop.className "repo-list"
                        prop.children (model.Repos |> List.map (repoSection dispatch model.EditorName model.IsCompact model.FocusedElement model.BranchEvents model.SyncPending model.ActionCooldowns))
                    ]

                schedulerFooter model.Repos model.SchedulerEvents model.LatestByCategory

                CreateWorktreeModal.view (ModalMsg >> dispatch) model.CreateModal
                ConfirmModal.view (ConfirmMsg >> dispatch) model.ConfirmModal
            ]
        ]
    ]

open Elmish.React

Program.mkProgram init update view
|> Program.withSubscription pollingSubscription
|> Program.withReactSynchronous "app"
|> Program.run
