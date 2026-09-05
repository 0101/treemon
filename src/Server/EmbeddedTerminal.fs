module Server.EmbeddedTerminal

open System
open Shared
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Server.TerminalHostReplacement
open Server.TerminalHostRecovery

[<RequireQualifiedAccess>]
type private ManagerPhase = Steady | Replacing

[<RequireQualifiedAccess>]
type private ReconciliationMode =
    | RefreshOnly
    | RebindAfterHostChange
    | PreserveDuringCleanup

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      LastHost: DiscoveryManifest option
      Phase: ManagerPhase
      CleanupReservations: Map<string, System.Guid> }

type internal CloseTarget = OneTerminal of EmbeddedTerminalId | WorktreeTerminals of WorktreePath

type internal CleanupLease =
    { Token: Guid; Target: CloseTarget
      WorktreePath: WorktreePath
      CachedTerminalIds: Set<EmbeddedTerminalId>
      LastHost: DiscoveryManifest option }

type internal CleanupPreparation = NoCleanupNeeded of EmbeddedTerminalSnapshot | CleanupReserved of CleanupLease

type internal CleanupLeaseAcquirer =
    CloseTarget
        -> WorktreePath option
        -> Async<Result<CleanupPreparation, string>>

type internal CleanupRemoval = KeepCleanupTargets | RemoveCleanupTarget of CloseTarget | RemoveClosedTerminals of Set<EmbeddedTerminalId>

type internal CleanupUpdate =
    | ReconcileCleanup of DiscoveryManifest * RegistrySnapshot * CleanupRemoval
    | UnverifiedCleanup of DiscoveryManifest * RegistrySnapshot option * Set<EmbeddedTerminalId> * string
    | UnavailableCleanup of string * CloseTarget
    | FailedCleanup of string

type private Message =
    | Start of WorktreePath * command: string option * AsyncReplyChannel<Result<EmbeddedTerminalStartResult, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | GetCached of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ReserveCleanup of CloseTarget * WorktreePath option * Guid * AsyncReplyChannel<Result<CleanupPreparation, string>>
    | ApplyCleanup of CleanupUpdate * AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ReleaseCleanup of Guid
    | BeginReplacement of
        ReplacementPlan *
        ReplacementPolicyQuery *
        ReplacementOperations *
        AsyncReplyChannel<ReplacementOutcome>
    | FinishReplacement of ReplacementResolution * AsyncReplyChannel<ReplacementOutcome>

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
             |> List.map (fun tab ->
                 recordsById
                 |> Map.tryFind tab.Id
                 |> Option.map tabForRecord
                 |> Option.defaultWith (fun () ->
                     if preserveMissing then tab
                     else interrupted "The terminal is no longer present in the authoritative TerminalHost registry." tab)))
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

let private withoutTarget target snapshot =
    let keep tab =
        match target with
        | OneTerminal terminalId -> tab.Id <> terminalId
        | WorktreeTerminals path ->
            not (samePath (WorktreePath.value tab.Worktree) (WorktreePath.value path))

    { Tabs = snapshot.Tabs |> List.filter keep }

let private removeTarget target (state: ManagerState) =
    { state with LastSnapshot = withoutTarget target state.LastSnapshot }

let private targetTerminalIds target fallback (snapshot: EmbeddedTerminalSnapshot) =
    let cached =
        snapshot.Tabs
        |> List.choose (fun tab ->
            match target with
            | OneTerminal terminalId when tab.Id = terminalId -> Some tab.Id
            | WorktreeTerminals path when samePath (WorktreePath.value tab.Worktree) (WorktreePath.value path) -> Some tab.Id
            | _ -> None)
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

let private applyCleanupRemoval removal (state: ManagerState) =
    match removal with
    | KeepCleanupTargets -> state
    | RemoveCleanupTarget target -> removeTarget target state
    | RemoveClosedTerminals terminalIds -> removeTerminalIds terminalIds state

let private applyCleanupUpdate (state: ManagerState) = function
    | ReconcileCleanup(connection, registry, removal) ->
        applyRegistryWith
            ReconciliationMode.PreserveDuringCleanup
            (applyCleanupRemoval removal state)
            connection
            registry
    | UnverifiedCleanup(connection, registry, closedTerminalIds, error) ->
        MutationUnverified(registry, error)
        |> mutationFailure (removeTerminalIds closedTerminalIds state) connection
        |> fst
    | UnavailableCleanup(reason, target) ->
        { state with LastSnapshot = state.LastSnapshot |> interruptSnapshot reason |> withoutTarget target }
    | FailedCleanup error -> withHostFailure error state

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

let private applyReplacementResolution (state: ManagerState) = function
    | ReplacementResolution.KeepState outcome ->
        state, outcome
    | ReplacementResolution.ApplyRegistry(manifest, registry, outcome) ->
        applyRegistryWith
            ReconciliationMode.RebindAfterHostChange
            state
            manifest
            registry,
        outcome
    | ReplacementResolution.ApplyRecoveredRegistry(
        manifest,
        registry,
        outcome
      ) ->
        applyRegistry state manifest registry, outcome
    | ReplacementResolution.InterruptWithHost(
        manifest,
        message,
        outcome
      ) ->
        { withHostFailure message state with
            LastHost = Some manifest },
        outcome
    | ReplacementResolution.InterruptWithoutHost(message, outcome) ->
        { withHostFailure message state with
            LastHost = None },
        outcome

