module Server.TerminalHostReplacement

open System
open FsToolkit.ErrorHandling
open Server.SessionActivity
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Treemon.TerminalHosting

type internal ReplacementTerminal =
    { TerminalSessionId: string
      WorktreePath: string }

type internal ReplacementShutdownTarget =
    { TerminalSessionId: string
      WorktreePath: string
      CopilotSessionId: string
      ProcessIdentity: ProcessIdentity }

type internal ReplacementResumeCommand =
    { CopilotSessionId: string
      Command: string }

[<RequireQualifiedAccess>]
type internal ReplacementSessionPlan =
    | WaitingForIdle
    | Ready of
        activityEpoch: int64 *
        shutdownTargets: ReplacementShutdownTarget list *
        resumeCommands: Map<string, ReplacementResumeCommand>

type internal ReplacementPolicyQuery = DateTimeOffset -> ReplacementTerminal list -> Result<ReplacementSessionPlan, string>

[<RequireQualifiedAccess>]
type internal ReplacementOutcome =
    | NoCandidate
    | WaitingForIdle
    | RaceLost
    | Replaced of stagedVersion: string
    | Failed of stagedVersion: string * error: string

type internal ReplacementPlan =
    { OldHost: DiscoveryManifest
      OldExecutablePath: string
      StagedVersion: string
      StagedExecutablePath: string
      RegistryRevision: int64
      Terminals: ReplacementTerminal list
      ActivityEpoch: int64
      ShutdownTargets: ReplacementShutdownTarget list
      ResumeCommands: Map<string, ReplacementResumeCommand> }

type private FailedVersionCooldown =
    { StagedVersion: string
      RetryAfter: DateTimeOffset }

type internal HostLaunchFailure =
    | LaunchRejected of string
    | LaunchStartedButUnhealthy of string

type internal HostLaunchOutcome =
    | HostLaunchFailed of HostLaunchFailure
    | HostLaunched of DiscoveryManifest

type internal ReplacementShutdownAttempt =
    { Target: ReplacementShutdownTarget
      Outcome:
        Result<
            SessionBridge.ShutdownCompletion,
            SessionBridge.ShutdownFailure
         > }

[<RequireQualifiedAccess>]
type internal ReplacementHostState =
    | OldHostHealthy
    | OldHostStopUnconfirmed
    | NoConfirmedHost
    | StagedHostRunning of DiscoveryManifest

type internal RecreatedTerminal =
    { OriginalTerminalSessionId: string
      NewTerminalSessionId: string
      WorktreePath: string }

type internal ReplacementProgress =
    { ShutdownAttempts: ReplacementShutdownAttempt list
      HostState: ReplacementHostState
      RecreatedTerminals: RecreatedTerminal list
      DeliveredCommandTerminalIds: Set<string> }

[<RequireQualifiedAccess>]
type internal ReplacementFailure =
    | GracefulShutdownFailed of ReplacementShutdownAttempt list
    | OldHostStopFailed of string
    | StagedHostLaunchFailed of HostLaunchFailure
    | StagedHostVerificationFailed of string
    | StagedRegistryReadFailed of string
    | StagedRegistryNotEmpty of terminalCount: int
    | TerminalRecreationFailed of ReplacementTerminal * TerminalMutationFailure
    | CommandDeliveryFailed of ReplacementTerminal * string

type internal ReplacementRecovery =
    { Capture: ReplacementPlan
      Progress: ReplacementProgress
      Failure: ReplacementFailure }

type internal ReplacementOperations =
    { ShutdownSession:
        ReplacementShutdownTarget
            -> Async<
                Result<
                    SessionBridge.ShutdownCompletion,
                    SessionBridge.ShutdownFailure
                 >
             >
      StopHost:
        Config -> DiscoveryManifest -> Async<Result<unit, string>>
      LaunchHost: Config -> Async<HostLaunchOutcome>
      RecreateTerminal:
        Config
            -> DiscoveryManifest
            -> ReplacementTerminal
            -> Async<
                Result<
                    RegistrySnapshot * TerminalRecord,
                    TerminalMutationFailure
                 >
             >
      DeliverCommand:
        Config
            -> TerminalRecord
            -> string
            -> Async<Result<unit, string>> }

