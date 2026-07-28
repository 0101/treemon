module Server.SchedulerState

open System
open Shared
open Shared.EventUtils

type PerRepoState =
    { WorktreeList: GitWorktree.WorktreeInfo list
      KnownPaths: Set<string>
      GitData: Map<string, GitWorktree.GitData>
      BeadsData: Map<string, BeadsSummary>
      PlanningData: Map<string, BeadsPlanning>
      PrData: Map<string, PrStatus>
      CanvasData: Map<string, CanvasDoc list>
      Provider: RepoProvider option
      UpstreamRemote: string
      BaseBranch: string
      IsReady: bool }

module PerRepoState =
    let empty =
        { WorktreeList = []
          KnownPaths = Set.empty
          GitData = Map.empty
          BeadsData = Map.empty
          PlanningData = Map.empty
          PrData = Map.empty
          CanvasData = Map.empty
          Provider = None
          UpstreamRemote = "origin"
          BaseBranch = "main"
          IsReady = false }

type DashboardState =
    { Repos: Map<RepoId, PerRepoState>
      SchedulerEvents: CardEvent list
      PinnedErrors: Map<string * string, CardEvent>
      LatestByCategory: Map<string, CardEvent>
      ExpeditedRepos: Set<RepoId>
      ClientActivity: ActivityLevel
      ClientActivityAt: DateTimeOffset
      /// True only after the durable live-session rebuild has been applied, including an empty seed.
      /// Overview capture uses this to distinguish "no live sessions" from "startup has not loaded
      /// session state yet".
      SessionStatusesHydrated: bool
      // Push-model live session status, keyed by SessionId. Fed by the SessionActivity mailbox
      // (single writer) via UpdateSessionStatus and rebuilt from SQLite on restart. Kept bounded by
      // evicting entries older than the idle window (relative to the newest LastSeen) on each update
      // (evictStaleStatuses), so it mirrors the store's live cache (LoadLiveStatuses) rather than
      // growing append-only. This is the substrate the worktree card's coding-tool fields collapse
      // over (pickActive) — see the push-only repoint task; today it is populated but not yet read by
      // WorktreeApi.
      SessionStatuses: Map<SessionActivity.SessionId, SessionActivityStore.StoredStatus>
      // Per-worktree "entered Idle" timestamp for the time-since-idle chip (WorktreeStatus.CodingToolSince).
      // Keyed by the (normalised) worktree path. Stamped ONCE when a worktree's collapsed coding-tool
      // status transitions INTO Idle (the turn-end / last-active time), then FROZEN across the idle
      // heartbeats that keep advancing last_seen (so the chip shows time-in-category, not
      // time-since-last-write), and cleared when the status leaves Idle (a new Working turn moves it).
      // In-memory only: a restart rebuilds it from the reloaded sessions (re-stamping at reload time).
      CodingToolSinceByWorktree: Map<string, DateTimeOffset>
      AutoSyncLaunchesInFlight: Set<string>
      /// Worktrees with an auto-sync operation running: target selection, Treemon's own Git sync, and
      /// delivery. Separate from the launch guard, which one of those operations may take inside it.
      AutoSyncOperationsInFlight: Set<string> }

module DashboardState =
    let empty =
        { Repos = Map.empty
          SchedulerEvents = []
          PinnedErrors = Map.empty
          LatestByCategory = Map.empty
          ExpeditedRepos = Set.empty
          ClientActivity = ActivityLevel.Idle
          ClientActivityAt = DateTimeOffset.MinValue
          SessionStatusesHydrated = false
          SessionStatuses = Map.empty
          CodingToolSinceByWorktree = Map.empty
          AutoSyncLaunchesInFlight = Set.empty
          AutoSyncOperationsInFlight = Set.empty }

type RepositoryDiscovery =
    { Worktrees: GitWorktree.WorktreeInfo list option
      UpstreamRemote: string
      BaseBranch: string }

