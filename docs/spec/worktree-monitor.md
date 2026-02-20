# Worktree Monitor Dashboard

## Goals

- At-a-glance visibility into all active worktrees — see what's happening across branches without switching context
- Surface activity signals from multiple sources (git, beads, Claude Code, Azure DevOps) in one place so stalled or forgotten branches are obvious
- Lightweight and non-intrusive — background refresh loop, no hooks or agents that interfere with active work
- Zero configuration per worktree — point at a root directory and it discovers everything

## Expected Behavior

### Dashboard Layout

- Dark theme (Catppuccin Mocha-inspired palette), responsive 1–4 column card grid
- Title shows "Treemon: {FolderName}" with accent-colored folder name
- Cards sorted by last activity (default), toggleable to alphabetical
- Compact mode toggle for higher density view
- Merged PRs get dimmed cards with accented delete button
- Scheduler footer shows per-category refresh status (one row per source)
- Cold start shows loading skeleton until first worktree list refresh completes

### Per-Worktree Card

- **Branch name** as card header with inline work metrics (commit grid + diff stats)
- **Claude Code status** — colored border indicator (Working / WaitingForUser / Done / Idle)
- **Last commit** — message + relative time, branch-local only (excludes merge commits from origin/main)
- **Origin/main freshness** — "N behind main" indicator with sync button; "uncommitted changes" warning when dirty
- **Beads counts** — colored numbers (open / in-progress / done) with proportional progress bar segments
- **PR status** — active or merged badge linking to PR page, thread resolution progress (e.g. "3/10 threads")
- **Build badges** — one badge per pipeline run with abbreviated name, status color, clickable link; failed builds show step name + log tooltip
- **Event log** — last 3 events per card (sync steps, Claude messages), newest first; only on full cards
- **Actions** — sync button, cancel sync, open terminal, delete worktree

### Branch Sync

- Sync button appears when `MainBehindCount > 0` and worktree is clean
- Multi-step pipeline: CheckClean → Pull → Merge → ResolveConflicts (via Claude) → Test
- Cancellation supported mid-pipeline
- Per-card event log shows step progress in real-time
- `.treemon.json` in worktree root optionally configures test solution path

### Resilience

- API poll failure: show last successful data
- AzDo CLI failure: show "unknown" for PR status, don't block other data
- Per-worktree assembly errors return degraded data (defaults for failed parts)
- Hung processes time out after 30s — scheduler loop continues

## Technical Approach

### Architecture

- **Background refresh scheduler** via `MailboxProcessor` — replaces demand-driven TTL cache
- Single `DashboardState` record with immutable maps for git, beads, PR, Claude data
- Tail-recursive async loop picks the most-overdue task, executes it, posts result to mailbox
- API responses are instant — pure reads from in-memory state
- Client polls every 1s; data updates as each worktree completes (~500ms apart)

### Refresh Intervals

| Category | Target | Interval |
|----------|--------|----------|
| WorktreeList | global | 60s |
| GitRefresh | per-worktree | 15s |
| BeadsRefresh | per-worktree | 15s |
| ClaudeRefresh | per-worktree | 15s |
| PrFetch | global | 120s |
| GitFetch | global | 120s |

Intra-worktree parallel (6 git calls), inter-worktree sequential. AzDo calls sequential (at most 1 `az` CLI process).

### Data Sources

