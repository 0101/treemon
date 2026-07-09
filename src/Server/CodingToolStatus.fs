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
      CurrentSkill: string option
      LastUserMessage: (string * DateTimeOffset) option
      LastAssistantMessage: CardEvent option
      LastMessageProvider: CodingToolProvider option }

type internal ProviderResult =
    { Provider: CodingToolProvider
      Status: CodingToolStatus
      Mtime: DateTimeOffset option }

// Shared "active surface wins" rule: drop Idle sources, then the most recent (by mtime) wins.
// Both provider-status resolution and running-skill selection go through this so they can never
// disagree about which surface is active.
let private mostRecentActive
    (statusOf: 'a -> CodingToolStatus)
    (mtimeOf: 'a -> DateTimeOffset option)
    (items: 'a list)
    : 'a option =
    items
    |> List.filter (fun x -> statusOf x <> Idle)
    |> List.sortByDescending (fun x -> mtimeOf x |> Option.defaultValue DateTimeOffset.MinValue)
    |> List.tryHead

let internal pickActiveProvider (results: ProviderResult list) : ProviderResult option =
    results |> mostRecentActive _.Status _.Mtime

// The running skill must come from the SAME surface that wins the status resolution, not from
// whichever session file merely has the newest mtime. Mirrors pickActiveProvider (drop Idle, most
// recent wins), then scans only the winning surface for its skill via a lazy getter, so idle/losing
// surfaces are never read. All surfaces Idle -> None (-> Working via Activity.classify downstream).
let internal pickActiveSkill (surfaces: (ProviderResult * (unit -> string option)) list) : string option =
    surfaces
    |> mostRecentActive (fun (r, _) -> r.Status) (fun (r, _) -> r.Mtime)
    |> Option.bind (fun (_, getSkill) -> getSkill ())

// Uses pickActiveProvider (filter non-Idle, pick most recent) instead of tryFind
// so that when multiple providers report status, the most recently active wins.
let internal resolveStatus
    (configuredProvider: CodingToolProvider option)
    (providerResults: ProviderResult list)
    : CodingToolStatus * CodingToolProvider option =

    match configuredProvider with
    | Some provider ->
        let matching = providerResults |> List.filter (fun r -> r.Provider = provider)

        match pickActiveProvider matching with
        | Some r -> r.Status, Some provider
        | None -> Idle, Some provider
    | None ->
        match pickActiveProvider providerResults with
        | Some r -> r.Status, Some r.Provider
        | None -> Idle, None

// The three session surfaces scanned per refresh: Claude, Copilot CLI, and VS Code Copilot. Kept as
// named results so status resolution and running-skill selection reuse the SAME status/mtime reads.
type internal SessionResults =
    { Claude: ProviderResult
      CopilotCli: ProviderResult
      VsCodeCopilot: ProviderResult }

let private getClaudeResult (files: (FileInfo * ClaudeDetector.SessionFileKind) list) =
    { Provider = Claude
      Status = ClaudeDetector.getStatusFromEnumeratedFiles files
      Mtime = ClaudeDetector.getSessionMtimeFromFiles files }

let private gatherResultsFromFiles (worktreePath: string) (claudeFiles: (FileInfo * ClaudeDetector.SessionFileKind) list) : SessionResults =
    { Claude = getClaudeResult claudeFiles
      CopilotCli =
        { Provider = Copilot
          Status = CopilotDetector.getStatus worktreePath
          Mtime = CopilotDetector.getSessionMtime worktreePath }
      VsCodeCopilot =
        { Provider = Copilot
          Status = VsCodeCopilotDetector.getStatus worktreePath
          Mtime = VsCodeCopilotDetector.getSessionMtime worktreePath } }

let getRefreshData (worktreePath: string) : CodingToolResult =
    let configured = readConfiguredProvider worktreePath
    let claudeFiles = ClaudeDetector.enumerateFiles worktreePath
    let results = gatherResultsFromFiles worktreePath claudeFiles

    let status, provider =
        resolveStatus configured [ results.Claude; results.CopilotCli; results.VsCodeCopilot ]

    let target = configured |> Option.orElse provider

    let lastUserMsg, lastMsgProvider =
        match target with
        | Some Claude ->
            let msg = ClaudeDetector.getLastUserMessageFromFiles claudeFiles
            msg, msg |> Option.map (fun _ -> Claude)
        | Some Copilot ->
            let cliMsg = CopilotDetector.getLastUserMessage worktreePath
            let vsCodeMsg = VsCodeCopilotDetector.getLastUserMessage worktreePath

            let msg =
                [ cliMsg; vsCodeMsg ]
                |> List.choose id
                |> List.sortByDescending snd
                |> List.tryHead
            msg, msg |> Option.map (fun _ -> Copilot)
        | None ->
            let winner =
                [ ClaudeDetector.getLastUserMessageFromFiles claudeFiles |> Option.map (fun m -> Claude, m)
                  CopilotDetector.getLastUserMessage worktreePath |> Option.map (fun m -> Copilot, m)
                  VsCodeCopilotDetector.getLastUserMessage worktreePath |> Option.map (fun m -> Copilot, m) ]
                |> List.choose id
                |> List.sortByDescending (fun (_, (_, ts)) -> ts)
                |> List.tryHead
            winner |> Option.map snd, winner |> Option.map fst

    let lastAssistantMsg =
        match target with
        | Some Claude ->
            ClaudeDetector.getLastMessageFromFiles claudeFiles
        | Some Copilot ->
            [ CopilotDetector.getLastMessage worktreePath
              VsCodeCopilotDetector.getLastMessage worktreePath ]
            |> List.choose id
            |> List.sortByDescending _.Timestamp
            |> List.tryHead
        | None ->
            [ ClaudeDetector.getLastMessageFromFiles claudeFiles
              CopilotDetector.getLastMessage worktreePath
              VsCodeCopilotDetector.getLastMessage worktreePath ]
            |> List.choose id
            |> List.sortByDescending _.Timestamp
            |> List.tryHead

    let currentSkill =
        match target with
        | Some Claude -> ClaudeDetector.getCurrentSkillFromFiles claudeFiles
        | Some Copilot ->
            pickActiveSkill
                [ results.CopilotCli, (fun () -> CopilotDetector.getCurrentSkill worktreePath)
                  results.VsCodeCopilot, (fun () -> VsCodeCopilotDetector.getCurrentSkill worktreePath) ]
        | None -> None

    { Status = status; Provider = provider; CurrentSkill = currentSkill; LastUserMessage = lastUserMsg; LastAssistantMessage = lastAssistantMsg; LastMessageProvider = lastMsgProvider }

let configureTestsPrompt (repoRoot: string) =
    "Look at this project and determine the appropriate test command to run (e.g. 'dotnet test', 'npm test', 'pytest', etc). "
    + $"Then create or update .treemon.json at '{repoRoot}' with a \"testCommand\" field set to the full test command string. "
    + $"IMPORTANT: The config file MUST be at '{repoRoot}\\.treemon.json', not in the current directory. "
    + "For example: {\"testCommand\": \"dotnet test src/Tests/Tests.fsproj\"}"

/// Wraps an arbitrary argument in a provider-aware skill invocation.
/// Copilot uses the natural-language "use {skill} skill with {arg}" form;
/// Claude uses the "/{skill} {arg}" slash-command form. Shared by actionPrompt
/// (FixPr/FixBuild) and the worktree-create auto-launch flow so both stay byte-identical.
let skillInvocation (provider: CodingToolProvider option) (skill: string) (arg: string) =
    match provider |> Option.defaultValue CodingToolProvider.Default with
    | Copilot -> $"use {skill} skill with {arg}"
    | Claude -> $"/{skill} {arg}"

let actionPrompt (provider: CodingToolProvider option) (action: ActionKind) =
    match action with
    | FixPr url -> skillInvocation provider "pr" url
    | FixBuild url -> skillInvocation provider "fix-build" url
    | FixTests ->
        $"Please fix the failing tests. See the test failure report in {TestFailureLog.relPath} for details."
    | ConfigureTests -> configureTestsPrompt "the repo root"
    | CreatePr -> "Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"
    | CanvasSession prompt -> prompt

let getLastSessionId (provider: CodingToolProvider option) (worktreePath: string) =
    match provider |> Option.defaultValue CodingToolProvider.Default with
    | CodingToolProvider.Claude ->
        ClaudeDetector.enumerateFiles worktreePath
        |> ClaudeDetector.getLastSessionIdFromFiles
    | CodingToolProvider.Copilot -> CopilotDetector.getLastSessionId worktreePath
