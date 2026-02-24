# Future Improvements

- Type worktree path keys — all `PerRepoState` maps (`GitData`, `BeadsData`, `CodingToolData`, `PrData`) use `Map<string, ...>` keyed by worktree path. Wrap in a `WorktreePath` single-case DU like `RepoId` for type safety.
