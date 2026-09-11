module Server.TerminalHostReplacement

open System
open FsToolkit.ErrorHandling
open Server.SessionActivity
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Treemon.TerminalHosting

type internal ReplacementTerminal =
    { TerminalSessionId: TerminalSessionId
      WorktreePath: string }

type internal ReplacementShutdownTarget =
    { TerminalSessionId: TerminalSessionId
      WorktreePath: string
      CopilotSessionId: SessionId
      ProcessIdentity: ProcessIdentity }

type internal ReplacementResumeCommand =
    { CopilotSessionId: SessionId
      Command: string }

[<RequireQualifiedAccess>]
type internal ReplacementSessionPlan =
    | WaitingForIdle
    | Ready of
        activityEpoch: int64 *
        shutdownTargets: ReplacementShutdownTarget list *
        resumeCommands: Map<TerminalSessionId, ReplacementResumeCommand>

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
      ResumeCommands: Map<TerminalSessionId, ReplacementResumeCommand> }

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
type internal ReplacementFailure =
    | GracefulShutdownFailed of ReplacementShutdownAttempt list
    | OldHostStopFailed of string
    | StagedHostLaunchFailed of HostLaunchFailure
    | StagedHostVerificationFailed of string
    | StagedRegistryReadFailed of string
    | StagedRegistryNotEmpty of terminalCount: int
    | TerminalRecreationFailed of ReplacementTerminal * TerminalMutationFailure
    | CommandDeliveryFailed of ReplacementTerminal * string

