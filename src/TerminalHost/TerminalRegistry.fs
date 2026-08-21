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
    | Start of
        CanonicalWorktree *
        AsyncReplyChannel<Result<RegistrySnapshot, string>>
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

    let private snapshot state =
        { Revision = state.Revision
          Terminals =
            state.Entries
            |> Map.values
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

    let private findBySessionId sessionId entries =
        entries
        |> Map.toList
        |> List.tryFind (fun (_, terminal) ->
            String.Equals(
                terminal.Record.SessionId,
                sessionId,
                StringComparison.Ordinal
            ))

    let private removeAfterClose state (key, terminal) =
        async {
            do! closeHosted terminal

            return
                { state with
                    Entries = Map.remove key state.Entries
                    Revision = state.Revision + 1L }
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
                return
                    { state with
                        Entries = live
                        Revision = state.Revision + 1L }
        }

    let private logMailboxFailure scope (error: exn) =
        try
            Console.Error.WriteLine(
                $"TerminalRegistry {scope} failed ({error.GetType().Name})"
            )
        with _ ->
            ()

    let private replyAfterFailure state message =
        try
            match message with
            | Start(_, reply) ->
                reply.Reply(Error "Terminal registry operation failed")
            | List reply
            | Close(_, reply) -> reply.Reply(snapshot state)
            | Shutdown reply -> reply.Reply()
            | UpstreamExited _ -> ()
        with _ ->
            ()

    let create starter dataPlaneStarter =
        let mailbox =
            MailboxProcessor.Start(fun inbox ->
                let processMessage state message =
                    async {
                        let! current = pruneExited state

                        match message with
                        | Start(worktree, reply) when current.Stopped ->
                            reply.Reply(Error "Terminal host is shutting down")
                            return current
                        | Start(worktree, reply) ->
                            let key = CanonicalWorktree.key worktree

                            match Map.tryFind key current.Entries with
                            | Some _ ->
                                reply.Reply(Ok(snapshot current))
                                return current
                            | None ->
                                let sessionId = Guid.NewGuid().ToString("N")
                                let! started = starter sessionId worktree

                                match started with
                                | Error error ->
                                    reply.Reply(Error error)
                                    return current
                                | Ok terminalProcess when terminalProcess.HasExited() ->
                                    terminalProcess.Close()
                                    reply.Reply(Error "ttyd exited during terminal startup")
                                    return current
                                | Ok terminalProcess ->
                                    let notifyUpstreamExited () =
                                        inbox.Post(UpstreamExited sessionId)

                                    let! dataPlaneResult =
                                        async {
                                            try
                                                return!
                                                    dataPlaneStarter
                                                        sessionId
                                                        terminalProcess.TtydPort
                                                        notifyUpstreamExited
                                            with error ->
                                                try
                                                    terminalProcess.Close()
                                                with cleanupError ->
                                                    logMailboxFailure
                                                        "startup cleanup"
                                                        cleanupError

                                                return raise error
                                        }

                                    match dataPlaneResult with
                                    | Error error ->
                                        terminalProcess.Close()
                                        reply.Reply(Error error)
                                        return current
                                    | Ok dataPlane when terminalProcess.HasExited() ->
                                        do! stopAndClose dataPlane terminalProcess
                                        reply.Reply(Error "ttyd exited during terminal startup")
                                        return current
                                    | Ok dataPlane ->
                                        let terminal =
                                            { Record =
                                                { SessionId = sessionId
                                                  WorktreePath =
                                                    CanonicalWorktree.path worktree
                                                  AttachmentEndpoint =
                                                    dataPlane.AttachmentEndpoint }
                                              Process = terminalProcess
                                              DataPlane = dataPlane
                                              OpenedOrder = current.NextOpenedOrder }

                                        let updated =
                                            { current with
                                                Entries = Map.add key terminal current.Entries
                                                Revision = current.Revision + 1L
                                                NextOpenedOrder = current.NextOpenedOrder + 1L }

                                        reply.Reply(Ok(snapshot updated))
                                        return updated
                        | List reply ->
                            reply.Reply(snapshot current)
                            return current
                        | Close(sessionId, reply) ->
                            match findBySessionId sessionId current.Entries with
                            | None ->
                                reply.Reply(snapshot current)
                                return current
                            | Some matched ->
                                let! updated = removeAfterClose current matched
                                reply.Reply(snapshot updated)
                                return updated
                        | Shutdown reply ->
                            do! closeAll current.Entries

                            let updated =
                                if Map.isEmpty current.Entries && current.Stopped then
                                    current
                                else
                                    { current with
                                        Entries = Map.empty
                                        Revision =
                                            if Map.isEmpty current.Entries then
                                                current.Revision
                                            else
                                                current.Revision + 1L
                                        Stopped = true }

                            reply.Reply()
                            return updated
                        | UpstreamExited sessionId ->
                            match findBySessionId sessionId current.Entries with
                            | None -> return current
                            | Some matched ->
                                return! removeAfterClose current matched
                    }

                let rec loop state =
                    async {
                        let! message = inbox.Receive()
                        let! next =
                            async {
                                try
                                    return! processMessage state message
                                with error ->
                                    logMailboxFailure "message" error
                                    replyAfterFailure state message
                                    return state
                            }

                        return! loop next
                    }

                loop
                    { Entries = Map.empty
                      Revision = 0L
                      NextOpenedOrder = 0L
                      Stopped = false })

        mailbox.Error.Add(fun error ->
            logMailboxFailure "mailbox" error)

        TerminalRegistry mailbox

    let start (TerminalRegistry mailbox) worktree =
        mailbox.PostAndAsyncReply(
            (fun reply -> Start(worktree, reply)),
            timeout = ReplyTimeoutMilliseconds
        )

    let list (TerminalRegistry mailbox) =
        mailbox.PostAndAsyncReply(List, timeout = ReplyTimeoutMilliseconds)

    let close (TerminalRegistry mailbox) sessionId =
        mailbox.PostAndAsyncReply(
            (fun reply -> Close(sessionId, reply)),
            timeout = ReplyTimeoutMilliseconds
        )

    let shutdown (TerminalRegistry mailbox) =
        mailbox.PostAndAsyncReply(
            Shutdown,
            timeout = ShutdownReplyTimeoutMilliseconds
        )
