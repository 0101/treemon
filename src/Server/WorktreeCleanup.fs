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

let private terminalSessionIds terminalIds =
    terminalIds
    |> Set.map (EmbeddedTerminalId.value >> TerminalSessionId)

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

let private closeHealthy prepare manager lease connection registry =
    async {
        match targetRecords lease registry with
        | Error error ->
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
            let prepared =
                records
                |> originPaths lease
                |> prepareSessionClose prepare

            do! beforeHostClose prepared activeIds
            let! closeResult =
                closeTarget
                    (EmbeddedTerminal.clientConfig manager)
                    connection
                    registry
                    []
                    records

            let closedIds, update, hostError =
                match closeResult with
                | Ok after ->
                    confirmedClosed targetIds initialAbsent (Some after),
                    ReconcileCleanup(
                        connection,
                        after,
                        RemoveCleanupTarget lease.Target
                    ),
                    None
                | Error(MutationRejected(after, error)) ->
                    let closed =
                        confirmedClosed targetIds initialAbsent (Some after)

                    closed,
                    ReconcileCleanup(
                        connection,
                        after,
                        RemoveClosedTerminals closed
                    ),
                    Some error
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
                    Some error

            let! snapshot =
                EmbeddedTerminal.applyCleanup manager update

            return
                afterHostClose prepared closedIds
                |> cleanupResult snapshot hostError
    }

let private closeReserved prepare manager (lease: CleanupLease) =
    async {
        let config = EmbeddedTerminal.clientConfig manager

        match! discoverHost config with
        | HealthyHost connection ->
            match! listTerminals config connection with
            | Ok registry ->
                return!
                    closeHealthy
                        prepare
                        manager
                        lease
                        connection
                        registry
            | Error error ->
                let! _ =
                    EmbeddedTerminal.applyCleanup
                        manager
                        (FailedCleanup error)

                return Error error
        | discovery ->
            match safeWithoutHealthyHost config lease.LastHost discovery with
            | Error error ->
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

                let! snapshot =
                    EmbeddedTerminal.applyCleanup
                        manager
                        (UnavailableCleanup(reason, lease.Target))

                return
                    afterHostClose prepared lease.CachedTerminalIds
                    |> cleanupResult snapshot None
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
    prepare
    manager
    target
    cancellation
    operation
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
                            closeReserved prepare manager lease
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

let internal closeEmbeddedTerminalWith prepare manager terminalId =
    withTerminalCleanupResult
        prepare
        manager
        (OneTerminal terminalId)
        Threading.CancellationToken.None
        (Ok >> async.Return)

let internal withTerminalCleanup prepare manager worktreePath operation =
    async {
        let! cancellation = Async.CancellationToken

        return!
            withTerminalCleanupResult
                prepare
                manager
                (WorktreeTerminals worktreePath)
                cancellation
                (fun _ -> operation ())
    }
