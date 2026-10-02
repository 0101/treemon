module Server.TerminalLaunch

open System
open System.Text.Json
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
        | SessionBridge.StartupResult.LaunchFailed error ->
            let path = JsonSerializer.Serialize(WorktreePath.value worktreePath)
            Log.log "TerminalLaunch" $"Prompted terminal start failed for worktree={path}: {JsonSerializer.Serialize error}"
            return Error PromptedLaunchError.TerminalStartFailed
        | SessionBridge.StartupResult.PromptFailed(started, failure) ->
            let! cleanup =
                dependencies.CloseTerminal started.TerminalId
                |> Async.Catch

            match cleanup with
            | Choice1Of2(Ok _) -> return Error(PromptedLaunchError.StartupFailed failure)
            | Choice1Of2(Error cleanupError) ->
                Log.log
                    "TerminalLaunch"
                    $"Prompted launch cleanup failed for terminal={EmbeddedTerminalId.value started.TerminalId}: {JsonSerializer.Serialize cleanupError}"
                return
                    Error(
                        PromptedLaunchError.StartupCleanupFailed(
                            failure,
                            started.TerminalId))
            | Choice2Of2 ex ->
                Log.logException
                    "TerminalLaunch"
                    $"Failed to close prompted launch terminal={EmbeddedTerminalId.value started.TerminalId}"
                    ex
                return
                    Error(
                        PromptedLaunchError.StartupCleanupFailed(
                            failure,
                            started.TerminalId))
    }

    let guarded =
        async {
            try
                return! operation
            with ex ->
                let path = JsonSerializer.Serialize(WorktreePath.value worktreePath)
                Log.logException "TerminalLaunch" $"Prompted launch failed for worktree={path}" ex
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
        CodingToolProvider option ->
        WorktreePath ->
        string ->
        Async<Result<EmbeddedTerminalStartResult, PromptedLaunchError>> }

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