type StateMsg =
    | InitializeRepo of repoId: RepoId
    | UpdateWorktreeList of repoId: RepoId * GitWorktree.WorktreeInfo list
    | UpdateRepositoryDiscovery of repoId: RepoId * RepositoryDiscovery
    | UpdateGit of repoId: RepoId * path: string * GitWorktree.GitData
    | UpdateBeads of repoId: RepoId * path: string * BeadsSummary * BeadsPlanning
    | UpdateCanvasDoc of repoId: RepoId * path: string * CanvasDoc list
    | UpdatePr of repoId: RepoId * Map<string, PrStatus>
    | UpdateProvider of repoId: RepoId * RepoProvider option
    | UpdateUpstreamRemote of repoId: RepoId * remote: string
    | UpdateBaseBranch of repoId: RepoId * baseBranch: string
    | RemoveWorktree of repoId: RepoId * path: string
    | GetState of AsyncReplyChannel<DashboardState>
    | LogSchedulerEvent of CardEvent
    | ExpediteRefresh of RepoId
    | ClearExpedite of RepoId
    | ReportClientActivity of ActivityLevel * DateTimeOffset
    /// Push-model live status for one session, produced by the SessionActivity single-writer
    /// mailbox after folding an ingested event. Stored keyed by SessionId so a worktree's live
    /// sessions can later be collapsed (pickActive) into the card's coding-tool fields.
    | UpdateSessionStatus of SessionActivityStore.StoredStatus
    /// Restart rebuild: seed the whole live-status map in one shot (rows arrive oldest-first from
    /// LoadLiveStatuses) and stamp each worktree's time-since-idle from its NEWEST session — never the
    /// oldest-replayed row, which the per-row UpdateSessionStatus path would freeze in, overstating the
    /// chip for the whole post-restart idle span (F11/C-14).
    | SeedSessionStatuses of SessionActivityStore.StoredStatus list
    | TryBeginAutoSyncLaunch of path: string * AsyncReplyChannel<bool>
    | CompleteAutoSyncLaunch of path: string
    /// The per-worktree operation guard `AutoSync.trigger` holds for a whole sync attempt.
    | TryBeginAutoSyncOperation of path: string * AsyncReplyChannel<bool>
    | CompleteAutoSyncOperation of path: string

let private maxEvents = 50

let private trimEvents (events: CardEvent list) =
    events
    |> List.sortByDescending _.Timestamp
    |> List.truncate maxEvents

let private updatePinnedErrors (errors: Map<string * string, CardEvent>) (event: CardEvent) =
    let key = eventKey event
    match event.Status with
    | Some (StepStatus.Failed _) -> errors |> Map.add key event
    | Some StepStatus.Succeeded -> errors |> Map.remove key
    | _ -> errors

let private getRepo (repoId: RepoId) (state: DashboardState) =
    state.Repos
    |> Map.tryFind repoId
    |> Option.defaultValue PerRepoState.empty

let private updateRepo (repoId: RepoId) (repo: PerRepoState) (state: DashboardState) =
    { state with Repos = state.Repos |> Map.add repoId repo }

let private removeWorktreeData (path: string) (repo: PerRepoState) =
    { repo with
        WorktreeList = repo.WorktreeList |> List.filter (fun wt -> wt.Path <> path)
        GitData = repo.GitData |> Map.remove path
        BeadsData = repo.BeadsData |> Map.remove path
        PlanningData = repo.PlanningData |> Map.remove path
        CanvasData = repo.CanvasData |> Map.remove path }

/// Evict live session-status entries older than the idle window. `SessionStatuses` is otherwise
/// append-only, so without this it grows unboundedly and drifts from the store's live cache
/// (`LoadLiveStatuses`, same `idleWindow` cutoff) — long-dead sessions would linger in memory forever.
/// The window is measured against the NEWEST `LastSeen` in the map (the freshest heartbeat observed)
/// rather than wall-clock, so it stays deterministic and replay-safe (events can carry historical
/// timestamps) and never drops the entry that was just added. Applied on every `UpdateSessionStatus`.
let internal evictStaleStatuses
    (statuses: Map<SessionActivity.SessionId, SessionActivityStore.StoredStatus>)
    =
    if Map.isEmpty statuses then
        statuses
    else
        let newest = statuses |> Seq.map _.Value.LastSeen |> Seq.max
        let cutoff = newest - SessionActivity.idleWindow
        statuses |> Map.filter (fun _ s -> s.LastSeen >= cutoff)

