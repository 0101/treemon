module Server.SessionActivityIngestion

open System
open Server.SessionActivity
open Server.SessionActivityProtocol
open Server.SessionActivityStore
open Shared

type internal ExactReport =
    { ProcessIdentity: ProcessIdentity
      ReceivedAt: DateTimeOffset
      Report: SessionActivityReport }

type internal ServiceState =
    { Live: Map<ProcessIdentity, StoredInstance>
      PendingReconciliation: Set<ProcessIdentity>
      ActivityEpochState: TerminalOriginEpochState }

let internal emptyServiceState =
    { Live = Map.empty
      PendingReconciliation = Set.empty
      ActivityEpochState = emptyTerminalOriginEpochState }

let private exactMetadataMatches
    (prior: StoredInstance)
    (report: SessionActivityReport)
    =
    prior.SessionId = report.SessionId
    && prior.WorktreePath = report.WorktreePath
    && prior.Provider = report.Provider

let private withMatchingMetadata
    (prior: StoredInstance)
    (report: SessionActivityReport)
    onMatch
    =
    if exactMetadataMatches prior report then
        onMatch ()
    else
        Error
            "the exact process identity is registered to different session metadata"

let private resolvedTerminalOrigin
    (prior: StoredInstance)
    (report: SessionActivityReport)
    =
    match prior.TerminalSessionId, report.TerminalSessionId with
    | Some current, Some incoming when current <> incoming ->
        Error
            "terminalSessionId cannot change for one exact process identity"
    | Some current, _ -> Ok(Some current)
    | None, incoming -> Ok incoming

let private preparePrior observedAt (prior: StoredInstance) =
    if observedAt - prior.LastSeen > stalenessTimeout then
        { prior with
            Status.BackgroundAgentClocks = Map.empty }
    else
        { prior with
            Status =
                prior.Status
                |> pruneCompletedBackgroundAgentClocks
                    (max prior.LastSeen observedAt) }

let internal tryPrior
    (store: SessionActivityStore)
    (state: ServiceState)
    identity
    =
    state.Live
    |> Map.tryFind identity
    |> Option.orElseWith (fun () ->
        store.InstanceByIdentity identity)

let private originsChanged prior current =
    [ prior |> Option.bind _.TerminalSessionId
      current.TerminalSessionId ]
    |> List.choose id
    |> Set.ofList

let internal publishInstance
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (observedAt: DateTimeOffset)
    (prior: StoredInstance option)
    (state: ServiceState)
    (persisted: StoredInstance)
    =
    let live =
        state.Live
        |> Map.add persisted.ProcessIdentity persisted
        |> SchedulerState.evictStaleInstances

    scheduler.Post(
        SchedulerState.UpdateSessionInstance(
            persisted,
            observedAt
        )
    )

    { state with
        Live = live
        ActivityEpochState =
            state.ActivityEpochState
            |> recordTerminalOriginActivity (originsChanged prior persisted) }

let private createPresenceInstance (exact: ExactReport) =
    { ProcessIdentity = exact.ProcessIdentity
      SessionId = exact.Report.SessionId
      TerminalSessionId = exact.Report.TerminalSessionId
      WorktreePath = exact.Report.WorktreePath
      Provider = exact.Report.Provider
      Status = emptyStatus
      UpdatedAt = DateTimeOffset.MinValue
      LifecycleAt = None
      LastSeen = exact.ReceivedAt
      ContextUsageAt = None
      ClosedAt = None }

let internal applyPresence
    (store: SessionActivityStore)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    =
    let prior =
        tryPrior store state exact.ProcessIdentity
        |> Option.map (preparePrior exact.ReceivedAt)

    match prior with
    | Some existing when not (exactMetadataMatches existing exact.Report) ->
        Error(
            false,
            "the exact process identity is already registered to different session metadata"
        )
    | Some existing when existing.ClosedAt.IsSome ->
        Error(false, "the exact process identity is already closed")
    | Some existing ->
        match resolvedTerminalOrigin existing exact.Report with
        | Error reason -> Error(false, reason)
        | Ok terminalSessionId ->
            let next =
                { existing with
                    TerminalSessionId = terminalSessionId
                    LastSeen = max existing.LastSeen exact.ReceivedAt }

            let persisted = store.UpsertInstance next
            let published =
                publishInstance
                    scheduler
                    exact.ReceivedAt
                    prior
                    state
                    persisted

            Ok
                ({ published with
                    PendingReconciliation =
                        state.PendingReconciliation
                        |> Set.remove exact.ProcessIdentity },
                 persisted)
    | None ->
        let persisted =
            exact |> createPresenceInstance |> store.UpsertInstance

        Ok(
            publishInstance
                scheduler
                exact.ReceivedAt
                None
                state
                persisted,
            persisted
        )

