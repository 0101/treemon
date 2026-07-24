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

type internal PendingLaunch =
    { Role: PendingLaunchResult
      Completion: Task<Result<unit, string>> }

type private LaunchMsg =
    | BeginPendingLaunch of worktreeKey: string * AsyncReplyChannel<PendingLaunch>
    | CancelPendingLaunch of worktreeKey: string * reason: string * AsyncReplyChannel<unit>
    | CompletePendingLaunch of worktreeKey: string * AsyncReplyChannel<unit>

/// Tracks at most one in-flight session launch per worktree so that repeated interactions arriving
/// before the launched session registers join that launch instead of spawning another session.
/// This is only about *our own* spawns; sessions the user starts concurrently are not arbitrated.
let private launchAgent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (pending: Map<string, TaskCompletionSource<Result<unit, string>>>) =
            async {
                match! inbox.Receive() with
                | BeginPendingLaunch(worktreeKey, reply) ->
                    match pending |> Map.tryFind worktreeKey with
                    | Some existing ->
                        reply.Reply(
                            { Role = PendingLaunchJoined
                              Completion = existing.Task })

                        return! loop pending
                    | None ->
                        let completion =
                            TaskCompletionSource<Result<unit, string>>(
                                TaskCreationOptions.RunContinuationsAsynchronously)

                        reply.Reply(
                            { Role = PendingLaunchStarted
                              Completion = completion.Task })

                        return! loop (pending |> Map.add worktreeKey completion)

                | CancelPendingLaunch(worktreeKey, reason, reply) ->
                    pending
                    |> Map.tryFind worktreeKey
                    |> Option.iter (fun completion -> completion.TrySetResult(Error reason) |> ignore)

                    reply.Reply()
                    return! loop (pending |> Map.remove worktreeKey)

                | CompletePendingLaunch(worktreeKey, reply) ->
                    pending
                    |> Map.tryFind worktreeKey
                    |> Option.iter (fun completion -> completion.TrySetResult(Ok()) |> ignore)

                    reply.Reply()
                    return! loop (pending |> Map.remove worktreeKey)
            }

        loop Map.empty)

let internal beginPendingLaunch worktreePath =
    launchAgent.PostAndAsyncReply(fun reply -> BeginPendingLaunch(normalizePath worktreePath, reply))

let internal cancelPendingLaunch worktreePath reason =
    launchAgent.PostAndAsyncReply(fun reply ->
        CancelPendingLaunch(normalizePath worktreePath, reason, reply))

/// Which session receives an interaction from a canvas document.
///
/// An AgentDoc has a real author, so it keeps its persisted owner. A SystemView is server-generated
/// and has no author: it resolves, per interaction, to the most recently active session that
/// currently holds a live bridge registration. Nothing is stored for a SystemView, so there is no
/// routing target that can go stale, be raced, or need pruning.
///
/// Liveness and activity are deliberately separate inputs: bridge registration decides whether a
/// session can receive the prompt at all, while `UpdatedAt` decides which of the reachable sessions
/// is the most recent (see the `LastSeen` note in `SessionActivityStore`).
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

            let liveSessionIds =
                SessionBridge.sessionsForWorktree worktreePath
                |> List.filter (SessionBridge.isSessionAlive now)
                |> List.choose _.SessionId
                |> Set.ofList

            return
                sessionStatuses
                |> Seq.filter (fun stored ->
                    liveSessionIds |> Set.contains (SessionId.value stored.SessionId))
                |> List.ofSeq
                |> StoredStatus.tryMostRecentActivity
                |> Option.map (_.SessionId >> SessionId.value)
    }

/// Sessions for `worktreePath` from a scheduler snapshot, in the shape `resolveTarget` expects.
let private sessionStatusesFor (worktreePath: string) (statuses: StoredStatus seq) =
    let worktreeKey = normalizePath worktreePath

    statuses
    |> Seq.filter (fun stored ->
        String.Equals(
            normalizePath (WorktreePath.value stored.WorktreePath),
            worktreeKey,
            StringComparison.OrdinalIgnoreCase))

/// Route one canvas interaction. Returns the resolved target alongside the outcome so the caller can
/// tell "queued because nothing is reachable" (target `None`) from "queued behind a known session".
let internal sendMessage (sessionStatuses: StoredStatus seq) (request: CanvasMessageRequest) =
    async {
        let worktreePath = WorktreePath.value request.WorktreePath

        let! target =
            sessionStatuses
            |> sessionStatusesFor worktreePath
            |> resolveTarget
            <| worktreePath
            <| request.Filename

        let! result =
            SessionBridge.send
                { WorktreePath = worktreePath
                  SessionId = target
                  Prompt = SessionBridge.Prompt.canvasFor request.Filename request.Payload }

        return
            target,
            match result with
            | SessionBridge.SendResult.Delivered -> CanvasMessageResult.Ok
            | SessionBridge.SendResult.Queued -> CanvasMessageResult.Queued
    }

let registerSession worktreePath injectUrl sessionId =
    let normalizedSessionId =
        match sessionId with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    SessionBridge.registerSession worktreePath injectUrl normalizedSessionId

    // An identified registration satisfies whatever launch was waiting on it, so queued
    // interactions drain to it.
    normalizedSessionId
    |> Option.iter (fun _ ->
        launchAgent.PostAndReply(fun reply ->
            CompletePendingLaunch(normalizePath worktreePath, reply)))

let drainPending worktreePath =
    SessionBridge.drainPendingCanvas worktreePath
    |> List.map _.Text
