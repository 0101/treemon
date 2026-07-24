module Server.CanvasBridge

open System
open System.Threading.Tasks
open Shared

let private normalizePath = Server.PathUtils.normalizePath

type internal PendingLaunchResult =
    | PendingLaunchStarted
    | PendingLaunchJoined

type internal PendingLaunch =
    { Role: PendingLaunchResult
      Completion: Task<Result<unit, string>> }

type internal PendingResumeResult =
    | PendingResumeStarted
    | PendingResumeJoined

type internal PendingResume =
    { Role: PendingResumeResult
      Completion: Task<CanvasMessageResult> }

type internal ActivityTargetAssignment =
    | ActivityTargetAssigned
    | ActivityTargetDeferred

type private PendingLaunchState =
    { Filenames: Set<string>
      Completion: TaskCompletionSource<Result<unit, string>> }

type private PendingResumeState =
    { Outcome: TaskCompletionSource<CanvasMessageResult> }

type private ResumeMsg =
    | BeginPendingResume of
        worktreeKey: string *
        sessionId: string *
        AsyncReplyChannel<PendingResume>
    | CompletePendingResume of
        worktreeKey: string *
        sessionId: string *
        result: CanvasMessageResult *
        AsyncReplyChannel<unit>

type private RoutingMsg =
    | BeginPendingLaunch of worktreeKey: string * filename: string * AsyncReplyChannel<PendingLaunch>
    | CancelPendingLaunch of worktreeKey: string * reason: string * AsyncReplyChannel<unit>
    | RegisterSession of
        worktreeKey: string *
        injectUrl: string *
        sessionId: string option *
        AsyncReplyChannel<Set<string>>
    | AssignActivityTarget of
        worktreeKey: string *
        filename: string *
        sessionId: string *
        AsyncReplyChannel<ActivityTargetAssignment>

let private routingAgent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop pending =
            async {
                match! inbox.Receive() with
                | BeginPendingLaunch(worktreeKey, filename, reply) ->
                    match pending |> Map.tryFind worktreeKey with
                    | Some existing ->
                        reply.Reply(
                            { Role = PendingLaunchJoined
                              Completion = existing.Completion.Task })

                        return!
                            loop (
                                pending
                                |> Map.add worktreeKey
                                    { existing with
                                        Filenames = existing.Filenames |> Set.add filename }
                            )
                    | None ->
                        let completion =
                            TaskCompletionSource<Result<unit, string>>(
                                TaskCreationOptions.RunContinuationsAsynchronously)

                        reply.Reply(
                            { Role = PendingLaunchStarted
                              Completion = completion.Task })

                        return!
                            loop (
                                pending
                                |> Map.add worktreeKey
                                    { Filenames = Set.singleton filename
                                      Completion = completion }
                            )

                | CancelPendingLaunch(worktreeKey, reason, reply) ->
                    pending
                    |> Map.tryFind worktreeKey
                    |> Option.iter (fun launch ->
                        launch.Completion.TrySetResult(Error reason) |> ignore)

                    reply.Reply()
                    return! loop (pending |> Map.remove worktreeKey)

                | RegisterSession(worktreeKey, injectUrl, sessionId, reply) ->
                    match sessionId, pending |> Map.tryFind worktreeKey with
                    | Some sessionId, Some launch ->
                        let! assignment =
                            launch.Filenames
                            |> Set.toList
                            |> List.map (fun filename ->
                                CanvasDocOwnership.assign worktreeKey filename sessionId)
                            |> Async.Sequential
                            |> Async.Ignore
                            |> Async.Catch

                        SessionBridge.registerSession worktreeKey injectUrl (Some sessionId)

                        match assignment with
                        | Choice1Of2 () ->
                            launch.Completion.TrySetResult(Ok ()) |> ignore
                            reply.Reply(launch.Filenames)
                        | Choice2Of2 ex ->
                            launch.Completion.TrySetResult(Error ex.Message) |> ignore
                            reply.Reply(Set.empty)
                            Log.log
                                "CanvasBridge"
                                $"Could not assign pending canvas targets for {worktreeKey}: {ex.Message}"

                        return! loop (pending |> Map.remove worktreeKey)
                    | _ ->
                        SessionBridge.registerSession worktreeKey injectUrl sessionId
                        reply.Reply(Set.empty)
                        return! loop pending

                | AssignActivityTarget(worktreeKey, filename, sessionId, reply) ->
                    match pending |> Map.tryFind worktreeKey with
                    | Some launch when launch.Filenames |> Set.contains filename ->
                        reply.Reply(ActivityTargetDeferred)
                        return! loop pending
                    | _ ->
                        do! CanvasDocOwnership.assign worktreeKey filename sessionId
                        reply.Reply(ActivityTargetAssigned)
                        return! loop pending
            }

        loop Map.empty)

