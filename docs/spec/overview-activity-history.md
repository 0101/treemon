# Overview Activity History

Persist every change to the dashboard **Overview** roll-up (cross-worktree agent stats + task stats)
as timestamped records, and surface a past-**24h/72h** timeseries chart **inside** the Overview band.

Investigation: `.agents/overview-activity-history-investigation.md`.
Prototype: `.agents/canvas/overview-chart-prototype.html`.
Related: `docs/spec/beads-overview-band.md` (the live band this extends).

## Goals

- Give the Overview band a **history**: record how agent activity and task buckets evolve over time,
  so trends (overnight execution, backlog draining, planning cadence) are visible — not just the
  current snapshot.
- Capture that history **24/7**, independent of whether any browser is open, from the existing
  server-side scan — one aggregation, no second collection path.
- Let the user **see** the history in place: a togglable stacked-area chart under each Overview
  section, scoped to the last 24h or 72h.
- Add **no runtime dependency** and keep a single source of truth for the aggregation shared by the
  live band, the logfile, and the chart.

## Decisions

These were settled with the user during investigation and prototyping:

1. **Trigger:** log continuously from the `RefreshScheduler` loop (24/7), not the client-poll path.
2. **Format:** JSON Lines (`logs/overview-history.jsonl`), append-only, one snapshot per line.
3. **Dedupe:** log **only on change** — append a record only when the `{Tasks; Agents}` aggregate
   differs from the last logged one (drop the timestamp from the comparison). No fixed-interval
   sampling.
4. **Chart location:** in-band, directly under each live section — order is **agents live → agents
   history → tasks live → tasks history**.
5. **Window control:** a single **cycle** button in the band toggles **hidden → 24h → 72h → hidden**.
6. **Toggle state is ephemeral** — client-only model state, resets on reload. No config, no
   persistence API.
7. **Chart type:** hand-rolled inline **SVG stacked-area, stepped** (value holds until the next
   logged change), reusing the band's existing accent CSS classes for colour. No charting library.
8. **Tooltips:** crosshair tooltip on hover, snapped to the active stepped snapshot, listing each
   non-empty series as `label: count` plus a total and a relative timestamp.
9. **Mutually exclusive with the drill-down panel** (`docs/spec/overview-drilldown.md`): the band's
   two ephemeral detail views never coexist. Opening the history chart clears any drill-down
   selection; selecting a drill-down group hides the history chart. One detail view at a time.
10. **Snapshot stores counts only.** The persisted record carries just `{Kind; Count}` per bucket/
    group — never the drill-down `Members` — so the JSONL stays lean and change-detection tracks
    count transitions, not per-worktree membership churn.
11. **Loop assembly is injected, not called directly.** `RefreshScheduler.fs` is compiled *before*
    `WorktreeApi.fs`/`SyncEngine.fs`, so `RefreshScheduler.loop` cannot name `WorktreeApi.assembleRepos`
    (which depends on `SyncEngine`). `RefreshScheduler.start` therefore takes an injected
    `assembleOverview : DashboardState -> Async<Overview>`; `Program.fs` supplies the closure
    (`getActiveSessions` → `assembleRepos rootPaths activeSessionPaths state` → `OverviewData.aggregate`).
    Active sessions are included so the logged roll-up matches the client band exactly. The
    change-detection + append + immutably-threaded `lastLoggedSnapshot` accumulator all stay inside
    `loop` as intended.

## Expected Behavior

### Logging (server, 24/7)

- On each scheduler iteration the server computes the same `Overview` aggregate the client band
  shows (via the relocated shared `OverviewData.aggregate`).
- If the freshly computed `{Tasks; Agents}` differs from the last appended record, a new line is
  appended to `logs/overview-history.jsonl`: `{ "ts": <ISO-8601>, "tasks": [...], "agents": [...] }`.
  Identical consecutive aggregates append nothing.
- The file is append-only and tolerant of a partial trailing line on read (a crash mid-write must
  not break parsing of prior records). `logs/` is gitignored.

### History retrieval

- `IWorktreeApi.getOverviewHistory : unit -> Async<OverviewSnapshot list>` returns the records within
  the last **72h** (the widest window the chart offers), newest data included, parsed from the JSONL.
- Records older than 72h are ignored on read (not required to be pruned from disk in v1).

### In-band chart

