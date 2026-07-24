# Worktree Monitor Dashboard

## Goals

- At-a-glance visibility into all active worktrees across multiple repositories
- Surface activity signals from multiple sources (git, beads, coding AI tools, Azure DevOps, GitHub) so stalled branches are obvious
- Keep worktrees current through an agent-driven auto-sync preference rather than a mechanical Git pipeline
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
- Fixed header bar with system metrics and deploy branch badge
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
- Server resolves repo and branch from path internally; archive and auto-sync persistence store branch names per repo in `.treemon.json`
- Client optimistic state (`DeletedPaths: Set<string>`) filters by path, affecting only the correct repo

### Per-Worktree Card

- Branch name header with work metrics (commit grid + diff stats)
- Coding tool status dots — one per live session (Working / WaitingForUser / Idle), each a context-usage donut (arc = remaining context) when that session has reported usage, else a plain dot; the last known gauge survives server restart for sessions restored from the durable live window. A worktree with no live session shows the single grey NoSession dot. Tooltip shows the status.
- Last commit message + relative time (branch-local, excludes merges from origin/main)
- "N behind {base}" with an always-visible two-arrow auto-sync toggle; dirty indicator
- Beads counts (open / in-progress / done) with progress bar
- PR badge linking to PR page; merge conflict icon when conflicts detected; AzDo: thread resolution ("3/10 threads"), GitHub: comment count
- Build badges per pipeline/workflow run; failed builds show step name (AzDo also shows log tooltip)
- Event log (up to the last 2 events), terminal/delete actions
- Green left border on cards with active terminal sessions
- Contextual action buttons: fix PR comments, fix failed builds, and create PRs

### Branch Sync

- Every card shows a two-arrow auto-sync toggle in the behind-base row, including when the worktree is clean, dirty, behind, or up to date.
- The unpressed toggle uses the normal neutral card-action style. The pressed state reuses the green glow of the active-terminal button and persists per branch in `.treemon.json` under `autoSyncBranches`.
- Clicking the toggle updates the card optimistically and calls `IWorktreeApi.toggleAutoSync`; an API error restores the previous state and activates the dashboard's normal error surface until the next successful data refresh. The card's `S` key binding invokes the same toggle action.
- While persistence is pending for a worktree, the toggle is disabled and additional mouse or `S` key inputs for that path are ignored. Other worktrees remain independently toggleable, and the pending state clears on either success or failure.
- `autoSyncBranches` is intentionally not pruned when a worktree is archived or deleted. Branch-name reuse may restore the preference; avoiding cleanup machinery is preferred for this low-impact case.
- When enabled, fresh Git observations request a sync when the worktree is behind a newly observed base revision. The base revision, not repeated polling of the same behind count, is the deduplication identity.
- Scheduler trigger and fallback-launch guards use the resolved canonical worktree path, so differently-cased API input cannot create or clear alternate keys.
- Refresh-triggered delivery runs as guarded background work so bridge HTTP, registration grace, or fallback launch latency cannot stall the sequential scheduler. The explicit toggle API awaits its immediate trigger attempt before returning.
- The prompt targets the active open session when one is running; otherwise it targets the open
  session with the greatest activity `UpdatedAt`. A retained/offline session identity is used only
  when no open session exists (see `docs/spec/session-status-push.md`).
- A live session receives the prompt immediately through `SessionBridge`; the extension serializes it through the same `enqueueSend` chain used by canvas messages, including while the session is busy.
- `SessionBridge` POSTs a typed `{kind,prompt}` envelope. The extension passes `agent-prompt` text verbatim to `session.send`; `canvas` retains the existing `[canvas]` display/routing prefix.
- A selected session whose bridge is not registered gets a bounded registration grace period. If its bridge appears, delivery continues there; only a confirmed absence after the grace period opens a terminal and starts a new session with the sync prompt.
- A failed POST to a known live bridge queues the session-targeted prompt for retry instead of launching a replacement session. A per-worktree in-flight guard prevents duplicate fallback launches.
- The prompt asks the agent to sync with `{upstreamRemote}/{baseBranch}` when safe, preserve in-progress work, and run appropriate checks.
- Treemon does not run a separate pull/merge/conflict-resolution/test/commit/push pipeline. Agent prompt acceptance is observable; completion of the Git synchronization is not.

