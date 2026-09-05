module Server.LifecycleDiagnostics

open Server.SessionActivity

[<Literal>]
let internal maxListedValues = 8

[<RequireQualifiedAccess>]
type internal ObservationBoundary =
    | Presence
    | Bridge
    | Replacement

[<RequireQualifiedAccess>]
type internal PresenceKind =
    | FirstSeen
    | Reconnected

[<RequireQualifiedAccess>]
type internal BridgeRegistrationKind =
    | Added
    | Refreshed

type internal PresenceDiagnostic =
    { Kind: PresenceKind
      ProcessIdentity: ProcessIdentity
      SessionId: SessionId
      TerminalSessionId: TerminalSessionId option }

type internal BridgeRegistrationDiagnostic =
    { Kind: BridgeRegistrationKind
      ProcessIdentity: ProcessIdentity
      SessionId: SessionId option
      TerminalSessionId: TerminalSessionId option }

type internal MultipleSessionsDiagnostic =
    { Boundary: ObservationBoundary
      ProcessCount: int
      SessionIds: SessionId list }

type internal SameSessionMultiplicityDiagnostic =
    { Boundary: ObservationBoundary
      SessionId: SessionId
      ProcessIdentities: ProcessIdentity list
      TerminalSessionIds: TerminalSessionId list
      UnattributedProcessCount: int }

type internal TerminalConversationAnomalyDiagnostic =
    { TerminalSessionId: TerminalSessionId
      SelectedSessionId: SessionId option
      RetainedSessionIds: SessionId list
      ProcessIdentities: ProcessIdentity list }

[<RequireQualifiedAccess>]
type internal ShutdownRejection =
    | MissingRegistration
    | StaleRegistration
    | InvalidCapability
    | NonLoopbackRequest
    | Rejected
    | RequestFailed
    | VerificationFailed

[<RequireQualifiedAccess>]
type internal ShutdownStage =
    | Requested
    | RequestAccepted
    | CompletedExactClosure
    | CompletedProcessExit
    | Rejected of ShutdownRejection
    | TimedOut

type internal ShutdownDiagnostic =
    { ProcessIdentity: ProcessIdentity
      SessionId: SessionId option
      TerminalSessionId: TerminalSessionId option
      Stage: ShutdownStage }

[<RequireQualifiedAccess>]
type internal ExactClosureOutcome =
    | Recorded
    | Missing
    | Failed

type internal ExactClosureDiagnostic =
    { ProcessIdentity: ProcessIdentity
      SessionId: SessionId
      TerminalSessionId: TerminalSessionId
      Outcome: ExactClosureOutcome }

[<RequireQualifiedAccess>]
type internal ReplacementFailureKind =
    | GracefulShutdown
    | OldHostStop
    | StagedHostLaunch
    | StagedHostVerification
    | StagedRegistry
    | TerminalRecreation
    | CommandDelivery

[<RequireQualifiedAccess>]
type internal ReplacementStage =
    | Captured of terminalCount: int * processCount: int * selectedSessionCount: int
    | RecheckStarted
    | RaceLost
    | RecheckFailed
    | GracefulShutdownStarted of processCount: int
    | GracefulShutdownCompleted of completedCount: int * failedCount: int
    | OldHostCloseStarted of ProcessIdentity option
    | OldHostCloseConfirmed of ProcessIdentity option
    | OldHostCloseUnconfirmed of ProcessIdentity option
    | StagedHostLaunchStarted
    | StagedHostLaunchRejected
    | StagedHostStartedUnhealthy
    | StagedHostRunning of ProcessIdentity option
    | TerminalRecreationStarted of terminalCount: int * selectedSessionCount: int
    | TerminalRecreationCompleted of recreatedCount: int * deliveredCommandCount: int
    | RecoveryRequired of ReplacementFailureKind
    | RecoveryStarted
    | Completed

[<RequireQualifiedAccess>]
type internal HostGeneration =
    | Old
    | Staged
    | Unknown

[<RequireQualifiedAccess>]
type internal RecoveryHostOutcome =
    | Running of HostGeneration * ProcessIdentity option
    | Stopped
    | Unresolved of HostGeneration * ProcessIdentity option

