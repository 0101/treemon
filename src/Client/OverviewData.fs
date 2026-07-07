module OverviewData

// Pure cross-worktree aggregation behind the Overview band (spec: docs/spec/beads-overview-band.md).
// Folds every monitored worktree into the band's two aggregate lenses:
//   - Tasks: the five status buckets (Planned · Queued · In progress · Blocked · Done), each a
//     cross-worktree sum. Planned folds in Loose (decision #6); Done counts only NON-archived
//     worktrees (decision #7) — every other bucket sums across all worktrees.
//   - Activities: active worktrees (a live session / red dot) grouped by the skill each is running,
//     classified through the shared Shared.Activity.classify so the band and the per-card stripe
//     agree on one source of truth.
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

/// One non-empty activity group: an activity and how many active agents are running a skill that
/// classifies to it. Empty activities are dropped, so a present group always has Count > 0.
type ActivityGroup = { Activity: CurrentActivity; Count: int }

/// The Overview band's cross-worktree roll-up: non-empty task buckets and activity groups (both in
/// canonical order) plus Scale — the largest task-bucket count, the single linear denominator every
/// task bar is sized against (0 when there are no tasks at all).
type Overview =
    { Tasks: TaskBucket list
      Activities: ActivityGroup list
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

/// The activity a worktree's current skill classifies to. An absent skill classifies to Working
/// (classify normalizes "" -> Working), matching the spec's "active session, no recognized skill".
let private activityOf (wt: WorktreeStatus) =
    Activity.classify (wt.CurrentSkill |> Option.defaultValue "")

/// Fold every worktree across every repo into the Overview roll-up (spec: beads-overview-band.md).
let aggregate (repos: RepoWorktrees list) : Overview =
    let worktrees = repos |> List.collect (fun r -> r.Worktrees)

    // Task-bucket count for one kind. Only Done filters archived worktrees; every other bucket sums
    // across all worktrees. Planned folds Loose in (Loose -> Planned for display, decision #6).
    let sumOf f = worktrees |> List.sumBy f
    let countFor =
        function
        | TaskBucketKind.Planned    -> sumOf (fun w -> w.Planning.Planned + w.Planning.Loose)
        | TaskBucketKind.Queued     -> sumOf (fun w -> w.Planning.Queued)
        | TaskBucketKind.InProgress -> sumOf (fun w -> w.Beads.InProgress)
        | TaskBucketKind.Blocked    -> sumOf (fun w -> w.Beads.Blocked)
        | TaskBucketKind.Done       -> sumOf (fun w -> if w.IsArchived then 0 else w.Beads.Closed)

    // Count each bucket once, in canonical order; reuse for both the omit-empties list and Scale.
    let counts = taskOrder |> List.map (fun kind -> kind, countFor kind)

    let tasks =
        counts
        |> List.choose (fun (kind, count) ->
            if count > 0 then Some { Kind = kind; Count = count } else None)

    // One true shared scale: the largest bucket count, 0 when every bucket is empty. Empty buckets
    // are 0 and never raise the max, so this equals the max across the non-empty buckets.
    let scale = counts |> List.map snd |> List.max

    // Activity groups: active worktrees (live session) grouped by classified activity, empties omitted.
    let active = worktrees |> List.filter (fun w -> w.HasActiveSession)
    let activities =
        activityOrder
        |> List.choose (fun activity ->
            match active |> List.filter (fun w -> activityOf w = activity) |> List.length with
            | 0 -> None
            | count -> Some { Activity = activity; Count = count })

    { Tasks = tasks; Activities = activities; Scale = scale }