let private resumeAgent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop pending =
            async {
                match! inbox.Receive() with
                | BeginPendingResume(worktreeKey, sessionId, reply) ->
                    let key = worktreeKey, sessionId

                    match pending |> Map.tryFind key with
                    | Some existing ->
                        reply.Reply(
                            { Role = PendingResumeJoined
                              Completion = existing.Outcome.Task })

                        return! loop pending
                    | None ->
                        let completion =
                            TaskCompletionSource<CanvasMessageResult>(
                                TaskCreationOptions.RunContinuationsAsynchronously)

                        reply.Reply(
                            { Role = PendingResumeStarted
                              Completion = completion.Task })

                        return!
                            loop (
                                pending
                                |> Map.add key { Outcome = completion }
                            )

                | CompletePendingResume(worktreeKey, sessionId, result, reply) ->
                    let key = worktreeKey, sessionId

                    pending
                    |> Map.tryFind key
                    |> Option.iter (fun resume ->
                        resume.Outcome.TrySetResult(result) |> ignore)

                    reply.Reply()
                    return! loop (pending |> Map.remove key)
            }

        loop Map.empty)

let internal beginPendingLaunch worktreePath filename =
    routingAgent.PostAndAsyncReply(fun reply ->
        BeginPendingLaunch(
            normalizePath worktreePath,
            filename,
            reply))

let internal cancelPendingLaunch worktreePath reason =
    routingAgent.PostAndAsyncReply(fun reply ->
        CancelPendingLaunch(normalizePath worktreePath, reason, reply))

let internal beginPendingResume worktreePath sessionId =
    resumeAgent.PostAndAsyncReply(fun reply ->
        BeginPendingResume(
            normalizePath worktreePath,
            sessionId,
            reply))

let internal completePendingResume worktreePath sessionId result =
    resumeAgent.PostAndAsyncReply(fun reply ->
        CompletePendingResume(
            normalizePath worktreePath,
            sessionId,
            result,
            reply))

let internal assignActivityTarget worktreePath filename sessionId =
    routingAgent.PostAndAsyncReply(fun reply ->
        AssignActivityTarget(
            normalizePath worktreePath,
            filename,
            sessionId,
            reply))

let private registerCoordinatedSession worktreePath injectUrl sessionId =
    let worktreeKey = normalizePath worktreePath

    let filenames =
        routingAgent.PostAndReply(fun reply ->
            RegisterSession(
                worktreeKey,
                injectUrl,
                sessionId,
                reply))

    match sessionId with
    | Some sessionId when not (Set.isEmpty filenames) ->
        Log.log
            "CanvasBridge"
            $"Session {sessionId} assigned {Set.count filenames} pending canvas target(s) for {worktreeKey}"
    | _ -> ()

let private pollUntil (timeout: TimeSpan) condition =
    let deadline = DateTime.UtcNow + timeout

    let rec wait () =
        async {
            if condition () then
                return true
            elif DateTime.UtcNow >= deadline then
                return false
            else
                do! Async.Sleep 50
                return! wait ()
        }

    wait ()

let internal waitForPendingLaunchCompletion
    (timeout: TimeSpan)
    (pendingLaunch: PendingLaunch)
    =
    async {
        let! completed =
            pollUntil timeout (fun () ->
                pendingLaunch.Completion.IsCompleted)

        if completed then
            return! pendingLaunch.Completion |> Async.AwaitTask
        else
            return
                Error
                    "the session did not register with Treemon before the timeout"
    }

let registerSession worktreePath injectUrl sessionId =
    let normalizedSessionId =
        match sessionId with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    registerCoordinatedSession worktreePath injectUrl normalizedSessionId

let internal registrationStamp worktreePath sessionId =
    SessionBridge.registrationStamp worktreePath sessionId

let internal waitForRegistrationAfter timeout worktreePath sessionId previous =
    pollUntil timeout (fun () ->
        registrationStamp worktreePath sessionId
        |> Option.exists (fun registeredAt ->
            previous
            |> Option.forall (fun previousAt ->
                registeredAt > previousAt)))

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

type internal MessageSendResult =
    | MessageDelivered
    | MessageQueued of failedRegistration: SessionBridge.SessionEntry option

let internal sendMessageForRecovery (request: CanvasMessageRequest) =
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
            | SessionBridge.SendResult.Delivered -> MessageDelivered
            | SessionBridge.SendResult.Queued -> MessageQueued None
            | SessionBridge.SendResult.TransportFailed registration ->
                MessageQueued(Some registration)
    }

let internal invalidateFailedRegistration registration =
    SessionBridge.invalidateRegistration registration

let sendMessage (request: CanvasMessageRequest) =
    async {
        match! sendMessageForRecovery request with
        | MessageDelivered -> return CanvasMessageResult.Ok
        | MessageQueued _ -> return CanvasMessageResult.Queued
    }

let drainPending worktreePath =
    SessionBridge.drainPendingCanvas worktreePath
    |> List.map _.Text
