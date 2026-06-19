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
- **treemon.ps1 §7 implementation edge cases.** Three robustness details settled while implementing
  the shims: (1) **Migration file deleted only after a confirmed start.** `Read-LegacyRoots` is a
  pure reader; `Start-ProductionServer` removes `.treemon.config` *after* the post-launch
  `HasExited` success check (by which point the server has persisted the roots into the global
  config), so a `dotnet publish` failure or an immediate server crash can never lose the migrated
  roots. (2) **Null/empty roots are filtered before building args.** `start`/`dev`/`restart` pass
  `$WorktreeRoots` straight through, but with `[ValueFromRemainingArguments]` an omitted path binds
  the parameter to `$null` (and `@($null)` is a *1-element* array), so `Start-ProductionServer`/
  `Start-DevMode` normalize via `@($Roots | Where-Object { $_ })` — otherwise the "no path" feature
  threw on `$null.TrimEnd()` (and `restart` would kill prod then throw). (3) **add/remove restart
  on any genuine change (tri-state exit code — fix tm-config-audit-rf2).** The CLI folds a path
  batch into a tri-state exit code (`Cli.foldRootResults`): `0` = all succeeded, `1` = all failed,
  `2` = partial. The shims gate the restart on `$tmExit -eq 0 -or $tmExit -eq 2` and keep
  `exit $tmExit`, so prod restarts whenever at least one root was actually persisted (full or partial
  success) while a fully-failed call (bad path, server down) still skips the restart and reports
  non-zero. The original *binary* gate (`$tmExit -eq 0`) was the bug: `addRootToConfig` persists each
  accepted path immediately, so a `[valid; invalid]` batch persisted `valid` but returned exit 1 —
  the shim skipped the restart and the new root stayed dormant despite the printed success line.
  `Invoke-Tm` routes the CLI's stdout to the host (`| Out-Host`) and returns only `[int]$LASTEXITCODE`,
  so the exit code stays a scalar and the CLI's own messages aren't swallowed.
- **Config-dir test isolation uses `TREEMON_CONFIG_DIR`, not a fixture-set `USERPROFILE`/`HOME`.**
  The spec preferred redirecting `USERPROFILE`/`HOME`, but on Windows .NET 9
  `Environment.GetFolderPath(SpecialFolder.UserProfile)` reads the user token, **not** the env
  vars (empirically confirmed), so in-process unit tests can't redirect it that way. Per the
  spec's allowed fallback, `WorktreeApi.globalConfigDir ()` honors a `TREEMON_CONFIG_DIR` override
  (the directory that holds `config.json`, i.e. the `~/.treemon` equivalent); `globalConfigDir` is
  `internal` so §5's `Program.fs` orphan-`roots.json` handling can resolve the same dir. Note this
  override only covers `WorktreeApi`'s global reads/writes; the deferred-consolidation duplicate
  reader in `TreemonConfig.fs` still targets the real `~/.treemon` — so a future endpoint/server
  test that needs `TreemonConfig`-mediated global reads isolated must run in a separate process
  whose `USERPROFILE`/`HOME` point at a temp dir (a server process *does* honor those for path
  building), or that reader must also adopt the override.

- **`addRoot`/`removeRoot` surface persistence failures.** `updateGlobalConfig` was changed to
  return `Result<unit,string>` (it previously logged-and-swallowed write exceptions, which would
  have made the endpoints report a false `Ok()` on a failed write); `writeWorktreeRoots`
  propagates that outcome so the endpoints return `Error` when the config write fails. The four
  best-effort UI-state writers (collapsedRepos, canvas open/position, lastViewedHashes) explicitly
  `|> ignore` the result — their `save*` members are `Async<unit>` and a dropped UI-state write is
  non-critical. The roots read-modify-write reads (`readWorktreeRootsConfig`) outside the lock and
  writes (`writeWorktreeRoots`) inside it; the read-then-write window is accepted because add/remove
  are driven by the serialized, online-only CLI (one path per call), so it is uncontended in
  practice. A future live model would move the read inside the locked `updateGlobalConfig` callback.

