module Server.WorktreeCleanup

open System
open Shared
open Server.EmbeddedTerminal
open Server.SessionActivity
open Server.TerminalHostClient
open Server.TerminalHostManifest

type SessionClosePlan =
    { BeforeHostClose: Set<TerminalSessionId> -> Async<unit>
      AfterHostClose: Set<TerminalSessionId> -> Result<unit, string> }

type PrepareSessionClose =
    Map<TerminalSessionId, WorktreePath> -> SessionClosePlan

let internal noSessionClose _ =
    { BeforeHostClose = fun _ -> async.Return()
      AfterHostClose = fun _ -> Ok() }

let private record
    (diagnostics: LifecycleDiagnostics.Sink)
    stage
    =
    diagnostics (
        LifecycleDiagnostics.Diagnostic.TeardownTransition
            stage
    )

let private diagnosticTarget =
    function
    | OneTerminal _ ->
        LifecycleDiagnostics.TeardownTarget.Terminal
    | WorktreeTerminals _ ->
        LifecycleDiagnostics.TeardownTarget.Worktree

let private terminalSessionIds terminalIds =
    terminalIds
    |> Set.map (EmbeddedTerminalId.value >> TerminalSessionId)

let private recordHostCloseFailure
    diagnostics
    (lease: CleanupLease)
    outcome
    =
    let terminalIds =
        lease.CachedTerminalIds
        |> terminalSessionIds
        |> Set.toList

    record
        diagnostics
        (LifecycleDiagnostics.TeardownStage.Started(
            diagnosticTarget lease.Target,
            terminalIds
        ))

    record
        diagnostics
        (LifecycleDiagnostics.TeardownStage.HostCloseCompleted(
            outcome,
            terminalIds.Length,
            0
        ))

    record
        diagnostics
        (LifecycleDiagnostics.TeardownStage.Failed
            terminalIds.Length)

let private targetRecords (lease: CleanupLease) (registry: RegistrySnapshot) =
    let pathMatches (terminal: TerminalRecord) =
        samePath terminal.WorktreePath (WorktreePath.value lease.WorktreePath)

    let misplaced =
        lease.CachedTerminalIds
        |> Seq.exists (fun terminalId ->
            registry.Terminals
            |> findTerminalById (EmbeddedTerminalId.value terminalId)
            |> Option.exists (pathMatches >> not))

    if misplaced then
        Error "The embedded terminal is registered for a different worktree"
    else
        match lease.Target with
        | OneTerminal terminalId ->
            registry.Terminals
            |> findTerminalById (EmbeddedTerminalId.value terminalId)
            |> Option.toList
            |> Ok
        | WorktreeTerminals _ ->
            registry.Terminals |> List.filter pathMatches |> Ok

let private originPaths (lease: CleanupLease) records =
    records
    |> List.fold (fun paths (terminal: TerminalRecord) ->
        paths
        |> Map.add
            (TerminalSessionId terminal.SessionId)
            (PathUtils.toWorktreePath terminal.WorktreePath))
        (lease.CachedTerminalIds
         |> Seq.map (fun terminalId ->
             terminalId |> EmbeddedTerminalId.value |> TerminalSessionId,
             lease.WorktreePath)
         |> Map.ofSeq)

let private prepareSessionClose prepare paths =
    try Ok(prepare paths)
    with _ -> Error "Could not prepare exact session cleanup"

let private beforeHostClose prepared terminalIds =
    match prepared with
    | Error _ -> async.Return()
    | Ok plan ->
        async {
            let! _ =
                plan.BeforeHostClose(terminalSessionIds terminalIds)
                |> Async.Catch

            return ()
        }

let private afterHostClose prepared terminalIds =
    if Set.isEmpty terminalIds then
        Ok()
    else
        match prepared with
        | Error error -> Error error
        | Ok plan ->
            try plan.AfterHostClose(terminalSessionIds terminalIds)
            with _ -> Error "Could not record exact session closure"

let private cleanupResult snapshot hostError sessionResult =
    match hostError, sessionResult with
    | None, Ok() -> Ok snapshot
    | Some error, Ok() -> Error error
    | None, Error error -> Error error
    | Some host, Error session -> Error $"{host}; {session}"

