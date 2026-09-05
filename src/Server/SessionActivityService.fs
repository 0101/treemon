module Server.SessionActivityService

open System
open System.Threading
open Giraffe
open Microsoft.AspNetCore.Http
open Shared
open Server.SessionActivity
open Server.SessionActivityIngestion
open Server.SessionActivityProtocol
open Server.SessionActivityStore

// POST /api/session/activity validates one passive reporting record, resolves its parent Copilot PID
// to an exact process identity, and hands it to the single-writer mailbox. Presence is the one
// synchronous wire operation: success is returned only after session_instances was persisted.

// --- Request guard ----------------------------------------------------------------------------

let private allKnownPaths
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    =
    async {
        let! state =
            scheduler.PostAndAsyncReply SchedulerState.GetState

        return
            state.Repos
            |> Map.values
            |> Seq.collect _.KnownPaths
            |> Set.ofSeq
    }

let private isKnownWorktree scheduler path =
    async {
        let! paths = allKnownPaths scheduler
        return paths.Contains path
    }

let private isSyntheticSystemReminder report =
    match report.Event with
    | UserPrompt message ->
        match UserMessageFormatting.classify message.Text with
        | UserMessageFormatting.UserMessageClassification.SystemReminder ->
            true
        | UserMessageFormatting.UserMessageClassification.Display _ -> false
    | _ -> false

type AcceptOutcome =
    | Accepted of SessionActivityReport
    | Unmonitored of worktreePath: string
    | IgnoredSystemReminder
    | Rejected of reason: string

let tryAcceptReport
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    request
    =
    async {
        match parseReport DateTimeOffset.UtcNow request with
        | Error reason -> return Rejected reason
        | Ok report ->
            let path = WorktreePath.value report.WorktreePath
            let! known = isKnownWorktree scheduler path

            return
                if not known then
                    Unmonitored path
                elif isSyntheticSystemReminder report then
                    IgnoredSystemReminder
                else
                    Accepted report
    }

// --- Exact ingestion --------------------------------------------------------------------------

let internal retentionPeriod = TimeSpan.FromDays 60.0
let internal pruneInterval = TimeSpan.FromHours 1.0
let internal acknowledgedWriteTimeout = 5_000

[<RequireQualifiedAccess>]
type PresenceAcknowledge =
    | Recorded of ProcessIdentity
    | NotRecorded of retryable: bool * reason: string

[<RequireQualifiedAccess>]
type ClosureAcknowledge =
    | Closed
    | Missing
    | Failed of string

type private ServiceMsg =
    | Ingest of ExactReport
    | IngestForTest of ExactReport
    | PersistPresence of
        ExactReport *
        AsyncReplyChannel<PresenceAcknowledge>
    | CloseExact of
        ProcessIdentity *
        DateTimeOffset *
        AsyncReplyChannel<ClosureAcknowledge>
    | Seed of
        DateTimeOffset *
        StoredInstance list *
        AsyncReplyChannel<unit>
    | Snapshot of AsyncReplyChannel<Map<ProcessIdentity, StoredInstance>>
    | QueryTerminalActivity of
        DateTimeOffset *
        Set<TerminalSessionId> *
        AsyncReplyChannel<
            Result<
                int64 * StoredInstance list * Set<ProcessIdentity>,
                string
             >
         >
    | PruneMemory of
        DateTimeOffset *
        Set<TerminalSessionId> *
        AsyncReplyChannel<unit>
    | Stop of AsyncReplyChannel<unit>

