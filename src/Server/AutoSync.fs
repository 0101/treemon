module Server.AutoSync

open System
open Shared

type DeliveryRequest =
    { WorktreePath: WorktreePath
      SessionId: string option
      Prompt: string }

let prompt upstreamRemote baseBranch =
    $"Sync this worktree with {upstreamRemote}/{baseBranch} when safe. Preserve any in-progress work, resolve conflicts carefully, and run the appropriate checks before considering the sync complete."

let revision enabled (gitData: GitWorktree.GitData) =
    if enabled && gitData.MainBehindCount > 0 then gitData.BaseRevision else None

let selectSessionId
    (activityStore: SessionActivityStore.SessionActivityStore option)
    (liveSessions: SessionActivityStore.StoredStatus seq)
    (path: string)
    =
    let retained =
        activityStore
        |> Option.map _.RetainedByWorktree()
        |> Option.defaultValue Map.empty

    liveSessions
    |> CodingToolStatus.includeRetainedSessions retained
    |> Seq.filter (fun stored -> WorktreePath.value stored.WorktreePath = path)
    |> Seq.toList
    |> CodingToolStatus.selectFooterSessionId DateTimeOffset.UtcNow

let deliver
    (tryDeliver: SessionBridge.SendRequest -> Async<bool>)
    (tryBeginLaunch: string -> Async<bool>)
    (completeLaunch: string -> unit)
    (launch: WorktreePath -> string -> Async<Result<unit, string>>)
    (request: DeliveryRequest)
    =
    async {
        let path = WorktreePath.value request.WorktreePath

        let! delivered =
            match request.SessionId with
            | Some _ ->
                tryDeliver
                    { WorktreePath = path
                      SessionId = request.SessionId
                      Prompt = SessionBridge.Prompt.agentPrompt request.Prompt }
            | None -> async { return false }

        if delivered then
            return true
        else
            let! canLaunch = tryBeginLaunch path

            if not canLaunch then
                return false
            else
                try
                    try
                        let! result = launch request.WorktreePath request.Prompt
                        return Result.isOk result
                    with ex ->
                        Log.log "AutoSync" $"Fallback launch failed for {path}: {ex.Message}"
                        return false
                finally
                    completeLaunch path
    }
