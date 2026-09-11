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
      PrData: Map<string, PrStatus> option
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
          PrData = None
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
      SessionInstancesHydrated: bool
      /// Exact physical process instances, keyed by PID plus process-start identity. The collection
      /// stays bounded by the activity idle window and never collapses duplicate durable SessionIds.
      SessionInstances:
          Map<
              ProcessIdentity,
              SessionActivityStore.StoredInstance
           >
      /// Last observed collapsed status per worktree. This is paired with
      /// `CodingToolSinceByWorktree` so liveness-only updates can preserve the transition stamp.
      CodingToolStatusByWorktree: Map<string, CodingToolStatus>
      /// When the collapsed worktree status last changed. Heartbeats and other exact-instance
      /// updates preserve the timestamp while the collapsed status is unchanged. NoSession has no
      /// entry. In-memory only: startup seeds the currently observed status at the rebuild time.
      CodingToolSinceByWorktree: Map<string, DateTimeOffset>
      /// Worktrees with an auto-sync operation running: target selection, Treemon's own Git sync, and
      /// delivery, including any fallback launch one of those operations makes.
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
          SessionInstancesHydrated = false
          SessionInstances = Map.empty
          CodingToolStatusByWorktree = Map.empty
          CodingToolSinceByWorktree = Map.empty
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
    /// One exact process-instance update. A closed instance removes only that physical identity.
    | UpdateSessionInstance of
        SessionActivityStore.StoredInstance *
        observedAt: DateTimeOffset
    /// Restart rebuild: seed the complete bounded exact collection in one shot.
    | SeedSessionInstances of
        observedAt: DateTimeOffset *
        SessionActivityStore.StoredInstance list
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

/// Evict exact process instances older than the activity cache's idle window.
/// `SessionInstances` is otherwise append-only, so long-dead rows would linger indefinitely.
/// The window is measured against the NEWEST `LastSeen` in the map (the freshest heartbeat observed)
/// rather than wall-clock, so it stays deterministic and replay-safe (events can carry historical
/// timestamps) and never drops the entry that was just added. Applied on every exact update.
let internal evictStaleInstances
    (
        instances:
            Map<
                ProcessIdentity,
                SessionActivityStore.StoredInstance
             >
    )
    =
    if Map.isEmpty instances then
        instances
    else
        let newest = instances |> Seq.map _.Value.LastSeen |> Seq.max
        let cutoff = newest - SessionActivity.idleWindow
        instances |> Map.filter (fun _ instance -> instance.LastSeen >= cutoff)

let internal groupInstancesByWorktree
    (
        instances:
            seq<SessionActivityStore.StoredInstance>
    )
    =
    instances
    |> Seq.groupBy (
        _.WorktreePath
        >> WorktreePath.value
        >> PathUtils.normalizePath
    )
    |> Seq.map (fun (worktreePath, grouped) ->
        worktreePath, grouped |> List.ofSeq)
    |> Map.ofSeq

let private collapsedStatusAt
    (observedAt: DateTimeOffset)
    (worktreePath: string)
    (
        instancesByWorktree:
            Map<
                string,
                SessionActivityStore.StoredInstance list
             >
    )
    =
    instancesByWorktree
    |> Map.tryFind worktreePath
    |> Option.defaultValue []
    |> CodingToolStatus.fromPushInstances observedAt None
    |> _.Status

let internal updateCodingToolTransition
    (observedAt: DateTimeOffset)
    (worktreePath: string)
    (status: CodingToolStatus)
    (
        statuses: Map<string, CodingToolStatus>,
        since: Map<string, DateTimeOffset>
    )
    =
    match status, statuses |> Map.tryFind worktreePath with
    | NoSession, _ ->
        statuses |> Map.remove worktreePath,
        since |> Map.remove worktreePath
    | current, Some previous
        when current = previous
             && Map.containsKey worktreePath since ->
        statuses, since
    | current, _ ->
        statuses |> Map.add worktreePath current,
        since |> Map.add worktreePath observedAt

let private refreshCodingToolTransitions
    (observedAt: DateTimeOffset)
    (
        previousInstances:
            Map<
                ProcessIdentity,
                SessionActivityStore.StoredInstance
             >
    )
    (
        currentInstances:
            Map<
                ProcessIdentity,
                SessionActivityStore.StoredInstance
             >
    )
    (statuses: Map<string, CodingToolStatus>)
    (since: Map<string, DateTimeOffset>)
    =
    let previousByWorktree =
        previousInstances
        |> Map.values
        |> groupInstancesByWorktree

    let currentByWorktree =
        currentInstances
        |> Map.values
        |> groupInstancesByWorktree

    Set.unionMany
        [ previousByWorktree |> Map.keys |> Set.ofSeq
          currentByWorktree |> Map.keys |> Set.ofSeq
          statuses |> Map.keys |> Set.ofSeq ]
    |> Set.fold
        (fun transitionState worktreePath ->
            let previousStatus =
                collapsedStatusAt
                    observedAt
                    worktreePath
                    previousByWorktree

            let currentStatus =
                collapsedStatusAt
                    observedAt
                    worktreePath
                    currentByWorktree

            let preparedState =
                if previousStatus = currentStatus then
                    transitionState
                else
                    let statuses, since = transitionState
                    statuses |> Map.remove worktreePath,
                    since |> Map.remove worktreePath

            updateCodingToolTransition
                observedAt
                worktreePath
                currentStatus
                preparedState)
        (statuses, since)

