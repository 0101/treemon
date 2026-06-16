# Canvas code-health cleanup

Quality cleanup of the canvas feature on branch `canvas48`, driven by the focused-review
report at `.agents/focused-review/20260616-092556/review.md`. This spec captures the durable
invariants the cleanup enforces; per-finding detail lives in that report and in the beads tasks.

> **Retiring this spec (tm-canvas48-fr4):** The durable invariants and decisions here have been
> migrated into `docs/spec/canvas-pane.md` — loopback-only `/api/canvas/register` (Bridge Protocol),
> link-interceptor query/hash stripping (Link Handling), the pure-`update`/no-wall-clock invariant
> (Technical Approach), the canvas-session launch path (Liveness and Session Routing), and the
> `CanvasState` nested-record extraction + cross-platform doc-path decisions (Decisions). This
> branch-specific `-cleanup` file is intentionally **retained for now** because the still-open
> canvas48 fix tasks and the `verify` task (`tm-canvas48-9kv`) reference it. Delete it once the
> feature is verified and closed; nothing in the source tree links to it (only the beads DB does).

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

### Decision: `CanvasState` extraction shape (Finding 5, safe part)

The canvas Model-field group is extracted as a **nested record** `Canvas: CanvasState.CanvasState`
on `App.Model` (not as a flattened set of fields moved wholesale, and not helpers-only). Rationale:
this mirrors the existing `CreateModal: CreateWorktreeModal.ModalState` / `ConfirmModal` nesting
precedent, is the literal reading of "Model-field group", and is fully compiler-verifiable. The four
pure helpers (`touchVisitedDoc`, `canvasDocKind`, `activeVisibleDoc`, `markVisibleDocCmd`) plus the
`MaxLiveIframes` literal move into `src/Client/CanvasState.fs` (compiled before `App.fs`); the helpers
are refactored to pure slice-based signatures (`repos`/`focused`/`activeCanvasDoc` rather than the whole
`Model`), and `markVisibleDocCmd` is parameterized over the message constructor so the module needs no
concrete `Msg` type. Thin `App.fs` wrappers (`activeVisibleDoc model`, `markVisibleDocCmd model`) are
deliberately retained to keep `update` call sites unchanged. This is field-path nesting only — **not**
the larger `Cmd.map` sub-component split (no sub-`Msg`/sub-`update`; `update` stays one function), which
is explicitly out of scope for this task. Net effect: zero behaviour change (verified by the full suite).

### Decision: canvas doc path uses forward slashes (Findings C-01, C-05)

The illustrative `{worktree}\.agents\canvas\{filename}` notation in *Expected Behavior* / *Verification*
is Windows-style shorthand for "the path contains the `.agents` and `canvas` segments" — it does **not**
mandate backslashes. `CanvasPrompt.continueWorking` (the single source of truth, in `src/Shared/Types.fs`)
builds the path with **forward slashes** (`{worktree}/.agents/canvas/{filename}`), which resolve correctly
on Windows, Linux and macOS. `System.IO.Path.Combine` is deliberately **not** used because `src/Shared` is
Fable-compiled to JS and cannot reference `System.IO`. The previously separate `docPath` helper was
single-use and has been inlined into `continueWorking`. The launch-path verify check (`.agents`/`canvas`
segments present) is satisfied by the forward-slash form.

### Decision: `CanvasSendState.Waiting` carries only `scopedKey` (Finding C-02)

Finding C-02 calls for adding a target identity to `CanvasSendState.Waiting` so the "Waiting for
session…" banner is cleared only by the *target* worktree's session activity (via the pure
`CanvasAwareness.clearWaitingOnDelivery`, scoped by `scopedKey`), not by any unrelated worktree's
doc change. The pre-existing `queuedAt: float` payload became dead at this point — its only consumer,
the wall-clock failure timer, was already removed by the "Honest send state / pure `update`" work, and
no view or reducer reads it (`CanvasPane` matches `Waiting _`). Rather than *add* `scopedKey` alongside
a field nothing reads, `Waiting` now carries **only** `scopedKey: string`; `CanvasSendResult` likewise
drops its `now` argument (removing two `Date.now()` reads from the send command). This keeps the case
free of a silent unused field, per the feature's "no footguns" goal. The target `scopedKey` is the
focused card's key (`WorktreePath.value wt.Path`), the same key space as `agentChangedDocs`, so the
scoped clear compares equal for the same worktree.

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
