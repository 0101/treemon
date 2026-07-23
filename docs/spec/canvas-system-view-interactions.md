# Canvas SystemView Interactions

## Goals

- Provide the universal selected-text Explain, Remove, and Comment actions in every SystemView.
- Route generated-view interactions to one deterministic session without assigning an authored-document owner.
- Keep diff interactions aligned with the worktree session that most recently performed real activity.
- Let trusted SystemViews attach bounded structured source identity to selection messages.
- Preserve existing SystemView behavior: no author liveness, Start-session button, archive, share, content awareness, or DOM morphing.

## Expected Behavior

- Selecting ordinary non-editable text in either an AgentDoc or SystemView shows the same contextual action toolbar and processing indicator.
- AgentDoc messages continue routing through `CanvasDoc.OwnerSessionId`.
- Each `(worktree, SystemView filename)` has a separate persistent interaction-session owner.
- `diff.html` automatically follows the worktree session with the newest real-activity timestamp. Heartbeats and usage gauges update liveness only and never transfer the diff target.
- Other SystemViews keep sticky affinity: the first explicitly started or claimed session remains the target until ownership is reassigned or the view/worktree is removed.
- An explicit pending initial claim or reassignment takes precedence over automatic diff following. The previous durable owner remains unchanged until that flow completes or is cancelled.
- Resuming an offline interaction owner is successful only after that exact session re-registers its bridge. A spawn error or registration timeout is shown as a hard failure with an explicit **Start fresh and reassign** action; it is never left as a silent queued wait.
- Starting fresh preserves the old durable owner while the replacement launches, suspends queue delivery to that old owner, then atomically changes the owner and drains to the new identified session when it registers. Launch failure or timeout cancels the pending reassignment, so retrying still targets the old owner.
- A SystemView may provide optional selection metadata. Valid metadata is sent as nested `sourceContext`, separate from the human-readable `request` and selected text.
- The diff view supplies `kind = "diff"`, file identity, hunk header, and old/new line ranges. Beadspace supplies `kind = "beads"` and the selected task ID.
- Missing metadata is valid. Invalid, non-serializable, oversized, or exception-producing metadata blocks the send and displays an error in the selection toolbar.
- A standalone top-level SystemView has no parent transport: its selection toolbar stays visible, displays a messaging-unavailable error, and never starts the processing indicator.

## Technical Approach

`CanvasDocServer.buildInjection` includes `CanvasSendScript` and `CanvasSelectionScript` for both document kinds. SystemViews continue to omit heartbeat/author bridge, error/morph authoring machinery, and idiomorph.

Interaction ownership is stored separately from `CanvasDocOwnership` because generated views have no author. Message routing resolves authored ownership for AgentDocs and interaction ownership for SystemViews. An unclaimed SystemView interaction is queued while Treemon starts or continues a session for that view; the session claims the interaction target before the queued message is delivered. Ownership persists across restarts and is removed with the view or worktree.

The session-activity service compares the per-worktree winner before and after every ingested report.
The winner is ordered by `StoredStatus.UpdatedAt`, the same real-activity clock used by resume and
footer selection; `LastSeen` is excluded because bridge and activity heartbeats advance it. When the
winner changes, `diff.html` is assigned to that session unless an explicit claim or reassignment is
pending. Restart seeding performs the same reconciliation from the restored live statuses.

For an owned SystemView whose bridge is offline, Treemon resumes the persisted owner and waits for
a newer registration from that exact session. Spawn failure or registration timeout returns a
recoverable owner-unavailable result to the pane. The user-approved start-fresh path records an
in-memory reassignment claim without deleting the durable owner; the identified session registering
with that reassignment's launch token atomically replaces and persists the owner before queue
draining. Cancelling or timing out the
claim leaves the previous owner unchanged. While the reassignment claim is pending, owner resolution
for delivery returns no target so a late heartbeat from the old session cannot consume the queued
interaction before the replacement claims it.

The pending claim is in-memory and keyed by normalized worktree plus SystemView filename, with an
opaque launch token; the durable owner is persisted in `data/canvas-interaction-owners.json`.
Treemon passes that token only to the deliberately launched or continued session, whose bridge
returns it in `/api/canvas/register`. Registration atomically claims only the matching pending view
before queue draining. Tokenless periodic heartbeat registrations cannot steal a pending claim, and
concurrent launches for `diff.html` and `beads.html` remain independently correlated. SystemView
queue entries re-resolve
the current interaction owner when drained, never drain anonymously while unclaimed, and therefore
honor an explicit reassignment made after enqueue. Scheduler reconciliation prunes owners whose
worktree is no longer known or whose generated view file no longer exists. Explicit worktree-removal
cleanup and scheduler-state removal run only after Git removal succeeds, so a failed removal attempt
remains known to reconciliation and preserves ownership.

