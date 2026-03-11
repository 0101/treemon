# Worktree Path Identification

## Goals

- Eliminate cross-repo misidentification when multiple repos share a branch name
- Unify all API endpoints to use unambiguous worktree identifiers (`WorktreePath`)
- Fix client-side optimistic state updates that incorrectly affect all repos

## Expected Behavior

### API Consistency

All `IWorktreeApi` methods that target a specific worktree use `WorktreePath` as the identifier. Currently 5 methods already do this (`openTerminal`, `openEditor`, `focusSession`, `killSession`, `openNewTab`). The remaining 5 must be migrated:

| Method | Current Parameter | New Parameter |
|--------|------------------|---------------|
| `deleteWorktree` | `string` (branch) | `WorktreePath` |
| `startSync` | `string` (branch) | `WorktreePath` |
| `cancelSync` | `string` (branch) | `WorktreePath` |
| `archiveWorktree` | `BranchName` | `WorktreePath` |
| `unarchiveWorktree` | `BranchName` | `WorktreePath` |

### Delete Worktree

- User clicks delete or presses Delete key on a worktree card
- Confirm dialog shows the correct branch name and path regardless of duplicate branch names across repos
- Server receives `WorktreePath`, looks up worktree by path, deletes only the targeted worktree
- Client optimistically removes only the targeted worktree from its repo (by path, not branch)

### Archive/Unarchive Worktree

- Server receives `WorktreePath`, resolves which repo and branch, then updates that repo's archived branches set
- The `.treemon.json` persistence still stores branch names per repo (internal detail); the API boundary uses paths

### Start/Cancel Sync

- Server receives `WorktreePath`, finds the worktree by path, determines repo and branch for sync key construction
- No ambiguity when two repos have the same branch name

### Client Optimistic State

- `DeletedBranches: Set<string>` in client model becomes `DeletedPaths: Set<string>`
- Optimistic removal filters by path, affecting only the correct repo

## Technical Approach

### API Interface Changes (src/Shared/Types.fs)

Change the 5 affected method signatures to use `WorktreePath`. This is a breaking change but the API is internal (Fable.Remoting, no external consumers).

### Server Lookup Pattern (src/Server/WorktreeApi.fs)

Replace `allWorktrees` + `List.tryFind` by branch with path-based lookup. The path is sufficient to identify both the worktree and its parent repo (via `findRepoForPath`). For archive/unarchive, extract the branch name from the found worktree to update the per-repo archived branches set.

For `startSync`/`cancelSync`, the sync key (`scopedBranchKey`) is derived from the resolved repo and branch after path lookup.

### Client Message Changes (src/Client/App.fs, src/Client/ArchiveViews.fs)

- `ConfirmDeleteWorktree of WorktreePath`
- `DeleteWorktree of WorktreePath`
- `StartSync of path: WorktreePath * scopedKey: string`
- `CancelSync of WorktreePath`
- `Archive of WorktreePath` / `Unarchive of WorktreePath`

Dispatch sites already have access to `WorktreeStatus.Path` — pass `WorktreePath.create wt.Path` instead of `wt.Branch` or `BranchName.create wt.Branch`.

## Key Files

| File | Purpose |
|------|---------|
| `src/Shared/Types.fs` | `IWorktreeApi` interface, `WorktreePath` type |
| `src/Server/WorktreeApi.fs` | Server implementations of affected endpoints |
| `src/Client/App.fs` | Client messages, update handlers, dispatch sites |
| `src/Client/ArchiveViews.fs` | Archive message types, update, view |

## Decisions

- **WorktreePath over RepoId/Branch composite**: `WorktreePath` is already used by 5 API methods, is inherently unique, and requires no new types. A `RepoId * BranchName` tuple would work but adds complexity without benefit.
- **Breaking API change**: Acceptable because Fable.Remoting is internal. No versioning or backwards compatibility needed.
- **Server resolves branch from path**: Archive persistence uses branch names in `.treemon.json`, so the server extracts the branch after path lookup rather than requiring both path and branch from the client.
