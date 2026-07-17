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
- Fixed header bar with system metrics and deploy branch badge (see `docs/spec/fixed-header.md`)
- Keyboard navigation: arrow keys move focus spatially across cards and repo headers (see `docs/spec/keyboard-navigation.md`)
- Canvas pane: per-worktree interactive HTML documents for agent-to-user rich content (see `docs/spec/canvas-pane.md`)

### Multi-Repo

- Watched roots resolve at server startup by priority: CLI args → the global `worktreeRoots` key in `~/.treemon/config.json` → a one-time import of the legacy orphan `~/.treemon/roots.json`. With roots configured, `treemon.ps1 start`/`dev` no longer require a path; with no args the server uses the global config (an empty list is valid, as in demo mode). A *present* `worktreeRoots` key — even an explicit empty list — is authoritative and never repopulated; the server persists a resolved set only when the key is *absent* (fresh install / migration), so curating every root away stays sticky across restarts.
- Roots are managed live through the `tm` CLI — `tm add <path>...` (validates the path exists, normalizes it, no-op if already watched), `tm remove <path>...` (errors on an unknown path; removing the last root is allowed), and `tm roots` (list). All three are online-only (require the running server). The server is the single, serialized writer of `config.json`; changes persist immediately and take effect on the next server (re)start. The `treemon.ps1 add`/`remove` shims call `tm` and then restart the production server when it is running.
- Roots are a per-machine singleton: dev and prod instances on the same machine share one global list. Legacy stores migrate then delete losslessly — `treemon.ps1` migrates a legacy `.treemon.config` (PowerShell-written, plural `WorktreeRoots` or the older singular `WorktreeRoot`) and the server migrates the orphan `roots.json`, each removed only after its roots are safely persisted (a parse failure or unmigrated content is preserved with a warning, never silently dropped).
- Each root is an independent section — cards never mix across repos
- Scheduler picks most-overdue task globally across all repos
- Branch events scoped by `{repoId}/{branch}` to prevent cross-repo collisions

### Configuration Store

Machine-level state persists in `~/.treemon/config.json` (or `$TREEMON_CONFIG_DIR` when set, for tests). `src/Server/GlobalConfig.fs` is the sole owner of that file — a single JSON store fronted by typed accessors, with these invariants:

- **Single serialized writer, atomic on disk.** Every mutation funnels through one in-process lock and writes via a temp-file-then-replace; no write bypasses the lock, so concurrent updates can't interleave or leave a partially written file.
- **Never destroy data.** An unparseable `config.json` is backed up to a timestamped `*.corrupt-<ts>` sibling before a fresh object is started, and each write touches only its own named keys — every unrelated key is left intact.
- **Typed accessors over one store.** Watched roots (with the missing-vs-empty distinction the startup resolver depends on — see Multi-Repo above), canvas pane open/position, collapsed repos, last-viewed hashes, and the editor command/name reader are thin wrappers over the same locked store.

### Worktree Identification

- All `IWorktreeApi` methods use `WorktreePath` (filesystem path) as the worktree identifier — no branch name ambiguity across repos
- Server resolves repo and branch from path internally; archive persistence still stores branch names per repo in `.treemon.json`
- Client optimistic state (`DeletedPaths: Set<string>`) filters by path, affecting only the correct repo

### Per-Worktree Card

- Branch name header with work metrics (commit grid + diff stats)
- Coding tool status dot (Working / WaitingForUser / Idle / NoSession) with tooltip showing provider name
- Last commit message + relative time (branch-local, excludes merges from origin/main)
- "N behind main" with sync button; dirty indicator
- Beads counts (open / in-progress / done) with progress bar
- PR badge linking to PR page; merge conflict icon when conflicts detected; AzDo: thread resolution ("3/10 threads"), GitHub: comment count
- Build badges per pipeline/workflow run; failed builds show step name (AzDo also shows log tooltip)
- Event log (last 3 events), sync/cancel/terminal/delete actions
- Green left border on cards with active terminal sessions
- Contextual action buttons: fix PR comments, fix failed build, create PR (see `docs/spec/contextual-actions.md`)

### Branch Sync

