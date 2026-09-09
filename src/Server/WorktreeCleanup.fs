module Server.WorktreeCleanup

open System
open Shared
open Server.EmbeddedTerminal
open Server.SessionActivity
open Server.TerminalHostClient
open Server.TerminalHostManifest

/// Exact-session work bracketing the authoritative host close: the capture that produced this plan
/// already fixed the identities, so the graceful attempt and the monotonic closure act on the same
/// instances no matter what the registry does in between.
type SessionClosePlan =
    { BeforeHostClose: Set<TerminalSessionId> -> Async<unit>
      AfterHostClose: Set<TerminalSessionId> -> Result<unit, string> }

type PrepareSessionClose =
    Map<TerminalSessionId, WorktreePath> -> SessionClosePlan

let internal noSessionClose _ =
    { BeforeHostClose = fun _ -> async.Return()
      AfterHostClose = fun _ -> Ok() }

type private Stage = LifecycleDiagnostics.TeardownStage
type private HostOutcome = LifecycleDiagnostics.HostCloseOutcome

/// One teardown attempt as the authoritative registry left it: which terminals it proved closed,
/// what the manager must reconcile, and why the caller's mutation is refused when it is.
type private HostClose =
    { Outcome: HostOutcome
      Registry: (DiscoveryManifest * RegistrySnapshot) option
      ConfirmedClosed: Set<EmbeddedTerminalId>
      RemainingOnFailure: int
      Interruption: string option
      Failure: string option }

let private record (diagnostics: LifecycleDiagnostics.Sink) stage =
    diagnostics (LifecycleDiagnostics.Diagnostic.TeardownTransition stage)

let private diagnosticTarget =
    function
    | OneTerminal _ -> LifecycleDiagnostics.TeardownTarget.Terminal
    | WorktreeTerminals _ -> LifecycleDiagnostics.TeardownTarget.Worktree

let private terminalSessionIds terminalIds =
    terminalIds
    |> Set.map (EmbeddedTerminalId.value >> TerminalSessionId)

/// Nothing was proven closed, so the manager keeps every tab and marks it interrupted.
let private hostUnusable (lease: CleanupLease) error =
    { Outcome = HostOutcome.Unverified
      Registry = None
      ConfirmedClosed = Set.empty
      RemainingOnFailure = lease.CachedTerminalIds.Count
      Interruption = Some error
      Failure = Some error }

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

let private preparePlan prepare paths =
    try Ok(prepare paths)
    with _ -> Error "Could not prepare exact session cleanup"

/// A graceful attempt is best effort: its failure never authorizes claiming closure and never
/// prevents the host close of this user-authorized teardown.
let private gracefulShutdown prepared terminalIds =
    match prepared with
    | Error _ -> async.Return()
    | Ok(plan: SessionClosePlan) ->
        async {
            let! _ =
                plan.BeforeHostClose(terminalSessionIds terminalIds)
                |> Async.Catch

            return ()
        }

let private exactClosure prepared closedIds =
    match prepared with
    | Error error -> Error error
    | Ok(plan: SessionClosePlan) ->
        try plan.AfterHostClose(terminalSessionIds closedIds)
        with _ -> Error "Could not record exact session closure"

let private safeWithoutHealthyHost config lastHost = function
    | DeadHost error -> Ok $"{error}. Its terminals were interrupted."
    | MissingHost ->
        validateMissingHostGone config lastHost
        |> Result.map (fun () ->
            "TerminalHost is not running; no live terminal remains to close.")
    | IncompatibleHost(_, error)
    | UnusableHost error -> Error error
    | HealthyHost _ -> failwith "unreachable"

