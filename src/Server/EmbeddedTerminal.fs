module Server.EmbeddedTerminal

open Shared
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Server.TerminalHostReplacement

[<RequireQualifiedAccess>]
type private ManagerPhase =
    | Steady
    | Replacing

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      LastHost: DiscoveryManifest option
      Phase: ManagerPhase
      CleanupReservations: Map<string, System.Guid> }

type private CleanupReservation =
    | CleanupReservation of pathKey: string * token: System.Guid

type private CloseTarget =
    | OneTerminal of EmbeddedTerminalId
    | WorktreeTerminals of WorktreePath

type private Message =
    | Start of WorktreePath * command: string option * AsyncReplyChannel<Result<EmbeddedTerminalStartResult, string>>
    | Close of EmbeddedTerminalId * AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | GetCached of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ReserveCleanup of WorktreePath * CleanupReservation * AsyncReplyChannel<Result<CleanupReservation, string>>
    | ReleaseCleanup of CleanupReservation
    | BeginReplacement of ReplacementPlan * ReplacementPolicyQuery * AsyncReplyChannel<ReplacementOutcome>
    | FinishReplacement of ReplacementCommit * AsyncReplyChannel<ReplacementOutcome>

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

let private reconcileSnapshot resetTabs previousHost currentHost (records: TerminalHostClient.TerminalRecord list) (snapshot: EmbeddedTerminalSnapshot) =
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
                     interrupted "The terminal is no longer present in the authoritative TerminalHost registry." tab)))
            @ (records
               |> List.filter (fun terminal ->
                   not (Set.contains (EmbeddedTerminalId terminal.SessionId) previousIds))
               |> List.map tabForRecord) }

let private applyRegistryWith rebindTerminals (state: ManagerState) (manifest: DiscoveryManifest) (registry: RegistrySnapshot) =
    { state with
        LastSnapshot =
            reconcileSnapshot rebindTerminals state.LastHost manifest registry.Terminals state.LastSnapshot
        LastHost = Some manifest }

let private applyRegistry = applyRegistryWith false

let private withHostFailure error state =
    { state with LastSnapshot = interruptSnapshot error state.LastSnapshot }

let private getTerminals config state =
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

let private mutationFailure state connection = function
    | MutationUnverified(lastRegistry, error) ->
        let current =
            lastRegistry
            |> Option.map (applyRegistry state connection)
            |> Option.defaultValue state

        withHostFailure error current, error
    | MutationRejected(registry, error) ->
        applyRegistry state connection registry, error

let private mutationResult prepare state connection = function
    | Error failure ->
        let next, error = mutationFailure state connection failure
        next, Error error
    | Ok registry ->
        let next = applyRegistry (prepare state) connection registry
        next, Ok next.LastSnapshot

let private safeWithoutHealthyHost config state discovery =
    match discovery with
    | DeadHost _ -> Ok()
    | MissingHost ->
        match knownHostIsStillLive config state.LastHost with
        | Ok false -> Ok()
        | Ok true -> Error "The TerminalHost manifest is missing while the exact recorded host is still running"
        | Error error -> Error error
    | IncompatibleHost(_, error)
    | UnusableHost error -> Error error
    | HealthyHost _ -> failwith "unreachable"

let private withoutTarget target snapshot =
    let keep tab =
        match target with
        | OneTerminal terminalId -> tab.Id <> terminalId
        | WorktreeTerminals path ->
            not (samePath (WorktreePath.value tab.Worktree) (WorktreePath.value path))

    { Tabs = snapshot.Tabs |> List.filter keep }

let private removeTarget target state =
    { state with LastSnapshot = withoutTarget target state.LastSnapshot }

let private deliverCommand config attachmentEndpoint command =
    async {
        try
            return! config.SendTerminalCommand attachmentEndpoint command
        with _ ->
            return Error "Could not submit the terminal command"
    }

