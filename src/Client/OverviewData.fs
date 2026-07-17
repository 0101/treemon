module OverviewData

// Pure cross-worktree aggregation behind the Overview band (spec: docs/spec/beads-overview-band.md).
// Folds every monitored NON-ARCHIVED worktree into the band's two aggregate lenses (archived
// worktrees are dropped up front, so they contribute to nothing — no task bucket and no agent group):
//   - Tasks: the status buckets (Planned · Queued · In progress · Blocked · Done · Unattended), each
//     a cross-worktree sum. Planned folds in Loose (decision #6). In progress and Queued count only
//     where the worktree has an ACTIVE agent (CodingTool = Working or WaitingForUser); on an inactive
//     worktree those tasks are likely stale beads status and fold into the muted Unattended catch-all
//     instead. Every other bucket sums across all (non-archived) worktrees.
//   - Agents: red-dot WORKING agents (CodingTool = Working) grouped by the skill each is running,
//     classified through the shared Shared.Activity.classify, PLUS a distinct Waiting group for
//     agents parked on the user (CodingTool = WaitingForUser, yellow dot), PLUS a distinct Idle
//     group for agents with an open-but-idle session (CodingTool = Idle, blue dot). NoSession (grey,
//     no open session) is excluded, and terminal presence (HasActiveSession) never inflates the counts.
// Empty buckets/groups are omitted (never surfaced as a 0), and Scale is the one true shared linear
// scale — the largest task-bucket count — so the view sizes every bar against a single denominator.
//
// Pure and Fable-safe: no IO, no Model/RepoModel dependency. It folds the Shared RepoWorktrees shape
// (every worktree present, archived ones flagged via IsArchived) rather than the client RepoModel,
// which already splits archived worktrees into a separate field. aggregate owns the archived policy:
// it excludes IsArchived worktrees from the whole roll-up, so callers can hand it the un-split list
// (archived flagged) and let aggregate drop them.

open Shared

/// A cross-worktree task status bucket. RequireQualifiedAccess keeps Done/InProgress/Blocked from
/// colliding with the BeadsSummary field labels and other DU cases (same reason
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
/// stable RepoId (the identity the drill-down groups/counts/keys repo blocks on — two distinct repos
/// that share a folder name stay distinct), the owning repo's RootFolderName (RepoName — display
/// label only, preserved before the aggregate flattens repos away), and Contribution — how much this
/// worktree adds to the group's Count (agent group: always 1; task bucket: this worktree's task count
/// in the bucket). A worktree is a group member iff its Contribution > 0.
type GroupMember =
    { ScopedKey: string
      Branch: string
      RepoId: RepoId
      RepoName: string
      /// When the agent entered its current state — used to show the per-agent "time in category"
      /// in the agent-group drill-down. Set (from the worktree's CodingToolSince) only for agent-group
      /// members; always None for task-bucket members (passed explicitly, so the contract holds by
      /// construction rather than convention).
      Since: System.DateTimeOffset option
      /// The live sessions for this agent's worktree, each carrying its own status + context usage —
      /// the source of the per-session donuts drawn in the Agents row and the drill-down chip. Set
      /// (from the worktree's Sessions) only for agent-group members; always [] for task-bucket
      /// members. Empty also for an agent whose worktree reports no live session.
      Sessions: SessionDot list
      Contribution: int }

/// One non-empty task bucket: its kind, cross-worktree count, and the member worktrees that make it
/// up (built from the SAME per-worktree predicate as the count, so Count = Σ member Contribution can
/// never diverge). Empty buckets are dropped from the roll-up entirely (the band never shows a 0), so
/// a present bucket always has Count > 0 and a non-empty Members list.
type TaskBucket = { Kind: TaskBucketKind; Count: int; Members: GroupMember list }

/// What an agent group represents: a skill-derived activity (a red-dot WORKING agent), the distinct
/// "waiting for user" state (yellow dot), or the "idle" state (blue dot — an agent with an
/// open-but-idle session, CodingTool = Idle). Modeled as a kind so the band can render Waiting and
/// Idle alongside the activity groups; Idle is a second track sharing the same Agents row.
[<RequireQualifiedAccess>]
type AgentGroupKind =
    | Activity of CurrentActivity
    | Waiting
    | Idle

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

/// Which Overview-band group the drill-down panel is currently showing. Single-select across both
/// sections (at most one is set): an agent group (Active agents section) or a task bucket (Tasks
/// section). Ephemeral session state — never persisted (unlike OverviewPanelOpen).
[<RequireQualifiedAccess>]
type OverviewSelection =
    | Agents of AgentGroupKind
    | Tasks of TaskBucketKind

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
      CurrentActivity.PR
      CurrentActivity.Working ]

