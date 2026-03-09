# Demo Fixture System

## Goals

- Produce a short (~10s) looping demo showing the dashboard with realistic fake data and visible state transitions (coding tool status changes, build failures, sync events)
- Reproducible for re-recording when features change — deterministic server data, `treemon.ps1 demo` command
- Comprehensive coverage of all dashboard features: both providers (Claude/Copilot), all build statuses, archived worktrees, system metrics, deploy branch, failed scheduler events
- Additive — existing `--test-fixtures` mode and E2E tests remain untouched

## Expected Behavior

- `.\treemon.ps1 demo` launches the server with `--demo` flag and Vite dev server
- Server cycles through 3-4 frames (totaling ~10s), looping continuously
- Each frame is a complete `FixtureData` (dashboard response + sync status)
- Client polls every 1s and renders whatever the server returns — no client changes needed
- Timestamps in demo data are adjusted at serve time so events show as "5s ago", "2m ago" etc.
- Both `getWorktrees` and `getSyncStatus` return data from the same frame for coherence

### Frame Sequence

1. **Frame 1 (3s):** Claude working, Copilot working, build running, PRs visible, active sessions
2. **Frame 2 (3s):** Build fails, Claude → WaitingForUser, new sync event, scheduler error appears
3. **Frame 3 (2s):** Sync completes, Copilot → Done, scheduler recovers
4. **Frame 4 (2s):** Variations, back toward baseline

### Coverage Matrix

All of these must appear across the frame sequence:

- Both AzDo and GitHub repos
- Claude and Copilot providers
- All `CodingToolStatus` values: Working, WaitingForUser, Done, Idle
- All `BuildStatus` values: Succeeded, Failed, Building, PartiallySucceeded, Canceled
- Archived worktrees (`IsArchived = true`)
- `SystemMetrics` with realistic CPU/RAM values
- `DeployBranch` showing branch name in header
- `AppVersion` with static placeholder (`"demo|0"`)
- `EditorName` with realistic value (`"VS Code"`)
- Failed scheduler events in `LatestByCategory`
- Work metrics with varying sizes
- `IsDirty` + `MainBehindCount` combo
- PR states: open, draft, merged
- Comments: `WithResolution`, `CountOnly`
- `HasActiveSession = true` on at least one worktree

## Technical Approach

### Architecture

```
src/Shared/Types.fs         — FixtureData type moved here (from WorktreeApi.fs)
src/Server/DemoFixture.fs   — F# module: base data, named worktrees, frame sequence, selectFrame
src/Server/WorktreeApi.fs   — demo mode API branch, loadFixtures stays
src/Server/Program.fs       — --demo CLI flag (no path needed), mutually exclusive with --test-fixtures
treemon.ps1                 — `demo` command
```

### Key Design Decisions

1. **F# data structures over JSON** — `with` expressions derive frame variations from a shared base. Compiler catches type changes immediately. No JSON sync issues.

2. **`FixtureData` type moves to `Shared/Types.fs`** — Avoids circular module dependency. `DemoFixture.fs` compiles before `WorktreeApi.fs` and needs the type. It's just a data shape (`DashboardResponse` + `Map<string, CardEvent list>`) so it belongs with the types it composes.

3. **`adjustTimestamps` operates on full `FixtureData`** — Shifts all timestamp fields in both `DashboardResponse` and `SyncStatus` relative to "now" at serve time. Both `getWorktrees` and `getSyncStatus` use the same adjusted frame.

4. **`selectFrame` uses simple recursion** — Elapsed time modulo total duration → walk the frame list subtracting durations until position falls within a frame.

5. **`parseArgs` relaxes root requirement for `--demo`** — Demo mode needs zero roots. Add `Demo: bool` to `ServerConfig`, handle in pattern match before the `roots <> []` guard.

### What Changes vs. What Doesn't

- **Changes:** `Types.fs` (add `FixtureData`), `WorktreeApi.fs` (remove `FixtureData` type, add demo API branch), `Program.fs` (add `--demo` parsing + wiring), `Server.fsproj` (add `DemoFixture.fs`), `treemon.ps1` (add `demo` command)
- **Unchanged:** Client code, existing `--test-fixtures` mode, E2E tests, `worktrees.json`
