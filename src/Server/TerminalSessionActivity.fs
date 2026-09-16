module Server.TerminalSessionActivity

open System
open Shared
open Server.SessionActivity
open Server.SessionActivityStore

type internal OwnedSessionState =
    { ProcessIdentity: ProcessIdentity
      TerminalSessionId: TerminalSessionId
      CopilotSessionId: SessionId }

type internal OwnedSessionSnapshot =
    { OpenSessions: OwnedSessionState list
      RestartSessionIds: Map<TerminalSessionId, SessionId> }

type internal ActivityQuery =
    Set<TerminalSessionId>
        -> Result<StoredInstance list, string>

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
    (instances: StoredInstance list)
    : OwnedSessionSnapshot =
    let liveOwnedInstances =
        instances
        |> joinOwnedInstances terminalSessionIds
        |> List.filter (snd >> StoredInstance.isOpenAt now)

    let openSessions =
        liveOwnedInstances
        |> List.map (fun (terminalId, instance) ->
            { ProcessIdentity = instance.ProcessIdentity
              TerminalSessionId = terminalId
              CopilotSessionId = instance.SessionId })
        |> List.sortBy (fun session ->
            session.TerminalSessionId,
            session.CopilotSessionId,
            ProcessIdentity.sortKey session.ProcessIdentity)

    let restartSessionIds =
        liveOwnedInstances
        |> List.groupBy fst
        |> List.choose (fun (terminalId, terminalInstances) ->
            terminalInstances
            |> List.map snd
            |> StoredInstance.tryMostRecentActivity
            |> Option.map (fun latest ->
                terminalId,
                latest.SessionId))
        |> Map.ofList

    { OpenSessions = openSessions
      RestartSessionIds = restartSessionIds }

let internal queryOwnedSessions
    (queryActivity: ActivityQuery)
    (now: DateTimeOffset)
    (terminalSessionIds: Set<TerminalSessionId>)
    =
    queryActivity terminalSessionIds
    |> Result.map (
        ownedSessionSnapshot
            now
            terminalSessionIds
    )

let private terminalOrigin (tab: EmbeddedTerminalTab) =
    tab.Id
    |> EmbeddedTerminalId.value
    |> TerminalSessionId.create
    |> Result.defaultWith (fun error ->
        invalidOp $"Invalid embedded terminal identity: {error}")

let internal tryFindLiveTerminalId
    (now: DateTimeOffset)
    (worktreePath: WorktreePath)
    (copilotSessionId: SessionId)
    (instances: StoredInstance seq)
    (snapshot: EmbeddedTerminalSnapshot)
    : EmbeddedTerminalId option =
    let runningTerminals =
        snapshot.Tabs
        |> List.choose (fun tab ->
            match tab.Lifecycle with
            | EmbeddedTerminalLifecycle.Running _
                when tab.Worktree = worktreePath ->
                Some(terminalOrigin tab, tab.Id)
            | _ -> None)
        |> Map.ofList

    instances
    |> Seq.filter (fun instance ->
        instance.WorktreePath = worktreePath
        && instance.SessionId = copilotSessionId
        && StoredInstance.isOpenAt now instance)
    |> joinOwnedInstances
        (runningTerminals |> Map.keys |> Set.ofSeq)
    |> List.sortByDescending (
        snd >> StoredInstance.activityOrderKey
    )
    |> List.tryPick (fun (terminalSessionId, _) ->
        runningTerminals
        |> Map.tryFind terminalSessionId)

let internal withReportedActivity
    (now: DateTimeOffset)
    (instances: StoredInstance seq)
    (snapshot: EmbeddedTerminalSnapshot)
    =
    let terminalSessionIds =
        snapshot.Tabs
        |> List.map terminalOrigin
        |> Set.ofList

    let reportedActivity =
        instances
        |> Seq.filter (StoredInstance.isOpenAt now)
        |> joinOwnedInstances terminalSessionIds
        |> List.groupBy fst
        |> List.choose (fun (terminalSessionId, ownedSessions) ->
            ownedSessions
            |> List.map snd
            |> CodingToolStatus.representativeActivityText now
            |> Option.map (fun activity ->
                terminalSessionId,
                activity))
        |> Map.ofList

    { snapshot with
        Tabs =
            snapshot.Tabs
            |> List.map (fun tab ->
                { tab with
                    ReportedActivity =
                        reportedActivity
                        |> Map.tryFind (terminalOrigin tab) }) }

let internal restartSessions
    resolveProvider
    (queryActivity: ActivityQuery)
    (now: DateTimeOffset)
    (terminals: TerminalHostReplacement.HostedTerminal list)
    =
    let terminalSessionIds =
        terminals
        |> List.map _.TerminalSessionId
        |> Set.ofList

    queryOwnedSessions
        queryActivity
        now
        terminalSessionIds
    |> Result.map (fun snapshot ->
        terminals
        |> List.choose (fun terminal ->
            snapshot.RestartSessionIds
            |> Map.tryFind terminal.TerminalSessionId
            |> Option.map (fun sessionId ->
                let restart:
                    TerminalHostReplacement.RestartSession =
                    { WorktreePath = terminal.WorktreePath
                      Command =
                        CodingToolCli.build
                            (resolveProvider terminal.WorktreePath)
                            (CodingToolCli.Resume(
                                Some(SessionId.value sessionId)
                            ))
                        |> _.AsShellString }

                restart)))
