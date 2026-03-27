# User Idle Detection — Adaptive Refresh Cadence

## Goals

- Detect when the user is actively using the dashboard vs. away
- Poll aggressively when active (5-10s for local/network tasks) for a responsive feel
- Ease off when idle (current baseline) and conserve resources when deeply idle (1-10 min intervals)
- Use coding tool activity in monitored worktrees as an additional "user is active" signal

## Expected Behavior

### Activity States

Three states derived from two signals (dashboard interaction + coding tool messages):

| State | Condition |
|-------|-----------|
| Active | Dashboard interaction within last 60s, OR any coding tool user message within last 5 min |
| Idle | No dashboard interaction for 60s AND no recent coding tool messages |
| Deep Idle | No dashboard interaction for 15 min AND no recent coding tool messages |

Dashboard interaction = `mousemove`, `keydown`, `click`, `scroll` on the document. Tracked coarse-grained (throttled to 1 dispatch per 5s). App runs as a PWA — no tab-switching concerns; when minimized, mouse events stop and the 60s timer kicks in naturally.

### Refresh Intervals

| Category | Active | Idle (current baseline) | Deep Idle |
|----------|--------|------------------------|-----------|
| Client poll | 1s | 1s | 15s |
| Git | 5s | 15s | 60s |
| CodingTool | 5s | 15s | 60s |
| Beads | 30s | 60s | 240s |
| WorktreeList | 10s | 15s | 60s |
| PR | 10s | 120s | 600s (10 min) |
| Fetch | 10s | 120s | 600s (10 min) |

### Sync Polling

SyncTick (2s interval when a sync is running) is unaffected by activity level. Syncs are user-initiated operations — always show progress regardless of idle state.

### Wake-Up Behavior

When transitioning from Idle/DeepIdle → Active, the client immediately dispatches a Tick (fetches fresh data) and reports the transition to the server. The user sees fresh data within 1s of touching the dashboard.

## Technical Approach

### Shared Types (`src/Shared/Types.fs`)

New `ActivityLevel` DU: `Active | Idle | DeepIdle`. New `reportActivity: ActivityLevel -> Async<unit>` method on `IWorktreeApi`.

### Client-Side (`src/Client/App.fs`)

Elmish subscription registers DOM event listeners (mousemove, keydown, click, scroll). Throttled to dispatch `UserActivity` at most once per 5s — the throttle uses a mutable timestamp inside the subscription closure (Elmish's designated impure boundary, same pattern as `setInterval`). The Model stays fully immutable with `LastActivityTime: float` and `ActivityLevel: ActivityLevel`.

`computeActivityLevel` is a pure function: compares `Date.now() - lastActivity` against 60s/15min thresholds.

`pollingSubscription` includes activity level in its key so Elmish tears down and recreates the interval on transitions. Active/Idle = 1s, DeepIdle = 15s.

On activity level transitions, `reportActivity` is called to inform the server.

### Server-Side (`src/Server/RefreshScheduler.fs`)

`DashboardState` gets a `ClientActivity: ActivityLevel` field, defaulting to `Idle`. New `ReportClientActivity` message updates it. `intervalOf` becomes a function of `(ActivityLevel * RefreshTask)` with explicit intervals per combination (no multiplier math).

`deadlineOf` passes activity through. The scheduler loop already reads state each iteration, so it naturally adapts.

`effectiveActivity` combines client report with coding tool data: if any worktree has a user message within 5 min, override to Active.

### Server API (`src/Server/WorktreeApi.fs`)

New `reportActivity` endpoint posts `ReportClientActivity` to the scheduler agent.

## Decisions

- No WebSocket/SignalR — tune existing polling intervals
- No per-worktree idle tracking — one global activity level
- No sync-running override — activity level is activity level, least code
- No multi-tab support — PWA, single tab assumed
- Mutable state confined to subscription throttle closure only
- `ActivityLevel` uses `[<RequireQualifiedAccess>]` — its `Idle` case would shadow `CodingToolStatus.Idle`
- Server-side client activity decay: `ClientActivityAt` timestamp stored alongside level. If 5 min stale and was Active → Idle; if 20 min stale → DeepIdle regardless. Mirrors coding tool's 5-min pattern. Client only reports transitions (not periodic), so generous timeouts prevent false decay while connected.
