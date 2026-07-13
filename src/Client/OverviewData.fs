module OverviewData

// Pure cross-worktree aggregation behind the Overview band (spec: docs/spec/beads-overview-band.md).
// Folds every monitored worktree into the band's two aggregate lenses:
//   - Tasks: the status buckets (Planned · Queued · In progress · Blocked · Done · Unattended), each
//     a cross-worktree sum. Planned folds in Loose (decision #6); Done counts only NON-archived
//     worktrees (decision #7). In progress and Queued count only where the worktree has an ACTIVE
//     agent (CodingTool = Working or WaitingForUser); on an inactive worktree those tasks are likely
//     stale beads status and fold into the muted Unattended catch-all instead. Every other bucket
//     sums across all worktrees.
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
    | Unattended

/// One worktree's membership in a group, carrying everything the drill-down panel needs: the focus
/// key (WorktreePath.value — the same key arrow-nav focuses on), the branch label, the owning repo's
/// RootFolderName (preserved before the aggregate flattens repos away), and Contribution — how much
/// this worktree adds to the group's Count (agent group: always 1; task bucket: this worktree's task
/// count in the bucket). A worktree is a group member iff its Contribution > 0.
type GroupMember =
    { ScopedKey: string
      Branch: string
      RepoName: string
      Contribution: int }

/// One non-empty task bucket: its kind, cross-worktree count, and the member worktrees that make it
/// up (built from the SAME per-worktree predicate as the count, so Count = Σ member Contribution can
/// never diverge). Empty buckets are dropped from the roll-up entirely (the band never shows a 0), so
/// a present bucket always has Count > 0 and a non-empty Members list.
type TaskBucket = { Kind: TaskBucketKind; Count: int; Members: GroupMember list }

/// What an agent group represents: a skill-derived activity (a red-dot WORKING agent) or the distinct
/// "waiting for user" state (yellow dot). Modeled as a kind so the band can render the Waiting group
/// alongside the activity groups (spec Corrections v1.1 (h)) while keeping the two header counts —
/// N working, M waiting — separable.
[<RequireQualifiedAccess>]
type AgentGroupKind =
    | Activity of CurrentActivity
    | Waiting

/// One non-empty agent group: its kind, how many agents belong to it, and the member worktrees
/// (each contributing 1, so Count = Members.Length). Empty groups are dropped, so a present group
/// always has Count > 0 and a non-empty Members list.
type AgentGroup = { Kind: AgentGroupKind; Count: int; Members: GroupMember list }

/// The Overview band's cross-worktree roll-up: non-empty task buckets and agent groups (both in
/// canonical order) plus Scale — the largest task-bucket count, the single linear denominator every
/// task bar is sized against (0 when there are no tasks at all).
type Overview =
    { Tasks: TaskBucket list
      Agents: AgentGroup list
      Scale: int }

// Canonical left-to-right order of the task bars. Unattended trails Done: it is the muted
// catch-all for In-progress/Queued tasks whose worktree has no active agent.
let private taskOrder =
    [ TaskBucketKind.Planned
      TaskBucketKind.Queued
      TaskBucketKind.InProgress
      TaskBucketKind.Blocked
      TaskBucketKind.Done
      TaskBucketKind.Unattended ]

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
    // Tag each worktree with its owning repo's RootFolderName BEFORE flattening, so every GroupMember
    // can carry its RepoName. Repo order and within-repo worktree order are preserved, so member
    // lists come back in the band's repo/worktree order.
    let taggedWorktrees =
        repos |> List.collect (fun r -> r.Worktrees |> List.map (fun w -> r.RootFolderName, w))

    let memberOf repoName (w: WorktreeStatus) contribution =
        { ScopedKey = WorktreePath.value w.Path
          Branch = w.Branch
          RepoName = repoName
          Contribution = contribution }

    // A worktree's contribution to one task bucket. In-progress and Queued only count toward their
    // live buckets when their worktree has an ACTIVE agent (CodingTool = Working or WaitingForUser);
    // on an inactive worktree (Done/Idle) they are likely stale beads status nobody is working, so
    // they fold into the muted Unattended catch-all instead. Only Done filters archived worktrees;
    // every other bucket sums across all worktrees. Planned folds Loose in (Loose -> Planned,
    // decision #6). This single per-worktree predicate is the one source of truth: the bucket Count
    // sums it and Members keep every worktree whose contribution is > 0 — they can never diverge.
    let isActive w =
        w.CodingTool = CodingToolStatus.Working || w.CodingTool = CodingToolStatus.WaitingForUser

    let contributionFor kind (w: WorktreeStatus) =
        match kind with
        | TaskBucketKind.Planned    -> w.Planning.Planned + w.Planning.Loose
        | TaskBucketKind.Queued     -> if isActive w then w.Planning.Queued else 0
        | TaskBucketKind.InProgress -> if isActive w then w.Beads.InProgress else 0
        | TaskBucketKind.Blocked    -> w.Beads.Blocked
        | TaskBucketKind.Done       -> if w.IsArchived then 0 else w.Beads.Closed
        | TaskBucketKind.Unattended -> if isActive w then 0 else w.Beads.InProgress + w.Planning.Queued

    // Members of one task bucket, in repo/worktree order: every worktree whose contribution is > 0.
    let taskMembersFor kind =
        taggedWorktrees
        |> List.choose (fun (repoName, w) ->
            match contributionFor kind w with
            | c when c > 0 -> Some(memberOf repoName w c)
            | _ -> None)

    // Build members once per bucket, in canonical order; the count is Σ contribution over them.
    let taskGroups =
        taskOrder
        |> List.map (fun kind ->
            let members = taskMembersFor kind
            kind, members, members |> List.sumBy _.Contribution)

    let tasks =
        taskGroups
        |> List.choose (fun (kind, members, count) ->
            if count > 0 then Some { TaskBucket.Kind = kind; Count = count; Members = members } else None)

    // One true shared scale: the largest bucket count, 0 when every bucket is empty. Empty buckets
    // are 0 and never raise the max, so this equals the max across the non-empty buckets.
    let scale = taskGroups |> List.map (fun (_, _, count) -> count) |> List.max

    // Agent groups: red-dot WORKING agents (CodingTool = Working) grouped by their classified
    // activity, plus a distinct Waiting group (CodingTool = WaitingForUser). HasActiveSession is NOT
    // used — it also covers Done/Idle/WaitingForUser terminals, which would inflate Working with idle
    // and finished agents (spec Corrections v1.1 (h)). Each member contributes 1, so Count =
    // Members.Length. Empty groups omitted; Waiting sorts last.
    let agentMembersFor kind =
        taggedWorktrees
        |> List.choose (fun (repoName, w) ->
            let isMember =
                match kind with
                | AgentGroupKind.Activity activity ->
                    w.CodingTool = CodingToolStatus.Working && activityOf w = activity
                | AgentGroupKind.Waiting -> w.CodingTool = CodingToolStatus.WaitingForUser
            if isMember then Some(memberOf repoName w 1) else None)

    let agents =
        agentGroupOrder
        |> List.choose (fun kind ->
            match agentMembersFor kind with
            | [] -> None
            | members -> Some { AgentGroup.Kind = kind; Count = List.length members; Members = members })

    { Tasks = tasks; Agents = agents; Scale = scale }
