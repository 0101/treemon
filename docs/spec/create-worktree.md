# Create Worktree

## Goals

- Let users create new worktrees from the dashboard without leaving the browser or typing git commands
- Pre-select the best base branch so the common case (branching from main) requires zero clicks
- Show progress and errors inline — no silent failures, no context switches
- Respect repo-specific setup by delegating to `fork.ps1`/`fork.sh` if present

## Expected Behavior

### Opening the Modal

A "+" button appears on each repo header (right side, next to repo name). Clicking it opens a modal dialog. The name input is auto-focused so the user can start typing immediately.

### Modal Contents

1. **Name input** (text field, focused on open) — the new worktree/branch name
2. **Source branch select** — dropdown of branches currently visible on the dashboard for this repo, sorted by priority:
   - `main`
   - `master`
   - `develop`
   - Anything starting with `dev` (alphabetical among matches)
   - All remaining branches (alphabetical)
3. **Submit** — Enter key or submit button creates the worktree

### Creation Flow

On submit:
1. Modal replaces its content with "Creating worktree" and animated dots (`. .. ...` cycling)
2. Server creates the worktree using this strategy:
   - Resolves the selected source branch to its worktree path from scheduler state
   - **If `fork.ps1` (Windows) or `fork.sh` (Unix) exists in repo root**: run it from the source worktree directory with the branch name as the sole argument. The script is responsible for all setup (npm install, symlinks, etc).
   - **If no fork script exists**: fall back to `git -C {sourceWorktreePath} worktree add -b {name} {parentDir}/tm-{name}` — this branches from HEAD of the source worktree
3. Server expedites the worktree list refresh for that repo so the new card appears quickly
4. On success: modal closes automatically
5. On error: modal shows error message with a close button

### Worktree Naming

When using the fallback (no fork script), the worktree directory is placed as a sibling of the repo root, named `tm-{name}`. When a fork script exists, naming is the script's responsibility (the server doesn't need to know the path — the scheduler will discover it on the next worktree list refresh).

## Technical Approach

### Shared Types (Types.fs)

New types for the API:
- `CreateWorktreeRequest = { RepoId: string; BranchName: string; BaseBranch: string }`
- New API method: `createWorktree: CreateWorktreeRequest -> Async<Result<unit, string>>`
- New API method: `getBranches: string -> Async<string list>` — takes repo ID string, returns branch names of worktrees currently on the dashboard, sorted by priority for the source branch selector

### Server (GitWorktree.fs + WorktreeApi.fs)

- `WorktreeApi.getBranches`: reads worktree branches from scheduler state (no git call needed), sorts by priority using `branchSortKey`
- `GitWorktree.createWorktree`:
  1. Detect OS via `System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform`
  2. Check if `fork.ps1` (Windows) or `fork.sh` (Unix) exists in repo root
  3. If fork script exists: run it (`powershell -File fork.ps1 {name}` on Windows, `bash fork.sh {name}` on Unix) from the source worktree directory, capture stdout+stderr, return Ok/Error based on exit code
  4. If no fork script: run `git -C {sourceWorktreePath} worktree add -b {name} {parentDir}/tm-{name}`
- `WorktreeApi.createWorktree`: resolves `BaseBranch` to a worktree path from scheduler state, then calls `GitWorktree.createWorktree`; posts `ExpediteRefresh` to scheduler after creation

### Scheduler (RefreshScheduler.fs) — done as part of API wiring

- Add `ExpeditedRepos: Set<RepoId>` field to `DashboardState`
- Add `ExpediteRefresh of RepoId` case to `StateMsg` DU; handler adds the repo ID to `ExpeditedRepos`
- In the main loop: after building the task list, check if any `RefreshWorktreeList` task's repo is in `ExpeditedRepos`. If so, treat it as immediately due (override `lastRuns` for that task). After executing, clear the repo from `ExpeditedRepos` via a new `ClearExpedite of RepoId` message.
- Post `ExpediteRefresh` from `createWorktree` API handler after successful creation

### Client (App.fs)

Elmish MVU additions:

**Model:**
```
CreateWorktreeModal =
    | Closed
    | LoadingBranches of RepoId
    | Open of { RepoId; Branches: string list; Name: string; BaseBranch: string }
    | Creating of RepoId
    | Error of { RepoId; Message: string }
```

**Messages:**
- `OpenCreateWorktree of RepoId` — fetch branches, open modal
- `BranchesLoaded of Result<string list, exn>` — populate dropdown
- `SetNewWorktreeName of string`
- `SetBaseBranch of string`
- `SubmitCreateWorktree`
- `CreateWorktreeCompleted of Result<unit, string>`
- `CloseCreateModal`

**View:**
- "+" button in `repoSectionHeader` (right-aligned, stops propagation so it doesn't toggle collapse)
- Modal overlay: semi-transparent backdrop + centered dialog
- Three modal states render differently: form, "Creating..." animation, error display

### Verification

- **Elmish unit tests**: The update function is pure — test the full state machine by dispatching messages and asserting on resulting model states. Covers: modal open/close transitions, branch priority sorting, form state updates, success/error handling.
- **Playwright E2E tests**: Assert on DOM structure (plus button, modal, input focus, dropdown) AND exercise the full roundtrip (submit form → creating state → server response → modal closes or shows error). Use a known-invalid branch name to test the error path end-to-end without creating real worktrees.

## Decisions

- **Delegate to fork script if present**: repos have different setup needs (npm install, symlinks, bd init, etc). The server shouldn't hardcode any of that — it calls `fork.ps1`/`fork.sh` if it exists, otherwise does bare `git worktree add`. The fork script receives only the branch name; no base branch argument since it runs from the source worktree directory.
- **OS detection for script extension**: `.ps1` on Windows, `.sh` on Unix. Uses `RuntimeInformation.IsOSPlatform`.
- **Branch list from dashboard worktrees, not remotes**: the dropdown only shows branches that already have worktrees on the dashboard. This avoids showing branches with no local worktree and keeps the UI consistent with what the user sees. The new branch forks from HEAD of the selected worktree.
- **No validation beyond git**: if the branch name is invalid, git (or the fork script) will report the error which bubbles up to the modal
