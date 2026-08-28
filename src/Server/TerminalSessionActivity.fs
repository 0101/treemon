module Server.TerminalSessionActivity

open System
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

type internal OwnedSessionState =
    { TerminalSessionId: TerminalSessionId
      CopilotSessionId: SessionId
      Status: SessionLevelStatus }

type internal OwnedSessionSnapshot =
    { ActivityEpoch: int64
      OpenSessions: OwnedSessionState list
      ResumableSessionIds: Map<TerminalSessionId, SessionId> }

type internal ActivityQuery =
    Set<TerminalSessionId> -> Result<int64 * StoredStatus list, string>

let internal joinOwnedSessions
    (terminalSessionIds: Set<TerminalSessionId>)
    (sessions: StoredStatus seq)
    : (TerminalSessionId * StoredStatus) list =
    sessions
    |> Seq.choose (fun session ->
        session.TerminalSessionId
        |> Option.filter terminalSessionIds.Contains
        |> Option.map (fun terminalId -> terminalId, session))
    |> Seq.toList

let internal effectiveOwnedSessionStates
    (now: DateTimeOffset)
    (ownedSessions: (TerminalSessionId * StoredStatus) list)
    : OwnedSessionState list =
    ownedSessions
    |> List.choose (fun (terminalId, session) ->
        let status = effectiveStatus session.Status

        // Generic openness and crash-freshness windows are display/liveness heuristics. An exact
        // ask_user wait is a durable replacement gate until its request/completion clocks say that
        // input completed, even when heartbeats stop updating LastSeen.
        let replacementStatus =
            match status with
            | SessionLevelStatus.WaitingForUser -> Some status
            | _ when now - session.LastSeen < openWindow ->
                session.Status
                |> freshnessAdjusted now session.LastSeen
                |> effectiveStatus
                |> Some
            | _ -> None

        replacementStatus
        |> Option.map (fun status ->
            { TerminalSessionId = terminalId
              CopilotSessionId = session.SessionId
              Status = status }))
    |> List.sortBy (fun session ->
        TerminalSessionId.value session.TerminalSessionId, SessionId.value session.CopilotSessionId)

let internal resumableSessionIds
    (ownedSessions: (TerminalSessionId * StoredStatus) list)
    : Map<TerminalSessionId, SessionId> =
    ownedSessions
    |> List.groupBy fst
    |> List.choose (fun (terminalId, sessions) ->
        sessions
        |> List.map snd
        |> StoredStatus.tryMostRecentActivity
        |> Option.map (fun latest -> terminalId, latest.SessionId))
    |> Map.ofList

let internal ownedSessionSnapshot
    (now: DateTimeOffset)
    (terminalSessionIds: Set<TerminalSessionId>)
    (activityEpoch: int64, sessions: StoredStatus list)
    : OwnedSessionSnapshot =
    let ownedSessions = joinOwnedSessions terminalSessionIds sessions

    { ActivityEpoch = activityEpoch
      OpenSessions = effectiveOwnedSessionStates now ownedSessions
      ResumableSessionIds = resumableSessionIds ownedSessions }

let internal queryOwnedSessions
    (queryActivity: ActivityQuery)
    (now: DateTimeOffset)
    (terminalSessionIds: Set<TerminalSessionId>)
    =
    queryActivity terminalSessionIds
    |> Result.map (ownedSessionSnapshot now terminalSessionIds)

let private terminalOrigin (tab: EmbeddedTerminalTab) =
    tab.Id
    |> EmbeddedTerminalId.value
    |> TerminalSessionId

let internal withReportedActivity
    (now: DateTimeOffset)
    (sessions: StoredStatus seq)
    (snapshot: EmbeddedTerminalSnapshot)
    =
    let terminalSessionIds =
        snapshot.Tabs
        |> List.map terminalOrigin
        |> Set.ofList

    let reportedActivity =
        sessions
        |> joinOwnedSessions terminalSessionIds
        |> List.groupBy fst
        |> List.choose (fun (terminalSessionId, ownedSessions) ->
            ownedSessions
            |> List.map snd
            |> CodingToolStatus.representativeActivityText now
            |> Option.map (fun activity -> terminalSessionId, activity))
        |> Map.ofList

    { snapshot with
        Tabs =
            snapshot.Tabs
            |> List.map (fun tab ->
                { tab with
                    ReportedActivity =
                        reportedActivity
                        |> Map.tryFind (terminalOrigin tab) }) }

let private hasNonIdleOwnedSession (snapshot: OwnedSessionSnapshot) =
    snapshot.OpenSessions
    |> List.exists (fun session ->
        match session.Status with
        | SessionLevelStatus.Working
        | SessionLevelStatus.WaitingForUser -> true
        | SessionLevelStatus.Idle -> false)

let internal replacementSessionPlan
    resolveProvider
    (terminals: TerminalHostReplacement.ReplacementTerminal list)
    (snapshot: OwnedSessionSnapshot)
    =
    if hasNonIdleOwnedSession snapshot then
        TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle
    else
        let resumeCommands =
            terminals
            |> List.choose (fun terminal ->
                snapshot.ResumableSessionIds
                |> Map.tryFind (TerminalSessionId terminal.TerminalSessionId)
                |> Option.map (fun sessionId ->
                    let command =
                        CodingToolCli.build
                            (resolveProvider terminal.WorktreePath)
                            (CodingToolCli.Resume(Some(SessionId.value sessionId)))

                    terminal.TerminalSessionId, command.AsShellString))
            |> Map.ofList

        TerminalHostReplacement.ReplacementSessionPlan.Ready(snapshot.ActivityEpoch, resumeCommands)

/// Adapt the session-activity service's narrow raw query into the opaque policy consumed by
/// TerminalHost replacement. All exact ownership, terminal-specific gating, resume selection, and
/// provider command construction stays in this terminal-focused module.
let internal queryReplacementPlan
    resolveProvider
    (queryActivity: ActivityQuery)
    (now: DateTimeOffset)
    (terminals: TerminalHostReplacement.ReplacementTerminal list)
    =
    let terminalSessionIds = terminals |> List.map (_.TerminalSessionId >> TerminalSessionId) |> Set.ofList

    queryOwnedSessions queryActivity now terminalSessionIds
    |> Result.map (replacementSessionPlan resolveProvider terminals)
