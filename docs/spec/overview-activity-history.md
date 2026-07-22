# Overview Activity History

## Overview

Treemon records count-only Overview history on a durable canonical 30-second grid and renders
stepped 12-hour, 24-hour, or 72-hour charts inside the Overview band. Raw activity events, liveness,
and task snapshots remain authoritative repair inputs; normal history requests read only published
rollups.

## Goals

- Keep 12-hour, 24-hour, and 72-hour history available from durable server data without an open
  browser.
- Match the live task and per-session agent projections at every emitted boundary.
- Make normal uncached request work, allocations, and database reads independent of raw history
  volume.
- Publish only coherent generations across late writes, concurrent ingestion, compaction, restart,
  migration, and retention.
- Retain count data only and never expose drill-down membership.
- Render history inside the existing Overview band without a charting dependency.

## Expected Behavior

### Durable history

- `activity_events`, `session_liveness`, and `task_snapshots` remain the authoritative sources for
  rebuilding history. The rollup tables are disposable derived state.
- Published rollups contain only `TaskCount` and `AgentCount` values at canonical UTC boundaries
  divisible by 30 seconds. No session IDs, worktree IDs, messages, titles, or members are stored.
- Events and task changes timestamped exactly at a boundary apply before that boundary is emitted.
  Historical agent values preserve the stored post-event semantics used by the current pure sampler,
  including for out-of-order events.
- Agent counts use the same openness rules and `OverviewData.agentCountsOf` projection as live
  Overview. Multiple sessions in one worktree count independently.
- Task snapshot change detection, event insertion, and liveness insertion update source generation
  and the earliest dirty boundary in the same SQLite transaction. Duplicate or unchanged writes do
  neither; liveness is inserted only when it advances the durable session observation time.
- The first affected boundary is the smallest canonical boundary greater than or equal to the source
  timestamp. It is clamped to the oldest retained boundary needed by the currently exposed 72-hour
  horizon, so arbitrarily old late writes repair the visible baseline without creating unbounded
  work.
- Activity and liveness writes update each session's earliest and latest observation bounds in the
  same transaction. Reconstruction uses those bounds only to exclude sessions that cannot affect a
  candidate range; they are derived state and are rebuilt with the rollups.
- A late source write invalidates its first affected boundary and every later published boundary.
  The currently published generation remains available until the repaired generation is complete.
- Initial schema migration and exposed-horizon backfill complete before history requests are served.
  There is no raw reconstruction fallback and no partially available history response. If the
  initial rebuild cannot complete, real-mode startup fails before binding the HTTP server.
- Derived-state schema changes or detected corruption discard and rebuild rollups idempotently from
  retained raw sources. A failed runtime repair leaves the last coherent generation published and is
  retried.
- Rollups retain the exposed 72-hour horizon plus the predecessor needed for the left-edge baseline.
  Raw-source retention preserves its existing baselines and cannot prune data required by an
  in-progress rebuild.

### Sampling and API

- `getOverviewHistory` accepts 12 hours, 24 hours, or 72 hours. Its anchor is the latest completely
  published 30-second boundary. A healthy caught-up worker wakes for every new boundary and leaves
  the anchor at most one 30-second interval behind wall clock after each completed cycle.
- The response samples the canonical grid at fixed strides:

  | Window | Grid stride | Sample interval |
  |---|---:|---:|
  | 12 hours | 5 rows | 2.5 minutes |
  | 24 hours | 10 rows | 5 minutes |
  | 72 hours | 30 rows | 15 minutes |

- Each response contains the left-edge baseline and up to 288 later samples, for at most 289 ordered
  snapshots. Consecutive equal snapshots are collapsed.
- Every returned timestamp is between `Anchor - Window` and `Anchor`, inclusive. Counts are complete
  observed states, never averages or independently selected maxima.
- A normal uncached request opens one SQLite read snapshot, reads rollup publication metadata, and
  reads at most 289 rows from the published rollup. It reads no raw `activity_events`,
  `session_liveness`, or `task_snapshots` rows.
- In real mode, missing publication state is an availability error rather than an empty successful
  history. Demo and fixture modes may continue returning their existing empty history response.
- Cache entries are keyed by history window, published generation, and complete-through bucket.
  Concurrent callers for one missing key share one computation. Publication or repair naturally
  invalidates prior entries; failed computations are removed for retry.
- The API wire type remains `OverviewHistoryResponse`; only `Anchor` changes from an arbitrary
  request instant to the latest published grid boundary.

### Publication and repair

- Each reconstruction batch contains at most 512 canonical boundaries, uses one stable SQLite read
  snapshot, and stages candidate rows under the source generation it observed. Multi-batch work is
  coherent because every batch carries the same generation and publication verifies it again.
