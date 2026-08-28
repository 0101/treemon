# Worktree Monitor Dashboard

## Goals

- At-a-glance visibility into all active worktrees across multiple repositories
- Surface activity signals from multiple sources (git, beads, coding AI tools, Azure DevOps, GitHub) so stalled branches are obvious
- Keep enabled worktrees current through an open-session agent path and a narrow mechanical fast path when no coding session is open
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
- Roots are managed live through the `tm` CLI — `tm add <path>...` (validates the path exists, normalizes it, no-op if already watched), `tm remove <path>...` (errors on an unknown path; removing the last root is allowed), and `tm roots` (list). All three are online-only (require the running server). The server is the single, serialized writer of `config.json`; changes persist immediately and take effect on the next server (re)start. The `treemon.ps1 add`/`remove` shims restart running production outside embedded terminals; inside one, the change stays persisted and requires an external PowerShell restart (see `docs/spec/embedded-terminal.md`).
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

- Branch name header with work metrics (commit grid + diff stats) only when committed history has a net diff from the base
- Coding tool status dots — one per live session (Working / WaitingForUser / Idle), each a context-usage donut (arc = remaining context) when that session has reported usage, else a plain dot; the last known gauge survives server restart for sessions restored from the durable live window. A worktree with no live session shows the single grey NoSession dot. Tooltip shows the status.
- Last commit message + relative time (branch-local, excludes merges from origin/main)
- "N behind {base}" with an always-visible circular two-arrow auto-sync toggle and tracked staged/unstaged dirty indicator
- Beads counts (open / in-progress / done) with progress bar
- PR badge linking to PR page; merge conflict icon when conflicts detected; AzDo: thread resolution ("3/10 threads"), GitHub: comment count
- Build badges per pipeline/workflow run; failed builds show step name (AzDo also shows log tooltip)
- Event log (up to the last 2 events), diff/auto-sync/terminal/delete actions
- Green left border on cards with active terminal sessions
- Contextual action buttons: fix PR comments, fix failed builds, and create PRs
- Archived worktrees render below the card grid as one-line cards on the same responsive grid columns, so they align with the full cards above. Each shows the branch name (never dropped), then the commit grid and diff stats only while they fit the remaining width, the compact commit age (`123d`, no "ago" suffix), and the unarchive button.

### Branch Sync