- Available when `MainBehindCount > 0` and worktree is clean
- Pipeline: CheckClean -> Pull -> Merge -> ResolveConflicts -> Test -> Commit -> Push (if PR exists)
- Conflict resolution uses the detected/configured coding tool CLI (Claude or Copilot)
- Test step runs the shell command from `.treemon.json` `"testCommand"` (e.g. `"dotnet test src/Tests/Tests.fsproj"`, `"npm test"`, `"pytest"`)
- If `testCommand` is not configured, the sync engine skips tests and shows a "not configured" status with a clickable action to configure it
- Cancellable mid-pipeline; progress shown in card event log

### Coding Tool Detection

Coding-tool status is **pushed** by the Copilot CLI extension, not parsed from session log files —
the per-provider log-parsing detectors (`ClaudeDetector`, `CopilotDetector`, `VsCodeCopilotDetector`,
`getStatusFromFiles`) have been **removed**. The extension observes the SDK session event stream and
POSTs lifecycle events to the server, which folds them into live per-session state and collapses each
worktree's sessions in `CodingToolStatus.fs` (`fromPushSessions`). See
`docs/spec/session-status-push.md` for the full model.

- Status vocabulary is `CodingToolStatus = Working | WaitingForUser | Idle | NoSession`; the dot is a
  pure function of the collapsed status (red / yellow / blue open-idle / grey no-session).
- `.treemon.json` optional `"codingTool": "claude"|"copilot"` still selects the per-worktree
  provider for command-building (`readConfiguredProvider`); the push status source is Copilot-CLI-only today.
- The card footer shows a `▶ <skill>` label when a skill is running, else the last user message
  (never a `<skill-context>` injection) — a pure `cardUserLine` decision in `CardViews.fs`.

### Create Worktree

A "+" button on each repo header opens a modal to create new worktrees without leaving the dashboard.

- **Name input** (auto-focused) + **source branch dropdown** (sorted: main > master > develop > dev* > alphabetical from dashboard worktrees)
- Treemon creates the worktree itself: it fetches the base branch from the upstream remote, then forks via `git worktree add -b {name} {parentDir}/tm-{name} {baseRef}`. `baseRef` prefers the remote-tracking ref `{remote}/{base}` — so a new worktree forks from the upstream tip rather than a possibly-stale local branch — falling back to the local `{base}` branch when no remote-tracking ref exists. No worktree needs the base checked out; fetch/remote failures fall back to whatever ref is available.
- After creation, an optional `post-fork.ps1` (Windows) / `post-fork.sh` (Unix) in the repo root runs **inside the new worktree**, receiving `{worktreePath} {sourceRepoRoot} {baseRef} {branchName}`. It is for setup only (symlinks, dependency install). Because setup can be slow, it runs **asynchronously in a background task** *after* the create call returns — its start and outcome are surfaced on the worktree card via the sync event log (`BeginPostFork` → `CompletePostFork(status)`), not through the create response. A failure is non-fatal since the worktree already exists.
- Legacy `fork.ps1`/`fork.sh` scripts are **no longer executed** — Treemon now owns forking. If one is present, creation still succeeds but returns a warning to migrate setup steps into `post-fork.*`.
- Warnings returned by `createWorktree` (`Result<string list, string>`) now carry **only the legacy-fork-script advisory** and are surfaced in the modal (UI) or console (CLI); post-fork success/failure is reported on the card, not through this return value. Internally, `forkWorktree` performs the fork (returning a `ForkResult`) and `runPostFork` runs the hook.
- Modal shows creating animation, then auto-closes on clean success, or shows warnings / error
- Server expedites worktree list refresh for the repo so the new card appears quickly
- **Optional prompt** — a multi-line textarea below the source-branch dropdown. A non-blank value auto-launches a coding-agent session in the new worktree; **Enter inserts a newline** (it does not submit — the Create button submits, Escape closes), and a blank/whitespace prompt is a no-op (identical to the no-prompt flow). The prompt rides the create request as a `string option`.
- **Skill selection** — a radio group between the source-branch dropdown and the prompt textarea chooses which skill wraps the prompt on launch. A built-in **None** option (always present) sends the prompt **verbatim**; each configured skill wraps it. The chosen skill rides the create request as a `string option` (`None` ⇒ verbatim). The offered skills are the machine-level `worktreeSkills` list (see below); the first entry is the default selection. When no skills are configured the only option is None, and a subtle hint next to it points at `~/.treemon/config.json` (`worktreeSkills`).
- On a non-blank prompt the server, after a successful create, **fire-and-forget** spawns a tracked coding-agent window in the new worktree. When a skill was chosen it seeds a provider-aware skill invocation (`use {skill} skill with {prompt}` for Copilot, `/{skill} {prompt}` for Claude); for **None** it seeds the prompt verbatim. It reuses `SessionManager.launchAction` — the same path the contextual-action buttons use (see `docs/spec/contextual-actions.md`) — so there is no bespoke spawn logic; the modal still returns/closes on the create result and does not wait for the window. The launch runs even when create returned a post-fork warning.
- The offered skills are config-driven: the machine-level `~/.treemon/config.json` `worktreeSkills` (a string array, blank entries dropped, **empty by default**), surfaced to the client via `DashboardResponse.WorktreeSkills` (like `EditorName`).

