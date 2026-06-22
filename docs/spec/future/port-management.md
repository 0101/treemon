# Port Management

Status: **Future / Deferred** — design only. NOT implemented on the canvas48 branch.
That branch shipped only a targeted SmokeTests fix (see [Scope](#scope-what-canvas48-actually-changed)).

## Problem Statement

Network ports are hardcoded and scattered across the server, client, tests, and ops
scripts. There is no single place to audit which ports exist or what they are for, so
collisions are easy to introduce and hard to diagnose. Collisions happen when:

- A **production Treemon is running** while a developer starts the app or tests from
  another worktree — both try to bind the same fixed ports.
- **Other apps on the machine** already claim a port in our range.
- Two of our own tools pick the **same** number independently (e.g. `5176` is used by
  both `scripts/record-demo.mjs` and `src/Tests/DemoModeTests.fs`).

### Motivating incident (concrete example)

This branch added an always-on `CanvasDocServer` bound to a fixed port `5002`. The Local
`SmokeTests` had long reused `5002` as their main API port. Once a production Treemon was
running (API `5000`, canvas doc server `5002`), the smoke child could no longer bind
`5002`; the server exited during fixture startup, and the fixture only reported a blind
`Timed out waiting for IsReady=true (60s)` — hiding the real
`address already in use` bind error. All 5 Local smoke tests failed.

Full root-cause writeup: `.agents/smoke-tests-investigation.md`.

The incident is the general problem in miniature: a fixed port chosen in one place
(`CanvasDocServer`) silently invalidated a fixed port chosen in another place
(`SmokeTests`), and nothing enumerated the two so the conflict was invisible until runtime.

## Current Port Inventory

Snapshot of every port the repo defines today (regenerate with
`rg -n '\b(500[0-9]|517[0-9])\b'` excluding `bin/obj/output/dist/node_modules`).

| Port | Purpose | Defined / hardcoded in | Env override |
|---|---|---|---|
| 5000 | Production main API (Fable Remoting) | `src/Cli/Program.fs:15`, `src/Server/Program.fs:74`, `treemon.ps1:20`, `vite.config.js:6`, `src/Extension/extension.mjs:4` | `TREEMON_PORT` |
| 5002 | Canvas doc / bridge server | `src/Server/Program.fs:44` (`defaultCanvasPort`); **client origin hardcoded** `src/Client/CanvasPane.fs:8`; test origin `src/Tests/CanvasPaneTests.fs:33` | none (only `--canvas-port` / `--no-canvas`) |
| 5001 | Dev server API | `treemon.ps1:241`, `src/Tests/ServerFixture.fs:24,94,133,166` | `API_PORT` |
| 5174 | Dev Vite client | `treemon.ps1:242`, `src/Tests/ServerFixture.fs:25,132,167` | `VITE_PORT` |
| 5173 | Vite default fallback | `vite.config.js:5` | `VITE_PORT` |
| 5003 | Demo-mode test server | `src/Tests/DemoModeTests.fs:20` | `API_PORT` (per-proc) |
| 5176 | Demo Vite (tests **and** demo recorder — collision risk) | `src/Tests/DemoModeTests.fs:21`, `scripts/record-demo.mjs:20` | `VITE_PORT` |
| 5051 | Demo recorder server | `scripts/record-demo.mjs:19` | `API_PORT` (per-proc) |

Already dynamic (good): `src/Tests/SmokeTests.fs` uses `TestUtils.getFreeTcpPort()` /
`getFreeTcpPorts 2`.

### Latent bug to fix during migration

`src/Client/CanvasPane.fs:8` hardcodes `let [<Literal>] CanvasOrigin = "http://127.0.0.1:5002"`.
The server now accepts `--canvas-port`, but the client literal cannot follow it — changing
the canvas port silently breaks the canvas iframe. A compile-time `[<Literal>]` is the wrong
mechanism for a value the server can reconfigure at runtime (see Technical Approach).

## Goals

- **One source of truth in code.** A `Ports` module in `src/Shared` enumerates every port,
  its purpose, its default, and its env-var override — so conflicts are impossible by
  construction and there is exactly one place to audit. Because `src/Shared` is shared
  with the Fable client, the client stops hardcoding the canvas origin.
- **Dynamic allocation for dev/test.** When a fixed port is busy, obtain a free one instead
  of failing or fighting over it. Tests must never depend on a specific global port and must
  never kill another process — especially the developer's production Treemon.
- **Predictable production.** Production keeps stable, well-known ports (the VS Code
  extension and any bookmarks expect API `5000`) and fails loudly on conflict rather than
  silently moving.

## Technical Approach

### 1. `Ports` module (single source of truth) — `src/Shared/Ports.fs`

A plain data module of port specs. Plain `int` constants and records compile cleanly under
Fable, so the client can reference the same definitions the server uses.

```fsharp
module Treemon.Shared.Ports

/// One named port: its default, what it's for, and the env var that overrides it.
type PortSpec =
    { Name: string                 // "api", "canvas", "devApi", "devVite"
      Purpose: string
      Default: int
      EnvVar: string option }      // e.g. Some "TREEMON_PORT"

let api     = { Name = "api";     Purpose = "Production Fable Remoting API";  Default = 5000; EnvVar = Some "TREEMON_PORT" }
let canvas  = { Name = "canvas";  Purpose = "Canvas doc / bridge server";    Default = 5002; EnvVar = None }
let devApi  = { Name = "devApi";  Purpose = "Dev server API";                Default = 5001; EnvVar = Some "API_PORT" }
let devVite = { Name = "devVite"; Purpose = "Dev Vite client";              Default = 5174; EnvVar = Some "VITE_PORT" }

/// Every port Treemon owns — the audit list.
let all = [ api; canvas; devApi; devVite ]
```

Keep the module dumb: only definitions and pure helpers. Anything that touches sockets or
the process environment lives in the resolver below, guarded for the server/test side.

### 2. Resolution + dynamic allocation — server/test only

Free-port probing uses `System.Net.Sockets` / `Environment`, which Fable cannot compile, so
it lives server-side (or behind `#if !FABLE_COMPILER`). The primitive already exists —
`TestUtils.getFreeTcpPort()` / `getFreeTcpPorts count` bind `TcpListener(Loopback, 0)` and
read back the OS-assigned port. Promote that into a small shared `PortResolver` and apply a
per-context policy:

| Context | Policy | Rationale |
|---|---|---|
| Production | Use `spec.Default` (or `EnvVar` override). If busy, **fail loudly** with the owning PID. | Extension/bookmarks expect `5000`; silent moves break them. |
| Dev (`treemon dev`) | Start at `spec.Default`, **probe upward** to the next free port, skipping any port in `Ports.all`. Print the chosen port. | Tolerates a busy port and a running production instance; URL stays roughly predictable. |
| Test | **OS-assigned free port** (`getFreeTcpPort`, port 0). | Zero contention, never collides, no global state. Already used by SmokeTests. |

```fsharp
// Sketch — server/test side only
let resolve (spec: PortSpec) (strategy: Strategy) : int =
    let preferred =
        spec.EnvVar
        |> Option.bind (Environment.GetEnvironmentVariable >> Option.ofObj)
        |> Option.bind (fun s -> match Int32.TryParse s with | true, p -> Some p | _ -> None)
        |> Option.defaultValue spec.Default
    match strategy with
    | Fixed     -> if isFree preferred then preferred else failBusy spec preferred
    | ProbeUp   -> firstFreeFrom preferred (Set.ofList (Ports.all |> List.map _.Default))
    | OsAssigned-> getFreeTcpPort ()
```

Delete or quarantine `TestUtils.killOrphansOnPort`. Killing whatever owns a port is the
exact footgun the goals forbid (it can take down the user's production Treemon). Dynamic
allocation removes the need for it.

### 3. Client canvas origin at runtime (not a `[<Literal>]`)

The canvas port is now server-configurable, so the client must learn it at runtime instead
of compiling in `5002`. The main API already knows `config.CanvasPort`; expose it and have
the client read it once at startup.

- Server: include the active canvas origin in the worktree/config payload the client already
  fetches (or add a tiny `getCanvasOrigin` API method).
- Client: replace the `CanvasPane.CanvasOrigin` literal with that runtime value; fall back to
  `Ports.canvas.Default` only when canvas is disabled.

This is the trickiest migration step and the reason the canvas port was left hardcoded — call
it out so it isn't rediscovered.

## Migration Outline

1. **Inventory** — confirm the table above is complete:
   `rg -n '\b(500[0-9]|517[0-9])\b'` excluding `bin/obj/output/dist/node_modules`.
2. **Add `src/Shared/Ports.fs`** to `Shared.fsproj` (compile order: before consumers).
3. **Server** — replace `defaultCanvasPort = 5002` and the literal `5000` in
   `src/Server/Program.fs` with `Ports.canvas.Default` / `Ports.api.Default`; route startup
   through the resolver. Keep the existing `--port` / `--canvas-port` / `--no-canvas` flags
   and the canvas≠main-port guard.
4. **CLI** — replace `defaultPort = 5000` and help text in `src/Cli/Program.fs` with
   `Ports.api`.
5. **Client** — remove the `CanvasOrigin` literal (`src/Client/CanvasPane.fs:8`); consume the
   runtime canvas origin (step 3 of Technical Approach).
6. **Tests** — convert `ServerFixture.fs` and `DemoModeTests.fs` to `getFreeTcpPort()` like
   `SmokeTests.fs` already does; drop `killOrphansOnPort` calls; update `CanvasPaneTests.fs`
   to derive its origin from the fixture's chosen port.
7. **Ops scripts** — `treemon.ps1` (`$DefaultPort`, `$devApiPort`, `$devVitePort`),
   `vite.config.js` (`API_PORT`/`VITE_PORT` defaults), `scripts/record-demo.mjs`
   (`SERVER_PORT`/`VITE_PORT`), and `src/Extension/extension.mjs` (`TREEMON_PORT`) read their
   defaults from the same env-var names the `Ports` module declares. PowerShell/JS can't
   import the F# module directly, so either (a) keep them reading the canonical **env-var
   names** (`TREEMON_PORT`, `API_PORT`, `VITE_PORT`) with the F# module as the documented
   owner of the defaults, or (b) generate a small `ports.json` from `Ports.all` at build time
   for non-.NET consumers. Prefer (a); it's lower-friction.
8. **Docs** — regenerate the `## Ports` table in `AGENTS.md` from `Ports.all`.

## Decisions

- **Module lives in `src/Shared`, not `src/Server`** — the client needs the canvas default
  too, and `Shared` is the only project both compile against. Keep it Fable-safe (no socket
  or `Environment` access in the module itself).
- **Production fails loud; dev probes; tests use OS-assigned** — moving production off `5000`
  silently would break the VS Code extension (`extension.mjs`) and user bookmarks, so only
  dev/test allocate dynamically.
- **`killOrphansOnPort` is removed, not reused** — the goals explicitly forbid killing other
  processes; dynamic free ports make it unnecessary.
- **Non-.NET consumers stay env-var-driven** — PowerShell, Vite, the demo recorder, and the
  extension can't reference the F# module, so the env-var names are the cross-language
  contract; the `Ports` module owns the defaults. A generated `ports.json` is an optional
  upgrade if drift becomes a problem.

## Scope: what canvas48 actually changed

Implemented on this branch (targeted fix only):

- `src/Server/Program.fs` — `ServerConfig.CanvasPort: int option`, `defaultCanvasPort`,
  `--canvas-port` / `--no-canvas` flags, and a guard that `--canvas-port` must differ from
  `--port`.
- `src/Server/CanvasDocServer.fs` — `start` now takes the canvas port as a parameter instead
  of hardcoding it.
- `src/Tests/SmokeTests.fs` — uses `TestUtils.getFreeTcpPort()` / `getFreeTcpPorts` instead
  of fixed `5002`.

NOT implemented on this branch (this spec): the centralized `Ports` module, the
production/dev/test resolver policy, the runtime client canvas origin, and the conversion of
`ServerFixture` / `DemoModeTests` / ops scripts. Everything in Technical Approach and
Migration Outline is future work.

## Key Files

- **New**: `src/Shared/Ports.fs` (+ `Shared.fsproj`), optional server/test `PortResolver`.
- **Server**: `src/Server/Program.fs`, `src/Server/CanvasDocServer.fs`.
- **Client**: `src/Client/CanvasPane.fs` (remove `CanvasOrigin` literal), App config plumbing.
- **CLI**: `src/Cli/Program.fs`.
- **Tests**: `src/Tests/ServerFixture.fs`, `src/Tests/DemoModeTests.fs`,
  `src/Tests/CanvasPaneTests.fs`, `src/Tests/TestUtils.fs` (`getFreeTcpPort`,
  retire `killOrphansOnPort`).
- **Ops**: `treemon.ps1`, `vite.config.js`, `scripts/record-demo.mjs`,
  `src/Extension/extension.mjs`, `AGENTS.md` (`## Ports`).
