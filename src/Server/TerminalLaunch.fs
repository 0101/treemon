module Server.TerminalLaunch

open System
open System.Threading
open Shared
open Server.SessionActivity

let internal startupPromptTimeout = TimeSpan.FromSeconds 30.0

type internal PromptedAgentDependencies =
    { StartCommand:
        WorktreePath -> string -> Async<Result<EmbeddedTerminalStartResult, string>>
      CloseTerminal:
        EmbeddedTerminalId -> Async<Result<EmbeddedTerminalSnapshot, string>>
      Timeout: TimeSpan }

let internal startPromptedAgentWith dependencies sessionId provider worktreePath prompt =
    let operation = async {
        let command =
            CodingToolCli.build provider (CodingToolCli.NewSession(SessionId.value sessionId))

        let! attempt =
            SessionBridge.startWithPrompt
                dependencies.Timeout
                (WorktreePath.value worktreePath)
                sessionId
                prompt
                (fun () -> dependencies.StartCommand worktreePath command.AsShellString)

        match attempt with
        | SessionBridge.StartupResult.Accepted started -> return Ok started
        | SessionBridge.StartupResult.LaunchFailed error -> return Error error
        | SessionBridge.StartupResult.PromptFailed(started, failure) ->
            let error =
                match failure with
                | SessionBridge.StartupPromptFailure.TimedOut ->
                    "Timed out waiting for Copilot's Treemon extension to accept the startup prompt."
                | SessionBridge.StartupPromptFailure.Rejected ->
                    "Copilot's Treemon extension rejected the startup prompt."

            let! cleanup =
                dependencies.CloseTerminal started.TerminalId
                |> Async.Catch

            match cleanup with
            | Choice1Of2(Ok _) -> return Error error
            | Choice1Of2(Error cleanupError) ->
                return Error $"{error} Could not close the new terminal: {cleanupError}"
            | Choice2Of2 ex ->
                Log.logException "TerminalLaunch" "Failed to close a rejected prompted launch" ex
                return Error $"{error} Could not close the new terminal."
    }

    async {
        // A disconnected caller must not abandon startup acceptance or exact-terminal cleanup.
        return!
            Async.StartAsTask(operation, cancellationToken = CancellationToken.None)
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
        CodingToolProvider option ->
        WorktreePath ->
        string ->
        Async<Result<EmbeddedTerminalStartResult, string>> }

let internal create sessionAgent embeddedTerminal prepareSessionClose : Operations =
    let prompted =
        { StartCommand = EmbeddedTerminal.startWithCommand embeddedTerminal
          CloseTerminal =
            WorktreeCleanup.closeEmbeddedTerminalWith prepareSessionClose embeddedTerminal
          Timeout = startupPromptTimeout }

    { OpenNativeTerminal = SessionManager.spawnTerminal sessionAgent
      StartEmbeddedTerminal = EmbeddedTerminal.start embeddedTerminal
      StartEmbeddedCommand = prompted.StartCommand
      StartPromptedAgent =
        fun provider path prompt ->
            async {
                let sessionId = SessionId(Guid.NewGuid().ToString())
                return! startPromptedAgentWith prompted sessionId provider path prompt
            } }
