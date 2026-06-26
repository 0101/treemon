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
open CanvasAwareness
open CardViews
open AppTypes

let fetchWorktrees () =
    Cmd.OfAsync.either worktreeApi.Value.getWorktrees () (fun r -> DataLoaded (r, System.DateTimeOffset.Now)) DataFailed

let fetchSyncStatus () =
    Cmd.OfAsync.perform worktreeApi.Value.getSyncStatus () SyncStatusUpdate

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
      FocusedElement = None
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      DeployBranch = None
      SystemMetrics = None
      ActionCooldowns = Set.empty
      Activity = { ActivityState.empty with LastActivityTime = Fable.Core.JS.Constructors.Date.now () }
      Mascot = MascotState.empty
      Canvas = CanvasState.empty },
    Cmd.batch [ fetchWorktrees (); fetchSyncStatus (); Cmd.OfAsync.attempt worktreeApi.Value.reportActivity ActivityLevel.Active (fun _ -> NoOp); Cmd.OfAsync.perform worktreeApi.Value.loadLastViewedHashes () LoadLastViewedHashes ]

let filterDeletedPaths (deleted: Set<string>) (repos: RepoModel list) =
    if Set.isEmpty deleted then repos
    else
        repos
        |> List.map (fun r ->
            { r with Worktrees = r.Worktrees |> List.filter (fun wt -> not (Set.contains (WorktreePath.value wt.Path) deleted)) })

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
    | Card scopedKey, "r" -> findWorktree scopedKey model |> Option.bind (fun wt -> if canResumeSession wt then Some (ResumeSession wt.Path) else None)
    | Card scopedKey, "e" -> findWorktree scopedKey model |> Option.map (fun wt -> OpenEditor wt.Path)
    | Card scopedKey, "c" -> Some ToggleCanvasPane
    | Card scopedKey, "a" -> findWorktree scopedKey model |> Option.map (fun _ -> ConfirmArchiveWorktree scopedKey)
    | Card scopedKey, "Delete" -> findWorktree scopedKey model |> Option.bind (fun wt -> if not wt.IsMainWorktree then Some (ConfirmDeleteWorktree scopedKey) else None)
    | RepoHeader repoId, "Enter" -> Some (ToggleCollapse repoId)
    | RepoHeader repoId, "+" -> Some (ModalMsg (CreateWorktreeModal.OpenCreateWorktree repoId))
    | _ -> None