- Publication uses one write transaction that verifies the source generation is unchanged, applies
  the complete candidate range, advances completeness and published generation, and clears only the
  dirty range covered by that generation.
- A concurrent source write either invalidates the candidate before publication or executes after
  publication and creates a new dirty range. It cannot be lost by dirty-marker clearing.
- Readers observe either the previous published generation or the replacement generation through one
  SQLite snapshot, never a mixture.
- Forward compaction may publish complete contiguous batches. Repair of an already published dirty
  range publishes only after the full affected range through the current anchor is rebuilt.
- Worker state is restart-safe. Incomplete or generation-mismatched staging data is discarded or
  resumed without changing the published generation.
- Only one compactor loop runs per store. It wakes for each new grid boundary and reads durable
  source/dirty metadata on every cycle, so invalidation needs no second in-memory signaling path;
  retries are serialized through the same loop.

### In-band charts

- One button cycles `Hidden -> 12h -> 24h -> 72h -> Hidden`; the state is client-only and resets on
  reload.
- History and drill-down are mutually exclusive. Opening either closes the other.
- The client fetches immediately when a chart opens and refreshes at most every 30 seconds while
  visible.
- When switching windows, the installed chart remains mounted until the matching response succeeds.
  A stale or failed response cannot replace the selected chart.
- Agents and Tasks render stepped stacked-area charts below their live sections. A chart is omitted
  when that live section is empty.
- Hover shows a snapped crosshair, non-empty series, total, and absolute local time. Static geometry
  is reused until the window, anchor, or snapshots change; hover work is frame-coalesced.

### Acceptance bounds

- Release-build uncached SQLite + API + serialization for a 72-hour database with at least 40,000
  status events across at least 130 sessions, plus liveness and task changes, completes below
  1,000 ms, returns at most 289 ordered snapshots, and serializes below 250 KB.
- Repeating the same visible history with 400,000 raw events keeps request time and allocations
  effectively flat and performs zero normal-path raw history reads.
- Rollups match the pure sampler across exact boundaries, duplicates, liveness gaps, status and skill
  transitions, multiple sessions per worktree, task baselines and changes, late writes, and restarts.
- Dirty-boundary tests prove exact-boundary inclusion, ceiling to the next boundary, and clamping of
  older late writes to the retained exposed horizon.
- After successful startup and after a healthy forward-compaction cycle, the published anchor is no
  more than one 30-second interval behind wall clock. Startup never serves an empty or partial real
  history while initial backfill is incomplete.
- A repeated cached request completes within 100 ms, and simultaneous callers trigger one rollup
  read.
- For a loaded 24-hour chart, a 10-second idle measurement has at most 1,500 ms of main-thread task
  time, at most 1,500 ms of script time, and no task lasting 50 ms or longer.
- Thirty measured pointer moves complete within 1,000 ms total with no task lasting 50 ms or longer,
  while tooltip and crosshair values remain correct.

## Technical Approach

### Shared count model

`OverviewData` owns `TaskCount`, `AgentCount`, `OverviewSnapshot`, requested windows, and the anchored
history response. History omits members and scale. Client-only selection, labels, and CSS mappings
remain in `OverviewPresentation`.

### Derived schema

`SessionActivityStore` owns idempotent creation and migration of:

- published count-only rollup rows keyed by canonical bucket;
- singleton publication state containing schema version, resolution, source generation, published
  generation, complete-through bucket, and earliest dirty bucket;
- durable staging rows tagged with their candidate generation;
- session observation bounds used by indexed background reconstruction.

Every source mutation uses the same transaction as generation and dirty-range maintenance.
`AppendTaskSnapshotIfChanged` performs comparison and insertion on one connection and transaction.
Dirty boundaries use a shared ceiling-to-grid function and clamp to the retained exposed baseline.
Event and liveness mutations maintain session observation bounds; task mutations do not.

### Reconstruction and compaction

`OverviewHistory` remains the pure correctness oracle. `OverviewHistoryRollup` owns grid arithmetic,
bounded backfill/repair, staging, publication, retention, and recovery.
`OverviewHistoryReconstruction` owns the background-only indexed source reads and dense boundary
projection, keeping raw reconstruction separate from both request handling and store persistence.

The background reconstruction query seeks task, status, and latest-observation predecessors at each
boundary. It may read raw history because it is outside the request path. It maps reconstructed open
sessions through the same shared agent classifier as live Overview.

