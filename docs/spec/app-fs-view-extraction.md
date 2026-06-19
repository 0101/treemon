# App.fs View Extraction

Status: **Active** — design decided, ready for execution. Supersedes
`docs/spec/future/app-fs-view-extraction.md` (kept for provenance/sizing history).

## Goals

- Shrink `src/Client/App.fs` (currently **1685 lines**) by relocating its pure-render
  view families into focused modules — **and make the architecture strictly better**, not
  merely shorter.
- Replace the loose-parameter smell in the card views (`repoSection` takes **9 positional
  args**) with explicit data/callback records.
- Promote the one feature that genuinely owns cohesive state + behavior (the mascot eyes)
  into a proper vertical slice, mirroring the established canvas seam.
- Zero behavior change. The existing test/E2E suite stays green at every step; tests assert
  on CSS classes and DOM structure, so identical render output proves correctness.

## Decisions

These were chosen deliberately (see *Per-family evidence* for why):

1. **Evidence-driven hybrid, not one-size-fits-all.** Vertical-slice only where a feature
   owns separable state+behavior (Mascot); props/callback records for render-over-shared-state
   (Cards); plain pure-view extraction where there is no owned state at all (Overview).
   Forcing sub-states onto Cards/Overview would invent leaky boundaries.
2. **Cards get `CardViewProps` + `CardCallbacks` records**, mirroring `CanvasPaneCallbacks`.
   This is the central quality win: it kills the 9-arg signature, makes card data flow
   explicit, and lets canvas slices ride along (which unblocks decision 5).
3. **Mascot becomes a vertical slice**: `MascotState` sub-state + `MascotView` + `MascotUpdate`,
   exactly like `CanvasState`/`CanvasUpdate`/`CanvasPane`. `Tick`/`UserActivity` arm *bodies*
   delegate to `MascotUpdate`; the arms stay in the root `update`.
4. **Flat `Msg` + single `update` are preserved.** No nested sub-`Msg`, no `Cmd.map`
   sub-component split. Arm bodies move into `<Feature>Update` modules; the `match` stays in
   `App.fs`. This is consistent with the existing canvas decision (see `AppTypes.fs` header).
5. **Canvas card-view (Opportunity B) is included now.** Once cards take a props record, the
   canvas-pane wiring lifts cleanly into `CanvasView.fs` in the same pass instead of
   re-touching card signatures later.
6. **Sequencing**: Overview → Cards → Mascot → CanvasView. Overview first because it is the
   smallest and proves the extraction seam; each step keeps the build and tests green.
7. **Shared status/time formatters live in `Components.fs`, not the view modules.**
   `stepStatusClassName`, `stepStatusText`, and `relativeEventTime` are pure, Shared-only
   formatters consumed by *both* the overview rows (Step 1) and the card / event-log views
   (Steps 3–4). Because `OverviewViews.fs` compiles before `AppTypes` (and before
   `CardViews.fs`), it cannot reach them where they previously sat in `App.fs`. Per the
   "reuse, don't duplicate — check `Components.fs`" guidance, they were relocated into
   `Components.fs` (which already hosts the sibling `relativeTime` / `cardTitle` formatters).
   `App.fs` keeps thin `let x = Components.x` re-export aliases — the existing pattern it
   already uses for `relativeTime`, `workMetricsView`, `cardTitle`, etc. — so card-side call
   sites stay untouched and there is a single source of truth.

### Leaf helpers take `CardCallbacks`, not `dispatch` (Step 2 implementation)

To keep `CardViewProps`/`CardCallbacks` **Msg-free** (so they relocate ahead of `AppTypes` with
the card views in Steps 3–4), the card **leaf** helpers (`terminalButton`, `editorButton`,
`syncButton`, `eventLog`, `canvasEventEntry`, the PR badge/section helpers, etc.) were converted
from taking raw `dispatch` to taking the whole `callbacks: CardCallbacks` record — a 1:1 swap of a
single capability handle, but a strictly narrower one (it can only raise named card actions, not an
arbitrary `Msg`). The composite views (`compactWorktreeCard`/`worktreeCard`/`renderCard`/
`repoSection`) then hold no `dispatch` at all. Consequences, all behavior-preserving:
- `terminalButton`'s `FocusSession`-vs-`OpenTerminal` choice moved into the `OpenTerminal` callback
  lambda built in `view`; the button keeps only its title text.
