# Canvas Bridge — Pre-Existing Fixes (deferred)

Status: **Deferred** — surfaced by a whole-branch focused review during the
`canvas-browser-fallback` work, but these defects live in **pre-existing canvas code**
(the parent `canvas` branch), not in the browser-fallback changes. Documented here so they
aren't lost; intentionally **not** fixed on the browser-fallback branch to keep that change
scoped.

Source: `.agents/focused-review/20260603-152741/review.md` (confirmed findings #1, #4, #5).
Beads: `tm-canvas-browser-fallback-7fs.2`, `tm-canvas-browser-fallback-7fs.4`.

## 1. Stale bridge sessions drop canvas messages instead of queueing

**File:** `src/Server/CanvasBridge.fs` (`sendMessage`, ~line 105) · **Severity:** High

The bridge keeps its own registries (`sessionRegistry`, `pollRegistry`) outside the scheduler
lifecycle, with no unregister/reconcile path. `sendMessage` always prefers `sessionRegistry`
when an entry exists, even if that session is stale/dead. When delivery to a stale inject URL
fails, the code returns `Error` and never falls through to the existing queue — so messages
are lost in a realistic reconnect scenario. The `isSessionAlive` check exists but is not used
in the delivery path; the queue infrastructure is fully implemented but bypassed.

The spec already calls for queueing on delivery failure
(`docs/spec/canvas-awareness-liveness.md` lines 146-149).

**Fix:** In `sendMessage`, when the HTTP POST to `entry.InjectUrl` fails, remove/invalidate the
stale `sessionRegistry` entry, enqueue the payload via the existing `enqueue`, and return
`Queued` instead of `Error`. Optionally check `isSessionAlive entry` first and skip straight to
queueing for known-stale entries.

## 2. App.fs has grown past the file-size limit

**File:** `src/Client/App.fs` (1933 lines) · **Severity:** Medium

Canvas pane functionality (types, state management, views — ~162 canvas references) is mixed
into the main application module, pushing it well past the size limit.

**Fix:** Extract canvas-related code into a dedicated module (e.g. `CanvasState.fs` /
`CanvasHelpers.fs`): the `CanvasEvent` type, canvas `Msg` variants, canvas update handlers,
canvas subscription logic, and canvas view glue. Keep canvas fields in the main `Model` but
delegate canvas logic to the new module.

## 3. Spec hygiene: delete canvas-review-fixes.md

**File:** `docs/spec/canvas-review-fixes.md` · **Severity:** Medium (spec hygiene)

Filename contains the `-fixes` red flag and the content is one-off cleanup narrative (bug
fixes with line numbers, refactoring, DU replacement) — implementation history that belongs in
PRs/commits, not a lasting spec. The only durable content is two "Decisions" entries (bridge
registration for agent-created docs; split bridge-registry race fix).

**Fix:** Move those two Decisions subsections into `docs/spec/canvas-pane.md`, then delete
`docs/spec/canvas-review-fixes.md`.

## Not included here (already fixed on the browser-fallback branch)

Review finding #2 (extension path guard hardcoded backslash) and #3 (in-canvas link fragment
not stripped) **were** in scope and are fixed: commits `0606915` and `45c17a5`.
