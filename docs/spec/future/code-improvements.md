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
| 2 | **Port management** — mostly resolved; the centralized `Ports` module is deliberately not worth building. What remains is converting `DemoModeTests` off its two hardcoded ports so `TestUtils.killOrphansOnPort` can be deleted. | `docs/spec/future/port-management.md` | Mostly done |
| 3 | **Canvas roadmap items** — follow-on canvas-pane enhancements | `docs/spec/future/canvas-roadmap.md` | Idea |
| 4 | **Process: guard against spec drift** — a lightweight check (or review rule) that flags `Key Files` references to moved/renamed modules so docs can't silently rot after refactors | — | Idea |
| 5 | **Survey other large modules** — *investigated.* `RefreshScheduler.fs` and `WorktreeApi.fs` both mix concerns; the concrete split is broken out as #8 below (and #7, the `GlobalConfig` extract, now *Done*). `WorktreeDiff.fs` is tracked separately as #13. (Strict-FP smells are already clean: no stray `let mutable`/loops/`null` in production; `Dictionary` only at cache/registry boundaries.) | — | Done (survey) |
| 6 | **Remoting CSRF / Origin hardening** — pipeline-level Origin/Referer check so a cross-origin browser page can't drive the unauthenticated loopback Fable.Remoting API (covers the dangerous pre-existing process-launching endpoints, not just watched-roots) | `docs/spec/remoting-csrf-hardening.md` | **Done** — shipped on `quicklaunch` (`HttpSecurity.csrfGuard`) |
| 8 | **Split `RefreshScheduler.fs`** — the scheduler module also carries the `PerRepoState`/`DashboardState`/`StateMsg` state slice (everything above `SchedulerServices`) and an embedded `CanvasWatchers` filesystem-watcher module at the end of the file. Lift one or both out of the scheduling loop into their own files (vertical-slice seam, like canvas/mascot/activity). Behavior-preserving but a heavy ripple: `WorktreeApi.fs` alone reaches into those state types throughout. | — | Idea (from survey) |
| 10 | **Share the `WorktreeDiffTests` git fixture** — the suite creates a real repository per test in `[<SetUp>]` (`initRepoOnMain`), making it the single largest contributor to Fast-suite runtime. Build the repository once in `[<OneTimeSetUp>]` and give each test its own branch or clone. | — | Idea |
| 11 | **Fast-suite runtime exceeds its documented budget** — AGENTS.md advertises `<60s` for `Category=Fast`; the suite has been several times that for a while, dominated by browser-driven fixtures (`DashboardTests`, `CreateWorktreeServerTests`, `ArchiveTests`) plus #10. Either bring the suite back under budget or correct the figure, because a stale number stops it acting as a gate. | — | Idea |
| 12 | **Pin a SystemView to a chosen session** — SystemView interactions resolve per interaction to the worktree's most recently active live session, so with two agents alternating the target follows whoever spoke last. A user-visible pin would make it sticky. Deliberately out of scope when the resolution rule was adopted: storage would exist solely to hold rare, uncontended overrides. | `docs/spec/canvas-interaction-routing.md` | Idea |
| 13 | **Split `WorktreeDiff.fs` and `DiffTemplate.html`** — both exceed the 1,000-line limit in `review/rules/file-size-limit.md`. Extract the entry parsing and untracked-content handling from `WorktreeDiff.fs` so it owns comparison orchestration and result types only; move the viewer CSS and JS out of `DiffTemplate.html` into embedded assets, leaving an HTML shell and making the renderer independently testable. | `docs/spec/worktree-diff-viewer.md` | Idea |
| 14 | **Process: route review findings that need a decision to a human** — focused-review marks some findings as needing a product decision rather than a fix. Implementing the reviewer's suggested mechanism instead of answering the question has produced whole subsystems that were later removed. Make that state a stop in the review→fix flow. | — | Idea (process) |


> Add new candidates here as they surface (often from focused-review findings). Keep the list
> honest: remove ones that turn out not to be worth it, and record why in the relevant spec.

## Done

- **`ProcessRunner` consolidation on the argument-list API** — the string-argument entry points are
  deleted; see `docs/spec/process-execution.md`.
- **`GlobalConfig` store extraction** — lifted the machine-level `~/.treemon/config.json`
  read/modify/write (single-writer lock, atomic temp-file replace, missing-vs-empty
  `worktreeRoots` semantics, plus the canvas / collapsed-repos / last-viewed-hashes / editor
  accessors) out of `WorktreeApi.fs` into `src/Server/GlobalConfig.fs`, leaving the
  API module with just `IWorktreeApi` wiring + `DashboardResponse` assembly. Behavior-preserving;
  see `docs/spec/worktree-monitor.md` (Configuration Store).
- **App.fs view extraction** — split `src/Client/App.fs` into smaller modules. Extracted
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
