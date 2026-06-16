# Canvas code-health cleanup

Quality cleanup of the canvas feature on branch `canvas48`, driven by the focused-review
report at `.agents/focused-review/20260616-092556/review.md`. This spec captures the durable
invariants the cleanup enforces; per-finding detail lives in that report and in the beads tasks.

## Goals

- Canvas code is correct, simple, and reasoned-about — illegal states and silent footguns are
  removed in favour of compiler-enforced guarantees.
- The canvas feature upholds the project's F# discipline (MVU purity, no imperative loops,
  immutability) rather than carrying local exceptions to it.
- Newly added canvas surfaces are no less safe or maintainable than the code they sit beside.

## Expected Behavior (invariants after cleanup)

- **Correct canvas-session launch.** Starting a session from a canvas card points the agent at the
  real on-disk doc path (`{worktree}\.agents\canvas\{filename}`), matching the server launch flow.
- **Honest canvas send state.** A queued canvas message is not reported to the user as a failure
  on a wall-clock timer when it was in fact delivered.
- **Pure `update`.** The Elmish `update` reads no wall-clock; time enters only via message payloads.
- **Loopback-only canvas registration.** `/api/canvas/register` accepts an `injectUrl` only when it
  is an absolute `http(s)` URL whose host is a loopback IP (`IPAddress.IsLoopback`) or the literal
  `localhost` (rejected `400` otherwise), and only for a known worktree (`isKnownWorktree`, mirroring
  the heartbeat/doc routes; unknown → `404`). The route is wired with the scheduler agent, so demo
  mode (no agent) omits it.
- **Robust in-doc navigation.** Same-origin `.html` links resolve to a bare filename even with
  `?query`/`#hash` suffixes.
- **Single source of truth for the beadspace template** — runtime and E2E tests read one definition;
  drift between the two is impossible (or guarded by a test).
- **Functional style throughout canvas code and its tests** — no imperative `for`/`while` loops or
  `let mutable` accumulators where a higher-order or recursive form is idiomatic; NUnit lifecycle
  mutables are documented.
- **Tests assert behaviour** — no tautological `Assert.Pass()` standing in for a real check.

## Technical Approach

Fixes are grouped into small, independently reviewable tasks (see beads feature). Grouping:

- **Client correctness** (`src/Client/App.fs`): canvas doc path, `update` purity, queued-send state.
- **Client cleanup/structure** (`src/Client/App.fs`, `src/Tests`): F# 9 shorthand, dead parameter
  removal, and extraction of the canvas pure helpers + Model-field group into a `CanvasState` module
  to shrink the 2124-line file.
- **Server** (`src/Server`): loopback validation for canvas registration, query/hash-safe link
  interception, `Async.Sequential` in place of the `drainQueue` loop, and de-duplicating the
  beadspace template.
- **Tests** (`src/Tests`): real assertions for the queue-cap test, recursive poll helpers in place of
  mutable while-loops, and documenting NUnit fixture lifecycle mutables.
- **Docs**: retire the task-log `canvas-system-view.md`, absorbing its durable rationale into
  `canvas-pane.md`.

A focused-review pass over the cleanup diff acts as the quality gate before the work is considered done.

## Verification

The cleanup is done when a single `verify`-labelled task confirms — falsifiably, each check with an
explicit FAIL condition — that every invariant above holds:

- **Build + full suite green.** `npm run build` (client) and `dotnet test src/Tests/Tests.fsproj`
  both succeed. FAIL on any compile error or failing test.
- **Static invariant checks** over the canvas surface: no `Date.now()` inside `update`; no
  `for`/`while`/`let mutable` accumulator in `CanvasBridge.fs` or the canvas test files (NUnit
  lifecycle mutables excepted *only if* commented); the queue-cap test asserts ten survivors rather
  than `Assert.Pass()`; the canvas-session launch builds the `{worktree}\.agents\canvas\{filename}`
  path; the link interceptor strips `?`/`#`; `/api/canvas/register` rejects a non-loopback
  `injectUrl`; one beadspace-template source of truth (or a drift-guard test); and
  `canvas-system-view.md` is gone with no dangling links. FAIL if any check does not hold.
- **Honest send state** (behavioural): if not covered by an automated test, verify by inspection
  that `Waiting` is never flipped to `Failed` purely by the wall-clock timer.

The `Run focused code review` task runs before verification; any significant issue it surfaces
becomes a fix task that blocks the verify task.

## Related Specs

- `docs/spec/canvas-pane.md` — canvas pane architecture and doc kinds (durable home for the model).
- `docs/spec/worktree-monitor.md` — overall dashboard architecture.