type private ReplacementRecheck =
    | ReadyToCommit of DiscoveryManifest
    | RecheckChanged
    | RecheckFailed of string

[<RequireQualifiedAccess>]
type internal ReplacementCommit =
    | KeepState of ReplacementOutcome
    | RecoveryRequired of ReplacementRecovery
    | ApplyRegistry of DiscoveryManifest * RegistrySnapshot * ReplacementOutcome

let private queryReplacementPolicy
    (query: ReplacementPolicyQuery)
    (terminals: TerminalRecord list)
    : Result<ReplacementSessionPlan, string> =
    try
        terminals
        |> List.map (fun terminal ->
            { TerminalSessionId = terminal.SessionId; WorktreePath = terminal.WorktreePath })
        |> query DateTimeOffset.UtcNow
    with error ->
        Error $"Could not query the terminal replacement policy: {error.Message}"

let internal configForExecutable config executablePath =
    { config with
        HostExecutablePath = executablePath
        TtydExecutablePath =
            TerminalHostLayout.adjacentTtydExecutablePath executablePath }

let private launchHostAt config =
    async {
        match startHostProcess config with
        | Error error ->
            return HostLaunchFailed(LaunchRejected error)
        | Ok() ->
            match! waitForHealthyHost config with
            | Ok connection -> return HostLaunched connection
            | Error error ->
                return HostLaunchFailed(LaunchStartedButUnhealthy error)
    }

let internal defaultOperations
    (isClosed: ProcessIdentity -> Async<Result<bool, string>>)
    =
    { ShutdownSession =
        fun target ->
            SessionBridge.shutdownExact
                isClosed
                { WorktreePath = target.WorktreePath
                  ProcessIdentity = target.ProcessIdentity }
      StopHost = shutdownAndWait
      LaunchHost = launchHostAt
      RecreateTerminal =
        fun config connection terminal ->
            startTerminalOnHost
                config
                connection
                terminal.WorktreePath
      DeliverCommand =
        fun config terminal command ->
            async {
                try
                    return!
                        config.SendTerminalCommand
                            terminal.AttachmentEndpoint
                            command
                with _ ->
                    return Error "Could not submit the terminal command"
            } }

let private terminalPresentation (terminal: TerminalRecord) =
    { TerminalSessionId = terminal.SessionId
      WorktreePath = terminal.WorktreePath }

let private terminalPresentations terminals =
    terminals |> List.map terminalPresentation

let private validateReplacementPolicy
    (terminals: ReplacementTerminal list)
    (shutdownTargets: ReplacementShutdownTarget list)
    (resumeCommands: Map<string, ReplacementResumeCommand>)
    =
    let terminalsById =
        terminals
        |> List.map (fun terminal ->
            terminal.TerminalSessionId, terminal)
        |> Map.ofList

    let targetIdentities =
        shutdownTargets
        |> List.map _.ProcessIdentity

    if
        targetIdentities
        |> Set.ofList
        |> Set.count
        <> targetIdentities.Length
    then
        Error "The replacement policy returned duplicate exact process targets"
    elif
        shutdownTargets
        |> List.exists (fun target ->
            match terminalsById |> Map.tryFind target.TerminalSessionId with
            | Some terminal ->
                not (samePath terminal.WorktreePath target.WorktreePath)
            | None -> true)
    then
        Error "The replacement policy returned a process outside the captured terminal registry"
    elif
        resumeCommands
        |> Map.exists (fun terminalSessionId _ ->
            not (terminalsById.ContainsKey terminalSessionId))
    then
        Error "The replacement policy returned a command outside the captured terminal registry"
    elif
        resumeCommands
        |> Map.exists (fun terminalSessionId resume ->
            shutdownTargets
            |> List.exists (fun target ->
                target.TerminalSessionId = terminalSessionId
                && target.CopilotSessionId = resume.CopilotSessionId)
            |> not)
    then
        Error "The replacement policy returned a command without a matching exact process target"
    elif
        resumeCommands
        |> Map.exists (fun _ resume ->
            validateTerminalCommand resume.Command
            |> Result.isError)
    then
        Error "The replacement policy returned an invalid terminal command"
    else
        Ok()

