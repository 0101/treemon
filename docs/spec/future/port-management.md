# Port Management

Status: **Mostly resolved.** The collision class this spec was written for is gone — tests take
OS-assigned ports and the client learns the canvas origin at build time. What remains is one
holdout, `DemoModeTests`, described under [Remaining Scope](#remaining-scope).

## Problem Statement

Network ports were hardcoded and scattered across the server, client, tests, and ops scripts, so
collisions were easy to introduce and hard to diagnose. They happened when a production Treemon was
running while a developer started the app or tests from another worktree, when another app on the
machine already claimed a port, or when two of our own tools independently picked the same number.

The motivating incident: an always-on `CanvasDocServer` bound to a fixed `5002`, which the Local
`SmokeTests` had long reused as their main API port. With production running, the smoke child could
no longer bind, and the fixture reported only a blind `Timed out waiting for IsReady=true (60s)`
instead of the underlying `address already in use`. A fixed port chosen in one place silently
invalidated a fixed port chosen in another, and nothing enumerated the two.

## Expected Behavior

- **Tests never depend on a global port and never kill another process.** A fixture binds
  `TcpListener(Loopback, 0)` and reads back the OS-assigned port, so concurrent runs and a running
  production instance cannot collide. `TestUtils.getFreeTcpPort` / `getFreeTcpPorts` provide this
  and are used across the test suite.
- **The client follows the canvas port it was built for.** `src/Client/CanvasPane.fs` reads
  `__CANVAS_ORIGIN__`, a Vite build-time `define` computed from `CANVAS_PORT` (defaulting to 5002)
  in `vite.config.js`. A `[<Literal>]` fallback exists only for the test assembly, which compiles
  the same file without Fable.
- **The E2E fixture owns all three of its ports.** `src/Tests/ServerFixture.fs` takes three free
  ports via `getFreeTcpPorts 3` for API, canvas, and Vite, and threads the canvas port into the
  client build through `CANVAS_PORT` so the iframe origin matches the server it is talking to.
- **Production keeps stable, well-known ports.** The VS Code extension and user bookmarks expect
  API `5000`, so production does not allocate dynamically.

## Remaining Scope

`src/Tests/DemoModeTests.fs` is the only holdout. It hardcodes `demoServerPort = 5003` and
`demoVitePort = 5176`, and calls `TestUtils.killOrphansOnPort` (`src/Tests/TestUtils.fs`) on both.

That helper kills whatever process owns a port, which contradicts the AGENTS.md rule that runtime
checks must not disturb any existing process — it can take down the developer's production Treemon.
`DemoModeTests` is its only caller, so converting these two ports to `getFreeTcpPorts` allows the
helper to be deleted outright rather than merely left unused.

Note that `5176` is also used by `scripts/record-demo.mjs`, so the two collide whenever a demo
recording and the demo tests overlap. Moving the tests to OS-assigned ports resolves that too.

## Decisions

- **No centralized `src/Shared/Ports.fs` module.** Both original justifications are gone: the client
  no longer needs a shared canvas default (it gets one from the Vite `define`), and fixtures no
  longer collide (they take OS-assigned ports). A module enumerating ports would now be an audit
  aid with no consumer, so it is deliberately not worth building.
- **Build-time `define` beat a runtime API for the canvas origin.** This spec originally proposed a
  `getCanvasOrigin` API method for the client to call at startup, and named it the trickiest
  migration step. Threading `CANVAS_PORT` into the bundle at build time achieves the same result
  with no request, no startup ordering concern, and no server surface.
- **`killOrphansOnPort` is to be removed, not reused.** Killing whatever owns a port is the exact
  footgun dynamic allocation exists to avoid.
- **Non-.NET consumers stay env-var-driven.** PowerShell, Vite, the demo recorder, and the extension
  cannot reference F# definitions, so the env-var names (`TREEMON_PORT`, `API_PORT`, `VITE_PORT`,
  `CANVAS_PORT`) are the cross-language contract.

## Key Files

| File | Role |
|---|---|
| `src/Tests/TestUtils.fs` | `getFreeTcpPort` / `getFreeTcpPorts`; `killOrphansOnPort` pending removal |
| `src/Tests/ServerFixture.fs` | Takes three free ports and threads `CANVAS_PORT` into the client build |
| `src/Tests/DemoModeTests.fs` | The remaining hardcoded ports and the last `killOrphansOnPort` callers |
| `src/Client/CanvasPane.fs` | Reads `__CANVAS_ORIGIN__`, with a non-Fable literal fallback |
| `vite.config.js` | Computes `__CANVAS_ORIGIN__` from `CANVAS_PORT` |
| `scripts/record-demo.mjs` | Also uses `5176`, colliding with `DemoModeTests` |

## Related Specs

- `docs/spec/canvas-pane.md` — the canvas iframe whose origin this determines.
