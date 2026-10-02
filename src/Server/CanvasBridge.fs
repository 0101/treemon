module Server.CanvasBridge

open System
open System.Threading
open System.Threading.Tasks
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

let private normalizePath = Server.PathUtils.normalizePath

type internal PendingLaunchResult =
    | PendingLaunchStarted of startedAt: DateTime
    | PendingLaunchJoined of startedAt: DateTime * EmbeddedTerminalId option

type private PendingLaunch =
    { StartedAt: DateTime
      Recipient: (TerminalSessionId * (unit -> Async<SessionId option>)) option }

type private Coordination =
    { Launch: PendingLaunch option
      Handled: Set<Guid> }

type private LaunchMsg =
    | BeginPendingLaunch of worktreeKey: string * now: DateTime * AsyncReplyChannel<PendingLaunchResult>
    | CancelPendingLaunch of worktreeKey: string * startedAt: DateTime option * AsyncReplyChannel<unit>
    | ReservePendingLaunch of worktreeKey: string * startedAt: DateTime * AsyncReplyChannel<bool>
    | CompletePendingLaunch of worktreeKey: string * startedAt: DateTime * terminalId: TerminalSessionId * resolveSession: (unit -> Async<SessionId option>) * AsyncReplyChannel<unit>
    | BeginFallbackCoordination of worktreeKey: string * AsyncReplyChannel<bool>
    | EndFallbackCoordination of worktreeKey: string * handled: Set<Guid> * finished: TaskCompletionSource<unit> * AsyncReplyChannel<SessionId option option>
    | ReleaseFallbackCoordination of worktreeKey: string * finished: TaskCompletionSource<unit>

/// How long a started launch suppresses another one for the same worktree. A spawn that never
/// registers must not block future interactions, so suppression expires independently of bridges.
let private launchSuppressionWindow = TimeSpan.FromSeconds 30.0

