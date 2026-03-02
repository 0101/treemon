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
2. **Base branch select** — dropdown of remote branches to branch from, pre-selected by priority:
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
   - **If `fork.ps1` (Windows) or `fork.sh` (Unix) exists in repo root**: run it with the branch name as the first argument. The script is responsible for all setup (npm install, symlinks, etc). The server passes the base branch as a second argument.
   - **If no fork script exists**: fall back to bare `git worktree add -b {name} {parentDir}/tm-{name} origin/{baseBranch}`
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
- New API method: `getBranches: string -> Async<string list>` — takes repo ID string, returns remote branch names sorted by priority for the base branch selector

### Server (GitWorktree.fs + WorktreeApi.fs)

- `GitWorktree.listRemoteBranches`: run `git branch -r --format='%(refname:short)'` on repo root, strip `origin/` prefix, filter out the bare `origin` entry (HEAD symbolic ref), return sorted by priority
- `GitWorktree.createWorktree`:
  1. Detect OS via `System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform`
  2. Check if `fork.ps1` (Windows) or `fork.sh` (Unix) exists in repo root
  3. If fork script exists: run it (`powershell -File fork.ps1 {name} {baseBranch}` on Windows, `bash fork.sh {name} {baseBranch}` on Unix), capture stdout+stderr, return Ok/Error based on exit code
  4. If no fork script: run `git worktree add -b {name} {parentDir}/tm-{name} origin/{baseBranch}`
- `WorktreeApi`: wire up new endpoints, post `ExpediteRefresh` to scheduler after creation

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

- **Delegate to fork script if present**: repos have different setup needs (npm install, symlinks, bd init, etc). The server shouldn't hardcode any of that — it calls `fork.ps1`/`fork.sh` if it exists, otherwise does bare `git worktree add`.
- **OS detection for script extension**: `.ps1` on Windows, `.sh` on Unix. Uses `RuntimeInformation.IsOSPlatform`.
- **Branch list from remotes, not locals**: remote branches (`git branch -r`) cover the common case better; users branch off of origin/main, not local tracking branches
- **No validation beyond git**: if the branch name is invalid, git (or the fork script) will report the error which bubbles up to the modal