let internal mutationFailureReason = function
    | MutationRejected(_, reason)
    | MutationUnverified(_, reason) -> reason

let private shutdownFailureText = function
    | SessionBridge.ShutdownFailure.MissingRegistration ->
        "missing bridge registration"
    | SessionBridge.ShutdownFailure.StaleRegistration ->
        "stale bridge registration"
    | SessionBridge.ShutdownFailure.InvalidCapability ->
        "invalid shutdown capability"
    | SessionBridge.ShutdownFailure.NonLoopbackRequest ->
        "non-loopback shutdown endpoint"
    | SessionBridge.ShutdownFailure.Rejected ->
        "shutdown request rejected"
    | SessionBridge.ShutdownFailure.RequestFailed ->
        "shutdown request failed"
    | SessionBridge.ShutdownFailure.TimedOut ->
        "exact closure or process exit timed out"
    | SessionBridge.ShutdownFailure.VerificationFailed ->
        "exact closure verification failed"

let private launchFailureText = function
    | LaunchRejected error ->
        $"The staged host could not be launched: {error}"
    | LaunchStartedButUnhealthy error ->
        $"The staged host process started but did not become healthy: {error}"

let internal replacementFailureMessage = function
    | ReplacementFailure.GracefulShutdownFailed attempts ->
        let failures =
            attempts
            |> List.choose (fun attempt ->
                match attempt.Outcome with
                | Ok _ -> None
                | Error failure ->
                    let processId, startTicks =
                        ProcessIdentity.sortKey
                            attempt.Target.ProcessIdentity

                    Some(
                        $"PID {processId} at start ticks {startTicks}: {shutdownFailureText failure}"
                    ))

        let shown = failures |> List.truncate 8
        let omitted = failures.Length - shown.Length
        let suffix =
            if omitted = 0 then ""
            else $" ({omitted} additional failures omitted)"

        let detail = shown |> String.concat "; "

        $"Graceful shutdown failed for {failures.Length} of {attempts.Length} exact sessions: {detail}{suffix}"
    | ReplacementFailure.OldHostStopFailed error ->
        $"The previous TerminalHost could not be confirmed stopped: {error}"
    | ReplacementFailure.StagedHostLaunchFailed failure ->
        launchFailureText failure
    | ReplacementFailure.StagedHostVerificationFailed error ->
        $"The launched TerminalHost identity could not be verified: {error}"
    | ReplacementFailure.StagedRegistryReadFailed error ->
        $"The replacement TerminalHost registry could not be read: {error}"
    | ReplacementFailure.StagedRegistryNotEmpty terminalCount ->
        $"The replacement TerminalHost started with {terminalCount} unexpected terminals"
    | ReplacementFailure.TerminalRecreationFailed(terminal, failure) ->
        $"Could not recreate terminal {terminal.TerminalSessionId}: {mutationFailureReason failure}"
    | ReplacementFailure.CommandDeliveryFailed(terminal, error) ->
        $"Could not deliver the replacement command for terminal {terminal.TerminalSessionId}: {error}"

let internal replacementRecoveryOutcome recovery =
    ReplacementOutcome.Failed(
        recovery.Capture.StagedVersion,
        replacementFailureMessage recovery.Failure
    )

let internal replacementCommitOutcome = function
    | ReplacementCommit.KeepState outcome
    | ReplacementCommit.ApplyRegistry(_, _, outcome) -> outcome
    | ReplacementCommit.RecoveryRequired recovery ->
        replacementRecoveryOutcome recovery

let private recoveryRequired capture progress failure =
    ReplacementCommit.RecoveryRequired
        { Capture = capture
          Progress = progress
          Failure = failure }

