# Worktree Monitor Dashboard

## Goals

- At-a-glance dashboard showing status of all git worktrees under a root path (CLI argument)
- Per-worktree: last commit, beads task summary, Claude Code activity, PR status with build badges
- Dark-themed responsive card grid, lightweight polling, no interference with active work

## Expected Behavior

### Dashboard layout
- Dark theme (Catppuccin Mocha-inspired palette), responsive 1–4 column card grid
- Title shows "Worktree Monitor: {FolderName}" with accent-colored folder name
- Cards sorted alphabetically by branch (default), toggleable to sort by last activity
- Compact mode toggle for higher density view
- Stale detection: worktrees with no recent activity get dimmed styling

### Per-worktree card
- **Branch name** as card header
- **Claude Code status** — colored border indicator (Active / Recent / Idle / Unknown)
- **Last commit** — message + relative time, branch-local only (excludes merge commits from origin/main)
- **Origin/main freshness** — "N behind main" indicator, warning styling when significantly behind
- **Beads counts** — colored numbers (open / in-progress / done) with proportional progress bar segments
- **PR status** — active or merged badge linking to PR page, thread resolution progress (e.g. "3/10 threads")
- **Build badges** — one badge per pipeline run with abbreviated name, status color, and clickable link to build results

### Resilience
- On API poll failure: show last successful data
- On AzDo CLI failure: show "unknown" for PR status, don't block other data
- Per-worktree assembly errors return degraded data (defaults for failed parts) rather than crashing the response

## Technical Approach

- **Stack**: F# server (Saturn/Giraffe) + Fable/Elmish client, shared types via Fable.Remoting
- **Data sources**: git CLI (worktrees, commits), `bd` CLI (beads counts), Claude session file mtimes, `az` CLI (PRs, threads, builds)
- **Caching**: server-side per-source TTLs — worktree list 60s, git/beads/Claude 15s, PRs 120s
- **API**: `IWorktreeApi.getWorktrees` returns `WorktreeResponse = { RootFolderName; Worktrees }`, polled every 15s
- **Fable.Remoting route format**: `/{TypeName}/{MethodName}` (e.g. `/IWorktreeApi/getWorktrees`); Vite proxy matches this prefix
- **CC activity detection**: session file mtime < 2min = Active, < 30min = Recent, else Idle; path encoding replaces `:`, `\`, `/` with `-`
- **Branch-local commits**: `git log --first-parent --no-merges -1` to exclude merge commits
- **Main freshness**: `git rev-list --count HEAD..origin/main` for behind count
- **PR detection**: parse org/project/repo from git remote URL, match upstream branch to PR `sourceRefName`
- **PR threads**: `az devops invoke --area git --resource pullRequestThreads` to count unresolved (active/pending) vs total threads; excludes deleted and system threads
- **Merged PRs**: `--status all` query, completed PRs show "Merged" badge, skip thread/build queries
- **Build badges**: `az pipelines runs list --top 10`, parse all runs, deduplicate by pipeline definition (latest wins), construct URL from build `id`
- **Pipeline name abbreviation**: strip repo name prefix and trailing " - PR" suffix
- **Stale detection**: all of — last commit > 24h, CC Idle/Unknown, no beads in_progress, no active PR
- **Server logging**: diagnostic log to `logs/server.log`, truncated on startup; logs process invocations, exit codes, parse failures with context labels (`[PR]`, `[Git]`, `[Beads]`, `[Claude]`, `[API]`)

## Key Files

| File | Purpose |
|------|---------|
| `src/Shared/Types.fs` | Domain types shared between client and server |
| `src/Server/Log.fs` | Diagnostic logging module |
| `src/Server/GitWorktree.fs` | Worktree enumeration, commit data, main-behind count |
| `src/Server/BeadsStatus.fs` | Beads task counts via `bd` CLI |
| `src/Server/ClaudeStatus.fs` | Claude Code activity from session file mtimes |
| `src/Server/PrStatus.fs` | PR, thread, and build status via `az` CLI |
| `src/Server/WorktreeApi.fs` | API implementation, caching, parallel data assembly |
| `src/Server/Program.fs` | Saturn app entry point, CLI arg parsing |
| `src/Client/App.fs` | Elmish app (Model, Msg, update, view) |
| `src/Client/index.html` | Entry point with dark theme CSS |
| `src/Tests/ServerFixture.fs` | Test fixture: starts server + Vite |
| `src/Tests/DashboardTests.fs` | Playwright E2E tests |

## Decisions

- Web app over TUI: richer layout, easy to keep open in a browser tab
- F# + Fable/Elmish: single language both sides, functional-first, shared types
- Polling over WebSocket: simpler, sufficient for 15s refresh, no connection management
- Target net9.0 (not net10.0): Fable 4.28.0 FCS hangs with .NET 10 preview SDK
- `bd` CLI for beads: `bd count --by-status --json --db <path>` avoids custom JSONL parsing
- Pipeline name abbreviation uses `RootFolderName` as repo name proxy for prefix stripping
- No external logging framework — just `System.IO.File` operations
- E2E tests via Playwright + NUnit against live data; test fixture auto-starts server + Vite
