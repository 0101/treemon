# Worktree Roots in Global Config (CLI-managed, server-mediated)

## Goals

Make the set of watched worktree roots a **single machine-level setting** owned by the server,
managed live through the `tm` CLI — eliminating the PowerShell-managed `.treemon.config`, the
stale orphan `~/.treemon/roots.json`, and the restart-to-apply cycle.

- One source of truth for roots: `~/.treemon/config.json` → `"worktreeRoots": [...]`.
- The **server is the only writer** of `config.json` (fixes the cross-process clobber hazard
  where PowerShell's whole-file overwrite would wipe server-written keys like `collapsedRepos`).
- `tm add <path>` / `tm remove <path>` **persist immediately** (server-side, single writer);
  the change is applied when the server (re)starts. The `treemon.ps1 add/remove` shims trigger
  that restart automatically when the production server is running (matching today's UX).
- `treemon.ps1` keeps only lifecycle duties (`start/stop/restart/status/dev/deploy/log`) and no
  longer needs a path for `start`/`dev` once roots are configured.
- One-time migration of existing `.treemon.config` and the orphan `~/.treemon/roots.json` into
  the global config, then delete both.

Non-goals (explicitly deferred): consolidating the *other* duplicated config readers
(`TreemonConfig` vs `WorktreeApi` global helpers) and introducing typed config records
(Solutions A/B from the investigation), and splitting `.treemon.json` shared-vs-local
(Solution C). This spec is Solution **D** only.

## Expected Behavior

### CLI
- `tm add <path> [<path>...]` — validates each path exists, normalizes it (absolute,
  trailing-slash trimmed), calls the server, which persists it to global config. On success
  prints `✓ Added <path> (applies on next server restart)`. Adding an already-watched path is a
  no-op success. Requires the server running (online-only); applied on the next (re)start.
- `tm remove <path> [<path>...]` — calls the server, which removes it from global config. On
  success prints `✓ Removed <path> (applies on next server restart)`. Removing an unknown path
  reports a clear error. Removing the last remaining root is allowed (empty dashboard is valid).
- `tm roots` — lists currently watched roots (so users/scripts can inspect without the UI).
- All three are online-only: if the server isn't running they print the existing
  "server is not running" message (consistent with `tm worktrees`/`new`/`launch`).

### Server
- On startup, **effective roots** resolve by priority:
  1. roots passed as CLI args (used by `dev`/tests; preserves current arg behavior),
  2. else `worktreeRoots` from `~/.treemon/config.json`,
  3. else (migration) import the orphan `~/.treemon/roots.json` if present.
- After resolving, if `config.json` has no `worktreeRoots`, the server **persists** the resolved
  roots into it (so a migrated/arg-provided set becomes the durable source of truth), and deletes
  the orphan `roots.json` once imported.
- `--demo` and `--test-fixtures` modes are unchanged (roots stay `[]`, scheduler behavior as today).
- `addRoot`/`removeRoot` validate and persist to global config (single, locked writer); they
  return `Result<unit,string>` so the CLI can surface failures (bad path, etc.). They do not
  mutate the running watch set — roots are re-read at (re)start.

### treemon.ps1
- `start` / `dev` no longer require a path argument; with no path they let the server use the
  global config. A path argument still works (passed as args → highest priority).
- `status` shows the watched roots by querying the server (via `tm roots`/endpoint), not by
  reading a local file.
- `add` / `remove` become **thin shims** that call `tm add` / `tm remove` and then, if the
  production server is running, restart it to apply (same effect as today). Usage text, AGENTS.md,
  and README are updated accordingly.
- On any invocation, if a legacy `.treemon.config` exists in the script dir, its roots are passed
  to the next `start` as args (so the server migrates+persists them) and the file is deleted.

## Technical Approach

### 1. Shared API surface (`src/Shared/Types.fs`, IWorktreeApi ~248-274)
Add three members (paths as plain `string`, consistent with existing simple endpoints):
```fsharp
addRoot: string -> Async<Result<unit, string>>
removeRoot: string -> Async<Result<unit, string>>
getRoots: unit -> Async<string list>
```
No extra Fable.Remoting registration is needed beyond implementing them in the record
(`Remoting.fromValue api` in `Program.fs`).

### 2. Global-config roots read/write + write lock (`src/Server/WorktreeApi.fs` ~174-234)
- Reuse the existing `withConfigDocument` (read) and `updateGlobalConfig` (RMW) helpers — add
  `readWorktreeRootsConfig () : string list` and `writeWorktreeRoots (roots: string list)`.
- **Add a private lock object guarding `updateGlobalConfig`** (it currently has none). All
  global-config writes (collapsedRepos, canvas state, lastViewedHashes, and the new roots) go
  through the locked helper so concurrent API calls can't corrupt `config.json`. This is the
  mechanism that makes "server is the single, serialized writer" true.
- **Test isolation:** the global-config directory must be overridable so endpoint tests don't
  touch the real `~/.treemon`. Prefer test-side isolation (the fixture sets the server process's
  `USERPROFILE`/`HOME` to a temp dir); only if that proves insufficient, honor a
  `TREEMON_CONFIG_DIR` env override in `globalConfigPath`.

### 3. Scheduler — no change (restart-to-apply)
Roots are read fresh from global config at each server (re)start (§5), so the scheduler needs no
runtime add/remove machinery. `rootPaths` continues to be built once in `start` (~671). *(If live
updates are wanted later, this is the spot: add `RootPaths` to `DashboardState`, `AddRoot`/
`RemoveRoot` `StateMsg` cases, and have the refresh loop read roots from state.)*

### 4. API endpoint implementation (`src/Server/WorktreeApi.fs` worktreeApi ~455-466)
- Implement `addRoot`/`removeRoot`: normalize+validate path → read-modify-write via
  `writeWorktreeRoots` (§2). No scheduler message. `getRoots` returns `readWorktreeRootsConfig ()`.
- `getWorktrees`/`createWorktree`/path-validation keep using the `rootPaths` captured at startup
  (line 466) — correct, since roots only change across restarts.

### 5. Server startup resolution + migration (`src/Server/Program.fs` ~46-98, 141-186)
- **Allow empty roots in `parseArgs` (~46-98).** Today the final match arm (~95-98) prints usage and
  `exit 1` whenever no positional root is given and `--demo` is absent. Change it so zero root args is
  valid in normal mode (`WorktreeRoots = []`); leave `--demo`/`--test-fixtures` handling intact. This
  is the change that lets `start`/`dev` run with no path.
- Add a root-resolution step before `RefreshScheduler.start`/`worktreeApi`: args → global config
  → orphan import (per Expected Behavior). Persist resolved roots to global config if absent;
  delete `~/.treemon/roots.json` after import.
- Pass the resolved roots to both `RefreshScheduler.start` and `worktreeApi` as today. Demo/
  fixture modes bypass this (unchanged).

### 6. CLI subcommands (`src/Cli/Program.fs`)
Add `addCmd`, `removeCmd`, `rootsCmd` following the `newCmd` idiom (~164-187): `command "add" {
inputs (...) ; setAction handler }`, calling via `runApi`/`tryCallServer`. Register them in
`main` (~218-227). `add`/`remove` accept one-or-more paths; loop calling the endpoint per path.

### 7. treemon.ps1 cleanup + migration (`treemon.ps1`)
- Delete `Save-Config`, `Get-SavedConfig`, `Resolve-WorktreeRoots`, `Add-Roots`, `Remove-Roots`,
  the legacy `WorktreeRoot` (singular) fallback, and the `add`/`remove` switch arms.
- `start`/`dev`: make the path argument optional; pass through whatever paths are given.
- `restart`/`deploy`: stop relying on `Get-SavedConfig` — restart with no path args (server reads
  global config).
- `status`: list roots via `tm roots`.
- Migration shim: if `$ConfigFile` (`.treemon.config`) exists, read its `WorktreeRoots`, pass them
  to the next `Start-ProductionServer` as args (one time), then delete the file.

### 8. Docs (`docs/spec/worktree-monitor.md`, `AGENTS.md`, `README.md`)
Update the config section + the `add`/`remove` command lines to describe `tm add`/`tm remove`,
the global `worktreeRoots` key, and that `start`/`dev` no longer need a path.

## Decisions

- **Restart-to-apply (chosen for simpler code).** `addRoot`/`removeRoot` only persist to global
  config; the new roots take effect when the server (re)starts, which the `treemon.ps1`
  add/remove shims trigger automatically when the server is running (matching today's behavior).
  This avoids the scheduler-state machinery a live model would need (§3). Live application remains
  a clean future extension.
- **Server is the single writer of `config.json`**, with an added write lock (§2). The CLI never
  writes config files (online-only, confirmed). This is the fix for the cross-process clobber +
  the missing intra-process lock.
- **add/remove require the server running** (online-only CLI). Cold bootstrap: `treemon.ps1 start`
  with no path launches an empty dashboard (empty roots is valid, as in demo mode), then `tm add`
  populates it.
- **Roots become a per-machine singleton.** Dev and prod instances on the same machine now share
  one global roots list, instead of independent per-script-dir `.treemon.config` files. Accepted
  as the intended simplification.
- **Keep `treemon.ps1 add/remove` as thin shims** that call `tm` then restart the server if
  running. `tm` is installed on PATH by `deploy`. Persistence is server-side (single writer);
  process lifecycle stays in PowerShell — clean separation.
- **Orphan `~/.treemon/roots.json`** is migrated-then-deleted by the server; `.treemon.config` is
  migrated-then-deleted by `treemon.ps1`.

## Key Files

| File | Role in this change |
|---|---|
| `src/Shared/Types.fs` | `IWorktreeApi` — add `addRoot`/`removeRoot`/`getRoots` |
| `src/Server/WorktreeApi.fs` | global-config roots read/write + write lock; endpoint impls; read live roots |
| `src/Server/RefreshScheduler.fs` | no change (roots re-read at restart); future home for live updates |
| `src/Server/Program.fs` | startup root resolution + orphan migration; pass resolved roots |
| `src/Cli/Program.fs` | `add`/`remove`/`roots` subcommands |
| `treemon.ps1` | strip roots logic; optional path for start/dev; status via `tm roots`; `.treemon.config` migration |
| `docs/spec/worktree-monitor.md`, `AGENTS.md`, `README.md` | docs |

## Related Specs
- `docs/spec/worktree-monitor.md` — overall architecture and the existing config section this
  amends.
- Investigation: `.agents/config-files-investigation.md` — full config inventory and the A/B/C/D
  options; this spec implements D.