let private diagnosticFailureKind =
    function
    | ReplacementFailure.GracefulShutdownFailed _ ->
        LifecycleDiagnostics.ReplacementFailureKind.GracefulShutdown
    | ReplacementFailure.OldHostStopFailed _ ->
        LifecycleDiagnostics.ReplacementFailureKind.OldHostStop
    | ReplacementFailure.StagedHostLaunchFailed _ ->
        LifecycleDiagnostics.ReplacementFailureKind.StagedHostLaunch
    | ReplacementFailure.StagedHostVerificationFailed _ ->
        LifecycleDiagnostics.ReplacementFailureKind.StagedHostVerification
    | ReplacementFailure.StagedRegistryReadFailed _
    | ReplacementFailure.StagedRegistryNotEmpty _ ->
        LifecycleDiagnostics.ReplacementFailureKind.StagedRegistry
    | ReplacementFailure.TerminalRecreationFailed _ ->
        LifecycleDiagnostics.ReplacementFailureKind.TerminalRecreation
    | ReplacementFailure.CommandDeliveryFailed _ ->
        LifecycleDiagnostics.ReplacementFailureKind.CommandDelivery

let private recoveryRequiredWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    capture
    progress
    failure
    =
    diagnostics (
        LifecycleDiagnostics.Diagnostic.ReplacementTransition(
            LifecycleDiagnostics.ReplacementStage.RecoveryRequired(
                diagnosticFailureKind failure
            )
        )
    )

    recoveryRequired capture progress failure

let private recordReplacementCapture
    (diagnostics: LifecycleDiagnostics.Sink)
    (plan: ReplacementPlan)
    =
    diagnostics (
        LifecycleDiagnostics.Diagnostic.ReplacementTransition(
            LifecycleDiagnostics.ReplacementStage.Captured(
                plan.Terminals.Length,
                plan.ShutdownTargets.Length,
                plan.ResumeCommands.Count
            )
        )
    )

    let typedTargets =
        plan.ShutdownTargets
        |> List.choose (fun target ->
            target.CopilotSessionId
            |> LifecycleDiagnostics.trySessionId
            |> Option.map (fun sessionId ->
                target,
                sessionId,
                LifecycleDiagnostics.tryTerminalSessionId
                    target.TerminalSessionId))

    let sessionIds =
        typedTargets
        |> List.map (fun (_, sessionId, _) -> sessionId)
        |> List.distinct

    if sessionIds.Length > 1 then
        diagnostics (
            LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved
                { Boundary =
                    LifecycleDiagnostics.ObservationBoundary.Replacement
                  ProcessCount = typedTargets.Length
                  SessionIds = sessionIds }
        )

    typedTargets
    |> List.groupBy (fun (_, sessionId, _) -> sessionId)
    |> List.iter (fun (sessionId, targets) ->
        if targets.Length > 1 then
            diagnostics (
                LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved
                    { Boundary =
                        LifecycleDiagnostics.ObservationBoundary.Replacement
                      SessionId = sessionId
                      ProcessIdentities =
                        targets
                        |> List.map (fun (target, _, _) ->
                            target.ProcessIdentity)
                      TerminalSessionIds =
                        targets
                        |> List.choose (fun (_, _, terminalSessionId) ->
                            terminalSessionId)
                        |> List.distinct
                      UnattributedProcessCount =
                        targets
                        |> List.filter (fun (_, _, terminalSessionId) ->
                            terminalSessionId.IsNone)
                        |> List.length }
            ))

    typedTargets
    |> List.choose (fun (target, sessionId, terminalSessionId) ->
        terminalSessionId
        |> Option.map (fun terminalId ->
            terminalId, target, sessionId))
    |> List.groupBy (fun (terminalId, _, _) -> terminalId)
    |> List.iter (fun (terminalSessionId, targets) ->
        let conversations =
            targets
            |> List.map (fun (_, _, sessionId) -> sessionId)
            |> List.distinct

        if conversations.Length > 1 then
            let selected =
                plan.ResumeCommands
                |> Map.tryFind (
                    TerminalSessionId.value
                        terminalSessionId
                )
                |> Option.bind (fun resume ->
                    LifecycleDiagnostics.trySessionId
                        resume.CopilotSessionId)

            diagnostics (
                LifecycleDiagnostics.Diagnostic.TerminalConversationAnomalyObserved
                    { TerminalSessionId = terminalSessionId
                      SelectedSessionId = selected
                      RetainedSessionIds =
                        conversations
                        |> List.filter (fun sessionId ->
                            selected <> Some sessionId)
                      ProcessIdentities =
                        targets
                        |> List.map (fun (_, target, _) ->
                            target.ProcessIdentity) }
            ))

