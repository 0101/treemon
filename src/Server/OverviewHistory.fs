module Server.OverviewHistory

// Pure fixed-resolution reconstruction for the Overview band's durable history. Tasks carry forward
// from change-only snapshots. Agent status carries forward from activity events while event/liveness
// observations control openness. Source changes are folded chronologically, but the expensive live
// projection is performed only at the 289 possible sample boundaries.

open System
open OverviewData
open Server.SessionActivity
open Server.SessionActivityStore

let sampleBucketCount = OverviewHistoryRollup.sampleIntervalCount

let tasksChanged (last: TaskCount list option) (current: TaskCount list) : bool =
    match last with
    | None -> true
    | Some prev -> prev <> current

let private toStored (r: ActivityEventRow) : StoredStatus =
    { SessionId = r.SessionId
      WorktreePath = r.WorktreePath
      Provider = r.Provider
      Status =
        { Status = r.Status
          Skill = r.Skill
          Intent = None
          Title = None
          LastUserMessage = None
          LastAssistantMessage = None
          ContextUsage = None }
      UpdatedAt = r.Ts
      LastSeen = r.Ts
      ContextUsageAt = None }

type private Change =
    | CloseSession of SessionId
    | SetStatus of ActivityEventRow
    | ObserveLiveness of SessionId
    | SetTasks of TaskCount list

type private TimedChange =
    { At: DateTimeOffset
      Priority: int
      Sequence: int
      Change: Change }

type private SweepState =
    { Tasks: TaskCount list
      Sessions: Map<SessionId, StoredStatus>
      OpenSessions: Set<SessionId> }

let private closeBoundaries (observations: (SessionId * DateTimeOffset) list) =
    observations
    |> List.groupBy fst
    |> List.collect (fun (sessionId, rows) ->
        match rows |> List.map snd |> List.distinct |> List.sort with
        | [] -> []
        | first :: rest ->
            let lastSeen, closes =
                rest
                |> List.fold (fun (lastSeen, closes) observedAt ->
                    if observedAt - lastSeen <= openWindow then observedAt, closes
                    else observedAt, lastSeen + openWindow :: closes
                ) (first, [])

            (lastSeen + openWindow :: closes)
            |> List.map (fun closesAt -> sessionId, closesAt))

let private applyChange (state: SweepState) (timed: TimedChange) : SweepState =
    match timed.Change with
    | SetTasks tasks ->
        { state with Tasks = tasks }
    | SetStatus row ->
        let stored =
            match Map.tryFind row.SessionId state.Sessions with
            | Some current -> { toStored row with LastSeen = max current.LastSeen row.Ts }
            | None -> toStored row

        { state with
            Sessions = Map.add row.SessionId stored state.Sessions
            OpenSessions = Set.add row.SessionId state.OpenSessions }
    | ObserveLiveness sessionId ->
        match Map.tryFind sessionId state.Sessions with
        | None -> state
        | Some stored ->
            { state with
                Sessions =
                    state.Sessions
                    |> Map.add sessionId { stored with LastSeen = max stored.LastSeen timed.At }
                OpenSessions = Set.add sessionId state.OpenSessions }
    | CloseSession sessionId ->
        match Map.tryFind sessionId state.Sessions with
        | Some stored when stored.LastSeen + openWindow <= timed.At ->
            { state with OpenSessions = Set.remove sessionId state.OpenSessions }
        | _ -> state

let private snapshotAt (timestamp: DateTimeOffset) (state: SweepState) : OverviewSnapshot =
    let agents =
        state.OpenSessions
        |> Seq.choose (fun sessionId -> Map.tryFind sessionId state.Sessions)
        |> CodingToolStatus.collapseByWorktree timestamp
        |> Map.values
        |> Seq.collect _.SessionStatuses
        |> Seq.map (fun session -> session.Status, session.Skill)
        |> agentCountsOf

    { Timestamp = timestamp
      Tasks = state.Tasks
      Agents = agents }

