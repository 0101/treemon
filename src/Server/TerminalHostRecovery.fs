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
    { OriginalTerminalSessionId: string
      CurrentTerminalSessionId: string option
      CopilotSessionId: string
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
    | ApplyRecovery of ReplacementRecovery * ReplacementRecoveryResult

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
            expected.TerminalSessionId = actual.SessionId
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

let private recoverSelectedOnExistingOldHost
    (operations: ReplacementOperations)
    (config: Config)
    (recovery: ReplacementRecovery)
    (manifest: DiscoveryManifest)
    (registry: RegistrySnapshot)
    =
    let failedByTerminal =
        failedShutdownsByTerminal recovery

    let rec deliver
        (accumulated: RecoverySelectedSession list)
        (terminals: ReplacementTerminal list)
        =
        async {
            match terminals with
            | [] -> return List.rev accumulated
            | terminal :: remaining ->
                match
                    recovery.Capture.ResumeCommands
                    |> Map.tryFind terminal.TerminalSessionId,
                    failedByTerminal
                    |> Map.tryFind terminal.TerminalSessionId
                with
                | None, _ ->
                    return! deliver accumulated remaining
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
                            (selected :: accumulated)
                            remaining
                | Some resume, None ->
                    match
                        findTerminalById
                            terminal.TerminalSessionId
                            registry.Terminals
                    with
                    | None ->
                        let error =
                            $"Original terminal {terminal.TerminalSessionId} is missing during recovery"

                        let selected =
                            { OriginalTerminalSessionId =
                                terminal.TerminalSessionId
                              CurrentTerminalSessionId = None
                              CopilotSessionId =
                                resume.CopilotSessionId
                              Outcome =
                                RecoverySelectedSessionOutcome.ResumeNotAttempted
                                    error }

                        return!
                            deliver
                                (selected :: accumulated)
                                remaining
                    | Some current ->
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

                        let selected =
                            { OriginalTerminalSessionId =
                                terminal.TerminalSessionId
                              CurrentTerminalSessionId =
                                Some current.SessionId
                              CopilotSessionId =
                                resume.CopilotSessionId
                              Outcome = outcome }

                        return!
                            deliver
                                (selected :: accumulated)
                                remaining
        }

    async {
        let! selected =
            deliver [] recovery.Capture.Terminals

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
                     @ exactHostUnresolved manifest)
                    reason
        | Ok finalRegistry
            when not (
                originalRegistryMatches
                    recovery.Capture
                    finalRegistry
            ) ->
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
                    (unresolvedShutdownProcesses recovery)
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
                    (unresolvedShutdownProcesses recovery)
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

                    let! currentRegistry =
                        listTerminals config manifest

                    let registry =
                        match currentRegistry, failure with
                        | Ok exact, _ ->
                            RecoveryTerminalRegistry.Exact exact
                        | Error _, MutationRejected(exact, _) ->
                            RecoveryTerminalRegistry.Exact exact
                        | Error error, MutationUnverified _ ->
                            RecoveryTerminalRegistry.Unavailable(
                                $"{reason}; authoritative relist failed: {error}"
                            )

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
                        let! delivery =
                            operations.DeliverCommand
                                config
                                recreated
                                resume.Command

                        let outcome =
                            match delivery with
                            | Ok() ->
                                RecoverySelectedSessionOutcome.ResumeDelivered
                            | Error error ->
                                RecoverySelectedSessionOutcome.ResumeDeliveryUnconfirmed
                                    error

                        let selected =
                            { OriginalTerminalSessionId =
                                terminal.TerminalSessionId
                              CurrentTerminalSessionId =
                                Some recreated.SessionId
                              CopilotSessionId =
                                resume.CopilotSessionId
                              Outcome = outcome }

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

let internal recoveryOutcome recovery result =
    let originalFailure =
        replacementFailureMessage recovery.Failure

    let error =
        match result.Status with
        | RecoveryStatus.Recovered ->
            $"{originalFailure}; recovery restored one authoritative TerminalHost state"
        | RecoveryStatus.Rejected recoveryError ->
            $"{originalFailure}; recovery was incomplete: {recoveryError}"

    ReplacementOutcome.Failed(
        recovery.Capture.StagedVersion,
        error
    )

let internal resolveWith operations config commit =
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
                recoverWith operations config recovery

            return
                ReplacementResolution.ApplyRecovery(
                    recovery,
                    recoveryResult
                )
    }

let internal resolutionOutcome = function
    | ReplacementResolution.KeepState outcome
    | ReplacementResolution.ApplyRegistry(_, _, outcome) ->
        outcome
    | ReplacementResolution.ApplyRecovery(recovery, result) ->
        recoveryOutcome recovery result
