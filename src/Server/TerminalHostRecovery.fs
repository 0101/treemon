module Server.TerminalHostRecovery

open Server.SessionActivity
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Server.TerminalHostReplacement

[<RequireQualifiedAccess>]
type internal RecoveryHostGeneration =
    | Old
    | Staged
    | Unknown

[<RequireQualifiedAccess>]
type internal RecoveryHostState =
    | Running of RecoveryHostGeneration * DiscoveryManifest
    | Stopped
    | Unresolved of RecoveryHostGeneration * DiscoveryManifest option

[<RequireQualifiedAccess>]
type internal RecoveryTerminalRegistry =
    | Exact of RegistrySnapshot
    | Unavailable of string

[<RequireQualifiedAccess>]
type internal RecoverySelectedSessionOutcome =
    | ResumeDelivered
    | ShutdownUnconfirmed of ProcessIdentity list
    | ResumeDeliveryUnconfirmed of string
    | ResumeNotAttempted of string

type internal RecoverySelectedSession =
    { OriginalTerminalSessionId: TerminalSessionId
      CurrentTerminalSessionId: TerminalSessionId option
      CopilotSessionId: SessionId
      Outcome: RecoverySelectedSessionOutcome }

[<RequireQualifiedAccess>]
type internal RecoveryUnresolvedProcess =
    | ExactProcess of ProcessIdentity
    | StartedWithoutIdentity of RecoveryHostGeneration

[<RequireQualifiedAccess>]
type internal RecoveryStatus =
    | Recovered
    | Rejected of string

type internal ReplacementRecoveryResult =
    { HostState: RecoveryHostState
      TerminalRegistry: RecoveryTerminalRegistry
      SelectedSessions: RecoverySelectedSession list
      UnresolvedProcesses: RecoveryUnresolvedProcess list
      Status: RecoveryStatus }

[<RequireQualifiedAccess>]
type internal ReplacementResolution =
    | KeepState of ReplacementOutcome
    | ApplyRegistry of DiscoveryManifest * RegistrySnapshot * ReplacementOutcome
    | ApplyRecoveredRegistry of DiscoveryManifest * RegistrySnapshot * ReplacementOutcome
    | InterruptWithHost of DiscoveryManifest * string * ReplacementOutcome
    | InterruptWithoutHost of string * ReplacementOutcome

let private emptyRegistry =
    { Revision = 0L
      Terminals = [] }

let private exactHostIdentity (manifest: DiscoveryManifest) =
    ProcessIdentity.create
        manifest.Pid
        manifest.ProcessStartTimeUtcTicks
    |> Result.defaultWith invalidOp

let private exactHostUnresolved manifest =
    [ manifest
      |> exactHostIdentity
      |> RecoveryUnresolvedProcess.ExactProcess ]

let private result hostState registry selected unresolved status =
    { HostState = hostState
      TerminalRegistry = registry
      SelectedSessions = selected
      UnresolvedProcesses = unresolved
      Status = status }

let private rejected hostState registry selected unresolved error =
    result
        hostState
        registry
        selected
        unresolved
        (RecoveryStatus.Rejected error)

let private selectedSessionsNotAttempted
    (capture: ReplacementPlan)
    reason
    (terminals: ReplacementTerminal list)
    =
    terminals
    |> List.choose (fun terminal ->
        capture.ResumeCommands
        |> Map.tryFind terminal.TerminalSessionId
        |> Option.map (fun resume ->
            { OriginalTerminalSessionId =
                terminal.TerminalSessionId
              CurrentTerminalSessionId = None
              CopilotSessionId = resume.CopilotSessionId
              Outcome =
                RecoverySelectedSessionOutcome.ResumeNotAttempted
                    reason }))

let private deliverResumeAndSelect
    (operations: ReplacementOperations)
    (config: Config)
    (terminal: ReplacementTerminal)
    (current: TerminalRecord)
    (resume: ReplacementResumeCommand)
    =
    async {
        match TerminalSessionId.create current.SessionId with
        | Error _ ->
            return
                { OriginalTerminalSessionId =
                    terminal.TerminalSessionId
                  CurrentTerminalSessionId = None
                  CopilotSessionId = resume.CopilotSessionId
                  Outcome =
                    RecoverySelectedSessionOutcome.ResumeNotAttempted
                        "TerminalHost returned an invalid terminal session identity" }
        | Ok currentTerminalSessionId ->
            let! delivery =
                operations.DeliverCommand
                    config
                    current
                    resume.Command

            let outcome =
                match delivery with
                | Ok() ->
                    RecoverySelectedSessionOutcome.ResumeDelivered
                | Error error ->
                    RecoverySelectedSessionOutcome.ResumeDeliveryUnconfirmed
                        error

            return
                { OriginalTerminalSessionId =
                    terminal.TerminalSessionId
                  CurrentTerminalSessionId =
                    Some currentTerminalSessionId
                  CopilotSessionId = resume.CopilotSessionId
                  Outcome = outcome }
    }

let private rejectUnavailable
    (recovery: ReplacementRecovery)
    generation
    manifest
    unresolved
    reason
    =
    rejected
        (RecoveryHostState.Unresolved(generation, manifest))
        (RecoveryTerminalRegistry.Unavailable reason)
        (selectedSessionsNotAttempted
            recovery.Capture
            reason
            recovery.Capture.Terminals)
        unresolved
        reason