### Contextual Card Actions

- An open PR with unresolved review threads shows **Fix PR comments** beside the thread badge. A failed build with a result URL shows **Fix build** beside that build badge. A branch with no PR shows **Create PR**, except on `main` and `master`.
- The same buttons render in full and compact cards. Clicking one sends an `ActionKind` through `IWorktreeApi.launchAction`; the button stays visible but is disabled for a per-worktree 10-second cooldown to prevent duplicate launches.
- `FixPr` and `FixBuild` become provider-specific skill prompts; `CreatePr` uses the fixed commit/push/create-PR prompt. The command runs interactively so the user can inspect or continue the session.
- Session placement is server-owned: an existing tracked Windows Terminal window gets a new action tab; otherwise Treemon opens and tracks a new window. See `docs/spec/native-session-management.md`.

### Coding Tool Detection

Coding-tool status is **pushed** by the Copilot CLI extension, not parsed from session log files —
the per-provider log-parsing detectors (`ClaudeDetector`, `CopilotDetector`, `VsCodeCopilotDetector`,
`getStatusFromFiles`) have been **removed**. The extension observes the SDK session event stream and
POSTs lifecycle events to the server, which folds them into live per-session state and collapses each
worktree's sessions in `CodingToolStatus.fs` (`fromPushSessions`). Explicit background-agent
lifecycle events are folded into process-local per-tool clocks so a root turn cannot settle Idle
while delegated agents are still running. The clocks are intentionally forgotten on a Treemon
restart in exchange for a much simpler persistence model. See
`docs/spec/session-status-push.md` for the full model.

- Status vocabulary is `CodingToolStatus = Working | WaitingForUser | Idle | NoSession`; the dot is a
  pure function of the collapsed status (red / yellow / blue open-idle / grey no-session).
- `.treemon.json` optional `"codingTool": "claude"|"copilot"` still selects the per-worktree
  provider for command-building (`readConfiguredProvider`); the push status source is Copilot-CLI-only today.
- The card footer has up to three lines: the freshest source-tagged activity (`assistant.intent` or
  the session title) with an optional `▶ <skill>` pill, the last genuine user message (never a
  `<skill-context>` injection or runtime `<system_reminder>`), and the last assistant message tagged
  with its coding-tool provider.
  The title is bootstrapped from session metadata on join/rejoin when the ephemeral
  `session.title_changed` event was missed; `assistant.intent` remains optional enrichment when the
  CLI emits it. The last-user line is serialized as
  `UserFooterMessage { Glyph; Text; Timestamp }`. Canvas prompts are projected by
  `UserMessageFormatting` into concise display text plus `MessageGlyph.Canvas`; the same server
  classifier suppresses runtime system reminders before ingestion and when projecting retained
  footer data. Built-in selection actions show their `request`, known actions get action-specific
  summaries, and unknown valid JSON is formatted structurally without changing string values.
  Activity titles use the same text projection before duplicate suppression, so a raw canvas title
  cannot reappear beside its formatted user-message line. Canvas notifications render alongside all
  footer lines rather than replacing them.

### Create Worktree

A "+" button on each repo header opens a modal to create new worktrees without leaving the dashboard.