let private isBaseLifecycle =
    function
    | TurnStarted
    | UserPrompt _
    | AssistantMessage _
    | SkillInvoked _
    | TurnEnded
    | WentIdle -> true
    | _ -> false

let private isIndependentHistory =
    function
    | IntentReported _
    | TitleReported _
    | AwaitingUserInput _
    | UserInputCompleted _
    | BackgroundAgentStarted _
    | BackgroundAgentFinished _ -> true
    | _ -> false

let private eventRow (exact: ExactReport) =
    { ProcessIdentity = exact.ProcessIdentity
      EventId = exact.Report.EventId
      Ts = exact.Report.OccurredAt }

let private withAcceptedOrigin
    (prior: StoredInstance)
    (report: SessionActivityReport)
    =
    resolvedTerminalOrigin prior report
    |> Result.map (fun terminalSessionId ->
        { prior with TerminalSessionId = terminalSessionId })

/// Shared tail of every known-report path except closure: resolve the accepted terminal origin,
/// let the caller fold its event-specific fields onto it, persist, and publish to scheduler state.
/// `persist` returns `None` for a no-op/dedupe write (e.g. a duplicate event ID), leaving `state`
/// unchanged.
let private persistAcceptedInstance
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    (prior: StoredInstance)
    (buildNext: StoredInstance -> StoredInstance)
    (persist: StoredInstance -> StoredInstance option)
    =
    withAcceptedOrigin prior exact.Report
    |> Result.map (fun withOrigin ->
        match persist (buildNext withOrigin) with
        | None -> state
        | Some persisted ->
            publishInstance
                scheduler
                exact.ReceivedAt
                (Some prior)
                state
                persisted)

let private applyLifecycleEvent
    (store: SessionActivityStore)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    (prior: StoredInstance)
    =
    let isOutOfOrder =
        prior.LifecycleAt
        |> Option.exists (fun timestamp ->
            exact.Report.OccurredAt < timestamp)

    let buildNext (withOrigin: StoredInstance) =
        if isOutOfOrder then
            withOrigin
        else
            { withOrigin with
                Status = fold prior.Status exact.Report.Event
                UpdatedAt =
                    max
                        withOrigin.UpdatedAt
                        exact.Report.OccurredAt
                LifecycleAt =
                    Some(
                        withOrigin.LifecycleAt
                        |> Option.fold
                            max
                            exact.Report.OccurredAt
                    ) }

    persistAcceptedInstance
        scheduler
        state
        exact
        prior
        buildNext
        (fun next -> store.AppendAndUpsert(eventRow exact, next))

let private applyIndependentHistoryEvent
    (store: SessionActivityStore)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    (prior: StoredInstance)
    =
    let expiredBackgroundEvent =
        match exact.Report.Event with
        | BackgroundAgentStarted _
        | BackgroundAgentFinished _ ->
            isExpiredBackgroundAgentEvent
                prior.LastSeen
                exact.Report.OccurredAt
        | _ -> false

    let nextStatus =
        if expiredBackgroundEvent then
            prior.Status
        else
            fold prior.Status exact.Report.Event

    if expiredBackgroundEvent then
        Ok state
    else
        let buildNext (withOrigin: StoredInstance) =
            { withOrigin with
                Status = nextStatus
                UpdatedAt =
                    if nextStatus = prior.Status then
                        withOrigin.UpdatedAt
                    else
                        max
                            withOrigin.UpdatedAt
                            exact.Report.OccurredAt }

        persistAcceptedInstance
            scheduler
            state
            exact
            prior
            buildNext
            (fun next -> store.AppendAndUpsert(eventRow exact, next))

let private applyTitleBootstrap
    (store: SessionActivityStore)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    (prior: StoredInstance)
    =
    persistAcceptedInstance
        scheduler
        state
        exact
        prior
        (fun (withOrigin: StoredInstance) ->
            { withOrigin with
                Status = fold prior.Status exact.Report.Event })
        (fun next -> Some(store.UpsertInstance next))

let private applyUsage
    (store: SessionActivityStore)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    (prior: StoredInstance)
    (currentTokens: int)
    (tokenLimit: int)
    =
    let stale =
        prior.ContextUsageAt
        |> Option.exists (fun timestamp ->
            exact.Report.OccurredAt < timestamp)

    if stale then
        Ok state
    else
        persistAcceptedInstance
            scheduler
            state
            exact
            prior
            (fun (withOrigin: StoredInstance) ->
                { withOrigin with
                    Status.ContextUsage =
                        Some
                            { CurrentTokens = currentTokens
                              TokenLimit = tokenLimit }
                    ContextUsageAt =
                        Some exact.Report.OccurredAt })
            (fun next -> Some(store.UpsertInstance next))