let private shutdownSessions
    (operations: ReplacementOperations)
    targets
    =
    async {
        let! attempts =
            targets
            |> List.map (fun target ->
                async {
                    let! outcome =
                        operations.ShutdownSession target

                    return
                        { Target = target
                          Outcome = outcome }
                })
            |> Async.Parallel

        return attempts |> Array.toList
    }

let private recreateTerminals
    (operations: ReplacementOperations)
    (config: Config)
    (connection: DiscoveryManifest)
    (plan: ReplacementPlan)
    progress
    =
    let rec recreate registry currentProgress = function
        | [] -> async.Return(Ok(registry, currentProgress))
        | terminal :: remaining ->
            async {
                match!
                    operations.RecreateTerminal
                        config
                        connection
                        terminal
                with
                | Error failure ->
                    return
                        Error(
                            currentProgress,
                            ReplacementFailure.TerminalRecreationFailed(
                                terminal,
                                failure
                            )
                        )
                | Ok(nextRegistry, recreated) ->
                    let recreatedTerminal =
                        { OriginalTerminalSessionId =
                            terminal.TerminalSessionId
                          NewTerminalSessionId =
                            recreated.SessionId
                          WorktreePath = terminal.WorktreePath }

                    let afterRecreation =
                        { currentProgress with
                            RecreatedTerminals =
                                currentProgress.RecreatedTerminals
                                @ [ recreatedTerminal ] }

                    match
                        plan.ResumeCommands
                        |> Map.tryFind terminal.TerminalSessionId
                    with
                    | None ->
                        return!
                            recreate
                                nextRegistry
                                afterRecreation
                                remaining
                    | Some resume ->
                        match!
                            operations.DeliverCommand
                                config
                                recreated
                                resume.Command
                        with
                        | Error error ->
                            return
                                Error(
                                    afterRecreation,
                                    ReplacementFailure.CommandDeliveryFailed(
                                        terminal,
                                        error
                                    )
                                )
                        | Ok() ->
                            let afterDelivery =
                                { afterRecreation with
                                    DeliveredCommandTerminalIds =
                                        afterRecreation.DeliveredCommandTerminalIds
                                        |> Set.add
                                            terminal.TerminalSessionId }

                            return!
                                recreate
                                    nextRegistry
                                    afterDelivery
                                    remaining
            }

    async {
        match! listTerminals config connection with
        | Error error ->
            return
                Error(
                    progress,
                    ReplacementFailure.StagedRegistryReadFailed error
                )
        | Ok registry when not registry.Terminals.IsEmpty ->
            return
                Error(
                    progress,
                    ReplacementFailure.StagedRegistryNotEmpty
                        registry.Terminals.Length
                )
        | Ok registry ->
            return! recreate registry progress plan.Terminals
    }

let private recheckReplacement
    (config: Config)
    (plan: ReplacementPlan)
    (query: ReplacementPolicyQuery)
    =
    async {
        match! discoverHost config with
        | HealthyHost connection
            when hostIdentityMatches connection plan.OldHost
                 && connection.StagedExecutableVersion = Some plan.StagedVersion ->
            match! listTerminals config connection with
            | Error error ->
                return RecheckFailed $"Could not recheck the authoritative terminal registry: {error}"
            | Ok registry
                when registry.Revision <> plan.RegistryRevision
                     || terminalPresentations registry.Terminals <> plan.Terminals ->
                return RecheckChanged
            | Ok registry ->
                match queryReplacementPolicy query registry.Terminals with
                | Error error -> return RecheckFailed error
                | Ok ReplacementSessionPlan.WaitingForIdle -> return RecheckChanged
                | Ok(
                    ReplacementSessionPlan.Ready(
                        activityEpoch,
                        shutdownTargets,
                        resumeCommands
                    )
                  )
                    when activityEpoch <> plan.ActivityEpoch
                         || shutdownTargets <> plan.ShutdownTargets
                         || resumeCommands <> plan.ResumeCommands ->
                    return RecheckChanged
                | Ok(ReplacementSessionPlan.Ready _) ->
                    match resolveProcessExecutable config connection with
                    | Error error -> return RecheckFailed error
                    | Ok executablePath
                        when samePath executablePath plan.OldExecutablePath ->
                        return ReadyToCommit connection
                    | Ok _ -> return RecheckChanged
        | HealthyHost _
        | MissingHost
        | DeadHost _ -> return RecheckChanged
        | IncompatibleHost(_, error)
        | UnusableHost error ->
            return RecheckFailed $"Could not recheck the exact TerminalHost: {error}"
    }

