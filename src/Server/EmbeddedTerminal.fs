module Server.EmbeddedTerminal

open System
open Shared
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Server.TerminalHostReplacement

[<RequireQualifiedAccess>]
type private MaintenanceState =
    | Unlocked
    | Updating of RestartSession list
    | Fatal of error: string

[<RequireQualifiedAccess>]
type private ReconciliationMode =
    | RefreshOnly
    | RebindAfterHostChange
    | PreserveDuringCleanup

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      LastHost: DiscoveryManifest option
      Maintenance: MaintenanceState
      CleanupReservations: Map<string, System.Guid> }

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
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | GetCached of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ReserveCleanup of CloseTarget * WorktreePath option * Guid * AsyncReplyChannel<Result<CleanupLease option, string>>
    | ApplyCleanup of CleanupCompletion * AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ReleaseCleanup of Guid
    | GetUpdateState of AsyncReplyChannel<TerminalHostUpdateState>
    | UpdateTerminalHost of
        RestartSessionQuery *
        Operations *
        AsyncReplyChannel<TerminalHostUpdateState>
    | FinishTerminalHostUpdate of
        Result<DiscoveryManifest * RegistrySnapshot, string> *
        AsyncReplyChannel<TerminalHostUpdateState>

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

let private fatalUpdateMessage error =
    $"TerminalHost update failed: {error}. Terminal actions remain locked. Redeploy or restart Treemon manually from an external PowerShell window."

let private cleanupInProgressError = "Terminal cleanup is in progress for this worktree; try again when it completes."

let private terminalHostUpdateState config (state: ManagerState) =
    match state.Maintenance with
    | MaintenanceState.Updating _ ->
        TerminalHostUpdateState.Updating
    | MaintenanceState.Fatal error ->
        TerminalHostUpdateState.Fatal error
    | MaintenanceState.Unlocked ->
        match state.LastHost with
        | Some host
            when TerminalHostReplacement.updateAvailable
                     config
                     host ->
            TerminalHostUpdateState.Available
        | Some _
        | None ->
            TerminalHostUpdateState.Unavailable

let private terminalMutationLockError (state: ManagerState) =
    match state.Maintenance with
    | MaintenanceState.Unlocked -> None
    | MaintenanceState.Updating _ ->
        Some updateInProgressError
    | MaintenanceState.Fatal error ->
        Some error

let private enterFatalUpdate error (state: ManagerState) =
    let message = fatalUpdateMessage error

    Log.log
        "TerminalHost"
        $"Update entered a permanent fatal state: {error}"

    { withHostFailure message state with
        Maintenance = MaintenanceState.Fatal message },
    TerminalHostUpdateState.Fatal message

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
                                    { state with
                                        CleanupReservations =
                                            state.CleanupReservations
                                            |> Map.add key token }

                                return! loop (respond reply (Ok(Some lease)) next)
                    | ApplyCleanup(completion, reply) ->
                        let next = applyCleanupCompletion state completion
                        return! loop (respond reply next.LastSnapshot next)
                    | ReleaseCleanup token ->
                        let reservations =
                            state.CleanupReservations
                            |> Map.filter (fun _ current -> current <> token)

                        return! loop { state with CleanupReservations = reservations }
                    | GetUpdateState reply ->
                        return!
                            state
                            |> terminalHostUpdateState config
                            |> fun update ->
                                respond reply update state
                            |> loop
                    | UpdateTerminalHost(_, _, reply)
                        when state.Maintenance
                             <> MaintenanceState.Unlocked ->
                        return!
                            state
                            |> terminalHostUpdateState config
                            |> fun update ->
                                respond reply update state
                            |> loop
                    | UpdateTerminalHost(_, _, reply)
                        when not state.CleanupReservations.IsEmpty ->
                        return!
                            state
                            |> terminalHostUpdateState config
                            |> fun update ->
                                respond reply update state
                            |> loop
                    | UpdateTerminalHost(
                        queryRestartSessions,
                        operations,
                        reply
                      ) ->
                        let! current, prepared =
                            prepareUpdate
                                config
                                state
                                queryRestartSessions

                        match prepared with
                        | Error error ->
                            let fatal, update =
                                enterFatalUpdate error current

                            return!
                                fatal
                                |> respond reply update
                                |> loop
                        | Ok None ->
                            return!
                                current
                                |> respond
                                    reply
                                    TerminalHostUpdateState.Unavailable
                                |> loop
                        | Ok(
                            Some(
                                host,
                                stagedExecutable,
                                sessions
                            )
                          ) ->
                            Log.log
                                "TerminalHost"
                                $"Starting user-requested update with {sessions.Length} resumable session(s)"

                            async {
                                let! result =
                                    runUpdate
                                        operations
                                        config
                                        host
                                        stagedExecutable
                                        sessions

                                inbox.Post(
                                    FinishTerminalHostUpdate(
                                        result,
                                        reply
                                    )
                                )
                            }
                            |> Async.Start

                            return!
                                loop
                                    { current with
                                        Maintenance =
                                            MaintenanceState.Updating
                                                sessions }
                    | FinishTerminalHostUpdate(
                        Ok(host, registry),
                        reply
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
                            |> respond
                                reply
                                TerminalHostUpdateState.Unavailable
                            |> loop
                    | FinishTerminalHostUpdate(Error error, reply) ->
                        let fatal, update =
                            enterFatalUpdate error state

                        return!
                            fatal
                            |> respond reply update
                            |> loop
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty; LastHost = None
                  Maintenance = MaintenanceState.Unlocked
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

let internal reserveCleanup (Manager(_, agent)) target fallback =
    async {
        let token = Guid.NewGuid()

        try
            return! ask agent (fun reply -> ReserveCleanup(target, fallback, token, reply))
        with _ ->
            agent.Post(ReleaseCleanup token)
            return Error "Terminal cleanup could not start within 60 seconds; try again."
    }

let internal releaseCleanup (Manager(_, agent)) (lease: CleanupLease) =
    agent.Post(ReleaseCleanup lease.Token)