let private startedWithoutIdentity recovery generation reason =
    rejected
        (RecoveryHostState.Unresolved(generation, None))
        (RecoveryTerminalRegistry.Unavailable reason)
        (selectedSessionsNotAttempted
            recovery.Capture
            reason
            recovery.Capture.Terminals)
        [ RecoveryUnresolvedProcess.StartedWithoutIdentity
              generation ]
        reason

let private originalRegistryMatches
    (capture: ReplacementPlan)
    (registry: RegistrySnapshot)
    =
    capture.Terminals.Length = registry.Terminals.Length
    && List.forall2
        (fun
            (expected: ReplacementTerminal)
            (actual: TerminalRecord)
            ->
            TerminalSessionId.value expected.TerminalSessionId =
                actual.SessionId
            && samePath expected.WorktreePath actual.WorktreePath)
        capture.Terminals
        registry.Terminals

let private recreatedRegistryMatches
    (capture: ReplacementPlan)
    (registry: RegistrySnapshot)
    =
    capture.Terminals.Length = registry.Terminals.Length
    && List.forall2
        (fun
            (expected: ReplacementTerminal)
            (actual: TerminalRecord)
            ->
            samePath expected.WorktreePath actual.WorktreePath)
        capture.Terminals
        registry.Terminals

let private selectedSessionFailure selected =
    selected
    |> List.tryPick (fun session ->
        match session.Outcome with
        | RecoverySelectedSessionOutcome.ResumeDeliveryUnconfirmed error
        | RecoverySelectedSessionOutcome.ResumeNotAttempted error ->
            Some error
        | RecoverySelectedSessionOutcome.ResumeDelivered
        | RecoverySelectedSessionOutcome.ShutdownUnconfirmed _ ->
            None)

let private statusFor selected explicitFailure =
    explicitFailure
    |> Option.orElseWith (fun () -> selectedSessionFailure selected)
    |> Option.map RecoveryStatus.Rejected
    |> Option.defaultValue RecoveryStatus.Recovered

let private generationForExecutable capture executablePath =
    if samePath executablePath capture.OldExecutablePath then
        RecoveryHostGeneration.Old
    elif samePath executablePath capture.StagedExecutablePath then
        RecoveryHostGeneration.Staged
    else
        RecoveryHostGeneration.Unknown

let private reportKnownHost
    config
    generation
    manifest
    selected
    unresolved
    reason
    =
    async {
        match! listTerminals config manifest with
        | Ok registry ->
            return
                rejected
                    (RecoveryHostState.Running(
                        generation,
                        manifest
                    ))
                    (RecoveryTerminalRegistry.Exact registry)
                    selected
                    unresolved
                    reason
        | Error registryError ->
            let error =
                $"{reason}; authoritative registry read failed: {registryError}"

            return
                rejected
                    (RecoveryHostState.Unresolved(
                        generation,
                        Some manifest
                    ))
                    (RecoveryTerminalRegistry.Unavailable error)
                    selected
                    unresolved
                    error
    }

let private failedShutdownsByTerminal
    (recovery: ReplacementRecovery)
    =
    recovery.Progress.ShutdownAttempts
    |> List.choose (fun attempt ->
        match attempt.Outcome with
        | Ok _ -> None
        | Error _ ->
            Some(
                attempt.Target.TerminalSessionId,
                attempt.Target.ProcessIdentity
            ))
    |> List.groupBy fst
    |> List.map (fun (terminalSessionId, failures) ->
        terminalSessionId,
        (failures |> List.map snd))
    |> Map.ofList

let private unresolvedShutdownProcesses
    (recovery: ReplacementRecovery)
    =
    recovery.Progress.ShutdownAttempts
    |> List.choose (fun attempt ->
        match attempt.Outcome with
        | Ok _ -> None
        | Error _ ->
            Some(
                RecoveryUnresolvedProcess.ExactProcess
                    attempt.Target.ProcessIdentity
            ))

let private exactClosuresByTerminal
    (recovery: ReplacementRecovery)
    =
    recovery.Progress.ShutdownAttempts
    |> List.choose (fun attempt ->
        match attempt.Outcome with
        | Ok SessionBridge.ShutdownCompletion.ExactClosure ->
            Some(
                attempt.Target.TerminalSessionId,
                attempt.Target.ProcessIdentity
            )
        | Ok SessionBridge.ShutdownCompletion.ProcessExit
        | Error _ ->
            None)
    |> List.groupBy fst
    |> List.map (fun (terminalSessionId, closures) ->
        terminalSessionId,
        (closures |> List.map snd))
    |> Map.ofList

let private unresolvedExactProcesses identities =
    identities
    |> Seq.map RecoveryUnresolvedProcess.ExactProcess
    |> Seq.toList

let private registryAfterMutationFailure
    config
    manifest
    reason
    failure
    =
    async {
        let! currentRegistry =
            listTerminals config manifest

        match currentRegistry, failure with
        | Ok exact, _ ->
            return RecoveryTerminalRegistry.Exact exact
        | Error _, MutationRejected(exact, _) ->
            return RecoveryTerminalRegistry.Exact exact
        | Error error, MutationUnverified _ ->
            return
                RecoveryTerminalRegistry.Unavailable(
                    $"{reason}; authoritative relist failed: {error}"
                )
    }

