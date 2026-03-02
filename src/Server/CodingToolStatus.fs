module Server.CodingToolStatus

open System
open Shared

type internal ProviderEntry =
    { Provider: CodingToolProvider
      GetStatus: string -> CodingToolStatus
      GetLastMessage: string -> CardEvent option
      GetLastUserMessage: string -> (string * DateTimeOffset) option
      GetSessionMtime: string -> DateTimeOffset option }

let private providers =
    [ { Provider = Claude
        GetStatus = ClaudeDetector.getStatus
        GetLastMessage = ClaudeDetector.getLastMessage
        GetLastUserMessage = ClaudeDetector.getLastUserMessage
        GetSessionMtime = ClaudeDetector.getSessionMtime }
      { Provider = Copilot
        GetStatus = CopilotDetector.getStatus
        GetLastMessage = CopilotDetector.getLastMessage
        GetLastUserMessage = CopilotDetector.getLastUserMessage
        GetSessionMtime = CopilotDetector.getSessionMtime } ]

let internal readConfiguredProvider (worktreePath: string) : CodingToolProvider option =
    (TreemonConfig.read worktreePath).CodingTool

type internal ProviderResult =
    { Provider: CodingToolProvider
      Status: CodingToolStatus
      Mtime: DateTimeOffset option }

let internal pickActiveProvider (results: ProviderResult list) : ProviderResult option =
    results
    |> List.filter (fun r -> r.Status <> Idle)
    |> List.sortByDescending (fun r -> r.Mtime |> Option.defaultValue DateTimeOffset.MinValue)
    |> List.tryHead

let internal resolveStatus
    (configuredProvider: CodingToolProvider option)
    (providerResults: ProviderResult list)
    : CodingToolStatus * CodingToolProvider option =

    match configuredProvider with
    | Some provider ->
        let result =
            providerResults
            |> List.tryFind (fun r -> r.Provider = provider)
            |> Option.map _.Status
            |> Option.defaultValue Idle

        result, Some provider
    | None ->
        match pickActiveProvider providerResults with
        | Some r -> r.Status, Some r.Provider
        | None -> Idle, None

let private gatherResults (worktreePath: string) (entries: ProviderEntry list) =
    entries
    |> List.map (fun entry ->
        { Provider = entry.Provider
          Status = entry.GetStatus worktreePath
          Mtime = entry.GetSessionMtime worktreePath })

let getStatus (worktreePath: string) : CodingToolStatus * CodingToolProvider option =
    let configured = readConfiguredProvider worktreePath
    let results = gatherResults worktreePath providers
    resolveStatus configured results

let getLastMessage (worktreePath: string) : CardEvent option =
    let configured = readConfiguredProvider worktreePath

    let candidates =
        match configured with
        | Some provider -> providers |> List.filter (fun e -> e.Provider = provider)
        | None -> providers

    candidates
    |> List.choose (fun entry -> entry.GetLastMessage worktreePath)
    |> List.sortByDescending _.Timestamp
    |> List.tryHead

let getLastUserMessage (worktreePath: string) (activeProvider: CodingToolProvider option) : (string * DateTimeOffset) option =
    let configured = readConfiguredProvider worktreePath
    let target = configured |> Option.orElse activeProvider

    let candidates =
        match target with
        | Some provider -> providers |> List.filter (fun e -> e.Provider = provider)
        | None -> providers

    candidates
    |> List.choose (fun entry -> entry.GetLastUserMessage worktreePath)
    |> List.tryHead

let buildInteractiveCommand (provider: CodingToolProvider option) (prompt: string) =
    let escapedPrompt = prompt.Replace("'", "''")
    match provider |> Option.defaultValue CodingToolProvider.Claude with
    | CodingToolProvider.Claude -> $"claude --dangerously-skip-permissions '{escapedPrompt}'"
    | CodingToolProvider.Copilot -> $"copilot --yolo -i '{escapedPrompt}'"