let update msg model =
    match msg with
    | DataLoaded (response, now) ->
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
                          if isFirstLoad then response.CollapsedRepos |> Set.contains r.RepoId
                          else existingCollapse |> Map.tryFind r.RepoId |> Option.defaultValue false
                      Provider = r.Provider
                      BaseBranch = r.BaseBranch })
                |> filterDeletedPaths stillPending
            let currentCanvasHashes = canvasHashesByScopedKey repos
            let canvasEvents =
                if isFirstLoad then model.Canvas.CanvasEvents
                else
                    let newEvents = detectCanvasEvents now model.Canvas.PreviousCanvasHashes currentCanvasHashes
                    mergeCanvasEvents model.Canvas.CanvasEvents newEvents
                    |> expireCanvasEvents now
            let changedDocs =
                if isFirstLoad then []
                else detectChangedCanvasDocs now model.Canvas.PreviousCanvasHashes currentCanvasHashes
            let now = now.ToUnixTimeMilliseconds() |> float
            let isIdle = now - model.Activity.LastActivityTime > ActivityState.autoDisplayIdleMs
            // Idle auto-display only focus-steals for AgentDoc changes. A SystemView (beads
            // dashboard) is server-generated and self-refreshing, so its data churn must not
            // hijack focus; filter it out of the candidates before picking the most recent.
            let agentChangedDocs =
                changedDocs
                |> List.filter (fun (scopedKey, filename) -> CanvasState.canvasDocKind repos scopedKey filename = Some AgentDoc)
            let autoDisplayTarget =
                if isIdle && not (List.isEmpty agentChangedDocs)
                then findMostRecentChangedDoc repos agentChangedDocs
                else None
            // Delivery signal for a queued canvas message — see CanvasAwareness.clearWaitingOnDelivery.
            // The clear is scoped to the queued message's target worktree: an unrelated worktree's doc
            // change must not dismiss the banner (that would falsely report delivery). This is the
            // success edge the wall-clock timer used to (wrongly) report as a failure.
            let canvasSendState = clearWaitingOnDelivery model.Canvas.CanvasSendState agentChangedDocs
            let canvasShowingDoc = model.Canvas.CanvasPaneOpen && Option.isSome (CanvasUpdate.activeVisibleDoc model)
            let repos, autoExpanded =
                match autoDisplayTarget with
                | Some (scopedKey, _) when not canvasShowingDoc -> expandRepoOwning scopedKey repos
                | _ -> repos, false
            let autoDisplayCmd =
                match autoDisplayTarget with
                | Some (scopedKey, filename) when not canvasShowingDoc ->
                    Cmd.batch [
                        if not model.Canvas.CanvasPaneOpen then Cmd.ofMsg ToggleCanvasPane
                        Cmd.ofMsg (SetFocus (Some (Card scopedKey)))
                        Cmd.ofMsg (SelectCanvasDoc (scopedKey, filename))
                    ]
                | _ -> Cmd.none
            { model with
                Repos = repos
                IsLoading = false
                HasError = false
                SchedulerEvents = response.SchedulerEvents
                LatestByCategory = response.LatestByCategory
                AppVersion = Some response.AppVersion
                EditorName = response.EditorName
                Mascot = { model.Mascot with EyeDirection = MascotState.randomEyeDirection () }
                DeletedPaths = stillPending
                DeployBranch = response.DeployBranch
                SystemMetrics = response.SystemMetrics
                Canvas =
                    { model.Canvas with
                        CanvasPaneOpen = if isFirstLoad then response.CanvasPaneOpen else model.Canvas.CanvasPaneOpen
                        CanvasPosition = if isFirstLoad then response.CanvasPosition else model.Canvas.CanvasPosition
                        CanvasSize = if isFirstLoad then response.CanvasSize else model.Canvas.CanvasSize
                        PreviousCanvasHashes = currentCanvasHashes
                        CanvasEvents = canvasEvents
                        CanvasSendState = canvasSendState } }
            |> (fun m -> { m with FocusedElement = adjustFocusForVisibility m.Repos m.FocusedElement })
            |> (fun m ->
                if isFirstLoad then
                    let seeded = seedLastViewedHashes m.Repos m.Canvas.LastViewedHashes
                    if seeded = m.Canvas.LastViewedHashes then m
                    else { m with Canvas = { m.Canvas with LastViewedHashes = seeded } }
                else m)
            |> (fun updatedModel ->
                let allPaths =
                    repos
                    |> List.collect _.Worktrees
                    |> List.filter (fun wt -> not (List.isEmpty wt.CanvasDocs))
                    |> List.map (fun wt -> WorktreePath.value wt.Path)
                let livenessCmd =
                    if List.isEmpty allPaths then Cmd.none
                    else Cmd.OfAsync.perform worktreeApi.Value.getBridgeLiveness allPaths BridgeLivenessLoaded
                let markVisibleCmd =
                    if updatedModel.Canvas.CanvasPaneOpen then CanvasUpdate.markVisibleDocCmd updatedModel
                    else Cmd.none
                let seedSaveCmd =
                    if updatedModel.Canvas.LastViewedHashes <> model.Canvas.LastViewedHashes then
                        Cmd.OfAsync.attempt worktreeApi.Value.saveLastViewedHashes updatedModel.Canvas.LastViewedHashes (fun _ -> NoOp)
                    else Cmd.none
                let morphCmd =
                    if not isFirstLoad && updatedModel.Canvas.CanvasPaneOpen then
                        match CanvasUpdate.activeVisibleDoc updatedModel with
                        | Some (scopedKey, filename) when CanvasState.canvasDocKind updatedModel.Repos scopedKey filename = Some AgentDoc ->
                            let oldHash = model.Canvas.PreviousCanvasHashes |> Map.tryFind scopedKey |> Option.bind (Map.tryFind filename)
                            let newHash = currentCanvasHashes |> Map.tryFind scopedKey |> Option.bind (Map.tryFind filename)
                            match oldHash, newHash with
                            | Some o, Some n when o <> n -> Cmd.ofMsg MorphActiveDoc
                            | _ -> Cmd.none
                        | _ -> Cmd.none
                    else Cmd.none
                let autoExpandSaveCmd =
                    if autoExpanded then saveCollapsedReposCmd updatedModel.Repos else Cmd.none
                updatedModel, Cmd.batch [ autoDisplayCmd; livenessCmd; markVisibleCmd; seedSaveCmd; morphCmd; autoExpandSaveCmd ])

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
            if isCollapsing then adjustFocusAfterCollapse repoId updatedModel.Repos updatedModel.FocusedElement
            else updatedModel.FocusedElement
        { updatedModel with FocusedElement = focusAdjusted },
        saveCollapsedReposCmd updatedModel.Repos

    | OpenTerminal path ->
        model, Cmd.OfAsync.attempt worktreeApi.Value.openTerminal path (fun _ -> Tick(Fable.Core.JS.Constructors.Date.now ()))
    | OpenEditor path ->
        model, Cmd.OfAsync.attempt worktreeApi.Value.openEditor path (fun _ -> Tick(Fable.Core.JS.Constructors.Date.now ()))

    | Tick now ->
        // Tick stays in the root update because it also expires canvas events and drives the
        // worktree/sync poll; only the activity-recompute delegates to ActivityUpdate.
        let activity, reportCmd = ActivityUpdate.tickActivity now model.Activity
        let expiredEvents = expireCanvasEvents (System.DateTimeOffset.FromUnixTimeMilliseconds(int64 now)) model.Canvas.CanvasEvents

        // A queued canvas message is honestly pending, not failed: it is delivered to the
        // server-side queue and drained when a session registers. Never flip Waiting -> Failed on
        // a wall-clock timer. The delivery signal (an agent doc content-hash change) clears it to
        // Idle in DataLoaded; absent that, it persists until the user dismisses it.
        { model with Activity = activity; Canvas = { model.Canvas with CanvasEvents = expiredEvents } },
        Cmd.batch [ fetchWorktrees (); fetchSyncStatus (); reportCmd ]

    | UserActivity now -> ActivityUpdate.userActivity now model

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
        Cmd.OfAsync.perform worktreeApi.Value.startSync path (fun r -> SyncStarted (key, r))

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
        model, Cmd.OfAsync.attempt worktreeApi.Value.cancelSync path (fun _ -> Tick(Fable.Core.JS.Constructors.Date.now ()))

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
            Cmd.OfAsync.perform worktreeApi.Value.deleteWorktree path DeleteCompleted
        | ConfirmModal.DeleteAfterKillSession path ->
            model, Cmd.OfAsync.perform worktreeApi.Value.killSession path (function
                | Ok () -> SessionKilledForDelete path
                | Error _ -> Tick(Fable.Core.JS.Constructors.Date.now ()))
        | ConfirmModal.Archive path ->
            model, Cmd.ofMsg (ArchiveMsg (ArchiveViews.Archive path))
        | ConfirmModal.ArchiveAfterKillSession path ->
            model, Cmd.OfAsync.perform worktreeApi.Value.killSession path (function
                | Ok () -> SessionKilledForArchive path
                | Error _ -> Tick(Fable.Core.JS.Constructors.Date.now ()))

    | DeleteCompleted (Ok _) ->
        model, fetchWorktrees ()

    | DeleteCompleted (Error _) ->
        { model with DeletedPaths = Set.empty }, fetchWorktrees ()

    | SessionKilledForDelete path ->
        removeWorktreeByPath path model,
        Cmd.OfAsync.perform worktreeApi.Value.deleteWorktree path DeleteCompleted

    | SessionKilledForArchive path ->
        model, Cmd.ofMsg (ArchiveMsg (ArchiveViews.Archive path))

    | FocusSession path ->
        model, Cmd.OfAsync.perform worktreeApi.Value.focusSession path SessionResult

    | OpenNewTab path ->
        model, Cmd.OfAsync.perform worktreeApi.Value.openNewTab path SessionResult

    | ResumeSession path ->
        model, Cmd.OfAsync.perform worktreeApi.Value.resumeSession path SessionResult

    | LaunchCanvasSession scopedKey -> CanvasUpdate.launchCanvasSession scopedKey model

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
                Cmd.OfAsync.perform worktreeApi.Value.launchAction { Path = path; Action = action } LaunchActionResult
                clearAfter
            ]

    | LaunchActionResult _ ->
        model, fetchWorktrees ()

    | ClearActionCooldown path ->
        { model with ActionCooldowns = model.ActionCooldowns.Remove path }, Cmd.none

    | SetFocus target ->
        { model with FocusedElement = target }, Cmd.none

    | ArchiveMsg archiveMsg ->
        let result, archiveCmd = ArchiveViews.update worktreeApi archiveMsg
        let refreshCmd = if result.RefreshWorktrees then fetchWorktrees () else Cmd.none
        model, Cmd.batch [ Cmd.map ArchiveMsg archiveCmd; refreshCmd ]

    | ModalMsg modalMsg ->
        let result, modalCmd = CreateWorktreeModal.update worktreeApi modalMsg model.CreateModal
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

    | ToggleCanvasPane -> CanvasUpdate.toggleCanvasPane model

    | SetCanvasPosition position -> CanvasUpdate.setCanvasPosition position model

    | SetCanvasSize size -> CanvasUpdate.setCanvasSize size model

    | SelectCanvasDoc (scopedKey, filename) -> CanvasUpdate.selectCanvasDoc scopedKey filename model

    | FocusOverviewCard scopedKey ->
        let openPane = not model.Canvas.CanvasPaneOpen
        let repos, expanded = expandRepoOwning scopedKey model.Repos
        { model with Repos = repos; FocusedElement = Some (Card scopedKey); Canvas = { model.Canvas with CanvasPaneOpen = true } },
        Cmd.batch [
            if openPane then Cmd.OfAsync.attempt worktreeApi.Value.saveCanvasPaneOpen true (fun _ -> NoOp)
            if expanded then saveCollapsedReposCmd repos
        ]

    | OpenCanvasDoc (scopedKey, filename) -> CanvasUpdate.openCanvasDoc scopedKey filename model

    | ArchiveCanvasDoc (scopedKey, filename) -> CanvasUpdate.archiveCanvasDoc scopedKey filename model

    | ArchiveCanvasDocResult (scopedKey, filename, result) -> CanvasUpdate.archiveCanvasDocResult scopedKey filename result model

    | NavigateCanvasDoc filename -> CanvasUpdate.navigateCanvasDoc filename model

    | CanvasMessageReceived payload -> CanvasUpdate.canvasMessageReceived payload model

    | CanvasSendResult (result, scopedKey) -> CanvasUpdate.canvasSendResult result scopedKey model

    | DismissCanvasMessageError -> CanvasUpdate.dismissCanvasMessageError model

    | CanvasDocError (scopedKey, filename, message) -> CanvasUpdate.canvasDocError scopedKey filename message model

    | DismissCanvasDocError -> CanvasUpdate.dismissCanvasDocError model

    | MarkDocViewed (scopedKey, filename) ->
        let worktree = findWorktree scopedKey model
        let currentHash =
            worktree
            |> Option.bind (fun wt ->
                wt.CanvasDocs
                |> List.tryFind (fun d -> d.Filename = filename)
                |> Option.map _.ContentHash)
        match worktree, currentHash with
        | Some _, Some hash ->
            let innerMap =
                model.Canvas.LastViewedHashes
                |> Map.tryFind scopedKey
                |> Option.defaultValue Map.empty
                |> Map.add filename hash
            let updatedHashes = model.Canvas.LastViewedHashes |> Map.add scopedKey innerMap
            { model with Canvas = { model.Canvas with LastViewedHashes = updatedHashes } },
            Cmd.OfAsync.attempt worktreeApi.Value.saveLastViewedHashes updatedHashes (fun _ -> NoOp)
        | _ -> model, Cmd.none

    | LoadLastViewedHashes hashes ->
        { model with Canvas = { model.Canvas with LastViewedHashes = hashes } }, Cmd.none

    | BridgeLivenessLoaded liveness ->
        { model with Canvas = { model.Canvas with BridgeLiveness = liveness } }, Cmd.none

    | MorphActiveDoc -> CanvasUpdate.morphActiveDoc model

    | MorphComplete -> CanvasUpdate.morphComplete model

    | NoOp -> model, Cmd.none

