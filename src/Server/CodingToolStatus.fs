module Server.CodingToolStatus

open System
open System.IO
open System.Text.Json
open Shared
open Server.SessionActivity
open Server.SessionActivityStore


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
                | "copilot" -> Some CopilotCli
                | other ->
                    Log.log "CodingTool" $"Unknown/unsupported codingTool value '{other}' in {configPath} — using the default"
                    None
            | false, _ -> None
        with ex ->
            Log.log "CodingTool" $"Failed to read .treemon.json: {ex.Message}"
            None

type CodingToolResult =
    { Status: CodingToolStatus
      /// One SessionDot per open physical instance, ordered Working→Waiting→Idle then by durable
      /// session ID and exact process identity. Each marker carries its own context usage. Empty ⇔
      /// Status = NoSession.
      SessionStatuses: SessionDot list
      Provider: CodingToolProvider option
      CurrentSkill: string option
      /// The freshest source-tagged activity value from the same footer session as the other fields.
      AgentActivity: AgentActivity option
      LastUserMessage: UserFooterMessage option
      LastAssistantMessage: (string * DateTimeOffset) option
      /// `LastSeen` of the active session that won status resolution. None when every session is Idle.
      LastActivity: DateTimeOffset option }

/// Wraps an arbitrary argument in a provider-aware skill invocation. The Copilot CLI uses the
/// natural-language "use {skill} skill with {arg}" form. Shared by actionPrompt (FixPr/FixBuild) and
/// the worktree-create auto-launch flow so both stay byte-identical. Provider-matched so a future
/// provider must supply its own form.
let skillInvocation (provider: CodingToolProvider option) (skill: string) (arg: string) =
    match provider |> Option.defaultValue CodingToolProvider.Default with
    | CopilotCli -> $"use {skill} skill with {arg}"

let actionPrompt (provider: CodingToolProvider option) (action: ActionKind) =
    match action with
    | FixPr url -> skillInvocation provider "pr" url
    | FixBuild url -> skillInvocation provider "fix-build" url
    | CreatePr -> "Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"
    | CanvasSession prompt -> prompt

// Push-model live-state sourcing.
//
// The card's coding-tool fields come from the push model's exact process-instance state, not the
// log-parsing detectors. A worktree's exact process instances are collapsed via
// `fromPushInstances`, which
// makes TWO decoupled picks:
//   * the STATUS dot is driven by OPENNESS (only sessions still heartbeating count): open-active →
//     Working/WaitingForUser, open-but-idle → Idle (blue), no open session → NoSession (grey);
//   * the FOOTER (activity / skill / last-user / last-assistant) comes from the active winner when
//     one runs, else the session with the most-recent activity, so it survives Idle / NoSession.
// Resume is a THIRD, distinct durable-store scalar pick: the most-recently-active session regardless
// of active/idle (the session the user last touched).

/// The blank grey card a worktree shows when it has NO push session at all (never reported, or its
/// rows pruned). The `fromPushInstances` collapse below reproduces this exact value for an empty
/// instance list, and `WorktreeApi` falls back to it for a worktree absent from the collapse map.
/// A worktree with an OPEN-but-idle session collapses to blue `Idle` (not here), and one whose
/// sessions have all gone stale collapses to `NoSession` but KEEPS its retained footer.
let noSessionPushResult: CodingToolResult =
    { Status = NoSession
      SessionStatuses = []
      Provider = None
      CurrentSkill = None
      AgentActivity = None
      LastUserMessage = None
      LastAssistantMessage = None
      LastActivity = None }

let private toFooterMessage maxLength (message: Message) =
    FileUtils.truncateMessage maxLength message.Text, message.At

let private tryFormatActivityMessage (message: Message) =
    match UserMessageFormatting.classify message.Text with
    | UserMessageFormatting.UserMessageClassification.SystemReminder -> None
    | UserMessageFormatting.UserMessageClassification.Display(_, displayText) ->
        Some { message with Text = FileUtils.truncateMessage 120 displayText }