let internal commitReplacementWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    (operations: ReplacementOperations)
    (config: Config)
    (plan: ReplacementPlan)
    (query: ReplacementPolicyQuery)
    =
    async {
        recordReplacementCapture diagnostics plan

        diagnostics (
            LifecycleDiagnostics.Diagnostic.ReplacementTransition
                LifecycleDiagnostics.ReplacementStage.RecheckStarted
        )

        let failed error =
            ReplacementOutcome.Failed(plan.StagedVersion, error)
            |> ReplacementCommit.KeepState

        match! recheckReplacement config plan query with
        | RecheckChanged ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition
                    LifecycleDiagnostics.ReplacementStage.RaceLost
            )

            return
                ReplacementCommit.KeepState
                    ReplacementOutcome.RaceLost
        | RecheckFailed error ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition
                    LifecycleDiagnostics.ReplacementStage.RecheckFailed
            )

            return failed error
        | ReadyToCommit connection ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                    LifecycleDiagnostics.ReplacementStage.GracefulShutdownStarted(
                        plan.ShutdownTargets.Length
                    )
                )
            )

            let! shutdownAttempts =
                shutdownSessions operations plan.ShutdownTargets

            let failedShutdowns =
                shutdownAttempts
                |> List.filter (_.Outcome >> Result.isError)
                |> List.length

            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                    LifecycleDiagnostics.ReplacementStage.GracefulShutdownCompleted(
                        shutdownAttempts.Length - failedShutdowns,
                        failedShutdowns
                    )
                )
            )

            let afterShutdown =
                { ShutdownAttempts = shutdownAttempts
                  HostState = ReplacementHostState.OldHostHealthy
                  RecreatedTerminals = []
                  DeliveredCommandTerminalIds = Set.empty }

            if
                shutdownAttempts
                |> List.exists (_.Outcome >> Result.isError)
            then
                return
                    recoveryRequiredWithDiagnostics
                        diagnostics
                        plan
                        afterShutdown
                        (ReplacementFailure.GracefulShutdownFailed
                            shutdownAttempts)
            else
                let oldHostIdentity =
                    tryProcessIdentity connection

                diagnostics (
                    LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                        LifecycleDiagnostics.ReplacementStage.OldHostCloseStarted
                            oldHostIdentity
                    )
                )

                match! operations.StopHost config connection with
                | Error error ->
                    diagnostics (
                        LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                            LifecycleDiagnostics.ReplacementStage.OldHostCloseUnconfirmed
                                oldHostIdentity
                        )
                    )

                    return
                        recoveryRequiredWithDiagnostics
                            diagnostics
                            plan
                            { afterShutdown with
                                HostState =
                                    ReplacementHostState.OldHostStopUnconfirmed }
                            (ReplacementFailure.OldHostStopFailed error)
                | Ok() ->
                    diagnostics (
                        LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                            LifecycleDiagnostics.ReplacementStage.OldHostCloseConfirmed
                                oldHostIdentity
                        )
                    )

                    let withoutHost =
                        { afterShutdown with
                            HostState =
                                ReplacementHostState.NoConfirmedHost }

                    let stagedConfig =
                        configForExecutable
                            config
                            plan.StagedExecutablePath

                    diagnostics (
                        LifecycleDiagnostics.Diagnostic.ReplacementTransition
                            LifecycleDiagnostics.ReplacementStage.StagedHostLaunchStarted
                    )

                    match!
                        operations.LaunchHost stagedConfig
                    with
                    | HostLaunchFailed failure ->
                        diagnostics (
                            LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                                match failure with
                                | LaunchRejected _ ->
                                    LifecycleDiagnostics.ReplacementStage.StagedHostLaunchRejected
                                | LaunchStartedButUnhealthy _ ->
                                    LifecycleDiagnostics.ReplacementStage.StagedHostStartedUnhealthy
                            )
                        )

                        return
                            recoveryRequiredWithDiagnostics
                                diagnostics
                                plan
                                withoutHost
                                (ReplacementFailure.StagedHostLaunchFailed
                                    failure)
                    | HostLaunched replacement ->
                        diagnostics (
                            LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                                LifecycleDiagnostics.ReplacementStage.StagedHostRunning(
                                    tryProcessIdentity replacement
                                )
                            )
                        )

                        let withStagedHost =
                            { withoutHost with
                                HostState =
                                    ReplacementHostState.StagedHostRunning
                                        replacement }

                        match
                            resolveProcessExecutable
                                stagedConfig
                                replacement
                        with
                        | Error error ->
                            return
                                recoveryRequiredWithDiagnostics
                                    diagnostics
                                    plan
                                    withStagedHost
                                    (ReplacementFailure.StagedHostVerificationFailed
                                        error)
                        | Ok executable
                            when not (
                                samePath
                                    executable
                                    plan.StagedExecutablePath
                            ) ->
                            return
                                recoveryRequiredWithDiagnostics
                                    diagnostics
                                    plan
                                    withStagedHost
                                    (ReplacementFailure.StagedHostVerificationFailed
                                        "the launch published an unexpected TerminalHost executable")
                        | Ok _ ->
                            diagnostics (
                                LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                                    LifecycleDiagnostics.ReplacementStage.TerminalRecreationStarted(
                                        plan.Terminals.Length,
                                        plan.ResumeCommands.Count
                                    )
                                )
                            )

                            match!
                                recreateTerminals
                                    operations
                                    stagedConfig
                                    replacement
                                    plan
                                    withStagedHost
                            with
                            | Error(progress, failure) ->
                                return
                                    recoveryRequiredWithDiagnostics
                                        diagnostics
                                        plan
                                        progress
                                        failure
                            | Ok(registry, progress) ->
                                diagnostics (
                                    LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                                        LifecycleDiagnostics.ReplacementStage.TerminalRecreationCompleted(
                                            progress.RecreatedTerminals.Length,
                                            progress.DeliveredCommandTerminalIds.Count
                                        )
                                    )
                                )

                                diagnostics (
                                    LifecycleDiagnostics.Diagnostic.ReplacementTransition
                                        LifecycleDiagnostics.ReplacementStage.Completed
                                )

                                return
                                    ReplacementCommit.ApplyRegistry(
                                        replacement,
                                        registry,
                                        ReplacementOutcome.Replaced
                                            plan.StagedVersion
                                    )
    }