- **git CLI** — worktree enumeration, commits (`git log --first-parent --no-merges -1`), behind count (`git rev-list --count HEAD..origin/main`), dirty detection (`git status --porcelain -uno`), work metrics (`git rev-list --count --no-merges origin/main..HEAD` + `git diff --shortstat origin/main...HEAD`), periodic fetch (`git fetch origin main`)
- **bd CLI** — `bd count --by-status --json --db <path>/.beads/beads.db`
- **az CLI** — `az repos pr list` (PR detection by branch), `az devops invoke` (PR threads, build timeline/logs), `az pipelines runs list` (build badges)
- **Claude session files** — reads `~/.claude/projects/<encoded-path>/*.jsonl` for status (mtime-based) and last assistant message; path encoding replaces `:`, `\`, `/` with `-`

### Client

- Single-file Elmish MVU app (`App.fs`) with React 18 via Feliz
- 1s poll cycle for main data; 2s fast poll for sync status during active sync operations
- Scheduler footer with persistent status overview (one row per category, never reverts to "pending" after first run via server-side `LatestByCategory` map)
- Pinned errors in footer until source+worktree combination succeeds

### Build Failure Extraction

- Server fetches build timeline for failed builds, extracts first failed task's log (last 50 lines)
- `BuildFailure = { StepName; Log }` populated on `BuildInfo`
- Client shows step name on badge, log as hover tooltip

### Fable.Remoting

- Routes use `/{TypeName}/{MethodName}` format (e.g. `/IWorktreeApi/getWorktrees`)
- Vite proxy matches `/IWorktreeApi` prefix

### Dev/Prod Separation

- Production: port 5000, serves pre-built `wwwroot/` + API
- Development: server port 5001, Vite port 5174 (proxies API to 5001)
- `treemon.ps1` manages lifecycle: start/stop/restart/status/log/dev/deploy
- Deploy: `npm run build` → copy `dist/` to `wwwroot/` → restart production
- PID tracked via `.treemon.pid` for reliable process management
- `AppVersion` in API response enables client-side reload detection after deploy

## Key Files

| File | Purpose |
|------|---------|
| `src/Shared/Types.fs` | Domain types shared between client and server |
| `src/Shared/EventUtils.fs` | Event processing: branch extraction, pinning, deduplication |
| `src/Server/Log.fs` | Diagnostic logging to `logs/server.log` |
| `src/Server/RefreshScheduler.fs` | Background refresh loop, MailboxProcessor state agent |
| `src/Server/GitWorktree.fs` | Worktree enumeration, commit data, dirty detection, work metrics |
| `src/Server/BeadsStatus.fs` | Beads task counts via `bd` CLI |
| `src/Server/ClaudeStatus.fs` | Claude Code activity + last assistant message parsing |
| `src/Server/PrStatus.fs` | PR, thread, build status, failure extraction via `az` CLI |
| `src/Server/SyncEngine.fs` | Branch sync pipeline orchestration |
| `src/Server/WorktreeApi.fs` | API implementation, state reads, event merging |
| `src/Server/Program.fs` | Saturn app entry point, scheduler wiring |
| `src/Client/App.fs` | Elmish app (Model, Msg, update, view) |
| `src/Client/index.html` | Entry point with dark theme CSS |
| `src/Tests/ServerFixture.fs` | Test fixture: starts server + Vite |
| `src/Tests/DashboardTests.fs` | Playwright E2E tests |
| `src/Tests/EventProcessingTests.fs` | Unit tests for event processing |
| `src/Tests/GitParsingTests.fs` | Unit tests for git output parsing |
| `src/Tests/SchedulerTests.fs` | Unit tests for scheduler logic |
| `src/Tests/SmokeTests.fs` | Smoke tests against live data |
| `treemon.ps1` | Production lifecycle, dev mode, deploy |
| `vite.config.js` | Vite config with API proxy |

## Decisions

- Web app over TUI: richer layout, easy to keep open in a browser tab
- F# + Fable/Elmish: single language both sides, functional-first, shared types
- MailboxProcessor over TTL cache: caps concurrent processes at ~6, instant API responses, eliminates process overload
- Polling over WebSocket: simpler, sufficient for 1s refresh, no connection management
- Most-overdue task selection over round-robin: simpler, no cursor state, naturally prevents starvation
- Target net9.0 (not net10.0): Fable 4.28.0 FCS hangs with .NET 10 preview SDK
- `bd` CLI for beads: avoids custom JSONL parsing
- Server-side `LatestByCategory` map: persistent per-category state that survives event list trimming
- `CardEvent` reused for both sync steps and scheduler events: one type, `Duration` field distinguishes
- Merged-based dimming instead of stale-based: actionable signal (delete the worktree) vs ambiguous staleness
- Three-dot diff (`origin/main...HEAD`) for work metrics: unaffected by being behind main
