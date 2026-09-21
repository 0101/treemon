module Server.EmbeddedTerminal

open System
open Shared
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Server.TerminalHostReplacement

[<NoEquality; NoComparison>]
type private PendingTerminalHostUpdate =
    { QueryRestartSessions: RestartSessionQuery
      Operations: Operations
      RequestedAtTick: int64 }

[<RequireQualifiedAccess>]
type private MaintenanceState =
    | Unlocked
    | WaitingForCleanup of PendingTerminalHostUpdate
    | Updating of RestartSession list
    | Fatal of error: string

[<RequireQualifiedAccess>]
type private ReconciliationMode =
    | RefreshOnly
    | RebindAfterHostChange
    | PreserveDuringCleanup

type private CleanupReservation =
    { Token: Guid
      AcquiredAtTick: int64 }

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      LastHost: DiscoveryManifest option
      Maintenance: MaintenanceState
      LastUpdateAvailabilityError: string option
      CleanupReservations: Map<string, CleanupReservation> }

type internal CloseTarget = OneTerminal of EmbeddedTerminalId | WorktreeTerminals of WorktreePath

type internal CleanupLease =
    { Token: Guid; Target: CloseTarget
      WorktreePath: WorktreePath
      CachedTerminalIds: Set<EmbeddedTerminalId>
      LastHost: DiscoveryManifest option }

/// The single state change a finished teardown applies: the authoritative registry it reached (when
/// it reached one), the terminals that registry proved closed, and the message to stamp on whatever
/// the manager still believes is running.
type internal CleanupCompletion =
    { Registry: (DiscoveryManifest * RegistrySnapshot) option
      ClosedTerminalIds: Set<EmbeddedTerminalId>
      Interruption: string option }

type private Message =
    | Start of WorktreePath * command: string option * AsyncReplyChannel<Result<EmbeddedTerminalStartResult, string>>
    | RunTerminalAction of
        (unit -> Async<Result<unit, string>>) *
        AsyncReplyChannel<Result<unit, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | GetCached of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ReserveCleanup of CloseTarget * WorktreePath option * Guid * AsyncReplyChannel<Result<CleanupLease option, string>>
    | ApplyCleanup of CleanupCompletion * AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ReleaseCleanup of Guid * AsyncReplyChannel<unit>
    | GetUpdateState of AsyncReplyChannel<TerminalHostUpdateState>
    | BeginTerminalHostUpdate
    | UpdateTerminalHost of
        RestartSessionQuery *
        Operations *
        AsyncReplyChannel<TerminalHostUpdateState>
    | FinishTerminalHostUpdate of
        Result<DiscoveryManifest * RegistrySnapshot, string>

type Manager = private | Manager of Config * MailboxProcessor<Message>

let private interrupted error tab =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Running _ ->
        { tab with
            Lifecycle = EmbeddedTerminalLifecycle.Interrupted error }
    | EmbeddedTerminalLifecycle.Interrupted _ ->
        tab

let private interruptSnapshot error snapshot =
    { Tabs = snapshot.Tabs |> List.map (interrupted error) }

let private tabForRecord (terminal: TerminalHostClient.TerminalRecord) =
    { Id = EmbeddedTerminalId terminal.SessionId
      Worktree = PathUtils.toWorktreePath terminal.WorktreePath
      ReportedActivity = None
      SessionIds = []
      Lifecycle = EmbeddedTerminalLifecycle.Running terminal.AttachmentEndpoint }

