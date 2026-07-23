module Server.AutoSync

open System
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

type DeliveryRequest =
    { WorktreePath: WorktreePath
      SessionId: string option
      Prompt: string }

type TriggerDependencies =
    { ClaimRevision: string -> string -> Async<bool>
      ReleaseRevision: string -> string -> unit
      SelectSessionId: string -> Async<string option>
      Deliver: DeliveryRequest -> Async<bool> }

let prompt upstreamRemote baseBranch =
    $"Sync this worktree with {upstreamRemote}/{baseBranch} when safe. Preserve any in-progress work, resolve conflicts carefully, and run the appropriate checks before considering the sync complete."

let revision enabled (gitData: GitWorktree.GitData) =
    if enabled && gitData.MainBehindCount > 0 then gitData.BaseRevision else None

let internal selectTargetSessionId (now: DateTimeOffset) (sessions: StoredStatus list) =
    let openSessions =
        sessions |> List.filter (fun session -> now - session.LastSeen < openWindow)

    openSessions
    |> pickActive _.Status StoredStatus.activityOrderKey
    |> Option.orElseWith (fun () -> openSessions |> StoredStatus.tryMostRecentActivity)
    |> Option.orElseWith (fun () -> sessions |> StoredStatus.tryMostRecentActivity)
    |> Option.map (_.SessionId >> SessionId.value)

let selectSessionId
    (activityStore: SessionActivityStore.SessionActivityStore option)
    (liveSessions: StoredStatus seq)
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
    |> selectTargetSessionId DateTimeOffset.UtcNow

let internal registrationGraceMilliseconds = 3000

let deliver
    (tryDeliver: SessionBridge.SendRequest -> Async<SessionBridge.DeliveryResult>)
    (waitForRegistration: unit -> Async<unit>)
    (tryBeginLaunch: string -> Async<bool>)
    (completeLaunch: string -> unit)
    (launch: WorktreePath -> string -> Async<Result<unit, string>>)
    (request: DeliveryRequest)
    =
    async {
        let path = WorktreePath.value request.WorktreePath

        let sendRequest: SessionBridge.SendRequest =
            { WorktreePath = path
              SessionId = request.SessionId
              Prompt = SessionBridge.Prompt.agentPrompt request.Prompt }

        let tryDelivery () =
            match request.SessionId with
            | Some _ -> tryDeliver sendRequest
            | None -> async { return SessionBridge.DeliveryResult.NoLiveSession }

        let launchFallback () =
            async {
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

        match! tryDelivery () with
        | SessionBridge.DeliveryResult.Delivered
        | SessionBridge.DeliveryResult.DeliveryFailed ->
            return true
        | SessionBridge.DeliveryResult.NoLiveSession when Option.isSome request.SessionId ->
            do! waitForRegistration ()

            match! tryDelivery () with
            | SessionBridge.DeliveryResult.Delivered
            | SessionBridge.DeliveryResult.DeliveryFailed ->
                return true
            | SessionBridge.DeliveryResult.NoLiveSession ->
                return! launchFallback ()
        | SessionBridge.DeliveryResult.NoLiveSession ->
            return! launchFallback ()
    }

let trigger
    (dependencies: TriggerDependencies)
    (repoRoot: string)
    (upstreamRemote: string)
    (baseBranch: string)
    (gitData: GitWorktree.GitData)
    =
    async {
        let enabled =
            TreemonConfig.readAutoSyncBranchSet (Some repoRoot)
            |> Set.contains gitData.Branch

        match revision enabled gitData with
        | None -> ()
        | Some baseRevision ->
            let! claimed = dependencies.ClaimRevision gitData.Path baseRevision

            if claimed then
                try
                    let! sessionId = dependencies.SelectSessionId gitData.Path

                    let! accepted =
                        dependencies.Deliver
                            { WorktreePath = WorktreePath gitData.Path
                              SessionId = sessionId
                              Prompt = prompt upstreamRemote baseBranch }

                    if accepted then
                        Log.log "AutoSync" $"Prompt accepted for {gitData.Branch} at base revision {baseRevision}"
                    else
                        dependencies.ReleaseRevision gitData.Path baseRevision
                with ex ->
                    Log.log "AutoSync" $"Trigger failed for {gitData.Branch}: {ex.Message}"
                    dependencies.ReleaseRevision gitData.Path baseRevision
    }

let internal startGuarded onError workflow =
    async {
        try
            do! workflow
        with ex ->
            onError ex
    }
    |> Async.Start

let triggerInBackground dependencies repoRoot upstreamRemote baseBranch (gitData: GitWorktree.GitData) =
    startGuarded
        (fun ex -> Log.log "AutoSync" $"Background trigger failed for {gitData.Branch}: {ex.Message}")
        (trigger dependencies repoRoot upstreamRemote baseBranch gitData)