/// Exact single-writer activity service. The process resolver is borrowed and may be shared with
/// TerminalHost/session-bridge lifecycle code.
type SessionActivityService internal
    (
        store: SessionActivityStore,
        scheduler: MailboxProcessor<SchedulerState.StateMsg>,
        ?processIdentityResolver: ProcessIdentityResolver,
        ?diagnosticSink: LifecycleDiagnostics.Sink
    ) =

    let resolver =
        defaultArg
            processIdentityResolver
            ProcessIdentityResolverRuntime.defaultResolver

    let diagnostics =
        defaultArg diagnosticSink LifecycleDiagnostics.write

    let dispositionGate = obj ()
    // Mailbox lifetime is the one unavoidable mutable service boundary.
    let mutable disposed = false

    let isDisposed () =
        lock dispositionGate (fun () -> disposed)

    let resolveReport receivedAt report =
        match
            ProcessIdentityResolver.resolve
                report.ParentProcessId
                resolver
        with
        | Error error ->
            Log.log
                "Activity"
                $"Process identity resolution failed: {error}"

            Error(true, "could not resolve the parent Copilot process identity")
        | Ok None ->
            Error(false, "the parent Copilot process is not running")
        | Ok(Some identity) ->
            Ok
                { ProcessIdentity = identity
                  ReceivedAt = receivedAt
                  Report = report }

    let recordPresence
        wasKnown
        observedAt
        (state: ServiceState)
        (persisted: StoredInstance)
        =
        diagnostics (
            LifecycleDiagnostics.Diagnostic.PresenceAcknowledged
                { Kind =
                    if wasKnown then
                        LifecycleDiagnostics.PresenceKind.Reconnected
                    else
                        LifecycleDiagnostics.PresenceKind.FirstSeen
                  ProcessIdentity = persisted.ProcessIdentity
                  SessionId = persisted.SessionId
                  TerminalSessionId = persisted.TerminalSessionId }
        )

        let openWorktreeInstances =
            state.Live
            |> Map.values
            |> Seq.filter (fun instance ->
                instance.WorktreePath = persisted.WorktreePath
                && instance.ClosedAt.IsNone
                && observedAt - instance.LastSeen < openWindow)
            |> Seq.toList

        let sessionIds =
            openWorktreeInstances
            |> List.map _.SessionId
            |> List.distinct

        if sessionIds.Length > 1 then
            diagnostics (
                LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved
                    { Boundary =
                        LifecycleDiagnostics.ObservationBoundary.Presence
                      ProcessCount = openWorktreeInstances.Length
                      SessionIds = sessionIds }
            )

        let sameSessionInstances =
            openWorktreeInstances
            |> List.filter (fun instance ->
                instance.SessionId = persisted.SessionId)

        if sameSessionInstances.Length > 1 then
            diagnostics (
                LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved
                    { Boundary =
                        LifecycleDiagnostics.ObservationBoundary.Presence
                      SessionId = persisted.SessionId
                      ProcessIdentities =
                        sameSessionInstances
                        |> List.map _.ProcessIdentity
                      TerminalSessionIds =
                        sameSessionInstances
                        |> List.choose _.TerminalSessionId
                        |> List.distinct
                      UnattributedProcessCount =
                        sameSessionInstances
                        |> List.filter _.TerminalSessionId.IsNone
                        |> List.length }
            )

    let mailbox =
        MailboxProcessor<ServiceMsg>.Start(fun inbox ->
            let rec loop state =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Ingest exact ->
                        let next =
                            try
                                match
                                    applyKnownReport
                                        store
                                        scheduler
                                        state
                                        exact
                                with
                                | Ok applied -> applied
                                | Error reason ->
                                    Log.log
                                        "Activity"
                                        $"Ignored exact activity report: {reason}"

                                    state
                            with error ->
                                Log.log
                                    "Activity"
                                    $"Ingest failed (report dropped, mailbox kept alive): {error.Message}"

                                state

                        return! loop next
                    | IngestForTest exact ->
                        let next =
                            try
                                let seeded =
                                    ensureTestPresence
                                        store
                                        scheduler
                                        state
                                        exact

                                match
                                    applyKnownReport
                                        store
                                        scheduler
                                        seeded
                                        exact
                                with
                                | Ok applied -> applied
                                | Error _ -> seeded
                            with _ ->
                                state

                        return! loop next
                    | PersistPresence(exact, reply) ->
                        try
                            let wasKnown =
                                tryPrior
                                    store
                                    state
                                    exact.ProcessIdentity
                                |> Option.isSome

                            match
                                applyPresence
                                    store
                                    scheduler
                                    state
                                    exact
                            with
                            | Ok(next, persisted) ->
                                reply.Reply(
                                    PresenceAcknowledge.Recorded
                                        persisted.ProcessIdentity
                                )

                                recordPresence
                                    wasKnown
                                    exact.ReceivedAt
                                    next
                                    persisted

                                return! loop next
                            | Error(retryable, reason) ->
                                reply.Reply(
                                    PresenceAcknowledge.NotRecorded(
                                        retryable,
                                        reason
                                    )
                                )

                                return! loop state
                        with error ->
                            Log.log
                                "Activity"
                                $"Exact presence persistence failed: {error.Message}"

                            reply.Reply(
                                PresenceAcknowledge.NotRecorded(
                                    true,
                                    "could not persist exact session presence"
                                )
                            )

                            return! loop state
                    | CloseExact(identity, closedAt, reply) ->
                        try
                            match tryPrior store state identity with
                            | None ->
                                reply.Reply ClosureAcknowledge.Missing
                                return! loop state
                            | Some prior ->
                                match
                                    store.CloseInstance(
                                        identity,
                                        closedAt,
                                        None
                                    )
                                with
                                | None ->
                                    reply.Reply ClosureAcknowledge.Missing
                                    return! loop state
                                | Some persisted ->
                                    let published =
                                        publishInstance
                                            scheduler
                                            closedAt
                                            (Some prior)
                                            state
                                            persisted

                                    let next =
                                        { published with
                                            PendingReconciliation =
                                                state.PendingReconciliation
                                                |> Set.remove identity }

                                    reply.Reply ClosureAcknowledge.Closed

                                    return!
                                        loop next
                        with error ->
                            Log.log
                                "Activity"
                                $"Exact closure persistence failed: {error.Message}"

                            reply.Reply(
                                ClosureAcknowledge.Failed
                                    "could not persist exact session closure"
                            )

                            return! loop state
                    | Seed(now, loaded, reply) ->
                        let live =
                            loaded
                            |> List.map (fun instance ->
                                instance.ProcessIdentity, instance)
                            |> Map.ofList
                            |> SchedulerState.evictStaleInstances

                        let pending =
                            live
                            |> Map.values
                            |> Seq.filter (fun instance ->
                                instance.ClosedAt.IsNone
                                && instance.TerminalSessionId.IsSome
                                && now - instance.LastSeen < openWindow)
                            |> Seq.map _.ProcessIdentity
                            |> Set.ofSeq

                        reply.Reply()

                        return!
                            loop
                                { state with
                                    Live = live
                                    PendingReconciliation = pending }
                    | Snapshot reply ->
                        reply.Reply state.Live
                        return! loop state
                    | QueryTerminalActivity(
                        now,
                        terminalSessionIds,
                        reply
                      ) ->
                        match
                            reconcilePending
                                resolver
                                scheduler
                                now
                                terminalSessionIds
                                store
                                state
                        with
                        | Error error ->
                            reply.Reply(Error error)
                            return! loop state
                        | Ok reconciled ->
                            let activityEpoch, activityEpochState =
                                reconciled.ActivityEpochState
                                |> observeCurrentTerminalOrigins
                                    terminalSessionIds

                            let pending =
                                reconciled.PendingReconciliation
                                |> Set.filter (fun identity ->
                                    reconciled.Live
                                    |> Map.tryFind identity
                                    |> Option.bind _.TerminalSessionId
                                    |> Option.exists
                                        terminalSessionIds.Contains)

                            reply.Reply(
                                Ok(
                                    activityEpoch,
                                    statusesForTerminalOrigins
                                        terminalSessionIds
                                        reconciled.Live,
                                    pending
                                )
                            )

                            return!
                                loop
                                    { reconciled with
                                        ActivityEpochState =
                                            activityEpochState }
                    | PruneMemory(
                        now,
                        retainedTerminalSessionIds,
                        reply
                      ) ->
                        let liveCutoff = now - idleWindow

                        let live =
                            state.Live
                            |> Map.filter (fun _ instance ->
                                instance.LastSeen >= liveCutoff)

                        reply.Reply()

                        return!
                            loop
                                { state with
                                    Live = live
                                    PendingReconciliation =
                                        state.PendingReconciliation
                                        |> Set.filter live.ContainsKey
                                    ActivityEpochState =
                                        state.ActivityEpochState
                                        |> pruneTerminalOriginEpochs
                                            retainedTerminalSessionIds }
                    | Stop reply -> reply.Reply()
                }

            loop emptyServiceState)

    let persistPresence receivedAt report =
        if isDisposed () then
            PresenceAcknowledge.NotRecorded(
                true,
                "session activity service is stopped"
            )
        elif report.Event <> SessionPresent then
            PresenceAcknowledge.NotRecorded(
                false,
                "presence acknowledgement requires session_present"
            )
        else
            match resolveReport receivedAt report with
            | Error(retryable, reason) ->
                PresenceAcknowledge.NotRecorded(retryable, reason)
            | Ok exact ->
                try
                    mailbox.PostAndReply(
                        (fun reply ->
                            PersistPresence(exact, reply)),
                        timeout = acknowledgedWriteTimeout
                    )
                with
                | :? TimeoutException ->
                    PresenceAcknowledge.NotRecorded(
                        true,
                        "session presence persistence timed out"
                    )
                | :? ObjectDisposedException ->
                    PresenceAcknowledge.NotRecorded(
                        true,
                        "session activity service is stopped"
                    )
                | error ->
                    Log.log
                        "Activity"
                        $"Presence mailbox request failed: {error.Message}"

                    PresenceAcknowledge.NotRecorded(
                        true,
                        "session presence persistence failed"
                    )

    let pruneAt now =
        let deleted = store.PruneOld(now - retentionPeriod)
        let retainedOrigins = store.RetainedTerminalSessionIds()

        mailbox.PostAndReply(fun reply ->
            PruneMemory(now, retainedOrigins, reply))

        deleted

    let prune _ =
        try
            let deleted = pruneAt DateTimeOffset.UtcNow

            if deleted > 0 then
                Log.log
                    "Activity"
                    $"Retention: pruned {deleted} old activity row(s)"
        with error ->
            Log.log
                "Activity"
                $"Retention prune failed: {error.Message}"

    let pruneTimer =
        new Timer(
            TimerCallback(prune),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        )

    member this.Handler: HttpHandler =
        fun next context ->
            task {
                try
                    let! body =
                        context.BindJsonAsync<SessionActivityRequest>()

                    let! outcome =
                        tryAcceptReport scheduler body
                        |> Async.StartAsTask

                    match outcome with
                    | Rejected reason ->
                        Log.log "Activity" $"Rejected report: {reason}"
                        return!
                            RequestErrors.BAD_REQUEST reason next context
                    | Unmonitored path ->
                        Log.log
                            "Activity"
                            $"Report for unmonitored worktree — {path} (ignored)"

                        return!
                            Successful.ok
                                (json
                                    {| recorded = false
                                       monitored = false
                                       retryable = false |})
                                next
                                context
                    | IgnoredSystemReminder ->
                        return!
                            Successful.ok
                                (json
                                    {| recorded = false
                                       monitored = true
                                       retryable = false |})
                                next
                                context
                    | Accepted report ->
                        match report.Event with
                        | SessionPresent ->
                            match
                                persistPresence
                                    DateTimeOffset.UtcNow
                                    report
                            with
                            | PresenceAcknowledge.Recorded _ ->
                                return!
                                    Successful.ok
                                        (json
                                            {| recorded = true
                                               monitored = true
                                               retryable = false |})
                                        next
                                        context
                            | PresenceAcknowledge.NotRecorded(
                                retryable,
                                reason
                              ) ->
                                return!
                                    Successful.ok
                                        (json
                                            {| recorded = false
                                               monitored = true
                                               retryable = retryable
                                               reason = reason |})
                                        next
                                        context
                        | _ ->
                            match
                                resolveReport
                                    DateTimeOffset.UtcNow
                                    report
                            with
                            | Error(retryable, reason) ->
                                return!
                                    Successful.ok
                                        (json
                                            {| recorded = false
                                               monitored = true
                                               retryable = retryable
                                               reason = reason |})
                                        next
                                        context
                            | Ok exact ->
                                if isDisposed () then
                                    return!
                                        Successful.ok
                                            (json
                                                {| recorded = false
                                                   monitored = true
                                                   retryable = true
                                                   reason =
                                                    "session activity service is stopped" |})
                                            next
                                            context
                                else
                                    mailbox.Post(Ingest exact)

                                    return!
                                        Successful.ok
                                            (json
                                                {| recorded = true
                                                   monitored = true
                                                   retryable = false |})
                                            next
                                            context
                with error ->
                    Log.log
                        "Activity"
                        $"Report failed: malformed JSON — {error.Message}"

                    return!
                        RequestErrors.BAD_REQUEST
                            $"malformed JSON: {error.Message}"
                            next
                            context
            }

    /// Deterministic acknowledged presence seam used by HTTP and focused tests.
    member internal _.Present
        (
            report: SessionActivityReport,
            receivedAt: DateTimeOffset
        ) =
        persistPresence receivedAt report

    /// Lower-level fold test seam. Production ingestion requires session_present first; this helper
    /// creates a test-only presence shell for a first history-bearing event so the existing pure fold
    /// tests remain focused on ordering rather than HTTP bootstrap ceremony.
    member internal _.Submit(report: SessionActivityReport) =
        if isDisposed () then
            raise (ObjectDisposedException(nameof SessionActivityService))

        match resolveReport report.OccurredAt report with
        | Error(_, reason) -> invalidOp reason
        | Ok exact -> mailbox.Post(IngestForTest exact)

    member internal _.CloseProcess
        (
            identity: ProcessIdentity,
            closedAt: DateTimeOffset
        ) =
        if isDisposed () then
            ClosureAcknowledge.Failed
                "session activity service is stopped"
        else
            try
                mailbox.PostAndReply(
                    (fun reply ->
                        CloseExact(identity, closedAt, reply)),
                    timeout = acknowledgedWriteTimeout
                )
            with error ->
                Log.log
                    "Activity"
                    $"Exact closure mailbox request failed: {error.Message}"

                ClosureAcknowledge.Failed
                    "exact session closure failed"

    member internal _.IsProcessClosed(identity: ProcessIdentity) =
        if isDisposed () then
            Error "session activity service is stopped"
        else
            try
                store.InstanceByIdentity identity
                |> Option.exists _.ClosedAt.IsSome
                |> Ok
            with _ ->
                Error "exact session closure state could not be read"

    member internal _.StartAt(now: DateTimeOffset) =
        if isDisposed () then
            raise (ObjectDisposedException(nameof SessionActivityService))

        let loaded = store.LoadRecentInstances now

        scheduler.Post(
            SchedulerState.SeedSessionInstances(
                now,
                loaded
            )
        )

        mailbox.PostAndReply(fun reply ->
            Seed(now, loaded, reply))

        Log.log
            "Activity"
            $"Rebuilt {List.length loaded} exact session instance(s) from store"

        pruneTimer.Change(pruneInterval, pruneInterval) |> ignore

    member this.Start() = this.StartAt DateTimeOffset.UtcNow

    member internal _.ExactSnapshot() =
        if isDisposed () then
            raise (ObjectDisposedException(nameof SessionActivityService))

        mailbox.PostAndReply Snapshot

    member internal _.QueryTerminalActivityAt
        (
            now: DateTimeOffset,
            terminalSessionIds: Set<TerminalSessionId>
        ) =
        if isDisposed () then
            Error "session activity service is stopped"
        else
            try
                mailbox.PostAndReply(
                    (fun reply ->
                        QueryTerminalActivity(
                            now,
                            terminalSessionIds,
                            reply
                        )),
                    timeout = acknowledgedWriteTimeout
                )
            with error ->
                Error error.Message

    member this.QueryTerminalActivity terminalSessionIds =
        this.QueryTerminalActivityAt(
            DateTimeOffset.UtcNow,
            terminalSessionIds
        )

    member internal _.RunRetention(now: DateTimeOffset) =
        if isDisposed () then
            raise (ObjectDisposedException(nameof SessionActivityService))

        pruneAt now |> ignore

    member internal _.Store = store
    member internal _.ProcessIdentityResolver = resolver

    interface IDisposable with
        member _.Dispose() =
            let shouldStop =
                lock dispositionGate (fun () ->
                    if disposed then
                        false
                    else
                        disposed <- true
                        true)

            if shouldStop then
                pruneTimer.DisposeAsync().AsTask().GetAwaiter().GetResult()
                mailbox.PostAndReply Stop
                (mailbox :> IDisposable).Dispose()