let private reconcileSnapshot mode previousHost currentHost (records: TerminalHostClient.TerminalRecord list) (snapshot: EmbeddedTerminalSnapshot) =
    let resetTabs, preserveMissing =
        match mode with
        | ReconciliationMode.RefreshOnly -> false, false
        | ReconciliationMode.RebindAfterHostChange -> true, false
        | ReconciliationMode.PreserveDuringCleanup -> false, true

    let resetTabs =
        resetTabs
        || (previousHost |> Option.exists (fun previous -> not (hostIdentityMatches previous currentHost)))

    if resetTabs then
        { Tabs = records |> List.map tabForRecord }
    else
        let recordsById =
            records
            |> List.map (fun terminal -> EmbeddedTerminalId terminal.SessionId, terminal)
            |> Map.ofList

        let previousIds =
            snapshot.Tabs |> List.map _.Id |> Set.ofList

        { Tabs =
            (snapshot.Tabs
             |> List.choose (fun tab ->
                 match Map.tryFind tab.Id recordsById with
                 | Some terminal -> Some(tabForRecord terminal)
                 | None when preserveMissing -> Some tab
                 | None -> None))
            @ (records
               |> List.filter (fun terminal ->
                   not (Set.contains (EmbeddedTerminalId terminal.SessionId) previousIds))
               |> List.map tabForRecord) }

let private applyRegistryWith mode (state: ManagerState) (manifest: DiscoveryManifest) (registry: RegistrySnapshot) =
    { state with
        LastSnapshot =
            reconcileSnapshot mode state.LastHost manifest registry.Terminals state.LastSnapshot
        LastHost = Some manifest }

let private applyRegistry =
    applyRegistryWith ReconciliationMode.RefreshOnly

let private withHostFailure error (state: ManagerState) =
    { state with LastSnapshot = interruptSnapshot error state.LastSnapshot }

let private getTerminals config (state: ManagerState) =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            match! listTerminals config connection with
            | Ok registry -> return applyRegistry state connection registry
            | Error error -> return withHostFailure error state
        | MissingHost -> return withHostFailure "TerminalHost discovery is missing; running terminals can no longer be verified." state
        | DeadHost error -> return withHostFailure $"{error}. Its terminals were interrupted." state
        | IncompatibleHost(_, error)
        | UnusableHost error -> return withHostFailure error state
    }

let private mutationFailure (state: ManagerState) connection = function
    | MutationUnverified(lastRegistry, error) ->
        let current =
            lastRegistry
            |> Option.map (applyRegistry state connection)
            |> Option.defaultValue state

        withHostFailure error current, error
    | MutationRejected(registry, error) ->
        applyRegistry state connection registry, error

let private mutationResult prepare (state: ManagerState) connection = function
    | Error failure ->
        let next, error = mutationFailure state connection failure
        next, Error error
    | Ok registry ->
        let next = applyRegistry (prepare state) connection registry
        next, Ok next.LastSnapshot

let private matchesTarget target tab =
    match target with
    | OneTerminal terminalId -> tab.Id = terminalId
    | WorktreeTerminals path ->
        samePath (WorktreePath.value tab.Worktree) (WorktreePath.value path)

let private removeTarget target (state: ManagerState) =
    { state with
        LastSnapshot.Tabs =
            state.LastSnapshot.Tabs |> List.filter (matchesTarget target >> not) }

let private targetTerminalIds target fallback (snapshot: EmbeddedTerminalSnapshot) =
    let cached =
        snapshot.Tabs
        |> List.filter (matchesTarget target)
        |> List.map _.Id
        |> Set.ofList

    match target, fallback with
    | OneTerminal terminalId, Some _ -> Set.add terminalId cached
    | _ -> cached

let private targetWorktree (state: ManagerState) target fallback =
    match target with
    | WorktreeTerminals path -> Some path
    | OneTerminal terminalId ->
        state.LastSnapshot.Tabs |> List.tryFind (fun tab -> tab.Id = terminalId)
        |> Option.map _.Worktree |> Option.orElse fallback

let private removeTerminalIds (terminalIds: Set<EmbeddedTerminalId>) (state: ManagerState) =
    { state with
        LastSnapshot.Tabs =
            state.LastSnapshot.Tabs
            |> List.filter (fun tab -> not (terminalIds.Contains tab.Id)) }

let private applyCleanupCompletion (state: ManagerState) completion =
    let remaining = removeTerminalIds completion.ClosedTerminalIds state

    let reconciled =
        match completion.Registry with
        | Some(manifest, registry) ->
            applyRegistryWith
                ReconciliationMode.PreserveDuringCleanup
                remaining
                manifest
                registry
        | None -> remaining

    match completion.Interruption with
    | Some error -> withHostFailure error reconciled
    | None -> reconciled