let rec private closeTarget config connection latest failures = function
    | [] ->
        match List.rev failures with
        | [] -> async.Return(Ok latest)
        | errors ->
            async.Return(
                Error(MutationRejected(latest, String.concat "; " errors))
            )
    | (terminal: TerminalRecord) :: remaining ->
        async {
            match! closeTerminalOnHost config connection terminal.SessionId with
            | Ok after ->
                return! closeTarget config connection after failures remaining
            | Error(MutationRejected(after, error)) ->
                return!
                    closeTarget config connection after (error :: failures) remaining
            | Error(MutationUnverified(lastRegistry, error)) ->
                return
                    Error(
                        MutationUnverified(
                            Some(Option.defaultValue latest lastRegistry),
                            error
                        )
                    )
        }

/// Registry absence is the only accepted proof that a terminal is closed; anything still listed,
/// and anything an unverified attempt could not relist, stays with the manager.
let private confirmedClosed targetIds initialAbsent registry =
    registry
    |> Option.map (fun snapshot ->
        targetIds
        |> Set.filter (fun terminalId ->
            snapshot.Terminals
            |> findTerminalById (EmbeddedTerminalId.value terminalId)
            |> Option.isNone))
    |> Option.defaultValue initialAbsent

let private finish diagnostics manager prepared hostClose =
    async {
        record
            diagnostics
            (Stage.HostCloseCompleted(
                hostClose.Outcome,
                hostClose.ConfirmedClosed.Count,
                hostClose.RemainingOnFailure
            ))

        let! snapshot =
            EmbeddedTerminal.applyCleanup
                manager
                { Registry = hostClose.Registry
                  ClosedTerminalIds = hostClose.ConfirmedClosed
                  Interruption = hostClose.Interruption }

        let closure =
            if Set.isEmpty hostClose.ConfirmedClosed then
                Ok()
            else
                exactClosure prepared hostClose.ConfirmedClosed

        let result =
            match hostClose.Failure, closure with
            | None, Ok() -> Ok snapshot
            | Some error, Ok() -> Error error
            | None, Error error -> Error error
            | Some host, Error session -> Error $"{host}; {session}"

        record diagnostics (if Result.isOk result then Stage.Completed else Stage.Failed)
        return result
    }

let private closeHealthy diagnostics prepare manager (lease: CleanupLease) connection registry =
    async {
        let records = targetRecords lease registry

        let prepared =
            records |> Result.defaultValue [] |> originPaths lease |> preparePlan prepare

        match records with
        | Error error ->
            // The trust boundary rejects the close before any host request is made.
            return!
                finish diagnostics manager prepared
                    { Outcome = HostOutcome.Rejected
                      Registry = Some(connection, registry)
                      ConfirmedClosed = Set.empty
                      RemainingOnFailure = lease.CachedTerminalIds.Count
                      Interruption = None
                      Failure = Some error }
        | Ok records ->
            let activeIds =
                records |> List.map (_.SessionId >> EmbeddedTerminalId) |> Set.ofList

            let targetIds = Set.union lease.CachedTerminalIds activeIds
            let initialAbsent = Set.difference targetIds activeIds

            record diagnostics (Stage.GracefulShutdownAttempted activeIds.Count)
            do! gracefulShutdown prepared activeIds

            let! closeResult =
                closeTarget
                    (EmbeddedTerminal.clientConfig manager)
                    connection
                    registry
                    []
                    records

            let outcome, lastRegistry, failure =
                match closeResult with
                | Ok after -> HostOutcome.Confirmed, Some after, None
                | Error(MutationRejected(after, error)) ->
                    HostOutcome.Rejected, Some after, Some error
                | Error(MutationUnverified(lastRegistry, error)) ->
                    HostOutcome.Unverified, lastRegistry, Some error

            let closed = confirmedClosed targetIds initialAbsent lastRegistry

            return!
                finish diagnostics manager prepared
                    { Outcome = outcome
                      Registry =
                        lastRegistry |> Option.map (fun snapshot -> connection, snapshot)
                      ConfirmedClosed = closed
                      RemainingOnFailure = targetIds.Count - closed.Count
                      Interruption =
                        if outcome = HostOutcome.Unverified then failure else None
                      Failure = failure }
    }