- Every card shows a circular two-arrow auto-sync toggle in the behind-base row, including when the worktree is clean, dirty, behind, or up to date.
- The unpressed toggle uses the normal neutral card-action style. The pressed state reuses the green glow of the active-terminal button and persists per branch in `.treemon.json` under `autoSyncBranches`.
- Clicking the toggle updates the card optimistically and calls `IWorktreeApi.toggleAutoSync`; an API error restores the previous state and activates the dashboard's normal error surface until the next successful data refresh. The card's `S` key binding invokes the same toggle action.
- While the request is in flight for a worktree, the toggle is disabled and additional mouse or `S` key inputs for that path are ignored; it turns its circular two-arrow glyph and reads "Syncing with {base}…" so an enable that reaches the network is visibly working rather than dead. Other worktrees remain independently toggleable, and the pending state clears on either success or failure.
- Archiving or deleting a worktree does not prune `autoSyncBranches`; branch-name reuse may restore the preference. When PR reconciliation identifies a merged PR it removes the corresponding enabled local branch from `autoSyncBranches`, so the card's toggle deactivates on the next dashboard refresh, and clears the accepted-revision record of every worktree it observes merged. That clear ignores the preference: a record written by an operation already past its eligibility check when the preference was removed is erased by the next reconciliation instead of outliving it. A `.treemon.json` write that fails leaves the preference for that next reconciliation to retry rather than discarding the PR data the refresh just fetched.
- Eligibility is the persisted preference *and* the absence of a known merged PR, and it is re-read immediately before an operation acts, since PR refresh runs on its own cadence. A branch disabled or reconciled merged after the observation that started the operation delivers nothing and records nothing. Nothing is cancelled once a prompt has been accepted or a Git command is already running.
- When enabled, fresh Git observations start a sync only when the worktree is behind a newly observed base revision. The canonical worktree path plus that base revision, not repeated polling of the same behind count, is the deduplication identity. One durable accepted record carries it: the per-worktree operation guard already serializes work inside one process, so the record only has to answer whether this exact revision was already prompted and how long ago, which it does across a restart too.
- An accepted agent prompt — and only an accepted one, so a rejection or a crash before acceptance retries — persists the canonical worktree path, base revision, and acceptance time in port-scoped gitignored runtime state at `data/auto-sync-{port}.json`; a missing, corrupt, or incomplete record loads as empty, so an unusable record re-prompts rather than suppressing a sync. The same revision stays suppressed for one hour, long enough to cover an in-progress sync; a different revision triggers immediately. Once the hour passes, that revision is acted on exactly once more — with or without a restart — and the new acceptance restarts the window. That retry cannot duplicate a prompt into an agent's queue, because an agent still working on the previous one makes the worktree busy and defers the observation. Catching up clears the record, as do disabling the preference and deleting the worktree — through the API or outside Treemon, since a worktree that disappears from a *successful* discovery is cleared too, and a discovery that failed reports no removals rather than reading a Git error as every worktree vanishing. So falling behind the same revision again prompts instead of staying suppressed, and a worktree recreated at a path that once held a record does not inherit its suppression.
- The scheduler trigger guard uses the resolved canonical worktree path, so differently-cased API input cannot create or clear alternate keys.
- Refresh-triggered delivery runs as guarded background work so bridge HTTP, registration grace, or fallback launch latency cannot stall the sequential scheduler. The explicit toggle API awaits its immediate trigger attempt before returning.
- The target model distinguishes a session that is *busy* from a settled-idle one, and both from retained/offline identity. A session mid-turn — background agents included — makes the worktree busy, and so does one that went idle less than `settleWindow` (30 s) ago: status dips to idle for milliseconds between back-to-back turns, so an instantaneous reading would let a fetch and merge start under an agent about to resume. Otherwise the open session with the greatest activity `UpdatedAt` is the settled-idle target. Retained identity is used only for agent fallback when no session is open (see `docs/spec/session-status-push.md`).
- A busy worktree is waited for, not prompted: nothing is delivered, nothing is mutated, and no acceptance is recorded, so the next observation simply looks again. Asking a session mid-turn to sync itself only queues a prompt behind work that can run for hours — unobservable to Treemon, and re-sent once the acceptance record expired — while the sync it asks for is work Git can do unattended the moment the worktree is free.
- Once no session is busy, Treemon attempts a bounded Git-only sync — whether the worktree has no session at all or a settled-idle one, including a session blocked on its user. An open terminal is not evidence that anyone is acting: waiting for it to close before syncing is a condition no user would guess, and the clean-worktree refusal below already protects work in progress. It merges only a worktree proven clean — local content, or a content probe that could not answer, both refuse — fetches `{upstreamRemote}/{baseBranch}` for that worktree, tries a fast-forward and then a non-editing merge, aborts conflicts, and verifies that the fetched base revision is an ancestor of `HEAD`. Cleanliness is proven twice, before the fetch and again immediately before the merge, because the fetch reaches the network and the tree can be edited while it runs. The sync is asked for one named branch and each merge attempt is bound to it: the branch is re-read from the worktree immediately before each merge command, so a checkout landing in that window refuses instead of merging the base into a branch nobody observed. Every command in the mechanical path runs with repository hooks disabled — `pre-merge-commit`, `post-merge`, and `pre-push` are project-controlled scripts, and this path runs unattended.
- After a successful mechanical sync, the reconciled PR status the operation already holds decides the push rule: an open pull request gets a non-force push of the observed branch, and anything else — no PR, a merged PR, a closed-unmerged PR — finishes locally. That status is a three-state `PrInfo.State` rather than a merged flag, because a merged flag alone cannot tell a closed-unmerged pull request from an open one. A status that has never been loaded is not the same answer as "no pull request": until a PR refresh has succeeded for the repository, the worktree is left for the next refresh instead of being merged locally, which would consume the only observation of that base revision without deciding the push. Once loaded, the status is at most one PR refresh old: a PR closed since then costs one harmless non-force push, and one *opened* since then is the asymmetric case — the merge finishes locally, nothing records the missing push, and the branch is published only when the base advances again and a later sync pushes the branch tip. The push names the observed branch and the remote and branch git itself recorded as its upstream, as an explicit refspec, so neither a push default nor whatever `HEAD` points at picks what moves; the push re-reads the checked-out branch first, and a recorded remote starting with a dash — which git would read as an option rather than as a destination — is refused before the command is built. An unconfigured or option-like upstream, an upstream naming a different branch than the one being synced, or a remote that has moved ahead, fails to the agent path rather than being forced. The mechanical path does not run project builds or tests. A completed mechanical sync re-reads that worktree's Git state immediately instead of waiting for the next scheduled pass: the sync is what made the card's behind count stale, so leaving it to the normal cadence would show a worktree as behind a base it has already merged.
- Dirty state, conflicts, failed aborts, command failures, a worktree that changed branches, and failed pushes all fall back to one agent prompt containing only a closed structured reason. When the worktree has an idle open session, that prompt goes to it rather than launching a second terminal. Raw Git output, paths, filenames, branch text, and commit messages never enter the prompt. Eligibility and the target are both re-read once more before that fallback prompt — a fetch and a merge have run since the pre-mutation check — so a branch disabled or reconciled merged meanwhile is dropped rather than handed to an agent, and a session that resumed while the sync ran receives nothing. The reconciled PR status becomes an explicit push or do-not-push instruction instead of asking the agent to rediscover it; if the post-attempt PR cache read is temporarily unavailable, the prompt keeps the known status from immediately before the mutation. An agent resuming is itself the likeliest cause of the dirty worktree that sends the sync down this path, so the worktree it would be prompted about is exactly the one it is now working in; nothing is recorded either way, so the next observation retries.
- One per-worktree operation guard covers target selection, mechanical work, and delivery — including any fallback session launch delivery makes — so a later fetch cannot overlap an in-progress sync even if it observes a newer base revision, and no second guard is needed to keep duplicate launches out. It is also what makes the durable record the only deduplication layer needed: no second operation for the worktree can read or write that record concurrently. Only the operation that took the guard releases it, in a `finally`; worktree bookkeeping never prunes it, since a path can disappear from discovery while its sync is still merging and dropping the guard there would license an overlapping one.
- `SessionBridge` POSTs a typed `{kind,prompt}` envelope. The extension coalesces identical sends that have not started; once `session.send` starts, a later identical prompt is eligible again. See `docs/spec/canvas-pane.md` for the shared queue contract.
- A selected session whose bridge is not registered gets a bounded registration grace period. If its bridge appears, delivery continues there; only a confirmed absence after the grace period opens a terminal and starts a new session with the sync prompt.
- A failed POST to a known live bridge queues the session-targeted prompt for retry instead of launching a replacement session.
- Agent prompt acceptance is observable; completion of agent-owned synchronization is not. Mechanical completion is accepted only after the Git and conditional-push checks above succeed.

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
- Per provider branch, an open PR wins; otherwise the most recently updated closed PR wins. A newer merged PR must not be masked by an older closed-unmerged PR for a reused branch name.
- Review thread resolution uses GraphQL (`PullRequest.reviewThreads.nodes.isResolved`) — REST API does not expose resolution status
- Dashboard renders `"{unresolved}/{total} threads"` badge, matching ADO format; dimmed when all resolved; action button only when unresolved threads exist
- Merged PRs return `WithResolution(0, 0)` without a network call; PRs with zero threads show no badge
- `first: 100` thread limit is acceptable — PRs rarely exceed 100 review threads
- GitHub Actions workflow runs mapped to `BuildInfo` / `BuildStatus`; failed runs fetch job details for step name
- Per open PR, an extra detail fetch (`/repos/{owner}/{repo}/pulls/{number}`) retrieves `mergeable` status; run in parallel with Actions fetch, adding no sequential latency