let private deliverCommand config attachmentEndpoint command =
    async {
        try
            return! config.SendTerminalCommand attachmentEndpoint command
        with _ ->
            return Error "Could not submit the terminal command"
    }

let private closeStartedTerminal config (state: ManagerState) connection terminalId =
    async {
        let! result =
            closeTerminalOnHost config connection (EmbeddedTerminalId.value terminalId)

        return
            result
            |> mutationResult (removeTarget (OneTerminal terminalId)) state connection
    }

let private startTerminal config (state: ManagerState) worktreePath command =
    async {
        let validatedCommand =
            match command with
            | None -> Ok None
            | Some value -> validateTerminalCommand value |> Result.map Some

        match validatedCommand with
        | Error error -> return state, Error error
        | Ok command ->
            match! ensureHost config state.LastHost with
            | Error error -> return withHostFailure error state, Error error
            | Ok connection ->
                match! startTerminalOnHost config connection (WorktreePath.value worktreePath) with
                | Error failure ->
                    let next, error = mutationFailure state connection failure
                    return next, Error error
                | Ok(registry, terminal) ->
                    let next = applyRegistry state connection registry
                    let terminalId = EmbeddedTerminalId terminal.SessionId

                    let started =
                        { Snapshot = next.LastSnapshot
                          TerminalId = terminalId }

                    let fail current error =
                        async {
                            let! afterCleanup, cleanupResult =
                                closeStartedTerminal config current connection terminalId

                            let message =
                                match cleanupResult with
                                | Ok _ -> error
                                | Error cleanupError ->
                                    $"{error}; could not close the new embedded terminal: {cleanupError}"

                            return afterCleanup, Error message
                        }

                    match command with
                    | None -> return next, Ok started
                    | Some validated ->
                        match! deliverCommand config terminal.AttachmentEndpoint validated with
                        | Error error -> return! fail next error
                        | Ok() ->
                            match! confirmTerminalOnHost config connection terminal.SessionId with
                            | Ok retainedRegistry ->
                                let retained = applyRegistry next connection retainedRegistry
                                return retained, Ok { started with Snapshot = retained.LastSnapshot }
                            | Error failure ->
                                let current, error = mutationFailure next connection failure
                                return! fail current error
    }

let private updateInProgressError =
    "TerminalHost update is in progress; terminal actions remain locked until it completes."

let private fatalUpdateMessage =
    "TerminalHost update failed. Terminal actions remain locked. Redeploy or restart Treemon manually from an external PowerShell window."

let private cleanupInProgressError = "Terminal cleanup is in progress for this worktree; try again when it completes."

let private terminalHostUpdateState config (state: ManagerState) =
    match state.Maintenance with
    | MaintenanceState.WaitingForCleanup _
    | MaintenanceState.Updating _ ->
        state, TerminalHostUpdateState.Updating
    | MaintenanceState.Fatal _ ->
        state, TerminalHostUpdateState.Fatal
    | MaintenanceState.Unlocked ->
        match state.LastHost with
        | None ->
            { state with
                LastUpdateAvailabilityError = None },
            TerminalHostUpdateState.Unavailable
        | Some host ->
            match
                TerminalHostReplacement.updateAvailable
                    config
                    host
            with
            | Ok available ->
                { state with
                    LastUpdateAvailabilityError = None },
                if available then
                    TerminalHostUpdateState.Available
                else
                    TerminalHostUpdateState.Unavailable
            | Error error ->
                if
                    state.LastUpdateAvailabilityError
                    <> Some error
                then
                    Log.log
                        "TerminalHost"
                        $"Could not determine update availability: {error}"

                { state with
                    LastUpdateAvailabilityError = Some error },
                TerminalHostUpdateState.Unavailable

let private terminalMutationLockError (state: ManagerState) =
    match state.Maintenance with
    | MaintenanceState.Unlocked -> None
    | MaintenanceState.WaitingForCleanup _
    | MaintenanceState.Updating _ ->
        Some updateInProgressError
    | MaintenanceState.Fatal error ->
        Some error

