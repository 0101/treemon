# GlobalConfig Store — extract machine-level config I/O from WorktreeApi

## Goals

- Lift the machine-level `~/.treemon/config.json` read/modify/write code out of `src/Server/WorktreeApi.fs`
  into a dedicated `src/Server/GlobalConfig.fs` module, so the largest production module is left with
  just the `IWorktreeApi` wiring and `DashboardResponse` assembly it is named for.
- Give the persistence concern a named home that matches its already-separate test suites
  (`ConfigWriterTests.fs`, `WorktreeRootsConfigTests.fs`).
- **Behavior-preserving.** No change to config file format, key semantics, locking, atomic-write
  behavior, or any `IWorktreeApi` response. The full suite (Unit + Fast + E2E) stays green; E2E asserts
  on DOM/CSS, so an identical render proves the move is behaviorally invisible.

## Expected Behavior

This is a pure extraction — every externally observable behavior is preserved. The invariants that must
still hold after the move:

- **Config location.** `globalConfigDir` resolves `TREEMON_CONFIG_DIR` (test override) else
  `~/.treemon`; `config.json` lives there. Unchanged.
- **Single serialized writer.** Every write still funnels through the one in-process lock
  (`globalConfigLock`) and the atomic temp-file-then-replace path (`updateConfigAtPath`). No write may
  bypass the lock.
- **Never destroy data.** An unparseable `config.json` is still backed up to a timestamped
  `*.corrupt-<ts>` sibling and a fresh object started; a write only touches the named keys, leaving every
  other key intact.
- **`worktreeRoots` missing-vs-empty distinction.** `tryReadWorktreeRootsConfig` still returns `None`
  for a missing/malformed key and `Some []` for a present empty list; `readWorktreeRootsConfig` still
  flattens missing → `[]`. The startup resolver in `Program.fs` depends on this distinction.
- **Roots add/remove semantics.** `addRootToConfig` still normalizes, requires an existing directory,
  is an idempotent no-op for an already-watched path, and surfaces persistence failures as `Error`.
  `removeRootFromConfig` still errors on an unwatched path and allows removing a root whose directory no
  longer exists.
- **Typed accessors.** `collapsedRepos`, `canvasPaneOpen`, `canvasPosition`, `lastViewedHashes`, and the
  editor command/name reader return the same values for the same files as before.

## Technical Approach

### New module: `src/Server/GlobalConfig.fs`

Move the whole config block (currently `WorktreeApi.fs` lines ~178–433) verbatim into
`module Server.GlobalConfig`. It has no dependency on anything else in `WorktreeApi.fs`; its only inputs
are `Log`, `Shared` domain types (`RepoId`, `CanvasPosition`), and `Shared.PathUtils.pathEquals` — all of
which compile before it. Functions to move:

- **Generic JSON store (private helpers):** `globalConfigPath`, `withConfigDocument`, `readGlobalConfig`,
  `globalConfigLock`, `tryParseJsonObject`, `updateGlobalConfig`, plus `canonicalRoot` / `tryNormalizeRoot`.
- **Public surface (consumed outside the module):** `globalConfigDir`, `updateConfigAtPath`,
  `readCollapsedRepos`, `writeCollapsedRepos`, `tryReadWorktreeRootsConfig`, `readWorktreeRootsConfig`,
  `writeWorktreeRoots`, `addRootToConfig`, `removeRootFromConfig`, `readCanvasPaneOpen`,
  `writeCanvasPaneOpen`, `readCanvasPosition`, `writeCanvasPosition`, `readLastViewedHashes`,
  `writeLastViewedHashes`, `getEditorConfig`.

Helpers used only inside the module stay `private`; everything consumed by `WorktreeApi.fs`,
`Program.fs`, or tests is `internal` (assembly-scoped, and `InternalsVisibleTo Tests` keeps the test
references working). Keep the existing doc-comments with the functions they describe.

### Compile order — `src/Server/Server.fsproj`

Insert `<Compile Include="GlobalConfig.fs" />` **before** `WorktreeApi.fs`. It depends only on `Log.fs`,
`PathUtils.fs` (in `Shared`), and the `Shared` types, all already earlier in the order, so it can slot in
right after `SyncEngine.fs` / `DemoFixture.fs` and before `WorktreeApi.fs`.

