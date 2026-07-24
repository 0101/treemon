module Server.SessionActivityRuntime

open Shared

type internal Components =
    { Store: SessionActivityStore.SessionActivityStore
      Service: SessionActivityService.SessionActivityService }

type internal Runtime =
    { Components: Components
      SnapshotStore: OverviewSnapshotStore.OverviewSnapshotStore
      Capture: OverviewSnapshotCapture.SnapshotCapture }

let internal createComponents
    (dbPath: string)
    (scheduler: MailboxProcessor<RefreshScheduler.StateMsg>)
    =
    let store = new SessionActivityStore.SessionActivityStore(dbPath)

    try
        { Store = store
          Service = new SessionActivityService.SessionActivityService(store, scheduler) }
    with _ ->
        (store :> System.IDisposable).Dispose()
        reraise ()

let internal create
    (dbPath: string)
    (scheduler: MailboxProcessor<RefreshScheduler.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    =
    let snapshotStore = OverviewSnapshotStore.OverviewSnapshotStore(dbPath)
    let components = createComponents dbPath scheduler

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