let private maintenanceKind =
    function
    | MaintenanceState.Unlocked -> "unlocked"
    | MaintenanceState.WaitingForCleanup _ -> "waiting-for-cleanup"
    | MaintenanceState.Updating _ -> "updating"
    | MaintenanceState.Fatal _ -> "fatal"

let private enterFatalUpdate error (state: ManagerState) =
    Log.log
        "TerminalHost"
        $"Update entered a permanent fatal state: {error}"

    { withHostFailure fatalUpdateMessage state with
        Maintenance =
            MaintenanceState.Fatal fatalUpdateMessage },
    TerminalHostUpdateState.Fatal

let private prepareUpdate
    config
    (state: ManagerState)
    (queryRestartSessions: RestartSessionQuery)
    =
    async {
        try
            match! discoverHost config with
            | HealthyHost host ->
                match
                    TerminalHostReplacement.tryStagedExecutable
                        config
                        host
                with
                | Error error ->
                    return state, Error error
                | Ok None ->
                    return
                        { state with LastHost = Some host },
                        Ok None
                | Ok(Some stagedExecutable) ->
                    match! listTerminals config host with
                    | Error error ->
                        return
                            state,
                            Error
                                $"Could not capture the hosted terminals: {error}"
                    | Ok registry ->
                        let current =
                            applyRegistry
                                state
                                host
                                registry

                        match
                            TerminalHostReplacement.hostedTerminals
                                registry.Terminals
                        with
                        | Error error ->
                            return current, Error error
                        | Ok terminals ->
                            match queryRestartSessions terminals with
                            | Error error ->
                                return
                                    current,
                                    Error
                                        $"Could not capture hosted sessions: {error}"
                            | Ok sessions ->
                                return
                                    current,
                                    Ok(
                                        Some(
                                            host,
                                            stagedExecutable,
                                            sessions
                                        )
                                    )
            | MissingHost ->
                return
                    state,
                    Error "TerminalHost is not running"
            | DeadHost error
            | UnusableHost error
            | IncompatibleHost(_, error) ->
                return state, Error error
        with error ->
            Log.logException
                "TerminalHost"
                "Could not prepare the TerminalHost update"
                error

            return
                state,
                Error
                    "An unexpected error occurred while preparing the TerminalHost update"
    }

let private runUpdate
    operations
    config
    host
    stagedExecutable
    sessions
    =
    async {
        try
            return!
                TerminalHostReplacement.runWithOperations
                    operations
                    config
                    host
                    stagedExecutable
                    sessions
        with error ->
            Log.logException
                "TerminalHost"
                "TerminalHost update transaction crashed"
                error

            return
                Error
                    "An unexpected error occurred during the TerminalHost update"
    }

let private cleanupPathKey worktreePath =
    let path = worktreePath |> WorktreePath.value |> Option.ofObj |> Option.defaultValue ""

    try
        path |> PathUtils.normalizePath |> pathKey
    with _ ->
        pathKey path

let private cleanupLeaseCorrelation (token: Guid) =
    token.ToString("N")[..7]

let private elapsedMilliseconds startedAtTick =
    max 0L (Environment.TickCount64 - startedAtTick)

let private cleanupLeaseSummaries
    (reservations: Map<string, CleanupReservation>)
    =
    reservations
    |> Map.toSeq
    |> Seq.map snd
    |> Seq.sortBy (fun (reservation: CleanupReservation) ->
        cleanupLeaseCorrelation reservation.Token)
    |> Seq.truncate 4
    |> Seq.map (fun (reservation: CleanupReservation) ->
        $"{cleanupLeaseCorrelation reservation.Token}:{elapsedMilliseconds reservation.AcquiredAtTick}ms")
    |> String.concat ","

let private cleanupTargetKind =
    function
    | OneTerminal _ -> "terminal"
    | WorktreeTerminals _ -> "worktree"

