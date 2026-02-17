# Investigation: Slow Initial Page Load

## Summary

Cold-start `getWorktrees` API call takes ~30 seconds. The overwhelming bottleneck (96% of time) is Azure DevOps CLI invocations for PR data. Git and beads operations are fast by comparison.

## Measured Timings (13 worktrees, 10 active PRs)

| Phase | Duration | % of Total |
|-------|----------|------------|
| `getCachedWorktrees` (git worktree list) | 93ms | 0.3% |
| `getCachedPrStatuses` (Azure CLI) | **28,469ms** | **96.4%** |
| `assembleAllWorktrees` (git + beads, parallel) | 956ms | 3.2% |
| **TOTAL** | **29,528ms** | |

### Azure CLI Breakdown

43 Python process invocations total, each taking 3-5 seconds due to Python startup overhead:

| Call Type | Count | Approximate Wall Time |
|-----------|-------|-----------------------|
| `az repos pr list` (sequential) | 1 | ~4.5s |
| PR thread counts (parallel per PR) | 10 | ~5s |
| Build statuses (parallel per PR) | 9 | ~5s |
| Build timelines for failed builds (parallel) | 11 | ~6s |
| Build logs for failed builds (parallel) | 11 | ~7s |

The chain is sequential: pr list -> (threads + builds) -> (timelines for failed) -> (logs for failed). Each layer must complete before the next starts.

### Per-Worktree Assembly (parallel, ~1s total)

| Operation | Time per worktree |
|-----------|------------------|
| Git data (3 sequential calls) | 333-504ms |
| Beads CLI | 400-536ms |
| Claude status (file mtime) | <1ms |

Git calls within `collectWorktreeGitData` are sequential (commit, upstream, mainBehind) at ~80-175ms each.

## Root Causes

1. **Azure CLI Python startup**: Each `az` invocation starts a Python interpreter (~2-3s overhead), making even simple API calls expensive.
2. **Sequential dependency chain**: PR list must complete before thread/build queries can start. Build queries must complete before failure detail queries can start.
3. **PR data blocks everything**: `getWorktrees` awaits PR data before starting worktree assembly. The client sees nothing for ~30s.

## Recommendations

### High Impact (addresses the 96% bottleneck)

**1. Decouple PR data from initial response** -- Return git/beads/claude data immediately, load PR data asynchronously. The client renders cards with "PR loading..." placeholder, then fills in PR info when available. This reduces cold start from ~30s to ~1s.

**2. Background cache warming on server startup** -- Fire off `getCachedPrStatuses` in a background async as soon as the server starts, before any client request arrives. The first client request would then hit warm caches. This eliminates cold start entirely for the common case (server starts, user opens browser a few seconds later).

**3. Replace Azure CLI with direct HTTP API calls** -- The AzDo REST API is the same one `az` uses under the hood. Using `HttpClient` directly eliminates ~2-3s Python startup per call. With a single `HttpClient` instance, all 43 calls could complete in ~5-10s total instead of ~28s. The auth token can be obtained once via `az account get-access-token`.

### Medium Impact

**4. Parallelize worktrees + PR fetch** -- Currently sequential (`let! worktrees = ...; let! prMap = ...`). These could run in parallel since PR data doesn't depend on the worktree list. Saves ~93ms (minor).

**5. Parallelize git calls within `collectWorktreeGitData`** -- The 3 git calls (commit, upstream, mainBehind) are independent and currently sequential. Running them in parallel would reduce per-worktree git time from ~400ms to ~175ms. Since worktrees already run in parallel, this mainly helps when there are few worktrees.

**6. Skip build failure details on cold start** -- Timeline and log fetches for failed builds add ~13s (timeline + log layers). These could be lazy-loaded when user expands/hovers a failed build badge, or fetched as a second pass after initial PR data renders.

### Low Impact (diminishing returns)

**7. Cache beads data across restarts** -- Beads CLI takes ~500ms per worktree but this is already parallelized across worktrees, so total contribution is <1s.

## Recommended Implementation Order

1. **Background cache warming** (simplest, no API changes, biggest immediate impact)
2. **Decouple PR from initial response** (requires API/client changes but eliminates the fundamental blocking)
3. **Replace az CLI with HttpClient** (biggest long-term improvement, more effort)

## Related

- ServerFixture uses a 30s timeout for server startup, separate from API response time
- PR cache TTL is 120s, so the ~30s penalty only hits every 2 minutes after initial load
- Subsequent requests within cache TTL return in <10ms