[<RequireQualifiedAccess>]
type internal RecoveryRegistryOutcome =
    | Exact of terminalCount: int
    | Unavailable

[<RequireQualifiedAccess>]
type internal RecoverySelectedOutcome =
    | ResumeDelivered
    | ShutdownUnconfirmed
    | ResumeDeliveryUnconfirmed
    | ResumeNotAttempted

type internal RecoverySelectedSessionDiagnostic =
    { OriginalTerminalSessionId: TerminalSessionId
      CurrentTerminalSessionId: TerminalSessionId option
      SessionId: SessionId
      Outcome: RecoverySelectedOutcome }

[<RequireQualifiedAccess>]
type internal RecoveryStatus =
    | Recovered
    | Rejected

type internal RecoveryDiagnostic =
    { Status: RecoveryStatus
      Host: RecoveryHostOutcome
      Registry: RecoveryRegistryOutcome
      SelectedSessions: RecoverySelectedSessionDiagnostic list
      UnresolvedProcesses: ProcessIdentity list
      UnidentifiedHostGenerations: HostGeneration list }

[<RequireQualifiedAccess>]
type internal TeardownTarget =
    | Terminal
    | Worktree

[<RequireQualifiedAccess>]
type internal HostCloseOutcome =
    | Confirmed
    | Rejected
    | Unverified
    | Unavailable

[<RequireQualifiedAccess>]
type internal TeardownClosureOutcome =
    | Recorded
    | Failed

[<RequireQualifiedAccess>]
type internal TeardownStage =
    | Started of TeardownTarget * TerminalSessionId list
    | GracefulShutdownStarted of terminalCount: int
    | GracefulShutdownCompleted of terminalCount: int
    | HostCloseStarted of terminalCount: int
    | HostCloseCompleted of HostCloseOutcome * requestedCount: int * closedCount: int
    | ExactClosureStarted of terminalCount: int
    | ExactClosureCompleted of TeardownClosureOutcome * terminalCount: int
    | Completed of closedTerminalCount: int
    | Failed of remainingTerminalCount: int

[<RequireQualifiedAccess>]
type internal Diagnostic =
    | PresenceAcknowledged of PresenceDiagnostic
    | BridgeRegistration of BridgeRegistrationDiagnostic
    | MultipleSessionsObserved of MultipleSessionsDiagnostic
    | SameSessionMultiplicityObserved of SameSessionMultiplicityDiagnostic
    | TerminalConversationAnomalyObserved of TerminalConversationAnomalyDiagnostic
    | ShutdownTransition of ShutdownDiagnostic
    | ExactClosure of ExactClosureDiagnostic
    | ReplacementTransition of ReplacementStage
    | RecoveryCompleted of RecoveryDiagnostic
    | TeardownTransition of TeardownStage

type internal Sink = Diagnostic -> unit

let internal ignore: Sink = fun _ -> ()

let internal trySessionId value =
    SessionId.create value |> Result.toOption

let internal tryTerminalSessionId value =
    TerminalSessionId.create value |> Result.toOption

let private processIdentityText identity =
    let processId, startTicks = ProcessIdentity.sortKey identity
    $"{processId}@{startTicks}"

let private sessionIdText = SessionId.value
let private terminalSessionIdText = TerminalSessionId.value

let private optionText formatter =
    Option.map formatter >> Option.defaultValue "none"

let private boundedValues formatter values =
    let all =
        values
        |> List.map formatter
        |> List.sort

    let shown = all |> List.truncate maxListedValues
    all.Length, String.concat "," shown, all.Length - shown.Length

let private boundedFields name formatter values =
    let count, shown, omitted = boundedValues formatter values
    $"{name}_count={count} {name}=[{shown}] {name}_omitted={omitted}"

let private boundaryText =
    function
    | ObservationBoundary.Presence -> "presence"
    | ObservationBoundary.Bridge -> "bridge"
    | ObservationBoundary.Replacement -> "replacement"