let internal commitReplacementWith =
    commitReplacementWithDiagnostics
        LifecycleDiagnostics.write

let internal tryReplaceHostIgnoring
    ignoredStagedVersion
    beforeRecheck
    query
    config
    commit
    =
    async {
        match! discoverHost config with
        | HealthyHost connection ->
            match connection.StagedExecutableVersion with
            | None -> return ReplacementOutcome.NoCandidate
            | Some stagedVersion when ignoredStagedVersion = Some stagedVersion ->
                return ReplacementOutcome.NoCandidate
            | Some stagedVersion ->
                let candidate =
                    result {
                        let! stagedExecutable =
                            config.HostStateDirectory
                            |> TerminalHostLayout.forStateDirectory
                            |> fun layout -> TerminalHostLayout.validateStagedVersion layout stagedVersion

                        let! oldExecutable = resolveProcessExecutable config connection

                        return stagedExecutable, oldExecutable
                    }

                match candidate with
                | Error error -> return ReplacementOutcome.Failed(stagedVersion, error)
                | Ok(stagedExecutable, oldExecutable)
                    when samePath oldExecutable stagedExecutable ->
                    return ReplacementOutcome.NoCandidate
                | Ok(stagedExecutable, oldExecutable) ->
                    match! listTerminals config connection with
                    | Error error ->
                        return
                            ReplacementOutcome.Failed(stagedVersion, $"Could not capture the authoritative terminal registry: {error}")
                    | Ok registry ->
                        match queryReplacementPolicy query registry.Terminals with
                        | Error error ->
                            return ReplacementOutcome.Failed(stagedVersion, error)
                        | Ok ReplacementSessionPlan.WaitingForIdle ->
                            return ReplacementOutcome.WaitingForIdle
                        | Ok(
                            ReplacementSessionPlan.Ready(
                                activityEpoch,
                                shutdownTargets,
                                resumeCommands
                            )
                          ) ->
                            let terminals =
                                terminalPresentations
                                    registry.Terminals

                            match
                                validateReplacementPolicy
                                    terminals
                                    shutdownTargets
                                    resumeCommands
                            with
                            | Error error ->
                                return
                                    ReplacementOutcome.Failed(
                                        stagedVersion,
                                        error
                                    )
                            | Ok() ->
                                let plan: ReplacementPlan =
                                    { OldHost = connection
                                      OldExecutablePath = oldExecutable
                                      StagedVersion = stagedVersion
                                      StagedExecutablePath =
                                        stagedExecutable
                                      RegistryRevision =
                                        registry.Revision
                                      Terminals = terminals
                                      ActivityEpoch = activityEpoch
                                      ShutdownTargets =
                                        shutdownTargets
                                      ResumeCommands = resumeCommands }

                                try
                                    do! beforeRecheck ()

                                    try
                                        return! commit plan query
                                    with :? TimeoutException ->
                                        return
                                            ReplacementOutcome.RaceLost
                                with error ->
                                    return
                                        ReplacementOutcome.Failed(
                                            stagedVersion,
                                            $"Could not coordinate TerminalHost replacement: {error.Message}"
                                        )
        | MissingHost
        | DeadHost _
        | IncompatibleHost _
        | UnusableHost _ ->
            return ReplacementOutcome.NoCandidate
    }

