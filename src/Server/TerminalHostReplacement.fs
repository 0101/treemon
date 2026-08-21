module Server.TerminalHostReplacement

open System
open FsToolkit.ErrorHandling
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Treemon.TerminalHosting

type internal ReplacementTerminal =
    { TerminalSessionId: string
      WorktreePath: string }

[<RequireQualifiedAccess>]
type internal ReplacementSessionPlan =
    | WaitingForIdle
    | Ready of activityEpoch: int64 * resumeCommands: Map<string, string>

type internal ReplacementPolicyQuery =
    DateTimeOffset
        -> ReplacementTerminal list
        -> Result<ReplacementSessionPlan, string>

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
      Terminals: TerminalRecord list
      ActivityEpoch: int64 }

type private FailedVersionCooldown =
    { StagedVersion: string
      RetryAfter: DateTimeOffset }

type private HostLaunchOutcome =
    | LaunchRejected of string
    | LaunchStartedButUnhealthy of string
    | HostLaunched of DiscoveryManifest

type private ReplacementRecheck =
    | ReadyToCommit of DiscoveryManifest * Map<string, string>
    | RecheckChanged
    | RecheckFailed of string

[<RequireQualifiedAccess>]
type internal ReplacementCommit =
    | KeepState of ReplacementOutcome
    | InterruptState of message: string * ReplacementOutcome
    | ApplyRegistry of DiscoveryManifest * RegistrySnapshot * ReplacementOutcome

let private stagedExecutablePath config version =
    config.HostStateDirectory
    |> TerminalHostLayout.forStateDirectory
    |> fun layout -> TerminalHostLayout.validateStagedVersion layout version

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

let private configForExecutable config executablePath =
    { config with
        HostExecutablePath = executablePath
        TtydExecutablePath =
            TerminalHostLayout.adjacentTtydExecutablePath executablePath }

let private launchHostAt config =
    async {
        match startHostProcess config with
        | Error error -> return LaunchRejected error
        | Ok() ->
            match! waitForHealthyHost config with
            | Ok connection -> return HostLaunched connection
            | Error error -> return LaunchStartedButUnhealthy error
    }

let private recreateTerminals
    (config: Config)
    (connection: DiscoveryManifest)
    (terminals: TerminalRecord list)
    (resumeCommands: Map<string, string>)
    =
    let rec recreate registry (remaining: TerminalRecord list) =
        asyncResult {
            match remaining with
            | [] -> return registry
            | previous :: tail ->
                let! nextRegistry, recreated =
                    startTerminalOnHost config connection previous.WorktreePath
                    |> AsyncResult.mapError (fun failure ->
                        let error = match failure with MutationRejected(_, reason) | MutationUnverified(_, reason) -> reason

                        $"Could not recreate the terminal for '{previous.WorktreePath}': {error}")

                match resumeCommands |> Map.tryFind previous.SessionId with
                | None -> ()
                | Some command ->
                    do!
                        config.SendTerminalCommand recreated.AttachmentEndpoint command
                        |> AsyncResult.mapError (fun error ->
                            $"Could not deliver the replacement command for '{previous.WorktreePath}': {error}")

                return! recreate nextRegistry tail
        }

    asyncResult {
        let! initial = listTerminals config connection

        if not (List.isEmpty initial.Terminals) then
            return! Error "The replacement TerminalHost did not start with an empty terminal registry"

        return! recreate initial terminals
    }

let private replacementFailure stageVersion error =
    ReplacementCommit.InterruptState(
        $"TerminalHost replacement failed: {error}",
        ReplacementOutcome.Failed(stageVersion, error)
    )

let private activateHost config expectedExecutable connection terminals resumeCommands =
    asyncResult {
        let! executable =
            resolveProcessExecutable config connection
            |> Result.mapError (fun error ->
                $"The launched TerminalHost identity could not be verified: {error}")

        if not (samePath executable expectedExecutable) then
            return! Error "The launch published an unexpected TerminalHost executable"

        let! registry = recreateTerminals config connection terminals resumeCommands
        return connection, registry
    }

let private recoverOldHost
    (config: Config)
    (plan: ReplacementPlan)
    resumeCommands
    failure
    =
    async {
        let oldConfig = configForExecutable config plan.OldExecutablePath

        let failed detail =
            replacementFailure plan.StagedVersion $"{failure}. {detail}"

        match! launchHostAt oldConfig with
        | LaunchRejected recoveryError
        | LaunchStartedButUnhealthy recoveryError ->
            return failed $"The previous host could not be restarted: {recoveryError}"
        | HostLaunched connection ->
            match!
                activateHost oldConfig plan.OldExecutablePath connection plan.Terminals resumeCommands
            with
            | Error recoveryError ->
                return failed $"The previous host restarted, but recovery failed: {recoveryError}"
            | Ok(recoveredHost, registry) ->
                let recovered =
                    $"{failure}. The previous host and its terminals were recovered."

                return
                    ReplacementCommit.ApplyRegistry(
                        recoveredHost,
                        registry,
                        ReplacementOutcome.Failed(plan.StagedVersion, recovered)
                    )
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
                     || registry.Terminals <> plan.Terminals ->
                return RecheckChanged
            | Ok registry ->
                match queryReplacementPolicy query registry.Terminals with
                | Error error -> return RecheckFailed error
                | Ok ReplacementSessionPlan.WaitingForIdle -> return RecheckChanged
                | Ok(ReplacementSessionPlan.Ready(activityEpoch, _))
                    when activityEpoch <> plan.ActivityEpoch ->
                    return RecheckChanged
                | Ok(ReplacementSessionPlan.Ready(_, resumeCommands)) ->
                    match resolveProcessExecutable config connection with
                    | Error error -> return RecheckFailed error
                    | Ok executablePath
                        when samePath executablePath plan.OldExecutablePath ->
                        return ReadyToCommit(connection, resumeCommands)
                    | Ok _ -> return RecheckChanged
        | HealthyHost _
        | MissingHost
        | DeadHost _ -> return RecheckChanged
        | IncompatibleHost(_, error)
        | UnusableHost error ->
            return RecheckFailed $"Could not recheck the exact TerminalHost: {error}"
    }