### Merged-PR Persistence

- Live provider results are authoritative. PR association uses the resolved provider branch. When a deleted remote ref makes `@{u}` unresolvable, Treemon reads Git's still-configured upstream name; only if both reads fail does it fall back to the local branch while disabling pruning. This keeps merged PRs linked when the provider deletes their source branch, including differently named local/provider branches. When the bounded PR fetch no longer returns the branch, a persisted merged record supplies a fallback `HasPr`; live open PRs and identity-matched merged PRs take precedence. Only the terminal merged fact is retained — open PRs and volatile builds, comments, draft, auto-merge, and conflict state are not persisted.
- `MergedPrStore` keeps `repo → upstream branch → { Id; Title; Url; HeadSha }` in port-scoped gitignored runtime state at `data/merged-prs-{port}.json`. Each server instance owns its store; missing, corrupt, identity-less, or older incompatible records start empty, and failed writes remain dirty in memory until a retry succeeds.
- Records are pruned to live branches only from a trustworthy enumeration: at least one eligible worktree and branch exist, every eligible worktree has collected git data, and no eligible worktree's upstream read failed. Archived worktrees are exempt from the completeness requirement — the steady-state refresh skips them, so one first seen while already archived never collects git data; its worktree-list branch enters the enumeration instead, keeping its record safe without blocking pruning. Otherwise live merge upserts and fallback overlay still run, but pruning is skipped.
- The provider branch identifies the association; `HeadSha` is the immutable provider-reported PR source commit. A merged provider result or persisted fallback is accepted only when its SHA matches a current worktree tip when both are known. A present mismatch evicts or suppresses the stale result so branch-name reuse or later unmerged commits cannot inherit an old merged badge.
- GitHub matches pull requests by head *ref*, which is a bare branch name shared across forks, so a fetched PR counts only when its head repository is one this checkout could push to: the owners of the configured remotes' push URLs, plus the upstream owner. That keeps fork workflows working — a collaborator's fork is a configured remote — while an arbitrary outsider's fork PR cannot decide a same-named local branch's PR status or open its auto-sync push gate. Logins compare case-insensitively; a head whose repository is gone has no owner and is excluded rather than trusted.
- Failed upstream reads still make branch enumeration untrustworthy and disable pruning when only the local fallback branch is available for PR discovery and card lookup.