Ownership pruning snapshots filesystem existence once for the union of durable-owner and pending
view keys at the mailbox's async boundary. Pure transformations then apply that immutable snapshot
to both maps, so duplicate keys observe one consistent existence result.

`POST /api/canvas/attribute` and the existing `canvas_take_ownership` tool dispatch by document
kind: AgentDocs assign author ownership, while SystemViews explicitly assign or reassign the
interaction owner. SystemView ownership is not surfaced through `CanvasDoc.OwnerSessionId`.

The selection runtime calls an optional `window.canvasSelectionMetadata` hook with the captured selection context. The hook may return a plain JSON object only. The runtime nests the validated result under `sourceContext`, includes it in the existing 64,000-code-unit payload limit, and prevents it from overriding reserved message fields. Source metadata remains data and is never interpolated into `request`.

The shared transport treats `window.parent === window` as unavailable unless a top-level host explicitly advertises its forwarding shim through `window.__canvasTopLevelTransportAvailable`. The browser extension sets that capability before installing the canonical runtimes; direct standalone SystemViews do not.

Full-stack verification waits for each generated view's own render completion before selecting text:
the diff highlighter must reach `plain`, `ready`, or `failed`, while Beadspace must finish its initial data
render. The target must remain connected and unchanged across animation frames. Programmatic
selection explicitly emits `selectionchange`, and readiness/selection failures retain frame,
target, toolbar, and render-state diagnostics.

## Decisions

- SystemView interaction ownership is distinct from authored-file ownership so generated views do not acquire incorrect liveness, archive, share, or morph behavior.
- Beadspace and future sticky SystemViews preserve ownership until explicit reassignment or view/worktree removal; session termination alone does not release it.
- Diff interaction affinity follows the most-recently-active session, using real activity rather than liveness heartbeats. A manual diff assignment remains in effect until another session becomes the real-activity winner.
- Explicit claim/reassignment flows win over automatic diff following. Outside that pending window, an unavailable diff owner may be replaced only when another session produces newer real activity; otherwise the pane surfaces the failure and offers **Start fresh and reassign**.
- Reassignment is committed only by a new identified bridge registration. Until then the old durable owner remains authoritative, so failed launches and registration timeouts are safe to retry.
- The session deliberately started or continued for an unclaimed interaction receives an opaque
  launch token and claims only that token's view before draining; tokenless heartbeat refreshes
  cannot claim it, and later registrations cannot overwrite it.
- Explicit assignment uses the existing ownership endpoint/tool, but dispatches to the separate
  interaction store for SystemViews.
- All SystemViews receive the generic runtime. View-specific behavior is limited to structured metadata enrichment.
- Metadata is nested and non-instructional to preserve the existing prompt-injection boundary between selected content and the user's action request.
- Top-level transport is capability-based so browser-extension forwarding remains supported without presenting false success in direct standalone tabs.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/CanvasDocServer.fs` | Runtime injection by canvas document kind |
| `src/Extension/canvas-selection-context.js` | Generic selection toolbar, metadata hook, and payload construction |
| `src/Server/CanvasInteractionOwnership.fs` | Persistent generated-view interaction owner |
| `src/Server/CanvasBridge.fs` | Owner-aware delivery and queue draining |
| `src/Server/SessionActivityService.fs` | Automatic diff-owner updates from real session activity |
| `src/Server/WorktreeApi.fs` | Resume/start behavior for unclaimed or offline interaction owners |
| `src/Server/BeadspaceTemplate.html` | Beadspace task-ID metadata provider |
| `src/Server/DiffTemplate.html` | Diff file/hunk/line metadata provider |

## Related Specs

- `docs/spec/canvas-pane.md` — document kinds, pane behavior, and message flow
- `docs/spec/canvas-doc-ownership.md` — authored AgentDoc ownership contrasted with generated-view interaction ownership
- `docs/spec/beadspace-canvas.md` — Beadspace SystemView consumer
- `docs/spec/worktree-diff-viewer.md` — diff SystemView consumer
