# Canvas Interaction Routing

## Goals

- Route every canvas interaction to one session, chosen by document kind.
- Preserve exact AgentDoc author routing: an authored document always reaches its author.
- Let a SystemView choose the worktree's activity recipient per interaction without persisted
  ownership or affinity.
- Queue an interaction when no session can receive it, and start at most one session per worktree
  to drain it.

## Expected Behavior

### Bridge Identity and Scope

One current bridge is keyed by a required, validated durable Copilot `SessionId`. Every valid
registration or heartbeat replaces that session's entry, including a later arrival from an older
physical instance. Receipt timestamps and installation are atomic; server receipt time supplies
the 60-second reachability window. Expiry is temporary loss of reachability, not retirement.

The required worktree is normalized and validated against monitored worktrees. Delivery and owner
liveness both require the bridge's registered worktree to equal the source document's worktree.
A session moving to another monitored worktree replaces its only entry: its original documents
remain owned but become offline and queued until it returns.

Parent PID/start identity and terminal origin are optional location hints, never bridge identity
keys or canvas-delivery vetoes. Missing or unverifiable hints leave canvas routing valid. Generic
prompts require verified process metadata; exact prompts and shutdown additionally carry the
expected durable session ID and exact process identity. A latest bridge in another process is not
an exact-process fallback.

### Target Resolution

Resolution depends only on `CanvasDoc.Kind`.

An **AgentDoc** has a real author. Its `(worktree, filename)` target is persisted in
`data/canvas-owners.json`, assigned when the authoring extension reports a successful canvas write
or when `canvas_take_ownership` claims it explicitly. Ownership is sticky across registration,
conversation switches, expiry, and restart: it changes only through another successful author write
or explicit claim. Both paths require the bare filename to
match the shared canvas filename contract; a full path, separator, traversal attempt, space, quote,
or control character is rejected before ownership state is touched.

The authoring extension observes successful create/edit/apply-patch destinations only in its startup
`.agents/canvas` folder. Filename-only claims also stay in that startup worktree, even after the
agent changes directory. Cross-worktree write attribution and claims are not supported. Scanner
fallback remains change-gated: it fills only unowned AgentDocs when exactly one local durable
session is reachable, never replacing an explicit owner.

A **SystemView** is server-generated and has no author. Each interaction chooses the greatest
activity among open instances in the source worktree, even when that recipient's bridge is
temporarily absent. Without open activity, it falls back to the freshest local live bridge.
`UpdatedAt` orders activity; heartbeat, usage, and `LastSeen` never do. `BridgeLiveness` exposes
this computed recipient for presentation, including bridge gaps; it is not stored view affinity.

A SystemView's owner is absent from
`CanvasDoc.OwnerSessionId`, so liveness, Start session, archive, share, awareness, heartbeat, and
morph behavior continue to depend only on `CanvasDoc.Kind`.

### Delivery and Session Startup

Before target resolution, the server validates that the payload is a JSON object with a nonblank
string `action` and no more than 64,000 UTF-16 code units. Invalid input returns an error without
entering a delivery lane or starting a fallback session. A resolved live target receives a valid
payload immediately. Otherwise the interaction is queued.

An open SystemView recipient without an eligible bridge gets three seconds of registration grace,
using the same bounded seam as AutoSync. Registration during that gap drains the queue without
launching. If the recipient remains unavailable, or there is no recipient, Treemon starts an
embedded session for the source worktree.

Fallback interactions retain the exact terminal ID returned by that launch. Each unsent dispatch
joins that terminal to its current open activity session in the same worktree, then resolves that
session's latest eligible bridge. An unrelated registration cannot capture the queue. Launch groups
are ephemeral queue targeting, not persisted SystemView affinity.