- **Startup resolution (§5 / `Program.fs`).** `parseArgs` now accepts zero positional roots in
  normal mode (the previously-`exit 1` "usage" arm was removed; it had become unreachable once the
  `roots <> []` guard was dropped, so the top-level match stays exhaustive with no redundant arm).
  `resolveWorktreeRoots` resolves CLI args > global `worktreeRoots` > orphan `roots.json`, and
  persists the resolved set **only when `config.json` has no `worktreeRoots` *key* yet** (absence,
  not mere emptiness — see the missing-vs-empty decision below) — so a present config is never
  clobbered and CLI args act as an *ephemeral* override there (they win for the run but don't
  rewrite the durable config). The orphan is read by a *pure* reader and deleted **only
  after a successful persist** (not before): an eager read-then-delete would silently lose the
  migrated set if the config write failed. Consequently the orphan is consumed only on the
  priority-3 path (no args, no `worktreeRoots` key); with args present (e.g. `treemon.ps1` migrating
  `.treemon.config`) the args win and a still-present orphan is left untouched. Orphan roots are
  migrated verbatim (no `GetFullPath`/existence check) — downstream comparisons canonicalize at
  compare time and the scheduler tolerates missing dirs.
- **Missing-vs-empty `worktreeRoots` (fix tm-config-audit-rf1).** Startup resolution must
  distinguish a *missing* `worktreeRoots` key (fresh install / pre-migration) from a *present but
  empty* one (the user curated every root away). The original `readWorktreeRootsConfig` collapsed
  both to `[]`, so an explicit `worktreeRoots:[]` was treated like a fresh install and got
  repopulated on restart — a stale orphan `roots.json` resurrected removed roots, and CLI args
  overwrote the explicit empty. Fix: `WorktreeApi.tryReadWorktreeRootsConfig () : string list option`
  is the presence-aware reader (`None` = key absent, `Some []` = explicit empty, `Some roots` =
  populated; a malformed non-array value reports `None` to preserve the old lenient behavior).
  `readWorktreeRootsConfig () : string list` stays as a thin `Option.defaultValue []` wrapper so the
  `getRoots` endpoint and add/remove read-modify-write are unchanged. `resolveWorktreeRoots` gates
  BOTH the orphan-import fallthrough AND the first-time persist on key **absence**
  (`Option.isSome configRoots`), never on `List.isEmpty`. Result: a present `worktreeRoots:[]` is
  the priority-2 source (resolves to `[]`, no orphan import) and is never persisted over, so removed
  roots stay removed across restarts. Regression coverage:
  `ServerStartupResolutionTests` — orphan-present and CLI-args-present each leave the explicit empty
  intact.
- **Demo/fixture modes pass `[]` to `worktreeApi`/scheduler** (resolution is bypassed entirely).
  This is behaviorally inert for fixture mode because `worktreeApi`'s fixture branch is built from
  `readOnlyApi` and ignores `rootPaths`; passing `[]` matches the spec's "(roots stay [])".
- **Read-only `addRoot`/`removeRoot` return `Error` (fix tm-config-audit-rf3).** In `--demo`/
  `--test-fixtures`, `readOnlyApi` wires these to `Error $"Root management is not available in
  {modeName}"` (matching every other unsupported mutation), not a silent `Ok()`. The old `Ok()`
  made the CLI print `✓ Added … (applies on next server restart)` against a read-only server even
  though nothing is persisted (these modes force `worktreeRoots=[]`). `getRoots` still returns `[]`
  (a read, correctly empty).
- **Smoke tests isolate the config dir.** Both `SmokeTests` fixtures start the server in *normal*
  mode with real roots, which now triggers the startup persist; without isolation that would write
  the developer's real `~/.treemon`. Each fixture points the child server at a throwaway
  `TREEMON_CONFIG_DIR` and deletes it in teardown. New in-process coverage lives in
  `src/Tests/ServerStartupResolutionTests.fs` (parseArgs empty-roots + resolveWorktreeRoots
  priority/persist/no-clobber/orphan-migration), isolated via the same `TREEMON_CONFIG_DIR` override
  used by `WorktreeRootsConfigTests`.

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
