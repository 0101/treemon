# Code Improvements — Running Backlog

A living index of code-quality improvements for this repo, plus the repeatable loop for
doing one at a time. Each improvement is a focused, behavior-preserving change shipped from
its own worktree. This file is the entry point; detailed designs live in their own spec.

## The loop (one improvement per worktree)

1. **New worktree off `main`** — e.g. `git worktree add ..\tm-<slug> -b <slug> main` (or the
   `tm`/treemon worktree tooling). One improvement per branch keeps diffs reviewable.
2. **Pick the top candidate** below, or investigate a new one. For anything with a design
   fork, write a canvas decision doc (`.agents/canvas/*.html`) and let the user choose before
   planning.
3. **`/bd-plan`** the work — produces a spec under `docs/spec/`, a beads feature + sequenced
   tasks, a focused-review gate, and a verification task.
4. **`/bd-execute`** — runs each task through executor + reviewer, then the focused-review
   quality gate, then the verify task (build + Unit + Fast + E2E + structure).
5. **Open a PR** (`github` skill). **Keep docs honest in the same PR**: if you moved code,
   update the `Key Files` tables / module references in any affected spec, and update this
   backlog (move the item to *Done*).

## Conventions

- **Behavior-preserving by default.** Refactors must keep the build and the full suite green
  (Unit + Fast + E2E); E2E asserts on DOM/CSS so identical render proves correctness.
- **Don't let specs rot.** When code moves between modules, the specs that point at it
  (their `Key Files` tables, `### Client-Side (…)` headers) must be updated in the same PR.
  Spec drift after a refactor is itself a tracked defect — see the *Process* candidate below.
- **Evidence-driven scope.** Prefer the simplest split the code supports; don't invent module
  boundaries the behavior doesn't justify (see the App.fs extraction's hybrid approach).

## Candidates (prioritized)

| # | Improvement | Detail / spec | Status |
|---|---|---|---|
| 1 | **Strong-typed paths** — an `AbsolutePath` type to kill path-comparison bugs at construction time | `docs/spec/future/strong-typed-paths.md` | Deferred (cost/benefit) |
| 2 | **Port management** — centralize/derive the dev/prod/canvas/vite port assignments | `docs/spec/future/port-management.md` | Idea |
| 3 | **Canvas roadmap items** — follow-on canvas-pane enhancements | `docs/spec/future/canvas-roadmap.md` | Idea |
| 4 | **Process: guard against spec drift** — a lightweight check (or review rule) that flags `Key Files` references to moved/renamed modules so docs can't silently rot after refactors | — | Idea |
| 5 | **Survey other large modules** — *investigated.* The two largest production modules are `WorktreeApi.fs` (870 L) and `RefreshScheduler.fs` (741 L); both mix concerns. Concrete splits broken out as #8 below (and #7, the `GlobalConfig` extract, now *Done*). (Strict-FP smells are already clean: no stray `let mutable`/loops/`null` in production; `Dictionary` only at cache/registry boundaries.) | — | Done (survey) |
| 6 | **Remoting CSRF / Origin hardening** — pipeline-level Origin/Referer (and optional custom-header) check so a cross-origin browser page can't drive the unauthenticated loopback Fable.Remoting API (covers the dangerous pre-existing process-launching endpoints, not just watched-roots) | `docs/spec/future/remoting-csrf-hardening.md` | Idea (from focused-review) |
| 8 | **Split `RefreshScheduler.fs`** — the scheduler module also carries the `DashboardState`/`StateMsg`/`processMessage` state slice (~190 L) and an embedded `CanvasWatchers` filesystem-watcher module (~110 L). Lift one or both out of the scheduling loop into their own files (vertical-slice seam, like canvas/mascot/activity). Behavior-preserving but heavier ripple (26 refs to `RefreshScheduler.DashboardState`/`StateMsg`/`PerRepoState` in `WorktreeApi.fs`). | — | Idea (from survey) |

> Add new candidates here as they surface (often from focused-review findings). Keep the list
> honest: remove ones that turn out not to be worth it, and record why in the relevant spec.

## Done

- **`GlobalConfig` store extraction** — lifted the machine-level `~/.treemon/config.json`
  read/modify/write (single-writer lock, atomic temp-file replace, missing-vs-empty
  `worktreeRoots` semantics, plus the canvas / collapsed-repos / last-viewed-hashes / editor
  accessors) out of `WorktreeApi.fs` (870 → 614 L) into `src/Server/GlobalConfig.fs`, leaving the
  API module with just `IWorktreeApi` wiring + `DashboardResponse` assembly. Behavior-preserving;
  see `docs/spec/global-config-store.md`.
- **App.fs view extraction** — `src/Client/App.fs` 1861 → 795 lines. Extracted
  `OverviewViews.fs`, `CardViews.fs` (with `CardViewProps`/`CardCallbacks` records),
  `MascotState.fs`/`MascotView.fs`, and `CanvasView.fs`; flat `Msg` + single `update`
  preserved. Branch `code-improvement`.
- **Activity / mascot separation of concerns** — split user-activity / idle-detection state
  out of the mascot into a dedicated `ActivityState.fs` + `ActivityUpdate.fs` slice; the
  mascot is now a pure gaze-and-eyes widget that *observes* `ActivityLevel`. See
  `docs/spec/user-idle-detection.md`.
- **Review-rule fix** — `review/rules/immutability.md` now forbids using a `ref` cell to dodge
  the rule (`let mutable` is the sanctioned, comment-justified mechanism when mutation is
  truly required).