let private effectiveDisplayActivity (status: SessionStatus) =
    { status with
        Intent = status.Intent |> Option.bind tryFormatActivityMessage
        Title = status.Title |> Option.bind tryFormatActivityMessage }
    |> SessionActivity.effectiveActivity

let private toUserFooterMessage (message: Message) =
    match UserMessageFormatting.classify message.Text with
    | UserMessageFormatting.UserMessageClassification.SystemReminder -> None
    | UserMessageFormatting.UserMessageClassification.Display(glyph, text) ->
        Some
            { Glyph = glyph
              Text = FileUtils.truncateMessage 120 text
              Timestamp = message.At }

/// Collapse a worktree's live push sessions into the card's coding-tool fields. Two DECOUPLED picks:
///
/// * **Status dot** — driven by OPENNESS. Only sessions seen within `openWindow` (a live CLI keeps
///   heartbeating, even while idle) count: among the open sessions `pickActive` picks the most-recent
///   ACTIVE winner (Working/WaitingForUser); open-but-all-idle collapses to `Idle` (blue); NO open
///   session collapses to `NoSession` (grey). `openWindow` (~3 min) is smaller than
///   `stalenessTimeout`, so a dead Working session drops out of openness (→ grey) before the crash-net
///   would rewrite it to Idle — it never lingers blue.
/// * **Footer** (activity / skill / last user / last assistant) — DECOUPLED from the dot: the active
///   winner when one is running, otherwise the session with the MOST-RECENT ACTIVITY of ANY status
///   (the same activity ordering the durable resume query uses). Going Idle or losing the open
///   session does NOT blank the footer: it stays populated while any session for the worktree remains
///   in the store (retention / `idleWindow`).
/// Render order for exact-instance dots: Working first, then WaitingForUser, then Idle. NoSession is
/// never an instance status (it is the worktree-level collapse of an empty open set).
let private sessionStatusOrder =
    function
    | Working -> 0
    | WaitingForUser -> 1
    | Idle -> 2
    | NoSession -> 3

type private SessionSelection =
    { OpenInstances: StoredInstance list
      AdjustedOpen: StoredInstance list
      ActiveWinner: StoredInstance option }

let private selectInstances (now: DateTimeOffset) (instances: StoredInstance list) =
    let openInstances =
        instances
        |> List.filter (fun instance ->
            instance.ClosedAt.IsNone
            && now - instance.LastSeen < SessionActivity.openWindow)

    let adjustedOpen =
        openInstances
        |> List.map (fun instance ->
            { instance with
                Status =
                    SessionActivity.freshnessAdjusted
                        now
                        instance.LastSeen
                        instance.Status })

    let activeWinner =
        adjustedOpen
        |> SessionActivity.pickActive _.Status StoredInstance.activityOrderKey

    { OpenInstances = openInstances
      AdjustedOpen = adjustedOpen
      ActiveWinner = activeWinner }

type private FooterSource =
    { Provider: CodingToolProvider
      Status: SessionStatus
      UpdatedAt: DateTimeOffset
      SessionId: SessionId }

let private footerFromInstance (instance: StoredInstance) =
    { Provider = instance.Provider
      Status = instance.Status
      UpdatedAt = instance.UpdatedAt
      SessionId = instance.SessionId }

let private footerFromRetained (retained: RetainedSession) =
    { Provider = retained.Provider
      Status = retained.Status
      UpdatedAt = retained.UpdatedAt
      SessionId = retained.SessionId }

let private mostRecentFooter sources =
    sources
    |> List.sortByDescending (fun source ->
        source.UpdatedAt, source.SessionId)
    |> List.tryHead

let internal representativeActivityText now instances =
    let selection = selectInstances now instances

    selection.ActiveWinner
    |> Option.orElseWith (fun () ->
        selection.OpenInstances
        |> StoredInstance.tryMostRecentActivity)
    |> Option.map _.Status
    |> Option.bind effectiveDisplayActivity
    |> Option.map (AgentActivity.textAndTimestamp >> fst >> _.Trim())