let private sameValues (left: OverviewSnapshot) (right: OverviewSnapshot) =
    left.Tasks = right.Tasks && left.Agents = right.Agents

/// Reconstruct one complete snapshot at every supplied boundary without collapsing equal values.
let internal reconstructAt
    (boundaries: DateTimeOffset list)
    (taskSnapshots: (DateTimeOffset * TaskCount list) list)
    (events: ActivityEventRow list)
    (liveness: (SessionId * DateTimeOffset) list)
    : OverviewSnapshot list =
    let anchor = boundaries |> List.tryLast
    let throughAnchor rows timestampOf =
        match anchor with
        | Some last -> rows |> List.filter (fun row -> timestampOf row <= last)
        | None -> []

    let events = throughAnchor events _.Ts
    let liveness = throughAnchor liveness snd
    let observations = (events |> List.map (fun row -> row.SessionId, row.Ts)) @ liveness

    let changes =
        [ events
          |> List.mapi (fun sequence row ->
              { At = row.Ts
                Priority = 1
                Sequence = sequence
                Change = SetStatus row })
          liveness
          |> List.mapi (fun sequence (sessionId, observedAt) ->
              { At = observedAt
                Priority = 2
                Sequence = sequence
                Change = ObserveLiveness sessionId })
          taskSnapshots
          |> fun rows -> throughAnchor rows fst
          |> List.mapi (fun sequence (at, tasks) ->
              { At = at
                Priority = 3
                Sequence = sequence
                Change = SetTasks tasks })
          closeBoundaries observations
          |> fun rows -> throughAnchor rows snd
          |> List.mapi (fun sequence (sessionId, closesAt) ->
              { At = closesAt
                Priority = 0
                Sequence = sequence
                Change = CloseSession sessionId }) ]
        |> List.concat
        |> List.sortBy (fun change -> change.At, change.Priority, change.Sequence)

    let rec applyThrough boundary state =
        function
        | change :: rest when change.At <= boundary ->
            applyThrough boundary (applyChange state change) rest
        | remaining -> state, remaining

    boundaries
    |> List.fold (fun (state, remaining, snapshots) boundary ->
        let state, remaining = applyThrough boundary state remaining
        state, remaining, snapshotAt boundary state :: snapshots
    ) ({ Tasks = []; Sessions = Map.empty; OpenSessions = Set.empty }, changes, [])
    |> fun (_, _, snapshots) -> List.rev snapshots

let private collapseEqualSnapshots snapshots =
    snapshots
    |> List.fold (fun collapsed snapshot ->
        match collapsed with
        | previous :: _ when sameValues previous snapshot -> collapsed
        | _ -> snapshot :: collapsed
    ) []
    |> List.rev

/// Sample the complete Tasks and Agents state at the left edge and 288 bucket right edges. Every
/// source change timestamped at or before a boundary is applied first. Consecutive equal snapshots
/// retain their earliest timestamp, so the result is ordered, left-edge carried, and bounded to 289.
let sample
    (anchor: DateTimeOffset)
    (window: TimeSpan)
    (taskSnapshots: (DateTimeOffset * TaskCount list) list)
    (events: ActivityEventRow list)
    (liveness: (SessionId * DateTimeOffset) list)
    : OverviewSnapshot list =
    let start = anchor - window

    [ 0 .. sampleBucketCount ]
    |> List.map (fun bucket ->
        if bucket = sampleBucketCount then anchor
        else
            start.AddTicks(
                window.Ticks * int64 bucket / int64 sampleBucketCount
            ))
    |> fun boundaries -> reconstructAt boundaries taskSnapshots events liveness
    |> collapseEqualSnapshots

let fromPublishedRows
    anchor
    (rows: OverviewHistoryRollup.RollupRow list)
    : OverviewHistoryResponse =
    { Anchor = anchor
      Snapshots =
        rows
        |> List.map (fun row ->
            { Timestamp = row.Boundary
              Tasks = row.Tasks
              Agents = row.Agents })
        |> collapseEqualSnapshots }
