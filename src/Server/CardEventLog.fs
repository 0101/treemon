module Server.CardEventLog

open System
open Shared

/// Per-branch card event log for asynchronous post-fork setup.
type CardEventLogState = { Events: Map<string, CardEvent list> }

type CardEventLogMsg =
    /// Post-fork setup begins: replace the prior lifecycle with a running marker.
    | PostForkStarted of key: string
    /// Post-fork setup ends: prepend the terminal event, dropping post-fork running markers.
    | PostForkEnded of key: string * status: StepStatus
    | GetAll of AsyncReplyChannel<Map<string, CardEvent list>>

let private mkEvent = EventUtils.makeCardEvent

let private clearRunning =
    List.filter (fun event -> event.Status <> Some StepStatus.Running)

let private branchEvents key (state: CardEventLogState) =
    state.Events |> Map.tryFind key |> Option.defaultValue []

let private setBranchEvents key events (state: CardEventLogState) =
    { state with Events = state.Events |> Map.add key events }

let processMessage (state: CardEventLogState) (msg: CardEventLogMsg) : CardEventLogState =
    match msg with
    | PostForkStarted key ->
        setBranchEvents key [ mkEvent EventSource.PostFork "setup" StepStatus.Running ] state
    | PostForkEnded (key, status) ->
        let cleared = branchEvents key state |> clearRunning
        setBranchEvents key (mkEvent EventSource.PostFork "setup" status :: cleared) state
    | GetAll _ -> state

let createAgent () : MailboxProcessor<CardEventLogMsg> =
    MailboxProcessor.Start(fun inbox ->
        let rec loop state =
            async {
                let! msg = inbox.Receive()
                match msg with
                | GetAll reply ->
                    reply.Reply(state.Events)
                    return! loop state
                | _ ->
                    return! loop (processMessage state msg)
            }
        loop { Events = Map.empty })
