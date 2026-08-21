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

[<RequireQualifiedAccess>]
type private TerminalMutation =
    | Start
    | Close

type private Message =
    | Mutate of
        TerminalMutation *
        WorktreePath *
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | GetCached of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | ReserveCleanup of
        WorktreePath *
        CleanupReservation *
        AsyncReplyChannel<Result<CleanupReservation, string>>
    | ReleaseCleanup of CleanupReservation
    | BeginReplacement of
        ReplacementPlan *
        ReplacementPolicyQuery *
        AsyncReplyChannel<ReplacementOutcome>
    | FinishReplacement of
        ReplacementCommit *
        AsyncReplyChannel<ReplacementOutcome>

type Manager =
    private
        | Manager of Config * MailboxProcessor<Message>

let private withoutPath path snapshot =
    { Tabs =
        snapshot.Tabs
        |> List.filter (fun tab ->
            not (samePath (WorktreePath.value tab.Worktree) path)) }

let private interrupted error tab =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Running _
    | EmbeddedTerminalLifecycle.Starting ->
        { tab with
            Lifecycle = EmbeddedTerminalLifecycle.Interrupted error }
    | EmbeddedTerminalLifecycle.Failed _
    | EmbeddedTerminalLifecycle.Interrupted _ ->
        tab

let private interruptSnapshot error snapshot =
    { Tabs = snapshot.Tabs |> List.map (interrupted error) }

let private tabForRecord (terminal: TerminalRecord) =
    { Worktree = PathUtils.toWorktreePath terminal.WorktreePath
      Lifecycle =
        EmbeddedTerminalLifecycle.Running terminal.AttachmentEndpoint }

let private reconcileSnapshot
    previousHost
    currentHost
    (records: TerminalRecord list)
    snapshot
    =
    let recordsByPath =
        records
        |> List.map (fun terminal ->
            pathKey terminal.WorktreePath, terminal)
        |> Map.ofList

    let hostChanged =
        previousHost
        |> Option.exists (fun previous ->
            hostIdentityMatches previous currentHost |> not)

    let missingReason =
        if hostChanged then
            "TerminalHost changed before this terminal could be verified. Restart the terminal to continue."
        else
            "The terminal is no longer present in the authoritative TerminalHost registry."

    let retained =
        snapshot.Tabs
        |> List.map (fun tab ->
            let key = tab.Worktree |> WorktreePath.value |> pathKey

            match Map.tryFind key recordsByPath with
            | Some terminal -> tabForRecord terminal
            | None -> interrupted missingReason tab)

    let retainedPaths =
        retained
        |> List.map (_.Worktree >> WorktreePath.value >> pathKey)
        |> Set.ofList

    let appended =
        records
        |> List.filter (fun terminal ->
            retainedPaths
            |> Set.contains (pathKey terminal.WorktreePath)
            |> not)
        |> List.map tabForRecord

    { Tabs = retained @ appended }

let private applyRegistry
    (state: ManagerState)
    (manifest: DiscoveryManifest)
    (registry: RegistrySnapshot)
    =
    { state with
        LastSnapshot =
            reconcileSnapshot state.LastHost manifest registry.Terminals state.LastSnapshot
        LastHost = Some manifest }

let private withHostFailure error state =
    { state with
        LastSnapshot = interruptSnapshot error state.LastSnapshot }

let private getTerminals config state =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            match! listTerminals config connection with
            | Ok registry -> return applyRegistry state connection registry
            | Error error -> return withHostFailure error state
        | MissingHost ->
            return withHostFailure "TerminalHost discovery is missing; running terminals can no longer be verified." state
        | DeadHost error ->
            return withHostFailure $"{error}. Its terminals were interrupted." state
        | IncompatibleHost(_, error)
        | UnusableHost error ->
            return withHostFailure error state
    }

let private mutationResult prepare state connection = function
    | Error(MutationUnverified(lastRegistry, error)) ->
        let current =
            lastRegistry
            |> Option.map (applyRegistry state connection)
            |> Option.defaultValue state

        withHostFailure error current, Error error
    | Error(MutationRejected(registry, error)) ->
        applyRegistry state connection registry, Error error
    | Ok registry ->
        let next = applyRegistry (prepare state) connection registry
        next, Ok next.LastSnapshot

let private startTerminal config state worktreePath =
    async {
        match! ensureHost config state.LastHost with
        | Error error -> return withHostFailure error state, Error error
        | Ok connection ->
            let! result =
                startTerminalOnHost config connection (WorktreePath.value worktreePath)

            return result |> Result.map fst |> mutationResult id state connection
    }