### Merge Conflict Detection

- `HasConflicts: bool` on `PrInfo` — `true` when the PR has merge conflicts
- AzDo: parsed from `mergeStatus` field in existing `az repos pr list` response (`"conflicts"` → true, all others → false)
- GitHub: parsed from `mergeable` field in per-PR detail response (`false` → conflicts, `true`/`null` → no conflicts)
- Merged PRs always have `HasConflicts = false`; unknown/computing states treated as no conflicts (resolves on next poll)
- Client renders an inline conflict icon (⚔) on the PR badge when `HasConflicts = true`

### Auto-Merge Indicator

- `AutoMergeEnabled: bool` on `PrInfo` — the provider will merge the PR by itself once the remaining checks and policies pass
- GitHub: `auto_merge` in the existing `/pulls` list response (object → true, `null`/absent → false)
- AzDo: `autoCompleteSetBy` in the existing `az repos pr list` response (identity → true, `null`/absent → false); AzDo names the same feature auto-complete
- Both providers keep the field populated after the PR merges, so parsing forces `false` for merged PRs — the fact is only meaningful while the PR is open
- Client renders an inline check-circle icon on the PR badge when `AutoMergeEnabled = true`; `tm status` adds an `auto-merge` flag

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
- **Stored** per-repo in `PerRepoState.UpstreamRemote`, resolved during worktree list refresh. Generated diff summaries consume this stored value for every root or linked worktree in the repo rather than re-reading config from the selected worktree path.
- **Config example**: `{ "upstreamRemote": "upstream" }` in `.treemon.json` at repo root