let private respond (channel: AsyncReplyChannel<'value>) value state = channel.Reply value; state

let private cleanupReserved (state: ManagerState) path =
    state.CleanupReservations |> Map.containsKey (cleanupPathKey path)

let internal createWithConfig config =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop state =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Get reply
                        when terminalMutationLockError state
                             |> Option.isSome ->
                        return! loop (respond reply state.LastSnapshot state)
                    | Get reply ->
                        let! next = getTerminals config state
                        return! loop (respond reply next.LastSnapshot next)
                    | GetCached reply ->
                        return! loop (respond reply state.LastSnapshot state)
                    | RunTerminalAction(_, reply)
                        when terminalMutationLockError state
                             |> Option.isSome ->
                        let error =
                            terminalMutationLockError state
                            |> Option.get

                        return! loop (respond reply (Error error) state)
                    | RunTerminalAction(operation, reply) ->
                        let! outcome =
                            operation ()
                            |> Async.Catch

                        let result =
                            match outcome with
                            | Choice1Of2 result -> result
                            | Choice2Of2 error ->
                                Log.log
                                    "TerminalHost"
                                    $"Terminal action crashed errorType={error.GetType().Name}"

                                Error "Terminal action failed"

                        return! loop (respond reply result state)
                    | Start(_, _, reply)
                        when terminalMutationLockError state
                             |> Option.isSome ->
                        let error =
                            terminalMutationLockError state
                            |> Option.get

                        return! loop (respond reply (Error error) state)
                    | Start(worktreePath, _, reply)
                        when cleanupReserved state worktreePath ->
                        return! loop (respond reply (Error cleanupInProgressError) state)
                    | Start(worktreePath, command, reply) ->
                        let! next, result =
                            startTerminal config state worktreePath command

                        return! loop (respond reply result next)
                    | ReserveCleanup(_, _, _, reply)
                        when terminalMutationLockError state
                             |> Option.isSome ->
                        let error =
                            terminalMutationLockError state
                            |> Option.get

                        return! loop (respond reply (Error error) state)
                    | ReserveCleanup(OneTerminal terminalId, _, _, reply)
                        when not (validSessionId (EmbeddedTerminalId.value terminalId)) ->
                        return! loop (respond reply (Error "Invalid embedded terminal ID") state)
                    | ReserveCleanup(target, fallback, token, reply) ->
                        match targetWorktree state target fallback with
                        | None -> return! loop (respond reply (Ok None) state)
                        | Some worktreePath ->
                            let key = cleanupPathKey worktreePath

                            if state.CleanupReservations.ContainsKey key then
                                return! loop (respond reply (Error cleanupInProgressError) state)
                            else
                                let lease =
                                    { Token = token
                                      Target = target
                                      WorktreePath = worktreePath
                                      CachedTerminalIds =
                                        targetTerminalIds target fallback state.LastSnapshot
                                      LastHost = state.LastHost }

                                let next =
                                    let reservation =
                                        { Token = token
                                          AcquiredAtTick =
                                            Environment.TickCount64 }

                                    { state with
                                        CleanupReservations =
                                            state.CleanupReservations
                                            |> Map.add key reservation }

                                Log.log
                                    "TerminalHost"
                                    $"Cleanup reservation acquired lease={cleanupLeaseCorrelation token} target={cleanupTargetKind target} cachedTerminals={lease.CachedTerminalIds.Count} reservations={next.CleanupReservations.Count}"

                                return! loop (respond reply (Ok(Some lease)) next)
                    | ApplyCleanup(completion, reply) ->
                        let next = applyCleanupCompletion state completion
                        return! loop (respond reply next.LastSnapshot next)
                    | ReleaseCleanup(token, reply) ->
                        let released =
                            state.CleanupReservations
                            |> Map.toSeq
                            |> Seq.map snd
                            |> Seq.tryFind (fun reservation ->
                                reservation.Token = token)

                        let reservations =
                            state.CleanupReservations
                            |> Map.filter (fun _ reservation ->
                                reservation.Token <> token)

                        let removed =
                            state.CleanupReservations.Count
                            - reservations.Count

                        let age =
                            released
                            |> Option.map (fun reservation ->
                                string (
                                    elapsedMilliseconds
                                        reservation.AcquiredAtTick
                                ))
                            |> Option.defaultValue "unknown"

                        Log.log
                            "TerminalHost"
                            $"Cleanup reservation release applied lease={cleanupLeaseCorrelation token} ageMs={age} removed={removed} reservations={reservations.Count}"

                        let next =
                            { state with
                                CleanupReservations = reservations }

                        if
                            removed > 0
                            && reservations.IsEmpty
                        then
                            match state.Maintenance with
                            | MaintenanceState.WaitingForCleanup queued ->
                                Log.log
                                    "TerminalHost"
                                    $"Cleanup reservations drained for queued update waitedMs={elapsedMilliseconds queued.RequestedAtTick}"

                                inbox.Post BeginTerminalHostUpdate
                            | MaintenanceState.Unlocked
                            | MaintenanceState.Updating _
                            | MaintenanceState.Fatal _ ->
                                ()

                        return!
                            next
                            |> respond reply ()
                            |> loop
                    | GetUpdateState reply ->
                        let next, update =
                            terminalHostUpdateState
                                config
                                state

                        return!
                            next
                            |> respond reply update
                            |> loop
                    | BeginTerminalHostUpdate ->
                        match state.Maintenance with
                        | MaintenanceState.WaitingForCleanup queued
                            when state.CleanupReservations.IsEmpty ->
                            let! current, prepared =
                                prepareUpdate
                                    config
                                    state
                                    queued.QueryRestartSessions

                            match prepared with
                            | Error error ->
                                let fatal, _ =
                                    enterFatalUpdate error current

                                return! loop fatal
                            | Ok None ->
                                Log.log
                                    "TerminalHost"
                                    "User-requested update found no staged candidate"

                                return!
                                    loop
                                        { current with
                                            Maintenance =
                                                MaintenanceState.Unlocked }
                            | Ok(
                                Some(
                                    host,
                                    stagedExecutable,
                                    sessions
                                )
                              ) ->
                                Log.log
                                    "TerminalHost"
                                    $"User-requested update entering transaction waitMs={elapsedMilliseconds queued.RequestedAtTick} resumableSessions={sessions.Length}"

                                async {
                                    let! result =
                                        runUpdate
                                            queued.Operations
                                            config
                                            host
                                            stagedExecutable
                                            sessions

                                    inbox.Post(
                                        FinishTerminalHostUpdate
                                            result
                                    )
                                }
                                |> Async.Start

                                return!
                                    loop
                                        { current with
                                            Maintenance =
                                                MaintenanceState.Updating
                                                    sessions }
                        | MaintenanceState.WaitingForCleanup _
                        | MaintenanceState.Unlocked
                        | MaintenanceState.Updating _
                        | MaintenanceState.Fatal _ ->
                            return! loop state
                    | UpdateTerminalHost(
                        queryRestartSessions,
                        operations,
                        reply
                      ) ->
                        match state.Maintenance with
                        | MaintenanceState.Unlocked ->
                            let waitingForCleanup =
                                not state.CleanupReservations.IsEmpty

                            let queued =
                                { QueryRestartSessions =
                                    queryRestartSessions
                                  Operations = operations
                                  RequestedAtTick =
                                    Environment.TickCount64 }

                            let next =
                                { state with
                                    Maintenance =
                                        MaintenanceState.WaitingForCleanup
                                            queued }

                            if waitingForCleanup then
                                Log.log
                                    "TerminalHost"
                                    $"User-requested update queued behind cleanup reservations={state.CleanupReservations.Count} leases={cleanupLeaseSummaries state.CleanupReservations}"
                            else
                                inbox.Post BeginTerminalHostUpdate

                            return!
                                next
                                |> respond
                                    reply
                                    TerminalHostUpdateState.Updating
                                |> loop
                        | MaintenanceState.WaitingForCleanup _
                        | MaintenanceState.Updating _
                        | MaintenanceState.Fatal _ ->
                            let next, update =
                                terminalHostUpdateState
                                    config
                                    state

                            Log.log
                                "TerminalHost"
                                $"Duplicate user-requested update ignored state={maintenanceKind state.Maintenance}"

                            return!
                                next
                                |> respond reply update
                                |> loop
                    | FinishTerminalHostUpdate(
                        Ok(host, registry)
                      ) ->
                        Log.log
                            "TerminalHost"
                            "User-requested update completed"

                        let next =
                            applyRegistryWith
                                ReconciliationMode.RebindAfterHostChange
                                state
                                host
                                registry

                        return!
                            { next with
                                Maintenance =
                                    MaintenanceState.Unlocked }
                            |> loop
                    | FinishTerminalHostUpdate(Error error) ->
                        let fatal, _ =
                            enterFatalUpdate error state

                        return! loop fatal
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty; LastHost = None
                  Maintenance = MaintenanceState.Unlocked
                  LastUpdateAvailabilityError = None
                  CleanupReservations = Map.empty })

    Manager(config, agent)

