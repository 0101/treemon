# Multi-Repo + GitHub Support

## Goals

- Monitor multiple git worktree roots from a single server instance, displayed as distinct collapsible sections
- Auto-detect PR provider (Azure DevOps or GitHub) from git remote URL — zero configuration per repo
- Maintain single-process simplicity: one port, one poll, one scheduler across all repos

## Expected Behavior

### Multi-Repo

- Server accepts multiple root paths via CLI args or `.treemon.config`
- Each repo root is an independent section in the dashboard with its own header, branch count, and collapse toggle
- Cards never mix across repo sections; each section uses the existing responsive grid
- Scheduler generates tasks for all repos and picks most-overdue-first — natural load balancing
- Actions (`openTerminal`, `deleteWorktree`, `startSync`) already use full paths and work across repos without changes

### GitHub PRs

- GitHub remote URLs (`github.com/{owner}/{repo}`) are auto-detected alongside existing AzDo detection
- PR data fetched via `gh api`: open PRs plus closed PRs updated within last 7 days, matched by head branch name
- Comment count read directly from PR list response fields (`comments` + `review_comments`) — no extra API calls needed. Mapped to `CommentSummary.CountOnly` (sum of conversation + inline review comments)
- GitHub Actions workflow runs mapped to existing `BuildInfo` type: `conclusion` field maps to `BuildStatus`, failed step name extracted from run jobs
- Existing shared types (`PrStatus`, `PrInfo`, `BuildInfo`) are provider-agnostic — `ThreadCounts` replaced by `CommentSummary` DU to honestly model provider capabilities

### UI

- Collapsible repo sections: header with repo name, branch count, toggle arrow
- Collapsed sections show only the header row (click to expand)
- Collapse state is client-side only (per `RepoModel.IsCollapsed`)
- Scheduler footer aggregates events across all repos

## Technical Approach

### Breaking API Change

`WorktreeResponse` is replaced by `DashboardResponse` containing `RepoWorktrees list`. Client and server deploy together (same repo, same build).

### Types (`Types.fs`)

New `RepoWorktrees` record: `{ RepoId: string; RootFolderName: string; Worktrees: WorktreeStatus list; IsReady: bool }`. `DashboardResponse` holds `Repos: RepoWorktrees list` plus `SchedulerEvents`, `LatestByCategory`, and `AppVersion`. `IWorktreeApi.getWorktrees` returns `DashboardResponse`. Per-repo `IsReady` allows repos to appear progressively as their first scans complete.

Replace `ThreadCounts` with a `CommentSummary` DU that honestly models provider capabilities:

```fsharp
type CommentSummary =
    | WithResolution of unresolved: int * total: int   // AzDo: thread status tracking
    | CountOnly of total: int                          // GitHub: comment count, no resolution
```

`PrInfo.ThreadCounts: ThreadCounts` becomes `PrInfo.Comments: CommentSummary`. Client renders differently per case: `"2/7 threads"` for `WithResolution`, `"5 comments"` for `CountOnly`. The `dimmed` CSS class applies when `WithResolution(0, _)` (all resolved) or when `CountOnly(0)` (no comments).

### Scheduler (`RefreshScheduler.fs`)

State becomes `Map<string, PerRepoState>` keyed by repo ID (folder name). All `StateMsg` and `RefreshTask` variants gain a `repoId` parameter. `buildTaskList` generates tasks across all repos.

### Config (`Program.fs`, `treemon.ps1`)

`ServerConfig.WorktreeRoots: string list`. CLI accepts multiple positional args. `.treemon.config` becomes `{ "WorktreeRoots": [...] }`. Backward compat: single string arg still works.

### PR Provider Routing (`PrStatus.fs`)

`RemoteInfo` DU: `AzureDevOps of AzDoRemote | GitHub of owner * repo`. `detectProvider` tries AzDo first, then GitHub. `fetchPrStatuses` dispatches to the appropriate implementation.

### GitHub PR Fetching (new `GithubPrStatus.fs`)

- `parseGithubUrl`: extract `owner`/`repo` from HTTPS (`https://github.com/{owner}/{repo}[.git]`) and SSH (`git@github.com:{owner}/{repo}.git`) URLs
- `gh api` calls: `/repos/{owner}/{repo}/pulls?state=open` plus `/pulls?state=closed&sort=updated&direction=desc&per_page=10` for recent closed PRs (comment counts come from `comments` + `review_comments` fields on each PR object — no extra calls), `/repos/{owner}/{repo}/actions/runs?branch={branch}` for CI status
- Map GitHub `conclusion` to `BuildStatus`: `"success"` -> Succeeded, `"failure"` -> Failed, `"cancelled"` -> Canceled, null (in progress) -> Building
- Returns `Map<string, PrStatus>` keyed by branch name — same contract as AzDo provider

### Client (`App.fs`)

`Model` holds `Repos: RepoModel list` instead of flat worktree data. Each `RepoModel` includes `IsCollapsed`. View renders repo sections with headers and per-section card grids.

## Decisions

- **Single API call** returns all repos (not per-repo polling) — simpler client, one timer
- **Repo ID** is the folder name of the root path (e.g., "AITestAgent") — unique enough for display and keying
- **No per-repo provider config** — detected from git remote URL at PR fetch time
- **`gh` CLI** for GitHub (not raw REST) — consistent with existing `az` CLI pattern, handles auth
- **`BranchEvents`** (sync status) becomes repo-scoped since branch names could collide across repos

## Key Files

- `src/Shared/Types.fs` — `RepoWorktrees`, `DashboardResponse` (replaces `WorktreeResponse`)
- `src/Server/RefreshScheduler.fs` — `PerRepoState`, repo-keyed `DashboardState`, repo-scoped tasks
- `src/Server/Program.fs` — multi-root `ServerConfig`, CLI arg parsing
- `src/Server/WorktreeApi.fs` — assemble `DashboardResponse` from all repos
- `src/Server/PrStatus.fs` — `RemoteInfo` DU, `detectProvider`, provider routing
- `src/Server/GithubPrStatus.fs` — new module for GitHub PR/build/thread fetching
- `src/Client/App.fs` — `RepoModel`, collapsible sections, multi-repo rendering
- `treemon.ps1` — multi-root arg handling and config format

### Test Categories

Existing categories (`Unit`, `Fast`, `E2E`, `Smoke`) describe test *type* but not environment requirements. Add a `Local` category for tests that require local-only resources (running server with `az`/`bd`/`gh` CLIs, live worktree data, specific paths). CI runs all tests *except* `Local`: `dotnet test --filter "Category!=Local"`. This is additive — existing categories stay unchanged, `Local` is applied alongside them (e.g. a test can be both `Fast` and `Local`).

## Test Fixtures

Capture real API responses from both providers for offline unit testing of JSON parsing logic:

- **AzDo fixtures** (`src/Tests/fixtures/azdo/`): PR list, thread list, build runs — from `az` CLI output
- **GitHub fixtures** (`src/Tests/fixtures/github/`): PR list, review comments, Actions runs — from `gh api` output

Unit tests for JSON parsing use these fixtures directly — no network calls needed. Fixtures are captured once from real data and committed to the repo.