type internal ReplacementOperations =
    { ShutdownSessions:
        ReplacementShutdownTarget list
            -> Async<ReplacementShutdownAttempt list>
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

/// Final transition the lifecycle mailbox applies when a replacement attempt ends.
[<RequireQualifiedAccess>]
type internal ReplacementResolution =
    | KeepState of ReplacementOutcome
    | ApplyRegistry of DiscoveryManifest * RegistrySnapshot * ReplacementOutcome
    | InterruptWithHost of DiscoveryManifest * string * ReplacementOutcome
    | InterruptWithoutHost of string * ReplacementOutcome

let private terminalPresentation (terminal: TerminalRecord) =
    terminal.SessionId
    |> TerminalSessionId.create
    |> Result.map (fun terminalSessionId ->
        { TerminalSessionId = terminalSessionId
          WorktreePath = terminal.WorktreePath })

let private terminalPresentations terminals =
    terminals
    |> List.traverseResultM terminalPresentation

let private queryReplacementPolicy
    (query: ReplacementPolicyQuery)
    (terminals: ReplacementTerminal list)
    : Result<ReplacementSessionPlan, string> =
    try
        query DateTimeOffset.UtcNow terminals
    with error ->
        Error $"Could not query the terminal replacement policy: {error.Message}"

let private configForExecutable config executablePath =
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
    closureSnapshot
    =
    { ShutdownSessions =
        fun targets ->
            async {
                let bridgeTargets =
                    targets
                    |> List.map (fun target ->
                        ({ WorktreePath = target.WorktreePath
                           ProcessIdentity = target.ProcessIdentity }
                         : SessionBridge.ShutdownTarget))

                let! attempts =
                    SessionBridge.shutdownExactBatch
                        closureSnapshot
                        bridgeTargets

                return
                    (targets, attempts)
                    ||> List.map2 (fun target attempt ->
                        { Target = target
                          Outcome = attempt.Outcome })
            }
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

let private validateReplacementPolicy
    (terminals: ReplacementTerminal list)
    (shutdownTargets: ReplacementShutdownTarget list)
    (resumeCommands: Map<TerminalSessionId, ReplacementResumeCommand>)
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

let private mutationFailureReason = function
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
        $"Could not recreate terminal {TerminalSessionId.value terminal.TerminalSessionId}: {mutationFailureReason failure}"
    | ReplacementFailure.CommandDeliveryFailed(terminal, error) ->
        $"Could not deliver the replacement command for terminal {TerminalSessionId.value terminal.TerminalSessionId}: {error}"

let private externalRestartRequired =
    "Treemon will not start another TerminalHost generation; restart Treemon from an external PowerShell window and Resume terminals explicitly."

/// Text for a failure that happened after the old host was confirmed stopped, so replacement can
/// no longer be undone.
let private irreversibleFailureMessage error =
    $"TerminalHost replacement stopped after the previous TerminalHost exited: {error}. {externalRestartRequired}"

let private retainedStagedHostMessage error stopError =
    $"TerminalHost replacement stopped after the previous TerminalHost exited: {error}. The replacement TerminalHost could not be confirmed stopped ({stopError}) and is retained as the only known host. {externalRestartRequired}"

let private unresolvedOldHostMessage error =
    $"TerminalHost replacement stopped before a replacement TerminalHost was started: {error}. The previous TerminalHost could not be confirmed stopped or alive and is retained as the only known host. {externalRestartRequired}"

let internal replacementResolutionOutcome = function
    | ReplacementResolution.KeepState outcome
    | ReplacementResolution.ApplyRegistry(_, _, outcome)
    | ReplacementResolution.InterruptWithHost(_, _, outcome)
    | ReplacementResolution.InterruptWithoutHost(_, outcome) -> outcome

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

let private recordFailure
    (diagnostics: LifecycleDiagnostics.Sink)
    failure
    hostOutcome
    retainedHost
    =
    diagnostics (
        LifecycleDiagnostics.Diagnostic.ReplacementTransition(
            LifecycleDiagnostics.ReplacementStage.Failed(
                diagnosticFailureKind failure,
                hostOutcome,
                retainedHost |> Option.bind tryProcessIdentity
            )
        )
    )

/// A failure before the old host is confirmed stopped aborts replacement and leaves the original
/// host in place; the user retries or resumes explicitly.
let private keepOldHost diagnostics (plan: ReplacementPlan) oldHost failure =
    recordFailure
        diagnostics
        failure
        LifecycleDiagnostics.ReplacementHostOutcome.OldHostRunning
        (Some oldHost)

    ReplacementResolution.KeepState(
        ReplacementOutcome.Failed(
            plan.StagedVersion,
            replacementFailureMessage failure
        )
    )

/// Replacement is irreversible once the old host has exited: fail closed by stopping the exact
/// staged host when one is known, and never start another host generation.
let private failClosed
    diagnostics
    (operations: ReplacementOperations)
    stagedConfig
    (plan: ReplacementPlan)
    stagedHost
    failure
    =
    async {
        let error = replacementFailureMessage failure

        let noCurrentHost () =
            let message = irreversibleFailureMessage error

            recordFailure
                diagnostics
                failure
                LifecycleDiagnostics.ReplacementHostOutcome.NoHostRunning
                None

            ReplacementResolution.InterruptWithoutHost(
                message,
                ReplacementOutcome.Failed(plan.StagedVersion, message)
            )

        match stagedHost with
        | None -> return noCurrentHost ()
        | Some staged ->
            match! operations.StopHost stagedConfig staged with
            | Ok() -> return noCurrentHost ()
            | Error stopError ->
                let message = retainedStagedHostMessage error stopError

                recordFailure
                    diagnostics
                    failure
                    LifecycleDiagnostics.ReplacementHostOutcome.StagedHostRetained
                    (Some staged)

                return
                    ReplacementResolution.InterruptWithHost(
                        staged,
                        message,
                        ReplacementOutcome.Failed(plan.StagedVersion, message)
                    )
    }

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

    let sessionIds =
        plan.ShutdownTargets
        |> List.map _.CopilotSessionId
        |> List.distinct

    if sessionIds.Length > 1 then
        diagnostics (
            LifecycleDiagnostics.Diagnostic.MultipleSessionsObserved
                { Boundary =
                    LifecycleDiagnostics.ObservationBoundary.Replacement
                  ProcessCount = plan.ShutdownTargets.Length
                  SessionIds = sessionIds }
        )

    plan.ShutdownTargets
    |> List.groupBy _.CopilotSessionId
    |> List.iter (fun (sessionId, targets) ->
        if targets.Length > 1 then
            diagnostics (
                LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved
                    { Boundary =
                        LifecycleDiagnostics.ObservationBoundary.Replacement
                      SessionId = sessionId
                      ProcessIdentities =
                        targets
                        |> List.map _.ProcessIdentity
                      TerminalSessionIds =
                        targets
                        |> List.map _.TerminalSessionId
                        |> List.distinct
                      UnattributedProcessCount = 0 }
            ))

    plan.ShutdownTargets
    |> List.groupBy _.TerminalSessionId
    |> List.iter (fun (terminalSessionId, targets) ->
        let conversations =
            targets
            |> List.map _.CopilotSessionId
            |> List.distinct

        if conversations.Length > 1 then
            let selected =
                plan.ResumeCommands
                |> Map.tryFind terminalSessionId
                |> Option.map _.CopilotSessionId

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
                        |> List.map _.ProcessIdentity }
            ))