let private recoverSelectedOnExistingOldHost
    (operations: ReplacementOperations)
    (config: Config)
    (recovery: ReplacementRecovery)
    (manifest: DiscoveryManifest)
    (registry: RegistrySnapshot)
    =
    let failedByTerminal =
        failedShutdownsByTerminal recovery

    let exactClosures =
        exactClosuresByTerminal recovery

    let exactClosuresEligibleForCleanup =
        exactClosures
        |> Map.toList
        |> List.filter (fun (terminalSessionId, _) ->
            not (failedByTerminal.ContainsKey terminalSessionId)
            && recovery.Capture.ResumeCommands.ContainsKey
                terminalSessionId)
        |> List.collect snd
        |> List.distinct

    let cleanupForTerminal terminalSessionId cleanupPending =
        exactClosures
        |> Map.tryFind terminalSessionId
        |> Option.defaultValue []
        |> Set.ofList
        |> Set.intersect cleanupPending

    let prepareTerminal
        (latestRegistry: RegistrySnapshot)
        (cleanupPending: Set<ProcessIdentity>)
        (terminal: ReplacementTerminal)
        =
        async {
            match
                findTerminalById
                    (TerminalSessionId.value terminal.TerminalSessionId)
                    latestRegistry.Terminals
            with
            | None ->
                let reason =
                    $"Original terminal {TerminalSessionId.value terminal.TerminalSessionId} is missing during recovery"

                return
                    Error(
                        RecoveryTerminalRegistry.Exact latestRegistry,
                        cleanupPending,
                        reason
                    )
            | Some current ->
                let terminalCleanup =
                    cleanupForTerminal
                        terminal.TerminalSessionId
                        cleanupPending

                if Set.isEmpty terminalCleanup then
                    return
                        Ok(
                            latestRegistry,
                            current,
                            cleanupPending
                        )
                else
                    match!
                        closeTerminalOnHost
                            config
                            manifest
                            (TerminalSessionId.value
                                terminal.TerminalSessionId)
                    with
                    | Error failure ->
                        let reason =
                            $"Closed Copilot processes did not exit and terminal cleanup failed: {mutationFailureReason failure}"

                        let! currentRegistry =
                            registryAfterMutationFailure
                                config
                                manifest
                                reason
                                failure

                        return
                            Error(
                                currentRegistry,
                                cleanupPending,
                                reason
                            )
                    | Ok _ ->
                        match!
                            operations.RecreateTerminal
                                config
                                manifest
                                terminal
                        with
                        | Error failure ->
                            let reason =
                                $"Could not recreate a terminal after exact closed-process cleanup: {mutationFailureReason failure}"

                            let! currentRegistry =
                                registryAfterMutationFailure
                                    config
                                    manifest
                                    reason
                                    failure

                            return
                                Error(
                                    currentRegistry,
                                    Set.difference
                                        cleanupPending
                                        terminalCleanup,
                                    reason
                                )
                        | Ok(nextRegistry, recreated) ->
                            return
                                Ok(
                                    nextRegistry,
                                    recreated,
                                    Set.difference
                                        cleanupPending
                                        terminalCleanup
                                )
        }

    let rec deliver
        (latestRegistry: RegistrySnapshot)
        (cleanupPending: Set<ProcessIdentity>)
        (accumulated: RecoverySelectedSession list)
        (terminals: ReplacementTerminal list)
        =
        async {
            match terminals with
            | [] ->
                return
                    Ok(
                        latestRegistry,
                        cleanupPending,
                        List.rev accumulated
                    )
            | terminal :: remaining ->
                match
                    recovery.Capture.ResumeCommands
                    |> Map.tryFind terminal.TerminalSessionId,
                    failedByTerminal
                    |> Map.tryFind terminal.TerminalSessionId
                with
                | None, _ ->
                    return!
                        deliver
                            latestRegistry
                            cleanupPending
                            accumulated
                            remaining
                | Some resume, Some unresolved ->
                    let selected =
                        { OriginalTerminalSessionId =
                            terminal.TerminalSessionId
                          CurrentTerminalSessionId =
                            Some terminal.TerminalSessionId
                          CopilotSessionId = resume.CopilotSessionId
                          Outcome =
                            RecoverySelectedSessionOutcome.ShutdownUnconfirmed
                                unresolved }

                    return!
                        deliver
                            latestRegistry
                            cleanupPending
                            (selected :: accumulated)
                            remaining
                | Some resume, None ->
                    match!
                        prepareTerminal
                            latestRegistry
                            cleanupPending
                            terminal
                    with
                    | Error(currentRegistry, pending, reason) ->
                        return
                            Error(
                                currentRegistry,
                                List.rev accumulated
                                @ selectedSessionsNotAttempted
                                    recovery.Capture
                                    reason
                                    (terminal :: remaining),
                                pending,
                                reason
                            )
                    | Ok(nextRegistry, ready, pending) ->
                        let! selected =
                            deliverResumeAndSelect
                                operations
                                config
                                terminal
                                ready
                                resume

                        return!
                            deliver
                                nextRegistry
                                pending
                                (selected :: accumulated)
                                remaining
        }

    async {
        let! exitWait =
            match exactClosuresEligibleForCleanup with
            | [] -> async.Return(Ok [])
            | identities ->
                ProcessIdentityResolver.waitForExit
                    config.ProcessExitTimeout
                    config.ProbeInterval
                    config.ProcessIdentityResolver
                    identities

        let cleanupPending =
            match exitWait with
            | Ok survivors ->
                Set.intersect
                    (Set.ofList exactClosuresEligibleForCleanup)
                    (Set.ofList survivors)
            | Error _ ->
                Set.ofList exactClosuresEligibleForCleanup

        match!
            deliver
                registry
                cleanupPending
                []
                recovery.Capture.Terminals
        with
        | Error(
            currentRegistry,
            selected,
            pending,
            reason
          ) ->
            let hostState, hostUnresolved =
                match currentRegistry with
                | RecoveryTerminalRegistry.Exact _ ->
                    RecoveryHostState.Running(
                        RecoveryHostGeneration.Old,
                        manifest
                    ),
                    []
                | RecoveryTerminalRegistry.Unavailable _ ->
                    RecoveryHostState.Unresolved(
                        RecoveryHostGeneration.Old,
                        Some manifest
                    ),
                    exactHostUnresolved manifest

            return
                rejected
                    hostState
                    currentRegistry
                    selected
                    (unresolvedShutdownProcesses recovery
                     @ unresolvedExactProcesses pending
                     @ hostUnresolved)
                    reason
        | Ok(expectedRegistry, pending, selected) ->
            match! listTerminals config manifest with
            | Error error ->
                let reason =
                    $"Could not confirm the old TerminalHost registry after recovery: {error}"

                return
                    rejected
                        (RecoveryHostState.Unresolved(
                            RecoveryHostGeneration.Old,
                            Some manifest
                        ))
                        (RecoveryTerminalRegistry.Unavailable reason)
                        selected
                        (unresolvedShutdownProcesses recovery
                         @ unresolvedExactProcesses pending
                         @ exactHostUnresolved manifest)
                        reason
            | Ok finalRegistry
                when finalRegistry <> expectedRegistry ->
                let reason =
                    "The old TerminalHost registry changed during recovery"

                return
                    rejected
                        (RecoveryHostState.Running(
                            RecoveryHostGeneration.Old,
                            manifest
                        ))
                        (RecoveryTerminalRegistry.Exact finalRegistry)
                        selected
                        (unresolvedShutdownProcesses recovery
                         @ unresolvedExactProcesses pending)
                        reason
            | Ok finalRegistry ->
                return
                    result
                        (RecoveryHostState.Running(
                            RecoveryHostGeneration.Old,
                            manifest
                        ))
                        (RecoveryTerminalRegistry.Exact finalRegistry)
                        selected
                        (unresolvedShutdownProcesses recovery
                         @ unresolvedExactProcesses pending)
                        (statusFor selected None)
    }

