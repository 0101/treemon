namespace TerminalHost

open System

type private HostedTerminal =
    { Record: TerminalRecord
      Process: TerminalProcess
      DataPlane: TerminalDataPlane
      OpenedOrder: int64 }

type private RegistryState =
    { Entries: Map<string, HostedTerminal>
      Revision: int64
      NextOpenedOrder: int64
      Stopped: bool }

type private RegistryMessage =
    | Start of CanonicalWorktree * AsyncReplyChannel<Result<RegistrySnapshot, string>>
    | List of AsyncReplyChannel<RegistrySnapshot>
    | Close of string * AsyncReplyChannel<RegistrySnapshot>
    | Shutdown of AsyncReplyChannel<unit>
    | UpstreamExited of string

type TerminalRegistry =
    private
        | TerminalRegistry of MailboxProcessor<RegistryMessage>

[<RequireQualifiedAccess>]
module TerminalRegistry =
    [<Literal>]
    let private ReplyTimeoutMilliseconds = 60_000

    [<Literal>]
    let private ShutdownReplyTimeoutMilliseconds = 300_000

    [<Literal>]
    let private MaximumTerminals = 1024

    let private snapshot state =
        { Revision = state.Revision
          Terminals =
            state.Entries |> Map.values
            |> Seq.sortBy _.OpenedOrder
            |> Seq.map _.Record
            |> Seq.toList }

    let private stopAndClose dataPlane terminalProcess =
        async {
            try
                do! dataPlane.Stop()
            finally
                terminalProcess.Close()
        }

    let private closeHosted terminal =
        stopAndClose terminal.DataPlane terminal.Process

    let private closeAll entries =
        entries
        |> Map.values
        |> Seq.map closeHosted
        |> Async.Sequential
        |> Async.Ignore

    let private removeAfterClose state (key, terminal) =
        async {
            do! closeHosted terminal

            return { state with Entries = Map.remove key state.Entries; Revision = state.Revision + 1L }
        }

    let private pruneExited state =
        async {
            let exited, live =
                state.Entries
                |> Map.partition (fun _ terminal -> terminal.Process.HasExited())

            do! closeAll exited

            if Map.isEmpty exited then
                return state
            else
                return { state with Entries = live; Revision = state.Revision + 1L }
        }

    let private respond (channel: AsyncReplyChannel<'value>) value state =
        channel.Reply value
        state

    let private recoverMessage state message =
        async {
            try
                match message with
                | Start(_, reply) ->
                    return respond reply (Error "Terminal registry operation failed") state
                | List reply
                | Close(_, reply) -> return respond reply (snapshot state) state
                | Shutdown reply -> return respond reply () state
                | UpstreamExited _ -> return state
            with _ ->
                return state
        }

    let private startHosted starter dataPlaneStarter notify worktree openedOrder =
        async {
            let sessionId = Guid.NewGuid().ToString("N")

            match! starter sessionId worktree with
            | Error error -> return Error error
            | Ok terminalProcess when terminalProcess.HasExited() ->
                terminalProcess.Close()
                return Error "ttyd exited during terminal startup"
            | Ok terminalProcess ->
                let! dataPlaneResult =
                    async {
                        try
                            return! dataPlaneStarter sessionId terminalProcess.TtydPort (fun () -> notify sessionId)
                        with error ->
                            terminalProcess.Close()
                            return raise error
                    }

                match dataPlaneResult with
                | Error error ->
                    terminalProcess.Close()
                    return Error error
                | Ok dataPlane when terminalProcess.HasExited() ->
                    do! stopAndClose dataPlane terminalProcess
                    return Error "ttyd exited during terminal startup"
                | Ok dataPlane ->
                    return
                        Ok
                            { Record =
                                { SessionId = sessionId
                                  WorktreePath = CanonicalWorktree.path worktree
                                  AttachmentEndpoint = dataPlane.AttachmentEndpoint }
                              Process = terminalProcess; DataPlane = dataPlane
                              OpenedOrder = openedOrder }
        }

    let create starter dataPlaneStarter =
        let initial =
            { Entries = Map.empty; Revision = 0L
              NextOpenedOrder = 0L; Stopped = false }

        let processMessage (inbox: MailboxProcessor<RegistryMessage>) state message =
            async {
                let! current = pruneExited state

                match message with
                | Start(_, reply) when current.Stopped ->
                    return respond reply (Error "Terminal host is shutting down") current
                | Start(_, reply) when current.Entries.Count >= MaximumTerminals ->
                    return respond reply (Error "Terminal host has reached its terminal limit") current
                | Start(worktree, reply) ->
                    match!
                        startHosted
                            starter
                            dataPlaneStarter
                            (UpstreamExited >> inbox.Post)
                            worktree
                            current.NextOpenedOrder
                    with
                    | Error error -> return respond reply (Error error) current
                    | Ok terminal ->
                        let updated =
                            { current with
                                Entries =
                                    current.Entries
                                    |> Map.add terminal.Record.SessionId terminal
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
                    do! closeAll current.Entries

                    let updated =
                        { current with
                            Entries = Map.empty
                            Revision = current.Revision + if Map.isEmpty current.Entries then 0L else 1L
                            Stopped = true }

                    return respond reply () updated
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
