module Server.TerminalHostReplacement

open System
open System.IO
open FsToolkit.ErrorHandling
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Treemon.TerminalHosting

type internal ReplacementActivityQuery =
    DateTimeOffset
        -> Set<SessionActivity.TerminalSessionId>
        -> Result<SessionActivity.OwnedSessionSnapshot, string>

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

type private HostLaunchOutcome =
    | LaunchRejected of string
    | LaunchStartedButUnhealthy of string
    | HostLaunched of DiscoveryManifest

type private ReplacementRecheck =
    | ReadyToCommit of DiscoveryManifest * SessionActivity.OwnedSessionSnapshot
    | RecheckChanged
    | RecheckFailed of string

[<RequireQualifiedAccess>]
type internal ReplacementCommit =
    | KeepState of ReplacementOutcome
    | InterruptState of message: string * ReplacementOutcome
    | ApplyRegistry of DiscoveryManifest * RegistrySnapshot * ReplacementOutcome

let private stagedExecutablePath config version =
    try
        let layout =
            TerminalHostLayout.forStateDirectory config.HostStateDirectory

        let stagingRoot =
            layout.StagingDirectory
            |> Path.TrimEndingDirectorySeparator

        let directory =
            TerminalHostLayout.versionDirectory layout version
            |> Path.GetFullPath
            |> Path.TrimEndingDirectorySeparator
            |> DirectoryInfo

        let hasExactParent =
            directory.Parent
            |> Option.ofObj
            |> Option.exists (fun parent ->
                samePath
                    (parent.FullName
                     |> Path.GetFullPath
                     |> Path.TrimEndingDirectorySeparator)
                    stagingRoot)

        let executable =
            Path.Combine(directory.FullName, layout.HostExecutableName)

        let executableInfo = FileInfo executable
        let invalidBundleMember =
            layout.RequiredBundleFileNames
            |> List.map (fun name -> FileInfo(Path.Combine(directory.FullName, name)))
            |> List.tryFind (fun info ->
                not info.Exists
                || (info.Attributes &&& FileAttributes.ReparsePoint) <> enum 0)

        if
            not (TerminalHostLayout.isValidVersionDirectoryName version)
            || directory.Name <> version
            || not hasExactParent
        then
            Error "The staged TerminalHost version is not a direct version directory"
        elif
            not directory.Exists
            || (directory.Attributes &&& FileAttributes.ReparsePoint) <> enum 0
        then
            Error "The staged TerminalHost version directory is missing or unsafe"
        elif invalidBundleMember.IsSome then
            let memberInfo = invalidBundleMember |> Option.get

            Error
                $"The staged TerminalHost bundle member is missing or unsafe at '{memberInfo.FullName}'"
        else
            Ok executableInfo.FullName
    with error ->
        Error $"Could not validate the staged TerminalHost executable: {error.Message}"

let private hasNonIdleOwnedSession
    (snapshot: SessionActivity.OwnedSessionSnapshot)
    =
    snapshot.OpenSessions
    |> List.exists (fun session ->
        match session.Status with
        | SessionActivity.SessionLevelStatus.Working
        | SessionActivity.SessionLevelStatus.WaitingForUser -> true
        | SessionActivity.SessionLevelStatus.Idle -> false)

