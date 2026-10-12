module Server.TerminalLaunch

open System
open System.Threading
open Shared
open Server.SessionActivity

let internal startupPromptTimeout = TimeSpan.FromSeconds 30.0

type internal PromptedAgentRequest =
    { Provider: CodingToolProvider option
      Prompt: string
      Diagnostics: PromptedLaunchDiagnostics.Attempt }

let internal request provider path prompt =
    { Provider = provider
      Prompt = prompt
      Diagnostics =
        PromptedLaunchDiagnostics.create (SessionId(Guid.NewGuid().ToString())) path }

type internal PromptedAgentDependencies =
    { StartCommand:
        WorktreePath -> string -> Async<Result<EmbeddedTerminalStartResult, string>>
      CloseTerminal:
        EmbeddedTerminalId -> Async<Result<EmbeddedTerminalSnapshot, string>>
      Timeout: TimeSpan }

let internal startPromptedAgentWith dependencies (request: PromptedAgentRequest) =
    let diagnostics = request.Diagnostics
    let worktreePath = diagnostics.WorktreePath
    let operation = async {
        let command =
            CodingToolCli.build request.Provider (CodingToolCli.NewSession(SessionId.value diagnostics.SessionId))
        diagnostics.Emit(
            PromptedLaunchDiagnostics.Event.CommandStarting(
                request.Provider, command.AsShellString, request.Prompt.Length, dependencies.Timeout))

        let! attempt =
            SessionBridge.startWithPrompt
                dependencies.Timeout
                diagnostics
                request.Prompt
                (fun () ->
                    async {
                        let! result = dependencies.StartCommand worktreePath command.AsShellString
                        match result with
                        | Ok started ->
                            diagnostics.Emit(PromptedLaunchDiagnostics.Event.TerminalStarted started.TerminalId)
                        | Error error ->
                            diagnostics.Emit(
                                PromptedLaunchDiagnostics.Event.Failed(
                                    PromptedLaunchDiagnostics.Failure.TerminalStartFailed error))
                        return result
                    })

        match attempt with
        | SessionBridge.StartupResult.Accepted started -> return Ok started
        | SessionBridge.StartupResult.LaunchFailed _ ->
            return Error PromptedLaunchError.TerminalStartFailed
        | SessionBridge.StartupResult.PromptFailed(started, failure) ->
            diagnostics.Emit PromptedLaunchDiagnostics.Event.CleanupStarted
            let! cleanup =
                dependencies.CloseTerminal started.TerminalId
                |> Async.Catch

            let cleanupResult =
                match cleanup with
                | Choice1Of2 result -> result |> Result.map ignore
                | Choice2Of2 ex -> Error(ex.ToString())

            diagnostics.Emit(PromptedLaunchDiagnostics.Event.CleanupCompleted cleanupResult)

            return
                match cleanupResult with
                | Ok () -> Error(PromptedLaunchError.StartupFailed failure)
                | Error _ -> Error(PromptedLaunchError.StartupCleanupFailed(failure, started.TerminalId))
    }

    let guarded =
        async {
            try
                return! operation
            with ex ->
                diagnostics.Emit(
                    PromptedLaunchDiagnostics.Event.Failed(
                        PromptedLaunchDiagnostics.Failure.Unexpected ex))
                return Error PromptedLaunchError.Unexpected
        }

    async {
        // A disconnected caller must not abandon startup acceptance or exact-terminal cleanup.
        return!
            Async.StartAsTask(guarded, cancellationToken = CancellationToken.None)
            |> Async.AwaitTask
    }

type internal Operations =
    { OpenNativeTerminal: WorktreePath -> Async<Result<unit, string>>
      StartEmbeddedTerminal: WorktreePath -> Async<Result<EmbeddedTerminalStartResult, string>>
      StartEmbeddedCommand:
        WorktreePath ->
        string ->
        Async<Result<EmbeddedTerminalStartResult, string>>
      StartPromptedAgent:
        PromptedAgentRequest ->
        Async<Result<EmbeddedTerminalStartResult, PromptedLaunchError>> }

let internal start (operations: Operations) provider path prompt =
    operations.StartPromptedAgent(request provider path prompt)

let internal create sessionAgent embeddedTerminal prepareSessionClose : Operations =
    let prompted =
        { StartCommand = EmbeddedTerminal.startWithCommand embeddedTerminal
          CloseTerminal =
            WorktreeCleanup.closeEmbeddedTerminalWith prepareSessionClose embeddedTerminal
          Timeout = startupPromptTimeout }

    { OpenNativeTerminal = SessionManager.spawnTerminal sessionAgent
      StartEmbeddedTerminal = EmbeddedTerminal.start embeddedTerminal
      StartEmbeddedCommand = prompted.StartCommand
      StartPromptedAgent = startPromptedAgentWith prompted }