- **Name input** (auto-focused) + **source branch dropdown** (sorted: main > master > develop > dev* > alphabetical from dashboard worktrees)
- Treemon creates the worktree itself: it fetches the base branch from the upstream remote, then forks via `git worktree add -b {name} --no-track {parentDir}/tm-{name} {baseRef}`. `baseRef` prefers the remote-tracking ref `{remote}/{base}` — so a new worktree forks from the upstream tip rather than a possibly-stale local branch — falling back to the local `{base}` branch when no remote-tracking ref exists. `--no-track` is required: without it git's default `autoSetupMerge` makes the new branch inherit `baseRef`'s upstream (e.g. `origin/{base}`), so PR detection (keyed off `@{u}`) would show the *base* branch's PR on the new worktree until it is first pushed. A freshly forked branch has no remote yet, so it correctly starts with no upstream. No worktree needs the base checked out; fetch/remote failures fall back to whatever ref is available.
- After creation, an optional `post-fork.ps1` (Windows) / `post-fork.sh` (Unix) in the repo root runs **inside the new worktree**, receiving `{worktreePath} {sourceRepoRoot} {baseRef} {branchName}`. It is for setup only (symlinks, dependency install). Because setup can be slow, it runs **asynchronously in a background task** *after* the create call returns, capped at a **5-minute timeout** (a run that exceeds it is treated as a failure). Its lifecycle is tracked in `CardEventLog` (`PostForkStarted` → `PostForkEnded(status)`), and the client refreshes those card events through `getSyncStatus` on the normal dashboard poll. Only a **failure** (a genuine failure or a timeout) is surfaced on the worktree card — a still-running or successful setup is routine noise and stays hidden. A failure is non-fatal since the worktree already exists.
- Legacy `fork.ps1`/`fork.sh` scripts are **no longer executed** — Treemon now owns forking. If one is present, creation still succeeds but returns a warning to migrate setup steps into `post-fork.*`.
- Warnings returned by `createWorktree` (`Result<string list, string>`) now carry **only the legacy-fork-script advisory** and are surfaced in the modal (UI) or console (CLI); post-fork failures are surfaced on the card (successful runs stay hidden), not through this return value. Internally, `forkWorktree` performs the fork (returning a `ForkResult`) and `runPostFork` runs the hook.
- Modal shows creating animation, then auto-closes on clean success, or shows warnings / error
- Server expedites worktree list refresh for the repo so the new card appears quickly
- **Optional prompt** — a multi-line textarea below the source-branch dropdown. A non-blank value auto-launches a coding-agent session in the new worktree; **Enter inserts a newline** (it does not submit — the Create button submits, Escape closes), and a blank/whitespace prompt is a no-op (identical to the no-prompt flow). The prompt rides the create request as a `string option`.
- **Skill selection** — a radio group between the source-branch dropdown and the prompt textarea chooses which skill wraps the prompt on launch. A built-in **None** option (always present) sends the prompt **verbatim**; each configured skill wraps it. The chosen skill rides the create request as a `string option` (`None` ⇒ verbatim). The offered skills are the machine-level `worktreeSkills` list (see below); the first entry is the default selection. When no skills are configured the only option is None, and a subtle hint next to it points at `~/.treemon/config.json` (`worktreeSkills`).
- On a non-blank prompt the server, after a successful create, **fire-and-forget** spawns a tracked coding-agent window in the new worktree. When a skill was chosen it seeds a provider-aware skill invocation (`use {skill} skill with {prompt}` for Copilot, `/{skill} {prompt}` for Claude); for **None** it seeds the prompt verbatim. It reuses `SessionManager.launchAction` — the same tracked-window launch path as contextual card actions — so there is no bespoke spawn logic; the modal still returns/closes on the create result and does not wait for the window. The launch runs even when create returned a post-fork warning.
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

### Merged-PR Persistence

- Live provider results are authoritative. When the bounded PR fetch no longer returns a tracked upstream branch, a persisted merged record supplies a fallback `HasPr`; live `HasPr` entries are never overridden. Only the terminal merged fact is retained — open PRs and volatile builds, comments, draft, and conflict state are not persisted.
- `MergedPrStore` keeps `repo → upstream branch → { Id; Title; Url; HeadSha }` in port-scoped gitignored runtime state at `data/merged-prs-{port}.json`. Each server instance owns its store; missing, corrupt, identity-less, or older incompatible records start empty, and failed writes remain dirty in memory until a retry succeeds.
- Records are pruned to live branches only from a trustworthy enumeration: at least one eligible worktree and branch exist, every non-ignored worktree has collected git data, and no eligible worktree's upstream read failed. Otherwise live merge upserts and fallback overlay still run, but pruning is skipped.
- The upstream/provider branch identifies the association; `HeadSha` is the immutable provider-reported PR source commit. Fallback accepts a match against any current worktree tip for that upstream branch, and a present mismatch evicts the stale record so branch-name reuse or later unmerged commits cannot inherit an old merged badge.
- `PrStatus.lookupPrStatus`, `WorktreeApi`, and auto-sync behavior are unchanged by persistence. The `WorktreeApi` PR lookup obtains the branch name from the upstream-state representation through `GitWorktree.upstreamBranchName`.

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
- Client polls every 1–15s depending on activity level (see `docs/spec/user-idle-detection.md`)

