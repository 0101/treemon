module Server.CanvasBridge

open System
open System.Threading.Tasks
open Shared

let private normalizePath = Server.PathUtils.normalizePath
let private normalizeFilename (filename: string) = filename.ToLowerInvariant()

type internal PendingLaunchResult =
    | PendingLaunchStarted
    | PendingLaunchJoined

type internal PendingLaunch =
    { Role: PendingLaunchResult
      Completion: Async<Result<unit, string>> }

type private PendingLaunchState =
    { Filenames: Set<string>
      Completion: TaskCompletionSource<Result<unit, string>> }

type private PendingLaunchMsg =
    | BeginPendingLaunch of worktreeKey: string * filename: string * AsyncReplyChannel<PendingLaunch>
    | CancelPendingLaunch of worktreeKey: string * reason: string * AsyncReplyChannel<unit>
    | AssignPendingLaunch of worktreeKey: string * sessionId: string * AsyncReplyChannel<Set<string>>

let private pendingLaunchAgent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop pending =
            async {
                match! inbox.Receive() with
                | BeginPendingLaunch(worktreeKey, filename, reply) ->
                    match pending |> Map.tryFind worktreeKey with
                    | Some existing ->
                        reply.Reply(
                            { Role = PendingLaunchJoined
                              Completion = existing.Completion.Task |> Async.AwaitTask })

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
                              Completion = completion.Task |> Async.AwaitTask })

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

                | AssignPendingLaunch(worktreeKey, sessionId, reply) ->
                    match pending |> Map.tryFind worktreeKey with
                    | None ->
                        reply.Reply(Set.empty)
                        return! loop pending
                    | Some launch ->
                        let! assignment =
                            launch.Filenames
                            |> Set.toList
                            |> List.map (fun filename ->
                                CanvasDocOwnership.assign worktreeKey filename sessionId)
                            |> Async.Sequential
                            |> Async.Ignore
                            |> Async.Catch

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
            }

        loop Map.empty)

let internal beginPendingLaunch worktreePath filename =
    pendingLaunchAgent.PostAndAsyncReply(fun reply ->
        BeginPendingLaunch(
            normalizePath worktreePath,
            normalizeFilename filename,
            reply))

let internal cancelPendingLaunch worktreePath reason =
    pendingLaunchAgent.PostAndAsyncReply(fun reply ->
        CancelPendingLaunch(normalizePath worktreePath, reason, reply))

let private assignPendingLaunch worktreePath sessionId =
    let worktreeKey = normalizePath worktreePath

    let filenames =
        pendingLaunchAgent.PostAndReply(fun reply ->
            AssignPendingLaunch(worktreeKey, sessionId, reply))

    if not (Set.isEmpty filenames) then
        Log.log
            "CanvasBridge"
            $"Session {sessionId} assigned {Set.count filenames} pending canvas target(s) for {worktreeKey}"

let internal waitForPendingLaunchCompletion
    (timeout: TimeSpan)
    (pendingLaunch: PendingLaunch)
    =
    async {
        try
            let! completion =
                Async.StartChild(
                    pendingLaunch.Completion,
                    int timeout.TotalMilliseconds)

            return! completion
        with :? TimeoutException ->
            return
                Error
                    "the session did not register with Treemon before the timeout"
    }

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