let private applyHeartbeat
    (store: SessionActivityStore)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    (prior: StoredInstance)
    =
    if prior.ClosedAt.IsSome then
        Ok state
    else
        withMatchingMetadata prior exact.Report (fun () ->
            persistAcceptedInstance
                scheduler
                state
                exact
                prior
                (fun (withOrigin: StoredInstance) ->
                    { withOrigin with
                        LastSeen = max prior.LastSeen exact.ReceivedAt })
                (fun next -> Some(store.UpsertInstance next)))

let private applyClosure
    (store: SessionActivityStore)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    (prior: StoredInstance)
    =
    withMatchingMetadata prior exact.Report (fun () ->
        resolvedTerminalOrigin prior exact.Report
        |> Result.map (fun terminalSessionId ->
            match
                store.CloseInstance(
                    exact.ProcessIdentity,
                    exact.Report.OccurredAt,
                    terminalSessionId
                )
            with
            | None -> state
            | Some persisted ->
                let published =
                    publishInstance
                        scheduler
                        exact.ReceivedAt
                        (Some prior)
                        state
                        persisted

                { published with
                    PendingReconciliation =
                        state.PendingReconciliation
                        |> Set.remove exact.ProcessIdentity }))

let internal applyKnownReport
    (store: SessionActivityStore)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (state: ServiceState)
    (exact: ExactReport)
    =
    let prior =
        tryPrior store state exact.ProcessIdentity
        |> Option.map (preparePrior exact.ReceivedAt)

    match exact.Report.Event, prior with
    | SessionPresent, _ ->
        applyPresence store scheduler state exact
        |> Result.map fst
        |> Result.mapError snd
    | _, None -> Ok state
    | Heartbeat, Some current ->
        applyHeartbeat store scheduler state exact current
    | SessionClosed, Some current ->
        applyClosure store scheduler state exact current
    | UsageInfo(currentTokens, tokenLimit), Some current ->
        withMatchingMetadata current exact.Report (fun () ->
            applyUsage
                store
                scheduler
                state
                exact
                current
                currentTokens
                tokenLimit)
    | TitleBootstrap _, Some current ->
        withMatchingMetadata current exact.Report (fun () ->
            applyTitleBootstrap
                store
                scheduler
                state
                exact
                current)
    | event, Some current when isBaseLifecycle event ->
        withMatchingMetadata current exact.Report (fun () ->
            applyLifecycleEvent
                store
                scheduler
                state
                exact
                current)
    | event, Some current when isIndependentHistory event ->
        withMatchingMetadata current exact.Report (fun () ->
            applyIndependentHistoryEvent
                store
                scheduler
                state
                exact
                current)
    | event, Some _ ->
        Error $"unexpected session activity event: {event}"

let internal statusesForTerminalOrigins
    (terminalSessionIds: Set<TerminalSessionId>)
    (live: Map<ProcessIdentity, StoredInstance>)
    =
    live
    |> Map.values
    |> Seq.filter (fun instance ->
        instance.TerminalSessionId
        |> Option.exists terminalSessionIds.Contains)
    |> List.ofSeq

let internal reconcilePending
    (resolver: ProcessIdentityResolver)
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (now: DateTimeOffset)
    (terminalSessionIds: Set<TerminalSessionId>)
    (store: SessionActivityStore)
    (state: ServiceState)
    =
    let clear identity origin current =
        let changedOrigins =
            origin
            |> Option.map Set.singleton
            |> Option.defaultValue Set.empty

        { current with
            PendingReconciliation =
                current.PendingReconciliation
                |> Set.remove identity
            ActivityEpochState =
                current.ActivityEpochState
                |> recordTerminalOriginActivity changedOrigins }

    let closeDead identity instance current =
        try
            match
                store.CloseInstance(
                    identity,
                    now,
                    instance.TerminalSessionId
                )
            with
            | None -> Ok(clear identity instance.TerminalSessionId current)
            | Some persisted ->
                let published =
                    publishInstance
                        scheduler
                        now
                        (Some instance)
                        current
                        persisted

                Ok
                    { published with
                        PendingReconciliation =
                            published.PendingReconciliation
                            |> Set.remove identity }
        with error ->
            Error
                $"Could not close dead startup session identity: {error.Message}"

    let folder result identity =
        result
        |> Result.bind (fun current ->
            match tryPrior store current identity with
            | None ->
                Ok(clear identity None current)
            | Some instance ->
                if instance.ClosedAt.IsSome then
                    Ok(clear identity instance.TerminalSessionId current)
                elif now - instance.LastSeen >= openWindow then
                    Ok(clear identity instance.TerminalSessionId current)
                else
                    match instance.TerminalSessionId with
                    | None -> Ok(clear identity None current)
                    | Some origin when not (terminalSessionIds.Contains origin) ->
                        Ok(clear identity (Some origin) current)
                    | Some _ ->
                        ProcessIdentityResolver.isAlive resolver identity
                        |> Result.bind (fun alive ->
                            if alive then
                                Ok current
                            else
                                closeDead identity instance current))

    state.PendingReconciliation
    |> Set.fold
        folder
        (Ok state)
