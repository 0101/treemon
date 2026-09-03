# Scheduler Module Boundaries

## Goals

- Keep refresh orchestration separate from Canvas filesystem-watcher ownership.
- Preserve the current scheduler state machine and watcher lifecycle without adding abstractions.
- Make each module small enough to understand without crossing unrelated responsibilities.

## Expected Behavior

Canvas directory watchers continue to track known worktrees, post document changes into the
scheduler mailbox, reconcile additions/removals after worktree discovery, and dispose every owned
watcher on shutdown.

Moving the code changes no polling cadence, watcher count, event filtering, scheduler message, or
failure behavior. `RefreshScheduler` remains the caller that decides when reconciliation runs;
`CanvasWatchers` owns only watcher construction, reconciliation, and disposal.

## Technical Approach

Move the existing `CanvasWatchers` module from `RefreshScheduler.fs` into a concept-specific
`CanvasWatchers.fs` compiled before `RefreshScheduler.fs`. Preserve its current function signatures
so the scheduler call sites remain direct.

Keep filesystem mutation and disposal inside the watcher module's existing impure boundaries.
Do not introduce an interface, class, generic watcher framework, or compatibility wrapper.

## Decisions

- **Extraction by existing module seam:** the cohesive module already exists; only file ownership
  changes.
- **Scheduler retains orchestration:** watcher reconciliation timing remains part of refresh
  lifecycle control.
- **No generalized filesystem service:** Canvas watching is the only behavior being separated.

## Related Specs

- `docs/spec/canvas-pane.md` - Canvas document scanning and change delivery.
- `docs/spec/worktree-monitor.md` - refresh scheduler architecture.
