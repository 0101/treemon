# Dashboard Fixes Round 2

## Goals

- Fix sync pipeline bugs: dirty-check false positives from untracked files, pull failure on branches without upstream tracking
- Fix event log visibility: Claude messages should appear on cards from initial load, not only after triggering sync
- Fix sync completion UI: show sync button (not cancel) after sync ends
- Console/event log should have terminal-like visual styling (black background, monospace text)
- Remove left border from cards (currently shows Claude status color as border-left)
- Add delete worktree button with confirmation dialog (remove worktree folder + delete local branch)
- Investigate and address slow initial page load on refresh

## Expected Behavior

### Bug: CheckClean rejects untracked files

Currently `git status --porcelain` reports both tracked modifications and untracked files. Untracked files should not block sync — only tracked file modifications should.

- Change CheckClean to use `git status --porcelain --untracked-files=no`
- Untracked files no longer prevent sync from starting
- Modified/staged tracked files still block sync with "Working tree is dirty"

### Bug: Pull fails on branches without upstream

Currently step 2 runs `git pull --ff-only` which fails on branches with no remote tracking. Since the real goal is to merge origin/main, pull of the branch's own upstream is unnecessary.

- Replace `git pull --ff-only` with `git fetch origin`
- This ensures origin/main (and all refs) are up-to-date before the merge step
- Works regardless of whether the branch tracks a remote
- The merge step (`git merge origin/main`) remains unchanged

### Bug: Event log only appears after sync trigger

`getSyncStatus` (which merges Claude messages with sync events) is only called via `SyncTick` polling, which only activates when a sync is running. On normal 15s polling, only `getWorktrees` is called — no events are fetched.

- Fetch `getSyncStatus` on every `Tick` (15s poll), not just during sync
- Claude's last assistant message should appear on cards from the first load
- Sync events should persist and remain visible after sync completes

### Bug: Cancel button persists after sync failure

When sync fails (e.g., "Working tree is dirty"), the cancel button stays visible because the sync state transitions to `Completed(Failed)` but the UI only checks for `Running` status in events. Since no new `Running` event exists, `isBranchSyncing` returns false — but the events may still contain stale `Running` entries from the last step.

- After sync completes/fails, ensure no events have `Running` status
- The sync button should reappear immediately after completion or failure

### Console styling

The event log should look like a terminal output area.

- Black background (`#11111b` or similar dark), monospace font
- Light/muted text colors for source/message
- Status badges keep their existing colored styling
- Small padding, rounded corners to match card aesthetic

### Remove left border on cards

- Remove `border-left: 4px solid` from `.wt-card` and all `.cc-*` border-left variants
- Claude status is already shown via the dot indicator in the card header

### Delete worktree button

- Add a delete/bin icon button to each card header (next to terminal button)
- Clicking shows a confirmation dialog (browser `confirm()` is sufficient)
- Confirmation text: "Remove worktree [branch-name]? This will delete the worktree folder and local branch."
- On confirm: call new API endpoint that runs `git worktree remove --force <path>` then `git branch -D <branch>` from the repo root
- On success: refresh worktree list
- Local-only cleanup — does not touch remote branches
- The delete commands must run from the main worktree (repo root), not from within the worktree being deleted

### Slow initial page load

- Investigate whether the delay is server-side (cold cache, CLI calls) or client-side (Fable/Vite)
- The 45s test timeout in ServerFixture suggests this is a known slow path
- Consider: preloading/warming caches on server startup, showing partial data faster, or loading cards progressively

## Technical Approach

### CheckClean fix
- `SyncEngine.fs` line 242: change `"status --porcelain"` to `"status --porcelain --untracked-files=no"`

### Pull → Fetch
- `SyncEngine.fs` line 256: change `"pull --ff-only"` to `"fetch origin"`
- Rename the step display if desired, though `SyncStep.Pull` → could stay as `Pull` or rename DU case

### Event log on every tick
- `App.fs` update handler for `Tick`: also dispatch `fetchSyncStatus()` alongside `fetchWorktrees()`
- This ensures Claude messages and sync events are fetched on every 15s poll

### Cancel button fix
- Review `isBranchSyncing` — it checks for any event with `Running` status
- When sync completes, the server should ensure no `Running`-status events remain in the event list
- Or: client-side, also check the `SyncState` (Completed/Cancelled) to override the button

### Console styling
- `index.html`: update `.event-log` CSS — add `background: #11111b`, `font-family: monospace`, `padding: 8px`, `border-radius: 6px`
- Adjust `.event-source`, `.event-message` colors for contrast against dark background

### Remove left border
- `index.html`: remove `border-left: 4px solid #585b70` from `.wt-card`
- Remove `.wt-card.cc-active`, `.cc-recent`, `.cc-idle`, `.cc-unknown` border-left-color rules

### Delete worktree
- `Types.fs`: add `deleteWorktree: string -> Async<Result<unit, string>>` to `IWorktreeApi`
- `WorktreeApi.fs`: implement — validate path is known worktree, run `git worktree remove --force <path>` then `git branch -D <branch>` from repo root
- `Program.fs`: wire up the new API method
- `App.fs`: add `DeleteWorktree of string` and `DeleteCompleted of Result<unit, string>` messages, render bin button, handle confirm dialog

### Page load investigation
- Profile server startup: which CLI calls are slowest on cold start?
- Consider: fire initial `getWorktrees` response with partial data (git-only) while beads/claude/PR load async
- This is investigative — may result in a separate spec if significant changes needed

## Key Files

- `src/Server/SyncEngine.fs` — CheckClean fix (line 242), Pull→Fetch fix (line 256)
- `src/Client/App.fs` — event polling on Tick, cancel button fix, delete button, confirm dialog
- `src/Client/index.html` — console styling, remove left border
- `src/Shared/Types.fs` — add deleteWorktree to IWorktreeApi
- `src/Server/WorktreeApi.fs` — implement deleteWorktree, wire in API
- `src/Server/Program.fs` — ensure new API method is routed

## Decisions

### Slow initial load investigation (mait-jwl)

Profiled cold-start `getWorktrees` call: 30s total. 96% of time (28.5s) is spent in Azure CLI Python invocations for PR data (43 process spawns at ~3-5s each due to Python startup overhead). Git + beads + claude together take <1s. Full analysis in `docs/investigations/slow-initial-load.md`.

Recommended next steps (in priority order):
1. Background cache warming on server startup (simplest, no API changes)
2. Decouple PR data from initial response (return git/beads/claude immediately, load PR async)
3. Replace `az` CLI with direct HTTP API calls (eliminates Python startup overhead entirely)

