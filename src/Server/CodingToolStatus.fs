module Server.CodingToolStatus

open System
open System.IO
open System.Text.Json
open Shared

type internal ProviderEntry =
    { Provider: CodingToolProvider
      GetStatus: string -> CodingToolStatus
      GetLastMessage: string -> CardEvent option
      GetLastUserMessage: string -> (string * DateTimeOffset) option
      GetSessionMtime: string -> DateTimeOffset option }

let private copilotProvider =
    { Provider = Copilot
      GetStatus = CopilotDetector.getStatus
      GetLastMessage = CopilotDetector.getLastMessage
      GetLastUserMessage = CopilotDetector.getLastUserMessage
      GetSessionMtime = CopilotDetector.getSessionMtime }

let private claudeProvider =
    { Provider = Claude
      GetStatus = ClaudeDetector.getStatus
      GetLastMessage = ClaudeDetector.getLastMessage
      GetLastUserMessage = ClaudeDetector.getLastUserMessage
      GetSessionMtime = ClaudeDetector.getSessionMtime }

let private providers = [ claudeProvider; copilotProvider ]

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
        match entry.Provider with
        | Claude ->
            let files = ClaudeDetector.enumerateFiles worktreePath
            { Provider = Claude
              Status = ClaudeDetector.getStatusFromEnumeratedFiles files
              Mtime = ClaudeDetector.getSessionMtimeFromFiles files }
        | Copilot ->
            { Provider = Copilot
              Status = entry.GetStatus worktreePath
              Mtime = entry.GetSessionMtime worktreePath })

let getStatus (worktreePath: string) : CodingToolStatus * CodingToolProvider option =
    let configured = readConfiguredProvider worktreePath
    let results = gatherResults worktreePath providers
    resolveStatus configured results

let getRefreshData (worktreePath: string) : CodingToolStatus * CodingToolProvider option * (string * DateTimeOffset) option =
    let configured = readConfiguredProvider worktreePath
    let claudeFiles = ClaudeDetector.enumerateFiles worktreePath

    let results =
        providers
        |> List.map (fun entry ->
            match entry.Provider with
            | Claude ->
                { Provider = Claude
                  Status = ClaudeDetector.getStatusFromEnumeratedFiles claudeFiles
                  Mtime = ClaudeDetector.getSessionMtimeFromFiles claudeFiles }
            | Copilot ->
                { Provider = Copilot
                  Status = entry.GetStatus worktreePath
                  Mtime = entry.GetSessionMtime worktreePath })

    let status, provider = resolveStatus configured results
    let target = configured |> Option.orElse provider

    let lastUserMsg =
        match target with
        | Some Claude ->
            ClaudeDetector.getLastUserMessageFromFiles claudeFiles
        | Some Copilot ->
            copilotProvider.GetLastUserMessage worktreePath
        | None ->
            [ ClaudeDetector.getLastUserMessageFromFiles claudeFiles
              copilotProvider.GetLastUserMessage worktreePath ]
            |> List.choose id
            |> List.tryHead

    status, provider, lastUserMsg

let getLastMessage (worktreePath: string) : CardEvent option =
    let configured = readConfiguredProvider worktreePath

    let candidates =
        match configured with
        | Some provider -> providers |> List.filter (fun e -> e.Provider = provider)
        | None -> providers

    candidates
    |> List.choose (fun entry ->
        match entry.Provider with
        | Claude ->
            ClaudeDetector.enumerateFiles worktreePath
            |> ClaudeDetector.getLastMessageFromFiles
        | Copilot ->
            entry.GetLastMessage worktreePath)
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
    |> List.choose (fun entry ->
        match entry.Provider with
        | Claude ->
            ClaudeDetector.enumerateFiles worktreePath
            |> ClaudeDetector.getLastUserMessageFromFiles
        | Copilot ->
            entry.GetLastUserMessage worktreePath)
    |> List.tryHead