/// Suppresses a second session spawn for the same worktree while one is starting up, so repeated
/// interactions arriving before the new session registers do not each spawn an agent. It bounds
/// only Treemon's own spawns; sessions the user starts concurrently are not arbitrated.
let private launchAgent =
    MailboxProcessor.Start(fun inbox ->
        let admit worktreeKey (pending: PendingLaunch) =
            async {
                let! reserved = SessionBridge.reservePendingSystemViews worktreeKey pending.StartedAt
                match pending.Recipient with
                | Some(terminal, resolveSession) ->
                    do! SessionBridge.targetPendingSystemViews worktreeKey pending.StartedAt terminal resolveSession
                | None -> ()
                return reserved
            }
        let markHandled worktreeKey handled (coordination: Coordination) =
            async {
                let! live = SessionBridge.pendingPrompts worktreeKey
                return
                    { coordination with
                        Handled =
                            Set.union coordination.Handled handled
                            |> Set.intersect (live |> List.map _.Id |> Set.ofList) }
            }
        let recentLaunch now worktreeKey launches =
            launches |> Map.tryFind worktreeKey
            |> Option.filter (fun pending -> now - pending.StartedAt < launchSuppressionWindow)
        let rec loop (launches: Map<string, PendingLaunch>) (coordinating: Map<string, Coordination>) =
            async {
                match! inbox.Receive() with
                | BeginPendingLaunch(worktreeKey, now, reply) ->
                    match recentLaunch now worktreeKey launches with
                    | Some pending ->
                        let terminal =
                            pending.Recipient
                            |> Option.map (fst >> TerminalSessionId.value >> EmbeddedTerminalId)
                        reply.Reply(PendingLaunchJoined(pending.StartedAt, terminal))
                        return! loop launches coordinating
                    | None ->
                        reply.Reply(PendingLaunchStarted now)
                        return! loop (launches |> Map.add worktreeKey { StartedAt = now; Recipient = None }) coordinating

                | CancelPendingLaunch(worktreeKey, expectedStart, reply) ->
                    let cancelledStart =
                        expectedStart
                        |> Option.orElseWith (fun () -> launches |> Map.tryFind worktreeKey |> Option.map _.StartedAt)
                    match cancelledStart with
                    | Some startedAt -> do! SessionBridge.releasePendingSystemViews worktreeKey startedAt
                    | None -> ()
                    let remaining =
                        launches
                        |> Map.change worktreeKey (Option.filter (fun pending ->
                            expectedStart |> Option.exists ((<>) pending.StartedAt)))
                    let coordinated =
                        coordinating
                        |> Map.change worktreeKey (Option.map (fun current ->
                            { current with
                                Launch =
                                    current.Launch
                                    |> Option.filter (fun pending -> cancelledStart <> Some pending.StartedAt) }))
                    reply.Reply()
                    return! loop remaining coordinated
                | ReservePendingLaunch(worktreeKey, startedAt, reply) ->
                    let pending =
                        coordinating
                        |> Map.tryFind worktreeKey
                        |> Option.bind _.Launch
                        |> Option.filter (fun owned -> owned.StartedAt = startedAt)
                        |> Option.orElseWith (fun () ->
                            launches |> Map.tryFind worktreeKey
                            |> Option.filter (fun current -> current.StartedAt = startedAt))
                    match pending with
                    | Some pending ->
                        let! handled = admit worktreeKey pending
                        let! coordinated =
                            async {
                                match coordinating |> Map.tryFind worktreeKey with
                                | Some current ->
                                    let owned =
                                        match current.Launch with
                                        | Some previous when previous.StartedAt <> startedAt && previous.Recipient.IsNone ->
                                            current.Launch
                                        | _ -> Some pending
                                    let! updated = markHandled worktreeKey handled { current with Launch = owned }
                                    return coordinating |> Map.add worktreeKey updated
                                | None -> return coordinating
                            }
                        reply.Reply true
                        return! loop launches coordinated
                    | _ ->
                        do! SessionBridge.releasePendingSystemViews worktreeKey startedAt
                        reply.Reply false
                        return! loop launches coordinating
                | CompletePendingLaunch(worktreeKey, startedAt, terminalId, resolveSession, reply) ->
                    let complete (pending: PendingLaunch) =
                        if pending.StartedAt = startedAt then
                            { pending with Recipient = Some(terminalId, resolveSession) }
                        else pending
                    let completed =
                        launches
                        |> Map.change worktreeKey (Option.map complete)
                    do! SessionBridge.targetPendingSystemViews worktreeKey startedAt terminalId resolveSession
                    let coordinated =
                        coordinating
                        |> Map.change worktreeKey (Option.map (fun current ->
                            { current with
                                Launch = current.Launch |> Option.map complete }))
                    reply.Reply()
                    return! loop completed coordinated
                | BeginFallbackCoordination(worktreeKey, reply) ->
                    match coordinating |> Map.tryFind worktreeKey with
                    | Some current ->
                        let! updated =
                            async {
                                match current.Launch with
                                | Some pending ->
                                    let! handled = admit worktreeKey pending
                                    return! markHandled worktreeKey handled current
                                | None -> return current
                            }
                        reply.Reply false
                        return! loop launches (coordinating |> Map.add worktreeKey updated)
                    | None ->
                        reply.Reply true
                        return! loop launches (coordinating |> Map.add worktreeKey { Launch = None; Handled = Set.empty })
                | EndFallbackCoordination(_, _, finished, reply) when finished.Task.IsCompleted ->
                    reply.Reply None
                    return! loop launches coordinating
                | EndFallbackCoordination(worktreeKey, handled, finished, reply) ->
                    let current = coordinating[worktreeKey]
                    let! admitted =
                        match current.Launch with
                        | Some pending -> admit worktreeKey pending
                        | None -> async.Return Set.empty
                    let! updated = markHandled worktreeKey (Set.union handled admitted) current
                    SessionBridge.retryPending worktreeKey
                    let next =
                        SessionBridge.pendingSystemViewWork worktreeKey
                        |> List.tryFind (fun (id, _) -> not (updated.Handled.Contains id))
                        |> Option.map snd
                    if next.IsNone then finished.TrySetResult() |> ignore
                    reply.Reply next
                    let remaining =
                        if next.IsNone then Map.remove worktreeKey coordinating
                        else coordinating |> Map.add worktreeKey updated
                    return! loop launches remaining
                | ReleaseFallbackCoordination(worktreeKey, finished) ->
                    let remaining =
                        if finished.TrySetResult() then Map.remove worktreeKey coordinating else coordinating
                    return! loop launches remaining
            }

        loop Map.empty Map.empty)

/// `PendingLaunchStarted` when the caller should spawn a session, `PendingLaunchJoined` when one is
/// already starting for this worktree.
let internal beginPendingLaunchAt now worktreePath =
    launchAgent.PostAndAsyncReply(fun reply ->
        BeginPendingLaunch(normalizePath worktreePath, now, reply))

let internal beginPendingLaunch worktreePath =
    beginPendingLaunchAt DateTime.UtcNow worktreePath

/// Release the suppression after a spawn fails, so the next interaction can try again immediately.
let internal cancelPendingLaunch worktreePath =
    launchAgent.PostAndAsyncReply(fun reply -> CancelPendingLaunch(normalizePath worktreePath, None, reply))

let internal cancelPendingLaunchAt worktreePath startedAt =
    launchAgent.PostAndAsyncReply(fun reply ->
        CancelPendingLaunch(normalizePath worktreePath, Some startedAt, reply))

let internal reservePendingLaunch worktreePath startedAt =
    launchAgent.PostAndAsyncReply(fun reply ->
        ReservePendingLaunch(normalizePath worktreePath, startedAt, reply))

let internal completePendingLaunch worktreePath startedAt terminalId resolveSession =
    launchAgent.PostAndAsyncReply(fun reply ->
        CompletePendingLaunch(normalizePath worktreePath, startedAt, terminalId, resolveSession, reply))