/// The status-overview "Agent \u2191" row (category `CodingToolRefresh`). Under the push model there is
/// no poll to log, so the row would sit permanently `pending`; instead we mark the latest extension
/// push here — which worktree last reported and when — as a green success, so a growing "X ago"
/// signals that pushes have stopped. `observedAt` is the server receipt time; duration is meaningless
/// for a push (no server-side work) so it stays blank.
let internal codingToolPushEvent
    (observedAt: DateTimeOffset)
    (stored: SessionActivityStore.StoredInstance)
    : CardEvent =
    { Source = "CodingToolRefresh"
      Message = WorktreePath.value stored.WorktreePath
      Timestamp = observedAt
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

    // Prune the GLOBAL status-transition stamps for removed worktrees. They hang off DashboardState
    // (not PerRepoState), so removeWorktreeData cannot reach them.
    let prunedSince =
        removedPaths
        |> Set.fold (fun m path -> Map.remove path m) state.CodingToolSinceByWorktree

    let prunedInstances =
        state.SessionInstances
        |> Map.filter (fun _ instance ->
            removedPaths
            |> Set.contains (WorktreePath.value instance.WorktreePath)
            |> not)

    // AutoSyncOperationsInFlight is deliberately NOT pruned here: AutoSync.trigger releases it in a
    // finally, so it already self-cleans for every operation that ends. Dropping it because the path
    // vanished from a discovery could only hand the guard to a second trigger while the first is
    // still merging, breaking the one-operation-per-worktree invariant (docs/spec/worktree-monitor.md).
    updateRepo
        repoId
        updated
        { state with
            SessionInstances = prunedInstances
            CodingToolStatusByWorktree =
                removedPaths
                |> Set.fold
                    (fun statuses path ->
                        Map.remove path statuses)
                    state.CodingToolStatusByWorktree
            CodingToolSinceByWorktree = prunedSince }

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
        updateRepo repoId { repo with PrData = Some prMap } state

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
        // Also drop the worktree's GLOBAL status-transition state (same reason as
        // UpdateWorktreeList — it lives on DashboardState, not PerRepoState).
        let prunedSince = state.CodingToolSinceByWorktree |> Map.remove path
        let prunedInstances =
            state.SessionInstances
            |> Map.filter (fun _ instance ->
                WorktreePath.value instance.WorktreePath <> path)
        // AutoSyncOperationsInFlight is left alone for the same reason as in updateWorktreeList: only
        // the operation that holds the guard may release it.
        updateRepo repoId (removeWorktreeData path repo)
            { state with
                SessionInstances = prunedInstances
                CodingToolStatusByWorktree =
                    state.CodingToolStatusByWorktree
                    |> Map.remove path
                CodingToolSinceByWorktree = prunedSince }

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

    | UpdateSessionInstance(stored, observedAt) ->
        let updated =
            if stored.ClosedAt.IsSome then
                state.SessionInstances
                |> Map.remove stored.ProcessIdentity
            else
                state.SessionInstances
                |> Map.add stored.ProcessIdentity stored
                |> evictStaleInstances

        let codingToolStatuses, codingToolSince =
            refreshCodingToolTransitions
                observedAt
                state.SessionInstances
                updated
                state.CodingToolStatusByWorktree
                state.CodingToolSinceByWorktree

        { state with
            SessionInstances = updated
            LatestByCategory =
                state.LatestByCategory
                |> Map.add
                    "CodingToolRefresh"
                    (codingToolPushEvent observedAt stored)
            CodingToolStatusByWorktree = codingToolStatuses
            CodingToolSinceByWorktree = codingToolSince }

    | SeedSessionInstances(observedAt, stored) ->
        let seeded =
            stored
            |> List.filter _.ClosedAt.IsNone
            |> List.map (fun instance ->
                instance.ProcessIdentity, instance)
            |> Map.ofList
            |> evictStaleInstances

        let codingToolStatuses, codingToolSince =
            refreshCodingToolTransitions
                observedAt
                Map.empty
                seeded
                Map.empty
                Map.empty

        // Prime the "Agent" push row from the newest seeded instance so it reflects the last known
        // presence immediately after restart instead of reverting to pending until a live report.
        let latestByCategory =
            match seeded |> Map.values |> List.ofSeq with
            | [] -> state.LatestByCategory
            | instances ->
                let newest = instances |> List.maxBy _.LastSeen

                state.LatestByCategory
                |> Map.add
                    "CodingToolRefresh"
                    (codingToolPushEvent newest.LastSeen newest)

        { state with
            SessionInstancesHydrated = true
            SessionInstances = seeded
            LatestByCategory = latestByCategory
            CodingToolStatusByWorktree = codingToolStatuses
            CodingToolSinceByWorktree = codingToolSince }

    | TryBeginAutoSyncOperation(path, reply) ->
        // One path may hold the guard; everyone else is refused until the holder releases it.
        let claimed = not (Set.contains path state.AutoSyncOperationsInFlight)
        reply.Reply claimed

        if claimed then
            { state with AutoSyncOperationsInFlight = Set.add path state.AutoSyncOperationsInFlight }
        else
            state

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