let createWithProcessIdentityResolver
    processIdentityResolver
    serverOrigin
    configuredOrigins
    =
    originsFor serverOrigin configuredOrigins
    |> TerminalHostClient.defaultConfigWithProcessIdentityResolver
        processIdentityResolver
    |> createWithConfig

let create serverOrigin configuredOrigins =
    createWithProcessIdentityResolver
        ProcessIdentityResolverRuntime.defaultResolver
        serverOrigin
        configuredOrigins

let private ask (agent: MailboxProcessor<Message>) build =
    agent.PostAndAsyncReply(build, timeout = 60_000)

let getUpdateState (Manager(_, agent)) =
    ask agent GetUpdateState

let internal runTerminalAction (Manager(_, agent)) operation =
    agent.PostAndAsyncReply(
        (fun reply ->
            RunTerminalAction(operation, reply)),
        timeout = 150_000
    )

let internal updateTerminalHostWithOperations
    operations
    queryRestartSessions
    (Manager(_, agent))
    =
    agent.PostAndAsyncReply(fun reply ->
        UpdateTerminalHost(
            queryRestartSessions,
            operations,
            reply
        ))

let internal updateTerminalHost manager queryRestartSessions =
    updateTerminalHostWithOperations
        TerminalHostReplacement.defaultOperations
        queryRestartSessions
        manager

