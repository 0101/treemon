module Server.TerminalSessionActivity

open System
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

type internal OwnedSessionState =
    { ProcessIdentity: ProcessIdentity
      TerminalSessionId: TerminalSessionId
      CopilotSessionId: SessionId
      Status: SessionLevelStatus }

type internal OwnedSessionSnapshot =
    { ActivityEpoch: int64
      OpenSessions: OwnedSessionState list
      PendingReconciliation: Set<ProcessIdentity>
      ReplacementSessionIds: Map<TerminalSessionId, SessionId> }

type internal ActivityQuery =
    Set<TerminalSessionId>
        -> Result<
            int64 * StoredInstance list * Set<ProcessIdentity>,
            string
         >

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

let internal joinOwnedInstances
    (terminalSessionIds: Set<TerminalSessionId>)
    (instances: StoredInstance seq)
    : (TerminalSessionId * StoredInstance) list =
    instances
    |> Seq.choose (fun instance ->
        instance.TerminalSessionId
        |> Option.filter terminalSessionIds.Contains
        |> Option.map (fun terminalId -> terminalId, instance))
    |> Seq.toList

let internal ownedSessionSnapshot
    (now: DateTimeOffset)
    (terminalSessionIds: Set<TerminalSessionId>)
    (
        activityEpoch: int64,
        instances: StoredInstance list,
        pendingReconciliation: Set<ProcessIdentity>
    )
    : OwnedSessionSnapshot =
    let liveOwnedInstances =
        instances
        |> joinOwnedInstances terminalSessionIds
        |> List.filter (fun (_, instance) ->
            instance.ClosedAt.IsNone
            && now - instance.LastSeen < openWindow)

    let openSessions =
        liveOwnedInstances
        |> List.map (fun (terminalId, instance) ->
            { ProcessIdentity = instance.ProcessIdentity
              TerminalSessionId = terminalId
              CopilotSessionId = instance.SessionId
              Status =
                instance.Status
                |> freshnessAdjusted now instance.LastSeen
                |> effectiveStatus })
        |> List.sortBy (fun session ->
            TerminalSessionId.value session.TerminalSessionId,
            SessionId.value session.CopilotSessionId,
            ProcessIdentity.sortKey session.ProcessIdentity)

    let replacementSessionIds =
        liveOwnedInstances
        |> List.groupBy fst
        |> List.choose (fun (terminalId, terminalInstances) ->
            terminalInstances
            |> List.map snd
            |> List.sortByDescending StoredInstance.activityOrderKey
            |> List.tryHead
            |> Option.map (fun latest -> terminalId, latest.SessionId))
        |> Map.ofList

    { ActivityEpoch = activityEpoch
      OpenSessions = openSessions
      PendingReconciliation = pendingReconciliation
      ReplacementSessionIds = replacementSessionIds }

let internal queryOwnedSessions
    (queryActivity: ActivityQuery)
    (now: DateTimeOffset)
    (terminalSessionIds: Set<TerminalSessionId>)
    =
    queryActivity terminalSessionIds
    |> Result.map (ownedSessionSnapshot now terminalSessionIds)

let private terminalOrigin (tab: EmbeddedTerminalTab) =
    tab.Id |> EmbeddedTerminalId.value |> TerminalSessionId

let internal tryFindLiveTerminalId
    (now: DateTimeOffset)
    (worktreePath: WorktreePath)
    (copilotSessionId: SessionId)
    (sessions: StoredStatus seq)
    (snapshot: EmbeddedTerminalSnapshot)
    : EmbeddedTerminalId option =
    let runningTerminals =
        snapshot.Tabs
        |> List.choose (fun tab ->
            match tab.Lifecycle with
            | EmbeddedTerminalLifecycle.Running _ when tab.Worktree = worktreePath ->
                Some(terminalOrigin tab, tab.Id)
            | _ -> None)
        |> Map.ofList

    sessions
    |> Seq.filter (fun session -> session.WorktreePath = worktreePath)
    |> joinOwnedSessions (runningTerminals |> Map.keys |> Set.ofSeq)
    |> List.tryPick (fun (terminalSessionId, session) ->
        let reusable =
            session.SessionId = copilotSessionId
            && match effectiveStatus session.Status with
               | SessionLevelStatus.WaitingForUser -> true
               | _ -> now - session.LastSeen < openWindow

        if reusable then
            runningTerminals |> Map.tryFind terminalSessionId
        else
            None)

let internal withReportedActivity
    (now: DateTimeOffset)
    (sessions: StoredStatus seq)
    (snapshot: EmbeddedTerminalSnapshot)
    =
    let terminalSessionIds = snapshot.Tabs |> List.map terminalOrigin |> Set.ofList

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
                { tab with ReportedActivity = reportedActivity |> Map.tryFind (terminalOrigin tab) }) }

let internal replacementSessionPlan
    resolveProvider
    (terminals: TerminalHostReplacement.ReplacementTerminal list)
    (snapshot: OwnedSessionSnapshot)
    =
    if
        not (Set.isEmpty snapshot.PendingReconciliation)
        || snapshot.OpenSessions
           |> List.exists (fun session ->
               session.Status <> SessionLevelStatus.Idle)
    then
        TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle
    else
        let resumeCommands =
            terminals
            |> List.choose (fun terminal ->
                snapshot.ReplacementSessionIds
                |> Map.tryFind (TerminalSessionId terminal.TerminalSessionId)
                |> Option.map (fun sessionId ->
                    terminal.TerminalSessionId,
                    CodingToolCli.build
                        (resolveProvider terminal.WorktreePath)
                        (CodingToolCli.Resume(Some(SessionId.value sessionId)))
                    |> _.AsShellString))
            |> Map.ofList

        TerminalHostReplacement.ReplacementSessionPlan.Ready(snapshot.ActivityEpoch, resumeCommands)

/// Adapt the session-activity service's narrow raw query into the opaque policy consumed by
/// TerminalHost replacement. All exact ownership, terminal-specific gating, resume selection, and
/// provider command construction stays in this terminal-focused module.
let internal queryReplacementPlan
    replacementReadyAt
    resolveProvider
    (queryActivity: ActivityQuery)
    (now: DateTimeOffset)
    (terminals: TerminalHostReplacement.ReplacementTerminal list)
    =
    if now < replacementReadyAt then
        Ok TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle
    else
        let terminalSessionIds = terminals |> List.map (_.TerminalSessionId >> TerminalSessionId) |> Set.ofList

        queryOwnedSessions queryActivity now terminalSessionIds
        |> Result.map (replacementSessionPlan resolveProvider terminals)