let private replacementInProgressError = "TerminalHost replacement is in progress; try again when it completes."
let private cleanupInProgressError = "Terminal cleanup is in progress for this worktree; try again when it completes."

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
                    | Get reply when state.Phase = ManagerPhase.Replacing ->
                        return! loop (respond reply state.LastSnapshot state)
                    | Get reply ->
                        let! next = getTerminals config state
                        return! loop (respond reply next.LastSnapshot next)
                    | GetCached reply ->
                        return! loop (respond reply state.LastSnapshot state)
                    | Start(_, _, reply) when state.Phase = ManagerPhase.Replacing ->
                        return! loop (respond reply (Error replacementInProgressError) state)
                    | Start(worktreePath, _, reply)
                        when cleanupReserved state worktreePath ->
                        return! loop (respond reply (Error cleanupInProgressError) state)
                    | Start(worktreePath, command, reply) ->
                        let! next, result =
                            startTerminal config state worktreePath command

                        return! loop (respond reply result next)
                    | ReserveCleanup(_, _, _, reply) when state.Phase = ManagerPhase.Replacing ->
                        return! loop (respond reply (Error replacementInProgressError) state)
                    | ReserveCleanup(OneTerminal terminalId, _, _, reply)
                        when not (validSessionId (EmbeddedTerminalId.value terminalId)) ->
                        return! loop (respond reply (Error "Invalid embedded terminal ID") state)
                    | ReserveCleanup(target, fallback, token, reply) ->
                        match targetWorktree state target fallback with
                        | None ->
                            return!
                                loop (
                                    respond reply (Ok(NoCleanupNeeded state.LastSnapshot)) state
                                )
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

                                return! loop (respond reply (Ok(CleanupReserved lease)) next)
                    | ApplyCleanup(update, reply) ->
                        let next = applyCleanupUpdate state update
                        return! loop (respond reply next.LastSnapshot next)
                    | ReleaseCleanup token ->
                        let reservations =
                            state.CleanupReservations
                            |> Map.filter (fun _ current -> current <> token)

                        return! loop { state with CleanupReservations = reservations }
                    | BeginReplacement(_, _, _, reply)
                        when state.Phase = ManagerPhase.Replacing
                             || not state.CleanupReservations.IsEmpty ->
                        return! loop (respond reply ReplacementOutcome.RaceLost state)
                    | BeginReplacement(plan, query, operations, reply) ->
                        async {
                            let! commit =
                                commitReplacementWith
                                    operations
                                    config
                                    plan
                                    query

                            let! resolution =
                                TerminalHostRecovery.resolveWith
                                    operations
                                    config
                                    commit

                            inbox.Post(
                                FinishReplacement(
                                    resolution,
                                    reply
                                )
                            )
                        }
                        |> Async.Start

                        return! loop { state with Phase = ManagerPhase.Replacing }
                    | FinishReplacement(resolution, reply) ->
                        let next, outcome =
                            applyReplacementResolution
                                state
                                resolution

                        return!
                            { next with Phase = ManagerPhase.Steady }
                            |> respond reply outcome
                            |> loop
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty; LastHost = None
                  Phase = ManagerPhase.Steady; CleanupReservations = Map.empty })

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

let private tryReplaceHostIgnoringWith
    operations
    ignoredStagedVersion
    beforeRecheck
    query
    (Manager(config, agent))
    =
    let commit plan activityQuery =
        agent.PostAndAsyncReply(
            (fun reply ->
                BeginReplacement(
                    plan,
                    activityQuery,
                    operations,
                    reply
                )),
            timeout = 300_000
        )

    TerminalHostReplacement.tryReplaceHostIgnoring ignoredStagedVersion beforeRecheck query config commit

let internal tryReplaceHostWithOperations
    beforeRecheck
    query
    operations
    manager
    =
    tryReplaceHostIgnoringWith
        operations
        None
        beforeRecheck
        query
        manager

let internal runReplacementCoordinator
    manager
    query
    closureSnapshot
    (cancellationToken: System.Threading.CancellationToken)
    =
    let operations =
        TerminalHostReplacement.defaultOperations closureSnapshot

    TerminalHostReplacement.runCoordinator (fun ignoredStagedVersion ->
        tryReplaceHostIgnoringWith
            operations
            ignoredStagedVersion
            (fun () -> async.Return())
            query
            manager)
        cancellationToken

let private ask (agent: MailboxProcessor<Message>) build =
    agent.PostAndAsyncReply(build, timeout = 60_000)

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

let internal applyCleanup (Manager(_, agent)) update = ask agent (fun reply -> ApplyCleanup(update, reply))

let private reserveCleanup (Manager(_, agent)) target fallback =
    async {
        let token = Guid.NewGuid()

        try
            return! ask agent (fun reply -> ReserveCleanup(target, fallback, token, reply))
        with _ ->
            agent.Post(ReleaseCleanup token)
            return Error "Terminal cleanup could not start within 60 seconds; try again."
    }

let internal withCleanupLease
    ((Manager(_, agent)) as manager)
    (
        acquire:
            CleanupLeaseAcquirer
                -> Async<Result<CleanupPreparation, string>>
    )
    (
        operation:
            Result<CleanupPreparation, string>
                -> Threading.Tasks.Task<'value>
    )
    : Async<'value>
    =
    async {
        let reservation =
            Async.StartAsTask(
                acquire (reserveCleanup manager),
                cancellationToken = Threading.CancellationToken.None
            )

        return!
            task {
                let! acquired = reservation

                match acquired with
                | Ok(CleanupReserved lease) ->
                    try
                        return! operation acquired
                    finally
                        agent.Post(ReleaseCleanup lease.Token)
                | _ -> return! operation acquired
            }
            |> Async.AwaitTask
    }