let private activeCooldown now =
    Option.filter (fun failed -> now < failed.RetryAfter)

let private nextCooldown now outcome current =
    match outcome with
    | ReplacementOutcome.Replaced _ -> None
    | ReplacementOutcome.Failed(stagedVersion, _) ->
        Some { StagedVersion = stagedVersion; RetryAfter = now + TimeSpan.FromMinutes 1.0 }
    | ReplacementOutcome.NoCandidate
    | ReplacementOutcome.WaitingForIdle
    | ReplacementOutcome.RaceLost ->
        current |> activeCooldown now

let private logOutcome = function
    | ReplacementOutcome.Replaced stagedVersion ->
        Log.log "TerminalHost" $"Replaced the host with staged version {stagedVersion} at a natural idle window"
    | ReplacementOutcome.Failed(stagedVersion, error) ->
        Log.log "TerminalHost" $"Replacement of staged version {stagedVersion} failed: {error}"
    | ReplacementOutcome.NoCandidate
    | ReplacementOutcome.WaitingForIdle
    | ReplacementOutcome.RaceLost -> ()

let internal runCoordinatorWith
    utcNow
    waitForNextPoll
    tryReplace
    (cancellationToken: System.Threading.CancellationToken)
    =
    let rec loop cooldown =
        async {
            if cancellationToken.IsCancellationRequested then
                return ()
            else
                let ignoredStagedVersion =
                    cooldown |> activeCooldown (utcNow ()) |> Option.map _.StagedVersion

                let! outcome = tryReplace ignoredStagedVersion
                logOutcome outcome

                let next = cooldown |> nextCooldown (utcNow ()) outcome
                let! keepGoing = waitForNextPoll cancellationToken

                if keepGoing then return! loop next
        }

    loop None

let private waitForNextPoll
    (cancellationToken: System.Threading.CancellationToken)
    =
    async {
        try
            do! System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 1.0, cancellationToken) |> Async.AwaitTask

            return true
        with :? OperationCanceledException ->
            return false
    }

let internal runCoordinator tryReplace cancellationToken =
    runCoordinatorWith (fun () -> DateTimeOffset.UtcNow) waitForNextPoll tryReplace cancellationToken