- The band gains a single cycle button (styled like the band's controls). Clicking cycles
  hidden → 24h → 72h → hidden; the label reflects the state (e.g. `◷ History` / `◷ 24h` / `◷ 72h`).
- **Opening the chart** (entering any non-hidden window) **clears the drill-down selection**
  (`SelectedOverviewGroup = None`); conversely **selecting a drill-down group hides the chart**
  (window → hidden). The two band detail views are mutually exclusive.
- When a window is active, two stacked-area charts render — one under the Active-agents section, one
  under the Tasks section — each scoped to the selected window ending at "now".
- Series use the **same palette** as the live band (`task-*`, `activity-*`, waiting) so a bucket's
  bar/circle and its area share one colour. Empty series are omitted from chart + legend.
- Rendering is **stepped**: each value holds until the next logged change; irregular gaps produce
  uneven step widths (correct — a wide flat band is a quiet stretch). The window may open mid-gap, so
  the left edge carries the snapshot active at the window start; the right edge holds flat to now.
- Hovering shows a dashed vertical crosshair and a tooltip snapped to the active snapshot: relative
  time header, one `label: count` row per non-empty series with a colour swatch, and a total.
- When the selected window has no history, the chart degrades gracefully (renders nothing or a flat
  baseline) — never an error.

## Technical Approach

### 1. Relocate the aggregation to `Shared`
Move `src/Client/OverviewData.fs` → `src/Shared/OverviewData.fs`; add it to `Shared.fsproj` after
`Types.fs`; remove it from `Client.fsproj`. The module already depends only on `Shared` and is
Fable-safe (confirmed post-merge — the drill-down additions `GroupMember`, `OverviewSelection`, and
the per-bucket/group `Members` lists all use `Shared` types only), so no code change is needed. Both
`OverviewBand.view` (client) and the server logger then call the same `aggregate`. The server ignores
the `Members` lists when logging (it projects to counts — see §2).

### 2. Snapshot type + history module
- Add a **count-only** snapshot shape in `Shared` (deliberately NOT reusing `TaskBucket`/`AgentGroup`,
  which now carry drill-down `Members`):
  ```fsharp
  type TaskCount  = { Kind: TaskBucketKind; Count: int }
  type AgentCount = { Kind: AgentGroupKind;  Count: int }
  type OverviewSnapshot = { Timestamp: DateTimeOffset; Tasks: TaskCount list; Agents: AgentCount list }
  ```
  (Scale is derived by the view; `Members` are omitted so the log stays lean.)
- A projection `Overview -> {Tasks: TaskCount list; Agents: AgentCount list}` drops `Members`; both
  logging and change-detection operate on this projection.
- New `src/Server/OverviewHistory.fs`:
  - `serialize`/`append` one JSONL line via the existing `Log.fs` `FileStream`+lock pattern and
    `JsonHelpers`/Newtonsoft.
  - `changed : OverviewSnapshot option -> Overview -> bool` — compares the count-only projection of
    the new `Overview` against the last snapshot's `{Tasks; Agents}`, ignoring the timestamp (and,
    by construction, membership). Identical counts → no append.
  - `readWindow : TimeSpan -> OverviewSnapshot list` — parse the JSONL, drop records older than the
    window, tolerate a partial trailing line.

### 3. Factor out server-side repo assembly + wire the loop
- Extract the `DashboardState -> RepoWorktrees list` assembly currently inline in
  `WorktreeApi.getWorktrees` (needs active sessions, archived branch sets, ignore predicate) into a
  reusable function so both the poll path and the scheduler reuse it.
- In `RefreshScheduler.loop`, after `GetState`, assemble repos, `OverviewData.aggregate`, and if
  `changed` against a threaded `lastLoggedSnapshot` accumulator, append and recurse with the updated
  accumulator (immutable threading, matching existing `lastRuns`/`watchers`; no `let mutable`).

### 4. Expose history over the API
Add `getOverviewHistory` to `IWorktreeApi` (Shared) and implement it in the server
(`OverviewHistory.readWindow (TimeSpan.FromHours 72)`). Update every `IWorktreeApi` implementer/stub
(demo fixture, test doubles, CLI proxy usage) — no compat shim.

### 5. Client: ephemeral window state + charts
- `AppTypes.fs`: add ephemeral `OverviewChartWindow` (e.g. `None | Hours24 | Hours72`) to the model
  and a `CycleOverviewChart` message; hold fetched `OverviewSnapshot list` in the model.
- `App.fs`: handle `CycleOverviewChart` (advance the state; on entering a non-hidden state,
  **clear `SelectedOverviewGroup`** and `Cmd.OfAsync` fetch `getOverviewHistory`); render the cycle
  button inside the band. In the existing `SelectOverviewGroup` handler, when a selection is set,
  **reset `OverviewChartWindow` to hidden** — enforcing mutual exclusivity (mirrors how
  `ToggleOverviewPanel` already clears the drill-down selection).
- New `src/Client/OverviewChart.fs`: pure builders that turn `OverviewSnapshot list` + a window into
  stacked stepped-area SVG (`Feliz` inline SVG), legend, and crosshair tooltip, reusing the `task-*`
  / `activity-*` accent classes. `OverviewBand.view` renders each chart directly under its section.
  Inline SVG geometry is the documented dynamic-value exception (same as the proportional bar width).
- Chart geometry/interaction mirrors `.agents/canvas/overview-chart-prototype.html` (windowing with
  left-edge carry, stepped stacked areas, snapped tooltip).

## Key Files

- `src/Shared/OverviewData.fs` (relocated), `src/Shared/Types.fs` (`OverviewSnapshot`, `IWorktreeApi`)
- `src/Server/OverviewHistory.fs` (new), `src/Server/RefreshScheduler.fs`, `src/Server/WorktreeApi.fs`
- `src/Client/OverviewChart.fs` (new), `src/Client/OverviewBand.fs`, `src/Client/App.fs`,
  `src/Client/AppTypes.fs`, `src/Client/Client.fsproj`, `src/Shared/Shared.fsproj`
