module Server.OverviewHistory

// The Overview band's history, unified onto the push-model event store (spec:
// docs/spec/overview-activity-history.md + docs/spec/session-status-push.md "Overview-history
// unification"). An OverviewSnapshot is `{ Tasks; Agents }`, but the two dimensions come from
// DIFFERENT sources and are reconciled only on read:
//
//   - Tasks  (beads planning counts) are NOT event-sourced — they are snapshot-based: the scheduler
//            logs the count-only projection to SessionActivityStore.task_snapshots on change
//            (`tasksChanged`), exactly as before, just persisted in SQLite instead of a JSONL file.
//   - Agents are DERIVED ON READ from status-bearing `activity_events` plus the separate compact
//            `session_liveness` timeline. Status/skill comes from events; heartbeat/usage points extend
//            LastSeen without fabricating activity events. The live collapse and history therefore use
//            the same openness and per-session grouping rules.
//
// `mergeHistory` stitches the two independently-changing series into one stepped OverviewSnapshot
// stream (carry each dimension forward at every change point), so getOverviewHistory and
// OverviewChart.fs are untouched — they still consume a plain `OverviewSnapshot list`.
//
// Pure: no IO, no mutation. The store owns persistence; this module owns the change-detection, the
// event→agent reconstruction, and the merge, so all three are unit-testable in isolation.

open System
open OverviewData
open Server.SessionActivity
open Server.SessionActivityStore

/// True when a freshly computed Tasks projection differs from the last logged one — the scheduler's
/// append-only change gate (identical counts append nothing; the first projection always counts as
/// changed so a baseline row is written).
let tasksChanged (last: TaskCount list option) (current: TaskCount list) : bool =
    match last with
    | None -> true
    | Some prev -> prev <> current

/// Project one event row to the StoredStatus shape `collapseByWorktree` consumes. Only Status/Skill/
/// LastSeen bear on the agent grouping, so the message/usage fields are left empty.
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

/// One session's state as of instant `t`: the StoredStatus of its last event at or before `t`, found by
/// binary search over a time-ascending `(Ts, StoredStatus)` array. None when every event is after `t`.
/// O(log rows) so the sweep does not re-scan a session's whole (idle-heartbeat-heavy) row list at every
/// candidate instant.
let private sessionAsOf (arr: (DateTimeOffset * StoredStatus)[]) (t: DateTimeOffset) : StoredStatus option =
    let rec search lo hi best =
        if lo > hi then best
        else
            let mid = (lo + hi) / 2
            if fst arr[mid] <= t then search (mid + 1) hi (Some(snd arr[mid]))
            else search lo (mid - 1) best

    search 0 (arr.Length - 1) None

let private latestAt (arr: DateTimeOffset[]) (t: DateTimeOffset) : DateTimeOffset option =
    let rec search lo hi best =
        if lo > hi then best
        else
            let mid = (lo + hi) / 2
            if arr[mid] <= t then search (mid + 1) hi (Some arr[mid])
            else search lo (mid - 1) best

    search 0 (arr.Length - 1) None

