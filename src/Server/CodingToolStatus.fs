module Server.CodingToolStatus

open System
open System.IO
open System.Text.Json
open Shared

type internal ProviderEntry =
    { Provider: CodingToolProvider
      GetStatus: string -> CodingToolStatus
      GetLastMessage: string -> CardEvent option
      GetLastUserMessage: string -> string option
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
    let configPath = Path.Combine(worktreePath, ".treemon.json")

    if not (File.Exists(configPath)) then
        None
    else
        try
            let json = File.ReadAllText(configPath)
            use doc = JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty("codingTool") with
            | true, elem ->
                match elem.GetString().ToLowerInvariant() with
                | "claude" -> Some Claude
                | "copilot" -> Some Copilot
                | other ->
                    Log.log "CodingTool" $"Unknown codingTool value '{other}' in {configPath}"
                    None
            | false, _ -> None
        with ex ->
            Log.log "CodingTool" $"Failed to read .treemon.json: {ex.Message}"
            None

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

let getLastUserMessage (worktreePath: string) : string option =
    let configured = readConfiguredProvider worktreePath

    let candidates =
        match configured with
        | Some provider -> providers |> List.filter (fun e -> e.Provider = provider)
        | None -> providers

    candidates
    |> List.choose (fun entry -> entry.GetLastUserMessage worktreePath)
    |> List.tryHead