The background launch does not open or retarget
the terminal pane; registry polling makes it attachable later. A started launch suppresses another
spawn for the same worktree for 30 seconds; the suppression **expires on time** rather than waiting
to be cleared by a registration, so a spawn that never registers cannot block later interactions,
and an unrelated session's periodic heartbeat cannot be mistaken for the launch completing. A
spawn that fails cancels only its triggering queued interaction before releasing suppression, so
retrying cannot deliver both the failed request and its retry. Unrelated queued interactions are
retained. Startup and this rollback finish even if the requesting browser disconnects. Sessions the
user starts concurrently are not arbitrated — the guard covers only Treemon's own spawns.

Spawn suppression is not an attachment lifetime. An active coordinator retains its own launch
start and outcome until completion or cancellation, so live followers remain attached during the
embedded launch's 150-second reply budget even after the 30-second cooldown expires. Completion
and cancellation affect only that launch's group, not a replacement spawn. Once coordination
finishes, a new interaction evaluates the cooldown normally; an expired completed launch has no
special attachment affinity.

An AgentDoc interaction with no reachable author is queued without launching, because a new session
would not be that document's author.

Explicit AgentDoc Start and automatic SystemView startup use the shared prompted-launch handshake
in `docs/spec/embedded-terminal.md`: the complete initial instruction is reserved for the exact
fresh session, delivered through its extension, and accepted before already-queued interactions.
This transport does not assign an AgentDoc owner or change SystemView target resolution.

Ordinary queued messages retain the cap of 10 and five-minute TTL, measured from original enqueue
time. Startup reservations are separate from those limits and remain until their prompted-launch
handshake settles. One delivery lane per normalized worktree serializes immediate sends and drains.
Before every unsent HTTP dispatch, routing rereads current document ownership, the latest session
bridge, worktree eligibility, and message age. A durable claim, endpoint replacement, or
registered-worktree change between two sends affects the second; already-issued HTTP requests need
not be recalled. After asynchronous target lookup, an atomic take must still find the queued item
within its original TTL; an expired or evicted item cannot issue HTTP. Heartbeats share one active
drain and one coalesced notification per worktree. Routing changes restart ordered lookup, while an
exact-terminal successor cannot overtake an older eligible message.

Failed queued messages retain age and relative order. They do not immediately loop against an
unchanged failed endpoint; a later valid registration after the failure can retry it, while a
different endpoint can recover sooner. Failure and expiry remove only the observed registration,
never its replacement. AgentDoc delivery always rereads the recorded owner. A SystemView selected
recipient remains stable while queued; when Treemon starts a session, retained interactions bind to
that launch's exact terminal. Anonymous registrations never drain either kind. Document heartbeat
polls are liveness-only and return no queued prompts.

### Persistence and Cleanup

The existing nested worktree/filename owner-file format is unchanged. Atomic persistence through
`JsonStore.tryPersist` must succeed before a proposed map becomes authoritative in memory or a
claim is acknowledged. Failure is explicit, preserves the previous map and disk file, and permits
the identical claim to retry. Successful declarations wake queued delivery only after that durable
acknowledgement. Removal and pruning follow the same commit rule; failed scanner attribution remains
eligible for retry rather than advancing its change baseline.

Only AgentDocs use stored ownership for routing. It is removed when a view or worktree disappears; scheduler
reconciliation prunes entries for worktrees that are no longer known **and** for documents whose file
is gone, which is the only path that reclaims a per-document entry.

### Authoritative Source Coordinates

The client captures the visible active iframe's worktree and filename when its message arrives,
validates that inventory identity, and carries it through `CanvasMessageRequest`. Authored `doc`,
`filename`, or `source` payload fields cannot select another document; a later tab selection cannot
replace the captured source.

After monitored-path, safe-filename, and payload validation, HTTP transport carries
`{kind:"canvas",prompt:<authored JSON>,source:{worktreePath,filename}}`. The Node parser produces
`[canvas] {source,payload,authoringReminder?}` for `session.send`, with escaped JSON data separate
from authored payload. Browser fallback additionally binds each served document to an unguessable
capability route, so same-origin authored script cannot claim a different filename as its source.
The authoritative filename controls edit reminders. Footer formatting reads
the nested payload while retaining the `[canvas]` glyph and historical flat-message formatting.

