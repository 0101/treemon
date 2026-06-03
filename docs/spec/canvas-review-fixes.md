# Canvas Review Fixes

Improvements identified by focused code review of the canvas pane branch.

## Goals

- Fix four bugs: (1) HTML injection fails on `</head>` casing variants, (2) fixture-mode `sendCanvasMessage` returns Error instead of Queued, (3) canvas bridge/liveness broken for agent-created docs, (4) `LaunchCanvasSession` prompt lacks doc path and canvas context
- Replace `CanvasMessageError: string option` + `CanvasMessageQueuedAt: float option` with a `CanvasSendState` DU — no illegal states
- Eliminate duplication: `activeVisibleDoc` logic exists in one place, test nav helpers in one shared module
- Enforce F# idioms: no null comparisons in CanvasPane.fs, no `DateTimeOffset.Now` capture inside `expireCanvasEvents`
- Cross-reference canvas pane in architecture spec (`docs/spec/worktree-monitor.md`)

## Expected Behavior

- HTML injection into canvas docs works regardless of `</head>` casing
- Fixture mode returns `Queued` for canvas messages, enabling test coverage of the queued path
- `CanvasSendState` DU makes illegal states (both error and queued-at set) unrepresentable
- `expireCanvasEvents` receives `now` as a parameter instead of capturing clock internally
- Canvas doc resolution logic exists in one place (`activeVisibleDoc`), not three
- Test navigation helpers (`canvasToggleBtn`, `focusCanvasCard`, etc.) live in a shared module
- JS interop string converted to `Option` at the boundary, no null comparisons
- Architecture spec references the canvas pane subsystem
- Canvas bridge interactions work for agent-created docs (postMessage reaches handler, liveness reflects active sessions)
- `LaunchCanvasSession` prompt includes full absolute doc path and canvas workflow context

## Technical Approach

All changes are localized refactors within the existing canvas pane feature. No new dependencies or architectural changes.

### Bug fixes
- `Program.fs:221`: Use `StringComparison.OrdinalIgnoreCase` overload of `String.Replace`
- `WorktreeApi.fs:63`: Return `CanvasMessageResult.Queued` in read-only API
- Canvas bridge/liveness: investigate why agent-created docs don't register with the bridge and why `BridgeLiveness.IsAlive` returns false for active sessions — fix root cause
- `LaunchCanvasSession` handler (`App.fs:632`): include full doc path (`Path.Combine` wt.Path and doc path) and brief canvas context in prompt string

### CanvasSendState DU
- Define `type CanvasSendState = Idle | Waiting of queuedAt: float | Failed of message: string`
- Replace `CanvasMessageError: string option` and `CanvasMessageQueuedAt: float option` on Model
- Update all pattern matches in update and view

### Clock purity
- Add `now: DateTimeOffset` parameter to `expireCanvasEvents`
- Pass from `Tick` handler (existing `now` payload) and `DataLoaded` handler

### Duplication removal
- Derive `detectChangedCanvasDocs` from `detectCanvasEvents`
- Rewrite `LaunchCanvasSession` active-doc lookup to use `activeVisibleDoc`
- Rewrite `focusedWorktreeCanvasDoc` in terms of `activeVisibleDoc`
- Extract shared test helpers to `CanvasTestHelpers` module

### Null handling
- Convert `emitJsExpr` result to `Option<string>` at the boundary in `CanvasPane.fs`

### Spec update
- Add canvas pane cross-reference to `docs/spec/worktree-monitor.md`

## Decisions

### Bridge registration for agent-created docs
Agent-created canvas docs don't explicitly register with the bridge (the SKILL.md says "No registration needed"). The fix injects a heartbeat script into every served HTML doc. The script:
1. Extracts the worktree path from the iframe URL
2. POSTs to `/bridge/heartbeat` on the canvas doc server (same origin — no CORS)
3. The heartbeat endpoint registers with `CanvasBridge` using `PollInjectUrl` sentinel
4. `sendMessage` detects the sentinel and enqueues messages instead of HTTP POST
5. `drainPending` returns queued messages in the heartbeat response

This avoids a duplicate message queue (single source of truth in `CanvasBridge.messageQueue`) and avoids needing a separate inject endpoint. Client-side liveness also uses `HasActiveSession` as a fallback for immediate detection.

## Key Files

- `src/Client/App.fs` — Model, update, canvas event helpers
- `src/Client/CanvasPane.fs` — Canvas pane view, JS interop
- `src/Server/Program.fs` — CanvasDocServer HTML injection
- `src/Server/WorktreeApi.fs` — Read-only API stub
- `src/Server/CanvasBridge.fs` — Bridge registration and liveness
- `src/Server/CodingToolStatus.fs` — Action prompt formatting (CanvasSession passthrough)
- `src/Tests/CanvasPhase4Tests.fs`, `src/Tests/CanvasPaneTests.fs` — Test helpers
- `docs/spec/worktree-monitor.md` — Architecture spec
