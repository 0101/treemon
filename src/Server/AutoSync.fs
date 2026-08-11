module Server.AutoSync

open System
open FsToolkit.ErrorHandling
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

/// Where a fallback prompt goes once Treemon has tried to sync a worktree nobody is working in.
/// Every case is an address, so a prompt can only ever be built for somewhere it can actually go —
/// the "nobody may be prompted here" verdict lives in `WorktreeOwnership` instead.
type SyncTarget =
    /// A CLI is open and has settled — idle for `settleWindow`, or blocked on a user who is not
    /// there. Treemon syncs the worktree itself and prompts this session only if it could not finish.
    | IdleSession of sessionId: string
    /// No CLI is open. A retained/offline identity from a closed CLI may still be known.
    | NoOpenSession of retainedSessionId: string option

module SyncTarget =
    /// The id a fallback prompt is addressed to, which is only ever a delivery hint — never evidence
    /// that a live agent will act on it.
    let sessionId =
        function
        | IdleSession sessionId -> Some sessionId
        | NoOpenSession retainedSessionId -> retainedSessionId

/// Who — if anyone — is working in a worktree, and therefore what Treemon may do about a sync.
/// Openness alone is not the question: a CLI that is merely open is a terminal somebody left
/// running, and waiting for it to close before Treemon will help is a condition no user would guess.
/// A session that is mid-turn — or one that has only just stopped — owns its worktree, so Treemon
/// waits for it rather than queueing a prompt the agent may not reach for hours. Keeping that
/// verdict out of `SyncTarget` is what makes delivering into a busy worktree unrepresentable rather
/// than merely unreached: `Busy` carries no address to deliver to.
type WorktreeOwnership =
    /// A CLI seen inside `SessionActivity.openWindow` is mid-turn (background agents included), or
    /// went idle too recently to have settled. Nothing is delivered and nothing is mutated; the next
    /// observation looks again.
    | Busy
    /// Nobody is working here, so Treemon may sync the worktree itself — and knows where to hand
    /// over whatever it could not finish.
    | Free of SyncTarget

type DeliveryRequest =
    { WorktreePath: WorktreePath
      Target: SyncTarget
      Prompt: string }

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
    | PushFailed

/// One mechanical attempt's inputs: the Git sync's own request plus the reconciled PR status that
/// decides whether the synced branch is also pushed.
type MechanicalSyncRequest =
    { Sync: GitBranchSync.BranchSyncRequest
      PrStatus: PrStatus }

/// The durable accepted-revision record is the only deduplication layer. `trigger` holds
/// `TryBeginOperation` across a whole attempt, so nothing else runs for the worktree while one is in
/// flight, and the record answers whether this exact revision was already prompted.
type TriggerDependencies =
    { ReadAcceptedRevision: string -> Async<AutoSyncStore.AcceptedSyncRecord option>
      /// Completes only once the record is readable, so the operation guard is never released ahead
      /// of the record the next observation re-reads.
      RecordAcceptedRevision: string -> string -> Async<unit>
      ClearAcceptedRevision: string -> unit
      /// The worktree's current PR status, keyed by canonical worktree path so the lookup resolves
      /// inside that worktree's own repo and a same-named branch elsewhere can never answer for it.
      /// Read again at action boundaries because `RefreshPr` runs on its own cadence.
      ReadPrStatus: string -> Async<PrStatus option>
      /// Whether anyone is working in the worktree, and where a fallback prompt goes when nobody is.
      ReadOwnership: string -> Async<WorktreeOwnership>
      /// One operation per worktree, held across target selection, mechanical work, and delivery, so
      /// a later observation cannot start a second fetch and merge over one still running.
      TryBeginOperation: string -> Async<bool>
      CompleteOperation: string -> unit
      /// Treemon's own sync, run once no session is working in the worktree.
      MechanicalSync: MechanicalSyncRequest -> Async<Result<unit, SyncFailure>>
      /// Re-reads the worktree's Git state after Treemon's own sync moved the branch, so the card
      /// stops reporting a behind count the sync has already cleared.
      ReloadGitData: string -> Async<unit>
      Deliver: DeliveryRequest -> Async<bool> }

