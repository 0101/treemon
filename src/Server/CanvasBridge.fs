module Server.CanvasBridge

open System
open Shared

let private normalizePath = Server.PathUtils.normalizePath
let private normalizeFilename (filename: string) = filename.ToLowerInvariant()

type internal PendingLaunchResult =
    | PendingLaunchStarted
    | PendingLaunchJoined

type private PendingLaunchMsg =
    | BeginPendingLaunch of worktreeKey: string * filename: string * AsyncReplyChannel<PendingLaunchResult>
    | CancelPendingLaunch of worktreeKey: string * AsyncReplyChannel<unit>
    | AssignPendingLaunch of worktreeKey: string * sessionId: string * AsyncReplyChannel<Set<string>>
    | HasPendingLaunch of worktreeKey: string * AsyncReplyChannel<bool>

let private pendingLaunchAgent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop pending =
            async {
                match! inbox.Receive() with
                | BeginPendingLaunch(worktreeKey, filename, reply) ->
                    let existing =
                        pending
                        |> Map.tryFind worktreeKey
                        |> Option.defaultValue Set.empty

                    reply.Reply(
                        if Set.isEmpty existing then PendingLaunchStarted
                        else PendingLaunchJoined)

                    return! loop (pending |> Map.add worktreeKey (existing |> Set.add filename))

                | CancelPendingLaunch(worktreeKey, reply) ->
                    reply.Reply()
                    return! loop (pending |> Map.remove worktreeKey)

                | AssignPendingLaunch(worktreeKey, sessionId, reply) ->
                    let filenames =
                        pending
                        |> Map.tryFind worktreeKey
                        |> Option.defaultValue Set.empty

                    do!
                        filenames
                        |> Set.toList
                        |> List.map (fun filename ->
                            CanvasDocOwnership.assign worktreeKey filename sessionId)
                        |> Async.Sequential
                        |> Async.Ignore

                    reply.Reply(filenames)
                    return! loop (pending |> Map.remove worktreeKey)

                | HasPendingLaunch(worktreeKey, reply) ->
                    reply.Reply(pending |> Map.containsKey worktreeKey)
                    return! loop pending
            }

        loop Map.empty)

let internal beginPendingLaunch worktreePath filename =
    pendingLaunchAgent.PostAndAsyncReply(fun reply ->
        BeginPendingLaunch(
            normalizePath worktreePath,
            normalizeFilename filename,
            reply))

let internal cancelPendingLaunch worktreePath =
    pendingLaunchAgent.PostAndAsyncReply(fun reply ->
        CancelPendingLaunch(normalizePath worktreePath, reply))

let private assignPendingLaunch worktreePath sessionId =
    let worktreeKey = normalizePath worktreePath

    let filenames =
        pendingLaunchAgent.PostAndReply(fun reply ->
            AssignPendingLaunch(worktreeKey, sessionId, reply))

    if not (Set.isEmpty filenames) then
        Log.log
            "CanvasBridge"
            $"Session {sessionId} assigned {Set.count filenames} pending canvas target(s) for {worktreeKey}"

let private hasPendingLaunch worktreePath =
    pendingLaunchAgent.PostAndReply(fun reply ->
        HasPendingLaunch(normalizePath worktreePath, reply))

let internal waitForPendingLaunchCompletion (timeout: TimeSpan) worktreePath =
    let deadline = DateTime.UtcNow + timeout

    let rec wait () =
        async {
            if not (hasPendingLaunch worktreePath) then
                return true
            elif DateTime.UtcNow >= deadline then
                return false
            else
                do! Async.Sleep 50
                return! wait ()
        }

    wait ()

let registerSession worktreePath injectUrl sessionId =
    let normalizedSessionId =
        match sessionId with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    normalizedSessionId
    |> Option.iter (assignPendingLaunch worktreePath)

    SessionBridge.registerSession worktreePath injectUrl normalizedSessionId

let internal registrationStamp worktreePath sessionId =
    SessionBridge.registrationStamp worktreePath sessionId

let internal waitForRegistrationAfter timeout worktreePath sessionId previous =
    SessionBridge.waitForRegistrationAfter timeout worktreePath sessionId previous

type internal TargetState =
    | NoTarget
    | LiveTarget of sessionId: string
    | OfflineTarget of sessionId: string

let internal getTargetState worktreePath filename =
    async {
        let! owner = CanvasDocOwnership.getOwner worktreePath filename
        let now = DateTime.UtcNow

        return
            match owner with
            | None -> NoTarget
            | Some ownerId ->
                SessionBridge.sessionsForWorktree worktreePath
                |> List.exists (fun entry ->
                    entry.SessionId = Some ownerId
                    && SessionBridge.isSessionAlive now entry)
                |> function
                    | true -> LiveTarget ownerId
                    | false -> OfflineTarget ownerId
    }

let sendMessage (request: CanvasMessageRequest) =
    async {
        let worktreePath = WorktreePath.value request.WorktreePath
        let! owner = CanvasDocOwnership.getOwner worktreePath request.Filename

        let! result =
            SessionBridge.send
                { WorktreePath = worktreePath
                  SessionId = owner
                  Prompt =
                    SessionBridge.Prompt.canvasFor
                        request.Filename
                        request.Payload }

        return
            match result with
            | SessionBridge.SendResult.Delivered -> CanvasMessageResult.Ok
            | SessionBridge.SendResult.Queued -> CanvasMessageResult.Queued
    }

let drainPending worktreePath =
    SessionBridge.drainPendingCanvas worktreePath
    |> List.map _.Text
