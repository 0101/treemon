# App.fs Canvas View Extraction

Status: **Future / Deferred** — design only. NOT implemented on the canvas48 branch.

Parent spec: `docs/spec/canvas-pane.md`.

## Problem Statement

The canvas48 branch grew `src/Client/App.fs` well past its `main` size by adding
canvas behavior in two layers:

1. **Canvas `update` logic** — the canvas `Msg` arm bodies plus their shared helpers.
2. **Canvas view code** — Feliz rendering for canvas events and the canvas pane, plus
   canvas data threaded through the existing worktree-card view functions.

Layer 1 has been extracted: `Model`/`Msg` → `AppTypes.fs` (task `tm-canvas48-aaxl`) and the
canvas `update`-arm bodies + helpers + subscription glue → `CanvasUpdate.fs`
(task `tm-canvas48-wfgx`). Each canvas `update` arm in `App.fs` is now a one-line
delegation, and `update` is still a single function (no `Cmd.map` sub-component split).

Layer 2 was **deliberately left in `App.fs`** and is the remaining bulk. That is why
`App.fs` lands above `main` size after the update-arm extraction rather than back at
`main` size — the two were conflated in the original sizing estimate.

## Why view extraction is deferred (not done with the update arms)

- The view bulk is **interleaved with non-canvas card rendering**, so it cannot be lifted
  wholesale into a canvas module. `worktreeCard`, `renderCard`, and `repoSection` are the
  generic worktree-card views; they merely take extra `canvasEvents` / `canvasPaneOpen`
  parameters. Splitting the canvas slice out cleanly needs a small view-model or callback
  bundle, which is a design step of its own.
- It is **pure render code with no behavior change**, so it is the lowest-risk thing to
  defer and the easiest to extract later in isolation.
- Keeping the two extractions separate keeps each change reviewable: the update-arm move
  is verified structurally (each arm is a one-line delegation), independent of view churn.

## Candidate scope (what would move)

The canvas-specific view code currently in `App.fs`:

- `canvasEventEntry` / `canvasEventLog` — the per-worktree canvas event list rendering.
- `focusedWorktreeCanvasDoc` — resolves the focused worktree's active `(WorktreeStatus, CanvasDoc)`
  for the pane (uses `CanvasUpdate.activeVisibleDoc`).
- The canvas-pane block inside the top-level `view` — building `CanvasPaneCallbacks`
  (`onOverviewClick`, `onOverviewDocClick`, `archiveCanvasDoc`, `launchCanvasSession`),
  `focusedUnviewedFilenames`, and the call into `CanvasPane.view`.
- The canvas parameters threaded through `worktreeCard` / `renderCard` / `repoSection`
  (`canvasEvents: Map<string, CanvasEvent list>`, `canvasPaneOpen: bool`).

## Suggested approach

- A `CanvasView.fs` (compiled after `CanvasUpdate.fs`, before `App.fs`) holds the
  canvas-specific render functions, taking pure slices + a `dispatch`/callback bundle
  rather than the whole `Model`, mirroring the `CanvasState.fs` / `CanvasUpdate.fs`
  seam already established.
- Thread canvas data into the worktree-card views via a small record (e.g. a
  `CanvasCardSlice`) instead of loose extra parameters, so the card signatures stop
  carrying canvas-shaped arguments.
- Keep `update` and the top-level `view` wiring in `App.fs`; this remains view-body
  relocation, not an Elmish sub-component split.

## Out of scope here

- No `Cmd.map` / sub-`Msg` / sub-`update` split. The flat `Msg` and single `update`
  function are intentional (see `docs/spec/canvas-pane.md` Decisions).
- No behavior change. This is a structure-only refactor; tests should stay green with no
  edits beyond `open`/namespace adjustments.