let private recoverExistingOldHost
    (operations: ReplacementOperations)
    (config: Config)
    (recovery: ReplacementRecovery)
    =
    async {
        match! discoverHost config with
        | HealthyHost manifest
            when hostIdentityMatches
                    manifest
                    recovery.Capture.OldHost ->
            match resolveProcessExecutable config manifest with
            | Error error ->
                let reason =
                    $"Could not verify the old TerminalHost executable: {error}"

                return
                    rejectUnavailable
                        recovery
                        RecoveryHostGeneration.Old
                        (Some manifest)
                        (exactHostUnresolved manifest)
                        reason
            | Ok executablePath
                when not (
                    samePath
                        executablePath
                        recovery.Capture.OldExecutablePath
                ) ->
                let reason =
                    "The old TerminalHost executable changed before recovery"

                return!
                    reportKnownHost
                        config
                        RecoveryHostGeneration.Unknown
                        manifest
                        (selectedSessionsNotAttempted
                            recovery.Capture
                            reason
                            recovery.Capture.Terminals)
                        (exactHostUnresolved manifest)
                        reason
            | Ok _ ->
                match! listTerminals config manifest with
                | Error error ->
                    let reason =
                        $"Could not read the old TerminalHost registry: {error}"

                    return
                        rejectUnavailable
                            recovery
                            RecoveryHostGeneration.Old
                            (Some manifest)
                            (exactHostUnresolved manifest)
                            reason
                | Ok registry
                    when not (
                        originalRegistryMatches
                            recovery.Capture
                            registry
                    ) ->
                    let reason =
                        "The old TerminalHost registry no longer matches the captured presentation"

                    return
                        rejected
                            (RecoveryHostState.Running(
                                RecoveryHostGeneration.Old,
                                manifest
                            ))
                            (RecoveryTerminalRegistry.Exact registry)
                            (selectedSessionsNotAttempted
                                recovery.Capture
                                reason
                                recovery.Capture.Terminals)
                            (unresolvedShutdownProcesses recovery)
                            reason
                | Ok registry ->
                    return!
                        recoverSelectedOnExistingOldHost
                            operations
                            config
                            recovery
                            manifest
                            registry
        | HealthyHost manifest
        | IncompatibleHost(manifest, _) ->
            let reason =
                "A different live TerminalHost became authoritative during recovery"

            let generation =
                resolveProcessExecutable config manifest
                |> Result.map (
                    generationForExecutable recovery.Capture
                )
                |> Result.defaultValue
                    RecoveryHostGeneration.Unknown

            return!
                reportKnownHost
                    config
                    generation
                    manifest
                    (selectedSessionsNotAttempted
                        recovery.Capture
                        reason
                        recovery.Capture.Terminals)
                    (exactHostUnresolved manifest)
                    reason
        | MissingHost ->
            let reason =
                "The old TerminalHost discovery manifest disappeared during recovery"

            return
                rejectUnavailable
                    recovery
                    RecoveryHostGeneration.Old
                    (Some recovery.Capture.OldHost)
                    (exactHostUnresolved recovery.Capture.OldHost)
                    reason
        | DeadHost error
        | UnusableHost error ->
            let reason =
                $"The old TerminalHost is not usable during recovery: {error}"

            return
                rejectUnavailable
                    recovery
                    RecoveryHostGeneration.Old
                    (Some recovery.Capture.OldHost)
                    (exactHostUnresolved recovery.Capture.OldHost)
                    reason
    }

