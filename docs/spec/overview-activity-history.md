# Overview Activity History

## Overview

Treemon records the count-only Overview roll-up and renders stepped 12-hour, 24-hour, or 72-hour charts
inside the Overview band. Historical agent values use the same per-session projection as the live
Overview, so multiple sessions in one worktree contribute identically in both views.

Tasks and Agents use different durable sources: task counts are snapshotted when they change, while
agent counts are reconstructed from status-bearing activity events plus a compact liveness timeline.
The API samples both dimensions at a fixed resolution for the requested window and returns the
server-side sample anchor with the resulting `OverviewSnapshot` timeline.

## Goals

- Keep 12-hour, 24-hour, and 72-hour Overview history available from durable server data without an
  open browser.
- At every emitted sample boundary, produce the same task and per-session agent counts as the live
  `OverviewData` projection would produce for the state active at that instant.
- Reconstruct agent history from the existing activity/liveness stream instead of persisting a
  second agent snapshot stream.
- Retain only count data, preserve the baseline required to reconstruct retained history, and never
  expose drill-down membership.
- Render the history inside the existing Overview band without a charting dependency.
- Bound every response to 289 snapshots and keep server, payload, and browser work independent of
  raw event volume.
- Reuse static chart geometry across unrelated dashboard updates and hover movement.

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
- `getOverviewHistory` accepts a 12-hour, 24-hour, or 72-hour window and returns only that window.
  The store prunes redundant history after 60 days but retains one pre-cutoff status baseline per
  still-retained session and one task baseline.
- Task reads include the latest snapshot before the requested window and carry it to the left edge.
  For each session observed in the window or lookback, agent reads include its latest pre-window
  status event plus liveness from one `openWindow` before the edge.
- Task, status, and liveness inputs for one response are read from one SQLite snapshot, so a
  concurrent write cannot return liveness without the corresponding status baseline.
- Each window is divided into 288 equal buckets: 2.5 minutes for 12 hours, 5 minutes for 24 hours,
  and 15 minutes for 72 hours. The response contains the left-edge baseline plus at most one
  right-edge sample per bucket, with consecutive equal snapshots collapsed.
- A bucket uses the complete Tasks and Agents state active at its right edge. Counts remain whole
  observed states; they are never averaged or independently maximized. Changes that begin and end
  between two sample boundaries may be omitted.
- Records timestamped exactly at a sample boundary are applied before that boundary is emitted.
  Every returned timestamp is between `anchor - window` and `anchor`, inclusive.
- A shared 30-second cache is keyed by window. Concurrent callers for the same missing or stale
  entry await one in-flight computation and receive the same server anchor and snapshots. An entry
  expires 30 seconds after its response anchor; failed computations are removed so the next caller
  can retry.

### In-band charts

- One button cycles `Hidden -> 12h -> 24h -> 72h -> Hidden`; the state is client-only and resets on
  reload.
- History and drill-down are mutually exclusive: opening either closes the other.
- The client fetches immediately when the chart opens and refreshes it at most every 30 seconds while
  visible. The server response anchor fixes the chart's right edge and remains consistent for every
  caller sharing a cached response.
- A response is installed only if its requested window is still selected; a slower response for a
  previously selected window cannot replace the current chart.
- Agents and Tasks each render a stepped stacked-area chart directly below their live section. A
  chart is omitted when that live section is empty.
- The left edge carries the value active at the window start; the right edge holds the latest value
  to the fetch time. Hover shows a snapped crosshair, non-empty series, total, and relative time.
- Static chart geometry is reused until the window, server anchor, or snapshots change. Hover updates
  are frame-coalesced and update only the crosshair and tooltip.

### Acceptance bounds

- Against a synthetic 72-hour history containing at least 40,000 status events across at least 130
  sessions plus liveness and task changes, an uncached computation completes within 1,000 ms,
  returns at most 289 ordered snapshots, and serializes below 250 KB.
- A repeated request for the same window during the cache lifetime returns the same anchor and
  snapshots within 100 ms, and simultaneous callers trigger one computation.
- For a loaded 24-hour chart, a 10-second idle measurement has at most 1,500 ms of main-thread task
  time, at most 1,500 ms of script time, and no task lasting 50 ms or longer.
- Thirty measured pointer moves complete within 1,000 ms total with no task lasting 50 ms or longer,
  while tooltip and crosshair values remain correct.

## Technical Approach

### Shared count model

