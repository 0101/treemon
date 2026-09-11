namespace TerminalHost

open System

type private HostedTerminal = { Record: TerminalRecord; Process: TerminalProcess; DataPlane: TerminalDataPlane; OpenedOrder: int64 }
type private PendingTerminalCleanup = { Process: TerminalProcess; DataPlane: TerminalDataPlane option }
type private RegistryState = { Entries: Map<string, HostedTerminal>; PendingCleanups: Map<string, PendingTerminalCleanup>; Revision: int64; NextOpenedOrder: int64; Stopped: bool }

type private RegistryMessage =
    | Start of CanonicalWorktree * AsyncReplyChannel<Result<RegistrySnapshot, string>>
    | List of AsyncReplyChannel<RegistrySnapshot>
    | Close of string * AsyncReplyChannel<RegistrySnapshot>
    | Shutdown of AsyncReplyChannel<bool>
    | UpstreamExited of string

type TerminalRegistry = private | TerminalRegistry of MailboxProcessor<RegistryMessage>

[<RequireQualifiedAccess>]
module TerminalRegistry =
    type private TerminalCloseFailure =
        | ProcessPreparationFailed of string
        | ProcessCleanupFailed of string

    type private HostedStartResult =
        | Hosted of HostedTerminal
        | Rejected of string
        | RejectedWithCleanup of string * string * PendingTerminalCleanup

    let private ReplyTimeoutMilliseconds, ShutdownReplyTimeoutMilliseconds = 60_000, 300_000
    // Bounds concurrent terminal teardowns during shutdown/pruning, so closing many terminals stays
    // well inside the server's replacement wait instead of running serially.
    let private ShutdownParallelism, MaximumTerminals = 256, 1024

    let private snapshot state =
        { Revision = state.Revision
          Terminals = state.Entries |> Map.values |> Seq.sortBy _.OpenedOrder |> Seq.map _.Record |> Seq.toList }

    let private closeTerminal sessionId (dataPlane: TerminalDataPlane option) (terminalProcess: TerminalProcess) =
        async {
            let! result, dataPlaneFailed =
                match
                    try terminalProcess.BeginClose()
                    with _ -> Error "terminal process cleanup preparation failed"
                with
                | Error error ->
                    async.Return(Error(ProcessPreparationFailed error), false)
                | Ok complete ->
                    async {
                        let! dataPlaneStopped =
                            match dataPlane with
                            | None -> async.Return true
                            | Some running ->
                                async {
                                    let! stopped = running.Stop() |> Async.Catch

                                    return
                                        match stopped with
                                        | Choice1Of2() -> true
                                        | Choice2Of2 _ -> false
                                }

                        let cleanup =
                            try complete()
                            with _ -> Error "process cleanup failed"

                        return
                            (match cleanup with
                             | Ok() -> Ok()
                             | Error error -> Error(ProcessCleanupFailed error)),
                            not dataPlaneStopped
                    }

            TerminalHostDiagnostics.write (
                TerminalHostDiagnostic.TerminalClose
                    { TerminalSessionId = sessionId
                      Outcome =
                        match result, dataPlaneFailed with
                        | Ok(), false ->
                            TerminalCloseOutcome.Completed
                        | Ok(), true ->
                            TerminalCloseOutcome.DataPlaneFailed
                        | Error(ProcessPreparationFailed _), _ ->
                            TerminalCloseOutcome.ProcessPreparationFailed
                        | Error(ProcessCleanupFailed _), _ ->
                            TerminalCloseOutcome.ProcessCleanupFailed }
            )

            return result
        }

    let private cleanupFailureMessage =
        function
        | ProcessPreparationFailed error
        | ProcessCleanupFailed error -> error

    let private pendingCleanupMessage startupError cleanupError =
        $"{startupError}; terminal cleanup remains pending: {cleanupError}"

    let private closeCleanupBatch (cleanups: Map<string, PendingTerminalCleanup>) =
        cleanups
        |> Map.toList
        |> List.map (fun (sessionId, cleanup) ->
            async {
                let! result = closeTerminal sessionId cleanup.DataPlane cleanup.Process
                return sessionId, cleanup, result
            })
        |> fun work -> Async.Parallel(work, maxDegreeOfParallelism = ShutdownParallelism)

    let private closeAll (entries: Map<string, HostedTerminal>) =
        async {
            let! results =
                entries
                |> Map.map (fun _ terminal ->
                    { Process = terminal.Process
                      DataPlane = Some terminal.DataPlane })
                |> closeCleanupBatch

            return
                results
                |> Array.choose (function
                    | key, _, Ok() -> Some key
                    | _, _, Error _ -> None)
                |> Set.ofArray
        }

    let private retryPendingCleanups (state: RegistryState) =
        async {
            let! results = closeCleanupBatch state.PendingCleanups

            let remaining =
                results
                |> Array.choose (function
                    | sessionId, cleanup, Error _ -> Some(sessionId, cleanup)
                    | _, _, Ok() -> None)
                |> Map.ofArray

            return { state with PendingCleanups = remaining }
        }

    let private removeAfterClose (state: RegistryState) (key, terminal: HostedTerminal) =
        async {
            match! closeTerminal key (Some terminal.DataPlane) terminal.Process with
            | Error _ ->
                return state
            | Ok() ->
                return { state with Entries = Map.remove key state.Entries; Revision = state.Revision + 1L }
        }

    let private pruneExited (state: RegistryState) =
        async {
            let exited, _ = state.Entries |> Map.partition (fun _ terminal -> terminal.Process.HasExited())
            let! closed = closeAll exited
            if Set.isEmpty closed then return state
            else
                return
                    { state with
                        Entries = state.Entries |> Map.filter (fun key _ -> not (Set.contains key closed))
                        Revision = state.Revision + 1L }
        }

    let private maintain (state: RegistryState) =
        async {
            let! afterPendingCleanup = retryPendingCleanups state
            return! pruneExited afterPendingCleanup
        }

    let private respond (channel: AsyncReplyChannel<'value>) value state =
        channel.Reply value; state

    let private recoverMessage state message =
        async {
            try
                match message with
                | Start(_, reply) -> return respond reply (Error "Terminal registry operation failed") state
                | List reply
                | Close(_, reply) -> return respond reply (snapshot state) state
                | Shutdown reply -> return respond reply false state
                | UpstreamExited _ -> return state
            with _ -> return state
        }

    let private rejectAfterCleanup
        sessionId
        startupError
        (dataPlane: TerminalDataPlane option)
        (terminalProcess: TerminalProcess)
        =
        async {
            match! closeTerminal sessionId dataPlane terminalProcess with
            | Ok() ->
                return Rejected startupError
            | Error cleanupFailure ->
                return
                    RejectedWithCleanup(
                        pendingCleanupMessage
                            startupError
                            (cleanupFailureMessage cleanupFailure),
                        sessionId,
                        { Process = terminalProcess
                          DataPlane = dataPlane }
                    )
        }

    let private startHosted starter dataPlaneStarter notify worktree openedOrder =
        async {
            let sessionId = Guid.NewGuid().ToString("N")

            match! starter sessionId worktree with
            | Error(TerminalLaunchFailure.LaunchFailed error) ->
                return Rejected error
            | Error(TerminalLaunchFailure.CleanupPending(startupError, cleanupError, terminalProcess)) ->
                return
                    RejectedWithCleanup(
                        pendingCleanupMessage startupError cleanupError,
                        sessionId,
                        { Process = terminalProcess
                          DataPlane = None }
                    )
            | Ok terminalProcess when terminalProcess.HasExited() ->
                return!
                    rejectAfterCleanup
                        sessionId
                        "ttyd exited during terminal startup"
                        None
                        terminalProcess
            | Ok terminalProcess ->
                let! dataPlaneResult =
                    dataPlaneStarter
                        sessionId
                        terminalProcess.TtydPort
                        (fun () -> notify sessionId)
                    |> Async.Catch

                match dataPlaneResult with
                | Choice2Of2 _ ->
                    return!
                        rejectAfterCleanup
                            sessionId
                            "Terminal registry operation failed"
                            None
                            terminalProcess
                | Choice1Of2(Error error) ->
                    return!
                        rejectAfterCleanup
                            sessionId
                            error
                            None
                            terminalProcess
                | Choice1Of2(Ok dataPlane) when terminalProcess.HasExited() ->
                    return!
                        rejectAfterCleanup
                            sessionId
                            "ttyd exited during terminal startup"
                            (Some dataPlane)
                            terminalProcess
                | Choice1Of2(Ok dataPlane) ->
                    return
                        Hosted
                            { Record = { SessionId = sessionId; WorktreePath = CanonicalWorktree.path worktree; AttachmentEndpoint = dataPlane.AttachmentEndpoint }
                              Process = terminalProcess; DataPlane = dataPlane; OpenedOrder = openedOrder }
        }

    let create starter dataPlaneStarter =
        let initial =
            { Entries = Map.empty
              PendingCleanups = Map.empty
              Revision = 0L
              NextOpenedOrder = 0L
              Stopped = false }

        let processMessage (inbox: MailboxProcessor<RegistryMessage>) state message =
            async {
                let! current =
                    match message with
                    | Start _
                    | List _ -> maintain state
                    | Close _ | Shutdown _ | UpstreamExited _ -> async.Return state

                match message with
                | Start(_, reply) when current.Stopped -> return respond reply (Error "Terminal host is shutting down") current
                | Start(_, reply) when current.Entries.Count + current.PendingCleanups.Count >= MaximumTerminals ->
                    return respond reply (Error "Terminal host has reached its terminal limit") current
                | Start(worktree, reply) ->
                    match! startHosted starter dataPlaneStarter (UpstreamExited >> inbox.Post) worktree current.NextOpenedOrder with
                    | Rejected error ->
                        return respond reply (Error error) current
                    | RejectedWithCleanup(error, sessionId, cleanup) ->
                        let updated =
                            { current with
                                PendingCleanups =
                                    current.PendingCleanups
                                    |> Map.add sessionId cleanup }

                        return respond reply (Error error) updated
                    | Hosted terminal ->
                        let updated =
                            { current with
                                Entries = current.Entries |> Map.add terminal.Record.SessionId terminal
                                Revision = current.Revision + 1L
                                NextOpenedOrder = current.NextOpenedOrder + 1L }
                        return respond reply (Ok(snapshot updated)) updated
                | List reply -> return respond reply (snapshot current) current
                | Close(sessionId, reply) ->
                    match Map.tryFind sessionId current.Entries with
                    | None -> return respond reply (snapshot current) current
                    | Some terminal ->
                        let! updated = removeAfterClose current (sessionId, terminal)
                        return respond reply (snapshot updated) updated
                | Shutdown reply ->
                    let! afterPendingCleanup = retryPendingCleanups current
                    let! closed = closeAll afterPendingCleanup.Entries
                    let remaining =
                        afterPendingCleanup.Entries
                        |> Map.filter (fun key _ -> not (Set.contains key closed))

                    let clean =
                        remaining.IsEmpty
                        && Map.isEmpty afterPendingCleanup.PendingCleanups

                    let updated =
                        { afterPendingCleanup with
                            Entries = remaining
                            Revision = afterPendingCleanup.Revision + if closed.IsEmpty then 0L else 1L
                            Stopped = clean }

                    return
                        respond
                            reply
                            clean
                            updated
                | UpstreamExited sessionId ->
                    match Map.tryFind sessionId current.Entries with
                    | None -> return current
                    | Some terminal ->
                        return! removeAfterClose current (sessionId, terminal)
            }
        let mailbox = ResilientMailbox.start "TerminalRegistry" initial recoverMessage processMessage
        TerminalRegistry mailbox

    let start (TerminalRegistry mailbox) worktree =
        ResilientMailbox.ask ReplyTimeoutMilliseconds (fun reply -> Start(worktree, reply)) mailbox
    let list (TerminalRegistry mailbox) =
        ResilientMailbox.ask ReplyTimeoutMilliseconds List mailbox
    let close (TerminalRegistry mailbox) sessionId =
        ResilientMailbox.ask ReplyTimeoutMilliseconds (fun reply -> Close(sessionId, reply)) mailbox
    let shutdown (TerminalRegistry mailbox) =
        ResilientMailbox.ask ShutdownReplyTimeoutMilliseconds Shutdown mailbox