let private safeWithoutHealthyHost config lastHost = function
    | DeadHost _ -> Ok()
    | MissingHost ->
        match knownHostIsStillLive config lastHost with
        | Ok false -> Ok()
        | Ok true -> Error "The TerminalHost manifest is missing while the exact recorded host is still running"
        | Error error -> Error error
    | IncompatibleHost(_, error)
    | UnusableHost error -> Error error
    | HealthyHost _ -> failwith "unreachable"

let rec private closeTarget config connection latest failures = function
    | [] ->
        match List.rev failures with
        | [] -> async.Return(Ok latest)
        | errors ->
            async.Return(
                Error(
                    MutationRejected(
                        latest,
                        String.concat "; " errors
                    )
                )
            )
    | (terminal: TerminalRecord) :: remaining ->
        async {
            match! closeTerminalOnHost config connection terminal.SessionId with
            | Ok after ->
                return!
                    closeTarget
                        config
                        connection
                        after
                        failures
                        remaining
            | Error(MutationRejected(after, error)) ->
                return!
                    closeTarget
                        config
                        connection
                        after
                        (error :: failures)
                        remaining
            | Error(MutationUnverified(None, error)) ->
                return Error(MutationUnverified(Some latest, error))
            | Error(MutationUnverified(Some registry, error)) ->
                return Error(MutationUnverified(Some registry, error))
        }

let private confirmedClosed targetIds initialAbsent registry =
    registry
    |> Option.map (fun snapshot ->
        targetIds
        |> Set.filter (fun terminalId ->
            snapshot.Terminals
            |> findTerminalById (EmbeddedTerminalId.value terminalId)
            |> Option.isNone))
    |> Option.defaultValue initialAbsent

let private closeHealthy
    (diagnostics: LifecycleDiagnostics.Sink)
    prepare
    manager
    lease
    connection
    registry
    =
    async {
        match targetRecords lease registry with
        | Error error ->
            let cachedIds =
                lease.CachedTerminalIds
                |> terminalSessionIds
                |> Set.toList

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.Started(
                    diagnosticTarget lease.Target,
                    cachedIds
                ))

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.Failed
                    cachedIds.Length)

            let! _ =
                EmbeddedTerminal.applyCleanup
                    manager
                    (ReconcileCleanup(
                        connection,
                        registry,
                        KeepCleanupTargets
                    ))

            return Error error
        | Ok records ->
            let activeIds =
                records
                |> List.map (_.SessionId >> EmbeddedTerminalId)
                |> Set.ofList

            let targetIds = Set.union lease.CachedTerminalIds activeIds
            let initialAbsent = Set.difference targetIds activeIds
            let diagnosticTerminalIds =
                targetIds
                |> terminalSessionIds
                |> Set.toList

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.Started(
                    diagnosticTarget lease.Target,
                    diagnosticTerminalIds
                ))

            let prepared =
                records
                |> originPaths lease
                |> prepareSessionClose prepare

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.GracefulShutdownStarted
                    activeIds.Count)

            do! beforeHostClose prepared activeIds

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.GracefulShutdownCompleted
                    activeIds.Count)

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.HostCloseStarted
                    activeIds.Count)

            let! closeResult =
                closeTarget
                    (EmbeddedTerminal.clientConfig manager)
                    connection
                    registry
                    []
                    records

            let closedIds, update, hostError, hostOutcome =
                match closeResult with
                | Ok after ->
                    confirmedClosed targetIds initialAbsent (Some after),
                    ReconcileCleanup(
                        connection,
                        after,
                        RemoveCleanupTarget lease.Target
                    ),
                    None,
                    LifecycleDiagnostics.HostCloseOutcome.Confirmed
                | Error(MutationRejected(after, error)) ->
                    let closed =
                        confirmedClosed targetIds initialAbsent (Some after)

                    closed,
                    ReconcileCleanup(
                        connection,
                        after,
                        RemoveClosedTerminals closed
                    ),
                    Some error,
                    LifecycleDiagnostics.HostCloseOutcome.Rejected
                | Error(MutationUnverified(lastRegistry, error)) ->
                    let closed =
                        confirmedClosed targetIds initialAbsent lastRegistry

                    closed,
                    UnverifiedCleanup(
                        connection,
                        lastRegistry,
                        closed,
                        error
                    ),
                    Some error,
                    LifecycleDiagnostics.HostCloseOutcome.Unverified

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.HostCloseCompleted(
                    hostOutcome,
                    activeIds.Count,
                    closedIds.Count
                ))

            let! snapshot =
                EmbeddedTerminal.applyCleanup manager update

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.ExactClosureStarted
                    closedIds.Count)

            let sessionResult =
                afterHostClose prepared closedIds

            let closureOutcome =
                if Result.isOk sessionResult then
                    LifecycleDiagnostics.TeardownClosureOutcome.Recorded
                else
                    LifecycleDiagnostics.TeardownClosureOutcome.Failed

            record
                diagnostics
                (LifecycleDiagnostics.TeardownStage.ExactClosureCompleted(
                    closureOutcome,
                    closedIds.Count
                ))

            let result =
                cleanupResult snapshot hostError sessionResult

            record
                diagnostics
                (match result with
                 | Ok _ ->
                     LifecycleDiagnostics.TeardownStage.Completed
                         closedIds.Count
                 | Error _ ->
                     LifecycleDiagnostics.TeardownStage.Failed(
                         targetIds.Count - closedIds.Count
                     ))

            return result
    }