/// Stamp / freeze / clear a worktree's time-since-idle timestamp from its freshly-collapsed
/// coding-tool status. Pure so it is unit-testable in isolation:
///   * status = Idle → stamp `now` on the FIRST entry, then FREEZE (keep the existing stamp across
///     the idle heartbeats that keep advancing last_seen — the chip must show time-IN-category, not
///     time-since-last-write);
///   * status = Working / WaitingForUser / NoSession → clear the entry (a new Working turn moves the
///     chip; a lost session leaves no idle time).
/// This stamp has TWO consumers that both depend on the freeze/reset policy above: the time-since-idle
/// chip (surfaced only while Idle) and `SessionActivity.debounceIdle` (the card read path), which
/// measures its Working→Idle display hold from this frozen transition instant — so weigh both before
/// changing when the stamp freezes or clears.
/// `worktreePath` is the normalised path key (`WorktreePath.value`) that WorktreeApi looks up.
let internal stampIdleSince
    (now: DateTimeOffset)
    (worktreePath: string)
    (status: CodingToolStatus)
    (idleSince: Map<string, DateTimeOffset>)
    : Map<string, DateTimeOffset> =
    match status with
    | Idle -> if Map.containsKey worktreePath idleSince then idleSince else idleSince |> Map.add worktreePath now
    | Working
    | WaitingForUser
    | NoSession -> idleSince |> Map.remove worktreePath

/// The status-overview "Agent \u2191" row (category `CodingToolRefresh`). Under the push model there is
/// no poll to log, so the row would sit permanently `pending`; instead we mark the latest extension
/// push here — which worktree last reported and when — as a green success, so a growing "X ago"
/// signals that pushes have stopped. `LastSeen` is the push instant; duration is meaningless for a
/// push (no server-side work) so it stays blank.
let internal codingToolPushEvent (stored: SessionActivityStore.StoredStatus) : CardEvent =
    { Source = "CodingToolRefresh"
      Message = WorktreePath.value stored.WorktreePath
      Timestamp = stored.LastSeen
      Status = Some StepStatus.Succeeded
      Duration = None }

let private updateWorktreeList
    (repoId: RepoId)
    (worktrees: GitWorktree.WorktreeInfo list)
    (state: DashboardState)
    =
    let repo = getRepo repoId state
    let newPaths = worktrees |> List.map _.Path |> Set.ofList
    let removedPaths = Set.difference repo.KnownPaths newPaths

    let cleaned =
        removedPaths
        |> Set.fold (fun r path -> removeWorktreeData path r) repo

    let updated =
        { cleaned with
            WorktreeList = worktrees
            KnownPaths = newPaths
            IsReady = true }

    // Prune the GLOBAL time-since-idle stamps for the removed worktrees. CodingToolSinceByWorktree
    // hangs off DashboardState (not PerRepoState), so it cannot be pruned inside removeWorktreeData;
    // without this a removed-then-recreated path inherits a stale FROZEN idle stamp (stampIdleSince
    // freezes existing keys), overstating the chip on reuse (F10/C-13).
    let prunedSince =
        removedPaths
        |> Set.fold (fun m path -> Map.remove path m) state.CodingToolSinceByWorktree

    // AutoSyncOperationsInFlight is deliberately NOT pruned here: AutoSync.trigger releases it in a
    // finally, so it already self-cleans for every operation that ends. Dropping it because the path
    // vanished from a discovery could only hand the guard to a second trigger while the first is
    // still merging, breaking the one-operation-per-worktree invariant (docs/spec/worktree-monitor.md).
    updateRepo repoId updated
        { state with
            CodingToolSinceByWorktree = prunedSince
            AutoSyncLaunchesInFlight = Set.difference state.AutoSyncLaunchesInFlight removedPaths }

