module Server.AutoSync

open System
open FsToolkit.ErrorHandling
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

/// The in-process claim one worktree's revision is held under. The stage is what keeps the two
/// deduplication layers from cancelling each other out: `Delivering` is a live operation nothing may
/// take over, while `Accepted` has handed suppression to the durable record and may be taken over
/// exactly when that record says its retry window has passed — without which the claim would outlive
/// every expiry and block the retry for as long as the server runs. `Accepted` carries the
/// generation of the acceptance that published it, so a clear can name the acceptance it observed
/// and leave a later one — which owns the record on disk now — untouched.
type ClaimedRevision =
    | Delivering of baseRevision: string
    | Accepted of baseRevision: string * generation: AutoSyncStore.AcceptanceGeneration

/// Why a claim is being asked for. Only the durable record can prove that an accepted prompt aged
/// out, so the caller carries that proof here; the claim itself still refuses while a delivery for
/// the same revision is in flight.
type ClaimReason =
    | FirstAttempt
    | RetryExpiredAccept

/// Why Treemon's own sync of a worktree could not finish. A closed vocabulary because it is the only
/// thing a fallback prompt may name: Git stdout, stderr, conflicted filenames, refs, paths, and
/// commit messages are untrusted repository text and never enter a prompt (see
/// `docs/spec/worktree-monitor.md`, Branch Sync).
type SyncFailure =
    | DirtyWorktree
    | MergeConflict
    | MergeAbortFailed
    | GitCommandFailed
    | BranchChanged
    | OpenPrCheckFailed
    | PushFailed

/// One mechanical attempt's inputs: the Git sync's own request plus the repo root where the shared
/// branch configuration the provider query reads lives. The tree that actually moves is
/// `Sync.WorktreePath`, never `RepoRoot`.
type MechanicalSyncRequest =
    { Sync: GitBranchSync.BranchSyncRequest
      RepoRoot: string }

/// `ClaimRevision`/`ReleaseRevision` are the in-process claim; the accepted-revision operations are
/// the durable, restart-safe layer on top of it (`AutoSyncStore`). `RecordAcceptedRevision` and
/// `RetireAcceptedRevision` each write both sides of one acceptance — the durable record and the
/// claim's stage — so the two layers age out and are forgotten together.
type TriggerDependencies =
    { ClaimRevision: string -> string -> ClaimReason -> Async<bool>
      ReleaseRevision: string -> string -> unit
      ReadAcceptedRevision: string -> Async<AutoSyncStore.AcceptedSyncRecord option>
      /// Completes only once the record is published, because the same call then makes the claim
      /// retryable and the record is what a retry re-reads to learn the prompt already happened.
      RecordAcceptedRevision: string -> string -> Async<unit>
      /// Forgets both layers of *one* acceptance, named by the generation of the record the caller
      /// read. Anything published after that read is a later acceptance holding a live claim, and
      /// this must leave it whole. Ending auto-sync for a worktree outright — disable, merged
      /// cleanup, removal — is not this: those forget the path's record and claim unconditionally,
      /// which is `AutoSyncStore.clear` beside `ClearAutoSyncTrigger`.
      RetireAcceptedRevision: string -> AutoSyncStore.AcceptanceGeneration -> unit
      /// The worktree's current PR status, keyed by canonical worktree path so the lookup resolves
      /// inside that worktree's own repo and a same-named branch elsewhere can never answer for it.
      /// Read again at action boundaries because `RefreshPr` runs on its own cadence.
      ReadPrStatus: string -> Async<PrStatus>
      SelectTarget: string -> Async<SyncTarget>
      /// The per-worktree operation guard, held across target selection, mechanical work, and
      /// delivery. It serializes *work*, which the revision claim deliberately does not: a claim for
      /// a newer base revision supersedes an older one, and without this guard that supersession
      /// would start a second fetch and merge over one still running.
      TryBeginOperation: string -> Async<bool>
      CompleteOperation: string -> unit
      /// Treemon's own sync, run only when no session is open to do it.
      MechanicalSync: MechanicalSyncRequest -> Async<Result<unit, SyncFailure>>
      Deliver: DeliveryRequest -> Async<bool> }

/// One merged-and-still-enabled branch. The local branch name is the `.treemon.json` preference
/// key; the canonical worktree path is the trigger and durable-record key.
type MergedAutoSyncTarget =
    { Branch: string
      Path: string }

/// Both halves of the push rule live in the prompt itself rather than being selected from cached PR
/// state: the dashboard's PR map is eventually consistent and cannot tell a closed pull request from
/// an open one, while the agent can read the live state at the moment it would push. Provider-neutral
/// and free of any repository text.
let private pushPolicy =
    "If this branch has an open pull request, push the synced branch after the checks pass; otherwise, do not push."

let prompt upstreamRemote baseBranch =
    $"Sync this worktree with {upstreamRemote}/{baseBranch} when safe. Preserve any in-progress work, resolve conflicts carefully, and run the appropriate checks before considering the sync complete. {pushPolicy}"

