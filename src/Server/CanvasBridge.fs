module Server.CanvasBridge

open System
open System.Threading.Tasks
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

let private normalizePath = Server.PathUtils.normalizePath

type internal PendingLaunchResult =
    | PendingLaunchStarted
    | PendingLaunchJoined

type private LaunchMsg =
    | BeginPendingLaunch of worktreeKey: string * now: DateTime * AsyncReplyChannel<PendingLaunchResult>
    | CancelPendingLaunch of worktreeKey: string * AsyncReplyChannel<unit>

/// How long a started launch suppresses another one for the same worktree. A spawn that never
/// registers must not block future interactions, so the entry expires rather than waiting to be
/// cleared by a registration — correlating registrations back to a launch is exactly the
/// bookkeeping this design set out to remove.
let private launchSuppressionWindow = TimeSpan.FromSeconds 30.0

/// Suppresses a second session spawn for the same worktree while one is starting up, so repeated
/// interactions arriving before the new session registers do not each spawn an agent. It bounds
/// only Treemon's own spawns; sessions the user starts concurrently are not arbitrated.
let private launchAgent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (startedAt: Map<string, DateTime>) =
            async {
                match! inbox.Receive() with
                | BeginPendingLaunch(worktreeKey, now, reply) ->
                    let recentlyStarted =
                        startedAt
                        |> Map.tryFind worktreeKey
                        |> Option.exists (fun started -> now - started < launchSuppressionWindow)

                    if recentlyStarted then
                        reply.Reply(PendingLaunchJoined)
                        return! loop startedAt
                    else
                        reply.Reply(PendingLaunchStarted)
                        return! loop (startedAt |> Map.add worktreeKey now)

                | CancelPendingLaunch(worktreeKey, reply) ->
                    reply.Reply()
                    return! loop (startedAt |> Map.remove worktreeKey)
            }

        loop Map.empty)

/// `PendingLaunchStarted` when the caller should spawn a session, `PendingLaunchJoined` when one is
/// already starting for this worktree.
let internal beginPendingLaunch worktreePath =
    launchAgent.PostAndAsyncReply(fun reply ->
        BeginPendingLaunch(normalizePath worktreePath, DateTime.UtcNow, reply))

/// Release the suppression after a spawn fails, so the next interaction can try again immediately.
let internal cancelPendingLaunch worktreePath =
    launchAgent.PostAndAsyncReply(fun reply -> CancelPendingLaunch(normalizePath worktreePath, reply))

/// Which session receives an interaction from a canvas document.
///
/// An AgentDoc has a real author, so it keeps its persisted owner. A SystemView is server-generated
/// and has no author: it resolves, per interaction, to the most recently active session that
/// currently holds a live bridge registration. Nothing is stored for a SystemView, so there is no
/// routing target that can go stale, be raced, or need pruning.
///
/// Liveness and activity are deliberately separate inputs, and they arrive from two independent
/// extensions: the bridge registry says which sessions can receive a prompt at all, while the
/// activity snapshot only *orders* them. A reachable session that has not reported activity is
/// therefore still a valid target — falling back to the freshest registration keeps Treemon from
/// spawning a second session next to a perfectly usable one.
/// Session ids scoped to `worktreePath` already make the activity snapshot worktree-correct — a
/// status row for another worktree carries a session id this worktree never registered — so the
/// snapshot is consumed unfiltered.
let internal resolveTarget
    (sessionStatuses: StoredStatus seq)
    (worktreePath: string)
    (filename: string)
    =
    async {
        match CanvasDocKinds.classify filename with
        | AgentDoc -> return! CanvasDocOwnership.getOwner worktreePath filename
        | SystemView ->
            let now = DateTime.UtcNow

            let liveSessions =
                SessionBridge.sessionsForWorktree worktreePath
                |> List.filter (SessionBridge.isSessionAlive now)

            let liveSessionIds = liveSessions |> List.choose _.SessionId |> Set.ofList

            let mostRecentlyActive =
                sessionStatuses
                |> Seq.filter (fun stored ->
                    liveSessionIds |> Set.contains (SessionId.value stored.SessionId))
                |> List.ofSeq
                |> StoredStatus.tryMostRecentActivity
                |> Option.map (_.SessionId >> SessionId.value)

            let freshestReachable () =
                liveSessions
                |> List.filter (fun entry -> entry.SessionId |> Option.isSome)
                |> List.sortByDescending _.RegisteredAt
                |> List.tryPick _.SessionId

            return mostRecentlyActive |> Option.orElseWith freshestReachable
    }

/// What routing decided, in the caller's terms. `QueuedNeedingSession` means nothing could receive
/// the interaction *and* the document kind allows starting one — the caller owns session lifecycle,
/// so it decides whether to spawn, without re-deriving the document kind.
type internal CanvasSendOutcome =
    | Routed of CanvasMessageResult
    | QueuedNeedingSession of CanvasMessageResult * CanvasDocKind

/// Route one canvas interaction.
let internal sendMessage (sessionStatuses: StoredStatus seq) (request: CanvasMessageRequest) =
    async {
        let worktreePath = WorktreePath.value request.WorktreePath
        let! target = resolveTarget sessionStatuses worktreePath request.Filename

        let! sendResult =
            SessionBridge.send
                { WorktreePath = worktreePath
                  SessionId = target
                  Prompt = SessionBridge.Prompt.canvasFor request.Filename request.Payload }

        let result =
            match sendResult with
            | SessionBridge.SendResult.Delivered -> CanvasMessageResult.Ok
            | SessionBridge.SendResult.Queued -> CanvasMessageResult.Queued

        // Only a SystemView may be served by a freshly started session; an AgentDoc waits for the
        // author that owns it, so a new session would be the wrong recipient. The kind travels with
        // the outcome so the caller can build the launch prompt without re-deriving it.
        return
            match target, CanvasDocKinds.classify request.Filename with
            | None, (SystemView as kind) -> QueuedNeedingSession(result, kind)
            | _ -> Routed result
    }

let registerSession worktreePath injectUrl sessionId =
    let normalizedSessionId =
        match sessionId with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    SessionBridge.registerSession worktreePath injectUrl normalizedSessionId

let drainPending worktreePath =
    SessionBridge.drainPendingCanvas worktreePath
    |> List.map _.Text