let internal selectSystemViewTarget
    now
    (worktreeInstances: StoredInstance seq)
    (liveSessions: SessionBridge.SessionEntry list)
    =
    let mostRecentlyActive =
        worktreeInstances
        |> Seq.filter (fun stored ->
            StoredInstance.isOpenAt now stored
            && stored.UpdatedAt <> DateTimeOffset.MinValue)
        |> List.ofSeq
        |> StoredInstance.tryMostRecentActivity
        |> Option.map _.SessionId

    let freshestReachable () =
        liveSessions
        |> List.sortByDescending _.RegisteredAt
        |> List.tryHead
        |> Option.map _.SessionId

    mostRecentlyActive |> Option.orElseWith freshestReachable

/// Which session receives an interaction from a canvas document.
///
/// An AgentDoc has a real author, so it keeps its persisted owner. A SystemView is server-generated
/// and has no author. Open activity chooses its recipient even during a temporary bridge gap;
/// without activity, a reachable bridge remains eligible. No view affinity is persisted.
let internal resolveTarget
    (sessionInstances: StoredInstance seq)
    (worktreePath: string)
    (filename: string)
    =
    async {
        match CanvasDocKinds.classify filename with
        | AgentDoc ->
            return! CanvasDocOwnership.getOwnerSessionId worktreePath filename
        | SystemView ->
            let now = DateTime.UtcNow
            let worktreeKey = normalizePath worktreePath

            let liveSessions =
                SessionBridge.canvasSessionsForWorktreeAt now worktreePath

            let worktreeInstances =
                sessionInstances
                |> Seq.filter (fun stored ->
                    stored.WorktreePath
                    |> WorktreePath.value
                    |> normalizePath
                    |> (=) worktreeKey)

            return selectSystemViewTarget (DateTimeOffset now) worktreeInstances liveSessions
    }

/// What routing decided, in the caller's terms. `QueuedNeedingSession` means nothing could receive
/// the interaction *and* the document kind allows starting one — the caller owns session lifecycle,
/// so it decides whether to spawn, without re-deriving the document kind.
type internal CanvasSendOutcome =
    | Routed of CanvasMessageResult
    | QueuedNeedingSession of recipient: SessionId option * CanvasMessageResult

/// Route one canvas interaction.
let internal sendMessage
    (queueCancellationToken: CancellationToken)
    (sessionInstances: StoredInstance seq)
    (request: CanvasMessageRequest)
    =
    async {
        if not (CanvasFilename.isValid request.Filename) then
            return Routed(CanvasMessageResult.Error "Invalid canvas filename")
        else
            let worktreePath = WorktreePath.value request.WorktreePath |> normalizePath
            let! target = resolveTarget sessionInstances worktreePath request.Filename

            let! sendResult =
                SessionBridge.send queueCancellationToken
                    { WorktreePath = worktreePath
                      Target = SessionBridge.SendTarget.ofSessionId target
                      Prompt = SessionBridge.Prompt.canvasFor worktreePath request.Filename request.Payload }

            let result =
                match sendResult with
                | SessionBridge.SendResult.Delivered -> CanvasMessageResult.Ok
                | SessionBridge.SendResult.Queued -> CanvasMessageResult.Queued

            return
                match sendResult, CanvasDocKinds.classify request.Filename with
                | SessionBridge.SendResult.Queued, SystemView -> QueuedNeedingSession(target, result)
                | _ -> Routed result
    }

let internal awaitSystemViewFallbackUsing (delay: int -> Async<unit>) worktreePath (recipient: SessionId option) =
    async {
        if recipient.IsSome then do! delay AutoSync.registrationGraceMilliseconds
        return! SessionBridge.prepareSystemViewFallback worktreePath recipient
    }

let internal coordinateSystemViewFallbackUsing delay worktreePath recipient operation =
    async {
        let key = normalizePath worktreePath
        let! started = launchAgent.PostAndAsyncReply(fun reply -> BeginFallbackCoordination(key, reply))
        if not started then return CanvasMessageResult.Queued
        else
            let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let rec coordinate current firstResult =
                async {
                    let! needsLaunch, handled = awaitSystemViewFallbackUsing delay key current
                    let! outcome =
                        if needsLaunch then operation ()
                        else async.Return CanvasMessageResult.Queued
                    let result =
                        match firstResult, outcome with
                        | None, _
                        | _, CanvasMessageResult.Error _ -> outcome
                        | Some previous, _ -> previous
                    let! next =
                        launchAgent.PostAndAsyncReply(fun reply -> EndFallbackCoordination(key, handled, finished, reply))
                    match next with
                    | Some pending -> return! coordinate pending (Some result)
                    | None -> return result
                }
            try
                return! coordinate recipient None
            finally
                if not finished.Task.IsCompleted then
                    launchAgent.Post(ReleaseFallbackCoordination(key, finished))
    }
