# App.fs View Extraction

Status: **Future / Deferred** — design only. NOT implemented on the canvas48 branch.

Parent spec: `docs/spec/canvas-pane.md`.

## Why this doc exists

`src/Client/App.fs` is large, and there are **two independent** view-extraction
opportunities that are easy to conflate. This note separates them so future work
attributes each one correctly — and so that neither is treated as a blocker for the
canvas48 merge.

It also **preserves the durable App.fs note** that lived as item #2 in
`docs/spec/future/canvas-bridge-pre-existing-fixes.md`, which is being retired (task
`tm-canvas48-wld1`). See *Provenance* below — nothing durable is lost by that deletion.

## Sizing (verified)

| Measurement (`src/Client/App.fs`) | `main` | canvas48 head |
|-----------------------------------|-------:|--------------:|
| Total lines                       |   1489 |          1685 |
| `canvas` references               |  **0** |        ~200   |

The key fact: **most of App.fs's bulk predates this branch and is unrelated to canvas.**
On `main` the file is already ~1489 lines with **zero** canvas references.

## Opportunity A — Pre-existing view bulk (already on `main`; NOT this branch's growth)

**This is the primary point of this document.**

On `main`, App.fs is dominated by three pure-render view families that have **nothing to
do with canvas** and predate the canvas work. Approximate sizes on `main`:

- **Worktree-card views — "CardView", ~650 lines.**
  `compactWorktreeCard`, `worktreeCard`, `renderCard`, plus their button/badge/section
  helpers: `terminalButton`, `editorButton`, `resumeButton`, `deleteButton`,
  `archiveButton`, `prActionButton`, `prBadgeContent`, `prSection`, `prRow`, `syncButton`,
  `mainBehindWithSync`, `buildBadge`, `beadsProgressBar`, `eventLogEntry`, `eventLog`.
- **Mascot eye views — "MascotView", ~190 lines.**
  `viewEyeOpen` / `viewEyeRolledBack` / `viewEyeClosed` (the ~150-line trio at
  `App.fs:1254–1401` on `main`) plus `randomEyeDirection` and the `computeActivityLevel`
  plumbing that drives the eyes.
- **Status-overview / scheduler-footer views — "OverviewView", ~100 lines.**
  `statusOverviewRow`, `pinnedErrorEntry`, `schedulerFooter`, plus the path-prefix helpers
  they depend on (`knownCategories`, `categoryDisplayName`, `commonPathPrefix`,
  `stripPrefix`).

Together these are **~900+ lines** of pre-existing view code. They:

- carry **zero** canvas references,
- exist verbatim on `main`,
- are therefore **not part of canvas48's growth** and must not be attributed to it
  (canvas refs in App.fs: **0 on `main`** → ~200 on head — all of that delta is the
  canvas feature, none of it is these families).

They are the **largest** App.fs extraction opportunity, but they are **deferred**: they are
orthogonal to the canvas feature, they are pure render code, and extracting them on this
branch would balloon the diff for no functional gain.

### Suggested approach (Opportunity A)

- Lift each family into its own client module, compiled before `App.fs`
  (e.g. `CardViews.fs`, `MascotView.fs`, `OverviewViews.fs`).
- Each function takes pure slices + `dispatch` and returns `ReactElement`; `App.fs` keeps
  only the top-level `view` wiring.
- Purely structural; tests should stay green with no edits beyond `open`/namespace
  adjustments.

## Opportunity B — This branch's canvas view layer (layer 2)

The canvas48 branch grew App.fs in two layers:

1. **Canvas `update` logic** (the canvas `Msg` arm bodies + shared helpers + subscription
   glue) — **already extracted**: `Model`/`Msg` → `AppTypes.fs` (task `tm-canvas48-aaxl`),
   and the canvas `update`-arm bodies/helpers → `CanvasUpdate.fs` (task `tm-canvas48-wfgx`),
   with canvas state in `CanvasState.fs`. Each canvas `update` arm in `App.fs` is now a
   one-line delegation, and `update` remains a single function (no `Cmd.map` split).
2. **Canvas view code** — **deliberately left in `App.fs`** and the remaining canvas bulk.
   This is why App.fs lands above `main` size after the update-arm extraction rather than
   back at `main` size: the two layers were conflated in the original sizing estimate.

Candidate scope (what would move):

- `canvasEventEntry` / `canvasEventLog` — per-worktree canvas event list rendering.
- `focusedWorktreeCanvasDoc` — resolves the focused worktree's active
  `(WorktreeStatus, CanvasDoc)` for the pane (uses `CanvasUpdate.activeVisibleDoc`).
- The canvas-pane block inside the top-level `view` — building `CanvasPaneCallbacks`
  (`onOverviewClick`, `onOverviewDocClick`, `archiveCanvasDoc`, `launchCanvasSession`),
  `focusedUnviewedFilenames`, and the call into `CanvasPane.view`.
- The canvas parameters threaded through `worktreeCard` / `renderCard` / `repoSection`
  (`canvasEvents: Map<string, CanvasEvent list>`, `canvasPaneOpen: bool`).

Why deferred (not done with the update arms):

- The canvas view bulk is **interleaved with the (pre-existing) card views** from
  Opportunity A, so it cannot be lifted wholesale into a canvas module. `worktreeCard`,
  `renderCard`, and `repoSection` are the generic card views; they merely take extra
  `canvasEvents` / `canvasPaneOpen` parameters. A clean split needs a small view-model or
  callback bundle — a design step of its own.
- It is **pure render code with no behavior change**, so it is the lowest-risk thing to
  defer and the easiest to extract later in isolation.
- Keeping the two extractions separate keeps each change reviewable: the update-arm move is
  verified structurally (each arm is a one-line delegation), independent of view churn.

### Suggested approach (Opportunity B)

- A `CanvasView.fs` (compiled after `CanvasUpdate.fs`, before `App.fs`) holds the
  canvas-specific render functions, taking pure slices + a `dispatch`/callback bundle rather
  than the whole `Model`, mirroring the `CanvasState.fs` / `CanvasUpdate.fs` seam already
  established.
- Thread canvas data into the card views via a small record (e.g. a `CanvasCardSlice`)
  instead of loose extra parameters, so the card signatures stop carrying canvas-shaped
  arguments.
- Keep `update` and the top-level `view` wiring in `App.fs`; this remains view-body
  relocation, not an Elmish sub-component split.

> Note: doing **B alone barely dents App.fs size** — most of the bulk is **Opportunity A**.
> Meaningful size reduction comes from extracting the pre-existing card/mascot/overview
> views, not from the canvas slice.

## Out of scope (both opportunities)

- No `Cmd.map` / sub-`Msg` / sub-`update` split. The flat `Msg` and single `update` function
  are intentional (see `docs/spec/canvas-pane.md` Decisions).
- No behavior change. These are structure-only refactors; tests should stay green with no
  edits beyond `open`/namespace adjustments.

## Provenance

This document supersedes the App.fs note (**item #2, "App.fs has grown past the file-size
limit"**) from `docs/spec/future/canvas-bridge-pre-existing-fixes.md` (retired by task
`tm-canvas48-wld1`). That note
attributed the size to "~162 canvas references" mixed into App.fs. That framing is corrected
here: on `main`, App.fs has **0** canvas references — the size is **pre-existing view code**
(Opportunity A), and the canvas additions (Opportunity B) are a separate, smaller layer whose
non-view portion has already been extracted. No durable content is lost by deleting that doc.
