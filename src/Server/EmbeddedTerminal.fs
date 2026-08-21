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
      Phase: ManagerPhase }

type private Message =
    | Start of
        WorktreePath *
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | GetCached of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | Close of
        WorktreePath *
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
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
            reconcileSnapshot
                state.LastHost
                manifest
                registry.Terminals
                state.LastSnapshot
        LastHost = Some manifest }

let private applyRegistryAfterClose
    path
    (state: ManagerState)
    (manifest: DiscoveryManifest)
    (registry: RegistrySnapshot)
    =
    let withoutClosed =
        withoutPath path state.LastSnapshot

    { state with
        LastSnapshot =
            reconcileSnapshot
                state.LastHost
                manifest
                registry.Terminals
                withoutClosed
        LastHost = Some manifest }

let private withHostFailure error state =
    { state with
        LastSnapshot = interruptSnapshot error state.LastSnapshot }

let private getTerminals config state =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            match! listTerminals config connection with
            | Ok registry ->
                return applyRegistry state connection registry
            | Error error ->
                return withHostFailure error state
        | MissingHost ->
            return
                withHostFailure
                    "TerminalHost discovery is missing; running terminals can no longer be verified."
                    state
        | DeadHost error ->
            return
                withHostFailure
                    $"{error}. Its terminals were interrupted."
                    state
        | IncompatibleHost(_, error)
        | UnusableHost error ->
            return withHostFailure error state
    }

let private startTerminal config state worktreePath =
    async {
        match! ensureHost config state.LastHost with
        | Error error ->
            return withHostFailure error state, Error error
        | Ok connection ->
            let path = WorktreePath.value worktreePath

            match! startTerminalOnHost config connection path with
            | Error(StartUnverified error) ->
                return withHostFailure error state, Error error
            | Error(StartRejected(registry, error)) ->
                let next = applyRegistry state connection registry
                return next, Error error
            | Ok(registry, _) ->
                let next = applyRegistry state connection registry
                return next, Ok next.LastSnapshot
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

let private closeOnHost config state connection worktreePath =
    async {
        let path = WorktreePath.value worktreePath

        match! listTerminals config connection with
        | Error error ->
            return
                withHostFailure error state,
                Error error
        | Ok before ->
            let listed = applyRegistry state connection before

            match findTerminalByPath path before.Terminals with
            | None ->
                let next =
                    applyRegistryAfterClose
                        path
                        listed
                        connection
                        before

                return next, Ok next.LastSnapshot
            | Some terminal ->
                let! closeResult =
                    requestTerminalClose
                        config
                        connection
                        terminal.SessionId

                match! listTerminals config connection with
                | Error listError ->
                    let error =
                        match closeResult with
                        | Error closeError ->
                            $"{closeError}; authoritative relist failed: {listError}"
                        | Ok _ ->
                            $"TerminalHost accepted the close request but its authoritative registry could not be read: {listError}"

                    return
                        withHostFailure error listed,
                        Error error
                | Ok after ->
                    match findTerminalByPath path after.Terminals with
                    | None ->
                        let next =
                            applyRegistryAfterClose
                                path
                                listed
                                connection
                                after

                        return next, Ok next.LastSnapshot
                    | Some _ ->
                        let next =
                            applyRegistry listed connection after

                        let error =
                            match closeResult with
                            | Error closeError -> closeError
                            | Ok _ ->
                                "TerminalHost still lists the terminal after its close request"

                        return next, Error error
    }

let private closeTerminal config state worktreePath =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            return!
                closeOnHost
                    config
                    state
                    connection
                    worktreePath
        | discovery ->
            match safeWithoutHealthyHost config state discovery with
            | Error error ->
                return withHostFailure error state, Error error
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
                            state.LastSnapshot
                            |> interruptSnapshot reason
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

let internal createWithConfig config =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop state =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Get reply when state.Phase = ManagerPhase.Replacing ->
                        reply.Reply state.LastSnapshot
                        return! loop state
                    | Get reply ->
                        let! next = getTerminals config state
                        reply.Reply next.LastSnapshot
                        return! loop next
                    | GetCached reply ->
                        reply.Reply state.LastSnapshot
                        return! loop state
                    | Start(_, reply) when state.Phase = ManagerPhase.Replacing ->
                        reply.Reply(Error replacementInProgressError)
                        return! loop state
                    | Start(worktreePath, reply) ->
                        let! next, result =
                            startTerminal
                                config
                                state
                                worktreePath

                        reply.Reply result
                        return! loop next
                    | Close(_, reply) when state.Phase = ManagerPhase.Replacing ->
                        reply.Reply(Error replacementInProgressError)
                        return! loop state
                    | Close(worktreePath, reply) ->
                        let! next, result =
                            closeTerminal
                                config
                                state
                                worktreePath

                        reply.Reply result
                        return! loop next
                    | BeginReplacement(_, _, reply)
                        when state.Phase = ManagerPhase.Replacing ->
                        reply.Reply ReplacementOutcome.RaceLost
                        return! loop state
                    | BeginReplacement(plan, query, reply) ->
                        async {
                            let! commit =
                                commitReplacement config plan query

                            inbox.Post(FinishReplacement(commit, reply))
                        }
                        |> Async.Start

                        return!
                            loop
                                { state with
                                    Phase = ManagerPhase.Replacing }
                    | FinishReplacement(commit, reply) ->
                        let next, outcome =
                            applyReplacementCommit state commit

                        reply.Reply outcome
                        return!
                            loop
                                { next with
                                    Phase = ManagerPhase.Steady }
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty
                  LastHost = None
                  Phase = ManagerPhase.Steady })

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
            (fun reply ->
                BeginReplacement(plan, activityQuery, reply)),
            timeout = 300_000
        )

    TerminalHostReplacement.tryReplaceHostIgnoring
        ignoredStagedVersion
        beforeRecheck
        query
        config
        commit

let internal tryReplaceHostWith beforeRecheck query manager =
    tryReplaceHostIgnoring
        None
        beforeRecheck
        query
        manager

let internal tryReplaceHost query manager =
    tryReplaceHostWith
        (fun () -> async.Return())
        query
        manager

let internal runReplacementCoordinator
    manager
    query
    (cancellationToken: System.Threading.CancellationToken)
    =
    TerminalHostReplacement.runCoordinator
        (fun ignoredStagedVersion ->
            tryReplaceHostIgnoring
                ignoredStagedVersion
                (fun () -> async.Return())
                query
                manager)
        cancellationToken

let start (Manager(_, agent)) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Start(worktreePath, reply)),
        timeout = 60_000
    )

let get (Manager(_, agent)) =
    agent.PostAndAsyncReply(Get, timeout = 60_000)

/// The current manager snapshot without host I/O (test seam for lifecycle transitions).
let internal getCached (Manager(_, agent)) =
    agent.PostAndAsyncReply(GetCached, timeout = 60_000)

let close (Manager(_, agent)) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Close(worktreePath, reply)),
        timeout = 60_000
    )

let internal closeStrict manager worktreePath =
    close manager worktreePath