let internal commitReplacement
    (config: Config)
    (plan: ReplacementPlan)
    (query: ReplacementPolicyQuery)
    =
    async {
        let failed error =
            ReplacementOutcome.Failed(plan.StagedVersion, error)
            |> ReplacementCommit.KeepState

        try
            match! recheckReplacement config plan query with
            | RecheckChanged -> return ReplacementCommit.KeepState ReplacementOutcome.RaceLost
            | RecheckFailed error -> return failed error
            | ReadyToCommit(connection, resumeCommands) ->
                match! shutdownAndWait config connection with
                | Error error ->
                    return
                        replacementFailure plan.StagedVersion
                            $"The previous TerminalHost could not be confirmed stopped: {error}"
                | Ok() ->
                    let stagedConfig = configForExecutable config plan.StagedExecutablePath

                    match! launchHostAt stagedConfig with
                    | LaunchRejected error ->
                        return!
                            recoverOldHost config plan resumeCommands
                                $"The staged host could not be launched: {error}"
                    | LaunchStartedButUnhealthy error ->
                        return
                            replacementFailure plan.StagedVersion
                                $"The staged host process started but did not become healthy; the previous host was not restarted because the staged process could not be proven stopped: {error}"
                    | HostLaunched replacement ->
                        match!
                            activateHost stagedConfig plan.StagedExecutablePath replacement plan.Terminals resumeCommands
                        with
                        | Error error ->
                            return replacementFailure plan.StagedVersion error
                        | Ok(replacementHost, registry) ->
                            return
                                ReplacementCommit.ApplyRegistry(
                                    replacementHost,
                                    registry,
                                    ReplacementOutcome.Replaced plan.StagedVersion
                                )
        with error ->
            return
                replacementFailure plan.StagedVersion
                    $"Unexpected replacement error: {error.Message}"
    }

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
                        let! stagedExecutable = stagedExecutablePath config stagedVersion
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
                        | Ok(ReplacementSessionPlan.Ready(activityEpoch, _)) ->
                            let plan: ReplacementPlan =
                                { OldHost = connection
                                  OldExecutablePath = oldExecutable
                                  StagedVersion = stagedVersion
                                  StagedExecutablePath = stagedExecutable
                                  RegistryRevision = registry.Revision
                                  Terminals = registry.Terminals
                                  ActivityEpoch = activityEpoch }

                            try
                                do! beforeRecheck ()

                                try
                                    return! commit plan query
                                with :? TimeoutException ->
                                    return ReplacementOutcome.RaceLost
                            with error ->
                                return
                                    ReplacementOutcome.Failed(stagedVersion, $"Could not coordinate TerminalHost replacement: {error.Message}")
        | MissingHost
        | DeadHost _
        | IncompatibleHost _
        | UnusableHost _ ->
            return ReplacementOutcome.NoCandidate
    }

let private failureRetryCooldown =
    TimeSpan.FromMinutes 1.0

let private activeIgnoredVersion now cooldown =
    cooldown
    |> Option.filter (fun failed -> now < failed.RetryAfter)
    |> Option.map _.StagedVersion

let private nextCooldown now outcome current =
    match outcome with
    | ReplacementOutcome.Replaced _ -> None
    | ReplacementOutcome.Failed(stagedVersion, _) ->
        Some { StagedVersion = stagedVersion; RetryAfter = now + failureRetryCooldown }
    | ReplacementOutcome.NoCandidate
    | ReplacementOutcome.WaitingForIdle
    | ReplacementOutcome.RaceLost ->
        current
        |> Option.filter (fun failed -> now < failed.RetryAfter)

let private logOutcome outcome =
    match outcome with
    | ReplacementOutcome.Replaced stagedVersion ->
        Log.log "TerminalHost" $"Replaced the host with staged version {stagedVersion} at a natural idle window"
    | ReplacementOutcome.Failed(stagedVersion, error) ->
        Log.log "TerminalHost" $"Replacement of staged version {stagedVersion} failed: {error}"
    | ReplacementOutcome.NoCandidate
    | ReplacementOutcome.WaitingForIdle
    | ReplacementOutcome.RaceLost ->
        ()

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
                let ignoredStagedVersion = cooldown |> activeIgnoredVersion (utcNow ())

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