let private restoreTerminalsOnOldHost
    (operations: ReplacementOperations)
    (config: Config)
    (recovery: ReplacementRecovery)
    (manifest: DiscoveryManifest)
    =
    let rec restore
        (accumulated: RecoverySelectedSession list)
        (terminals: ReplacementTerminal list)
        =
        async {
            match terminals with
            | [] ->
                match! listTerminals config manifest with
                | Ok registry ->
                    return
                        RecoveryTerminalRegistry.Exact registry,
                        List.rev accumulated,
                        None
                | Error error ->
                    let reason =
                        $"Could not confirm the recovered TerminalHost registry: {error}"

                    return
                        RecoveryTerminalRegistry.Unavailable reason,
                        List.rev accumulated,
                        Some reason
            | terminal :: remaining ->
                match!
                    operations.RecreateTerminal
                        config
                        manifest
                        terminal
                with
                | Error failure ->
                    let reason =
                        mutationFailureReason failure

                    let! registry =
                        registryAfterMutationFailure
                            config
                            manifest
                            reason
                            failure

                    return
                        registry,
                        (List.rev accumulated
                         @ selectedSessionsNotAttempted
                             recovery.Capture
                             reason
                             (terminal :: remaining)),
                        Some reason
                | Ok(_, recreated) ->
                    match
                        recovery.Capture.ResumeCommands
                        |> Map.tryFind terminal.TerminalSessionId
                    with
                    | None ->
                        return! restore accumulated remaining
                    | Some resume ->
                        let! selected =
                            deliverResumeAndSelect
                                operations
                                config
                                terminal
                                recreated
                                resume

                        return!
                            restore
                                (selected :: accumulated)
                                remaining
        }

    async {
        let! registry, selected, recreationFailure =
            restore [] recovery.Capture.Terminals

        let presentationFailure =
            match registry with
            | RecoveryTerminalRegistry.Exact exact
                when not (
                    recreatedRegistryMatches
                        recovery.Capture
                        exact
                ) ->
                Some
                    "The recovered TerminalHost registry does not match the captured presentation"
            | RecoveryTerminalRegistry.Exact _ ->
                None
            | RecoveryTerminalRegistry.Unavailable error ->
                Some error

        let failure =
            recreationFailure
            |> Option.orElse presentationFailure

        let hostState, unresolved =
            match registry with
            | RecoveryTerminalRegistry.Exact _ ->
                RecoveryHostState.Running(
                    RecoveryHostGeneration.Old,
                    manifest
                ),
                []
            | RecoveryTerminalRegistry.Unavailable _ ->
                RecoveryHostState.Unresolved(
                    RecoveryHostGeneration.Old,
                    Some manifest
                ),
                exactHostUnresolved manifest

        return
            result
                hostState
                registry
                selected
                unresolved
                (statusFor selected failure)
    }

let private restoreNewOldHost
    (operations: ReplacementOperations)
    (config: Config)
    (recovery: ReplacementRecovery)
    (manifest: DiscoveryManifest)
    =
    async {
        match resolveProcessExecutable config manifest with
        | Error error ->
            let reason =
                $"Could not verify the recovered old TerminalHost executable: {error}"

            return
                rejectUnavailable
                    recovery
                    RecoveryHostGeneration.Old
                    (Some manifest)
                    (exactHostUnresolved manifest)
                    reason
        | Ok executablePath
            when not (
                samePath
                    executablePath
                    recovery.Capture.OldExecutablePath
            ) ->
            let reason =
                "Recovery launched an unexpected TerminalHost executable"

            return!
                reportKnownHost
                    config
                    RecoveryHostGeneration.Unknown
                    manifest
                    (selectedSessionsNotAttempted
                        recovery.Capture
                        reason
                        recovery.Capture.Terminals)
                    (exactHostUnresolved manifest)
                    reason
        | Ok _ ->
            match! listTerminals config manifest with
            | Error error ->
                let reason =
                    $"Could not read the recovered old TerminalHost registry: {error}"

                return
                    rejectUnavailable
                        recovery
                        RecoveryHostGeneration.Old
                        (Some manifest)
                        (exactHostUnresolved manifest)
                        reason
            | Ok registry when not registry.Terminals.IsEmpty ->
                let reason =
                    $"The recovered old TerminalHost started with {registry.Terminals.Length} unexpected terminals"

                return
                    rejected
                        (RecoveryHostState.Running(
                            RecoveryHostGeneration.Old,
                            manifest
                        ))
                        (RecoveryTerminalRegistry.Exact registry)
                        (selectedSessionsNotAttempted
                            recovery.Capture
                            reason
                            recovery.Capture.Terminals)
                        []
                        reason
            | Ok _ ->
                return!
                    restoreTerminalsOnOldHost
                        operations
                        config
                        recovery
                        manifest
    }

