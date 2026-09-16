module Server.LifecycleDiagnostics

open Server.SessionActivity

[<Literal>]
let internal maxListedValues = 8

[<RequireQualifiedAccess>]
type internal ObservationBoundary =
    | Presence
    | Bridge

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
type internal TeardownStage =
    | Started of TeardownTarget * TerminalSessionId list
    | GracefulShutdownAttempted of terminalCount: int
    | HostCloseCompleted of HostCloseOutcome * closedCount: int * remainingCount: int
    | Completed
    | Failed

[<RequireQualifiedAccess>]
type internal Diagnostic =
    | PresenceAcknowledged of PresenceDiagnostic
    | BridgeRegistration of BridgeRegistrationDiagnostic
    | MultipleSessionsObserved of MultipleSessionsDiagnostic
    | SameSessionMultiplicityObserved of SameSessionMultiplicityDiagnostic
    | ShutdownTransition of ShutdownDiagnostic
    | ExactClosure of ExactClosureDiagnostic
    | TeardownTransition of TeardownStage

type internal Sink = Diagnostic -> unit

let internal ignore: Sink = fun _ -> ()

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

let private shutdownRejectionText =
    function
    | ShutdownRejection.MissingRegistration -> "missing_registration"
    | ShutdownRejection.StaleRegistration -> "stale_registration"
    | ShutdownRejection.InvalidCapability -> "invalid_capability"
    | ShutdownRejection.NonLoopbackRequest -> "non_loopback_request"
    | ShutdownRejection.Rejected -> "endpoint_rejected"
    | ShutdownRejection.RequestFailed -> "request_failed"
    | ShutdownRejection.VerificationFailed -> "verification_failed"

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
    | TeardownStage.GracefulShutdownAttempted terminalCount ->
        $"event=teardown stage=graceful_shutdown_attempted terminal_count={terminalCount}"
    | TeardownStage.HostCloseCompleted(outcome, closedCount, remainingCount) ->
        let outcomeText =
            match outcome with
            | HostCloseOutcome.Confirmed -> "confirmed"
            | HostCloseOutcome.Rejected -> "rejected"
            | HostCloseOutcome.Unverified -> "unverified"
            | HostCloseOutcome.Unavailable -> "unavailable"

        $"event=teardown stage=host_close_completed outcome={outcomeText} closed_count={closedCount} remaining_count={remainingCount}"
    | TeardownStage.Completed -> "event=teardown stage=completed"
    | TeardownStage.Failed -> "event=teardown stage=failed"

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
    | Diagnostic.TeardownTransition stage ->
        formatTeardownStage stage

let internal write: Sink =
    fun diagnostic ->
        diagnostic
        |> format
        |> Log.log "Lifecycle"