Initial startup rebuilds the full exposed horizon before API availability. Later work incrementally
fills new boundaries and repairs dirty history in batches of at most 512 boundaries. Each batch gets
its own stable read snapshot; generation tagging plus the final transactional check makes the whole
candidate range coherent. Durable staging holds one generation and one dense range; later batches
must append contiguously, and stale staging is explicitly discarded before restaging. The worker
retries generation conflicts and keeps the last published generation readable on runtime failure.
Missing or out-of-retention publication state rebuilds observation bounds, stages only the current
retained horizon, and atomically replaces all published rows instead of filling an unbounded gap.
The store serializes raw-source retention with active multi-batch reconstruction.

### Lifecycle, API, and cache

`Program` owns one shared `SessionActivityStore` and disposes it only after ingestion, compaction, and
API users stop. `SessionActivityService` receives the shared store instead of owning its lifetime.

`WorktreeApi.getOverviewHistory` reads publication state and the selected published rows only.
`OverviewHistoryCache` identifies entries by window plus publication identity rather than wall-clock
anchor age.

## Decisions

| Decision | Choice |
|---|---|
| Authoritative data | Raw events, liveness, and task snapshots remain repair sources; rollups are rebuildable derived state. |
| Canonical resolution | 30 seconds, shared by all windows through strides of 5, 10, and 30. |
| Wall-clock boundary | The latest complete boundary is the greatest UTC grid point at or before the clock instant; an exact boundary is included, and a later-arriving same-timestamp source write follows the normal dirty-repair path. |
| Bucket encoding | Published and staging buckets use canonical UTC Unix seconds; their payloads are typed `TaskCount` and `AgentCount` lists only. Session observation bounds remain separate reconstruction metadata. |
| Response anchor | Latest completely published grid boundary. |
| Request path | Published rollups only; no raw fallback, raw-row cap, or boundary-indexed raw query. |
| Stored shape | Count-only task and agent values. |
| Late event semantics | Preserve current stored post-event behavior in this performance change. |
| Liveness no-op | Equal or stale observations do not append liveness, advance generation, or change observation bounds. |
| Dirty boundary | Ceiling source timestamps to the 30-second grid and clamp to the oldest exposed baseline, bounding repair regardless of source age. |
| Initial availability | Complete the 72-hour backfill before serving history; keep the existing wire type. |
| Initial failure | Fail real-mode startup before binding rather than serve empty, partial, or raw-reconstructed history. |
| Publication | Generation check plus transactional candidate publication and dirty-marker update. |
| Repair consistency | Keep the prior coherent generation visible until the complete affected range is ready. |
| Stale publication | Atomically replace it with a current retained-horizon rebuild rather than reconstruct an unbounded downtime gap. |
| Reconstruction batch | At most 512 canonical boundaries per stable read snapshot. |
| Retention | Exposed 72-hour rollup horizon plus predecessor; raw retention remains authoritative. |
| Cache | Key by window, published generation, and complete-through bucket. |
| Rendering | Hand-written stepped SVG with memoized geometry and frame-coalesced hover. |

## Key Files

| File | Role |
|---|---|
| `src/Shared/OverviewData.fs` | Count-only history types and shared agent grouping. |
| `src/Shared/WorktreeApi.fs` | `getOverviewHistory` API contract. |
| `src/Server/OverviewHistory.fs` | Pure sampler oracle and response collapsing. |
| `src/Server/OverviewHistoryRollup.fs` | Grid arithmetic, reconstruction orchestration, staging, publication, repair, and retention. |
| `src/Server/OverviewHistoryReconstruction.fs` | Stable-snapshot indexed source reads and dense count reconstruction. |
| `src/Server/OverviewHistoryRollupWorker.fs` | Serialized startup backfill, boundary wake loop, conflict recovery, and retention coordination. |
| `src/Server/OverviewHistoryCache.fs` | Publication-keyed cache and in-flight request deduplication. |
| `src/Server/SessionActivityStore.fs` | Raw persistence, derived schema, transactional invalidation, and rollup reads/writes. |
| `src/Server/SessionActivityService.fs` | Ingestion through the shared store. |
| `src/Server/Program.fs` | Shared store and compactor lifecycle plus startup backfill. |
| `src/Server/WorktreeApi.fs` | Published-rollup history query. |
| `src/Client/App.fs` | Window state, fetching, and refresh throttle. |
| `src/Client/OverviewChart.fs` | Stepped chart geometry, SVG, and tooltip. |
| `src/Client/OverviewBand.fs` | In-band chart placement and controls. |

## Related Specs

- `docs/spec/session-status-push.md` - authoritative session events, liveness, and storage.
- `docs/spec/beads-overview-band.md` - live Overview aggregation and presentation.
- `docs/spec/overview-drilldown.md` - the mutually exclusive detail view.
