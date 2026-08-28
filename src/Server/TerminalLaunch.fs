module Server.TerminalLaunch

open Shared

[<RequireQualifiedAccess>]
type Intent =
    | OpenNativeTerminal
    | OpenNativeTab
    | StartEmbeddedTerminal
    | StartEmbeddedCommand of command: string

[<RequireQualifiedAccess>]
type LaunchResult =
    | Native
    | Embedded of EmbeddedTerminalStartResult

type internal Backends =
    { OpenNativeTerminal: WorktreePath -> Async<Result<unit, string>>
      OpenNativeTab: WorktreePath -> Async<Result<unit, string>>
      StartEmbedded: WorktreePath -> string option -> Async<Result<EmbeddedTerminalStartResult, string>> }

let private mapResult mapping operation =
    async {
        let! result = operation
        return result |> Result.map mapping
    }

let internal launchWith backends intent worktreePath =
    match intent with
    | Intent.OpenNativeTerminal ->
        backends.OpenNativeTerminal worktreePath
        |> mapResult (fun () -> LaunchResult.Native)
    | Intent.OpenNativeTab ->
        backends.OpenNativeTab worktreePath
        |> mapResult (fun () -> LaunchResult.Native)
    | Intent.StartEmbeddedTerminal ->
        backends.StartEmbedded worktreePath None
        |> mapResult LaunchResult.Embedded
    | Intent.StartEmbeddedCommand command ->
        backends.StartEmbedded worktreePath (Some command)
        |> mapResult LaunchResult.Embedded

let launch sessionAgent embeddedTerminal intent worktreePath =
    let backends =
        { OpenNativeTerminal = SessionManager.spawnTerminal sessionAgent
          OpenNativeTab = SessionManager.openNewTab sessionAgent
          StartEmbedded =
            fun path command ->
                match command with
                | None -> EmbeddedTerminal.start embeddedTerminal path
                | Some value ->
                    EmbeddedTerminal.startWithCommand
                        embeddedTerminal
                        path
                        value }

    launchWith backends intent worktreePath