/// Only an open pull request calls for publishing the synced branch.
let isOpenPr (prStatus: PrStatus) =
    match prStatus with
    | HasPr pr -> pr.State = PrState.Open
    | NoPr -> false

let private pushInstruction prStatus =
    if isOpenPr prStatus then
        "Push the synced branch after the checks pass."
    else
        "Do not push the synced branch."

let prompt upstreamRemote baseBranch prStatus =
    $"Sync this worktree with {upstreamRemote}/{baseBranch} when safe. Preserve any in-progress work, resolve conflicts carefully, and run the appropriate checks before considering the sync complete. {pushInstruction prStatus}"

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
    | PushFailed -> "the push was rejected"

let fallbackPrompt upstreamRemote baseBranch prStatus failure =
    $"{prompt upstreamRemote baseBranch prStatus} Treemon already attempted this sync itself and stopped because {failureDescription failure}."

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

/// Treemon's whole token-free path as one operation: the Git sync, then the push an open pull
/// request calls for. Only a run that finished all of it may skip agent delivery, so every step
/// leaves through the same closed reason. The effects are parameters so the sequence can be
/// exercised without a repository. Both mutations re-read the checked-out branch themselves, so a
/// worktree that moves on mid-run refuses instead of acting on a branch nobody observed.
let mechanicalSync
    (syncWithBase: GitBranchSync.BranchSyncRequest -> Async<GitBranchSync.BranchSyncOutcome>)
    (pushBranch: string -> string -> Async<GitBranchSync.BranchPushOutcome>)
    (request: MechanicalSyncRequest)
    =
    asyncResult {
        let! outcome = syncWithBase request.Sync
        do! syncOutcomeResult outcome

        if isOpenPr request.PrStatus then
            let! pushOutcome = pushBranch request.Sync.WorktreePath request.Sync.Branch
            return! pushOutcomeResult pushOutcome
    }

/// Merged is terminal: nothing remains to be synced into a branch whose PR is already merged, so a
/// leftover `.treemon.json` entry must not keep it syncing.
let isMergedPr (prStatus: PrStatus) =
    match prStatus with
    | HasPr pr -> pr.State = PrState.Merged
    | NoPr -> false

/// The single eligibility rule, shared by the first observation and every pre-action re-check.
let isEligible enabled prStatus = enabled && not (isMergedPr prStatus)

let internal readEnabledPreference repoRoot branch =
    TreemonConfig.readAutoSyncBranchSet (Some repoRoot) |> Set.contains branch

let revision eligible (gitData: GitWorktree.GitData) =
    if eligible && gitData.MainBehindCount > 0 then gitData.BaseRevision else None

/// How long an accepted prompt suppresses the same base revision. The confirmed incident's sync ran
/// for 23 minutes, so anything shorter re-prompts a session that is still working; the bound exists
/// at all so a prompt that was accepted but never acted on is eventually retried.
let acceptedRetryAge = TimeSpan.FromHours 1.0

/// A durable record suppresses only the revision it was written for, and only inside the retry
/// window — a different base revision is the legitimate case where the base advanced again. A record
/// stamped in the future is not inside any window: a clock rollback or a hand-edited runtime file
/// would otherwise suppress the revision until that future time, so it re-prompts instead.
let internal isAlreadyAccepted
    (now: DateTimeOffset)
    baseRevision
    (record: AutoSyncStore.AcceptedSyncRecord option)
    =
    match record with
    | Some record ->
        let age = now - record.AcceptedAt
        record.BaseRevision = baseRevision && age >= TimeSpan.Zero && age < acceptedRetryAge
    | None -> false

/// How long a session must have been idle before Treemon treats the worktree as free. Status dips to
/// idle for milliseconds between back-to-back turns, so an instantaneous reading would let a fetch
/// and merge start under an agent about to resume. Measured over 3,267 real inter-turn gaps, 87% are
/// under half a second and only 0.7% fall between two seconds and this window: the distribution is
/// transient dips or genuine idle with nothing in between, so the exact value is not delicate.
let internal settleWindow = TimeSpan.FromSeconds 30.0