### Update consumers — no compatibility shims (project rule)

`WorktreeApi.fs` keeps the API wiring and calls the moved functions via `GlobalConfig.*` (add
`open Server.GlobalConfig` or qualify). Other call sites, all moving from `WorktreeApi.*` →
`GlobalConfig.*`:

| Consumer | References to retarget |
|---|---|
| `src/Server/WorktreeApi.fs` | `getEditorConfig`, `readCollapsedRepos`/`writeCollapsedRepos`, `readCanvasPaneOpen`/`writeCanvasPaneOpen`, `readCanvasPosition`/`writeCanvasPosition`, `readLastViewedHashes`/`writeLastViewedHashes`, `addRootToConfig`/`removeRootFromConfig`, `readWorktreeRootsConfig` |
| `src/Server/Program.fs` | `globalConfigDir` (roots.json path), `tryReadWorktreeRootsConfig`, `writeWorktreeRoots` |
| `src/Tests/ConfigWriterTests.fs` | `open Server.WorktreeApi` → `open Server.GlobalConfig` (uses `updateConfigAtPath`) |
| `src/Tests/WorktreeRootsConfigTests.fs` | `open Server.WorktreeApi` → `open Server.GlobalConfig` (uses roots read/write/add/remove) |
| `src/Tests/ServerStartupResolutionTests.fs` | `Server.WorktreeApi.readWorktreeRootsConfig` / `writeWorktreeRoots` → `Server.GlobalConfig.*` |
| `src/Tests/SmokeTests.fs` | comment mention of `WorktreeApi.globalConfigDir` (line ~38) — keep honest |

The move and all consumer updates land in **one atomic change** so the build never goes red: once
`globalConfigDir` (etc.) leaves `WorktreeApi`, every `WorktreeApi.globalConfigDir` reference must update in
the same commit. No re-export shims (project forbids backwards-compatibility shims).

### Verification of the cut

After the move, `(Get-Content src/Server/WorktreeApi.fs).Count` should drop by ~250 lines (from 870), and
`GlobalConfig.fs` should contain the moved functions. Build + Unit + Fast + E2E stay green.

## Decisions

- **Module name `GlobalConfig`, not `Config`.** It owns the *machine-level* `~/.treemon/config.json`,
  distinct from the per-worktree `.treemon.json` handled by `TreemonConfig.fs`. The name keeps that
  distinction obvious and avoids collision with the existing `TreemonConfig` module.
- **One file, not split by concern.** The generic JSON store and the typed accessors are cohesive (the
  accessors are thin wrappers over the store) and share the lock/atomic-write helpers; splitting them
  would scatter a single responsibility. The whole thing is one ~256-line module.
- **`internal` over `public`.** The surface is only consumed inside the `Treemon` assembly and its test
  assembly (via `InternalsVisibleTo`), so `internal` preserves encapsulation while keeping every existing
  reference compiling.
- **Atomic move, no shims.** Per the project's no-backwards-compatibility-shims rule, consumers are
  updated in the same change rather than left pointing at re-exports.

## Key Files

- **New:** `src/Server/GlobalConfig.fs` — machine-level config store + typed accessors.
- **Shrinks:** `src/Server/WorktreeApi.fs` — left with `IWorktreeApi` wiring + `DashboardResponse` assembly.
- **Compile order:** `src/Server/Server.fsproj`.
- **Consumers:** `src/Server/Program.fs`, `src/Tests/ConfigWriterTests.fs`,
  `src/Tests/WorktreeRootsConfigTests.fs`, `src/Tests/ServerStartupResolutionTests.fs`.
- **Docs to keep honest:** `docs/spec/worktree-monitor.md` (Key Files table),
  `docs/spec/future/code-improvements.md` (move the item to *Done*).

## Related Specs

- `docs/spec/worktree-monitor.md` — the watched-roots / `config.json` behavior this code implements.
- `docs/spec/future/code-improvements.md` — the running backlog; this is candidate #7 (the survey's top pick).