let private queryReplacementActivity
    (query: ReplacementActivityQuery)
    (terminals: TerminalRecord list)
    : Result<SessionActivity.OwnedSessionSnapshot, string> =
    try
        terminals
        |> List.map (_.SessionId >> SessionActivity.TerminalSessionId)
        |> Set.ofList
        |> query DateTimeOffset.UtcNow
    with error ->
        Error $"Could not query terminal-owned Copilot activity: {error.Message}"

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
    (resumableSessionIds:
        Map<SessionActivity.TerminalSessionId, SessionActivity.SessionId>)
    =
    let rec recreate registry remaining =
        asyncResult {
            match remaining with
            | [] -> return registry
            | previous :: tail ->
                let! nextRegistry, recreated =
                    startTerminalOnHost config connection previous.WorktreePath
                    |> AsyncResult.mapError (fun failure ->
                        let error =
                            match failure with
                            | StartRejected(_, reason)
                            | StartUnverified reason -> reason

                        $"Could not recreate the terminal for '{previous.WorktreePath}': {error}")

                match
                    resumableSessionIds
                    |> Map.tryFind (
                        SessionActivity.TerminalSessionId previous.SessionId
                    )
                with
                | None -> ()
                | Some(SessionActivity.SessionId sessionId) ->
                    let provider =
                        CodingToolStatus.readConfiguredProvider previous.WorktreePath

                    let command =
                        CodingToolCli.build provider (CodingToolCli.Resume(Some sessionId))

                    do!
                        config.SendTerminalCommand
                            recreated.AttachmentEndpoint
                            command.AsShellString
                        |> AsyncResult.mapError (fun error ->
                            $"Could not resume the terminal-owned Copilot session for '{previous.WorktreePath}': {error}")

                return! recreate nextRegistry tail
        }

    asyncResult {
        let! initial = listTerminals config connection

        if not (List.isEmpty initial.Terminals) then
            return!
                Error
                    "The replacement TerminalHost did not start with an empty terminal registry"

        return! recreate initial terminals
    }

let private replacementFailure stageVersion error =
    ReplacementCommit.InterruptState(
        $"TerminalHost replacement failed: {error}",
        ReplacementOutcome.Failed(stageVersion, error)
    )