### Base Branch Resolution

Each repo can configure which branch is considered the "base" for ahead/behind counts, diff stats, fetch, fast-forward, and auto-sync prompts:

- **Resolution**: `.treemon.json` `"baseBranch"` field → default `"main"`
- **Affects**: committed `git rev-list`/`git diff --shortstat` metrics use the remote-tracking ref when available and otherwise the local branch. Behind count and auto-sync target use only the remote-tracking ref; a local fallback or missing base reports zero behind. Missing-base refreshes retain last-commit, upstream, tracked-dirty, and local/untracked diff data while omitting committed metrics.
- **Stored** per-repo in `PerRepoState.BaseBranch`, resolved during worktree list refresh. The dashboard and generated diff viewer therefore expose the same base branch, including for linked worktrees without their own `.treemon.json`.
- **Config example**: `{ "baseBranch": "dev" }` in `.treemon.json` at repo root

### Diff Categories

The diff viewer's optional per-repository grouping of changed files:

- **Resolution**: `.treemon.json` `"diffCategories"` array → absent means the flat file list
- **Affects**: only the generated diff viewer's grouping and its categorization warning; comparison semantics, layers, and file identities are unchanged
- **Not stored** in scheduler state: the canvas server resolves the request's owning repository root and reads and validates the field on every diff-summary request, so an edited or agent-written configuration shows up on the next Refresh instead of at the next scheduler cycle. Linked worktrees therefore read the repo-root value, like `baseBranch` and `upstreamRemote`.
- **Schema, matching, and validation**: see `docs/spec/diff-file-categories.md`
- **Config example**: `{ "diffCategories": [ { "name": "Server", "patterns": ["src/Server/**"] } ] }` in `.treemon.json` at repo root

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
| `src/Server/SchedulerState.fs` | Dashboard state model, `StateMsg` protocol, and the MailboxProcessor state agent |
| `src/Server/RefreshScheduler.fs` | Repo-keyed task scheduling, task execution, merged-PR reconciliation |
| `src/Server/SessionActivity.fs` / `SessionActivityStore.fs` / `SessionActivityService.fs` | Push session-status model: pure live fold, SQLite (WAL) base-state/history store, ingest endpoint + mailbox (see `docs/spec/session-status-push.md`) |
| `src/Server/UserMessageFormatting.fs` | Server-owned system-reminder suppression and canvas prompt projection shared by ingestion, activity, and footer fields |
| `src/Server/CodingToolStatus.fs` | Collapse live push session-status into card coding-tool fields (`fromPushSessions`), resume pick, and per-worktree provider config |
| `src/Server/AutoSync.fs` | Busy-worktree deferral, mechanical-sync orchestration for every free worktree, agent delivery of structured fallback reasons, and base-revision eligibility |
| `src/Server/AutoSyncStore.fs` | Port-scoped accepted-base-revision persistence used for restart-safe prompt deduplication |
| `src/Server/CardEventLog.fs` | Transient post-fork lifecycle events surfaced on worktree cards through `getSyncStatus` |
| `src/Server/SessionBridge.fs` | Generic session registration, liveness, queued prompt delivery, and forwarding |
| `src/Extension/extension.mjs`, `session-prompt.mjs`, `send-queue.mjs` | Session bridge HTTP receiver, serialized `session.send` queue with pending-duplicate coalescing, and typed prompt-transport decoding |
| `src/Server/PrStatus.fs` | Provider routing, AzDo PR/thread/build fetching |
| `src/Server/GithubPrStatus.fs` | GitHub PR/Actions fetching and open-first per-branch selection |
| `src/Server/MergedPrStore.fs` | Durable merged-PR fallback reconciliation, identity checks, and runtime-state persistence |
| `src/Server/GitWorktree.fs` | Worktree enumeration and lifecycle (fork/remove), commit data, upstream-read state, HEAD identity, observed base revision, dirty detection, work metrics |
| `src/Server/GitBranchSync.fs` | Bounded mechanical sync of a worktree onto its base and non-force branch push |
| `src/Server/TreemonConfig.fs` | Repo-local `.treemon.json` persistence for auto-sync branches, archived branches, base branch, upstream remote, and the raw `diffCategories` read |
| `src/Server/GlobalConfig.fs` | Machine-level `config.json` store + typed accessors (watched roots, canvas, collapsed repos, last-viewed hashes, editor) |
| `src/Server/WorktreeApi.fs` | `IWorktreeApi` wiring + `DashboardResponse` assembly |
| `src/Server/SessionManager.fs` | MailboxProcessor session agent, spawn/focus/kill, persistence |
| `src/Server/Win32.fs` | P/Invoke: EnumWindows, SetForegroundWindow, WM_CLOSE |
| `src/Client/App.fs` | Elmish MVU app: `init`, the `update` `match`, `appSubscriptions`, top-level `view` wiring |
| `src/Client/CardViews.fs` | Worktree card rendering, including the persistent circular two-arrow auto-sync toggle, action buttons, badges, and event-log helpers |
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
- Narrow mechanical sync with agent fallback: when no session is open, Treemon may fetch and merge only a clean worktree and may non-force push only when the reconciled PR status says a pull request is open. Any ambiguity or failure delegates to an agent, and project-specific checks remain agent/CI work.
- Cached PR openness over an action-time provider query: the PR refresh already fetches open and closed pull requests separately, so `PrInfo.State` carries that answer to the push decision instead of a second per-branch lookup. Data one refresh old is acceptable here because the push is non-force; an *unloaded* status is not, so the mechanical path — now the only path — defers rather than merging without a push decision.
- Restart-safe accepted-revision suppression: a one-hour durable acceptance record prevents a restart from re-prompting work already queued to an agent while still allowing eventual retry and immediate delivery for a newer base revision. The per-worktree operation guard serializes work in-process, so this record is the only deduplication layer.
- Nonblocking polling over awaited refresh delivery: scheduled Git observations dispatch guarded auto-sync work in the background to preserve polling cadence, while the explicit toggle path awaits delivery so its request lifecycle stays deterministic. The await can now include a mechanical sync, so the toggle reports progress with a spinner rather than being backgrounded — the enabling click and its result stay connected.
- Wait for a free worktree over prompting a busy one: a session mid-turn — or one that stopped less
  than `settleWindow` ago — defers the observation entirely, and every sync then runs through the one
  mechanical path, which spends an agent only on what it could not finish. A prompt handed to a
  working agent enters a queue Treemon cannot observe, so it can sit unread for hours and be sent a
  second time when the acceptance record expires; waiting removes that failure mode rather than
  detecting it, and the sync lands sooner and without an agent turn. The settle window exists because
  status dips to idle for milliseconds between back-to-back turns: measured over 3,267 real
  inter-turn gaps, 87% are under half a second and only 0.7% fall between two seconds and 30 s, so
  the threshold sits in an empty valley and its exact value is not delicate.
