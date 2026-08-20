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

type TerminalRegistry =
    private
        | TerminalRegistry of MailboxProcessor<RegistryMessage>

[<RequireQualifiedAccess>]
module TerminalRegistry =
    let private snapshot state =
        { Revision = state.Revision
          Terminals =
            state.Entries
            |> Map.values
            |> Seq.sortBy _.OpenedOrder
            |> Seq.map _.Record
            |> Seq.toList }

    let private closeHosted terminal =
        async {
            try
                do! terminal.DataPlane.Stop()
            finally
                terminal.Process.Close()
        }

    let private closeAll entries =
        entries
        |> Map.values
        |> Seq.map closeHosted
        |> Async.Sequential
        |> Async.Ignore

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

    let create starter dataPlaneStarter =
        let mailbox =
            MailboxProcessor.Start(fun inbox ->
                let rec loop state =
                    async {
                        let! message = inbox.Receive()
                        let! current = pruneExited state

                        match message with
                        | Start(worktree, reply) when current.Stopped ->
                            reply.Reply(Error "Terminal host is shutting down")
                            return! loop current
                        | Start(worktree, reply) ->
                            let key = CanonicalWorktree.key worktree

                            match Map.tryFind key current.Entries with
                            | Some _ ->
                                reply.Reply(Ok(snapshot current))
                                return! loop current
                            | None ->
                                let sessionId = Guid.NewGuid().ToString("N")
                                let! started = starter sessionId worktree

                                match started with
                                | Error error ->
                                    reply.Reply(Error error)
                                    return! loop current
                                | Ok terminalProcess when terminalProcess.HasExited() ->
                                    terminalProcess.Close()
                                    reply.Reply(Error "ttyd exited during terminal startup")
                                    return! loop current
                                | Ok terminalProcess ->
                                    match! dataPlaneStarter sessionId terminalProcess with
                                    | Error error ->
                                        terminalProcess.Close()
                                        reply.Reply(Error error)
                                        return! loop current
                                    | Ok dataPlane when terminalProcess.HasExited() ->
                                        do! dataPlane.Stop()
                                        terminalProcess.Close()
                                        reply.Reply(Error "ttyd exited during terminal startup")
                                        return! loop current
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
                                        return! loop updated
                        | List reply ->
                            reply.Reply(snapshot current)
                            return! loop current
                        | Close(sessionId, reply) ->
                            let matched =
                                current.Entries
                                |> Map.toList
                                |> List.tryFind (fun (_, terminal) ->
                                    String.Equals(
                                        terminal.Record.SessionId,
                                        sessionId,
                                        StringComparison.Ordinal
                                    ))

                            match matched with
                            | None ->
                                reply.Reply(snapshot current)
                                return! loop current
                            | Some(key, terminal) ->
                                do! closeHosted terminal

                                let updated =
                                    { current with
                                        Entries = Map.remove key current.Entries
                                        Revision = current.Revision + 1L }

                                reply.Reply(snapshot updated)
                                return! loop updated
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
                            return! loop updated
                    }

                loop
                    { Entries = Map.empty
                      Revision = 0L
                      NextOpenedOrder = 0L
                      Stopped = false })

        TerminalRegistry mailbox

    let start (TerminalRegistry mailbox) worktree =
        mailbox.PostAndAsyncReply(fun reply -> Start(worktree, reply))

    let list (TerminalRegistry mailbox) =
        mailbox.PostAndAsyncReply List

    let close (TerminalRegistry mailbox) sessionId =
        mailbox.PostAndAsyncReply(fun reply -> Close(sessionId, reply))

    let shutdown (TerminalRegistry mailbox) =
        mailbox.PostAndAsyncReply Shutdown