- The `archiveSection dispatch` wrapper was removed; `repoSection` calls
  `ArchiveViews.archiveSection callbacks.DispatchArchive` directly.
- Pre-existing dead args were dropped: `renderCard`'s `repoId` and `worktreeCard`'s `canvasPaneOpen`
  (the model bool is still carried in `CardViewProps.CanvasPaneOpen` to preserve the 8-field shape).

This makes Step 3 (leaf relocation) a pure file move with no further signature changes.

### Why a vertical slice for Mascot but not Cards

| Family (~size) | State owned | Msg / update owned | Disposition |
|---|---|---|---|
| **Cards** (~650 ln) | none exclusive — reads 8 shared fields (`EditorName`, `IsCompact`, `FocusedElement`, `BranchEvents`, `SyncPending`, `ActionCooldowns`, `Canvas.CanvasEvents`, `Canvas.CanvasPaneOpen`) | ~19 update arms, but they are **core app behavior** (sync/delete/archive/launch/resume); focus + keyboard nav (`FocusedElement`/`KeyPressed`) is **shared** with canvas & overview | **Pure view + `CardViewProps`/`CardCallbacks` records.** Behavior is not separable. |
| **Mascot** (~190 ln) | `EyeDirection`, `LastActivityTime`, `ActivityLevel` (exclusive) | `Tick`, `UserActivity` + activity subscriptions. Edge case: `Tick` also expires canvas events; `ActivityLevel` also drives refresh-poll cadence | **Vertical slice.** `Tick` stays in root update (shared) but delegates the activity recompute to `MascotUpdate`. |
| **Overview / footer** (~100 ln) | reads `SchedulerEvents`, `LatestByCategory`, `Repos` | **none** — zero dedicated arms | **Pure view**, plain params. |
| **Canvas card-view** (Opp B) | reads `Canvas.*` slices | update already extracted (`CanvasUpdate.fs`) | Lift pane wiring into `CanvasView.fs`. |

## Expected Behavior

- The dashboard renders byte-for-byte identically before and after — same DOM, same CSS
  classes, same interactions. No user-visible change.
- `App.fs` is reduced to orchestration: `init`, the `update` `match` (with arm bodies
  delegating to feature `*Update` modules), `appSubscriptions`, and the top-level `view`
  wiring. Target: roughly half — about **800 lines**, down from **1685** (the six new modules
  absorb the ~900 lines of relocated view/state/update code).
- The Fable client build succeeds and the full test suite (Unit + Fast + E2E) passes after
  **each** task, not just at the end.

## Technical Approach

New modules and their compilation placement in `src/Client/Client.fsproj`
(before-`AppTypes` modules are pure and take slices/records; after-`AppTypes` modules
reference `Model`/`Msg`, like `CanvasUpdate.fs`):

| New module | Compiles | Holds |
|---|---|---|
| `OverviewViews.fs` | before `AppTypes` | `statusOverviewRow`, `pinnedErrorEntry`, `schedulerFooter`, and path-prefix helpers (`knownCategories`, `categoryDisplayName`, `lastSepIndex`, `commonPathPrefix`, `stripPrefix`) |
| `CardViews.fs` | before `AppTypes` | `CardViewProps` + `CardCallbacks` records; all card render functions, action buttons, icons, badges, PR/sync/event-log helpers, `repoSection`, `repoSectionHeader`, skeletons; per-worktree `canvasEventEntry`/`canvasEventLog` (card-embedded) |
| `MascotState.fs` | before `AppTypes` (next to `CanvasState.fs`) | `MascotState` record (`EyeDirection`, `LastActivityTime`, `ActivityLevel`), `empty`, and pure helpers `computeActivityLevel`, `randomEyeDirection`, idle thresholds |
| `MascotView.fs` | before `AppTypes` | `viewEyeOpen`/`viewEyeRolledBack`/`viewEyeClosed`, taking a `MascotState` slice |
| `MascotUpdate.fs` | after `AppTypes` (next to `CanvasUpdate.fs`) | `Tick`/`UserActivity` activity-recompute bodies + the subscription helper |
| `CanvasView.fs` | after `CanvasUpdate.fs`, before `App.fs` | `focusedWorktreeCanvasDoc` + the canvas-pane wiring block (builds `CanvasPaneCallbacks`, computes focused unviewed/visited docs, calls `CanvasPane.view`) |

