module Server.OverviewHistory

// The Overview band's history, unified onto the push-model event store (spec:
// docs/spec/overview-activity-history.md + docs/spec/session-status-push.md "Overview-history
// unification"). An OverviewSnapshot is `{ Tasks; Agents }`, but the two dimensions come from
// DIFFERENT sources and are reconciled only on read:
//
//   - Tasks  (beads planning counts) are NOT event-sourced — they are snapshot-based: the scheduler
//            logs the count-only projection to SessionActivityStore.task_snapshots on change
//            (`tasksChanged`), exactly as before, just persisted in SQLite instead of a JSONL file.
//   - Agents are DERIVED ON READ from `activity_events` (the durable push event stream): `deriveAgents`
//            replays each session's status/skill over the window and collapses it per worktree with the
//            SAME `CodingToolStatus.collapseByWorktree` the live band uses — so the history and the live
//            band bucket a status identically, with openness/staleness decay modelled (a closed/crashed
//            session drops out of the counts after `openWindow`, matching what the band showed then).
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

/// Derive the Agents history over the window from the raw push event stream. For each candidate
/// instant, reconstruct every session's state as of that instant, collapse per worktree
/// (`collapseByWorktree` → openness-driven status dot), classify into agent groups, and
/// emit the count vector — dropping consecutive unchanged vectors so only real transitions appear.
///
/// NOTE: the history collapses one status per worktree, whereas the live band (since #125) counts each
/// open session individually. History is therefore a coarser per-worktree view of the same event
/// stream; unifying it onto the per-session model is a possible follow-up.
///
/// Candidate instants are the window's left edge, each session's status/skill TRANSITIONS (heartbeats
/// that merely re-assert the same status are skipped — they can't move the counts), and each session's
/// openness-decay boundary (`lastEvent + openWindow`, when a quiet session stops counting). `events`
/// must cover a little BEFORE `now - window` (the caller widens the query) so a session already running
/// at the window's left edge is reconstructed there rather than materialising at its first in-window
/// heartbeat.
let deriveAgents (now: DateTimeOffset) (window: TimeSpan) (events: ActivityEventRow list) : (DateTimeOffset * AgentCount list) list =
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

    let agentsAt (t: DateTimeOffset) : AgentCount list =
        bySessionStored
        |> List.choose (fun arr -> sessionAsOf arr t)
        |> CodingToolStatus.collapseByWorktree t
        |> Map.values
        |> Seq.map (fun r -> r.Status, r.CurrentSkill)
        |> agentCountsOf

    // A session's status/skill transitions: its first event (establishes its state) plus every event
    // whose (status, skill) differs from the immediately preceding event of the same session.
    let transitionTimes =
        bySession
        |> List.collect (fun rows ->
            let changes =
                rows
                |> List.pairwise
                |> List.choose (fun (prev, cur) ->
                    if (prev.Status, prev.Skill) <> (cur.Status, cur.Skill) then Some cur.Ts else None)

            match rows with
            | first :: _ -> first.Ts :: changes
            | [] -> changes)

    // Where a quiet session stops being "open" (drops out of the status dot). openWindow < staleness,
    // so this openness boundary is the binding decay transition.
    let decayBoundaries =
        bySession
        |> List.choose (fun rows -> rows |> List.tryLast |> Option.map (fun last -> last.Ts + openWindow))

    let instants =
        (start :: now :: (transitionTimes @ decayBoundaries))
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
