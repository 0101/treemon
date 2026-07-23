# Overview Activity History

## Overview

Treemon records the canonical live Overview as durable count-only snapshots on a UTC 30-second
grid. History contains observations captured while Treemon is running; it does not reconstruct
missed time, late inputs, or downtime.

## Goals

- Persist the same task and per-session agent projection shown by the live Overview.
- Keep captured 12-hour, 24-hour, and 72-hour history available across restart without requiring an
  open browser.
- Store only task and agent counts, never drill-down membership or session content.
- Keep capture and request work bounded independently of activity-event volume.
- Render history inside the existing Overview band without a charting dependency.

## Expected Behavior

### Canonical capture

- A dedicated serial background loop targets every UTC Unix-second boundary divisible by 30. It
  does not adapt to client activity level or scheduler polling intervals and never runs overlapping
  capture attempts.
- For boundary `B`, the capture uses `B` as `RepoAssemblyInputs.Now`, one immutable
  `RefreshScheduler.GetState` result, one set of assembly inputs, one
  `WorktreeApi.assembleRepos` result, and one `OverviewData.aggregate` result. Wall-clock reads
  performed later in the attempt cannot change the projection time.
- Tasks and agents are reduced from that same `Overview` value and committed atomically as one
  `OverviewSnapshot`.
- Capture is independent of the refresh scheduler's task-execution loop and cannot delay Git,
  Beads, PR, or worktree refresh work.
- A failed attempt creates no row. If the loop is not ready to start a boundary before a later
  boundary arrives, the unstarted boundary is missed; after every attempt the loop advances to the
  next future boundary and never overlaps, catches up, or repairs missed history.
- Startup performs no backfill or immediate synthetic capture. Existing rows are available
  immediately; a new database returns empty history until the first successful future boundary.
- Source events arriving after a boundary never alter an already captured snapshot.

### Durable snapshots

- SQLite stores one table:

  | Column | Contract |
  |---|---|
  | `bucket` | Primary-key UTC Unix seconds divisible by 30 |
  | `tasks` | Serialized `TaskCount list` |
  | `agents` | Serialized `AgentCount list` |

- Inserting a snapshot and pruning expired rows use one transaction.
- A bucket is insert-once: an existing primary-key row is never overwritten by a duplicate capture.
- On a successful insert for bucket `B`, pruning deletes rows with
  `bucket < B - 72 hours`; the inclusive cutoff row is retained. A gap does not change this rule.
- Treemon downtime produces no rows. Captured rows before the gap remain available.
- Legacy liveness, task-snapshot, rollup, staging, publication-state, and observation-bound history
  tables are removed. Existing reconstructed history is not migrated.
- `activity_events` remains unchanged for ingestion idempotency and is not read by Overview history.

### Sampling and API

- `getOverviewHistory` accepts 12 hours, 24 hours, or 72 hours and reads only the snapshot table.
- `Anchor` is the latest committed snapshot boundary. With no rows, the API returns a successful
  empty response anchored to `floor(UTC Unix seconds / 30) * 30`.
- The query considers the inclusive range `[Anchor - window, Anchor]`, which contains at most 8,641
  possible capture buckets for the 72-hour window.
- The response selects captured rows on an anchor-aligned bucket grid:

  | Window | Capture-bucket stride | Interval |
  |---|---:|---:|
  | 12 hours | 5 buckets | 2.5 minutes |
  | 24 hours | 10 buckets | 5 minutes |
  | 72 hours | 30 buckets | 15 minutes |

- A row is selected when `(Anchor.bucket - row.bucket) / 30` is divisible by the window stride.
  Missing buckets stay missing rather than shifting later samples onto a different grid.
- Responses contain at most 289 snapshots ordered oldest-first. Consecutive equal snapshots may be
  collapsed, retaining the earliest timestamp in each equal run.
- Missing intervals remain absent from storage and the response; there is no raw fallback,
  reconstruction, publication availability state, generation, dirty range, repair, or server cache.

### In-band charts

- One button cycles `Hidden -> 12h -> 24h -> 72h -> Hidden`; the state is client-only and resets on
  reload.
- History and drill-down are mutually exclusive. Opening either closes the other.
- The client fetches immediately when a chart opens and refreshes at most every 30 seconds while
  visible, measured from each request attempt whether it succeeds or fails.
