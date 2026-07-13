# Canvas Overview Unread Highlighting

## Goals

- On the canvas overview page (shown when no card is focused), make it visually obvious **which**
  docs are unread — the same docs the Canvas button badge counts.
- Reuse the badge's own signal so the overview highlight and the badge count can never disagree.
- Keep the existing per-doc liveness dot (session-alive) as a separate, orthogonal signal.

## Expected Behavior

- Each doc listed in the overview whose content is **unviewed** (its `ContentHash` differs from the
  last-viewed hash for that filename, or it was never viewed) renders in **pure white `#fff`,
  normal weight**. Already-viewed docs keep the current muted color (`#6c7086`).
- The set of highlighted docs is exactly the set that increments the Canvas button badge
  (`unviewedDocsByScopedKey`). Opening/viewing a doc that clears its badge contribution also clears
  its overview highlight on the next render.
- `SystemView` docs (the beads dashboard) are never highlighted — they are excluded from all
  content-hash awareness, consistent with the badge and with the liveness-dot omission.
- The green liveness dot is unchanged: it still marks docs whose owner session is alive, occupying
  a separate visual channel (leading dot) from the unread text color.

## Technical Approach

Today the overview (`overviewView`, `src/Client/CanvasPane.fs`) receives `repos` and
`bridgeLiveness` but no unread data. The pane's existing `unviewedFilenames: Set<string>` param is
scoped to the *focused* card (`CanvasView.fs`), which is empty exactly when the overview renders
(`focusedDoc = None`). So the overview currently cannot mark unread docs.

Thread the global unviewed map — the same one the badge uses — into the overview:

1. **`src/Client/CanvasView.fs`** — compute
   `unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes` once and pass it into
   `CanvasPane.view` (as `Map<string, Set<string>>` keyed by scopedKey for O(1) per-doc lookup).
   Prefer deriving the existing focused-scoped `unviewedFilenames` from this same map (via the
   focused worktree's path) so there is a single unviewed input rather than two.
2. **`src/Client/CanvasPane.fs`** — thread the map through `CanvasPane.view` into `overviewView`.
   For each doc, add the CSS class `canvas-overview-doc-unviewed` when the doc's filename is in the
   map's set for that worktree's scopedKey. Leave `livenessDotFor` untouched.
3. **`src/Client/index.html`** — add `.canvas-overview-doc-unviewed { color: #fff; }` near the
   existing `.canvas-overview-doc` rules.

The source of truth (`unviewedDocsByScopedKey`, `src/Client/CanvasAwareness.fs`) already excludes
`SystemView` docs via `awarenessDocs`, so no extra guard is needed.

## Key Files

- `src/Client/CanvasAwareness.fs` — `unviewedDocsByScopedKey` (reused as-is; source of truth).
- `src/Client/CanvasView.fs` — computes awareness, passes state into the pane.
- `src/Client/CanvasPane.fs` — `overviewView` render + `CanvasPane.view` signature.
- `src/Client/index.html` — `.canvas-overview-doc` CSS.
- `src/Tests/CanvasPaneTests.fs` — existing overview tests (`~487-584`, liveness-dot `714-732`).

## Related Specs

- `docs/spec/canvas-pane.md` — overall canvas pane, overview, and content-hash awareness.
