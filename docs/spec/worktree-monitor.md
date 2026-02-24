# Worktree Monitor Dashboard

## Goals

- At-a-glance visibility into all active worktrees across multiple repositories
- Surface activity signals from multiple sources (git, beads, coding AI tools, Azure DevOps, GitHub) so stalled branches are obvious
- Lightweight polling — no hooks or agents inside worktrees
- Zero configuration — point at root directories, provider detection is automatic from git remotes

## Expected Behavior

### Dashboard Layout

- Dark theme, responsive 1-4 column card grid
- Collapsible repo sections — header with folder name, collapse toggle, coding tool status dots per worktree when collapsed
- Cards sorted by last activity (default), toggleable to alphabetical; compact mode toggle
- Merged PRs get dimmed cards with delete button
- Scheduler footer: one row per refresh category, persistent status (never reverts to "pending")
- Loading skeleton on cold start until first worktree list completes

### Multi-Repo

- Server accepts multiple root paths as positional CLI args
- Each root is an independent section — cards never mix across repos
- Scheduler picks most-overdue task globally across all repos
- Branch events scoped by `{repoId}/{branch}` to prevent cross-repo collisions

### Per-Worktree Card

- Branch name header with work metrics (commit grid + diff stats)
- Coding tool status dot (Working / WaitingForUser / Done / Idle) with tooltip showing provider name
- Last commit message + relative time (branch-local, excludes merges from origin/main)
- "N behind main" with sync button; dirty indicator
- Beads counts (open / in-progress / done) with progress bar
- PR badge linking to PR page; AzDo: thread resolution ("3/10 threads"), GitHub: comment count
- Build badges per pipeline/workflow run; failed builds show step name (AzDo also shows log tooltip)
- Event log (last 3 events), sync/cancel/terminal/delete actions

### Branch Sync

- Available when `MainBehindCount > 0` and worktree is clean
- Pipeline: CheckClean -> Pull -> Merge -> ResolveConflicts -> Test
- Conflict resolution uses the detected/configured coding tool CLI (Claude or Copilot)
- Cancellable mid-pipeline; progress shown in card event log

### Coding Tool Detection

- Supports multiple providers: Claude Code, Copilot CLI. Adding a new provider = one detector module + registration in orchestrator.
- Every 15s refresh cycle checks all registered providers for each worktree
- Each provider reads its own session files and returns `CodingToolStatus` (Working/WaitingForUser/Done/Idle)
- Orchestrator picks the most recently active non-Idle provider (by session file mtime)
- `.treemon.json` optional `"codingTool": "claude"|"copilot"` overrides auto-detect
- Detectors return `Idle` gracefully when session directories don't exist or files are corrupt
- Claude: reads `~/.claude/projects/{encoded-path}/*.jsonl` — path encoding replaces `:`, `\`, `/` with `-`
- Copilot: reads `~/.copilot/session-state/{uuid}/workspace.yaml` to match `cwd` to worktree, then `events.jsonl` for status

### GitHub PRs

- Auto-detected from git remote URL alongside AzDo
- Fetched via `gh api`: open + recent closed PRs, comment counts from PR fields (`CommentSummary.CountOnly`)
- GitHub Actions workflow runs mapped to `BuildInfo` / `BuildStatus`; failed runs fetch job details for step name

### Resilience

- Poll failure: show last successful data
- CLI failure: degrade gracefully, don't block other data sources
- Per-worktree assembly errors return defaults for failed parts
- Hung processes time out after 60s

## Technical Approach

### Architecture

- `MailboxProcessor` state agent with `Map<string, PerRepoState>` — each repo has its own data partitions
- Tail-recursive async loop picks most-overdue task, executes it, posts result to mailbox
- API responses are instant reads from in-memory state
- Client polls every 1s; 2s fast poll during active sync

### Refresh Intervals

| Category | Scope | Interval |
|----------|-------|----------|
| WorktreeList | per-repo | 60s |
| Git, Beads, CodingTool | per-worktree | 15s |
| PR, Fetch | per-repo | 120s |

### PR Provider Routing

- `RemoteInfo` DU: `AzureDevOps of AzDoRemote | GitHub of GithubRemote`
- `detectProvider` inspects `git remote get-url origin`, routes to appropriate fetcher
- Unknown remotes produce empty PR data — other sources unaffected

### CommentSummary

- `WithResolution of unresolved * total` — AzDo thread status tracking
- `CountOnly of total` — GitHub comment count (no native resolution tracking)
- Client renders differently per case; dimmed when all resolved / no comments

### Startup Burst

On startup, a one-time parallel burst populates the dashboard in ~5-10 seconds instead of 30-60:

1. **Phase 1** — `RefreshWorktreeList` for all repos in parallel
2. **Phase 2** — `RefreshGit`, `RefreshBeads`, `RefreshClaude`, `RefreshFetch` for all repos/worktrees in parallel
3. **Phase 3** — `RefreshPr` for all repos in parallel (needs branch names from Phase 2)

After the burst, `lastRuns` is pre-populated and the normal sequential loop takes over unchanged.

## Key Files

| File | Purpose |
|------|---------|
| `src/Shared/Types.fs` | `DashboardResponse`, `RepoWorktrees`, `CommentSummary`, `CodingToolStatus`, shared domain types |
| `src/Server/RefreshScheduler.fs` | MailboxProcessor state agent, repo-keyed task scheduling |
| `src/Server/ClaudeDetector.fs` | Claude Code session file scanning |
| `src/Server/CopilotDetector.fs` | Copilot CLI session scanning, workspace index |
| `src/Server/CodingToolStatus.fs` | Coding tool orchestrator: config override, provider dispatch, winner selection |
| `src/Server/PrStatus.fs` | Provider routing, AzDo PR/thread/build fetching |
| `src/Server/GithubPrStatus.fs` | GitHub PR/Actions fetching via `gh` CLI |
| `src/Server/GitWorktree.fs` | Worktree enumeration, commit data, dirty detection, work metrics |
| `src/Server/WorktreeApi.fs` | API implementation, `DashboardResponse` assembly |
| `src/Server/SyncEngine.fs` | Branch sync pipeline, provider-aware conflict resolution |
| `src/Client/App.fs` | Elmish MVU app, repo sections, card rendering |
| `src/Tests/fixtures/` | Captured AzDo, GitHub, and Copilot data for offline parsing tests |

## Decisions

- Web app over TUI: richer layout, easy to keep open in a browser tab
- F# + Fable/Elmish: single language both sides, shared types
- MailboxProcessor over TTL cache: caps concurrent processes, instant API reads
- Polling over WebSocket: simpler, sufficient at 1s
- Most-overdue task selection: no cursor state, naturally prevents starvation
- `gh`/`az` CLI over raw REST: handles auth, consistent pattern
- Single API call returns all repos: client doesn't need to know repo count
- Repo ID = folder name: simple, human-readable, no config needed
- `CommentSummary` DU over nullable fields: cleanly models provider capability differences
- Pluggable coding tool detection over hardcoded Claude: same interface pattern as PR providers, auto-detect with config override
- Repo-scoped branch events: prevents name collisions across repos
- net9.0 (not net10.0): Fable 4.28.0 FCS hangs with .NET 10 preview SDK

## Related Specs

- `docs/spec/future/llm-comment-resolution.md` — infer GitHub comment resolution via LLM, upgrade `CountOnly` to `WithResolution`
- `docs/spec/future/strong-typed-paths.md` — `AbsolutePath` wrapper type for compile-time path safety (deferred: entry-point normalization sufficient for now)