/// Whether an open session has been idle long enough for Treemon to act in its worktree. `UpdatedAt`
/// is a last-write-wins ordering clock rather than an "entered idle at" stamp, so the raw
/// subtraction needs two guards. The `MinValue` sentinel of a session whose only report so far is a
/// title or intent hydration is not two thousand years of idleness — it is a session nobody has
/// observed working yet, which must wait. And a stamp ahead of server time (reports are clamped only
/// five minutes into the future) would otherwise read as negative idleness and pin the worktree busy
/// until the clock catches up, so it counts as settled — the same direction `isAlreadyAccepted`
/// takes for a future acceptance.
let internal hasSettled (now: DateTimeOffset) (updatedAt: DateTimeOffset) =
    let idleFor = now - updatedAt

    updatedAt > DateTimeOffset.MinValue
    && (idleFor >= settleWindow || idleFor < TimeSpan.Zero)

/// A session mid-turn decides that Treemon waits, and so does one that has only just stopped —
/// neither is a worktree Treemon may fetch and merge in. Anything else open has settled, and an
/// offline identity is consulted only once nothing is open at all, so an open idle CLI can never be
/// mistaken for one that merely left an id behind. Background agents count as work: `effectiveStatus`
/// reports Working while one runs, even between the session's own turns.
let internal ownershipFromSessions (now: DateTimeOffset) (sessions: StoredStatus list) =
    let openSessions =
        sessions |> List.filter (fun session -> now - session.LastSeen < openWindow)

    match openSessions |> pickWorking _.Status StoredStatus.activityOrderKey with
    | Some _ -> Busy
    | None ->
        match openSessions |> StoredStatus.tryMostRecentActivity with
        | Some settled when hasSettled now settled.UpdatedAt ->
            Free(IdleSession(SessionId.value settled.SessionId))
        | Some _ -> Busy
        | None ->
            sessions
            |> StoredStatus.tryMostRecentActivity
            |> Option.map (_.SessionId >> SessionId.value)
            |> NoOpenSession
            |> Free

let readOwnership
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
    |> ownershipFromSessions DateTimeOffset.UtcNow

let internal registrationGraceMilliseconds = 3000

/// The answer of the pre-action gate: the PR status the operation may act on, or why it may not act.
type SyncEligibility =
    | Eligible of PrStatus
    | Ineligible
    /// No PR refresh has succeeded for the repository yet, so nothing can say whether a mechanically
    /// synced branch has a pull request to publish to.
    | PrStatusUnknown

/// The gate an operation must re-pass immediately before it acts, answering with the PR status it
/// read. `RefreshPr` runs on its own cadence, so it can remove the preference — or learn the PR
/// merged — after the Git observation that started this operation, and the same reading decides
/// whether a mechanically synced branch is pushed. Cancelling here is safe because nothing has been
/// delivered yet; nothing may cancel once a prompt has been accepted or a Git command is running.
let eligiblePrStatus (dependencies: TriggerDependencies) (repoRoot: string) (gitData: GitWorktree.GitData) =
    async {
        let enabled = readEnabledPreference repoRoot gitData.Branch

        if not enabled then
            return Ineligible
        else
            let! prStatus = dependencies.ReadPrStatus gitData.Path

            return
                match prStatus with
                | None -> PrStatusUnknown
                | Some status when isEligible enabled status -> Eligible status
                | Some _ -> Ineligible
    }

let deliver
    (tryDeliver: SessionBridge.SendRequest -> Async<SessionBridge.DeliveryResult>)
    (waitForRegistration: unit -> Async<unit>)
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

