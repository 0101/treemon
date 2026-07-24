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
until explicitly reassigned or removed. Automatic diff reassignment is discarded while a fresh
launch for `diff.html` is pending; the explicit launch keeps precedence until it succeeds or fails.

### Delivery and Session Startup

Canvas messages resolve the current target from the unified store. A live target receives the
payload immediately. An offline known target is resumed by its exact session ID; success requires
that session to register a newer bridge before the registration timeout. Concurrent recoveries for
the same normalized worktree and exact target session join one shared resume outcome. If an
immediate POST fails at the transport boundary, Treemon conditionally invalidates only that failed
registration; a newer concurrent re-registration wins and is never removed.

When no target exists, Treemon queues the interaction and starts one session under a worktree-wide
launch lock. After resume failure, the explicit “Start fresh and reassign” recovery is available
only for SystemViews; AgentDoc ownership can change only through an author write or explicit claim.
The previous durable target, if any, is not cleared or replaced by the launch path until
registration succeeds. The first identified bridge registration after that launch assigns pending
SystemViews before queued messages are drained. It may claim an unowned AgentDoc only while that
document remains unowned, so a newer author write or explicit claim always wins. Launch failure or
timeout clears the pending launch without changing the previous target.

Treemon starts a fresh canvas session only when the worktree has no active session suitable for the
interaction. Concurrent diff and Beadspace interactions join the same pending worktree launch
instead of starting competing sessions. Every starter and joiner awaits one shared completion
result: identified registration resolves success after target assignment, while spawn failure,
exception, cancellation, or timeout resolves the same error for every caller. A reconnecting
same-worktree session may win the narrow SystemView registration race; this bounded routing risk is
accepted because SystemView registration is correlated only by worktree and pending filenames.

Queued messages retain the existing cap of 10 and five-minute TTL. Drain re-resolves the current
target by filename, so ownership changes made while a message waits are honored.

### Persistence and Cleanup

Ownership is removed when a view or worktree disappears, and scheduler reconciliation prunes
targets whose known canvas file no longer exists.

### Selection Metadata

AgentDocs and SystemViews use the same injected selection runtime and `canvasSend` transport.
SystemViews may return bounded plain-JSON metadata from `window.canvasSelectionMetadata`; the
runtime nests it under `sourceContext`. Diff selections identify the file, hunk, and old/new line
ranges. Beadspace selections identify the task.

## Technical Approach

`CanvasDocOwnership` is the sole mailbox-backed ownership module, providing assignment, lookup,
removal, and pruning operations. `SessionBridge` owns the sessionId-keyed registry,
transport queue, limits, and liveness shared by canvas and agent prompts. `CanvasBridge` layers
filename-based target resolution and worktree launch policy over that generic transport.

The in-memory worktree launch coordinator records pending filenames plus one shared completion
result, serializes fresh starts per worktree, and is consumed by the next identified registration
before queue drain. SystemView targets use unconditional pending assignment. An AgentDoc pending
without an owner uses an atomic conditional assignment whose expected owner is `None`; generic
registration never replaces an existing AgentDoc owner. Registration carries only the worktree,
inject URL, and session identity; launch correlation remains server-side and worktree-scoped. The
same coordinator serializes automatic diff assignments with pending registration and queue drain,
discarding assignments for a filename whose fresh launch is still pending.

Exact-session recovery uses a separate in-memory coordinator keyed by normalized worktree and
target session ID. The starter owns spawn plus registration confirmation; joiners await the same
`CanvasMessageResult`, and completion removes the entry for success, spawn failure, exception, or
registration timeout.

`SessionActivityService` assigns the unified `diff.html` target whenever the real-activity winner
changes; heartbeat, usage, and metadata-only reports cannot move it. `CanvasScanner` continues
exposing `OwnerSessionId` only for AgentDocs, and the client continues gating every lifecycle
affordance on `CanvasDoc.Kind`.

## Decisions

- **One target store:** authorship and interaction affinity share the same persisted
  `(worktree, filename) -> sessionId` representation.
- **Case-preserving filename identity:** ownership keys retain the real on-disk filename case;
  only worktree paths are normalized. Scanner lookup and filesystem pruning therefore use the same
  identity on case-sensitive hosts.
- **Kind controls behavior:** a SystemView may have an internal routing owner without acquiring
  authored-document UI or lifecycle behavior.
- **One launch per worktree:** pending interactions share a replacement session rather than
  maintaining independent per-view claim state.
- **One resume per exact target:** concurrent offline interactions share the same exact-session
  recovery instead of repeatedly replacing the worktree's terminal.
- **Server-side launch correlation:** registration-after-launch is correlated by the worktree-wide
  pending launch. SystemViews accept the bounded same-worktree registration race; AgentDoc
  ownership does not.
- **AgentDoc ownership stays author-controlled:** generic registration may claim only a still-unowned
  AgentDoc. Existing ownership changes only through an author write or explicit claim. SystemView
  pending assignment remains unconditional.
- **Diff follows real activity; Beadspace stays sticky:** this preserves current user-facing
  affinity while allowing future SystemViews to choose their own assignment policy.
- **Pending fresh launch beats activity:** automatic diff assignments that reach the routing
  coordinator while `diff.html` is pending are discarded rather than replayed, so successful
  registration keeps the new target and failed launches preserve the previous durable target.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/CanvasDocOwnership.fs` | Unified persistent target store |
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