// Canonical order of the agent groups: the activity groups (in the order above), then the Waiting
// group, then the Idle group LAST — the blue-dot idle (open-but-idle) agents trail the live
// (working/waiting) ones.
let private agentGroupOrder =
    (activityOrder |> List.map AgentGroupKind.Activity) @ [ AgentGroupKind.Waiting; AgentGroupKind.Idle ]

/// The activity a WORKING worktree's current skill classifies to. An absent skill classifies to
/// Working (classify normalizes "" -> Working), matching the spec's "red-dot agent, no recognized
/// skill -> generic Working group".
let private activityOf (wt: WorktreeStatus) =
    Activity.classify (wt.CurrentSkill |> Option.defaultValue "")

/// Fold every worktree across every repo into the Overview roll-up (spec: beads-overview-band.md).
let aggregate (repos: RepoWorktrees list) : Overview =
    // Drop archived worktrees first: archiving removes a worktree from the entire roll-up, so it
    // contributes to no task bucket and no agent group. Then tag each remaining worktree with its
    // owning repo's stable RepoId and display RootFolderName BEFORE flattening, so every GroupMember
    // can carry both its repo identity (RepoId) and its display label (RepoName). Repo order and
    // within-repo worktree order are preserved, so member lists come back in the band's repo/worktree
    // order.
    let taggedWorktrees =
        repos
        |> List.collect (fun r ->
            r.Worktrees
            |> List.filter (fun w -> not w.IsArchived)
            |> List.map (fun w -> r.RepoId, r.RootFolderName, w))

    let memberOf repoId repoName since sessions (w: WorktreeStatus) contribution =
        { ScopedKey = WorktreePath.value w.Path
          Branch = w.Branch
          RepoId = repoId
          RepoName = repoName
          Since = since
          Sessions = sessions
          Contribution = contribution }

    // A worktree's contribution to one task bucket. In-progress and Queued only count toward their
    // live buckets when their worktree has an ACTIVE agent (CodingTool = Working or WaitingForUser);
    // on an inactive worktree (Idle/NoSession) they are likely stale beads status nobody is working, so
    // they fold into the muted Unattended catch-all instead. Archived worktrees never reach here
    // (dropped when building taggedWorktrees), so every bucket sums only non-archived worktrees.
    // Planned folds Loose in (Loose -> Planned, decision #6). This single per-worktree predicate is
    // the one source of truth: the bucket Count sums it and Members keep every worktree whose
    // contribution is > 0 — they can never diverge.
    let isActive w =
        w.CodingTool = CodingToolStatus.Working || w.CodingTool = CodingToolStatus.WaitingForUser

    let contributionFor kind (w: WorktreeStatus) =
        match kind with
        | TaskBucketKind.Planned    -> w.Planning.Planned + w.Planning.Loose
        | TaskBucketKind.Queued     -> if isActive w then w.Planning.Queued else 0
        | TaskBucketKind.InProgress -> if isActive w then w.Beads.InProgress else 0
        | TaskBucketKind.Blocked    -> w.Beads.Blocked
        | TaskBucketKind.Done       -> w.Beads.Closed
        | TaskBucketKind.Unattended -> if isActive w then 0 else w.Beads.InProgress + w.Planning.Queued

    // Members of one task bucket, in repo/worktree order: every worktree whose contribution is > 0.
    let taskMembersFor kind =
        taggedWorktrees
        |> List.choose (fun (repoId, repoName, w) ->
            match contributionFor kind w with
            | c when c > 0 -> Some(memberOf repoId repoName None [] w c)
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
    // activity, a distinct Waiting group (CodingTool = WaitingForUser), and a distinct Idle group
    // (CodingTool = Idle — the blue-dot agents with an open-but-idle session, each carrying
    // CodingToolSince for the time-since-idle chip). HasActiveSession is NOT used — it also covers
    // Idle terminals, which would inflate the counts with parked agents; NoSession (grey, no open
    // session) is excluded entirely (not an agent). Each member contributes 1, so Count =
    // Members.Length. Empty groups omitted; Waiting then Idle sort last.
    let agentMembersFor kind =
        taggedWorktrees
        |> List.choose (fun (repoId, repoName, w) ->
            let isMember =
                match kind with
                | AgentGroupKind.Activity activity ->
                    w.CodingTool = CodingToolStatus.Working && activityOf w = activity
                | AgentGroupKind.Waiting -> w.CodingTool = CodingToolStatus.WaitingForUser
                | AgentGroupKind.Idle -> w.CodingTool = CodingToolStatus.Idle
            if isMember then Some(memberOf repoId repoName w.CodingToolSince w.Sessions w 1) else None)

    let agents =
        agentGroupOrder
        |> List.choose (fun kind ->
            match agentMembersFor kind with
            | [] -> None
            | members -> Some { AgentGroup.Kind = kind; Count = List.length members; Members = members })

    { Tasks = tasks; Agents = agents; Scale = scale }