let private shutdownRejectionText =
    function
    | ShutdownRejection.MissingRegistration -> "missing_registration"
    | ShutdownRejection.StaleRegistration -> "stale_registration"
    | ShutdownRejection.InvalidCapability -> "invalid_capability"
    | ShutdownRejection.NonLoopbackRequest -> "non_loopback_request"
    | ShutdownRejection.Rejected -> "endpoint_rejected"
    | ShutdownRejection.RequestFailed -> "request_failed"
    | ShutdownRejection.VerificationFailed -> "verification_failed"

let private replacementFailureText =
    function
    | ReplacementFailureKind.GracefulShutdown -> "graceful_shutdown"
    | ReplacementFailureKind.OldHostStop -> "old_host_stop"
    | ReplacementFailureKind.StagedHostLaunch -> "staged_host_launch"
    | ReplacementFailureKind.StagedHostVerification -> "staged_host_verification"
    | ReplacementFailureKind.StagedRegistry -> "staged_registry"
    | ReplacementFailureKind.TerminalRecreation -> "terminal_recreation"
    | ReplacementFailureKind.CommandDelivery -> "command_delivery"

let private hostGenerationText =
    function
    | HostGeneration.Old -> "old"
    | HostGeneration.Staged -> "staged"
    | HostGeneration.Unknown -> "unknown"

let private recoverySelectedOutcomeText =
    function
    | RecoverySelectedOutcome.ResumeDelivered -> "resume_delivered"
    | RecoverySelectedOutcome.ShutdownUnconfirmed -> "shutdown_unconfirmed"
    | RecoverySelectedOutcome.ResumeDeliveryUnconfirmed -> "resume_delivery_unconfirmed"
    | RecoverySelectedOutcome.ResumeNotAttempted -> "resume_not_attempted"

let private formatReplacementStage =
    function
    | ReplacementStage.Captured(terminalCount, processCount, selectedSessionCount) ->
        $"event=replacement stage=captured terminal_count={terminalCount} process_count={processCount} selected_session_count={selectedSessionCount}"
    | ReplacementStage.RecheckStarted ->
        "event=replacement stage=recheck_started"
    | ReplacementStage.RaceLost ->
        "event=replacement stage=race_lost"
    | ReplacementStage.RecheckFailed ->
        "event=replacement stage=recheck_failed"
    | ReplacementStage.GracefulShutdownStarted processCount ->
        $"event=replacement stage=graceful_shutdown_started process_count={processCount}"
    | ReplacementStage.GracefulShutdownCompleted(completedCount, failedCount) ->
        $"event=replacement stage=graceful_shutdown_completed completed_count={completedCount} failed_count={failedCount}"
    | ReplacementStage.OldHostCloseStarted identity ->
        $"event=replacement stage=old_host_close_started host_process={optionText processIdentityText identity}"
    | ReplacementStage.OldHostCloseConfirmed identity ->
        $"event=replacement stage=old_host_close_confirmed host_process={optionText processIdentityText identity}"
    | ReplacementStage.OldHostCloseUnconfirmed identity ->
        $"event=replacement stage=old_host_close_unconfirmed host_process={optionText processIdentityText identity}"
    | ReplacementStage.StagedHostLaunchStarted ->
        "event=replacement stage=staged_host_launch_started"
    | ReplacementStage.StagedHostLaunchRejected ->
        "event=replacement stage=staged_host_launch_rejected"
    | ReplacementStage.StagedHostStartedUnhealthy ->
        "event=replacement stage=staged_host_started_unhealthy"
    | ReplacementStage.StagedHostRunning identity ->
        $"event=replacement stage=staged_host_running host_process={optionText processIdentityText identity}"
    | ReplacementStage.TerminalRecreationStarted(terminalCount, selectedSessionCount) ->
        $"event=replacement stage=terminal_recreation_started terminal_count={terminalCount} selected_session_count={selectedSessionCount}"
    | ReplacementStage.TerminalRecreationCompleted(recreatedCount, deliveredCommandCount) ->
        $"event=replacement stage=terminal_recreation_completed recreated_count={recreatedCount} delivered_command_count={deliveredCommandCount}"
    | ReplacementStage.RecoveryRequired failure ->
        $"event=replacement stage=recovery_required failure={replacementFailureText failure}"
    | ReplacementStage.RecoveryStarted ->
        "event=replacement stage=recovery_started"
    | ReplacementStage.Completed ->
        "event=replacement stage=completed"

