# Parallel Startup Burst

## Goals

- Fully populate the dashboard within ~5-10 seconds of startup instead of 30-60
- Run all initial refresh tasks in parallel (respecting dependencies), then resume normal sequential scheduling

## Expected Behavior

On startup, before the normal scheduler loop begins, execute a one-time parallel burst in three phases:

1. **Phase 1 — Discover worktrees:** `RefreshWorktreeList` for all repos in parallel
2. **Phase 2 — Local data + fetch:** `RefreshGit`, `RefreshBeads`, `RefreshClaude`, `RefreshFetch` for all repos/worktrees in parallel
3. **Phase 3 — PR data:** `RefreshPr` for all repos in parallel (needs branch names from Phase 2)

After the burst completes, the `lastRuns` map is pre-populated with current timestamps and the normal sequential `loop` takes over unchanged.

Steady-state behavior (intervals, sequential execution, timeouts) is completely unchanged.

## Technical Approach

Add a `runInitialBurst` function in `RefreshScheduler.fs` that uses `Async.Parallel` to run tasks within each phase. Modify `start` to call it before entering the main loop, passing the resulting `lastRuns` map.

### Key Files
- `src/Server/RefreshScheduler.fs` — only file that changes

### Constraints
- Reuse existing `executeWithTimeout` and `logTaskResult` — no new execution infrastructure
- `MailboxProcessor.Post` is fire-and-forget (enqueue only) — safe for concurrent callers
- `PostAndAsyncReply(GetState)` between phases ensures state is consistent before the next phase reads it

### Testability
- Extract pure functions for building the task list per phase (given repo state, return task list) — these are unit-testable
- `runInitialBurst` itself is side-effectful (calls executeWithTimeout), but the phase-building logic should be pure
- Existing `buildTaskList` is already public and tested; new phase functions follow the same pattern

### Decisions
- No parallelism cap — startup burst is brief and bounded by repo/worktree count
- Three phases (not two or one) because `RefreshPr` depends on git data for branch name filtering