let private recreateTerminals
    (operations: ReplacementOperations)
    (config: Config)
    (connection: DiscoveryManifest)
    (plan: ReplacementPlan)
    =
    let rec recreate registry = function
        | [] -> async.Return(Ok registry)
        | terminal :: remaining ->
            async {
                match! operations.RecreateTerminal config connection terminal with
                | Error failure ->
                    return
                        Error(
                            ReplacementFailure.TerminalRecreationFailed(
                                terminal,
                                failure
                            )
                        )
                | Ok(nextRegistry, recreated) ->
                    match
                        plan.ResumeCommands
                        |> Map.tryFind terminal.TerminalSessionId
                    with
                    | None -> return! recreate nextRegistry remaining
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
                                    ReplacementFailure.CommandDeliveryFailed(
                                        terminal,
                                        error
                                    )
                                )
                        | Ok() -> return! recreate nextRegistry remaining
            }

    async {
        match! listTerminals config connection with
        | Error error ->
            return Error(ReplacementFailure.StagedRegistryReadFailed error)
        | Ok registry when not registry.Terminals.IsEmpty ->
            return
                Error(
                    ReplacementFailure.StagedRegistryNotEmpty
                        registry.Terminals.Length
                )
        | Ok registry -> return! recreate registry plan.Terminals
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
            | Ok registry ->
                match terminalPresentations registry.Terminals with
                | Error error -> return RecheckFailed error
                | Ok terminals
                    when registry.Revision <> plan.RegistryRevision
                         || terminals <> plan.Terminals ->
                    return RecheckChanged
                | Ok terminals ->
                    match queryReplacementPolicy query terminals with
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

let private tryVerifiedLiveStagedHost config expectedExecutable =
    match readManifest config with
    | Ok(Some manifest) ->
        match
            processIdentityMatches config manifest,
            resolveProcessExecutable config manifest
        with
        | Ok true, Ok executable when samePath executable expectedExecutable ->
            Some manifest
        | _ ->
            None
    | _ ->
        None

let private replaceAfterOldHostStopped
    (diagnostics: LifecycleDiagnostics.Sink)
    (operations: ReplacementOperations)
    (config: Config)
    (plan: ReplacementPlan)
    =
    async {
        let stagedConfig =
            configForExecutable config plan.StagedExecutablePath

        let failAfterStop =
            failClosed diagnostics operations stagedConfig plan

        diagnostics (
            LifecycleDiagnostics.Diagnostic.ReplacementTransition
                LifecycleDiagnostics.ReplacementStage.StagedHostLaunchStarted
        )

        match! operations.LaunchHost stagedConfig with
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

            let stagedHost =
                match failure with
                | LaunchRejected _ -> None
                | LaunchStartedButUnhealthy _ ->
                    tryVerifiedLiveStagedHost
                        stagedConfig
                        plan.StagedExecutablePath

            return!
                failAfterStop
                    stagedHost
                    (ReplacementFailure.StagedHostLaunchFailed failure)
        | HostLaunched staged ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                    LifecycleDiagnostics.ReplacementStage.StagedHostRunning(
                        tryProcessIdentity staged
                    )
                )
            )

            match resolveProcessExecutable stagedConfig staged with
            | Error error ->
                return!
                    failAfterStop
                        (Some staged)
                        (ReplacementFailure.StagedHostVerificationFailed error)
            | Ok executable
                when not (samePath executable plan.StagedExecutablePath) ->
                return!
                    failAfterStop
                        (Some staged)
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
                    recreateTerminals operations stagedConfig staged plan
                with
                | Error failure -> return! failAfterStop (Some staged) failure
                | Ok registry ->
                    diagnostics (
                        LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                            LifecycleDiagnostics.ReplacementStage.TerminalRecreationCompleted(
                                plan.Terminals.Length,
                                plan.ResumeCommands.Count
                            )
                        )
                    )

                    diagnostics (
                        LifecycleDiagnostics.Diagnostic.ReplacementTransition
                            LifecycleDiagnostics.ReplacementStage.Completed
                    )

                    return
                        ReplacementResolution.ApplyRegistry(
                            staged,
                            registry,
                            ReplacementOutcome.Replaced plan.StagedVersion
                        )
    }