Record shapes (illustrative — finalize during implementation):

- `CardViewProps` = the read slice currently threaded as loose args (`EditorName`, `IsCompact`,
  `FocusedElement`, `BranchEvents`, `SyncPending`, `ActionCooldowns`, `CanvasEvents`,
  `CanvasPaneOpen`). No `Model` dependency — Shared/Navigation types only.
- `CardCallbacks` = the dispatch-derived actions cards trigger (terminal, editor, new-tab,
  resume, delete, archive, sync, launch-action, focus, toggle-collapse, PR action). Plain
  `… -> unit` functions — no `Msg` dependency. `App.fs` constructs both records in `view`
  from `model` and `dispatch`.

**Reuse, don't duplicate.** Before relocating helpers, check `Components.fs` and
`ActionButtons.fs` (already holds `noFocusProps`, `commentIcon`, `wrenchIcon`, `createPrIcon`)
and route through existing helpers; move shared icons there rather than copying.

**Each task ends green.** Relocations are cut/paste + `open`/namespace adjustments plus the
`.fsproj` `<Compile>` entry; no logic edits. Before marking a task done, run the per-step
green check: `npm run build` plus `dotnet test src/Tests/Tests.fsproj --filter "Category=Unit"`
and `--filter "Category=Fast"`. The heavier E2E suite (`--filter "Category=E2E"`) runs in the
final verify task.

## Task Sequence

1. `OverviewViews.fs` — pure relocation (proves the seam).
2. `CardViewProps` + `CardCallbacks` records — define them and refactor the **in-place** card
   views + the `view` call site to use them (no file move yet).
3. Relocate card **leaf** helpers (icons, action buttons, badges, sync/event-log/PR helpers,
   class/text/format helpers, `canvasEventEntry`/`canvasEventLog`) into `CardViews.fs`.
4. Relocate **composite** card views (`compactWorktreeCard`, `worktreeCard`, `renderCard`,
   `repoSectionHeader`, `repoSection`, skeletons, `providerIcon`, `sortLabel`) into
   `CardViews.fs` and wire `view` to call `CardViews.repoSection` with the records.
5. `MascotState.fs` — introduce the sub-state, embed as `Model.Mascot`, and repoint `init`,
   `DataLoaded`, `Tick`, `UserActivity`, `appSubscriptions`, and the header to `model.Mascot.*`.
6. `MascotView.fs` + `MascotUpdate.fs` — move the eye views and the `Tick`/`UserActivity`
   bodies; wire the header to `MascotView` and the arms to delegate to `MascotUpdate`.
7. `CanvasView.fs` — lift `focusedWorktreeCanvasDoc` + the canvas-pane wiring block out of
   `view`.

Tasks are chained (each blocks the next) to honor the order and to serialize edits to the
shared `App.fs`, avoiding self-conflicts.

## Key Files

- **Primary**: `src/Client/App.fs`, `src/Client/Client.fsproj`.
- **Referenced patterns**: `src/Client/CanvasState.fs`, `src/Client/CanvasUpdate.fs`,
  `src/Client/CanvasPane.fs` (the vertical-slice + callback-record precedent),
  `src/Client/AppTypes.fs` (root `Model`/`Msg`; flat-`Msg` decision).
- **Reuse targets**: `src/Client/Components.fs`, `src/Client/ActionButtons.fs`.

## Provenance

Activates and supersedes `docs/spec/future/app-fs-view-extraction.md`, which framed the same
work as deferred "Opportunity A" (pre-existing view bulk) and "Opportunity B" (canvas view
layer). This spec carries the design decisions (records, mascot slice, sequencing) that the
deferred note left open.