let private safeWithoutHealthyHost config state discovery =
    match discovery with
    | DeadHost _ -> Ok()
    | MissingHost ->
        match knownHostIsStillLive config state.LastHost with
        | Ok false -> Ok()
        | Ok true ->
            Error
                "The TerminalHost manifest is missing while the exact recorded host is still running"
        | Error error -> Error error
    | IncompatibleHost(_, error)
    | UnusableHost error -> Error error
    | HealthyHost _ -> failwith "unreachable"

let private closeTerminal config state worktreePath =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            let path = WorktreePath.value worktreePath
            let! result = closeTerminalOnHost config connection path

            return result |> mutationResult (fun current ->
                { current with LastSnapshot = withoutPath path current.LastSnapshot }) state connection
        | discovery ->
            match safeWithoutHealthyHost config state discovery with
            | Error error -> return withHostFailure error state, Error error
            | Ok() ->
                let reason =
                    match discovery with
                    | DeadHost error -> $"{error}. Its terminals were interrupted."
                    | MissingHost ->
                        "TerminalHost is not running; no live terminal remains to close."
                    | IncompatibleHost _
                    | UnusableHost _
                    | HealthyHost _ -> failwith "unreachable"

                let next =
                    { state with
                        LastSnapshot =
                            state.LastSnapshot |> interruptSnapshot reason
                            |> withoutPath (WorktreePath.value worktreePath) }

                return next, Ok next.LastSnapshot
    }

let private applyReplacementCommit state commit =
    match commit with
    | ReplacementCommit.KeepState outcome -> state, outcome
    | ReplacementCommit.InterruptState(message, outcome) ->
        withHostFailure message state, outcome
    | ReplacementCommit.ApplyRegistry(manifest, registry, outcome) ->
        applyRegistry state manifest registry, outcome

let private replacementInProgressError =
    "TerminalHost replacement is in progress; try again when it completes."

let private cleanupInProgressError =
    "Terminal cleanup is in progress for this worktree; try again when it completes."

let private cleanupRequestTimeoutError =
    "Terminal cleanup could not start within 60 seconds; try again."

let private cleanupPathKey worktreePath =
    let path =
        worktreePath
        |> WorktreePath.value
        |> Option.ofObj
        |> Option.defaultValue ""

    try
        path
        |> PathUtils.normalizePath
        |> pathKey
    with _ ->
        pathKey path

let private respond (channel: AsyncReplyChannel<'value>) value state =
    channel.Reply value
    state

let private mutate config mutation state worktreePath =
    match mutation with
    | TerminalMutation.Start -> startTerminal config state worktreePath
    | TerminalMutation.Close -> closeTerminal config state worktreePath

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
                    | Mutate(_, _, reply) when state.Phase = ManagerPhase.Replacing ->
                        return! loop (respond reply (Error replacementInProgressError) state)
                    | Mutate(_, worktreePath, reply)
                        when state.CleanupReservations
                             |> Map.containsKey (cleanupPathKey worktreePath) ->
                        return! loop (respond reply (Error cleanupInProgressError) state)
                    | Mutate(mutation, worktreePath, reply) ->
                        let! next, result = mutate config mutation state worktreePath

                        return! loop (respond reply result next)
                    | ReserveCleanup(_, _, reply)
                        when state.Phase = ManagerPhase.Replacing ->
                        return! loop (respond reply (Error replacementInProgressError) state)
                    | ReserveCleanup(worktreePath, (CleanupReservation(key, token) as reservation), reply) ->
                        match state.CleanupReservations |> Map.tryFind key with
                        | Some _ ->
                            return! loop (respond reply (Error cleanupInProgressError) state)
                        | None ->
                            let reserved =
                                { state with
                                    CleanupReservations =
                                        state.CleanupReservations |> Map.add key token }

                            let! next, result = closeTerminal config reserved worktreePath

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
                    | BeginReplacement(_, _, reply)
                        when state.Phase = ManagerPhase.Replacing ->
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
    TerminalHostReplacement.runCoordinator
        (fun ignoredStagedVersion ->
            tryReplaceHostIgnoring ignoredStagedVersion (fun () -> async.Return()) query manager)
        cancellationToken

let private ask (agent: MailboxProcessor<Message>) build =
    agent.PostAndAsyncReply(build, timeout = 60_000)

let start (Manager(_, agent)) worktreePath =
    ask agent (fun reply -> Mutate(TerminalMutation.Start, worktreePath, reply))

let get (Manager(_, agent)) =
    ask agent Get

/// The current manager snapshot without host I/O (test seam for lifecycle transitions).
let internal getCached (Manager(_, agent)) =
    ask agent GetCached

let close (Manager(_, agent)) worktreePath =
    ask agent (fun reply -> Mutate(TerminalMutation.Close, worktreePath, reply))

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
                    return Error cleanupRequestTimeoutError
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