let private attemptSync
    (dependencies: TriggerDependencies)
    (repoRoot: string)
    upstreamRemote
    baseBranch
    (gitData: GitWorktree.GitData)
    (prStatus: PrStatus)
    =
    async {
        let request =
            { Sync =
                { WorktreePath = gitData.Path
                  UpstreamRemote = upstreamRemote
                  BaseBranch = baseBranch
                  Branch = gitData.Branch }
              PrStatus = prStatus }

        match! dependencies.MechanicalSync request with
        | Ok() ->
            Log.log "AutoSync" $"Mechanical sync completed for {gitData.Branch}"
            // This sync is what made the card's behind count wrong, so the worktree is re-read now
            // rather than whenever the scheduler next comes round to it.
            do! dependencies.ReloadGitData gitData.Path
            return true
        | Error failure ->
            // The gate is re-passed here because a fetch and a merge have run since it was last
            // read: a branch disabled or reconciled merged meanwhile must not be handed to an
            // agent either. If the cache is temporarily unreadable, the reconciled status from
            // before the mutation still gives the prompt its explicit push instruction.
            let deliverFallback currentPrStatus =
                async {
                    // The target is re-selected rather than carried in from before the mutation, for
                    // the same reason: a session that had settled when the fetch started can be mid-turn
                    // by the time it stops. Nothing is recorded, so the next observation retries once
                    // the worktree is free.
                    match! dependencies.ReadOwnership gitData.Path with
                    | Busy ->
                        Log.log "AutoSync" $"Dropped fallback prompt for {gitData.Branch}: a session is now working in the worktree"
                        return false
                    | Free target ->
                        return!
                            fallbackPrompt upstreamRemote baseBranch currentPrStatus failure
                            |> deliverPrompt dependencies gitData target
                }

            match! eligiblePrStatus dependencies repoRoot gitData with
            | Ineligible ->
                Log.log "AutoSync" $"Dropped fallback prompt for {gitData.Branch}: no longer eligible"
                return false
            | Eligible currentPrStatus ->
                return! deliverFallback currentPrStatus
            | PrStatusUnknown ->
                return! deliverFallback prStatus
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
            // again is new work, so the record must forget it and prompt again.
            if gitData.MainBehindCount = 0 then
                dependencies.ClearAcceptedRevision gitData.Path
        | Some baseRevision ->
            let! acceptedRecord = dependencies.ReadAcceptedRevision gitData.Path

            if not (isAlreadyAccepted DateTimeOffset.UtcNow baseRevision acceptedRecord) then
                try
                    // Ownership is read before the eligibility gate so a deferred observation pays
                    // nothing for it: the gate reads `.treemon.json` under the process-wide config
                    // lock and takes a scheduler mailbox round-trip, and a busy worktree acts on
                    // neither answer. A session mid-turn — or one that has only just stopped — owns
                    // its worktree, so Treemon waits for the next observation rather than mutating
                    // under it or queueing a prompt the agent may not reach for hours.
                    match! dependencies.ReadOwnership gitData.Path with
                    | Busy ->
                        Log.log "AutoSync" $"Deferred sync for {gitData.Branch}: a session is working in the worktree"
                    | Free _ ->
                        let! eligibility = eligiblePrStatus dependencies repoRoot gitData

                        let plan =
                            match eligibility with
                            | Ineligible ->
                                Error $"Dropped sync for {gitData.Branch}: no longer eligible"
                            // Merging now would finish the observation and record it as accepted, and
                            // the branch is only ever behind this base revision once — so the push
                            // this run cannot decide on would never be retried. The next refresh,
                            // once PR data has loaded, observes the same worktree and syncs it with
                            // the decision in hand.
                            | PrStatusUnknown ->
                                Error $"Deferred sync for {gitData.Branch}: PR status not loaded yet"
                            | Eligible prStatus -> Ok prStatus

                        match plan with
                        | Error reason -> Log.log "AutoSync" reason
                        | Ok prStatus ->
                            let! accepted =
                                attemptSync dependencies repoRoot upstreamRemote baseBranch gitData prStatus

                            // Recorded only once the sync is certain to have happened: a crash, a
                            // rejected prompt, or a mechanical run that stopped must leave no record,
                            // so the revision can be retried. A completed mechanical run is an
                            // acceptance in its own right — the work it was started for is done.
                            if accepted then
                                do! dependencies.RecordAcceptedRevision gitData.Path baseRevision
                                Log.log "AutoSync" $"Sync accepted for {gitData.Branch} at base revision {baseRevision}"
                with ex ->
                    Log.log "AutoSync" $"Trigger failed for {gitData.Branch}: {ex.Message}"
    }

/// One operation per worktree at a time, covering target selection, mechanical work, and delivery,
/// so a later observation cannot start a second fetch and merge over one still running. A refused
/// start changes nothing, so the next refresh simply observes the worktree again.
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