`OverviewData` owns `TaskCount`, `AgentCount`, `OverviewSnapshot`, the requested history-window type,
and the anchored history response. Count snapshots omit `Members` and `Scale`; history exposes only
the sampled count timeline. Client-only selection state, labels, and CSS mappings live in
`OverviewPresentation`.

### Server reconstruction

`RefreshScheduler` receives injected task assembly and persistence functions because the scheduler
is compiled before the worktree API modules. It threads the last persisted Tasks projection through
its immutable loop and writes only changes. History assembly and persistence have their own guarded
failure boundary, so they cannot prevent overdue Git, beads, PR, or worktree refresh work.

`OverviewHistory` divides the selected window into 288 buckets and processes status events,
liveness observations, openness boundaries, and sample boundaries in chronological order. The
immutable sweep updates only the affected session between samples. At each sample boundary it applies
`CodingToolStatus.collapseByWorktree` and `OverviewData.agentCountsOf` to the current open-session
state, while task counts carry forward from the latest task snapshot.

`WorktreeApi.getOverviewHistory` queries only the requested window plus its edge lookback from one
SQLite read snapshot, obtains the anchored sampled timeline through the per-window cache, and
returns the shared response.

### Client rendering

`App` owns the optional ephemeral window, anchored response, and refresh throttle. `OverviewChart`
contains the pure geometry and tooltip logic plus the Feliz SVG component; its static geometry is
memoized separately from frame-coalesced hover state. `OverviewBand` places each chart under its
corresponding live section.

## Decisions

| Decision | Choice |
|---|---|
| Agent persistence | Derive status from `activity_events` and openness from `session_liveness`; do not store agent snapshots. |
| Task persistence | Snapshot on count changes because task state has no event stream. |
| Historical unit | Count sessions exactly as the live Overview does, not collapsed worktrees. |
| Stored shape | Persist counts only; omit drill-down members and derive chart scale. |
| Window | Request 12h, 24h, or 72h explicitly; divide every window into 288 equal buckets. |
| Quantization | Sample the complete state at each bucket's right edge; brief sub-bucket states may be omitted. |
| Openness sweep | Coalesce dense observations into actual session-close boundaries; ignore a stale close after later liveness extends `LastSeen`. |
| History read consistency | Read task, status, and liveness inputs in one SQLite transaction per uncached computation. |
| Response anchor | Use the server computation anchor so cached callers render the same timeline edges. |
| Client refresh gate | Track the identified in-flight request separately; use the installed server anchor for normal cadence and the last request time only for failure retry backoff. |
| Cache | Cache one in-flight/completed response per window until 30 seconds after its server anchor. |
| Rendering | Hand-written stepped SVG with memoized static geometry and frame-coalesced hover. |
| Client geometry key | Rebuild only when chart kind, selected window, server anchor, or snapshot-list identity changes; commit only the latest hover candidate per animation frame and suppress repeated sampled points. |
| Hover scheduler ownership | Keep animation-frame refs and cancellation inside a component-local hook; use functional React state updates for same-sample suppression. |
| Tooltip coordinates | Position the HTML tooltip in an unpadded chart stage shared with the responsive SVG so its anchor matches the SVG crosshair at both plot edges. |
| Detail state | History and drill-down are ephemeral and mutually exclusive. |

## Key Files

| File | Role |
|---|---|
| `src/Shared/OverviewData.fs` | Count-only history types, requested windows, anchored responses, and shared agent grouping. |
| `src/Client/OverviewPresentation.fs` | Client-only selection, labels, and CSS mappings. |
| `src/Shared/WorktreeApi.fs` | `getOverviewHistory` API contract. |
| `src/Server/OverviewHistory.fs` | Fixed-resolution sampling and chronological task/agent reconstruction. |
| `src/Server/OverviewHistoryCache.fs` | Per-window 30-second cache and in-flight request deduplication. |
| `src/Server/SessionActivityStore.fs` | `activity_events`, `session_liveness`, and `task_snapshots` persistence. |
| `src/Server/RefreshScheduler.fs` | Continuous task snapshot capture. |
| `src/Server/WorktreeApi.fs` | Window-aware history query, cache integration, and anchored response. |
| `src/Client/App.fs` | Window state, fetching, and refresh throttle. |
| `src/Client/OverviewChart.fs` | Stepped chart geometry, SVG, and tooltip. |
| `src/Client/OverviewBand.fs` | In-band chart placement and controls. |

## Related Specs

- `docs/spec/session-status-push.md` - session events, openness, and durable activity storage.
- `docs/spec/beads-overview-band.md` - live Overview aggregation and presentation.
- `docs/spec/overview-drilldown.md` - the mutually exclusive detail view.