### Native Session Management

Windows Terminal integration for spawning, tracking, and focusing terminal windows per worktree. See `docs/spec/native-session-management.md` for full details.

### GitHub PRs

- Auto-detected from git remote URL alongside AzDo
- Fetched via `gh api graphql`: open + recent closed PRs, review thread resolution counts (`CommentSummary.WithResolution`)
- Review thread resolution uses GraphQL (`PullRequest.reviewThreads.nodes.isResolved`) — REST API does not expose resolution status
- Dashboard renders `"{unresolved}/{total} threads"` badge, matching ADO format; dimmed when all resolved; action button only when unresolved threads exist
- Merged PRs return `WithResolution(0, 0)` without a network call; PRs with zero threads show no badge
- `first: 100` thread limit is acceptable — PRs rarely exceed 100 review threads
- GitHub Actions workflow runs mapped to `BuildInfo` / `BuildStatus`; failed runs fetch job details for step name
- Per open PR, an extra detail fetch (`/repos/{owner}/{repo}/pulls/{number}`) retrieves `mergeable` status; run in parallel with Actions fetch, adding no sequential latency

### Merge Conflict Detection

- `HasConflicts: bool` on `PrInfo` — `true` when the PR has merge conflicts
- AzDo: parsed from `mergeStatus` field in existing `az repos pr list` response (`"conflicts"` → true, all others → false)
- GitHub: parsed from `mergeable` field in per-PR detail response (`false` → conflicts, `true`/`null` → no conflicts)
- Merged PRs always have `HasConflicts = false`; unknown/computing states treated as no conflicts (resolves on next poll)
- Client renders an inline conflict icon (⚔) on the PR badge when `HasConflicts = true`

### Demo Mode

`treemon.ps1 demo` launches the server with `--demo` flag, cycling through pre-built `FixtureData` frames (~24s loop) that cover all dashboard features. No client changes — same poll-based rendering. See `src/Server/DemoFixture.fs`.

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
- Client polls every 1–15s depending on activity level (see `docs/spec/user-idle-detection.md`); 2s fast poll during active sync

### Refresh Intervals

Intervals adapt to user activity level (Active / Idle / Deep Idle). See `docs/spec/user-idle-detection.md` for the full interval table and activity state definitions. The Idle column matches the original fixed values shown here historically.

### PR Provider Routing

- `RemoteInfo` DU: `AzureDevOps of AzDoRemote | GitHub of GithubRemote`
- `detectProvider` inspects `git remote get-url {upstreamRemote}`, routes to appropriate fetcher
- Unknown remotes produce empty PR data — other sources unaffected

### Upstream Remote Resolution

For fork workflows (push to fork, PRs in upstream repo), treemon auto-detects and uses the correct remote:

- **Resolution order**: `.treemon.json` `"upstreamRemote"` field → auto-detect `upstream` remote → fall back to `origin`
- **Affects**: PR fetching (remote URL), base branch comparisons (`{remote}/{baseBranch}`), fetch cycle, sync merge target
- **Stored** per-repo in `PerRepoState.UpstreamRemote`, resolved during worktree list refresh
- **Config example**: `{ "upstreamRemote": "upstream" }` in `.treemon.json` at repo root