let private closeReserved
    (diagnostics: LifecycleDiagnostics.Sink)
    prepare
    manager
    (lease: CleanupLease)
    =
    async {
        let config = EmbeddedTerminal.clientConfig manager

        match! discoverHost config with
        | HealthyHost connection ->
            match! listTerminals config connection with
            | Ok registry ->
                return!
                    closeHealthy
                        diagnostics
                        prepare
                        manager
                        lease
                        connection
                        registry
            | Error error ->
                recordHostCloseFailure
                    diagnostics
                    lease
                    LifecycleDiagnostics.HostCloseOutcome.Unverified

                let! _ =
                    EmbeddedTerminal.applyCleanup
                        manager
                        (FailedCleanup error)

                return Error error
        | discovery ->
            match safeWithoutHealthyHost config lease.LastHost discovery with
            | Error error ->
                recordHostCloseFailure
                    diagnostics
                    lease
                    LifecycleDiagnostics.HostCloseOutcome.Unverified

                let! _ =
                    EmbeddedTerminal.applyCleanup
                        manager
                        (FailedCleanup error)

                return Error error
            | Ok() ->
                let reason =
                    match discovery with
                    | DeadHost error -> $"{error}. Its terminals were interrupted."
                    | MissingHost -> "TerminalHost is not running; no live terminal remains to close."
                    | _ -> failwith "unreachable"

                let prepared =
                    originPaths lease []
                    |> prepareSessionClose prepare

                let terminalIds =
                    lease.CachedTerminalIds
                    |> terminalSessionIds
                    |> Set.toList

                record
                    diagnostics
                    (LifecycleDiagnostics.TeardownStage.Started(
                        diagnosticTarget lease.Target,
                        terminalIds
                    ))

                record
                    diagnostics
                    (LifecycleDiagnostics.TeardownStage.HostCloseCompleted(
                        LifecycleDiagnostics.HostCloseOutcome.Unavailable,
                        terminalIds.Length,
                        terminalIds.Length
                    ))

                let! snapshot =
                    EmbeddedTerminal.applyCleanup
                        manager
                        (UnavailableCleanup(reason, lease.Target))

                record
                    diagnostics
                    (LifecycleDiagnostics.TeardownStage.ExactClosureStarted
                        terminalIds.Length)

                let sessionResult =
                    afterHostClose prepared lease.CachedTerminalIds

                let closureOutcome =
                    if Result.isOk sessionResult then
                        LifecycleDiagnostics.TeardownClosureOutcome.Recorded
                    else
                        LifecycleDiagnostics.TeardownClosureOutcome.Failed

                record
                    diagnostics
                    (LifecycleDiagnostics.TeardownStage.ExactClosureCompleted(
                        closureOutcome,
                        terminalIds.Length
                    ))

                let result =
                    cleanupResult snapshot None sessionResult

                record
                    diagnostics
                    (match result with
                     | Ok _ ->
                         LifecycleDiagnostics.TeardownStage.Completed
                             terminalIds.Length
                     | Error _ ->
                         LifecycleDiagnostics.TeardownStage.Failed
                             terminalIds.Length)

                return result
    }

