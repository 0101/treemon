# Canvas SystemView Interactions

## Goals

- Provide the universal selected-text Explain, Remove, and Comment actions in every SystemView.
- Route generated-view interactions to one deterministic session without assigning an authored-document owner.
- Let trusted SystemViews attach bounded structured source identity to selection messages.
- Preserve existing SystemView behavior: no author liveness, Start-session button, archive, share, content awareness, or DOM morphing.

## Expected Behavior

- Selecting ordinary non-editable text in either an AgentDoc or SystemView shows the same contextual action toolbar and processing indicator.
- AgentDoc messages continue routing through `CanvasDoc.OwnerSessionId`.
- Each `(worktree, SystemView filename)` has a separate persistent interaction-session owner. The first explicitly started or claimed session becomes the target; subsequent interactions resume or route only to that session until ownership is reassigned or the view/worktree is removed.
- A SystemView may provide optional selection metadata. Valid metadata is sent as nested `sourceContext`, separate from the human-readable `request` and selected text.
- The diff view supplies `kind = "diff"`, file identity, hunk header, and old/new line ranges. Beadspace supplies `kind = "beads"` and the selected task ID.
- Missing metadata is valid. Invalid, non-serializable, oversized, or exception-producing metadata blocks the send and displays an error in the selection toolbar.

## Technical Approach

`CanvasDocServer.buildInjection` includes `CanvasSendScript` and `CanvasSelectionScript` for both document kinds. SystemViews continue to omit heartbeat/author bridge, error/morph authoring machinery, and idiomorph.

Interaction ownership is stored separately from `CanvasDocOwnership` because generated views have no author. Message routing resolves authored ownership for AgentDocs and interaction ownership for SystemViews. An unclaimed SystemView interaction is queued while Treemon starts or continues a session for that view; the session claims the interaction target before the queued message is delivered. Ownership persists across restarts and is removed with the view or worktree.

The selection runtime calls an optional `window.canvasSelectionMetadata` hook with the captured selection context. The hook may return a plain JSON object only. The runtime nests the validated result under `sourceContext`, includes it in the existing 64,000-code-unit payload limit, and prevents it from overriding reserved message fields. Source metadata remains data and is never interpolated into `request`.

## Decisions

- SystemView interaction ownership is distinct from authored-file ownership so generated views do not acquire incorrect liveness, archive, share, or morph behavior.
- Ownership persists until explicit reassignment or view/worktree removal; session termination alone does not release it.
- All SystemViews receive the generic runtime. View-specific behavior is limited to structured metadata enrichment.
- Metadata is nested and non-instructional to preserve the existing prompt-injection boundary between selected content and the user's action request.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/CanvasDocServer.fs` | Runtime injection by canvas document kind |
| `src/Extension/canvas-selection-context.js` | Generic selection toolbar, metadata hook, and payload construction |
| `src/Server/CanvasInteractionOwnership.fs` | Persistent generated-view interaction owner |
| `src/Server/CanvasBridge.fs` | Owner-aware delivery and queue draining |
| `src/Server/WorktreeApi.fs` | Resume/start behavior for unclaimed or offline interaction owners |
| `src/Server/BeadspaceTemplate.html` | Beadspace task-ID metadata provider |
| `src/Server/DiffTemplate.html` | Diff file/hunk/line metadata provider |

## Related Specs

- `docs/spec/canvas-pane.md` — document kinds, pane behavior, and message flow
- `docs/spec/canvas-doc-ownership.md` — authored AgentDoc ownership contrasted with generated-view interaction ownership
- `docs/spec/beadspace-canvas.md` — Beadspace SystemView consumer
- `docs/spec/worktree-diff-viewer.md` — diff SystemView consumer
