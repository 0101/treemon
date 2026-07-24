module Server.CanvasBridge

open Shared

let sendMessage (request: CanvasMessageRequest) =
    async {
        let worktreePath = WorktreePath.value request.WorktreePath
        let! owner = CanvasDocOwnership.getOwner worktreePath request.Filename

        let! result =
            SessionBridge.send
                { WorktreePath = worktreePath
                  SessionId = owner
                  Prompt = SessionBridge.Prompt.canvas request.Payload }

        return
            match result with
            | SessionBridge.SendResult.Delivered -> CanvasMessageResult.Ok
            | SessionBridge.SendResult.Queued -> CanvasMessageResult.Queued
    }

let drainPending worktreePath =
    SessionBridge.drainPendingCanvas worktreePath
    |> List.map _.Text
