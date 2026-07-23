module Server.OverviewSnapshotCapture

open System
open System.Threading
open System.Threading.Tasks
open Shared

type internal CaptureClock =
    { UtcNow: unit -> DateTimeOffset
      WaitUntil: DateTimeOffset -> CancellationToken -> Async<unit> }

type internal CaptureDependencies =
    { CaptureState: unit -> Async<RefreshScheduler.DashboardState>
      LoadAssemblyInputs: DateTimeOffset -> WorktreeApi.OverviewAssemblyInputs
      IsReady:
        WorktreeApi.OverviewAssemblyInputs option ->
            RefreshScheduler.DashboardState ->
            bool
      AssembleRepos:
        WorktreeApi.OverviewAssemblyInputs ->
            RefreshScheduler.DashboardState ->
            RepoWorktrees list
      Aggregate: RepoWorktrees list -> OverviewData.Overview
      Insert: OverviewData.OverviewSnapshot -> bool }

let internal maximumStartDelay = TimeSpan.FromSeconds 1.0

let private systemClock =
    { UtcNow = fun () -> DateTimeOffset.UtcNow
      WaitUntil =
        fun target cancellationToken -> async {
            let delay = target - DateTimeOffset.UtcNow

            if delay > TimeSpan.Zero then
                do! Task.Delay(delay, cancellationToken) |> Async.AwaitTask
        } }

let internal snapshotAt
    (boundary: DateTimeOffset)
    (overview: OverviewData.Overview)
    : OverviewData.OverviewSnapshot =
    { Timestamp = boundary
      Tasks =
        overview.Tasks
        |> List.map (fun task ->
            { OverviewData.TaskCount.Kind = task.Kind
              Count = task.Count })
      Agents =
        overview.Agents
        |> List.map (fun agent ->
            { OverviewData.AgentCount.Kind = agent.Kind
              Count = agent.Count }) }

let internal captureBoundary
    (dependencies: CaptureDependencies)
    (boundary: DateTimeOffset)
    (cancellationToken: CancellationToken)
    =
    async {
        cancellationToken.ThrowIfCancellationRequested()
        let! state = dependencies.CaptureState()
        cancellationToken.ThrowIfCancellationRequested()

        let inputs =
            if Map.isEmpty state.Repos then
                None
            else
                Some(dependencies.LoadAssemblyInputs boundary)

        if dependencies.IsReady inputs state then
            let repos =
                inputs
                |> Option.map (fun readyInputs ->
                    dependencies.AssembleRepos readyInputs state)
                |> Option.defaultValue []

            let overview = dependencies.Aggregate repos
            let snapshot = snapshotAt boundary overview
            cancellationToken.ThrowIfCancellationRequested()
            dependencies.Insert snapshot |> ignore
    }

let rec private waitUntilReached
    (clock: CaptureClock)
    (boundary: DateTimeOffset)
    (cancellationToken: CancellationToken)
    =
    async {
        do! clock.WaitUntil boundary cancellationToken

        if clock.UtcNow() < boundary then
            return! waitUntilReached clock boundary cancellationToken
    }

let rec private runLoop
    (dependencies: CaptureDependencies)
    (clock: CaptureClock)
    (onError: DateTimeOffset -> exn -> unit)
    (cancellationToken: CancellationToken)
    =
    async {
        if not cancellationToken.IsCancellationRequested then
            let boundary =
                OverviewSnapshotBoundary.next (clock.UtcNow())

            try
                do! waitUntilReached clock boundary cancellationToken

                if not cancellationToken.IsCancellationRequested then
                    let barrierStartedAt = clock.UtcNow()

                    if barrierStartedAt <= boundary + maximumStartDelay then
                        try
                            do! captureBoundary dependencies boundary cancellationToken
                        with
                        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                            ()
                        | ex ->
                            onError boundary ex

                    return! runLoop dependencies clock onError cancellationToken
            with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                ()
    }

type internal SnapshotCapture
    (
        dependencies: CaptureDependencies,
        clock: CaptureClock,
        onError: DateTimeOffset -> exn -> unit
    ) =

    let runGate = new SemaphoreSlim(1, 1)

    member _.Run(cancellationToken: CancellationToken) : Async<unit> =
        async {
            if not (runGate.Wait 0) then
                invalidOp "The Overview snapshot capture loop is already running."

            try
                return! runLoop dependencies clock onError cancellationToken
            finally
                runGate.Release() |> ignore
        }

let internal create
    (scheduler: MailboxProcessor<RefreshScheduler.StateMsg>)
    (rootPaths: Map<RepoId, string>)
    (snapshotStore: OverviewSnapshotStore.OverviewSnapshotStore)
    =
    let dependencies =
        { CaptureState =
            fun () ->
                scheduler.PostAndAsyncReply RefreshScheduler.GetState
          LoadAssemblyInputs =
            fun boundary ->
                WorktreeApi.loadOverviewAssemblyInputs boundary rootPaths
          IsReady = WorktreeApi.isOverviewCaptureReady rootPaths
          AssembleRepos =
            fun inputs state ->
                WorktreeApi.assembleOverviewRepos inputs rootPaths state
          Aggregate = OverviewData.aggregate
          Insert = snapshotStore.Insert }

    SnapshotCapture(
        dependencies,
        systemClock,
        fun boundary ex ->
            Log.log
                "OverviewHistory"
                $"Snapshot capture failed for {boundary:O}: {ex.Message}"
    )