### Base Branch Resolution

Each repo can configure which branch is considered the "base" for ahead/behind counts, diff stats, fetch, fast-forward, and sync operations:

- **Resolution**: `.treemon.json` `"baseBranch"` field → default `"main"`
- **Affects**: `git rev-list` behind/commit counts, `git diff --shortstat`, `git fetch`, fast-forward, merge/rebase targets, branch sort priority
- **Stored** per-repo in `PerRepoState.BaseBranch`, resolved during worktree list refresh
- **Config example**: `{ "baseBranch": "dev" }` in `.treemon.json` at repo root

### CommentSummary

- `WithResolution of unresolved * total` — thread resolution tracking (both AzDo and GitHub)
- Client renders thread count badge; dimmed when all resolved; hidden when total = 0

### Startup Burst

On startup, a one-time parallel burst populates the dashboard in ~5-10 seconds instead of 30-60:

1. **Phase 1** — `RefreshWorktreeList` for all repos in parallel
2. **Phase 2** — `RefreshGit`, `RefreshBeads`, `RefreshFetch` for all repos/worktrees in parallel (coding-tool status is pushed, not scheduled)
3. **Phase 3** — `RefreshPr` for all repos in parallel (needs branch names from Phase 2)

After the burst, `lastRuns` is pre-populated and the normal sequential loop takes over unchanged.

## Key Files

| File | Purpose |
|------|---------|
| `src/Shared/Types.fs` | Domain types: `DashboardResponse`, `CodingToolStatus`, `CodingToolProvider`, `CommentSummary` |
| `src/Shared/EventUtils.fs` | Event processing: branch extraction, pinning, deduplication |
| `src/Server/RefreshScheduler.fs` | MailboxProcessor state agent, repo-keyed task scheduling |
| `src/Server/SessionActivity.fs` / `SessionActivityStore.fs` / `SessionActivityService.fs` | Push session-status model: pure fold, SQLite (WAL) store, ingest endpoint + mailbox (see `docs/spec/session-status-push.md`) |
| `src/Server/CodingToolStatus.fs` | Collapse live push session-status into card coding-tool fields (`fromPushSessions`), resume pick, per-worktree provider config |
| `src/Server/PrStatus.fs` | Provider routing, AzDo PR/thread/build fetching |
| `src/Server/GithubPrStatus.fs` | GitHub PR/Actions fetching via `gh` CLI |
| `src/Server/GitWorktree.fs` | Worktree enumeration, commit data, dirty detection, work metrics |
| `src/Server/GlobalConfig.fs` | Machine-level `config.json` store + typed accessors (watched roots, canvas, collapsed repos, last-viewed hashes, editor) |
| `src/Server/WorktreeApi.fs` | `IWorktreeApi` wiring + `DashboardResponse` assembly |
| `src/Server/SyncEngine.fs` | Branch sync pipeline, provider-aware conflict resolution |
| `src/Server/SessionManager.fs` | MailboxProcessor session agent, spawn/focus/kill, persistence |
| `src/Server/Win32.fs` | P/Invoke: EnumWindows, SetForegroundWindow, WM_CLOSE |
| `src/Client/App.fs` | Elmish MVU app: `init`, the `update` `match`, `appSubscriptions`, top-level `view` wiring |
| `src/Client/CardViews.fs` | Worktree card rendering (cards, action buttons, badges, PR/sync/event-log helpers, `repoSection`) via `CardViewProps`/`CardCallbacks` records |
| `src/Client/OverviewViews.fs` | Status-overview row + scheduler footer rendering |
| `src/Client/MascotState.fs` / `MascotView.fs` | Mascot eyes: gaze slice + eye SVG render (observes `ActivityLevel`) |
| `src/Client/ActivityState.fs` / `ActivityUpdate.fs` | User-activity / idle-detection: state slice + `Tick`/`UserActivity` bodies + activity subscription |
| `src/Client/CanvasView.fs` | Canvas pane view wiring (`CanvasPane.view` callbacks/slices) |
| `src/Client/Navigation.fs` | Keyboard navigation: spatial arrow keys, key bindings |
| `src/Tests/fixtures/` | Captured AzDo/GitHub PR + build data and dashboard fixtures for offline tests |