### Refresh Intervals

Intervals adapt to user activity level (Active / Idle / Deep Idle). See `docs/spec/user-idle-detection.md` for the full interval table and activity state definitions. The Idle column matches the original fixed values shown here historically.

### PR Provider Routing

- `RemoteInfo` DU: `AzureDevOps of AzDoRemote | GitHub of GithubRemote`
- `detectProvider` inspects `git remote get-url {upstreamRemote}`, routes to appropriate fetcher
- Unknown remotes produce empty PR data — other sources unaffected

### Upstream Remote Resolution

For fork workflows (push to fork, PRs in upstream repo), treemon auto-detects and uses the correct remote:

- **Resolution order**: `.treemon.json` `"upstreamRemote"` field → auto-detect `upstream` remote → fall back to `origin`
- **Affects**: PR fetching (remote URL), base branch comparisons (`{remote}/{baseBranch}`), fetch cycle, auto-sync prompt target
- **Stored** per-repo in `PerRepoState.UpstreamRemote`, resolved during worktree list refresh
- **Config example**: `{ "upstreamRemote": "upstream" }` in `.treemon.json` at repo root

### Base Branch Resolution

Each repo can configure which branch is considered the "base" for ahead/behind counts, diff stats, fetch, fast-forward, and auto-sync prompts:

- **Resolution**: `.treemon.json` `"baseBranch"` field → default `"main"`
- **Affects**: `git rev-list` behind/commit counts, `git diff --shortstat`, `git fetch`, fast-forward, auto-sync prompt target, branch sort priority
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
| `src/Server/RefreshScheduler.fs` | MailboxProcessor state agent, repo-keyed task scheduling, merged-PR reconciliation |
| `src/Server/SessionActivity.fs` / `SessionActivityStore.fs` / `SessionActivityService.fs` | Push session-status model: pure live fold, SQLite (WAL) base-state/history store, ingest endpoint + mailbox (see `docs/spec/session-status-push.md`) |
| `src/Server/UserMessageFormatting.fs` | Server-owned system-reminder suppression and canvas prompt projection shared by ingestion, activity, and footer fields |
| `src/Server/CodingToolStatus.fs` | Collapse live push session-status into card coding-tool fields (`fromPushSessions`), resume pick, and per-worktree provider config |
| `src/Server/AutoSync.fs` | Agent auto-sync prompt, open-session-first target selection, base-revision eligibility, and live-delivery/fallback orchestration |
| `src/Server/CardEventLog.fs` | Transient post-fork lifecycle events surfaced on worktree cards through `getSyncStatus` |
| `src/Server/SessionBridge.fs` | Generic session registration, liveness, queued prompt delivery, and forwarding |
| `src/Extension/extension.mjs`, `session-prompt.mjs` | Session bridge HTTP receiver, serialized `session.send` queue, and typed prompt-transport decoding |
| `src/Server/PrStatus.fs` | Provider routing, AzDo PR/thread/build fetching |
| `src/Server/GithubPrStatus.fs` | GitHub PR/Actions fetching via `gh` CLI, including the bounded recent-closed window |
| `src/Server/MergedPrStore.fs` | Durable merged-PR fallback reconciliation, identity checks, and runtime-state persistence |
| `src/Server/GitWorktree.fs` | Worktree enumeration, commit data, upstream-read state, HEAD identity, observed base revision, dirty detection, work metrics |
| `src/Server/TreemonConfig.fs` | Repo-local `.treemon.json` persistence for auto-sync branches, archived branches, base branch, and upstream remote |
| `src/Server/GlobalConfig.fs` | Machine-level `config.json` store + typed accessors (watched roots, canvas, collapsed repos, last-viewed hashes, editor) |
| `src/Server/WorktreeApi.fs` | `IWorktreeApi` wiring + `DashboardResponse` assembly |
| `src/Server/SessionManager.fs` | MailboxProcessor session agent, spawn/focus/kill, persistence |
| `src/Server/Win32.fs` | P/Invoke: EnumWindows, SetForegroundWindow, WM_CLOSE |
| `src/Client/App.fs` | Elmish MVU app: `init`, the `update` `match`, `appSubscriptions`, top-level `view` wiring |
| `src/Client/CardViews.fs` | Worktree card rendering, including the persistent two-arrow auto-sync toggle, action buttons, badges, and event-log helpers |
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
- Agent-driven auto-sync over a deterministic pipeline: one persistent preference delegates synchronization and conflict handling to the coding session instead of maintaining a second Git/test/push implementation.
- Nonblocking polling over awaited refresh delivery: scheduled Git observations dispatch guarded auto-sync work in the background to preserve polling cadence, while the explicit toggle path awaits delivery so its request lifecycle stays deterministic.
- Open-session-first auto-sync target: prefer the active open winner, then the greatest-`UpdatedAt`
  open idle session; use retained/offline identity only when no open session exists. Delivery
  liveness outranks footer identity so an open CLI receives the prompt instead of triggering an
  unnecessary second session.