### Selection Metadata

AgentDocs and SystemViews use the same injected selection runtime and `canvasSend` transport.
SystemViews may return bounded plain-JSON metadata from `window.canvasSelectionMetadata`; the
runtime nests it under `sourceContext`. Diff selections identify the file, hunk, and old/new line
ranges. Beadspace selections identify the task. The runtime clones the metadata before sending and
rejects non-plain objects, cycles, sparse arrays, symbol keys, non-finite numbers, functions, and
other values that do not have a stable JSON representation. Validation also rejects metadata whose
canonical JSON representation exceeds 64,000 UTF-16 code units or whose nesting exceeds 64 levels,
before allocating an unbounded clone.

## Technical Approach

`CanvasDocOwnership` is the mailbox-backed store for AgentDoc ownership, providing assignment,
lookup, removal, and pruning. `SessionBridge` owns the session-keyed current bridge map, separate poll
map, bounded transport queue, worktree delivery lanes, and reachability. Exact operations verify
optional location metadata without changing the canvas identity model.
`CanvasBridge` layers target resolution and worktree launch policy over that generic transport, and
delegates a required spawn to the shared prompted-launch boundary.

`CanvasBridge.resolveTarget` branches on `CanvasDocKinds.classify`: an AgentDoc reads
`CanvasDocOwnership`, while a SystemView takes the durable session IDs from the worktree's
canvas-collapsed live registrations, orders their exact rows from
`SchedulerState.SessionInstances` by `StoredInstance.activityOrderKey`, and falls back to the
freshest live registration when no reachable session has an activity row.
The same captured live-registration list and resolution populate `BridgeLiveness` for canvas tabs;
the target is absent when no live registration can receive a SystemView interaction.
`CanvasBridge.sendMessage` returns a routing outcome so the caller can
distinguish "queued because nothing is reachable" from "queued behind a known session".

The existing launch guard retains its start time and returned terminal ID. Its start time groups
only pending fallback messages, so a delayed completion or cancellation cannot retarget a newer
launch group after suppression expires. There is no bridge lease, generation, retirement history,
duplicate-source registry, or first-seen preference.
Reservation rechecks the same launch inside the mailbox turn that serializes completion and
cancellation, binding a completed recipient or releasing a cancelled group before replying.
Only one registration-grace/fallback workflow coordinates each worktree; additional queued
interactions join an existing launch before returning promptly. Completion, cancellation, and
follower admission share mailbox ordering. The coordinator rechecks the bounded pending work
before finishing, preserving each interaction's independently selected recipient and its own grace
opportunity rather than substituting the originator. The originating request still reports a
launch failure.
Handled work is tracked by live queued message identity, not by recipient. An earlier unassigned
interaction does not make a newly unadmitted unassigned follower handled; retained metadata is
bounded by the current queue.

Activity remains process-keyed: acknowledged sequential A-to-B switches supersede the prior active
binding while preserving durable history and terminal projection. Concurrent conversations within
one exact CLI process and conversation shutdown barriers are not part of this model.

`CanvasScanner` continues exposing `OwnerSessionId` only for AgentDocs, and the client continues
gating every lifecycle affordance on `CanvasDoc.Kind`.

## Decisions

- **Resolve SystemViews, store AgentDocs:** generated-view recipients are cheap to derive from
  current activity and reachability; authored ownership is a durable user-facing contract.
- **Latest valid arrival wins:** durable session identity is sufficient; even an older physical
  instance may become current again. Simultaneous instances of one session may alternate endpoints.
- **Activity chooses, reachability gates dispatch:** a bridge gap waits briefly rather than
  silently substituting a quieter co-located session.
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
- **No persisted SystemView pinning:** exact fallback terminal targeting lasts only as long as the
  queued interaction; the next interaction chooses anew.

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
- `docs/spec/worktree-diff-viewer.md` — diff SystemView behavior and structured selection context
- `docs/spec/beadspace-canvas.md` — Beadspace SystemView behavior