- Retained/offline identity is still consulted only when nothing is open, and a settled-idle session
  receives any fallback prompt itself instead of triggering an unnecessary second session.
- Generic `SessionBridge` under canvas routing: session registration, liveness, queueing, and prompt forwarding are shared infrastructure; `CanvasBridge` retains only document ownership and canvas-specific message semantics.
- New session fallback: wait briefly for a selected session's bridge registration, then launch a new prompted session only when no live bridge exists; delivery failures to known live bridges stay queued for that session rather than creating parallel agents.
- net10.0 with Fable pinned to 5.0.0: Fable 4.x deadlocks when the compiled project targets net10.0, so the client needs Fable 5 (which in turn requires Feliz 3 — the Feliz 2 compiler plugin targets the Fable 4 AST). Later Fable 5 releases each break the client: 5.1.0 made F# reflection report `option` as a union, which `Fable.SimpleJson` classifies before its option case, so every `option` field in a remoting response fails to deserialize; 5.5.0 does the same for `list` and additionally rejects `Fable.Remoting.MsgPack`'s `inline private` helpers with a check stricter than `fsc`'s own. 5.0.0 predates all three. Revisit when `Fable.SimpleJson` and `Fable.Remoting` publish fixes.
- Windows Terminal per-window tracking via HWND: tabs aren't reliably addressable, one window per worktree is simple and predictable
- Upstream remote auto-detection over config-only: `upstream` remote name is the universal convention for fork workflows; config override available for non-standard setups
- Watched roots are server-owned and restart-to-apply (not live-updated): `tm add`/`remove` persist to the global config and take effect on the next server (re)start. The `treemon.ps1` shims trigger that restart when production is running outside an embedded terminal; an embedded invocation preserves the change but defers application until an external PowerShell restart. Chosen for simpler code — no per-root scheduler-state machinery; live application remains a clean future extension. The server is the single writer of `config.json` (with an internal write lock); the online-only CLI never writes config files, which removes the cross-process clobber hazard.
- `GlobalConfig` vs `TreemonConfig` — the machine-level `~/.treemon/config.json` and the repo-local `.treemon.json` (`autoSyncBranches`, `baseBranch`, `upstreamRemote`, `diffCategories`) are deliberately separate stores in separate modules, named so the machine-vs-repo scope is obvious and the two never collide.
- Create-worktree prompt auto-launch is **fire-and-forget, server-side, and reuses `launchAction`**: repo root, provider, and the new path are all in scope on the server, so it orchestrates the launch there rather than via a client follow-up. A failed spawn is logged, not surfaced (the worktree already exists), and it launches even after a post-fork warning. Provider is read **directly** from the new worktree's `.treemon.json` (it isn't in scheduler state yet, so `resolveProvider` would return `None` there), and the worktree path is single-quote-escaped in `SessionManager.buildScript` so a path containing `'` can't break the launch script.
- The create-prompt skill is **chosen per-create via a radio group** (offered skills come from the machine-level `worktreeSkills`; built-in **None** sends the prompt verbatim). The chosen skill rides the create request; the server wraps the prompt with `skillInvocation` for a named skill or launches it verbatim for None. The prompt (and skill) are single-quote-escaped at the CLI sink, so an odd skill value is a no-op for the tool, not an injection concern, making validation pure complication.
- Create-worktree and delete-confirm MVU updates defer forcing the lazy remoting proxy until their Elmish commands execute. Constructing a command therefore remains separate from evaluating the pure model transition, including under .NET unit tests.

## Related Specs

- `docs/spec/user-idle-detection.md` — adaptive refresh cadence based on user activity level
- `docs/spec/keyboard-navigation.md` — spatial arrow-key navigation and key bindings
- `docs/spec/native-session-management.md` — Windows Terminal spawn/focus/kill via HWND tracking, including the no-live-session auto-sync fallback
- `docs/spec/future/strong-typed-paths.md` — `AbsolutePath` wrapper type (deferred: entry-point normalization sufficient)
- `docs/spec/remoting-csrf-hardening.md` — Origin/Referer CSRF guard fronting the remoting and canvas POST surfaces (the create-worktree auto-launch made state-changing remoting an agent-execution sink)
- `docs/spec/canvas-pane.md` — interactive HTML docs and the canvas-specific consumer of the generic session bridge
- `docs/spec/session-status-push.md` — coding-tool session collapse and the related auto-sync target selection