/// Derive the Agents history over the window from the raw push event stream. For each candidate
/// instant, reconstruct every session's state as of that instant, collapse per worktree
/// (`collapseByWorktree` → the same per-session statuses consumed by the live Overview), classify
/// each open session into an agent group, and emit the count vector — dropping consecutive unchanged
/// vectors so only real transitions appear.
///
/// Candidate instants are the window edges, every status event, and the start/end of each coalesced
/// open interval. Heartbeats inside one continuously-open interval do not trigger full reconstruction;
/// a gap longer than openWindow closes the interval and a later observation reopens it.
let deriveAgents
    (now: DateTimeOffset)
    (window: TimeSpan)
    (events: ActivityEventRow list)
    (liveness: (SessionId * DateTimeOffset) list)
    : (DateTimeOffset * AgentCount list) list =
    let start = now - window

    let bySession =
        events
        |> List.groupBy _.SessionId
        |> List.map (fun (_, rows) -> rows |> List.sortBy _.Ts)

    // Per-session time-ascending (Ts, StoredStatus) arrays, built ONCE up front so each candidate
    // instant reconstructs a session's state by binary search rather than re-scanning all its rows.
    // With idle heartbeats appending thousands of same-status rows per session, the old per-instant
    // full scan was ~instants × events; this makes the sweep ~instants × sessions × log rows.
    let bySessionStored =
        bySession
        |> List.map (fun rows -> rows |> List.map (fun r -> r.Ts, toStored r) |> List.toArray)

    let livenessBySession =
        liveness
        |> List.groupBy fst
        |> List.map (fun (sessionId, rows) -> sessionId, rows |> List.map snd |> List.sort |> List.toArray)
        |> Map.ofList

    let withLatestLiveness t (stored: StoredStatus) =
        livenessBySession
        |> Map.tryFind stored.SessionId
        |> Option.bind (fun rows -> latestAt rows t)
        |> Option.map (fun lastSeen -> { stored with LastSeen = max stored.LastSeen lastSeen })
        |> Option.defaultValue stored

    let agentsAt (t: DateTimeOffset) : AgentCount list =
        bySessionStored
        |> List.choose (fun arr -> sessionAsOf arr t)
        |> List.map (withLatestLiveness t)
        |> CodingToolStatus.collapseByWorktree t
        |> Map.values
        |> Seq.collect _.SessionStatuses
        |> Seq.map (fun s -> s.Status, s.Skill)
        |> agentCountsOf

    let openIntervals (times: DateTimeOffset list) =
        match times |> List.sort with
        | [] -> []
        | first :: rest ->
            let intervalStart, lastSeen, completed =
                rest
                |> List.fold (fun (intervalStart, lastSeen, completed) observedAt ->
                    if observedAt - lastSeen <= openWindow then
                        intervalStart, observedAt, completed
                    else
                        observedAt, observedAt, (intervalStart, lastSeen + openWindow) :: completed
                ) (first, first, [])

            List.rev ((intervalStart, lastSeen + openWindow) :: completed)

    let intervalBoundaries =
        ((events |> List.map (fun event -> event.SessionId, event.Ts)) @ liveness)
        |> List.groupBy fst
        |> List.collect (fun (_, rows) ->
            rows
            |> List.map snd
            |> openIntervals
            |> List.collect (fun (opensAt, closesAt) -> [ opensAt; closesAt ]))

    let instants =
        (start :: now :: ((events |> List.map _.Ts) @ intervalBoundaries))
        |> List.filter (fun t -> t >= start && t <= now)
        |> List.distinct
        |> List.sort

    (instants |> List.map (fun t -> t, agentsAt t), [])
    ||> List.foldBack (fun (t, agents) acc ->
        match acc with
        | (_, nextAgents) :: _ when nextAgents = agents -> (t, agents) :: List.tail acc
        | _ -> (t, agents) :: acc)

/// Merge the two independently-changing history series into one stepped OverviewSnapshot stream: at
/// every change point (in either dimension) emit a snapshot carrying the latest Tasks AND the latest
/// Agents. Simultaneous changes at one instant collapse into a single snapshot. Before a dimension's
/// first value it carries the empty list — nothing was known yet — which the chart renders as a bare
/// baseline. The output feeds `getOverviewHistory` / `OverviewChart.fs` unchanged.
let mergeHistory
    (taskSnaps: (DateTimeOffset * TaskCount list) list)
    (agentSnaps: (DateTimeOffset * AgentCount list) list)
    : OverviewSnapshot list =
    let events =
        (taskSnaps |> List.map (fun (t, v) -> t, Choice1Of2 v))
        @ (agentSnaps |> List.map (fun (t, v) -> t, Choice2Of2 v))
        |> List.sortBy fst

    (([], [], []), events)
    ||> List.fold (fun (tasks, agents, acc) (t, ev) ->
        let tasks, agents =
            match ev with
            | Choice1Of2 newTasks -> newTasks, agents
            | Choice2Of2 newAgents -> tasks, newAgents

        let snap = { Timestamp = t; Tasks = tasks; Agents = agents }

        match acc with
        | prev :: rest when prev.Timestamp = t -> tasks, agents, snap :: rest
        | _ -> tasks, agents, snap :: acc)
    |> fun (_, _, acc) -> List.rev acc
