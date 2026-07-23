module Server.OverviewSnapshotCapture

open System
open System.Threading
open System.Threading.Tasks
open Shared

[<Literal>]
let private resolutionSeconds = 30L

let private resolution = TimeSpan.FromSeconds(float resolutionSeconds)

type internal CaptureClock =
    { UtcNow: unit -> DateTimeOffset
      WaitUntil: DateTimeOffset -> CancellationToken -> Async<unit> }

type internal CaptureDependencies =
    { GetState: unit -> Async<RefreshScheduler.DashboardState>
      GetActiveSessionPaths: unit -> Async<Set<string>>
      LoadAssemblyInputs:
        DateTimeOffset ->
            RefreshScheduler.DashboardState ->
            WorktreeApi.RepoAssemblyInputs
      AssembleRepos:
        WorktreeApi.RepoAssemblyInputs ->
            Set<string> ->
            RefreshScheduler.DashboardState ->
            RepoWorktrees list
      Aggregate: RepoWorktrees list -> OverviewData.Overview
      Insert: OverviewData.OverviewSnapshot -> bool }

let private systemClock =
    { UtcNow = fun () -> DateTimeOffset.UtcNow
      WaitUntil =
        fun target cancellationToken -> async {
            let delay = target - DateTimeOffset.UtcNow

            if delay > TimeSpan.Zero then
                do! Task.Delay(delay, cancellationToken) |> Async.AwaitTask
        } }

let internal nextBoundary (now: DateTimeOffset) =
    let unixSeconds = now.ToUnixTimeSeconds()
    let boundary = unixSeconds - unixSeconds % resolutionSeconds + resolutionSeconds
    DateTimeOffset.FromUnixTimeSeconds boundary

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
        let! state = dependencies.GetState()
        cancellationToken.ThrowIfCancellationRequested()
        let! activeSessionPaths = dependencies.GetActiveSessionPaths()
        cancellationToken.ThrowIfCancellationRequested()
        let inputs = dependencies.LoadAssemblyInputs boundary state
        let repos = dependencies.AssembleRepos inputs activeSessionPaths state
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
            let boundary = nextBoundary (clock.UtcNow())

            try
                do! waitUntilReached clock boundary cancellationToken

                if not cancellationToken.IsCancellationRequested then
                    let startedAt = clock.UtcNow()

                    if startedAt < boundary + resolution then
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
    (sessionAgent: SessionManager.SessionAgent)
    (activityStore: SessionActivityStore.SessionActivityStore option)
    (rootPaths: Map<RepoId, string>)
    (snapshotStore: OverviewSnapshotStore.OverviewSnapshotStore)
    =
    let dependencies =
        { GetState =
            fun () ->
                scheduler.PostAndAsyncReply RefreshScheduler.GetState
          GetActiveSessionPaths =
            fun () -> async {
                let! activeSessions = SessionManager.getActiveSessions sessionAgent
                return activeSessions |> Map.keys |> Set.ofSeq
            }
          LoadAssemblyInputs =
            fun boundary state ->
                WorktreeApi.loadRepoAssemblyInputs
                    boundary
                    activityStore
                    rootPaths
                    state
          AssembleRepos =
            fun inputs activeSessionPaths state ->
                WorktreeApi.assembleRepos
                    inputs
                    rootPaths
                    activeSessionPaths
                    state
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