/// All the agent is told about a mechanical attempt that stopped: what state the worktree may be in,
/// in Treemon's own words. The agent inspects the repository itself for anything more, which is why
/// no Git output has to be quoted here.
let internal failureDescription =
    function
    | DirtyWorktree -> "the worktree has uncommitted local changes"
    | MergeConflict -> "the merge conflicted and was aborted"
    | MergeAbortFailed -> "the merge conflicted and could not be aborted, so a merge may still be in progress"
    | GitCommandFailed -> "a Git command did not complete"
    | BranchChanged -> "the worktree is not on the branch this sync was started for"
    | OpenPrCheckFailed -> "the pull request state could not be determined"
    | PushFailed -> "the push was rejected"

let fallbackPrompt upstreamRemote baseBranch failure =
    $"{prompt upstreamRemote baseBranch} Treemon already attempted this sync itself and stopped because {failureDescription failure}."

let internal syncOutcomeResult =
    function
    | GitBranchSync.BranchSyncOutcome.FastForwarded
    | GitBranchSync.BranchSyncOutcome.Merged
    | GitBranchSync.BranchSyncOutcome.AlreadyCurrent -> Ok()
    | GitBranchSync.BranchSyncOutcome.RefusedDirty -> Error DirtyWorktree
    | GitBranchSync.BranchSyncOutcome.Conflicted -> Error MergeConflict
    | GitBranchSync.BranchSyncOutcome.AbortFailed -> Error MergeAbortFailed
    | GitBranchSync.BranchSyncOutcome.BranchChanged -> Error BranchChanged
    | GitBranchSync.BranchSyncOutcome.CommandFailed -> Error GitCommandFailed

let internal pushOutcomeResult =
    function
    | GitBranchSync.BranchPushOutcome.Pushed -> Ok()
    | GitBranchSync.BranchPushOutcome.BranchChanged -> Error BranchChanged
    | GitBranchSync.BranchPushOutcome.PushFailed -> Error PushFailed

/// Treemon's whole token-free path as one operation: the Git sync, the live push decision, and the
/// push that decision may require, all bound to the branch the observation was made on. Only a run
/// that finished all of it may skip agent delivery, so every step leaves through the same closed
/// reason. The effects are parameters so the sequence can be exercised without a repository or a
/// provider.
let mechanicalSync
    (checkedOutBranch: string -> Async<string option>)
    (syncWithBase: GitBranchSync.BranchSyncRequest -> Async<GitBranchSync.BranchSyncOutcome>)
    (queryOpenPrState: string -> string -> string -> Async<PrOpenState.OpenPrState>)
    (pushBranch: string -> string -> Async<GitBranchSync.BranchPushOutcome>)
    (request: MechanicalSyncRequest)
    =
    // Asked again at each boundary instead of trusting the observation that started the run: a
    // checkout decides which branch a merge lands on and which branch's work a push would publish,
    // and the pull request that authorizes the push is looked up for the observed branch alone. The
    // sync and the push each re-read the branch at their own mutation, so what this adds is refusing
    // to spend a fetch or a provider query on a worktree that has already moved on.
    let stillOnObservedBranch () =
        async {
            let! current = checkedOutBranch request.Sync.WorktreePath
            return if current = Some request.Sync.Branch then Ok() else Error BranchChanged
        }

    asyncResult {
        do! stillOnObservedBranch ()
        let! outcome = syncWithBase request.Sync
        do! syncOutcomeResult outcome
        // A merge that landed somewhere else is not this operation's work, so the provider is never
        // even asked about a branch the worktree has left.
        do! stillOnObservedBranch ()

        // Asked now, at the moment the push would happen, and of the provider rather than the
        // dashboard's cached map: the branch this run just moved is the only thing that may move
        // remotely, and only while a pull request is actually open to receive it.
        match! queryOpenPrState request.RepoRoot request.Sync.UpstreamRemote request.Sync.Branch with
        | PrOpenState.OpenPr ->
            let! pushOutcome = pushBranch request.Sync.WorktreePath request.Sync.Branch
            return! pushOutcomeResult pushOutcome
        | PrOpenState.NoOpenPr -> return ()
        | PrOpenState.UnknownPrState -> return! Error OpenPrCheckFailed
    }

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

/// Where the durable expiry meets the in-process claim. A record for this exact revision can only be
/// an expired one by the time this is asked — a live one already suppressed the trigger — and it is
/// the only proof that licenses taking over the claim an accepted prompt still holds.
let internal claimReason baseRevision (record: AutoSyncStore.AcceptedSyncRecord option) =
    match record with
    | Some record when record.BaseRevision = baseRevision -> RetryExpiredAccept
    | _ -> FirstAttempt

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

let private deliverPrompt
    (dependencies: TriggerDependencies)
    (gitData: GitWorktree.GitData)
    (target: SyncTarget)
    promptText
    =
    dependencies.Deliver
        { WorktreePath = WorktreePath gitData.Path
          Target = target
          Prompt = promptText }