let private closeOnHost config state connection target =
    async {
        let! result =
            match target with
            | OneTerminal terminalId ->
                closeTerminalOnHost config connection (EmbeddedTerminalId.value terminalId)
            | WorktreeTerminals path ->
                closeTerminalsForWorktreeOnHost config connection (WorktreePath.value path)

        return
            result
            |> mutationResult (removeTarget target) state connection
    }

let private startTerminal config state worktreePath command =
    async {
        let validatedCommand =
            match command with
            | None -> Ok None
            | Some value ->
                validateTerminalCommand value
                |> Result.map Some

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
                                closeOnHost config current connection (OneTerminal terminalId)

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

let private closeTerminals config state target =
    async {
        match target with
        | OneTerminal terminalId
            when not (validSessionId (EmbeddedTerminalId.value terminalId)) ->
            return state, Error "Invalid embedded terminal ID"
        | _ ->
            match! discoverHost config with
            | HealthyHost connection ->
                return! closeOnHost config state connection target
            | discovery ->
                match safeWithoutHealthyHost config state discovery with
                | Error error -> return withHostFailure error state, Error error
                | Ok() ->
                    let reason =
                        match discovery with
                        | DeadHost error -> $"{error}. Its terminals were interrupted."
                        | MissingHost -> "TerminalHost is not running; no live terminal remains to close."
                        | _ -> failwith "unreachable"

                    let next =
                        { state with
                            LastSnapshot =
                                state.LastSnapshot
                                |> interruptSnapshot reason
                                |> withoutTarget target }

                    return next, Ok next.LastSnapshot
    }

let private applyReplacementCommit state commit =
    match commit with
    | ReplacementCommit.KeepState outcome -> state, outcome
    | ReplacementCommit.InterruptState(message, outcome) ->
        withHostFailure message state, outcome
    | ReplacementCommit.ApplyRegistry(manifest, registry, outcome) ->
        applyRegistryWith true state manifest registry, outcome

let private replacementInProgressError = "TerminalHost replacement is in progress; try again when it completes."
let private cleanupInProgressError = "Terminal cleanup is in progress for this worktree; try again when it completes."

let private cleanupPathKey worktreePath =
    let path = worktreePath |> WorktreePath.value |> Option.ofObj |> Option.defaultValue ""

    try
        path |> PathUtils.normalizePath |> pathKey
    with _ ->
        pathKey path

let private respond (channel: AsyncReplyChannel<'value>) value state = channel.Reply value; state

let private cleanupReserved state path =
    state.CleanupReservations |> Map.containsKey (cleanupPathKey path)

let private terminalCleanupReserved state terminalId =
    state.LastSnapshot.Tabs
    |> List.tryFind (fun tab -> tab.Id = terminalId)
    |> Option.exists (fun tab -> cleanupReserved state tab.Worktree)

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
                    | Close(_, reply) when state.Phase = ManagerPhase.Replacing ->
                        return! loop (respond reply (Error replacementInProgressError) state)
                    | Start(worktreePath, _, reply)
                        when cleanupReserved state worktreePath ->
                        return! loop (respond reply (Error cleanupInProgressError) state)
                    | Close(terminalId, reply)
                        when terminalCleanupReserved state terminalId ->
                        return! loop (respond reply (Error cleanupInProgressError) state)
                    | Start(worktreePath, command, reply) ->
                        let! next, result =
                            startTerminal config state worktreePath command

                        return! loop (respond reply result next)
                    | Close(terminalId, reply) ->
                        let! next, result =
                            closeTerminals config state (OneTerminal terminalId)

                        return! loop (respond reply result next)
                    | ReserveCleanup(_, _, reply) when state.Phase = ManagerPhase.Replacing ->
                        return! loop (respond reply (Error replacementInProgressError) state)
                    | ReserveCleanup(worktreePath, (CleanupReservation(key, token) as reservation), reply) ->
                        match state.CleanupReservations |> Map.tryFind key with
                        | Some _ ->
                            return! loop (respond reply (Error cleanupInProgressError) state)
                        | None ->
                            let reserved =
                                { state with
                                    CleanupReservations = state.CleanupReservations |> Map.add key token }

                            let! next, result =
                                closeTerminals config reserved (WorktreeTerminals worktreePath)

                            match result with
                            | Error error ->
                                let released =
                                    { next with CleanupReservations = next.CleanupReservations |> Map.remove key }

                                return! loop (respond reply (Error error) released)
                            | Ok _ -> return! loop (respond reply (Ok reservation) next)
                    | ReleaseCleanup(CleanupReservation(key, token)) ->
                        let reservations =
                            match state.CleanupReservations |> Map.tryFind key with
                            | Some current when current = token ->
                                state.CleanupReservations |> Map.remove key
                            | _ -> state.CleanupReservations

                        return! loop { state with CleanupReservations = reservations }
                    | BeginReplacement(_, _, reply) when state.Phase = ManagerPhase.Replacing ->
                        return! loop (respond reply ReplacementOutcome.RaceLost state)
                    | BeginReplacement(plan, query, reply) ->
                        async {
                            let! commit = commitReplacement config plan query
                            inbox.Post(FinishReplacement(commit, reply))
                        }
                        |> Async.Start

                        return! loop { state with Phase = ManagerPhase.Replacing }
                    | FinishReplacement(commit, reply) ->
                        let next, outcome = applyReplacementCommit state commit

                        return!
                            { next with Phase = ManagerPhase.Steady }
                            |> respond reply outcome
                            |> loop
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty; LastHost = None
                  Phase = ManagerPhase.Steady; CleanupReservations = Map.empty })

    Manager(config, agent)

