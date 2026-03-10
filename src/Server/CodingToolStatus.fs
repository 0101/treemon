module Server.CodingToolStatus

open System
open System.IO
open System.Text.Json
open Shared

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

type CodingToolResult =
    { Status: CodingToolStatus
      Provider: CodingToolProvider option
      LastUserMessage: (string * DateTimeOffset) option
      LastAssistantMessage: CardEvent option }

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

let private getClaudeResult (files: (FileInfo * ClaudeDetector.SessionFileKind) list) =
    { Provider = Claude
      Status = ClaudeDetector.getStatusFromEnumeratedFiles files
      Mtime = ClaudeDetector.getSessionMtimeFromFiles files }

let private gatherResultsFromFiles (worktreePath: string) (claudeFiles: (FileInfo * ClaudeDetector.SessionFileKind) list) =
    let claudeResult = getClaudeResult claudeFiles

    let copilotResult =
        { Provider = Copilot
          Status = CopilotDetector.getStatus worktreePath
          Mtime = CopilotDetector.getSessionMtime worktreePath }

    [ claudeResult; copilotResult ]

let getRefreshData (worktreePath: string) : CodingToolResult =
    let configured = readConfiguredProvider worktreePath
    let claudeFiles = ClaudeDetector.enumerateFiles worktreePath
    let results = gatherResultsFromFiles worktreePath claudeFiles

    let status, provider = resolveStatus configured results
    let target = configured |> Option.orElse provider

    let lastUserMsg =
        match target with
        | Some Claude ->
            ClaudeDetector.getLastUserMessageFromFiles claudeFiles
        | Some Copilot ->
            CopilotDetector.getLastUserMessage worktreePath
        | None ->
            let claudeMsg = ClaudeDetector.getLastUserMessageFromFiles claudeFiles
            let copilotMsg = CopilotDetector.getLastUserMessage worktreePath

            [ claudeMsg; copilotMsg ]
            |> List.choose id
            |> List.sortByDescending snd
            |> List.tryHead

    let lastAssistantMsg =
        match target with
        | Some Claude ->
            ClaudeDetector.getLastMessageFromFiles claudeFiles
        | Some Copilot ->
            CopilotDetector.getLastMessage worktreePath
        | None ->
            [ ClaudeDetector.getLastMessageFromFiles claudeFiles
              CopilotDetector.getLastMessage worktreePath ]
            |> List.choose id
            |> List.sortByDescending _.Timestamp
            |> List.tryHead

    { Status = status; Provider = provider; LastUserMessage = lastUserMsg; LastAssistantMessage = lastAssistantMsg }

let buildInteractiveCommand (provider: CodingToolProvider option) (prompt: string) =
    let escapedPrompt = prompt.Replace("'", "''")
    match provider |> Option.defaultValue CodingToolProvider.Claude with
    | CodingToolProvider.Claude -> $"claude --dangerously-skip-permissions '{escapedPrompt}'"
    | CodingToolProvider.Copilot -> $"copilot --yolo -i '{escapedPrompt}'"