- When switching windows, the installed chart remains mounted until the matching response succeeds.
  A stale or failed response cannot replace the selected chart, and an equal-anchor response
  preserves the already installed data.
- Agents and Tasks render stepped stacked-area charts below their live sections. The existing
  stepped carry-forward rendering visually spans missing capture or downtime intervals.
- Hover shows a snapped crosshair, non-empty series, total, and absolute local time. Static geometry
  is reused until the window, anchor, or snapshots change.

## Technical Approach

### Shared projection

`src/Shared/OverviewData.fs` owns the live aggregate and count-only history types. Snapshot capture
reuses the existing complete `WorktreeApi` assembly path and calls `OverviewData.aggregate`; it does
not introduce a second history-specific task or agent projector.

A leaner projection may replace the complete assembly only if both live Overview and history share
the same implementation.

### Snapshot store and capture

A shared snapshot-boundary module owns the canonical resolution, exact-boundary validation, floor,
and next-future-boundary arithmetic. The history store uses it for validation, sampling, and empty
anchors while owning table creation, atomic insert/prune, bounded window reads, serialization,
retention, and insert-once behavior. Its latest-anchor and sampled-row read is one SQLite statement,
so both come from the same read snapshot; equal runs are collapsed after the bounded read. A
separate capture component uses the same boundary module and obtains immutable scheduler state
through `GetState`.

Capture failures are logged and isolated to the affected boundary. The store has no staging,
publication generations, recovery worker, or reconstruction dependency.

### Lifecycle and API

`Program` starts and stops the capture component with the server runtime but never blocks HTTP
startup on history availability. `WorktreeApi.getOverviewHistory` performs one bounded snapshot
query and returns the existing `OverviewHistoryResponse` wire type.

The refresh scheduler no longer receives history assembly or persistence callbacks and performs no
history work inside its task loop.

## Decisions

| Decision | Choice |
|---|---|
| Authoritative data | The canonical `OverviewData.aggregate` result observed at capture time |
| Storage | One durable SQLite `overview_snapshots` table |
| Canonical resolution | 30 seconds |
| Boundary arithmetic | One shared server module for capture, validation, sampling, and empty anchors |
| Capture cadence | Independent fixed boundary loop |
| Stored shape | Count-only tasks and agents captured atomically |
| Retention | Exactly 72 hours |
| Startup | Existing rows or empty history; no backfill |
| Missed time | Leave gaps; never catch up or reconstruct |
| Late events | Do not modify captured snapshots |
| Activity events | Keep unchanged for event-ID idempotency |
| Request path | Snapshot table only; no raw fallback or cache |
| Rendering gaps | Keep stepped carry-forward behavior |
| Wire contract | Keep `OverviewHistoryResponse` and 12h/24h/72h windows |

## Key Files

| File | Role |
|---|---|
| `src/Shared/OverviewData.fs` | Canonical aggregate, count types, windows, and response |
| `src/Shared/WorktreeApi.fs` | `getOverviewHistory` API contract |
| `src/Server/OverviewSnapshotBoundary.fs` | Canonical resolution and boundary arithmetic |
| `src/Server/OverviewSnapshotStore.fs` | Direct snapshot schema, migration, retention, and bounded reads |
| `src/Server/OverviewSnapshotCapture.fs` | Serial capture scheduling, shared projection, and atomic capture |
| `src/Server/WorktreeApi.fs` | Shared repo assembly and bounded history query |
| `src/Server/RefreshScheduler.fs` | Immutable live state and `GetState` |
| `src/Server/Program.fs` | Capture lifecycle |
| `src/Client/App.fs` | Window state, fetching, and refresh cadence |
| `src/Client/OverviewChart.fs` | Stepped chart geometry and hover |
| `src/Client/OverviewBand.fs` | In-band chart placement and controls |

## Related Specs

- `docs/spec/session-status-push.md` - live session ingestion and event idempotency.
- `docs/spec/beads-overview-band.md` - canonical live Overview aggregation.
- `docs/spec/overview-drilldown.md` - the mutually exclusive detail view.
- `docs/spec/user-idle-detection.md` - adaptive dashboard polling, intentionally separate from
  history capture cadence.