/// Both auto-sync guards are the same thing at different scopes — one path may hold it, everyone
/// else is refused — so they share the claim/record step and differ only in which set they live in.
/// The answer is returned rather than replied to here, so the reply stays visible at the call site.
let private tryBeginGuard path (inFlight: Set<string>) =
    let claimed = not (Set.contains path inFlight)
    claimed, (if claimed then Set.add path inFlight else inFlight)

let private processMessage (state: DashboardState) (msg: StateMsg) =
    match msg with
    | InitializeRepo repoId ->
        if Map.containsKey repoId state.Repos then
            state
        else
            updateRepo repoId PerRepoState.empty state

    | UpdateWorktreeList(repoId, worktrees) ->
        updateWorktreeList repoId worktrees state

    | UpdateRepositoryDiscovery(repoId, discovery) ->
        let discoveredState =
            discovery.Worktrees
            |> Option.map (fun worktrees -> updateWorktreeList repoId worktrees state)
            |> Option.defaultValue state

        let repo = getRepo repoId discoveredState

        updateRepo
            repoId
            { repo with
                UpstreamRemote = discovery.UpstreamRemote
                BaseBranch = discovery.BaseBranch }
            discoveredState

    | UpdateGit(repoId, path, gitData) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId { repo with GitData = repo.GitData |> Map.add path gitData } state
        else
            state

    | UpdateBeads(repoId, path, beads, planning) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId
                { repo with
                    BeadsData = repo.BeadsData |> Map.add path beads
                    PlanningData = repo.PlanningData |> Map.add path planning }
                state
        else
            state

    | UpdateCanvasDoc(repoId, path, canvasDocs) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId { repo with CanvasData = repo.CanvasData |> Map.add path canvasDocs } state
        else
            state

    | UpdatePr(repoId, prMap) ->
        let repo = getRepo repoId state
        updateRepo repoId { repo with PrData = prMap } state

    | UpdateProvider(repoId, provider) ->
        let repo = getRepo repoId state
        updateRepo repoId { repo with Provider = provider } state

    | UpdateUpstreamRemote(repoId, remote) ->
        let repo = getRepo repoId state
        updateRepo repoId { repo with UpstreamRemote = remote } state

    | UpdateBaseBranch(repoId, baseBranch) ->
        let repo = getRepo repoId state
        updateRepo repoId { repo with BaseBranch = baseBranch } state

    | RemoveWorktree(repoId, path) ->
        let repo = getRepo repoId state
        // Also drop the worktree's GLOBAL time-since-idle stamp (same reason as UpdateWorktreeList —
        // it lives on DashboardState, not PerRepoState, so removeWorktreeData can't reach it; F10/C-13).
        let prunedSince = state.CodingToolSinceByWorktree |> Map.remove path
        // AutoSyncOperationsInFlight is left alone for the same reason as in updateWorktreeList: only
        // the operation that holds the guard may release it.
        updateRepo repoId (removeWorktreeData path repo)
            { state with
                CodingToolSinceByWorktree = prunedSince
                AutoSyncLaunchesInFlight = state.AutoSyncLaunchesInFlight |> Set.remove path }

    | GetState replyChannel ->
        replyChannel.Reply(state)
        state

    | LogSchedulerEvent event ->
        { state with
            SchedulerEvents = trimEvents (event :: state.SchedulerEvents)
            PinnedErrors = updatePinnedErrors state.PinnedErrors event
            LatestByCategory = state.LatestByCategory |> Map.add event.Source event }

    | ExpediteRefresh repoId ->
        { state with ExpeditedRepos = state.ExpeditedRepos |> Set.add repoId }

    | ClearExpedite repoId ->
        { state with ExpeditedRepos = state.ExpeditedRepos |> Set.remove repoId }

    | ReportClientActivity(activity, timestamp) ->
        { state with ClientActivity = activity; ClientActivityAt = timestamp }

    | UpdateSessionStatus stored ->
        // Add the fresh report, then evict entries past the idle window (measured against the newest
        // LastSeen) so the map stays bounded and mirrors the store's live cache instead of growing
        // append-only.
        let newStatuses =
            state.SessionStatuses
            |> Map.add stored.SessionId stored
            |> evictStaleStatuses

        // Re-collapse THIS worktree's live sessions (using the freshest observed time as `now`) to see
        // whether it just entered / left Idle, then stamp / freeze / clear the time-since-idle chip.
        let worktreePath = WorktreePath.value stored.WorktreePath

        let worktreeSessions =
            newStatuses
            |> Map.toList
            |> List.map snd
            |> List.filter (fun s -> s.WorktreePath = stored.WorktreePath)

        let collapsed = CodingToolStatus.fromPushSessions stored.LastSeen worktreeSessions

        { state with
            SessionStatuses = newStatuses
            LatestByCategory = state.LatestByCategory |> Map.add "CodingToolRefresh" (codingToolPushEvent stored)
            CodingToolSinceByWorktree =
                stampIdleSince stored.LastSeen worktreePath collapsed.Status state.CodingToolSinceByWorktree }

    | SeedSessionStatuses stored ->
        // Restart rebuild. LoadLiveStatuses replays rows OLDEST-first; feeding them one-by-one through
        // UpdateSessionStatus lets the oldest idle row stamp+FREEZE the chip, so a long-stale idle
        // session's timestamp gets locked in instead of the current open session's — the chip then
        // OVERSTATES time-since-idle for the whole post-restart idle span (F11/C-14). Instead seed the
        // map in one shot (same final set as replaying each row: evict measures against the global
        // newest), then stamp each worktree's chip from its NEWEST session's last_seen, collapsed at
        // that time. That yields the accepted "chip resets on restart" behaviour (Decision #8) rather
        // than an overstated old stamp — WITHOUT reversing the seed order to DESC.
        let seeded =
            (state.SessionStatuses, stored)
            ||> List.fold (fun m s -> Map.add s.SessionId s m)
            |> evictStaleStatuses

        let idleSince =
            seeded
            |> Map.toList
            |> List.map snd
            |> List.groupBy (fun s -> WorktreePath.value s.WorktreePath)
            |> List.fold
                (fun acc (worktreePath, sessions) ->
                    let newestSeen = sessions |> List.map _.LastSeen |> List.max
                    let collapsed = CodingToolStatus.fromPushSessions newestSeen sessions
                    stampIdleSince newestSeen worktreePath collapsed.Status acc)
                state.CodingToolSinceByWorktree

        // Prime the "Agent" push row from the newest seeded session so it reflects the last known push
        // immediately after restart instead of reverting to `pending` until the first live heartbeat.
        let latestByCategory =
            match seeded |> Map.toList |> List.map snd with
            | [] -> state.LatestByCategory
            | sessions ->
                let newest = sessions |> List.maxBy _.LastSeen
                state.LatestByCategory |> Map.add "CodingToolRefresh" (codingToolPushEvent newest)

        { state with
            SessionStatusesHydrated = true
            SessionStatuses = seeded
            LatestByCategory = latestByCategory
            CodingToolSinceByWorktree = idleSince }

    | TryBeginAutoSyncLaunch(path, reply) ->
        let claimed, inFlight = tryBeginGuard path state.AutoSyncLaunchesInFlight
        reply.Reply claimed
        { state with AutoSyncLaunchesInFlight = inFlight }

    | CompleteAutoSyncLaunch path ->
        { state with
            AutoSyncLaunchesInFlight = state.AutoSyncLaunchesInFlight |> Set.remove path }

    | TryBeginAutoSyncOperation(path, reply) ->
        let claimed, inFlight = tryBeginGuard path state.AutoSyncOperationsInFlight
        reply.Reply claimed
        { state with AutoSyncOperationsInFlight = inFlight }

    | CompleteAutoSyncOperation path ->
        { state with
            AutoSyncOperationsInFlight = state.AutoSyncOperationsInFlight |> Set.remove path }

let createAgent () =
    MailboxProcessor<StateMsg>.Start(fun inbox ->
        let rec loop (state: DashboardState) =
            async {
                let! msg = inbox.Receive()
                let newState = processMessage state msg
                return! loop newState
            }

        loop DashboardState.empty)
