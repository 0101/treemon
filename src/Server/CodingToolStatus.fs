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

// Claude is handled explicitly to share file enumeration, so it's not in this list
let private otherProviders = [ copilotProvider ]

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

let private getClaudeResult (files: (FileInfo * ClaudeDetector.SessionFileKind) list) =
    { Provider = Claude
      Status = ClaudeDetector.getStatusFromEnumeratedFiles files
      Mtime = ClaudeDetector.getSessionMtimeFromFiles files }

let private gatherResults (worktreePath: string) (claudeFiles: (FileInfo * ClaudeDetector.SessionFileKind) list) =
    let claudeResult = getClaudeResult claudeFiles

    let otherResults =
        otherProviders
        |> List.map (fun entry ->
            { Provider = entry.Provider
              Status = entry.GetStatus worktreePath
              Mtime = entry.GetSessionMtime worktreePath })

    claudeResult :: otherResults

let getRefreshData (worktreePath: string) : CodingToolStatus * CodingToolProvider option * (string * DateTimeOffset) option =
    let configured = readConfiguredProvider worktreePath
    let claudeFiles = ClaudeDetector.enumerateFiles worktreePath
    let results = gatherResults worktreePath claudeFiles

    let status, provider = resolveStatus configured results
    let target = configured |> Option.orElse provider

    let lastUserMsg =
        match target with
        | Some Claude ->
            ClaudeDetector.getLastUserMessageFromFiles claudeFiles
        | Some Copilot ->
            copilotProvider.GetLastUserMessage worktreePath
        | None ->
            let claudeMsg = ClaudeDetector.getLastUserMessageFromFiles claudeFiles
            let copilotMsg = copilotProvider.GetLastUserMessage worktreePath

            [ claudeMsg; copilotMsg ]
            |> List.choose id
            |> List.sortByDescending snd
            |> List.tryHead

    status, provider, lastUserMsg

let getLastMessage (worktreePath: string) : CardEvent option =
    let configured = readConfiguredProvider worktreePath
    let claudeFiles = ClaudeDetector.enumerateFiles worktreePath

    let claudeMsg =
        if configured.IsNone || configured = Some Claude then
            ClaudeDetector.getLastMessageFromFiles claudeFiles
        else None

    let copilotMsg =
        if configured.IsNone || configured = Some Copilot then
            copilotProvider.GetLastMessage worktreePath
        else None

    [ claudeMsg; copilotMsg ]
    |> List.choose id
    |> List.sortByDescending _.Timestamp
    |> List.tryHead
