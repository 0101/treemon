# Configurable Base Branch

## Goals

Allow each monitored repo to specify a "base branch" (e.g., `dev`, `develop`) instead of always assuming `main`. This affects ahead/behind counts, diff stats, fetch targets, fast-forward, merge/rebase targets, and branch sort priority.

Config-file level support only — no UI for editing this setting.

## Expected Behavior

- A repo's `.treemon.json` can include `"baseBranch": "dev"`.
- When omitted, defaults to `"main"` (current behavior preserved).
- All git operations that reference the base branch use the configured value:
  - `git fetch <remote> <baseBranch>`
  - `git rev-list --count HEAD..<remote>/<baseBranch>` (behind count)
  - `git rev-list --count --no-merges <remote>/<baseBranch>..HEAD` (commit count)
  - `git diff --shortstat <remote>/<baseBranch>...HEAD` (diff stats)
  - `git merge --ff-only <remote>/<baseBranch>` (fast-forward)
  - `git merge <remote>/<baseBranch>` / `git rebase <remote>/<baseBranch>` (sync pipeline)
- Fast-forward logic triggers when current branch matches the configured base branch (not hardcoded `"main"`).
- Branch sort priority puts the configured base branch first (priority 0).
- Deploy branch detection treats the configured base branch (and `master`) as non-deploy branches.

## Technical Approach

### Config Reading

Add `readBaseBranch` to `TreemonConfig.fs` using existing `readStringConfig` helper. Returns `string` (defaulting to `"main"`). Apply the same validation pattern as `readUpstreamRemote` (alphanumeric + `._/-`).

### State Propagation

Add `BaseBranch: string` to `PerRepoState` in `RefreshScheduler.fs` (default `"main"`). Read it during `RefreshWorktreeList` alongside `resolveUpstreamRemote`. Add `UpdateBaseBranch` message to `StateMsg`.

### GitWorktree Changes

- `mainRef`: add `baseBranch` parameter → `$"{upstreamRemote}/{baseBranch}"`
- `fetchUpstream`: add `baseBranch` parameter → `fetch {upstreamRemote} {baseBranch}`
- `tryFastForwardMain`: compare current branch against `baseBranch` instead of `"main"`
- `branchSortKey`: accept `baseBranch` parameter, give it priority 0

### Caller Updates

- `RefreshScheduler.fs`: pass `repo.BaseBranch` to `mainRef`, `fetchUpstream`
- `SyncEngine.fs`: accept and pass through `baseBranch`
- `WorktreeApi.fs`: pass `baseBranch` to `branchSortKey` and sync pipeline
- `Program.fs`: `readDeployBranch` — ideally reads config, but since it runs at startup before repo roots are known, keep matching `"main" | "master"` (acceptable simplification)

### Key Files

- `src/Server/TreemonConfig.fs` — add `readBaseBranch`
- `src/Server/RefreshScheduler.fs` — add to `PerRepoState`, state message, propagation
- `src/Server/GitWorktree.fs` — parameterize all base-branch-dependent functions
- `src/Server/SyncEngine.fs` — thread `baseBranch` through pipeline
- `src/Server/WorktreeApi.fs` — pass `baseBranch` to sort and sync calls
- `src/Tests/UpstreamRemoteTests.fs` — update `mainRef` tests for new signature

## Decisions

- **No rename of `MainBehindCount` / `IsMainWorktree` fields** — these are shared types used by the client. Renaming is a separate cosmetic change; the semantics (behind count relative to base branch) remain correct.
- **No rename of `mainRef` function** — keeping the name avoids a large mechanical diff. The function now takes `baseBranch` to clarify its meaning.
- **`Program.fs` deploy branch detection unchanged** — runs at server startup before any repo config is loaded; hardcoded `main/master` check is acceptable here.