let private recoverOldHost
    (config: Config)
    (plan: ReplacementPlan)
    resumableSessionIds
    failure
    =
    async {
        let oldConfig =
            configForExecutable config plan.OldExecutablePath

        let failed detail =
            replacementFailure
                plan.StagedVersion
                $"{failure}. {detail}"

        match! launchHostAt oldConfig with
        | LaunchRejected recoveryError
        | LaunchStartedButUnhealthy recoveryError ->
            return failed $"The previous host could not be restarted: {recoveryError}"
        | HostLaunched connection ->
            let recover =
                asyncResult {
                    let! executablePath =
                        resolveProcessExecutable config connection
                        |> Result.mapError (fun error ->
                            $"The restarted previous host could not be verified: {error}")

                    if not (samePath executablePath plan.OldExecutablePath) then
                        return!
                            Error
                                "Recovery started an unexpected TerminalHost executable."

                    let! registry =
                        recreateTerminals
                            oldConfig
                            connection
                            plan.Terminals
                            resumableSessionIds
                        |> AsyncResult.mapError (fun error ->
                            $"The previous host restarted, but its terminals could not be recovered: {error}")

                    return connection, registry
                }

            match! recover with
            | Error recoveryError -> return failed recoveryError
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
    (query: ReplacementActivityQuery)
    =
    async {
        match! discoverHost config with
        | HealthyHost connection
            when hostIdentityMatches connection plan.OldHost
                 && connection.StagedExecutableVersion = Some plan.StagedVersion ->
            match! listTerminals config connection with
            | Error error ->
                return
                    RecheckFailed
                        $"Could not recheck the authoritative terminal registry: {error}"
            | Ok registry
                when registry.Revision <> plan.RegistryRevision
                     || registry.Terminals <> plan.Terminals ->
                return RecheckChanged
            | Ok registry ->
                match queryReplacementActivity query registry.Terminals with
                | Error error -> return RecheckFailed error
                | Ok activity
                    when activity.ActivityEpoch <> plan.ActivityEpoch
                         || hasNonIdleOwnedSession activity ->
                    return RecheckChanged
                | Ok activity ->
                    match resolveProcessExecutable config connection with
                    | Error error -> return RecheckFailed error
                    | Ok executablePath
                        when samePath executablePath plan.OldExecutablePath ->
                        return ReadyToCommit(connection, activity)
                    | Ok _ -> return RecheckChanged
        | HealthyHost _
        | MissingHost
        | DeadHost _ ->
            return RecheckChanged
        | IncompatibleHost(_, error)
        | UnusableHost error ->
            return RecheckFailed $"Could not recheck the exact TerminalHost: {error}"
    }

let internal commitReplacement
    (config: Config)
    (plan: ReplacementPlan)
    (query: ReplacementActivityQuery)
    =
    async {
        let failed error =
            ReplacementOutcome.Failed(plan.StagedVersion, error)
            |> ReplacementCommit.KeepState

        try
            match! recheckReplacement config plan query with
            | RecheckChanged ->
                return ReplacementCommit.KeepState ReplacementOutcome.RaceLost
            | RecheckFailed error -> return failed error
            | ReadyToCommit(connection, activity) ->
                match! shutdownAndWait config connection with
                | Error error ->
                    return
                        replacementFailure
                            plan.StagedVersion
                            $"The previous TerminalHost could not be confirmed stopped: {error}"
                | Ok() ->
                    let stagedConfig =
                        configForExecutable config plan.StagedExecutablePath

                    match! launchHostAt stagedConfig with
                    | LaunchRejected error ->
                        return!
                            recoverOldHost
                                config
                                plan
                                activity.ResumableSessionIds
                                $"The staged host could not be launched: {error}"
                    | LaunchStartedButUnhealthy error ->
                        return
                            replacementFailure
                                plan.StagedVersion
                                $"The staged host process started but did not become healthy; the previous host was not restarted because the staged process could not be proven stopped: {error}"
                    | HostLaunched replacement ->
                        let activate =
                            asyncResult {
                                let! executablePath =
                                    resolveProcessExecutable config replacement
                                    |> Result.mapError (fun error ->
                                        $"The staged host identity could not be verified: {error}")

                                if not (samePath executablePath plan.StagedExecutablePath) then
                                    return!
                                        Error
                                            "The staged launch published an unexpected TerminalHost executable"

                                let! registry =
                                    recreateTerminals
                                        stagedConfig
                                        replacement
                                        plan.Terminals
                                        activity.ResumableSessionIds

                                return replacement, registry
                            }

                        match! activate with
                        | Error error ->
                            return
                                replacementFailure
                                    plan.StagedVersion
                                    error
                        | Ok(replacementHost, registry) ->
                            return
                                ReplacementCommit.ApplyRegistry(
                                    replacementHost,
                                    registry,
                                    ReplacementOutcome.Replaced plan.StagedVersion
                                )
        with error ->
            return
                replacementFailure
                    plan.StagedVersion
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
                        let! stagedExecutable =
                            stagedExecutablePath config stagedVersion

                        let! oldExecutable =
                            resolveProcessExecutable config connection

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
                            ReplacementOutcome.Failed(
                                stagedVersion,
                                $"Could not capture the authoritative terminal registry: {error}"
                            )
                    | Ok registry ->
                        match queryReplacementActivity query registry.Terminals with
                        | Error error ->
                            return ReplacementOutcome.Failed(stagedVersion, error)
                        | Ok activity when hasNonIdleOwnedSession activity ->
                            return ReplacementOutcome.WaitingForIdle
                        | Ok activity ->
                            let plan: ReplacementPlan =
                                { OldHost = connection
                                  OldExecutablePath = oldExecutable
                                  StagedVersion = stagedVersion
                                  StagedExecutablePath = stagedExecutable
                                  RegistryRevision = registry.Revision
                                  Terminals = registry.Terminals
                                  ActivityEpoch = activity.ActivityEpoch }

                            try
                                do! beforeRecheck ()
                                return! commit plan query
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

let internal runCoordinator tryReplace (cancellationToken: System.Threading.CancellationToken) =
    let rec loop ignoredStagedVersion =
        async {
            if cancellationToken.IsCancellationRequested then
                return ()
            else
                let! outcome = tryReplace ignoredStagedVersion

                let nextIgnored =
                    match outcome with
                    | ReplacementOutcome.Replaced stagedVersion ->
                        Log.log
                            "TerminalHost"
                            $"Replaced the host with staged version {stagedVersion} at a natural Copilot-idle window"

                        None
                    | ReplacementOutcome.Failed(stagedVersion, error) ->
                        Log.log
                            "TerminalHost"
                            $"Replacement of staged version {stagedVersion} failed: {error}"

                        Some stagedVersion
                    | ReplacementOutcome.NoCandidate
                    | ReplacementOutcome.WaitingForIdle
                    | ReplacementOutcome.RaceLost ->
                        ignoredStagedVersion

                try
                    do!
                        System.Threading.Tasks.Task.Delay(
                            TimeSpan.FromSeconds 1.0,
                            cancellationToken
                        )
                        |> Async.AwaitTask

                    return! loop nextIgnored
                with :? OperationCanceledException ->
                    return ()
        }

    loop None