let private formatRecoverySelected selected =
    let current =
        selected.CurrentTerminalSessionId
        |> optionText terminalSessionIdText

    $"{sessionIdText selected.SessionId}/{recoverySelectedOutcomeText selected.Outcome}/{terminalSessionIdText selected.OriginalTerminalSessionId}/{current}"

let private formatRecovery diagnostic =
    let host =
        match diagnostic.Host with
        | RecoveryHostOutcome.Running(generation, identity) ->
            $"host_state=running host_generation={hostGenerationText generation} host_process={optionText processIdentityText identity}"
        | RecoveryHostOutcome.Stopped ->
            "host_state=stopped host_generation=none host_process=none"
        | RecoveryHostOutcome.Unresolved(generation, identity) ->
            $"host_state=unresolved host_generation={hostGenerationText generation} host_process={optionText processIdentityText identity}"

    let registry =
        match diagnostic.Registry with
        | RecoveryRegistryOutcome.Exact terminalCount ->
            $"registry=exact terminal_count={terminalCount}"
        | RecoveryRegistryOutcome.Unavailable ->
            "registry=unavailable terminal_count=unknown"

    let status =
        match diagnostic.Status with
        | RecoveryStatus.Recovered -> "recovered"
        | RecoveryStatus.Rejected -> "rejected"

    let selected =
        boundedFields
            "selected_sessions"
            formatRecoverySelected
            diagnostic.SelectedSessions

    let unresolved =
        boundedFields
            "unresolved_processes"
            processIdentityText
            diagnostic.UnresolvedProcesses

    let unidentified =
        boundedFields
            "unidentified_host_generations"
            hostGenerationText
            diagnostic.UnidentifiedHostGenerations

    $"event=recovery status={status} {host} {registry} {selected} {unresolved} {unidentified}"

let private formatTeardownStage =
    function
    | TeardownStage.Started(target, terminalSessionIds) ->
        let targetText =
            match target with
            | TeardownTarget.Terminal -> "terminal"
            | TeardownTarget.Worktree -> "worktree"

        let terminalFields =
            boundedFields
                "terminals"
                terminalSessionIdText
                terminalSessionIds

        $"event=teardown stage=started target={targetText} {terminalFields}"
    | TeardownStage.GracefulShutdownStarted terminalCount ->
        $"event=teardown stage=graceful_shutdown_started terminal_count={terminalCount}"
    | TeardownStage.GracefulShutdownCompleted terminalCount ->
        $"event=teardown stage=graceful_shutdown_completed terminal_count={terminalCount}"
    | TeardownStage.HostCloseStarted terminalCount ->
        $"event=teardown stage=host_close_started terminal_count={terminalCount}"
    | TeardownStage.HostCloseCompleted(outcome, requestedCount, closedCount) ->
        let outcomeText =
            match outcome with
            | HostCloseOutcome.Confirmed -> "confirmed"
            | HostCloseOutcome.Rejected -> "rejected"
            | HostCloseOutcome.Unverified -> "unverified"
            | HostCloseOutcome.Unavailable -> "unavailable"

        $"event=teardown stage=host_close_completed outcome={outcomeText} requested_count={requestedCount} closed_count={closedCount}"
    | TeardownStage.ExactClosureStarted terminalCount ->
        $"event=teardown stage=exact_closure_started terminal_count={terminalCount}"
    | TeardownStage.ExactClosureCompleted(outcome, terminalCount) ->
        let outcomeText =
            match outcome with
            | TeardownClosureOutcome.Recorded -> "recorded"
            | TeardownClosureOutcome.Failed -> "failed"

        $"event=teardown stage=exact_closure_completed outcome={outcomeText} terminal_count={terminalCount}"
    | TeardownStage.Completed closedTerminalCount ->
        $"event=teardown stage=completed closed_terminal_count={closedTerminalCount}"
    | TeardownStage.Failed remainingTerminalCount ->
        $"event=teardown stage=failed remaining_terminal_count={remainingTerminalCount}"

