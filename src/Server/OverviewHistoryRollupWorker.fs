module Server.OverviewHistoryRollupWorker

open System
open System.Threading
open System.Threading.Tasks
open Server.OverviewHistoryReconstruction
open Server.OverviewHistoryRollup
open Server.SessionActivityStore

type internal RollupWorkerClock =
    { UtcNow: unit -> DateTimeOffset
      WaitUntil: DateTimeOffset -> CancellationToken -> Async<unit> }

type internal RollupWorkerHooks =
    { BeforeStage: RollupCandidate -> unit
      BeforePublish: RollupCandidate -> unit }

let private systemClock =
    { UtcNow = fun () -> DateTimeOffset.UtcNow
      WaitUntil =
        fun target cancellationToken -> async {
            let delay = target - DateTimeOffset.UtcNow

            if delay > TimeSpan.Zero then
                do! Task.Delay(delay, cancellationToken) |> Async.AwaitTask
        } }

let private noHooks =
    { BeforeStage = ignore
      BeforePublish = ignore }

let private batchEnd
    (startBoundary: DateTimeOffset)
    (endBoundary: DateTimeOffset)
    =
    let maximumSpan =
        TimeSpan.FromTicks(resolution.Ticks * int64 (maxBatchBoundaryCount - 1))

    min endBoundary (startBoundary + maximumSpan)

type private PlannedWork =
    | Current
    | Forward of RollupCandidate
    | Repair of RollupCandidate
    | Rebuild of RollupCandidate

let private plan (anchor: DateTimeOffset) (state: PublicationState) =
    let oldestRetained = oldestRetainedBoundary anchor

    match state.CompleteThrough with
    | None ->
        Rebuild
            { Generation = state.SourceGeneration
              StartBoundary = oldestRetained
              EndBoundary = anchor }
    | Some completeThrough
        when completeThrough < oldestRetained
             || (state.EarliestDirty |> Option.exists (fun dirty -> dirty < oldestRetained)) ->
        Rebuild
            { Generation = state.SourceGeneration
              StartBoundary = oldestRetained
              EndBoundary = anchor }
    | Some completeThrough ->
        match state.EarliestDirty with
        | Some dirty when dirty <= completeThrough ->
            Repair
                { Generation = state.SourceGeneration
                  StartBoundary = dirty
                  EndBoundary = max anchor completeThrough }
        | _ when completeThrough < anchor ->
            let startBoundary = completeThrough + resolution

            Forward
                { Generation = state.SourceGeneration
                  StartBoundary = startBoundary
                  EndBoundary = batchEnd startBoundary anchor }
        | _ -> Current

type private StagingOutcome =
    | RangeStaged
    | GenerationChanged

type OverviewHistoryRollupWorker internal
    (
        store: SessionActivityStore,
        clock: RollupWorkerClock,
        hooks: RollupWorkerHooks,
        onError: exn -> unit
    ) =

    let workerLease = store.ClaimOverviewRollupWorker()
    let cycleGate = new SemaphoreSlim(1, 1)
    let runGate = new SemaphoreSlim(1, 1)
    let dispositionGate = obj ()
    // Disposal state belongs to this lifetime-owning worker and cannot be modeled as immutable data.
    let mutable disposed = false

    let ensureActive () =
        lock dispositionGate (fun () ->
            if disposed then
                raise (ObjectDisposedException(nameof OverviewHistoryRollupWorker)))

    let stageRange
        (cancellationToken: CancellationToken)
        (candidate: RollupCandidate)
        =
        let rec stageFrom (startBoundary: DateTimeOffset) =
            cancellationToken.ThrowIfCancellationRequested()

            if startBoundary > candidate.EndBoundary then
                RangeStaged
            else
                let endBoundary = batchEnd startBoundary candidate.EndBoundary

                let batch =
                    { candidate with
                        StartBoundary = startBoundary
                        EndBoundary = endBoundary }

                let rows =
                    reconstructRange store startBoundary endBoundary
                    |> List.map (fun row ->
                        { Generation = candidate.Generation
                          Row = row })

                hooks.BeforeStage batch

                match store.StageOverviewRollup(batch, rows) with
                | StagingResult.Staged -> stageFrom (endBoundary + resolution)
                | StagingResult.SourceGenerationChanged _ -> GenerationChanged

        stageFrom candidate.StartBoundary

    let rec synchronize
        (cancellationToken: CancellationToken)
        (anchor: DateTimeOffset)
        publishedAny
        =
        cancellationToken.ThrowIfCancellationRequested()
        store.DiscardOverviewRollupStaging()
        let state = store.OverviewRollupState()

        let retry () =
            synchronize cancellationToken anchor publishedAny

        let continueAfter =
            function
            | PublicationResult.Published _ ->
                synchronize cancellationToken anchor true
            | PublicationResult.SourceGenerationChanged _ -> retry ()

        match plan anchor state with
        | Current -> state, publishedAny
        | Forward candidate
        | Repair candidate ->
            match stageRange cancellationToken candidate with
            | GenerationChanged -> retry ()
            | RangeStaged ->
                hooks.BeforePublish candidate
                store.PublishOverviewRollup candidate |> continueAfter
        | Rebuild candidate ->
            store.RebuildOverviewRollupObservationBounds()

            match stageRange cancellationToken candidate with
            | GenerationChanged -> retry ()
            | RangeStaged ->
                hooks.BeforePublish candidate
                store.ReplaceOverviewRollup candidate |> continueAfter

    let rec runLoop
        (this: OverviewHistoryRollupWorker)
        (cancellationToken: CancellationToken)
        =
        async {
            if not cancellationToken.IsCancellationRequested then
                let completed =
                    try
                        Some(this.Backfill cancellationToken)
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        None
                    | ex ->
                        onError ex
                        None

                if not cancellationToken.IsCancellationRequested then
                    let currentBoundary =
                        latestCompleteBoundary (clock.UtcNow())

                    let remainsBehind =
                        completed
                        |> Option.bind _.CompleteThrough
                        |> Option.exists (fun completeThrough ->
                            completeThrough < currentBoundary)

                    if remainsBehind then
                        return! runLoop this cancellationToken
                    else
                        try
                            do!
                                clock.WaitUntil
                                    (currentBoundary + resolution)
                                    cancellationToken

                            return! runLoop this cancellationToken
                        with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                            ()
        }

    new(store: SessionActivityStore) =
        new OverviewHistoryRollupWorker(
            store,
            systemClock,
            noHooks,
            fun ex -> Log.log "OverviewHistory" $"Rollup worker failed: {ex.Message}"
        )

    member _.Backfill(cancellationToken: CancellationToken) : PublicationState =
        ensureActive ()
        cycleGate.Wait cancellationToken

        try
            store.WithOverviewRollupMaintenance(fun () ->
                let anchor = latestCompleteBoundary (clock.UtcNow())
                let state, published = synchronize cancellationToken anchor false

                if published then
                    store.PruneOverviewRollup(oldestRetainedBoundary anchor) |> ignore

                state)
        finally
            cycleGate.Release() |> ignore

    member this.Run(cancellationToken: CancellationToken) : Async<unit> =
        async {
            ensureActive ()

            if not (runGate.Wait 0) then
                invalidOp "The Overview history rollup worker is already running."

            try
                return! runLoop this cancellationToken
            finally
                runGate.Release() |> ignore
        }

    interface IDisposable with
        member _.Dispose() =
            lock dispositionGate (fun () ->
                if not disposed then
                    disposed <- true
                    workerLease.Dispose())