let fromPushInstances
    (now: DateTimeOffset)
    (retained: RetainedSession option)
    (instances: StoredInstance list)
    : CodingToolResult =
    let selection = selectInstances now instances

    let status =
        match selection.OpenInstances with
        | [] -> NoSession
        | _ ->
            selection.ActiveWinner
            |> Option.map (fun winner ->
                winner.Status
                |> SessionActivity.effectiveStatus
                |> SessionActivity.toCodingToolStatus)
            |> Option.defaultValue Idle

    // Exact-instance dots: every open process's freshness-adjusted status paired with its own running
    // skill and context usage, ordered Working→Waiting→Idle then by session id. Each session keeps
    // its OWN skill + ContextUsage — no footer collapse — so the Overview band can classify each
    // session's activity independently and a session that has reported usage renders a donut
    // regardless of which session currently wins status. Empty ⇔ status = NoSession, so the client
    // reproduces the single grey dot from an empty list.
    let sessionStatuses =
        selection.AdjustedOpen
        |> List.map (fun instance ->
            { InstanceId =
                instance.ProcessIdentity
                |> ProcessIdentity.sessionInstanceId
              Status =
                instance.Status
                |> SessionActivity.effectiveStatus
                |> SessionActivity.toCodingToolStatus
              Skill = instance.Status.Skill
              ContextUsage = instance.Status.ContextUsage },
            instance.SessionId,
            ProcessIdentity.sortKey instance.ProcessIdentity)
        |> List.sortBy (fun (dot, sessionId, processIdentity) ->
            sessionStatusOrder dot.Status, sessionId, processIdentity)
        |> List.map (fun (dot, _, _) -> dot)

    // Footer source: the active winner if running. Otherwise use the greatest durable activity
    // representative across exact and migration-only history. The exact cache remains a fallback
    // for store-less fixtures, but liveness and terminal origin never come from retained history.
    let footer =
        selection.ActiveWinner
        |> Option.map footerFromInstance
        |> Option.orElseWith (fun () ->
            [ instances
              |> StoredInstance.tryMostRecentActivity
              |> Option.map footerFromInstance
              retained |> Option.map footerFromRetained ]
            |> List.choose id
            |> mostRecentFooter)

    { Status = status
      SessionStatuses = sessionStatuses
      Provider = footer |> Option.map _.Provider
      CurrentSkill = footer |> Option.bind (_.Status >> _.Skill)
      AgentActivity =
        footer
        |> Option.bind (_.Status >> effectiveDisplayActivity)
      LastUserMessage =
        footer
        |> Option.bind (_.Status >> _.LastUserMessage)
        |> Option.bind toUserFooterMessage
      LastAssistantMessage =
        footer
        |> Option.bind (_.Status >> _.LastAssistantMessage)
        |> Option.map (toFooterMessage 80)
      LastActivity = selection.ActiveWinner |> Option.map _.LastSeen }

/// Group exact process instances by worktree path and collapse each group into the
/// card's coding-tool fields (the openness-driven status dot + the decoupled footer). Keyed by the
/// normalised worktree path stored on each instance, so callers look it up by the
/// (already-normalised) `WorktreeInfo.Path`. Retained history is joined only after exact live
/// selection and can therefore populate footer fields without creating a marker or live address.
let collapseByWorktree
    (now: DateTimeOffset)
    (retainedByWorktree: Map<string, RetainedSession>)
    (instances: StoredInstance seq)
    : Map<string, CodingToolResult> =
    let instancesByWorktree =
        instances
        |> Seq.groupBy (_.WorktreePath >> WorktreePath.value)
        |> Seq.map (fun (path, grouped) -> path, List.ofSeq grouped)
        |> Map.ofSeq

    Set.union
        (instancesByWorktree |> Map.keys |> Set.ofSeq)
        (retainedByWorktree |> Map.keys |> Set.ofSeq)
    |> Seq.map (fun path ->
        path,
        fromPushInstances
            now
            (retainedByWorktree |> Map.tryFind path)
            (instancesByWorktree
             |> Map.tryFind path
             |> Option.defaultValue []))
    |> Map.ofSeq
