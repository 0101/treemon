module Server.TerminalLaunch

open Shared

type internal Operations =
    { OpenNativeTerminal: WorktreePath -> Async<Result<unit, string>>
      OpenNativeTab: WorktreePath -> Async<Result<unit, string>>
      StartEmbeddedTerminal: WorktreePath -> Async<Result<EmbeddedTerminalStartResult, string>>
      StartEmbeddedCommand:
        WorktreePath ->
        string ->
        Async<Result<EmbeddedTerminalStartResult, string>> }

let internal create sessionAgent embeddedTerminal : Operations =
    { OpenNativeTerminal = SessionManager.spawnTerminal sessionAgent
      OpenNativeTab = SessionManager.openNewTab sessionAgent
      StartEmbeddedTerminal = EmbeddedTerminal.start embeddedTerminal
      StartEmbeddedCommand = EmbeddedTerminal.startWithCommand embeddedTerminal }
