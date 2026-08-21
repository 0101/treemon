module Server.TerminalSessionActivity

open System
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
    |> List.filter (fun (_, session) -> now - session.LastSeen < openWindow)
    |> List.map (fun (terminalId, session) ->
        { TerminalSessionId = terminalId
          CopilotSessionId = session.SessionId
          Status = session.Status |> freshnessAdjusted now session.LastSeen |> effectiveStatus })
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
/// TerminalHost replacement. All exact ownership, openness, resume selection, and provider command
/// construction stays in this terminal-focused module.
let internal queryReplacementPlan
    resolveProvider
    (queryActivity: ActivityQuery)
    (now: DateTimeOffset)
    (terminals: TerminalHostReplacement.ReplacementTerminal list)
    =
    let terminalSessionIds = terminals |> List.map (_.TerminalSessionId >> TerminalSessionId) |> Set.ofList

    queryOwnedSessions queryActivity now terminalSessionIds
    |> Result.map (replacementSessionPlan resolveProvider terminals)