let private stopOldHost
    (diagnostics: LifecycleDiagnostics.Sink)
    (operations: ReplacementOperations)
    (config: Config)
    (plan: ReplacementPlan)
    (connection: DiscoveryManifest)
    =
    async {
        let oldHostIdentity = tryProcessIdentity connection

        diagnostics (
            LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                LifecycleDiagnostics.ReplacementStage.OldHostCloseStarted
                    oldHostIdentity
            )
        )

        match! operations.StopHost config connection with
        | Ok() ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                    LifecycleDiagnostics.ReplacementStage.OldHostCloseConfirmed
                        oldHostIdentity
                )
            )

            return!
                replaceAfterOldHostStopped diagnostics operations config plan
        | Error error ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                    LifecycleDiagnostics.ReplacementStage.OldHostCloseUnconfirmed
                        oldHostIdentity
                )
            )

            let failure = ReplacementFailure.OldHostStopFailed error

            match processIdentityMatches config connection with
            | Ok true ->
                return keepOldHost diagnostics plan connection failure
            | Ok false ->
                return!
                    failClosed
                        diagnostics
                        operations
                        config
                        plan
                        None
                        failure
            | Error livenessError ->
                let message =
                    unresolvedOldHostMessage
                        $"{replacementFailureMessage failure}; {livenessError}"

                recordFailure
                    diagnostics
                    failure
                    LifecycleDiagnostics.ReplacementHostOutcome.OldHostUnresolved
                    (Some connection)

                return
                    ReplacementResolution.InterruptWithHost(
                        connection,
                        message,
                        ReplacementOutcome.Failed(plan.StagedVersion, message)
                    )
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

        match! recheckReplacement config plan query with
        | RecheckChanged ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition
                    LifecycleDiagnostics.ReplacementStage.RaceLost
            )

            return
                ReplacementResolution.KeepState ReplacementOutcome.RaceLost
        | RecheckFailed error ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition
                    LifecycleDiagnostics.ReplacementStage.RecheckFailed
            )

            return
                ReplacementResolution.KeepState(
                    ReplacementOutcome.Failed(plan.StagedVersion, error)
                )
        | ReadyToCommit connection ->
            diagnostics (
                LifecycleDiagnostics.Diagnostic.ReplacementTransition(
                    LifecycleDiagnostics.ReplacementStage.GracefulShutdownStarted(
                        plan.ShutdownTargets.Length
                    )
                )
            )

            let! shutdownAttempts =
                operations.ShutdownSessions plan.ShutdownTargets

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

            if failedShutdowns > 0 then
                return
                    keepOldHost
                        diagnostics
                        plan
                        connection
                        (ReplacementFailure.GracefulShutdownFailed
                            shutdownAttempts)
            else
                return!
                    stopOldHost
                        diagnostics
                        operations
                        config
                        plan
                        connection
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
                        match terminalPresentations registry.Terminals with
                        | Error error ->
                            return ReplacementOutcome.Failed(stagedVersion, error)
                        | Ok terminals ->
                            match queryReplacementPolicy query terminals with
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