let internal format =
    function
    | Diagnostic.PresenceAcknowledged presence ->
        let kind =
            match presence.Kind with
            | PresenceKind.FirstSeen -> "first_seen"
            | PresenceKind.Reconnected -> "reconnected"

        $"event=presence_acknowledged kind={kind} process={processIdentityText presence.ProcessIdentity} session={sessionIdText presence.SessionId} terminal={optionText terminalSessionIdText presence.TerminalSessionId}"
    | Diagnostic.BridgeRegistration registration ->
        let kind =
            match registration.Kind with
            | BridgeRegistrationKind.Added -> "added"
            | BridgeRegistrationKind.Refreshed -> "refreshed"

        $"event=bridge_registration kind={kind} process={processIdentityText registration.ProcessIdentity} session={optionText sessionIdText registration.SessionId} terminal={optionText terminalSessionIdText registration.TerminalSessionId}"
    | Diagnostic.MultipleSessionsObserved multiple ->
        let sessionFields =
            boundedFields
                "sessions"
                sessionIdText
                multiple.SessionIds

        $"event=multiple_sessions boundary={boundaryText multiple.Boundary} process_count={multiple.ProcessCount} {sessionFields}"
    | Diagnostic.SameSessionMultiplicityObserved multiplicity ->
        let processFields =
            boundedFields
                "processes"
                processIdentityText
                multiplicity.ProcessIdentities

        let terminalFields =
            boundedFields
                "terminal_origins"
                terminalSessionIdText
                multiplicity.TerminalSessionIds

        $"event=same_session_multiplicity boundary={boundaryText multiplicity.Boundary} session={sessionIdText multiplicity.SessionId} {processFields} {terminalFields} unattributed_process_count={multiplicity.UnattributedProcessCount}"
    | Diagnostic.TerminalConversationAnomalyObserved multiplicity ->
        let retainedFields =
            boundedFields
                "retained_sessions"
                sessionIdText
                multiplicity.RetainedSessionIds

        let processFields =
            boundedFields
                "processes"
                processIdentityText
                multiplicity.ProcessIdentities

        $"event=terminal_origin_conversation_anomaly terminal={terminalSessionIdText multiplicity.TerminalSessionId} selected_session={optionText sessionIdText multiplicity.SelectedSessionId} {retainedFields} {processFields}"
    | Diagnostic.ShutdownTransition shutdown ->
        let stage =
            match shutdown.Stage with
            | ShutdownStage.Requested -> "requested"
            | ShutdownStage.RequestAccepted -> "request_accepted"
            | ShutdownStage.CompletedExactClosure -> "confirmed_exact_closure"
            | ShutdownStage.CompletedProcessExit -> "confirmed_process_exit"
            | ShutdownStage.Rejected rejection ->
                $"rejected outcome={shutdownRejectionText rejection}"
            | ShutdownStage.TimedOut -> "timed_out"

        $"event=graceful_shutdown stage={stage} process={processIdentityText shutdown.ProcessIdentity} session={optionText sessionIdText shutdown.SessionId} terminal={optionText terminalSessionIdText shutdown.TerminalSessionId}"
    | Diagnostic.ExactClosure closure ->
        let outcome =
            match closure.Outcome with
            | ExactClosureOutcome.Recorded -> "recorded"
            | ExactClosureOutcome.Missing -> "missing"
            | ExactClosureOutcome.Failed -> "failed"

        $"event=exact_closure source=terminal_teardown outcome={outcome} process={processIdentityText closure.ProcessIdentity} session={sessionIdText closure.SessionId} terminal={terminalSessionIdText closure.TerminalSessionId}"
    | Diagnostic.ReplacementTransition stage ->
        formatReplacementStage stage
    | Diagnostic.RecoveryCompleted recovery ->
        formatRecovery recovery
    | Diagnostic.TeardownTransition stage ->
        formatTeardownStage stage

let internal write: Sink =
    fun diagnostic ->
        diagnostic
        |> format
        |> Log.log "Lifecycle"