- Generic `SessionBridge` under canvas routing: session registration, liveness, queueing, and prompt forwarding are shared infrastructure; `CanvasBridge` retains only document ownership and canvas-specific message semantics.
- New session fallback: wait briefly for a selected session's bridge registration, then launch a new prompted session only when no live bridge exists; delivery failures to known live bridges stay queued for that session rather than creating parallel agents.
- net9.0 (not net10.0): Fable 4.28.0 FCS hangs with .NET 10 preview SDK
- Windows Terminal per-window tracking via HWND: tabs aren't reliably addressable, one window per worktree is simple and predictable
- Upstream remote auto-detection over config-only: `upstream` remote name is the universal convention for fork workflows; config override available for non-standard setups
- Watched roots are server-owned and restart-to-apply (not live-updated): `tm add`/`remove` persist to the global config and take effect on the next server (re)start (the `treemon.ps1` shims trigger it when prod is running). Chosen for simpler code — no per-root scheduler-state machinery; live application remains a clean future extension. The server is the single writer of `config.json` (with an internal write lock); the online-only CLI never writes config files, which removes the cross-process clobber hazard.
- `GlobalConfig` vs `TreemonConfig` — the machine-level `~/.treemon/config.json` and the repo-local `.treemon.json` (`autoSyncBranches`, `baseBranch`, `upstreamRemote`) are deliberately separate stores in separate modules, named so the machine-vs-repo scope is obvious and the two never collide.
- Create-worktree prompt auto-launch is **fire-and-forget, server-side, and reuses `launchAction`**: repo root, provider, and the new path are all in scope on the server, so it orchestrates the launch there rather than via a client follow-up. A failed spawn is logged, not surfaced (the worktree already exists), and it launches even after a post-fork warning. Provider is read **directly** from the new worktree's `.treemon.json` (it isn't in scheduler state yet, so `resolveProvider` would return `None` there), and the worktree path is single-quote-escaped in `SessionManager.buildScript` so a path containing `'` can't break the launch script.
- The create-prompt skill is **chosen per-create via a radio group** (offered skills come from the machine-level `worktreeSkills`; built-in **None** sends the prompt verbatim). The chosen skill rides the create request; the server wraps the prompt with `skillInvocation` for a named skill or launches it verbatim for None. The prompt (and skill) are single-quote-escaped at the CLI sink, so an odd skill value is a no-op for the tool, not an injection concern, making validation pure complication.

## Related Specs

- `docs/spec/user-idle-detection.md` — adaptive refresh cadence based on user activity level
- `docs/spec/keyboard-navigation.md` — spatial arrow-key navigation and key bindings
- `docs/spec/native-session-management.md` — Windows Terminal spawn/focus/kill via HWND tracking, including the no-live-session auto-sync fallback
- `docs/spec/future/strong-typed-paths.md` — `AbsolutePath` wrapper type (deferred: entry-point normalization sufficient)
- `docs/spec/remoting-csrf-hardening.md` — Origin/Referer CSRF guard fronting the remoting and canvas POST surfaces (the create-worktree auto-launch made state-changing remoting an agent-execution sink)
- `docs/spec/canvas-pane.md` — interactive HTML docs and the canvas-specific consumer of the generic session bridge
- `docs/spec/session-status-push.md` — coding-tool session collapse and the related auto-sync target selection