let private startCore (Manager(_, agent)) worktreePath command =
    agent.PostAndAsyncReply(
        (fun reply -> Start(worktreePath, command, reply)),
        timeout = 150_000
    )

let start manager worktreePath =
    startCore manager worktreePath None

let startWithCommand manager worktreePath command =
    startCore manager worktreePath (Some command)

let get (Manager(_, agent)) = ask agent Get

/// The current manager snapshot without host I/O (test seam for lifecycle transitions).
let internal getCached (Manager(_, agent)) = ask agent GetCached

let internal clientConfig (Manager(config, _)) = config

let internal applyCleanup (Manager(_, agent)) completion = ask agent (fun reply -> ApplyCleanup(completion, reply))

let private releaseCleanupToken (agent: MailboxProcessor<Message>) target token =
    Log.log
        "TerminalHost"
        $"Cleanup reservation release requested lease={cleanupLeaseCorrelation token} target={cleanupTargetKind target}"

    try
        agent.PostAndReply(
            (fun reply -> ReleaseCleanup(token, reply)),
            timeout = 60_000
        )
    with error ->
        Log.log
            "TerminalHost"
            $"Cleanup reservation release acknowledgement failed lease={cleanupLeaseCorrelation token} target={cleanupTargetKind target} errorType={error.GetType().Name}"

let internal reserveCleanup (Manager(_, agent)) target fallback =
    async {
        let token = Guid.NewGuid()

        try
            return! ask agent (fun reply -> ReserveCleanup(target, fallback, token, reply))
        with error ->
            Log.log
                "TerminalHost"
                $"Cleanup reservation acquisition did not complete lease={cleanupLeaseCorrelation token} target={cleanupTargetKind target} errorType={error.GetType().Name}"

            releaseCleanupToken agent target token
            return Error "Terminal cleanup could not start within 60 seconds; try again."
    }

let internal releaseCleanup (Manager(_, agent)) (lease: CleanupLease) =
    releaseCleanupToken agent lease.Target lease.Token
