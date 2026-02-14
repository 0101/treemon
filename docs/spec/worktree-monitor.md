# Worktree Monitor Dashboard

## Goals
- Real-time dashboard showing status of all git worktrees under a configurable root path (passed as CLI argument, e.g. `mait Q:\AITestAgent`)
- Display per-worktree: last commit, beads task summary, Claude Code activity, PR status
- Responsive card grid layout that adapts to screen width
- Lightweight polling that doesn't interfere with active work

## Expected Behavior
- Server runs on localhost, serves a single-page Elmish app
- Client polls `/api/worktrees` every 15 seconds, renders card grid
- Each card shows: branch name, CC status indicator, last commit message + relative time, beads counts (open/in-progress/closed), PR status with reviewer votes and unresolved thread count, build status indicator (succeeded/failed/in-progress)
- Cards sorted alphabetically by branch name (default), toggleable to sort by last activity
- Compact mode toggle for higher density view
- Stale detection: worktrees with no activity in 24h (no commits, no CC activity, no beads changes, no PR updates) get dimmed styling
- PR cards highlight unresolved comment threads with a count badge
- Responsive: 1→4 columns depending on viewport width
- Graceful handling of missing data (no beads, no upstream, no PR, CC idle)
- On API poll failure: show last successful data, display "unknown" status for affected sources
- On AzDo CLI failure: show "unknown" for PR status (don't block other data)

## Technical Approach
- **Stack**: F# server (Saturn/Giraffe) + Fable/Elmish client, shared domain types via Fable.Remoting (automatic type-safe serialization, no manual encoders/decoders)
- **Data sources**: git CLI for worktree/commit info, `bd count --by-status --json --db <wt>/.beads/beads.db` for task counts, `~/.claude/projects/` file mtimes for CC activity, `az repos pr list` for PR status, `az pipelines runs list` for build status
- **PR remote detection**: Parse org/project/repo from git remote URL of the monitored repository (handles both `dev.azure.com` and `visualstudio.com` formats)
- **PR branch matching**: Use `git rev-parse @{u}` to resolve upstream, strip `origin/` prefix, match against PR `sourceRefName`
- **PR threads**: Use `az repos pr thread list --id <prId>` to count unresolved threads (status=active) per PR
- **Build status**: Use `az pipelines runs list --branch <sourceBranch> --reason pullRequest --top 1 --org ... --project ... -o json` to get latest build run per PR branch. Extract `status` (inProgress, completed) and `result` (succeeded, failed, partiallySucceeded, canceled). Fetched alongside PR data, shares same 120s cache TTL. Graceful fallback to Unknown if `az pipelines` fails.
- **Caching**: Server-side per-source TTLs (worktree list 60s, git/beads/CC 15s, PRs 120s)
- **CC activity detection**: Session file mtime < 2min = Active, < 30min = Recent, else Idle. Path encoding: replace `:` and `\` with `-` (e.g. `Q:\code\AIT-foo` → `Q--code-AIT-foo`). Verify encoding against actual `~/.claude/projects/` directory names at implementation time.
- **Stale detection**: A worktree is stale when ALL of: last commit > 24h ago, CC status is Idle/Unknown, no open beads in_progress, and PR has no updates in 24h

## Decisions
- Web app over TUI: richer layout, easy to keep open in a browser tab
- F# + Fable/Elmish: single language both sides, functional-first, shared types
- Card grid over table: better visual scanning, natural responsive behavior
- Alphabetical default sort: predictable, easy to find specific branch
- Single `az repos pr list` call per poll: scoped to AITestAgent repo, matched in-memory
- `bd` CLI for beads counts: `bd count --by-status --json --db <path>` avoids custom JSONL parsing, works remotely via `--db` flag
- Polling over WebSocket: simpler, sufficient for 15s refresh, no connection management
- Target net9.0 (not net10.0): Fable 4.28.0 FCS hangs with .NET 10 preview SDK; global.json pins to 9.0.x
- Fable.Remoting for client-server communication: type-safe RPC, automatic serialization of shared F# types, no manual JSON encoding/decoding. Shared API contract as `type IWorktreeApi = { getWorktrees : unit -> Async<WorktreeStatus list> }`. Server implements it via `Fable.Remoting.Giraffe`, client calls it via `Fable.Remoting.Client` proxy. Default route format: `/{TypeName}/{MethodName}` (e.g. `/IWorktreeApi/getWorktrees`). Vite proxy must match this prefix, not `/api`.
- Solution uses .slnx format (new in .NET 9 SDK)
- E2E tests use Microsoft.Playwright.NUnit. Test fixture starts F# server + Vite dev server as processes, waits for both to be ready, runs headless Chromium against Vite URL. Tests verify: dashboard loads, cards render with branch/commit/CC/beads data, responsive breakpoints, compact mode toggle, sort toggle.
