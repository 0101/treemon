# Merged-PR Persistence (sticky merged-PR association)

## Goals

- Keep a worktree's **Merged** badge after its PR ages out of the bounded closed-PR fetch window,
  and across server restarts — so the dashboard never "forgets" a merged PR.
- Persist the branch → merged-PR association as **durable runtime state**, separate from
  user-authored `config.json`.
- **Fallback-only**: live GitHub PR data always wins; the store only fills gaps the live fetch
  no longer returns.
- **Bounded**: forget records for branches no longer tracked by any live worktree.

## Expected Behavior

### Recording merged PRs

On each per-repo PR refresh, every branch whose live status is `HasPr { IsMerged = true }` is
recorded in a persistent store keyed `repo → branch → { Id; Title; Url }`. The store is loaded at
server startup and persisted whenever it changes.

### Fallback overlay

When building the effective PR map for a repo, for any branch in the repo's current
`knownBranches` that the live fetch did **not** return as `HasPr`, an existing persisted record is
injected as:

```
HasPr { Id; Title; Url; IsMerged = true; IsDraft = false;
        Comments = WithResolution(0,0); Builds = []; HasConflicts = false }
```

Live `HasPr` entries are **never** overridden — the overlay only supplies branches the live map is
missing. The reconstructed `PrInfo` renders identically to a live merged PR (the merged badge uses
only `IsMerged`, `Title`, `Url`).

### Pruning

On each refresh, records for branches **not** in the repo's current `knownBranches` are dropped,
keeping the store bounded by live worktrees. A deleted worktree ⇒ its branch is no longer known ⇒
its record is forgotten.

### Persistence location

Stored as `data/merged-prs.json` (gitignored server runtime state), **not** `config.json` —
matching `data/canvas-owners.json` (`CanvasDocOwnership.fs`) and `data/sessions.json`
(`SessionManager.fs`). An absent or corrupt file loads as an empty store, i.e. today's behavior.

### What doesn't change

- The bounded GitHub fetch (`per_page` on the closed-PR query) is unchanged; the store is a memory
  layer on top of it.
- Shared types, the wire protocol, and the entire client are unchanged.
- Only the terminal **merged** fact is persisted. Volatile fields (builds, comments, conflicts,
  draft) are never persisted; open/active PRs are not stored at all.
- `PrStatus.lookupPrStatus` and `WorktreeApi` are untouched — `PerRepoState.PrData` simply becomes
  the effective (live + fallback) map.

## Technical Approach

- **`src/Server/MergedPrStore.fs`** — a `MailboxProcessor` serializing an immutable
  `Map<RepoId, Map<string, MergedPrRecord>>` (repo → branch → record), persisted to
  `data/merged-prs.json` on every change (atomic temp-file + `File.Move`) and loaded at startup.
  Mirrors `CanvasDocOwnership.fs`. Defines `MergedPrRecord = { Id: int; Title: string; Url: string }`.
  Exposes async reads (`getForRepo`) and a change-persisting write (`setForRepo`), plus `load()`.
- **Pure `reconcileMergedPrs`** (no I/O, unit-testable):
  `(livePrMap: Map<string, PrStatus>) -> (persisted: Map<string, MergedPrRecord>) ->
  (knownBranches: Set<string>) -> (effectiveMap: Map<string, PrStatus>) * (newPersisted: Map<string, MergedPrRecord>)`.
  Upserts branches observed as merged, prunes `persisted` to `knownBranches`, and overlays a
  reconstructed `HasPr` for each known branch missing from the live map. Returns the new persisted
  map so the caller can persist only when it changed.
- **Wiring in `src/Server/RefreshScheduler.fs`** (`RefreshPr` handler): after
  `PrStatus.fetchPrStatusesByRepoRoot`, read the repo's persisted records, run `reconcileMergedPrs`,
  `setForRepo` only if changed, then `UpdatePr(repoId, effectiveMap)`.
- **Startup** — `Program.fs` calls `MergedPrStore.load()` alongside `CanvasDocOwnership.load()`.

## Decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Persistence home | `data/merged-prs.json` runtime state — **not** `config.json` (which is user config). Matches `data/canvas-owners.json` / `data/sessions.json`. |
| 2 | Store shape | `Map<RepoId, Map<branch, {Id;Title;Url}>>` — minimal record; only fields the merged badge renders. |
| 3 | Overlay precedence | Fallback-only: live `HasPr` always wins; store fills only missing known branches. |
| 4 | Growth control | Prune to the repo's current `knownBranches` each refresh. |
| 5 | Pure/effect split | Pure `reconcileMergedPrs` (transform) separated from the effectful store (I/O) for testability. |
| 6 | Write frequency | Persist only when the reconciled store differs from the loaded one. |
| 7 | Client/protocol | Unchanged — reconstruct a full `PrInfo` server-side; badge renders from `IsMerged`/`Title`/`Url`. |

## Key Files

| File | Purpose |
|------|---------|
| `src/Server/MergedPrStore.fs` | New. Runtime-state store (`data/merged-prs.json`) + pure `reconcileMergedPrs`; mirrors `CanvasDocOwnership.fs`. |
| `src/Server/RefreshScheduler.fs` | `RefreshPr` handler reconciles live PRs with the store, persists on change, posts the effective map via `UpdatePr`. |
| `src/Server/Program.fs` | Calls `MergedPrStore.load()` at startup. |
| `src/Server/Server.fsproj` | Compile-order entry for `MergedPrStore.fs`. |
| `src/Server/CanvasDocOwnership.fs` | Template for the store (agent + atomic write + load). |
| `src/Server/GithubPrStatus.fs` | Bounded closed-PR fetch that causes merged PRs to age out (`per_page=30`). |
| `src/Server/PrStatus.fs` | `lookupPrStatus` — unchanged; consumes the effective `PrData`. |
| `src/Client/CardViews.fs` | Merged badge — unchanged; renders from `IsMerged`/`Title`/`Url`. |

## Related Specs

- `docs/spec/canvas-doc-ownership.md` — `data/*.json` runtime-state persistence pattern reused here.
- `docs/spec/worktree-monitor.md` — PR-status pipeline and refresh scheduler this extends.
