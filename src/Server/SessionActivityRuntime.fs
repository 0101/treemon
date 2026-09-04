module Server.SessionActivityRuntime

open System
open Shared
open Server.SessionActivity
open Server.SessionActivityService
open Server.TerminalSessionActivity

type internal Components =
    { Store: SessionActivityStore.SessionActivityStore
      Service: SessionActivityService.SessionActivityService }

type internal Runtime =
    { Components: Components
      SnapshotStore: OverviewSnapshotStore.OverviewSnapshotStore
      Capture: OverviewSnapshotCapture.SnapshotCapture }

let internal createComponentsWithProcessIdentityResolver
    processIdentityResolver
    (dbPath: string)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    =
    let store = new SessionActivityStore.SessionActivityStore(dbPath)

    try
        { Store = store
          Service =
            new SessionActivityService.SessionActivityService(
                store,
                scheduler,
                processIdentityResolver
            ) }
    with _ ->
        (store :> System.IDisposable).Dispose()
        reraise ()

let internal createComponents dbPath scheduler =
    createComponentsWithProcessIdentityResolver
        ProcessIdentityResolverRuntime.defaultResolver
        dbPath
        scheduler

let internal createWithProcessIdentityResolver
    processIdentityResolver
    (dbPath: string)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    =
    let snapshotStore = OverviewSnapshotStore.OverviewSnapshotStore(dbPath)
    let components =
        createComponentsWithProcessIdentityResolver
            processIdentityResolver
            dbPath
            scheduler

    try
        { Components = components
          SnapshotStore = snapshotStore
          Capture =
            OverviewSnapshotCapture.create
                scheduler
                rootPaths
                snapshotStore }
    with _ ->
        (components.Service :> System.IDisposable).Dispose()
        (components.Store :> System.IDisposable).Dispose()
        reraise ()

let internal create dbPath scheduler rootPaths =
    createWithProcessIdentityResolver
        ProcessIdentityResolverRuntime.defaultResolver
        dbPath
        scheduler
        rootPaths

let internal terminalSessionCleanup
    (service: SessionActivityService)
    : WorktreeCleanup.PrepareSessionClose =
    fun originPaths ->
        let query terminalSessionIds =
            queryOwnedSessions
                (fun ids -> service.QueryTerminalActivity ids)
                DateTimeOffset.UtcNow
                terminalSessionIds
            |> Result.map _.OpenSessions

        let captured =
            originPaths |> Map.keys |> Set.ofSeq |> query

        let beforeHostClose (activeTerminalIds: Set<TerminalSessionId>) =
            let requestShutdown (session: OwnedSessionState) =
                async {
                    match originPaths |> Map.tryFind session.TerminalSessionId with
                    | None -> ()
                    | Some worktreePath ->
                        let! _ =
                            SessionBridge.shutdownExact
                                (fun identity ->
                                    async {
                                        return service.IsProcessClosed identity
                                    })
                                { WorktreePath = WorktreePath.value worktreePath
                                  ProcessIdentity = session.ProcessIdentity }

                        ()
                }

            captured
            |> Result.defaultValue []
            |> List.filter (fun session ->
                activeTerminalIds.Contains session.TerminalSessionId)
            |> List.map (fun session ->
                requestShutdown session
                |> Async.Catch)
            |> Async.Parallel
            |> Async.Ignore

        let afterHostClose (closedTerminalIds: Set<TerminalSessionId>) =
            if Set.isEmpty closedTerminalIds then
                Ok()
            else
                let observedAfter = query closedTerminalIds

                let sessions =
                    [ captured; observedAfter ]
                    |> List.choose Result.toOption
                    |> List.collect id
                    |> List.filter (fun session ->
                        closedTerminalIds.Contains session.TerminalSessionId)
                    |> List.distinctBy _.ProcessIdentity

                let closedAt = DateTimeOffset.UtcNow

                let closureErrors =
                    sessions
                    |> List.map (fun session ->
                        match
                            service.CloseProcess(
                                session.ProcessIdentity,
                                closedAt
                            )
                        with
                        | ClosureAcknowledge.Closed -> None
                        | ClosureAcknowledge.Missing ->
                            Some "an exact session closure target was not found"
                        | ClosureAcknowledge.Failed error -> Some error)
                    |> List.choose id

                match observedAfter, closureErrors with
                | Error error, [] ->
                    Error $"Could not reconcile exact terminal sessions: {error}"
                | Error error, failures ->
                    let failureText = String.concat "; " failures
                    Error $"Could not reconcile exact terminal sessions: {error}; {failureText}"
                | Ok _, [] -> Ok()
                | Ok _, failures -> Error(String.concat "; " failures)

        { BeforeHostClose = beforeHostClose
          AfterHostClose = afterHostClose }

let internal shutdownStoreUsers
    (disposeIngestion: unit -> unit)
    (stopScheduler: unit -> unit)
    (disposeStore: unit -> unit)
    =
    try
        disposeIngestion ()
    finally
        try
            stopScheduler ()
        finally
            disposeStore ()

let internal shutdown
    (runtime: Runtime)
    (schedulerLoop: BackgroundLoop.Running option)
    =
    shutdownStoreUsers
        (fun () -> (runtime.Components.Service :> System.IDisposable).Dispose())
        (fun () ->
            schedulerLoop
            |> Option.iter (BackgroundLoop.stop "Refresh scheduler"))
        (fun () -> (runtime.Components.Store :> System.IDisposable).Dispose())
