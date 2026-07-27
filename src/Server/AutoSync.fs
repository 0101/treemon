module Server.AutoSync

open System
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

/// Who — if anyone — is listening in a worktree. Openness is a case, not a flag on an id, because
/// both cases can carry a session id: `NoOpenSession` may still know a retained/offline identity
/// from a closed CLI, and that identity is only a delivery hint, never evidence that a live agent
/// will act on a prompt. `OpenSession` means a CLI was seen inside `SessionActivity.openWindow`,
/// including an idle one — an idle open CLI is still attached and still owns its worktree.
type SyncTarget =
    | OpenSession of sessionId: string
    | NoOpenSession of retainedSessionId: string option

module SyncTarget =
    /// The id a bridge send is addressed to, which both cases can supply. Callers that need to know
    /// whether anyone is listening must match on the case instead.
    let sessionId =
        function
        | OpenSession sessionId -> Some sessionId
        | NoOpenSession retainedSessionId -> retainedSessionId

type DeliveryRequest =
    { WorktreePath: WorktreePath
      Target: SyncTarget
      Prompt: string }

/// `ClaimRevision`/`ReleaseRevision` are the in-process claim; the accepted-revision operations are
/// the durable, restart-safe layer on top of it (`AutoSyncStore`).
type TriggerDependencies =
    { ClaimRevision: string -> string -> Async<bool>
      ReleaseRevision: string -> string -> unit
      ReadAcceptedRevision: string -> Async<AutoSyncStore.AcceptedSyncRecord option>
      RecordAcceptedRevision: string -> string -> unit
      ClearAcceptedRevision: string -> unit
      /// The worktree's current PR status, keyed by canonical worktree path so the lookup resolves
      /// inside that worktree's own repo and a same-named branch elsewhere can never answer for it.
      /// Read again at action boundaries because `RefreshPr` runs on its own cadence.
      ReadPrStatus: string -> Async<PrStatus>
      SelectTarget: string -> Async<SyncTarget>
      Deliver: DeliveryRequest -> Async<bool> }

/// One merged-and-still-enabled branch. The local branch name is the `.treemon.json` preference
/// key; the canonical worktree path is the trigger and durable-record key.
type MergedAutoSyncTarget =
    { Branch: string
      Path: string }

let prompt upstreamRemote baseBranch =
    $"Sync this worktree with {upstreamRemote}/{baseBranch} when safe. Preserve any in-progress work, resolve conflicts carefully, and run the appropriate checks before considering the sync complete."

/// Merged is terminal: nothing remains to be synced into a branch whose PR is already merged, so a
/// leftover `.treemon.json` entry must not keep it syncing.
let isMergedPr (prStatus: PrStatus) =
    match prStatus with
    | HasPr pr -> pr.IsMerged
    | NoPr -> false

/// The single eligibility rule, shared by the first observation and every pre-action re-check.
let isEligible enabled prStatus = enabled && not (isMergedPr prStatus)

let internal readEnabledPreference repoRoot branch =
    TreemonConfig.readAutoSyncBranchSet (Some repoRoot) |> Set.contains branch

/// Branches whose reconciled PR is merged while auto-sync is still enabled for them. PRs are matched
/// by provider branch and preferences are keyed by local branch, both read from one repo's own Git
/// data, so a merged branch can never disable a same-named branch in another repo.
let mergedAutoSyncTargets
    (effectivePrMap: Map<string, PrStatus>)
    (enabledBranches: Set<string>)
    (gitData: Map<string, GitWorktree.GitData>)
    =
    gitData
    |> Map.values
    |> Seq.filter (fun data ->
        Set.contains data.Branch enabledBranches
        && isMergedPr (PrStatus.lookupPrStatus effectivePrMap (GitWorktree.prBranchName data)))
    |> Seq.map (fun data -> { Branch = data.Branch; Path = data.Path })
    |> Seq.toList

let revision eligible (gitData: GitWorktree.GitData) =
    if eligible && gitData.MainBehindCount > 0 then gitData.BaseRevision else None

/// How long an accepted prompt suppresses the same base revision. The confirmed incident's sync ran
/// for 23 minutes, so anything shorter re-prompts a session that is still working; the bound exists
/// at all so a prompt that was accepted but never acted on is eventually retried.
let acceptedRetryAge = TimeSpan.FromHours 1.0

/// A durable record suppresses only the revision it was written for, and only inside the retry
/// window — a different base revision is the legitimate case where the base advanced again.
let internal isAlreadyAccepted
    (now: DateTimeOffset)
    baseRevision
    (record: AutoSyncStore.AcceptedSyncRecord option)
    =
    match record with
    | Some record -> record.BaseRevision = baseRevision && now - record.AcceptedAt < acceptedRetryAge
    | None -> false