let private failedOldLaunch
    (config: Config)
    (recovery: ReplacementRecovery)
    failure
    =
    async {
        match readManifest config with
        | Error error ->
            let reason =
                $"{failure}; launched host identity read failed: {error}"

            return
                startedWithoutIdentity
                    recovery
                    RecoveryHostGeneration.Old
                    reason
        | Ok None ->
            let reason =
                $"{failure}; no exact process identity was published"

            return
                startedWithoutIdentity
                    recovery
                    RecoveryHostGeneration.Old
                    reason
        | Ok(Some manifest) ->
            match processIdentityMatches config manifest with
            | Ok false ->
                let reason =
                    $"{failure}; no live exact identity was published for the started host"

                return
                    startedWithoutIdentity
                        recovery
                        RecoveryHostGeneration.Old
                        reason
            | Error error ->
                let reason =
                    $"{failure}; launched host identity verification failed: {error}"

                return
                    rejectUnavailable
                        recovery
                        RecoveryHostGeneration.Old
                        (Some manifest)
                        (exactHostUnresolved manifest)
                        reason
            | Ok true ->
                return!
                    reportKnownHost
                        config
                        RecoveryHostGeneration.Old
                        manifest
                        (selectedSessionsNotAttempted
                            recovery.Capture
                            failure
                            recovery.Capture.Terminals)
                        (exactHostUnresolved manifest)
                        failure
    }

let private launchOldHost
    (operations: ReplacementOperations)
    (config: Config)
    (recovery: ReplacementRecovery)
    =
    async {
        match
            processIdentityMatches
                config
                recovery.Capture.OldHost
        with
        | Error error ->
            let reason =
                $"Could not prove the old TerminalHost stopped: {error}"

            return
                rejectUnavailable
                    recovery
                    RecoveryHostGeneration.Old
                    (Some recovery.Capture.OldHost)
                    (exactHostUnresolved recovery.Capture.OldHost)
                    reason
        | Ok true ->
            return!
                recoverExistingOldHost
                    operations
                    config
                    recovery
        | Ok false ->
            let oldConfig =
                configForExecutable
                    config
                    recovery.Capture.OldExecutablePath

            match! operations.LaunchHost oldConfig with
            | HostLaunched manifest ->
                return!
                    restoreNewOldHost
                        operations
                        oldConfig
                        recovery
                        manifest
            | HostLaunchFailed(LaunchRejected error) ->
                let reason =
                    $"The old TerminalHost could not be relaunched: {error}"

                return
                    rejected
                        RecoveryHostState.Stopped
                        (RecoveryTerminalRegistry.Exact emptyRegistry)
                        (selectedSessionsNotAttempted
                            recovery.Capture
                            reason
                            recovery.Capture.Terminals)
                        []
                        reason
            | HostLaunchFailed(
                LaunchStartedButUnhealthy error
              ) ->
                return!
                    failedOldLaunch
                        oldConfig
                        recovery
                        $"The old TerminalHost started but did not become healthy: {error}"
    }

let private selectedSessionsFromStagedProgress
    (recovery: ReplacementRecovery)
    reason
    =
    let recreatedByOriginal =
        recovery.Progress.RecreatedTerminals
        |> List.map (fun terminal ->
            terminal.OriginalTerminalSessionId,
            terminal.NewTerminalSessionId)
        |> Map.ofList

    let failedDelivery =
        match recovery.Failure with
        | ReplacementFailure.CommandDeliveryFailed(terminal, error) ->
            Some(terminal.TerminalSessionId, error)
        | _ ->
            None

    recovery.Capture.Terminals
    |> List.choose (fun terminal ->
        recovery.Capture.ResumeCommands
        |> Map.tryFind terminal.TerminalSessionId
        |> Option.map (fun resume ->
            let outcome =
                if
                    recovery.Progress.DeliveredCommandTerminalIds
                    |> Set.contains terminal.TerminalSessionId
                then
                    RecoverySelectedSessionOutcome.ResumeDelivered
                else
                    match failedDelivery with
                    | Some(terminalSessionId, error)
                        when terminalSessionId =
                             terminal.TerminalSessionId ->
                        RecoverySelectedSessionOutcome.ResumeDeliveryUnconfirmed
                            error
                    | _ ->
                        RecoverySelectedSessionOutcome.ResumeNotAttempted
                            reason

            { OriginalTerminalSessionId =
                terminal.TerminalSessionId
              CurrentTerminalSessionId =
                recreatedByOriginal
                |> Map.tryFind terminal.TerminalSessionId
              CopilotSessionId = resume.CopilotSessionId
              Outcome = outcome }))

let private recoverKnownStagedHost
    (operations: ReplacementOperations)
    (config: Config)
    (recovery: ReplacementRecovery)
    (manifest: DiscoveryManifest)
    =
    async {
        let stagedConfig =
            configForExecutable
                config
                recovery.Capture.StagedExecutablePath

        match! operations.StopHost stagedConfig manifest with
        | Ok() ->
            return! launchOldHost operations config recovery
        | Error stopError ->
            match processIdentityMatches stagedConfig manifest with
            | Ok false ->
                return! launchOldHost operations config recovery
            | Error identityError ->
                let reason =
                    $"Could not confirm the staged TerminalHost stopped: {stopError}; {identityError}"

                return
                    rejected
                        (RecoveryHostState.Unresolved(
                            RecoveryHostGeneration.Staged,
                            Some manifest
                        ))
                        (RecoveryTerminalRegistry.Unavailable reason)
                        (selectedSessionsFromStagedProgress
                            recovery
                            reason)
                        (exactHostUnresolved manifest)
                        reason
            | Ok true ->
                let reason =
                    $"Could not confirm the staged TerminalHost stopped: {stopError}"

                return!
                    reportKnownHost
                        stagedConfig
                        RecoveryHostGeneration.Staged
                        manifest
                        (selectedSessionsFromStagedProgress
                            recovery
                            reason)
                        (exactHostUnresolved manifest)
                        reason
    }

