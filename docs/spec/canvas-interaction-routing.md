# Canvas Interaction Routing

## Goals

- Route every canvas interaction to one deterministic session through a single persistent ownership store.
- Keep authored-document behavior and generated-view behavior controlled by `CanvasDoc.Kind`, not by whether a routing owner exists.
- Preserve exact AgentDoc author routing while letting `diff.html` follow the worktree's most recently active session.
- Queue interactions safely when no target session is available and start at most one replacement session per worktree.
- Correlate launch and registration at worktree scope without a per-view claim-token state machine
  or second ownership subsystem.

## Expected Behavior

### Ownership

Each `(worktree, filename)` has one persistent target session in `data/canvas-owners.json`.
AgentDocs assign that target when the authoring extension reports a successful canvas write.
SystemViews use the same store only for interaction routing; their owner remains absent from
`CanvasDoc.OwnerSessionId`, so liveness, Start session, archive, share, awareness, heartbeat, and
morph behavior continue to depend only on `CanvasDoc.Kind`.

`canvas_take_ownership` explicitly assigns the current session for either document kind. AgentDoc
ownership remains sticky until another author writes or claims the document. `diff.html` is
reassigned whenever the worktree's real-activity winner changes, using `StoredStatus.UpdatedAt`
rather than heartbeat or usage timestamps. Other SystemViews, including Beadspace, remain sticky
until explicitly reassigned or removed.

### Delivery and Session Startup

Canvas messages resolve the current target from the unified store. A live target receives the
payload immediately. An offline known target is resumed by its exact session ID; success requires
that session to register a newer bridge before the registration timeout.

When no target exists, or the user chooses a fresh session after resume failure, Treemon queues the
interaction and starts one session under a worktree-wide launch lock. The previous durable target,
if any, is not cleared or replaced by the launch path until registration succeeds. The first
identified bridge registration after that launch assigns every pending canvas filename for the
worktree before queued messages are drained. Launch failure or timeout clears the pending launch
without changing the previous target.

Treemon starts a fresh canvas session only when the worktree has no active session suitable for the
interaction. Concurrent diff and Beadspace interactions join the same pending worktree launch
instead of starting competing sessions. Every starter and joiner awaits one shared completion
result: identified registration resolves success after target assignment, while spawn failure,
exception, cancellation, or timeout resolves the same error for every caller. A reconnecting
same-worktree session may win the narrow registration race; this bounded same-worktree routing risk
is accepted because registration is correlated only by worktree and pending filenames.

Queued messages retain the existing cap of 10 and five-minute TTL. Drain re-resolves the current
target by filename, so ownership changes made while a message waits are honored.

### Persistence and Cleanup

Startup performs a bounded idempotent migration from
`data/canvas-interaction-owners.json`: SystemView entries missing from `data/canvas-owners.json`
are imported, the unified file is persisted, and the legacy file is no longer written. Ownership is
removed when a view or worktree disappears, and scheduler reconciliation prunes targets whose
known canvas file no longer exists.

### Selection Metadata

AgentDocs and SystemViews use the same injected selection runtime and `canvasSend` transport.
SystemViews may return bounded plain-JSON metadata from `window.canvasSelectionMetadata`; the
runtime nests it under `sourceContext`. Diff selections identify the file, hunk, and old/new line
ranges. Beadspace selections identify the task.

## Technical Approach

`CanvasDocOwnership` is the sole mailbox-backed ownership module, providing assignment, lookup,
removal, pruning, and legacy-import operations. `SessionBridge` owns the sessionId-keyed registry,
transport queue, limits, and liveness shared by canvas and agent prompts. `CanvasBridge` layers
filename-based target resolution and worktree launch policy over that generic transport.

The in-memory worktree launch coordinator records pending filenames plus one shared completion
result, serializes fresh starts per worktree, and is consumed by the next identified registration
before queue drain. Registration carries only the worktree, inject URL, and session identity;
launch correlation remains server-side and worktree-scoped.

`SessionActivityService` assigns the unified `diff.html` target whenever the real-activity winner
changes; heartbeat, usage, and metadata-only reports cannot move it. `CanvasScanner` continues
exposing `OwnerSessionId` only for AgentDocs, and the client continues gating every lifecycle
affordance on `CanvasDoc.Kind`.

## Decisions

- **One target store:** authorship and interaction affinity share the same persisted
  `(worktree, filename) -> sessionId` representation.
- **Kind controls behavior:** a SystemView may have an internal routing owner without acquiring
  authored-document UI or lifecycle behavior.
- **One launch per worktree:** pending interactions share a replacement session rather than
  maintaining independent per-view claim state.
- **Server-side launch correlation:** registration-after-launch is correlated by the worktree-wide
  pending launch; the rare same-worktree reconnect race is an accepted simplification.
- **Diff follows real activity; Beadspace stays sticky:** this preserves current user-facing
  affinity while allowing future SystemViews to choose their own assignment policy.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/CanvasDocOwnership.fs` | Unified persistent target store and legacy migration |
| `src/Server/SessionBridge.fs` | Session registry, prompt transport, queueing, and liveness |
| `src/Server/CanvasBridge.fs` | Canvas target resolution and worktree launch coordination |
| `src/Server/WorktreeApi.fs` | Resume, fresh-start, timeout, and recovery behavior |
| `src/Server/SessionActivityService.fs` | Automatic `diff.html` target updates |
| `src/Server/CanvasDocServer.fs` | Registration and explicit ownership endpoints |
| `src/Extension/extension.mjs` | Session registration and ownership declarations |
| `src/Client/CanvasPane.fs` | Kind-gated liveness and lifecycle affordances |

## Related Specs

- `docs/spec/canvas-pane.md` — document kinds, pane behavior, and message transport
- `docs/spec/canvas-authoring-dx.md` — authored document creation and selected-text actions
- `docs/spec/worktree-diff-viewer.md` — diff SystemView behavior and structured selection context
- `docs/spec/beadspace-canvas.md` — Beadspace SystemView behavior
- `docs/spec/resume-last-session.md` — session resume infrastructure