/// Where the two paths part, once an operation owns its claim and has passed the eligibility gate.
/// An open session — even an idle one — is attached to its worktree and owns it, so Treemon only
/// asks it to sync. With no session open Treemon syncs the worktree itself and spends an agent only
/// on what it could not finish.
let private attemptSync
    (dependencies: TriggerDependencies)
    (repoRoot: string)
    upstreamRemote
    baseBranch
    (gitData: GitWorktree.GitData)
    (target: SyncTarget)
    =
    async {
        match target with
        | OpenSession _ ->
            return! deliverPrompt dependencies gitData target (prompt upstreamRemote baseBranch)
        | NoOpenSession _ ->
            let request =
                { Sync =
                    { WorktreePath = gitData.Path
                      UpstreamRemote = upstreamRemote
                      BaseBranch = baseBranch
                      Branch = gitData.Branch }
                  RepoRoot = repoRoot }

            match! dependencies.MechanicalSync request with
            | Ok() ->
                Log.log "AutoSync" $"Mechanical sync completed for {gitData.Branch}"
                return true
            | Error failure ->
                // The gate is re-passed here because a fetch, a merge, and a provider query have run
                // since it was last read: a branch disabled or reconciled merged meanwhile must not
                // be handed to an agent either.
                let! stillEligible = isStillEligible dependencies repoRoot gitData

                if not stillEligible then
                    Log.log "AutoSync" $"Dropped fallback prompt for {gitData.Branch}: no longer eligible"
                    return false
                else
                    return!
                        fallbackPrompt upstreamRemote baseBranch failure
                        |> deliverPrompt dependencies gitData target
    }

let private runOperation
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
            // again is new work, so both suppression layers must forget it and prompt again. Only
            // the acceptance this observation actually read is retired: a prompt accepted after that
            // read holds a live claim and owns the record on disk now, and erasing that record would
            // leave its claim suppressing the revision with nothing left to age out.
            if gitData.MainBehindCount = 0 then
                let! acceptedRecord = dependencies.ReadAcceptedRevision gitData.Path

                acceptedRecord
                |> Option.iter (fun record ->
                    dependencies.RetireAcceptedRevision gitData.Path record.Generation)
        | Some baseRevision ->
            let! acceptedRecord = dependencies.ReadAcceptedRevision gitData.Path

            if not (isAlreadyAccepted DateTimeOffset.UtcNow baseRevision acceptedRecord) then
                let! claimed =
                    dependencies.ClaimRevision gitData.Path baseRevision (claimReason baseRevision acceptedRecord)

                if claimed then
                    let drop reason =
                        Log.log "AutoSync" $"Dropped sync for {gitData.Branch}: {reason}"
                        dependencies.ReleaseRevision gitData.Path baseRevision

                    try
                        let! target = dependencies.SelectTarget gitData.Path
                        let! stillEligible = isStillEligible dependencies repoRoot gitData
                        // The durable record is re-read at the same boundary as eligibility because
                        // the claim taken above may have been an accepted one: a concurrent trigger
                        // that started from the same expired record could have delivered and
                        // recorded meanwhile, and its fresh record is the proof that this delivery
                        // would be a repeat.
                        let! currentRecord = dependencies.ReadAcceptedRevision gitData.Path

                        if not stillEligible then
                            drop "no longer eligible"
                        elif isAlreadyAccepted DateTimeOffset.UtcNow baseRevision currentRecord then
                            drop "already accepted by a concurrent trigger"
                        else
                            let! accepted =
                                attemptSync dependencies repoRoot upstreamRemote baseBranch gitData target

                            // Recorded only once the sync is certain to have happened: a crash, a
                            // rejected prompt, or a mechanical run that stopped must leave no record,
                            // so the revision can be retried. A completed mechanical run is an
                            // acceptance in its own right — the work it was claimed for is done.
                            if accepted then
                                do! dependencies.RecordAcceptedRevision gitData.Path baseRevision
                                Log.log "AutoSync" $"Sync accepted for {gitData.Branch} at base revision {baseRevision}"
                            else
                                dependencies.ReleaseRevision gitData.Path baseRevision
                    with ex ->
                        Log.log "AutoSync" $"Trigger failed for {gitData.Branch}: {ex.Message}"
                        dependencies.ReleaseRevision gitData.Path baseRevision
    }

/// One operation per worktree at a time. The revision claim cannot provide this: it exists to let a
/// newer base revision supersede an older one, which is exactly the case that would otherwise start
/// a second fetch and merge while the first is still running. A refused start changes nothing, so
/// the next refresh simply observes the worktree again.
let trigger
    (dependencies: TriggerDependencies)
    (repoRoot: string)
    (upstreamRemote: string)
    (baseBranch: string)
    (prStatus: PrStatus)
    (gitData: GitWorktree.GitData)
    =
    async {
        let! started = dependencies.TryBeginOperation gitData.Path

        if not started then
            Log.log "AutoSync" $"Skipped sync for {gitData.Branch}: an operation is already running"
        else
            try
                do! runOperation dependencies repoRoot upstreamRemote baseBranch prStatus gitData
            finally
                dependencies.CompleteOperation gitData.Path
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