/// The open winner decides the target; retained identity is consulted only once no session is open,
/// so an open idle CLI can never be mistaken for an offline one that merely left an id behind.
let internal selectTargetFromSessions (now: DateTimeOffset) (sessions: StoredStatus list) =
    let openSessions =
        sessions |> List.filter (fun session -> now - session.LastSeen < openWindow)

    let openWinner =
        openSessions
        |> pickActive _.Status StoredStatus.activityOrderKey
        |> Option.orElseWith (fun () -> openSessions |> StoredStatus.tryMostRecentActivity)

    match openWinner with
    | Some winner -> OpenSession(SessionId.value winner.SessionId)
    | None ->
        sessions
        |> StoredStatus.tryMostRecentActivity
        |> Option.map (_.SessionId >> SessionId.value)
        |> NoOpenSession

let selectTarget
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
    |> selectTargetFromSessions DateTimeOffset.UtcNow

let internal registrationGraceMilliseconds = 3000

/// The gate an operation must re-pass immediately before it acts. `RefreshPr` runs on its own
/// cadence, so it can remove the preference — or learn the PR merged — after the Git observation
/// that started this operation. Cancelling here is safe because nothing has been delivered yet;
/// nothing may cancel once a prompt has been accepted or a Git command is already running.
let isStillEligible (dependencies: TriggerDependencies) (repoRoot: string) (gitData: GitWorktree.GitData) =
    async {
        let! prStatus = dependencies.ReadPrStatus gitData.Path
        return isEligible (readEnabledPreference repoRoot gitData.Branch) prStatus
    }

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
        let sessionId = SyncTarget.sessionId request.Target

        let sendRequest: SessionBridge.SendRequest =
            { WorktreePath = path
              SessionId = sessionId
              Prompt = SessionBridge.Prompt.agentPrompt request.Prompt }

        let launchFallback () =
            async {
                let! canLaunch = tryBeginLaunch path

                if not canLaunch then
                    return false
                else
                    try
                        try
                            let! result = launch request.WorktreePath request.Prompt
                            match result with
                            | Ok () ->
                                do! waitForRegistration ()
                                return true
                            | Error _ ->
                                return false
                        with ex ->
                            Log.log "AutoSync" $"Fallback launch failed for {path}: {ex.Message}"
                            return false
                    finally
                        completeLaunch path
            }

        match! tryDeliver sendRequest with
        | SessionBridge.DeliveryResult.Delivered
        | SessionBridge.DeliveryResult.DeliveryFailed ->
            return true
        | SessionBridge.DeliveryResult.NoLiveSession when Option.isSome sessionId ->
            do! waitForRegistration ()

            match! tryDeliver sendRequest with
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
    (prStatus: PrStatus)
    (gitData: GitWorktree.GitData)
    =
    async {
        let eligible =
            isEligible (readEnabledPreference repoRoot gitData.Branch) prStatus

        match revision eligible gitData with
        | None ->
            // Catching up ends an accepted prompt's life: falling behind the same base revision
            // again is new work, so it must be able to prompt again.
            if gitData.MainBehindCount = 0 then
                dependencies.ClearAcceptedRevision gitData.Path
        | Some baseRevision ->
            let! acceptedRecord = dependencies.ReadAcceptedRevision gitData.Path

            if not (isAlreadyAccepted DateTimeOffset.UtcNow baseRevision acceptedRecord) then
                let! claimed = dependencies.ClaimRevision gitData.Path baseRevision

                if claimed then
                    try
                        let! target = dependencies.SelectTarget gitData.Path
                        let! stillEligible = isStillEligible dependencies repoRoot gitData

                        if not stillEligible then
                            Log.log "AutoSync" $"Dropped sync for {gitData.Branch}: no longer eligible"
                            dependencies.ReleaseRevision gitData.Path baseRevision
                        else
                            let! accepted =
                                dependencies.Deliver
                                    { WorktreePath = WorktreePath gitData.Path
                                      Target = target
                                      Prompt = prompt upstreamRemote baseBranch }

                            // Recorded only once acceptance is certain: a crash or rejection before
                            // this point must leave no record, so the prompt can be retried.
                            if accepted then
                                dependencies.RecordAcceptedRevision gitData.Path baseRevision
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

let triggerInBackground dependencies repoRoot upstreamRemote baseBranch prStatus (gitData: GitWorktree.GitData) =
    startGuarded
        (fun ex -> Log.log "AutoSync" $"Background trigger failed for {gitData.Branch}: {ex.Message}")
        (trigger dependencies repoRoot upstreamRemote baseBranch prStatus gitData)