let create serverOrigin configuredOrigins =
    originsFor serverOrigin configuredOrigins
    |> TerminalHostClient.defaultConfig
    |> createWithConfig

let private tryReplaceHostIgnoring
    ignoredStagedVersion
    beforeRecheck
    query
    (Manager(config, agent))
    =
    let commit plan activityQuery =
        agent.PostAndAsyncReply(
            (fun reply -> BeginReplacement(plan, activityQuery, reply)),
            timeout = 300_000
        )

    TerminalHostReplacement.tryReplaceHostIgnoring ignoredStagedVersion beforeRecheck query config commit

let internal tryReplaceHostWith beforeRecheck query manager =
    tryReplaceHostIgnoring None beforeRecheck query manager

let internal tryReplaceHost query manager =
    tryReplaceHostWith (fun () -> async.Return()) query manager

let internal runReplacementCoordinator
    manager
    query
    (cancellationToken: System.Threading.CancellationToken)
    =
    TerminalHostReplacement.runCoordinator (fun ignoredStagedVersion ->
        tryReplaceHostIgnoring ignoredStagedVersion (fun () -> async.Return()) query manager) cancellationToken

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

let get (Manager(_, agent)) =
    ask agent Get

/// The current manager snapshot without host I/O (test seam for lifecycle transitions).
let internal getCached (Manager(_, agent)) =
    ask agent GetCached

let close (Manager(_, agent)) terminalId =
    ask agent (fun reply -> Close(terminalId, reply))

let private asTask cancellation workflow =
    Async.StartAsTask(workflow, cancellationToken = cancellation)

let internal withReservedCleanup
    (Manager(_, agent))
    worktreePath
    operation
    =
    async {
        let! callerCancellation = Async.CancellationToken
        let requested =
            CleanupReservation(cleanupPathKey worktreePath, System.Guid.NewGuid())

        let reservation =
            async {
                try
                    return! ask agent (fun reply -> ReserveCleanup(worktreePath, requested, reply))
                with _ ->
                    agent.Post(ReleaseCleanup requested)
                    return Error "Terminal cleanup could not start within 60 seconds; try again."
            }
            |> asTask System.Threading.CancellationToken.None

        return!
            task {
                let! reservation = reservation

                match reservation with
                | Error error -> return Error error
                | Ok acquired ->
                    try
                        return! operation () |> asTask callerCancellation
                    finally
                        agent.Post(ReleaseCleanup acquired)
            }
            |> Async.AwaitTask
    }