let private closeReserved diagnostics prepare manager (lease: CleanupLease) =
    async {
        let config = EmbeddedTerminal.clientConfig manager

        record
            diagnostics
            (Stage.Started(
                diagnosticTarget lease.Target,
                lease.CachedTerminalIds |> terminalSessionIds |> Set.toList
            ))

        let unusable error =
            finish diagnostics manager (Ok(noSessionClose ())) (hostUnusable lease error)

        match! discoverHost config with
        | HealthyHost connection ->
            match! listTerminals config connection with
            | Ok registry ->
                return! closeHealthy diagnostics prepare manager lease connection registry
            | Error error -> return! unusable error
        | discovery ->
            match safeWithoutHealthyHost config lease.LastHost discovery with
            | Error error -> return! unusable error
            | Ok reason ->
                return!
                    finish diagnostics manager (originPaths lease [] |> preparePlan prepare)
                        { Outcome = HostOutcome.Unavailable
                          Registry = None
                          ConfirmedClosed = lease.CachedTerminalIds
                          RemainingOnFailure = 0
                          Interruption = Some reason
                          Failure = None }
    }

let private asTask cancellation workflow =
    Async.StartAsTask(workflow, cancellationToken = cancellation)

/// A close request naming a terminal this manager has never listed still has to find the worktree
/// that owns it, otherwise the reservation would guard the wrong path.
let private reserveTarget manager target =
    async {
        match! EmbeddedTerminal.reserveCleanup manager target None with
        | Ok None ->
            match target with
            | WorktreeTerminals _ -> return Ok None
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
                        | None -> return Ok None
                        | Some terminal ->
                            return!
                                EmbeddedTerminal.reserveCleanup
                                    manager
                                    target
                                    (Some(PathUtils.toWorktreePath terminal.WorktreePath))
                | MissingHost
                | DeadHost _ -> return Ok None
                | IncompatibleHost(_, error)
                | UnusableHost error -> return Error error
        | result -> return result
    }

/// The teardown itself runs uncancellable so a cancelled caller can never abandon half-closed
/// terminals; only the caller's own mutation observes `cancellation`.
let private withTerminalCleanupResult
    (diagnostics: LifecycleDiagnostics.Sink)
    (prepare: PrepareSessionClose)
    (manager: EmbeddedTerminal.Manager)
    (target: CloseTarget)
    (cancellation: Threading.CancellationToken)
    (operation: EmbeddedTerminalSnapshot -> Async<Result<'value, string>>)
    : Async<Result<'value, string>>
    =
    task {
        let! reservation =
            reserveTarget manager target
            |> asTask Threading.CancellationToken.None

        match reservation with
        | Error error -> return Error error
        | Ok None ->
            let! snapshot =
                EmbeddedTerminal.getCached manager
                |> asTask Threading.CancellationToken.None

            return! operation snapshot |> asTask cancellation
        | Ok(Some lease) ->
            try
                match!
                    closeReserved diagnostics prepare manager lease
                    |> asTask Threading.CancellationToken.None
                with
                | Error error -> return Error error
                | Ok snapshot -> return! operation snapshot |> asTask cancellation
            finally
                EmbeddedTerminal.releaseCleanup manager lease
    }
    |> Async.AwaitTask

let internal closeEmbeddedTerminalWithDiagnostics diagnostics prepare manager terminalId =
    withTerminalCleanupResult
        diagnostics
        prepare
        manager
        (OneTerminal terminalId)
        Threading.CancellationToken.None
        (Ok >> async.Return)

let internal closeEmbeddedTerminalWith prepare manager terminalId =
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

let internal withTerminalCleanup prepare manager worktreePath operation =
    withTerminalCleanupWithDiagnostics
        LifecycleDiagnostics.write
        prepare
        manager
        worktreePath
        operation