let private recoverUnhealthyStagedLaunch
    (operations: ReplacementOperations)
    (config: Config)
    (recovery: ReplacementRecovery)
    failure
    =
    let stagedConfig =
        configForExecutable
            config
            recovery.Capture.StagedExecutablePath

    async {
        match readManifest stagedConfig with
        | Error error ->
            let reason =
                $"{failure}; staged host identity read failed: {error}"

            return
                startedWithoutIdentity
                    recovery
                    RecoveryHostGeneration.Staged
                    reason
        | Ok None ->
            let reason =
                $"{failure}; no exact process identity was published"

            return
                startedWithoutIdentity
                    recovery
                    RecoveryHostGeneration.Staged
                    reason
        | Ok(Some manifest) ->
            match processIdentityMatches stagedConfig manifest with
            | Ok false ->
                let reason =
                    $"{failure}; no live exact identity was published for the started staged host"

                return
                    startedWithoutIdentity
                        recovery
                        RecoveryHostGeneration.Staged
                        reason
            | Error error ->
                let reason =
                    $"{failure}; staged host identity verification failed: {error}"

                return
                    rejectUnavailable
                        recovery
                        RecoveryHostGeneration.Staged
                        (Some manifest)
                        (exactHostUnresolved manifest)
                        reason
            | Ok true ->
                match resolveProcessExecutable stagedConfig manifest with
                | Ok executablePath
                    when samePath
                            executablePath
                            recovery.Capture.StagedExecutablePath ->
                    return!
                        recoverKnownStagedHost
                            operations
                            config
                            recovery
                            manifest
                | Error error ->
                    let reason =
                        $"{failure}; staged host executable verification failed: {error}"

                    return
                        rejectUnavailable
                            recovery
                            RecoveryHostGeneration.Staged
                            (Some manifest)
                            (exactHostUnresolved manifest)
                            reason
                | Ok _ ->
                    let reason =
                        $"{failure}; staged launch published an unexpected executable"

                    return!
                        reportKnownHost
                            stagedConfig
                            RecoveryHostGeneration.Unknown
                            manifest
                            (selectedSessionsNotAttempted
                                recovery.Capture
                                reason
                                recovery.Capture.Terminals)
                            (exactHostUnresolved manifest)
                            reason
    }

let internal recoverWith
    operations
    config
    recovery
    =
    match recovery.Progress.HostState, recovery.Failure with
    | ReplacementHostState.OldHostHealthy,
      ReplacementFailure.GracefulShutdownFailed _ ->
        recoverExistingOldHost operations config recovery
    | ReplacementHostState.OldHostStopUnconfirmed,
      ReplacementFailure.OldHostStopFailed _ ->
        async {
            match
                processIdentityMatches
                    config
                    recovery.Capture.OldHost
            with
            | Ok true ->
                return!
                    recoverExistingOldHost
                        operations
                        config
                        recovery
            | Ok false ->
                return! launchOldHost operations config recovery
            | Error error ->
                let reason =
                    $"Could not resolve the old TerminalHost after its failed stop: {error}"

                return
                    rejectUnavailable
                        recovery
                        RecoveryHostGeneration.Old
                        (Some recovery.Capture.OldHost)
                        (exactHostUnresolved recovery.Capture.OldHost)
                        reason
        }
    | ReplacementHostState.NoConfirmedHost,
      ReplacementFailure.StagedHostLaunchFailed(
          LaunchRejected _
      ) ->
        launchOldHost operations config recovery
    | ReplacementHostState.NoConfirmedHost,
      ReplacementFailure.StagedHostLaunchFailed(
          LaunchStartedButUnhealthy error
      ) ->
        recoverUnhealthyStagedLaunch
            operations
            config
            recovery
            $"The staged TerminalHost started but did not become healthy: {error}"
    | ReplacementHostState.StagedHostRunning manifest, _ ->
        recoverKnownStagedHost
            operations
            config
            recovery
            manifest
    | hostState, failure ->
        let reason =
            $"Replacement recovery received inconsistent progress {hostState} for failure {failure}"

        async.Return(
            rejectUnavailable
                recovery
                RecoveryHostGeneration.Unknown
                None
                []
                reason
        )

let internal recoveryFailureMessage recovery result =
    let originalFailure =
        replacementFailureMessage recovery.Failure

    match result.Status with
    | RecoveryStatus.Recovered ->
        $"{originalFailure}; recovery restored one authoritative TerminalHost state"
    | RecoveryStatus.Rejected recoveryError ->
        $"{originalFailure}; recovery was incomplete: {recoveryError}"

let private resolutionForRecovery recovery result =
    let error =
        recoveryFailureMessage
            recovery
            result

    let outcome =
        ReplacementOutcome.Failed(
            recovery.Capture.StagedVersion,
            error
        )

    let message = $"TerminalHost replacement failed: {error}"

    let selectedTerminalChanged =
        result.SelectedSessions
        |> List.exists (fun selected ->
            selected.CurrentTerminalSessionId
            |> Option.exists (fun current ->
                current <> selected.OriginalTerminalSessionId))

    match result.HostState, result.TerminalRegistry with
    | RecoveryHostState.Running(
        RecoveryHostGeneration.Old,
        manifest
      ),
      RecoveryTerminalRegistry.Exact registry
        when selectedTerminalChanged ->
        ReplacementResolution.ApplyRegistry(
            manifest,
            registry,
            outcome
        )
    | RecoveryHostState.Running(_, manifest),
      RecoveryTerminalRegistry.Exact registry ->
        ReplacementResolution.ApplyRecoveredRegistry(
            manifest,
            registry,
            outcome
        )
    | RecoveryHostState.Running(_, manifest),
      RecoveryTerminalRegistry.Unavailable _
    | RecoveryHostState.Unresolved(_, Some manifest), _ ->
        ReplacementResolution.InterruptWithHost(
            manifest,
            message,
            outcome
        )
    | RecoveryHostState.Stopped, _
    | RecoveryHostState.Unresolved(_, None), _ ->
        ReplacementResolution.InterruptWithoutHost(
            message,
            outcome
        )

