module Server.CardEventLog

open System
open Shared

/// Per-branch card activity log. Sync and post-fork setup are independent
/// lifecycles that each surface progress on a worktree card as one chronological
/// event stream, keyed by the scoped branch key. This log owns that stream so no
/// single lifecycle owns it: each only ever clears its *own* in-flight (Running)
/// markers, so overlapping operations on a freshly forked worktree never clobber
/// each other's card state.
type CardEventLogState = { Events: Map<string, CardEvent list> }

type CardEventLogMsg =
    /// A sync run begins: prepend a running marker, keeping only post-fork context
    /// (an unacknowledged terminal may still be on the card) and dropping the
    /// previous run's sync history so the log can't grow unbounded across syncs.
    | SyncStarted of key: string
    /// Append an event produced by a sync pipeline step.
    | SyncStep of key: string * event: CardEvent
    /// A sync run ends: drop lingering sync running markers, leaving step
    /// terminals and any post-fork events.
    | SyncEnded of key: string
    /// A sync run is cancelled: prepend a cancelled marker, dropping sync running markers.
    | SyncCancelled of key: string
    /// Post-fork setup begins: append a running marker.
    | PostForkStarted of key: string
    /// Post-fork setup ends: prepend the terminal event, dropping post-fork running markers.
    | PostForkEnded of key: string * status: StepStatus
    | GetAll of AsyncReplyChannel<Map<string, CardEvent list>>

let private mkEvent source message status =
    { Source = source
      Message = message
      Timestamp = DateTimeOffset.Now
      Status = Some status
      Duration = None }

let private isPostForkEvent (evt: CardEvent) = evt.Source = EventSource.PostFork
let private isRunning (evt: CardEvent) = evt.Status = Some StepStatus.Running

/// Removes only the running markers owned by one lifecycle, leaving the other's
/// in-flight state untouched.
let private clearRunningSync = List.filter (fun e -> not (isRunning e && not (isPostForkEvent e)))
let private clearRunningPostFork = List.filter (fun e -> not (isRunning e && isPostForkEvent e))

let private branchEvents key (state: CardEventLogState) =
    state.Events |> Map.tryFind key |> Option.defaultValue []

let private setBranchEvents key events (state: CardEventLogState) =
    { state with Events = state.Events |> Map.add key events }

let processMessage (state: CardEventLogState) (msg: CardEventLogMsg) : CardEventLogState =
    match msg with
    | SyncStarted key ->
        let preserved = branchEvents key state |> List.filter isPostForkEvent
        setBranchEvents key (mkEvent EventSource.Sync "Sync starting" StepStatus.Running :: preserved) state
    | SyncStep (key, event) ->
        setBranchEvents key (event :: branchEvents key state) state
    | SyncEnded key ->
        setBranchEvents key (branchEvents key state |> clearRunningSync) state
    | SyncCancelled key ->
        let cleared = branchEvents key state |> clearRunningSync
        setBranchEvents key (mkEvent EventSource.Sync "Sync cancelled" StepStatus.Cancelled :: cleared) state
    | PostForkStarted key ->
        setBranchEvents key (mkEvent EventSource.PostFork "setup" StepStatus.Running :: branchEvents key state) state
    | PostForkEnded (key, status) ->
        let cleared = branchEvents key state |> clearRunningPostFork
        setBranchEvents key (mkEvent EventSource.PostFork "setup" status :: cleared) state
    | GetAll reply ->
        reply.Reply(state.Events)
        state

let createAgent () : MailboxProcessor<CardEventLogMsg> =
    MailboxProcessor.Start(fun inbox ->
        let rec loop state =
            async {
                let! msg = inbox.Receive()
                return! loop (processMessage state msg)
            }
        loop { Events = Map.empty })
