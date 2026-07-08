module OverviewData

// Pure cross-worktree aggregation behind the Overview band (spec: docs/spec/beads-overview-band.md).
// Folds every monitored worktree into the band's two aggregate lenses:
//   - Tasks: the five status buckets (Planned · Queued · In progress · Blocked · Done), each a
//     cross-worktree sum. Planned folds in Loose (decision #6); Done counts only NON-archived
//     worktrees (decision #7) — every other bucket sums across all worktrees.
//   - Agents: red-dot WORKING agents (CodingTool = Working) grouped by the skill each is running,
//     classified through the shared Shared.Activity.classify, PLUS a distinct Waiting group for
//     agents parked on the user (CodingTool = WaitingForUser, yellow dot). Terminal presence
//     (HasActiveSession — which also covers Done/Idle/WaitingForUser) no longer inflates the counts
//     (spec Corrections v1.1 (h)).
// Empty buckets/groups are omitted (never surfaced as a 0), and Scale is the one true shared linear
// scale — the largest task-bucket count — so the view sizes every bar against a single denominator.
//
// Pure and Fable-safe: no IO, no Model/RepoModel dependency. It folds the Shared RepoWorktrees shape
// (every worktree present, archived ones flagged via IsArchived) rather than the client RepoModel,
// which already splits archived worktrees into a separate field — the Done filter needs archived
// worktrees present-but-flagged, so callers pass the un-split RepoWorktrees list.

open Shared

/// A cross-worktree task status bucket. RequireQualifiedAccess keeps Done/InProgress/Blocked from
/// colliding with CodingToolStatus.Done and the BeadsSummary field labels (same reason
/// CurrentActivity is qualified). The case list order below is the band's canonical left-to-right
/// display order.
[<RequireQualifiedAccess>]
type TaskBucketKind =
    | Planned
    | Queued
    | InProgress
    | Blocked
    | Done

/// One non-empty task bucket: its kind and cross-worktree count. Empty buckets are dropped from the
/// roll-up entirely (the band never shows a 0), so a present bucket always has Count > 0.
type TaskBucket = { Kind: TaskBucketKind; Count: int }

/// What an agent group represents: a skill-derived activity (a red-dot WORKING agent) or the distinct
/// "waiting for user" state (yellow dot). Modeled as a kind so the band can render the Waiting group
/// alongside the activity groups (spec Corrections v1.1 (h)) while keeping the two header counts —
/// N working, M waiting — separable.
[<RequireQualifiedAccess>]
type AgentGroupKind =
    | Activity of CurrentActivity
    | Waiting

/// One non-empty agent group: its kind and how many agents belong to it. Empty groups are dropped, so
/// a present group always has Count > 0.
type AgentGroup = { Kind: AgentGroupKind; Count: int }

/// The Overview band's cross-worktree roll-up: non-empty task buckets and agent groups (both in
/// canonical order) plus Scale — the largest task-bucket count, the single linear denominator every
/// task bar is sized against (0 when there are no tasks at all).
type Overview =
    { Tasks: TaskBucket list
      Agents: AgentGroup list
      Scale: int }

// Canonical left-to-right order of the task bars.
let private taskOrder =
    [ TaskBucketKind.Planned
      TaskBucketKind.Queued
      TaskBucketKind.InProgress
      TaskBucketKind.Blocked
      TaskBucketKind.Done ]

// Canonical order of the activity groups (mirrors the spec's activity table).
let private activityOrder =
    [ CurrentActivity.Investigating
      CurrentActivity.Planning
      CurrentActivity.Executing
      CurrentActivity.Reviewing
      CurrentActivity.Fixing
      CurrentActivity.Working ]

// Canonical order of the agent groups: the activity groups (in the order above), then the Waiting
// group LAST — matching the spec's palette enumeration where Waiting (yellow) trails Working (blue).
let private agentGroupOrder =
    (activityOrder |> List.map AgentGroupKind.Activity) @ [ AgentGroupKind.Waiting ]

/// The activity a WORKING worktree's current skill classifies to. An absent skill classifies to
/// Working (classify normalizes "" -> Working), matching the spec's "red-dot agent, no recognized
/// skill -> generic Working group".
let private activityOf (wt: WorktreeStatus) =
    Activity.classify (wt.CurrentSkill |> Option.defaultValue "")

/// Fold every worktree across every repo into the Overview roll-up (spec: beads-overview-band.md).
let aggregate (repos: RepoWorktrees list) : Overview =
    let worktrees = repos |> List.collect _.Worktrees

    // Task-bucket count for one kind. Only Done filters archived worktrees; every other bucket sums
    // across all worktrees. Planned folds Loose in (Loose -> Planned for display, decision #6).
    let sumOf f = worktrees |> List.sumBy f
    let countFor =
        function
        | TaskBucketKind.Planned    -> sumOf (fun w -> w.Planning.Planned + w.Planning.Loose)
        | TaskBucketKind.Queued     -> sumOf _.Planning.Queued
        | TaskBucketKind.InProgress -> sumOf _.Beads.InProgress
        | TaskBucketKind.Blocked    -> sumOf _.Beads.Blocked
        | TaskBucketKind.Done       -> sumOf (fun w -> if w.IsArchived then 0 else w.Beads.Closed)

    // Count each bucket once, in canonical order; reuse for both the omit-empties list and Scale.
    let counts = taskOrder |> List.map (fun kind -> kind, countFor kind)

    let tasks =
        counts
        |> List.choose (fun (kind, count) ->
            if count > 0 then Some { TaskBucket.Kind = kind; Count = count } else None)

    // One true shared scale: the largest bucket count, 0 when every bucket is empty. Empty buckets
    // are 0 and never raise the max, so this equals the max across the non-empty buckets.
    let scale = counts |> List.map snd |> List.max

    // Agent groups: red-dot WORKING agents (CodingTool = Working) grouped by their classified
    // activity, plus a distinct Waiting group (CodingTool = WaitingForUser). HasActiveSession is NOT
    // used — it also covers Done/Idle/WaitingForUser terminals, which would inflate Working with idle
    // and finished agents (spec Corrections v1.1 (h)). Empty groups omitted; Waiting sorts last.
    let working = worktrees |> List.filter (fun w -> w.CodingTool = CodingToolStatus.Working)
    let waitingCount =
        worktrees |> List.filter (fun w -> w.CodingTool = CodingToolStatus.WaitingForUser) |> List.length

    let countForKind =
        function
        | AgentGroupKind.Activity activity ->
            working |> List.filter (fun w -> activityOf w = activity) |> List.length
        | AgentGroupKind.Waiting -> waitingCount

    let agents =
        agentGroupOrder
        |> List.choose (fun kind ->
            match countForKind kind with
            | 0 -> None
            | count -> Some { AgentGroup.Kind = kind; Count = count })

    { Tasks = tasks; Agents = agents; Scale = scale }