let private diagnosticHostGeneration =
    function
    | RecoveryHostGeneration.Old ->
        LifecycleDiagnostics.HostGeneration.Old
    | RecoveryHostGeneration.Staged ->
        LifecycleDiagnostics.HostGeneration.Staged
    | RecoveryHostGeneration.Unknown ->
        LifecycleDiagnostics.HostGeneration.Unknown

let internal diagnosticSummary
    (result: ReplacementRecoveryResult)
    =
    let host =
        match result.HostState with
        | RecoveryHostState.Running(generation, manifest) ->
            LifecycleDiagnostics.RecoveryHostOutcome.Running(
                diagnosticHostGeneration generation,
                tryProcessIdentity manifest
            )
        | RecoveryHostState.Stopped ->
            LifecycleDiagnostics.RecoveryHostOutcome.Stopped
        | RecoveryHostState.Unresolved(generation, manifest) ->
            LifecycleDiagnostics.RecoveryHostOutcome.Unresolved(
                diagnosticHostGeneration generation,
                manifest |> Option.bind tryProcessIdentity
            )

    let registry =
        match result.TerminalRegistry with
        | RecoveryTerminalRegistry.Exact exact ->
            LifecycleDiagnostics.RecoveryRegistryOutcome.Exact
                exact.Terminals.Length
        | RecoveryTerminalRegistry.Unavailable _ ->
            LifecycleDiagnostics.RecoveryRegistryOutcome.Unavailable

    let selectedSessions =
        result.SelectedSessions
        |> List.map (fun selected ->
            let outcome =
                match selected.Outcome with
                | RecoverySelectedSessionOutcome.ResumeDelivered ->
                    LifecycleDiagnostics.RecoverySelectedOutcome.ResumeDelivered
                | RecoverySelectedSessionOutcome.ShutdownUnconfirmed _ ->
                    LifecycleDiagnostics.RecoverySelectedOutcome.ShutdownUnconfirmed
                | RecoverySelectedSessionOutcome.ResumeDeliveryUnconfirmed _ ->
                    LifecycleDiagnostics.RecoverySelectedOutcome.ResumeDeliveryUnconfirmed
                | RecoverySelectedSessionOutcome.ResumeNotAttempted _ ->
                    LifecycleDiagnostics.RecoverySelectedOutcome.ResumeNotAttempted

            let diagnostic:
                LifecycleDiagnostics.RecoverySelectedSessionDiagnostic =
                { OriginalTerminalSessionId =
                    selected.OriginalTerminalSessionId
                  CurrentTerminalSessionId =
                    selected.CurrentTerminalSessionId
                  SessionId = selected.CopilotSessionId
                  Outcome = outcome }

            diagnostic)

    let diagnostic: LifecycleDiagnostics.RecoveryDiagnostic =
        { Status =
            match result.Status with
            | RecoveryStatus.Recovered ->
                LifecycleDiagnostics.RecoveryStatus.Recovered
            | RecoveryStatus.Rejected _ ->
                LifecycleDiagnostics.RecoveryStatus.Rejected
          Host = host
          Registry = registry
          SelectedSessions = selectedSessions
          UnresolvedProcesses =
            result.UnresolvedProcesses
            |> List.choose (function
                | RecoveryUnresolvedProcess.ExactProcess identity ->
                    Some identity
                | RecoveryUnresolvedProcess.StartedWithoutIdentity _ ->
                    None)
            |> List.distinct
          UnidentifiedHostGenerations =
            result.UnresolvedProcesses
            |> List.choose (function
                | RecoveryUnresolvedProcess.ExactProcess _ -> None
                | RecoveryUnresolvedProcess.StartedWithoutIdentity generation ->
                    Some(diagnosticHostGeneration generation)) }

    diagnostic

let internal recoverWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    operations
    config
    recovery
    =
    async {
        diagnostics (
            LifecycleDiagnostics.Diagnostic.ReplacementTransition
                LifecycleDiagnostics.ReplacementStage.RecoveryStarted
        )

        let! result =
            recoverWith operations config recovery

        diagnostics (
            result
            |> diagnosticSummary
            |> LifecycleDiagnostics.Diagnostic.RecoveryCompleted
        )

        return result
    }

let internal resolveWithDiagnostics
    (diagnostics: LifecycleDiagnostics.Sink)
    operations
    config
    commit
    =
    async {
        match commit with
        | ReplacementCommit.KeepState outcome ->
            return ReplacementResolution.KeepState outcome
        | ReplacementCommit.ApplyRegistry(
            manifest,
            registry,
            outcome
          ) ->
            return
                ReplacementResolution.ApplyRegistry(
                    manifest,
                    registry,
                    outcome
                )
        | ReplacementCommit.RecoveryRequired recovery ->
            let! recoveryResult =
                recoverWithDiagnostics
                    diagnostics
                    operations
                    config
                    recovery

            return
                resolutionForRecovery
                    recovery
                    recoveryResult
    }

let internal resolveWith =
    resolveWithDiagnostics LifecycleDiagnostics.write
