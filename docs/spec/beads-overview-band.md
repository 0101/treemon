# Beads Overview Band

## Goals

- Summarize task state and live agent activity across all non-archived worktrees.
- Distinguish planned work, work currently owned by agents, unattended work, and work ready to land.
- Let users drill from an aggregate directly to its contributing worktrees.
- Reuse one canonical aggregate for the live band, drill-down membership, and durable history.

## Expected Behavior

### Band

The header **Overview** control toggles a persisted, chrome-less band above the repository list. The
band contains Agents and Tasks sections, omits empty groups, and renders nothing when the complete
aggregate is empty.

The Agents section becomes a compact sticky strip while the dashboard scrolls. Each group shows one
marker per live session. A reported context gauge renders as a remaining-context donut; otherwise
the session uses a solid status marker.

The Tasks section shows one proportional bar per non-empty bucket. All bars use the largest task
bucket as one shared linear scale.

### Agent groups

Grouping is per session, not per worktree. One worktree may therefore contribute sessions to several
groups.

- Working sessions are grouped by `Activity.classify` into Investigating, Planning, Executing,
  Reviewing, PR, or the generic Working fallback.
- WaitingForUser sessions form a distinct Waiting group.
- Open Idle sessions form a distinct Idle group.
- NoSession contributes nothing.

### Task buckets

Feature containers never count as task units. The planning projection classifies non-feature issues
from their status and parent-feature relationship.

| Bucket | Definition |
|---|---|
| Planned | Open tasks under an open feature, plus loose open tasks |
| Underway | In-progress tasks and open tasks under an in-progress feature, only on worktrees with a Working or WaitingForUser session |
| Blocked | Explicitly blocked non-feature tasks |
| Done | Closed tasks on a worktree that still has unfinished task work |
| To land | Closed tasks on a worktree with no remaining planned, underway, or blocked task work |
| Unattended | Underway-shaped work on a worktree with no active agent |

Done and To land partition closed task work. Underway and Unattended partition started task work by
whether an agent is active.

### Drill-down

Selecting an agent group or task bucket opens one ephemeral breakdown panel below its section.
Selecting it again, pressing Escape, closing the panel, hiding Overview, or opening history clears
the selection.

Members are grouped by repository. Agent groups render one worktree chip carrying the matching
sessions; task groups render one worktree row whose bar uses the same shared scale as the aggregate.
Each group's count equals the sum of its member contributions.

Selecting a member expands its repository, focuses the worktree card, and scrolls it into view. It
does not open the Canvas pane. Archived or no-longer-focusable worktrees are never selected.

### History

The history control and drill-down are mutually exclusive. History uses count-only snapshots from
the same aggregate; see `docs/spec/overview-activity-history.md`.

## Technical Approach

`BeadsStatus` reads the canonical beads issue export once per refresh and derives both the card
summary and the feature-free planning projection. Parent-child edges determine whether an open task
is Planned, Queued, or Loose.

`OverviewData.aggregate` is the single pure aggregation boundary. It accepts the server-shaped
`RepoWorktrees` list, removes archived worktrees, builds canonical task and agent groups, and stores
the contributing worktrees in `GroupMember`. Counts are derived from those same members, preventing
the band and drill-down from diverging. History drops membership and persists only group kinds and
counts.

Agent grouping consumes each `WorktreeStatus.Sessions` entry from the push-based session model.
Skill classification is pure shared logic; no session-log detector or second activity source exists.

`OverviewBand` renders the sticky live sections, breakdown panel, and history controls.
`OverviewPresentation.OverviewSelection` models the single selected agent or task group. App update
logic owns selection clearing, worktree navigation, and persisted open state.

## Decisions

- **Aggregate non-archived worktrees only:** archiving removes every task and agent contribution.
- **Per-session agent grouping:** concurrent sessions in one worktree retain their own status, skill,
  and context gauge.
- **One task scale:** aggregate and drill-down bars remain directly comparable.
- **Membership with the aggregate:** counts and drill-down rows cannot use different predicates.
- **Task state, not PR state, decides To land:** slower PR refreshes cannot make task buckets flap.
- **No per-card activity stripe:** cards retain their normal coding-tool status presentation.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/BeadsStatus.fs` | Beads summary and planning projection |
| `src/Shared/OverviewData.fs` | Canonical task/agent aggregate and membership |
| `src/Client/OverviewPresentation.fs` | Labels, styles, and selection type |
| `src/Client/OverviewBand.fs` | Band, sticky behavior, drill-down, and history placement |
| `src/Client/App.fs` | Toggle, selection, navigation, and history state |
| `src/Server/SessionActivityService.fs` | Push-based per-session state consumed by the aggregate |

## Related Specs

- `docs/spec/overview-activity-history.md` - durable count-only history.
- `docs/spec/session-status-push.md` - live session status, skill, and context usage.
- `docs/spec/worktree-monitor.md` - dashboard layout and worktree data.
- `docs/spec/beadspace-canvas.md` - per-worktree beads SystemView.