let private asTask cancellation workflow =
    Async.StartAsTask(workflow, cancellationToken = cancellation)

let private reserveTarget manager target =
    async {
        match! EmbeddedTerminal.reserveCleanup manager target None with
        | Ok(NoCleanupNeeded snapshot) ->
            match target with
            | WorktreeTerminals _ -> return Ok(NoCleanupNeeded snapshot)
            | OneTerminal terminalId ->
                let config = EmbeddedTerminal.clientConfig manager

                match! discoverHost config with
                | HealthyHost connection ->
                    match! listTerminals config connection with
                    | Error error -> return Error error
                    | Ok registry ->
                        match
                            registry.Terminals
                            |> findTerminalById (EmbeddedTerminalId.value terminalId)
                        with
                        | None -> return Ok(NoCleanupNeeded snapshot)
                        | Some terminal ->
                            return!
                                EmbeddedTerminal.reserveCleanup
                                    manager
                                    target
                                    (Some(
                                        PathUtils.toWorktreePath
                                            terminal.WorktreePath
                                    ))
                | MissingHost
                | DeadHost _ -> return Ok(NoCleanupNeeded snapshot)
                | IncompatibleHost(_, error)
                | UnusableHost error -> return Error error
        | result -> return result
    }

let private withTerminalCleanupResult
    (diagnostics: LifecycleDiagnostics.Sink)
    (prepare: PrepareSessionClose)
    (manager: EmbeddedTerminal.Manager)
    (target: CloseTarget)
    (cancellation: Threading.CancellationToken)
    (
        operation:
            EmbeddedTerminalSnapshot
                -> Async<Result<'value, string>>
    )
    : Async<Result<'value, string>>
    =
    let reservation =
        reserveTarget manager target
        |> asTask Threading.CancellationToken.None

    async {
        return!
            task {
                let! reservation = reservation

                match reservation with
                | Error error -> return Error error
                | Ok(NoCleanupNeeded snapshot) ->
                    return!
                        operation snapshot
                        |> asTask cancellation
                | Ok(CleanupReserved lease) ->
                    try
                        match!
                            closeReserved
                                diagnostics
                                prepare
                                manager
                                lease
                            |> asTask Threading.CancellationToken.None
                        with
                        | Error error -> return Error error
                        | Ok snapshot ->
                            return!
                                operation snapshot
                                |> asTask cancellation
                    finally
                        EmbeddedTerminal.releaseCleanup manager lease
            }
            |> Async.AwaitTask
    }

let internal closeEmbeddedTerminalWithDiagnostics
    diagnostics
    prepare
    manager
    terminalId
    =
    withTerminalCleanupResult
        diagnostics
        prepare
        manager
        (OneTerminal terminalId)
        Threading.CancellationToken.None
        (Ok >> async.Return)

let internal closeEmbeddedTerminalWith
    prepare
    manager
    terminalId
    =
    closeEmbeddedTerminalWithDiagnostics
        LifecycleDiagnostics.write
        prepare
        manager
        terminalId

let internal withTerminalCleanupWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    (prepare: PrepareSessionClose)
    (manager: EmbeddedTerminal.Manager)
    (worktreePath: WorktreePath)
    (operation: unit -> Async<Result<'value, string>>)
    : Async<Result<'value, string>>
    =
    async {
        let! cancellation = Async.CancellationToken

        return!
            withTerminalCleanupResult
                diagnostics
                prepare
                manager
                (WorktreeTerminals worktreePath)
                cancellation
                (fun _ -> operation ())
    }

let internal withTerminalCleanup
    prepare
    manager
    worktreePath
    operation
    =
    withTerminalCleanupWithDiagnostics
        LifecycleDiagnostics.write
        prepare
        manager
        worktreePath
        operation