## Decisions

- Web app over TUI: richer layout, easy to keep open in a browser tab
- F# + Fable/Elmish: single language both sides, shared types
- MailboxProcessor over TTL cache: caps concurrent processes, instant API reads
- Polling over WebSocket: simpler, sufficient at 1–15s variable cadence (activity-based)
- Most-overdue task selection: no cursor state, naturally prevents starvation
- `gh`/`az` CLI over raw REST: handles auth, consistent pattern
- Single API call returns all repos: client doesn't need to know repo count
- Repo ID = folder name: simple, human-readable, no config needed
- `CommentSummary` DU over nullable fields: cleanly models provider capability differences
- Push model over log-parsing for coding-tool status: explicit lifecycle events beat mtime inference; one pure server fold replaces three per-provider detectors (see `docs/spec/session-status-push.md`)
- `WorktreePath` over `RepoId * BranchName` composite: already used across the API, inherently unique, no new types needed
- Repo-scoped branch events: prevents name collisions across repos
- net9.0 (not net10.0): Fable 4.28.0 FCS hangs with .NET 10 preview SDK
- Windows Terminal per-window tracking via HWND: tabs aren't reliably addressable, one window per worktree is simple and predictable
- Upstream remote auto-detection over config-only: `upstream` remote name is the universal convention for fork workflows; config override available for non-standard setups
- Watched roots are server-owned and restart-to-apply (not live-updated): `tm add`/`remove` persist to the global config and take effect on the next server (re)start (the `treemon.ps1` shims trigger it when prod is running). Chosen for simpler code — no per-root scheduler-state machinery; live application remains a clean future extension. The server is the single writer of `config.json` (with an internal write lock); the online-only CLI never writes config files, which removes the cross-process clobber hazard.
- `GlobalConfig` vs `TreemonConfig` — the machine-level `~/.treemon/config.json` and the per-worktree `.treemon.json` (`testCommand`, `baseBranch`, `upstreamRemote`) are deliberately separate stores in separate modules, named so the machine-vs-worktree scope is obvious and the two never collide.
- Create-worktree prompt auto-launch is **fire-and-forget, server-side, and reuses `launchAction`**: repo root, provider, and the new path are all in scope on the server, so it orchestrates the launch there rather than via a client follow-up. A failed spawn is logged, not surfaced (the worktree already exists), and it launches even after a post-fork warning. Provider is read **directly** from the new worktree's `.treemon.json` (it isn't in scheduler state yet, so `resolveProvider` would return `None` there), and the worktree path is single-quote-escaped in `SessionManager.buildScript` so a path containing `'` can't break the launch script.
- The create-prompt skill is **chosen per-create via a radio group** (offered skills come from the machine-level `worktreeSkills`; built-in **None** sends the prompt verbatim). The chosen skill rides the create request; the server wraps the prompt with `skillInvocation` for a named skill or launches it verbatim for None. The prompt (and skill) are single-quote-escaped at the CLI sink, so an odd skill value is a no-op for the tool, not an injection concern, making validation pure complication.

## Related Specs

- `docs/spec/user-idle-detection.md` — adaptive refresh cadence based on user activity level
- `docs/spec/keyboard-navigation.md` — spatial arrow-key navigation and key bindings
- `docs/spec/native-session-management.md` — Windows Terminal spawn/focus/kill via HWND tracking
- `docs/spec/future/strong-typed-paths.md` — `AbsolutePath` wrapper type (deferred: entry-point normalization sufficient)
- `docs/spec/contextual-actions.md` — contextual action buttons (fix comments, fix build, create PR) launched from card badges
- `docs/spec/remoting-csrf-hardening.md` — Origin/Referer CSRF guard fronting the remoting and canvas POST surfaces (the create-worktree auto-launch made state-changing remoting an agent-execution sink)
- `docs/spec/canvas-pane.md` — interactive HTML docs in the canvas pane, including awareness, liveness, and bridge routing
