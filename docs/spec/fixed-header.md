# Fixed Header

## Goals

- Provide a persistent, always-visible header bar pinned to the top of the viewport
- Surface the server's deployment branch (when not `main`) so users know which code version is running
- Display machine-level CPU and memory usage for at-a-glance host health monitoring
- Consolidate the eye logo and control buttons into the fixed header, freeing vertical space in the scrollable content area

## Expected Behavior

### Header Bar

- A thin fixed header (approximately 36px) is pinned to the top of the viewport, remaining visible during scrolling
- Contains (left to right): eye logo (smaller than current), optional branch badge, flexible spacer, system metrics, existing Sort/Compact control buttons
- The scrollable `.dashboard` content starts below the header with appropriate top padding

### Branch Badge

- When the server is running from a branch other than `main`, a visually distinct badge displays the branch name
- When on `main`, no badge is shown (clean default state)
- The branch is detected once at server startup via `git rev-parse --abbrev-ref HEAD` and does not change during runtime

### System Metrics

- Machine-wide CPU percentage and memory usage are displayed on the right side of the header
- Memory shows used/total in GB (e.g., "12.4 / 32.0 GB")
- CPU shows percentage (e.g., "23% CPU")
- Values update naturally through the existing 1-second polling cycle
- Machine-wide metrics are obtained via P/Invoke (`GlobalMemoryStatusEx` for memory, `GetSystemTimes` delta sampling for CPU), consistent with existing Win32 patterns in the codebase

### Layout Migration

- The existing `.dashboard-header` (eye icon, Sort/Compact buttons) is removed; its contents are absorbed into the fixed header
- The `.scheduler-footer` and card grid remain in the scrollable area
- Keyboard navigation continues to work without interference from the fixed header

## Technical Approach

### Shared Types (`Types.fs`)

Add a `SystemMetrics` record type and two new optional fields to `DashboardResponse`:

```fsharp
type SystemMetrics =
    { CpuPercent: float
      MemoryUsedMb: int
      MemoryTotalMb: int }

type DashboardResponse =
    { ... existing fields ...
      DeployBranch: string option
      SystemMetrics: SystemMetrics option }
```

Both fields are `option` types for backward compatibility.

### Server: Branch Detection (`Program.fs`)

A `readDeployBranch` function runs `git rev-parse --abbrev-ref HEAD` once at startup, returning `None` for `main` and `Some branchName` otherwise. The value is captured and passed to the API builder.

### Server: System Metrics (`Win32.fs` + new metrics module)

- Add `GlobalMemoryStatusEx` and `GetSystemTimes` P/Invoke declarations to `Win32.fs`
- Create a metrics module that samples `GetSystemTimes` and computes CPU% via delta between consecutive calls
- Memory is read directly from `GlobalMemoryStatusEx` on each call (sub-millisecond, no sampling needed)
- CPU sampling stores previous `idle/kernel/user` times (isolated mutable state, updated per API call)

### Server: API Wiring (`WorktreeApi.fs`)

Pass `deployBranch` and `getSystemMetrics()` into the API response builder. Both are cheap reads.

### Client: Model + View (`App.fs`)

- Add `DeployBranch` and `SystemMetrics` to `Model`, populated from `DataLoaded` message
- Extract a new `viewAppHeader` function rendered outside the scrollable `.dashboard` div
- Move eye logo SVG and control buttons into the header
- Add branch badge (conditional on `DeployBranch`) and metrics display

### Client: CSS (`index.html`)

- Add `.app-header` styles: `position: fixed`, full width, appropriate z-index, dark background matching the app theme
- Add `.deploy-branch` badge styles (amber text, subtle background)
- Add `.system-metrics` styles (muted color, flex layout)
- Add `padding-top` to `.dashboard` to compensate for fixed header height
- Remove old `.dashboard-header` styles

## Decisions

- **Machine-wide metrics over process-only**: The user asked for host machine CPU/memory, not just the server process. P/Invoke is the appropriate approach since the app is Windows-only.
- **Branch read once at startup**: The deployed branch cannot change while the server is running, so a single read is correct and avoids unnecessary git calls.
- **Option fields for backward compatibility**: Fable.Remoting handles `option` types transparently; old clients ignore new fields gracefully.
- **Memory unit in MB for the type, display in GB**: Store in MB for integer precision, format as GB in the UI for readability.

## Key Files

- `src/Shared/Types.fs` -- new `SystemMetrics` type, updated `DashboardResponse`
- `src/Server/Win32.fs` -- new P/Invoke declarations
- `src/Server/Program.fs` -- `readDeployBranch` function
- `src/Server/WorktreeApi.fs` -- wire new fields into response
- `src/Client/App.fs` -- updated model, new header view, layout restructuring
- `src/Client/index.html` -- CSS for fixed header
