# Overview Activity History

## Overview

Treemon records the count-only Overview roll-up and renders stepped 24-hour or 72-hour charts
inside the Overview band. Historical agent values use the same per-session projection as the live
Overview, so multiple sessions in one worktree contribute identically in both views.

Tasks and Agents use different durable sources: task counts are snapshotted when they change, while
agent counts are reconstructed from status-bearing activity events plus a compact liveness timeline.
The API merges both dimensions into one `OverviewSnapshot` timeline.

## Goals

- Preserve a server-side Overview history without requiring an open browser.
- Make historical task and agent counts match the live Overview projection at capture time.
- Reuse the push event stream instead of maintaining a second agent snapshot stream.
- Keep storage bounded and expose only count data, never drill-down membership.
- Show history in place without adding a charting dependency.

## Expected Behavior

### Historical data

- The scheduler writes the count-only Tasks projection to `task_snapshots` when it changes. The first
  projection establishes a baseline; unchanged consecutive values are not written.
- Agents are derived from `activity_events` plus `session_liveness`. Events establish status and
  skill; heartbeat and usage points extend `last_seen` without fabricating status transitions. Each
  session is filtered through the same openness rules as the live card, then counted from the same
  per-session projection used by `OverviewData.aggregate`.
- A worktree with multiple open sessions contributes each session independently, including sessions
  split across different activity, Waiting, or Idle groups.
- `getOverviewHistory` returns at most 72 hours. The store prunes redundant history after 60 days but
  retains one pre-cutoff status baseline per still-retained session and one task baseline.
- Task reads include the latest snapshot before the requested window and carry it to the left edge.
  Agent reads include each session's latest status event before the window plus liveness from one
  `openWindow` before the edge.
- `mergeHistory` carries the latest Tasks and Agents values forward at every change point. Before a
  dimension has a value, it is empty.

### In-band charts

- One button cycles `Hidden -> 24h -> 72h -> Hidden`; the state is client-only and resets on reload.
- History and drill-down are mutually exclusive: opening either closes the other.
- The client fetches immediately when the chart opens and refreshes it at most every 30 seconds while
  visible. The fetch time anchors the chart's right edge.
- Agents and Tasks each render a stepped stacked-area chart directly below their live section. A
  chart is omitted when that live section is empty.
- The left edge carries the value active at the window start; the right edge holds the latest value
  to the fetch time. Hover shows a snapped crosshair, non-empty series, total, and relative time.

## Technical Approach

### Shared count model

`OverviewData` owns `TaskCount`, `AgentCount`, and `OverviewSnapshot`. These omit `Members` and
`Scale`; history tracks count transitions only. Client-only selection state, labels, and CSS mappings
live in `OverviewPresentation`.

### Server reconstruction

`RefreshScheduler` receives injected task assembly and persistence functions because the scheduler
is compiled before the worktree API modules. It threads the last persisted Tasks projection through
its immutable loop and writes only changes. History assembly and persistence have their own guarded
failure boundary, so they cannot prevent overdue Git, beads, PR, or worktree refresh work.

`OverviewHistory.deriveAgents` receives each session's latest status event before the requested
window, all in-window status events, and the liveness points needed for openness. It evaluates
status/skill transitions, liveness observations, and openness-decay boundaries, finds each session's
latest state and `last_seen` by timestamp, applies
`CodingToolStatus.collapseByWorktree`, and counts the resulting per-session statuses with
`OverviewData.agentCountsOf`. Consecutive identical count vectors are collapsed.

`WorktreeApi.getOverviewHistory` queries task snapshots and activity events, derives the agent
timeline, and combines both with `OverviewHistory.mergeHistory`.

### Client rendering

`App` owns the ephemeral window, fetched snapshots, refresh throttle, and right-edge timestamp.
`OverviewChart` contains the pure windowing and tooltip logic plus the Feliz SVG component;
`OverviewBand` places each chart under its corresponding live section.

## Decisions

| Decision | Choice |
|---|---|
| Agent persistence | Derive status from `activity_events` and openness from `session_liveness`; do not store agent snapshots. |
| Task persistence | Snapshot on count changes because task state has no event stream. |
| Historical unit | Count sessions exactly as the live Overview does, not collapsed worktrees. |
| Stored shape | Persist counts only; omit drill-down members and derive chart scale. |
| Window | Serve 72 hours; the client narrows to 24 hours when selected. |
| Rendering | Hand-written stepped SVG using the existing Overview palette. |
| Detail state | History and drill-down are ephemeral and mutually exclusive. |

## Key Files

| File | Role |
|---|---|
| `src/Shared/OverviewData.fs` | Count-only history types and shared agent grouping. |
| `src/Client/OverviewPresentation.fs` | Client-only selection, labels, and CSS mappings. |
| `src/Shared/WorktreeApi.fs` | `getOverviewHistory` API contract. |
| `src/Server/OverviewHistory.fs` | Task change detection, agent reconstruction, and timeline merge. |
| `src/Server/SessionActivityStore.fs` | `activity_events`, `session_liveness`, and `task_snapshots` persistence. |
| `src/Server/RefreshScheduler.fs` | Continuous task snapshot capture. |
| `src/Server/WorktreeApi.fs` | 72-hour history query and merge. |
| `src/Client/App.fs` | Window state, fetching, and refresh throttle. |
| `src/Client/OverviewChart.fs` | Stepped chart geometry, SVG, and tooltip. |
| `src/Client/OverviewBand.fs` | In-band chart placement and controls. |

## Related Specs

- `docs/spec/session-status-push.md` - session events, openness, and durable activity storage.
- `docs/spec/beads-overview-band.md` - live Overview aggregation and presentation.
- `docs/spec/overview-drilldown.md` - the mutually exclusive detail view.
