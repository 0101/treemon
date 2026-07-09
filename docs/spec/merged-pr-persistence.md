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

The overlay is additionally gated by **commit identity** (Decision #11): a persisted record is
surfaced only when its `HeadSha` matches the branch's current worktree tip (or the record is a
legacy one with an empty `HeadSha`). A record whose branch has a **present but differing** tip is a
reused-name incarnation and is **evicted** from the store, so a recreated branch never resurrects a
prior branch's merged badge.

### Pruning

On each refresh, records for branches **not** in the repo's current `knownBranches` are dropped,
keeping the store bounded by live worktrees. A deleted worktree ⇒ its branch is no longer known ⇒
its record is forgotten.

Pruning runs **only against a trustworthy enumeration**. `knownBranches` is derived from live git
data, which is empty or partial whenever worktree-git collection is unready, timed out, or was
dropped by a transient short worktree list. Pruning against such a set would delete just-loaded
merged facts, permanently forgetting merged PRs that have aged out of the bounded live fetch
(review F7). So the store is pruned only once **every** known worktree has collected git data, at least one
worktree exists, at least one branch resolved, **and no known worktree's upstream read transiently
failed**; otherwise pruning is skipped and the store is left untouched. Upserts (recording newly
observed live merges) and the fallback overlay always run — they are additive and can never lose
data. (Decisions #8 and #10.)

### Persistence location

Stored as `data/merged-prs.json` (gitignored server runtime state), **not** `config.json` —
matching `data/canvas-owners.json` (`CanvasDocOwnership.fs`) and `data/sessions.json`
(`SessionManager.fs`). An absent or corrupt file loads as an empty store, i.e. today's behavior.

### What doesn't change

- The closed-PR fetch stays bounded (the store is a memory layer on top of it); this PR increases its
  `per_page` from 10 to 30, widening the live window before merged PRs age out.
- Shared types, the wire protocol, and the entire client are unchanged.
- Only the terminal **merged** fact is persisted. Volatile fields (builds, comments, conflicts,
  draft) are never persisted; open/active PRs are not stored at all.
- `PrStatus.lookupPrStatus`, `WorktreeApi`, and `SyncEngine` code is untouched — `PerRepoState.PrData`
  simply becomes the effective (live + fallback) map. One behavioral consequence is intended and
  documented rather than a code change: `PrData` has two consumers — the merged badge **and** the sync
  pipeline's push step (`WorktreeApi.fs:391` `lookupPrStatus` → `SyncEngine.executeSyncPipeline`, whose
  `HasPr _ -> push` runs `git push`). The overlay makes an aged-out merged branch resolve to
  `HasPr { IsMerged = true }` instead of `NoPr`, so `sync` can now `git push` it. This is pre-existing,
  low-harm behavior (touches only the branch's own remote ref, never `main`) — see Decision #9 (review F8).

## Technical Approach

- **`src/Server/MergedPrStore.fs`** — a `MailboxProcessor` serializing an immutable
  `Map<RepoId, Map<string, MergedPrRecord>>` (repo → branch → record), persisted to
  `data/merged-prs.json` on every change (atomic temp-file + `File.Move`) and loaded at startup.
  Mirrors `CanvasDocOwnership.fs`. Defines
  `MergedPrRecord = { Id: int; Title: string; Url: string; HeadSha: string }` (`HeadSha` = the
  worktree tip identity at record time, Decision #11).
  Exposes async reads (`getForRepo`) and a change-persisting write (`setForRepo`), plus `load()`.
- **Pure `reconcileMergedPrs`** (no I/O, unit-testable):
  `(livePrMap: Map<string, PrStatus>) -> (persisted: Map<string, MergedPrRecord>) ->
  (worktreeHeads: Map<string, string>) -> (knownBranches: Set<string> option) ->
  (effectiveMap: Map<string, PrStatus>) * (newPersisted: Map<string, MergedPrRecord>)`.
  Upserts branches observed as merged (stamping `HeadSha` from `worktreeHeads`) and overlays a
  reconstructed `HasPr` for each persisted branch missing from the live map. Before the name-prune it
  **identity-gates** by tip: a record is evicted when its non-empty `HeadSha` differs from the
  branch's present worktree tip (a reused-name incarnation); a match, a missing tip, or an empty
  (legacy) `HeadSha` is kept (Decision #11). Pruning to `knownBranches` runs only when it is `Some`
  (a trustworthy enumeration); `None` skips pruning so an empty/partial set can never wipe the store
  (Decision #8). Returns the new persisted map so the caller can persist only when it changed.
- **Pure `pruneScope`** (`knownPaths -> collectedGitPaths -> readFailedPaths -> knownBranches -> Set<string> option`):
  returns `Some knownBranches` only when every known worktree path has a collected `GitData` entry,
  at least one worktree and one branch exist, and no known worktree's upstream read failed
  (`readFailedPaths ∩ knownPaths = ∅`); else `None`. Isolates the "is the enumeration complete and
  trustworthy?" decision from `RefreshPr` so it is unit-testable.
- **Read-failure surfacing in `src/Server/GitWorktree.fs`** (Decision #10): `getUpstreamBranch`
  returns `UpstreamResult` (`Upstream name | NoUpstream | UpstreamReadFailed`) via a pure
  `classifyUpstream`; `GitData` gains an `Upstream: UpstreamResult` field. This lets `RefreshPr` feed
  the read-failed paths into `pruneScope` so a transiently-unreadable upstream is excluded from the
  prune enumeration rather than mistaken for "no branch".
- **Wiring in `src/Server/RefreshScheduler.fs`** (`RefreshPr` handler): after
  `PrStatus.fetchPrStatusesByRepoRoot`, read the repo's persisted records, compute
  `pruneScope repo.KnownPaths (keys repo.GitData) (read-failed paths from repo.GitData) knownBranches`,
  build `worktreeHeads` (upstream branch name → `GitData.HeadCommit`) for identity gating (Decision
  #11), run `reconcileMergedPrs`, `setForRepo` only if changed, then `UpdatePr(repoId, effectiveMap)`.
- **Startup** — `Program.fs` calls `MergedPrStore.load()` alongside `CanvasDocOwnership.load()`.

## Decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Persistence home | `data/merged-prs.json` runtime state — **not** `config.json` (which is user config). Matches `data/canvas-owners.json` / `data/sessions.json`. |
| 2 | Store shape | `Map<RepoId, Map<branch, {Id;Title;Url;HeadSha}>>` — minimal record: the fields the merged badge renders plus `HeadSha`, the merged commit identity (Decision #11). |
| 3 | Overlay precedence | Fallback-only: live `HasPr` always wins; store fills only missing known branches — and only when the record's `HeadSha` matches the worktree's current tip (Decision #11). |
| 4 | Growth control | Prune to the repo's current `knownBranches` each refresh; additionally evict a record whose branch's present worktree tip proves a different incarnation (Decision #11). |
| 5 | Pure/effect split | Pure `reconcileMergedPrs` (transform) separated from the effectful store (I/O) for testability. |
| 6 | Write frequency | Persist only when the reconciled store differs from the loaded one. |
| 7 | Client/protocol | Unchanged — reconstruct a full `PrInfo` server-side; badge renders from `IsMerged`/`Title`/`Url`. |
| 8 | Prune safety (review F7) | Prune only against a **complete and non-empty** live-worktree enumeration: every known worktree has collected git data, ≥1 worktree exists, **and** ≥1 branch resolved (`pruneScope` → `Some`). Empty/partial git-data (unready, a `collectWorktreeGitData` timeout, or a transient short worktree list) → `None`; an enumeration that collapses to ∅ (a correlated `git rev-parse @{u}` failure degrading every upstream branch name to `None` while paths stay collected) is also → `None`, closing the full-store-wipe class. Upserts/overlay always run (additive, lossless). **Residual (partial) — now closed by Decision #10:** previously, when only *some* upstream reads transiently failed, those branches' records were still pruned, because `GitData` could not distinguish "read failed" from "no upstream". Decision #10 surfaces that distinction and skips the prune when any known worktree's upstream read failed. |
| 9 | Sync push of merged branches (review F8) | The effective `PrData` feeds **two** consumers — the merged badge **and** the sync pipeline's final push step (`WorktreeApi.fs:391` `lookupPrStatus` → `SyncEngine.executeSyncPipeline`, whose `HasPr _ -> push` runs `git push`). The fallback overlay makes an aged-out merged branch resolve to `HasPr { IsMerged = true }` (was `NoPr`), so `sync` can now `git push` it. **Accepted as-is, document-only (no code change):** the push-on-merged behavior is pre-existing — an in-window merged branch already resolved to live `HasPr { IsMerged = true }` and sync already pushed it; the overlay only makes this consistent for aged-out branches instead of window-dependent. It is reachable only in the `commitCount <> 0` arm (squash-merge / post-merge-commit branches) and touches only the branch's own remote ref — never `main`. If pushing merged branches during sync is ever deemed unwanted, guard it once for both cases in `SyncEngine.fs`: `match prStatus with HasPr pr when not pr.IsMerged -> push \| _ -> ()`. |
| 10 | Read-failure vs no-upstream (closes Decision #8 residual) | `GitWorktree.getUpstreamBranch` returns `UpstreamResult` (`Upstream name \| NoUpstream \| UpstreamReadFailed`) via a **pure** `classifyUpstream` of the `git rev-parse --abbrev-ref @{u}` result. Three empirically-confirmed deterministic fatals → `NoUpstream`: "no upstream configured for branch" (never pushed), detached-HEAD "does not point to a branch", and unborn-branch "no such branch" (no commits yet) — all stable states that carry no merged-PR record to lose, so they are safe to prune against. **Everything else → `UpstreamReadFailed`** (skip prune): a timeout / lock / unrecognized error, an anomalous empty success, and — importantly — `ambiguous argument '@{u}': unknown revision`, which git emits when an upstream WAS configured but its tracking ref is now unresolvable (e.g. a merged-then-deleted remote branch after `fetch --prune`). `GitData` carries the upstream state as an `Upstream: UpstreamResult` field (whose `UpstreamReadFailed` case marks a transient read); `RefreshPr` collects the read-failed known-worktree paths and passes them to `pruneScope`, which returns `None` (skip prune) if any known worktree's upstream read failed. A transiently- or persistently-unreadable upstream can thus no longer be mistaken for "no branch" and prune a still-valid aged-out merged PR. **Why default to `UpstreamReadFailed`:** the store keys on the upstream branch name, so a branch whose upstream is unreadable drops out of the enumeration even though its worktree still exists and its merged badge should persist; erring toward skip-prune upholds "never forget a merged PR" (the accepted cost is weaker bounded growth — Decision #4 — while an upstream is unreadable, which only lets tiny `{id,title,url}` records linger harmlessly). Conversely the common unpushed-branch case ("no upstream configured") **must** stay `NoUpstream` so pruning is not permanently disabled. |
| 11 | Branch identity for merged records (review F4) | Records key on branch **name**, which git lets you delete and recreate with unrelated history — so a reused name would resurrect a prior branch's merged badge (a false "Merged" on new work, and `sync` could `git push` it via Decision #9's gate). Each `MergedPrRecord` now carries **`HeadSha`**: the worktree tip commit at the moment the merged PR was recorded, sourced from **`GitData.HeadCommit`** (the `getLastCommit` hash we already compute but previously discarded). **Identity source is the worktree tip** — pure, no GitHub/wire-type change; the live PR map is already scoped to existing worktrees (`filterRelevantPrs knownBranches`), so a tip is always available when a record is written. **Overlay is gated by exact tip match:** the reconstructed merged `HasPr` is surfaced only when the worktree's current tip == the record's `HeadSha`. A record whose branch has a **present** worktree tip that **differs** is a confirmed different incarnation and is **evicted** from the store (self-cleaning). **Legacy fallback:** a record with an empty `HeadSha` (pre-existing on-disk data, or a tolerant-load default) is treated as unverified and keeps the prior name-only overlay until it is re-observed live and re-stamped — so no existing badge vanishes on deploy. `reconcileMergedPrs` stays **pure** (Decision #5): `RefreshPr` builds `worktreeHeads: Map<branch, tipSha>` from `repo.GitData` and passes it in; the upsert stamps `HeadSha` from that map. **Exact match (not ancestry)** means a branch advanced past its own merge drops the badge — conservative, arguably correct (there is now unmerged work), and never worse than the pre-persistence `NoPr`. |

## Key Files

| File | Purpose |
|------|---------|
| `src/Server/MergedPrStore.fs` | New. Runtime-state store (`data/merged-prs.json`) + pure `reconcileMergedPrs` (identity-gated by worktree tip SHA, Decision #11); mirrors `CanvasDocOwnership.fs`. |
| `src/Server/RefreshScheduler.fs` | `RefreshPr` handler reconciles live PRs with the store, persists on change, posts the effective map via `UpdatePr`. Collects read-failed worktree paths for `pruneScope` (Decision #10) and builds `worktreeHeads` (branch→tip SHA) for identity-gated reconcile (Decision #11). |
| `src/Server/GitWorktree.fs` | `getUpstreamBranch` returns `UpstreamResult` via pure `classifyUpstream`; `GitData.Upstream` (an `UpstreamResult`) distinguishes a transient upstream read failure from git's clean "no upstream" (Decision #10). `GitData.HeadCommit` carries the worktree tip SHA used as merged-record identity (Decision #11). |
| `src/Server/Program.fs` | Calls `MergedPrStore.load()` at startup. |
| `src/Server/Server.fsproj` | Compile-order entry for `MergedPrStore.fs`. |
| `src/Server/CanvasDocOwnership.fs` | Template for the store (agent + atomic write + load). |
| `src/Server/GithubPrStatus.fs` | Bounded closed-PR fetch that causes merged PRs to age out (`per_page=30`). |
| `src/Server/PrStatus.fs` | `lookupPrStatus` — unchanged; consumes the effective `PrData`. |
| `src/Client/CardViews.fs` | Merged badge — unchanged; renders from `IsMerged`/`Title`/`Url`. |

## Related Specs

- `docs/spec/canvas-doc-ownership.md` — `data/*.json` runtime-state persistence pattern reused here.
- `docs/spec/worktree-monitor.md` — PR-status pipeline and refresh scheduler this extends.
