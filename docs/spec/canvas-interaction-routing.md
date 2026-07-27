# Canvas Interaction Routing

## Goals

- Route every canvas interaction to one session, chosen by document kind.
- Preserve exact AgentDoc author routing: an authored document always reaches its author.
- Let a SystemView reach the worktree's most recently active usable session without storing,
  defending, or reconciling a routing target.
- Queue an interaction when no session can receive it, and start at most one session per worktree
  to drain it.

## Expected Behavior

### Target Resolution

Resolution depends only on `CanvasDoc.Kind`.

An **AgentDoc** has a real author. Its `(worktree, filename)` target is persisted in
`data/canvas-owners.json`, assigned when the authoring extension reports a successful canvas write
or when `canvas_take_ownership` claims it explicitly. That ownership is sticky: it changes only
through another author write or another explicit claim.

A **SystemView** is server-generated and has no author, so nothing is persisted for it. Each
interaction resolves, at send time, to the most recently active session that currently holds a live
bridge registration for that worktree. Liveness and activity are separate inputs, fed by two
independent extensions: the bridge registry says which sessions can receive a prompt at all, and
`StoredStatus.UpdatedAt` only *orders* them. A reachable session that has not reported activity is
therefore still a valid target — resolution falls back to the freshest registration rather than
reporting "no target", so Treemon does not spawn a second session beside a usable one. Heartbeat and
usage timestamps never decide the target, preserving the rule that `LastSeen` is liveness-only.

Because a SystemView target is computed rather than stored, it cannot go stale, be raced by
concurrent activity, or need pruning. A SystemView's owner is likewise absent from
`CanvasDoc.OwnerSessionId`, so liveness, Start session, archive, share, awareness, heartbeat, and
morph behavior continue to depend only on `CanvasDoc.Kind`.

### Delivery and Session Startup

A resolved live target receives the payload immediately. Otherwise the interaction is queued.

When a SystemView interaction resolves no target, Treemon starts one session for that worktree and
the queued interaction drains to it. A started launch suppresses another spawn for the same worktree
for 30 seconds; the suppression **expires on time** rather than waiting to be cleared by a
registration, so a spawn that never registers cannot block later interactions, and an unrelated
session's periodic heartbeat cannot be mistaken for the launch completing. A spawn that fails
releases the suppression immediately. Sessions the user starts concurrently are not arbitrated — the
guard covers only Treemon's own spawns.

An AgentDoc interaction with no reachable author is queued without launching, because a new session
would not be that document's author.

Queued messages retain the existing cap of 10 and five-minute TTL. An identical pending envelope for
the same worktree and target session is coalesced without changing FIFO order; different payloads and
targets remain distinct. Coalescing also covers the requeue path: a drain removes the whole queue,
and undelivered survivors merge back ahead of anything enqueued meanwhile, dropping concurrent
entries identical to a survivor rather than restoring a second pending copy. On drain, an AgentDoc
prompt goes only to its recorded owner, so ownership changes made while a message waits are honored.
A SystemView prompt stays bound to the session resolution picked, if any; when nothing was reachable
it drains to the next identified registration — the session the queue caused to launch. An anonymous
(session-less) registration never drains either kind.

### Persistence and Cleanup

Only AgentDoc ownership is persisted. It is removed when a view or worktree disappears; scheduler
reconciliation prunes entries for worktrees that are no longer known **and** for documents whose file
is gone, which is the only path that reclaims a per-document entry.

### Selection Metadata

AgentDocs and SystemViews use the same injected selection runtime and `canvasSend` transport.
SystemViews may return bounded plain-JSON metadata from `window.canvasSelectionMetadata`; the
runtime nests it under `sourceContext`. Diff selections identify the file, hunk, and old/new line
ranges. Beadspace selections identify the task.

## Technical Approach

`CanvasDocOwnership` is the mailbox-backed store for AgentDoc ownership, providing assignment,
lookup, removal, and pruning. `SessionBridge` owns the sessionId-keyed registry, transport queue,
limits, and liveness shared by canvas and agent prompts. `CanvasBridge` layers target resolution and
worktree launch policy over that generic transport.

`CanvasBridge.resolveTarget` branches on `CanvasDocKinds.classify`: an AgentDoc reads
`CanvasDocOwnership`, while a SystemView intersects the worktree's live bridge registrations with the
scheduler's `SessionStatuses` snapshot, takes the most recent by `StoredStatus.activityOrderKey`, and
falls back to the freshest live registration when no reachable session has an activity row.
`CanvasBridge.sendMessage` returns the resolved target alongside the outcome so the caller can
distinguish "queued because nothing is reachable" from "queued behind a known session".

The launch guard is a map from normalized worktree to the time a spawn started, suppressing another
spawn for `launchSuppressionWindow`. It is time-bounded by design: correlating a later registration
back to a specific launch is exactly the bookkeeping this model set out to remove, and an
expiry cannot deadlock the way an uncleared entry can.

`CanvasScanner` continues exposing `OwnerSessionId` only for AgentDocs, and the client continues
gating every lifecycle affordance on `CanvasDoc.Kind`.

## Decisions

- **Resolve SystemViews, store AgentDocs:** a routing target that is a pure function of live session
  state is computed per interaction rather than cached. Caching it required compare-and-swap
  ownership, pending-launch arbitration against concurrent activity, exact-session resume with
  registration stamps, transport-failure invalidation, and a filesystem-revalidating prune — all to
  keep a copy of a value that is cheap to derive.
- **Reachability gates, activity orders:** bridge registration decides candidacy and `UpdatedAt`
  ranks candidates. A more recently active session that cannot receive a prompt is never chosen.
- **No resume:** an unreachable session is not restarted to receive an interaction. If nothing is
  reachable, a SystemView launches a new session; an AgentDoc waits for its author. Without a resume
  path there is no resume failure, and therefore no reassignment UI.
- **Case-preserving filename identity:** ownership keys retain the real on-disk filename case; only
  worktree paths are normalized, so scanner lookup and pruning share one identity on
  case-sensitive hosts.
- **Kind controls behavior:** a SystemView never acquires authored-document UI or lifecycle
  behavior.
- **One launch per worktree, time-bounded, no wider arbitration:** the guard prevents Treemon from
  spawning duplicate sessions for concurrent interactions, and expires on a timer so a spawn that
  never registers cannot block later interactions. It deliberately does not arbitrate against
  sessions the user starts, which is accepted rather than defended.
- **Pinning is out of scope:** choosing a fixed session for a SystemView is a separate feature; the
  resolution rule above is the only policy today.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/CanvasDocOwnership.fs` | Persistent AgentDoc ownership store |
| `src/Server/SessionBridge.fs` | Session registry, prompt transport, queueing, and liveness |
| `src/Server/CanvasBridge.fs` | Target resolution and worktree launch coordination |
| `src/Server/WorktreeApi.fs` | Send path and launch-on-no-target behavior |
| `src/Server/CanvasDocServer.fs` | Registration and explicit ownership endpoints |
| `src/Extension/extension.mjs` | Session registration and ownership declarations |
| `src/Client/CanvasPane.fs` | Kind-gated liveness and lifecycle affordances |

## Related Specs

- `docs/spec/canvas-pane.md` — document kinds, pane behavior, and message transport
- `docs/spec/canvas-authoring-dx.md` — authored document creation and selected-text actions
- `docs/spec/worktree-diff-viewer.md` — diff SystemView behavior and structured selection context
- `docs/spec/beadspace-canvas.md` — Beadspace SystemView behavior