let appSubscriptions (model: Model) : Sub<Msg> =
    let pollingIntervalMs =
        match model.Activity.ActivityLevel with
        | ActivityLevel.Active | ActivityLevel.Idle -> 1000
        | ActivityLevel.DeepIdle -> 15000

    let activityLevelKey =
        match model.Activity.ActivityLevel with
        | ActivityLevel.Active -> "active"
        | ActivityLevel.Idle -> "idle"
        | ActivityLevel.DeepIdle -> "deep-idle"

    let worktreePolling (dispatch: Dispatch<Msg>) =
        let intervalId =
            Fable.Core.JS.setInterval (fun () -> dispatch (Tick(Fable.Core.JS.Constructors.Date.now ()))) pollingIntervalMs
        { new System.IDisposable with
            member _.Dispose() = Fable.Core.JS.clearInterval intervalId }

    let syncPolling (dispatch: Dispatch<Msg>) =
        let intervalId =
            Fable.Core.JS.setInterval (fun () -> dispatch SyncTick) 2000
        { new System.IDisposable with
            member _.Dispose() = Fable.Core.JS.clearInterval intervalId }

    let subs =
        [ [ "polling"; activityLevelKey ], worktreePolling
          [ "activity" ], ActivityUpdate.activityDetection
          [ "canvas-messages" ], CanvasUpdate.messageListener ]

    if hasSyncRunning model.BranchEvents then
        ([ "sync-polling" ], syncPolling) :: subs
    else
        subs

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
                    if model.HasError then MascotView.viewEyeRolledBack ()
                    elif hasAnyActive model.Repos then
                        let pupilColor = if hasAnyWaiting model.Repos then "#f9e2af" else "#1a1b2e"
                        MascotView.viewEyeOpen pupilColor model.Activity.ActivityLevel model.Mascot.EyeDirection
                    else MascotView.viewEyeClosed ()
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
                            Html.button [
                                prop.className (if model.Canvas.CanvasPaneOpen then "ctrl-btn active" else "ctrl-btn")
                                yield! noFocusProps
                                prop.onClick (fun _ -> dispatch ToggleCanvasPane)
                                prop.title "Toggle canvas pane (C)"
                                prop.children [
                                    Html.text "Canvas"
                                    let unviewedCount = unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes |> Map.values |> Seq.sumBy List.length
                                    if unviewedCount > 0 then
                                        Html.span [
                                            prop.className "canvas-badge"
                                            prop.text (string unviewedCount)
                                        ]
                                ]
                            ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let view model dispatch =
    let canvasPositionClass =
        match model.Canvas.CanvasPosition with
        | CanvasPosition.Left -> "canvas-left"
        | CanvasPosition.Right -> "canvas-right"
        | CanvasPosition.Top -> "canvas-top"
        | CanvasPosition.Bottom -> "canvas-bottom"

    let canvasSizeClass =
        match model.Canvas.CanvasSize with
        | CanvasSize.Ratio1To1 -> "canvas-size-1to1"
        | CanvasSize.Ratio2To1 -> "canvas-size-2to1"

    let dashboardClass =
        match model.Canvas.CanvasPaneOpen with
        | true -> $"dashboard canvas-open {canvasPositionClass}"
        | false -> "dashboard"

    let layoutClass =
        match model.Canvas.CanvasPaneOpen with
        | true -> $"app-layout canvas-open {canvasPositionClass} {canvasSizeClass}"
        | false -> "app-layout"

    let cardProps: CardViewProps =
        { EditorName = model.EditorName
          IsCompact = model.IsCompact
          FocusedElement = model.FocusedElement
          BranchEvents = model.BranchEvents
          SyncPending = model.SyncPending
          ActionCooldowns = model.ActionCooldowns
          CanvasEvents = model.Canvas.CanvasEvents
          CanvasPaneOpen = model.Canvas.CanvasPaneOpen }

    let cardCallbacks: CardCallbacks =
        { FocusCard = fun key -> dispatch (SetFocus (Some (Card key)))
          ToggleRepo = fun repoId -> dispatch (ToggleCollapse repoId)
          CreateWorktree = fun repoId -> dispatch (ModalMsg (CreateWorktreeModal.OpenCreateWorktree repoId))
          OpenTerminal = fun wt -> dispatch (if wt.HasActiveSession then FocusSession wt.Path else OpenTerminal wt.Path)
          OpenEditor = fun wt -> dispatch (OpenEditor wt.Path)
          OpenNewTab = fun wt -> dispatch (OpenNewTab wt.Path)
          ResumeSession = fun wt -> dispatch (ResumeSession wt.Path)
          DeleteWorktree = fun key -> dispatch (ConfirmDeleteWorktree key)
          ArchiveWorktree = fun key -> dispatch (ConfirmArchiveWorktree key)
          StartSync = fun path key -> dispatch (StartSync (path, key))
          CancelSync = fun path -> dispatch (CancelSync path)
          LaunchAction = fun path action -> dispatch (LaunchAction (path, action))
          OpenCanvasDoc = fun key filename -> dispatch (OpenCanvasDoc (key, filename))
          DispatchArchive = ArchiveMsg >> dispatch }

    let dashboardEl =
        Html.div [
            prop.className dashboardClass
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
                        prop.children (model.Repos |> List.map (repoSection cardProps cardCallbacks))
                    ]

                OverviewViews.schedulerFooter model.Repos model.SchedulerEvents model.LatestByCategory

                CreateWorktreeModal.view (ModalMsg >> dispatch) model.CreateModal
                ConfirmModal.view (ConfirmMsg >> dispatch) model.ConfirmModal
            ]
        ]

    let canvasEl =
        CanvasView.view model dispatch

    let children =
        match model.Canvas.CanvasPosition with
        | CanvasPosition.Left
        | CanvasPosition.Top -> [ canvasEl; dashboardEl ]
        | CanvasPosition.Right
        | CanvasPosition.Bottom -> [ dashboardEl; canvasEl ]

    React.fragment [
        viewAppHeader model dispatch
        Html.div [
            prop.className layoutClass
            prop.children children
        ]
    ]

// Elmish/React entry point. Guarded for Fable only: under the .NET test host
// this top-level expression runs the whole app at module load (init()'s
// JS Date.now + a React DOM mount), which throws inside App's static
// constructor and takes down every test that merely references App's pure
// helpers. Fable always defines FABLE_COMPILER, so the browser build is
// byte-for-byte unchanged; only the unit-test (.NET) load path is affected.
#if FABLE_COMPILER
open Elmish.React

Program.mkProgram init update view
|> Program.withSubscription appSubscriptions
|> Program.withReactSynchronous "app"
|> Program.run
#endif
