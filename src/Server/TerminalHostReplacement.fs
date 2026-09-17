module Server.TerminalHostReplacement

open FsToolkit.ErrorHandling
open Server.SessionActivity
open Server.TerminalHostClient
open Server.TerminalHostManifest
open Server.TerminalHostProcess
open Treemon.TerminalHosting

type internal HostedTerminal =
    { TerminalSessionId: TerminalSessionId
      WorktreePath: string }

type internal RestartSession =
    { WorktreePath: string
      Command: string }

type internal RestartSessionQuery =
    HostedTerminal list -> Result<RestartSession list, string>

type internal Operations =
    { StopHost:
        Config
            -> DiscoveryManifest
            -> Async<Result<unit, string>>
      LaunchHost:
        Config
            -> Async<Result<DiscoveryManifest, string>>
      RestartSession:
        Config
            -> DiscoveryManifest
            -> RestartSession
            -> Async<Result<RegistrySnapshot, string>>
      ReadRegistry:
        Config
            -> DiscoveryManifest
            -> Async<Result<RegistrySnapshot, string>> }

let private configForExecutable config executablePath =
    { config with
        HostExecutablePath = executablePath
        TtydExecutablePath =
            TerminalHostLayout.adjacentTtydExecutablePath executablePath }

let internal tryStagedExecutable config (host: DiscoveryManifest) =
    match host.StagedExecutableVersion with
    | None -> Ok None
    | Some stagedVersion ->
        result {
            let! stagedExecutable =
                config.HostStateDirectory
                |> TerminalHostLayout.forStateDirectory
                |> fun layout ->
                    TerminalHostLayout.validateStagedVersion
                        layout
                        stagedVersion

            let! currentExecutable =
                resolveProcessExecutable config host

            return
                if samePath currentExecutable stagedExecutable then
                    None
                else
                    Some stagedExecutable
        }

let internal updateAvailable config host =
    tryStagedExecutable config host
    |> Result.map Option.isSome

let internal hostedTerminals records =
    records
    |> List.traverseResultM (fun terminal ->
        terminal.SessionId
        |> TerminalSessionId.create
        |> Result.map (fun terminalSessionId ->
            { TerminalSessionId = terminalSessionId
              WorktreePath = terminal.WorktreePath }))

let private mutationFailureReason = function
    | MutationRejected(_, reason)
    | MutationUnverified(_, reason) -> reason

let private launchHost config =
    async {
        match startHostProcess config with
        | Error error -> return Error error
        | Ok() -> return! waitForHealthyHost config
    }

let private restartSession
    config
    host
    (session: RestartSession)
    =
    async {
        match!
            startTerminalOnHost
                config
                host
                session.WorktreePath
        with
        | Error failure ->
            return Error(mutationFailureReason failure)
        | Ok(registry, terminal) ->
            match!
                config.SendTerminalCommand
                    terminal.AttachmentEndpoint
                    session.Command
            with
            | Ok() -> return Ok registry
            | Error error -> return Error error
    }

let internal defaultOperations =
    { StopHost = shutdownAndWait
      LaunchHost = launchHost
      RestartSession = restartSession
      ReadRegistry = listTerminals }

let internal runWithOperations
    operations
    config
    currentHost
    stagedExecutable
    sessions
    =
    let stagedConfig =
        configForExecutable config stagedExecutable

    let rec restartAll host latestRegistry = function
        | [] ->
            match latestRegistry with
            | Some registry -> async.Return(Ok registry)
            | None -> operations.ReadRegistry stagedConfig host
        | session :: remaining ->
            async {
                match!
                    operations.RestartSession
                        stagedConfig
                        host
                        session
                with
                | Error error ->
                    return
                        Error
                            $"Could not restart a hosted session: {error}"
                | Ok registry ->
                    return!
                        restartAll
                            host
                            (Some registry)
                            remaining
            }

    async {
        match! operations.StopHost config currentHost with
        | Error error ->
            return
                Error
                    $"Could not stop the current TerminalHost: {error}"
        | Ok() ->
            match! operations.LaunchHost stagedConfig with
            | Error error ->
                return
                    Error
                        $"Could not start the staged TerminalHost: {error}"
            | Ok replacementHost ->
                match!
                    restartAll
                        replacementHost
                        None
                        sessions
                with
                | Error error -> return Error error
                | Ok registry ->
                    return Ok(replacementHost, registry)
    }
