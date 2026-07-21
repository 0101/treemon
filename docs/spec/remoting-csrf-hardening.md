# Remoting CSRF / Origin Hardening

Status: **Implemented.** Surfaced by focused-review (finding C-13 / A-13, *Confirmed · Low*)
on the `tm-config-audit` branch; shipped on `quicklaunch` once the Create-worktree auto-launch
(see `docs/spec/worktree-monitor.md` → *Create Worktree*) turned the remoting surface into an
agent-execution sink and raised the priority.

The gap is not specific to any one endpoint — it is a property of the shared, unauthenticated
remoting handler. The fix is a single pipeline-level guard covering the *whole* surface.

## Problem Statement

The server exposes `IWorktreeApi` over Fable.Remoting with **no authentication**, bound to
loopback. Absent an Origin check (the guard this spec adds), a malicious web page open in the
operator's browser can issue cross-origin `POST http://localhost:5000/IWorktreeApi/<method>`
requests that the server executes as if they were same-origin — a classic localhost CSRF.

The newly-added watched-roots endpoints are the *least* dangerous members of this surface.
The same handler already exposes state-changing, process-launching endpoints that share the
identical exposure — e.g. `launchSession`, `launchAction`, `resumeSession`,
`sendCanvasMessage` (builds and launches a coding-tool CLI process), `createWorktree`,
`deleteWorktree`, `openTerminal`, `openEditor`. If localhost CSRF is in scope at all, those
are the high-value targets; fixing it once at the pipeline protects all of them.

### Why the usual mitigations don't close it (verified)

- **No control exists.** `buildRemotingHandler` (`src/Server/Program.fs`) is
  `createApi → fromValue → withErrorHandler → buildHttpHandler` and the Saturn pipeline is
  `use_router / url / use_static / use_gzip` — no `use_cors`, no Origin/Referer middleware,
  no CSRF token, no auth. A tree-wide grep for `cors|origin|access-control|referer|
  x-requested-with` in `src/Server` finds only an unrelated git remote and same-origin
  client JS.
- **Loopback bind is not a defense for this threat model.** The malicious page runs in a
  browser *on the same machine*, which can reach `http://localhost:5000`. The default port
  (`5000`) is well-known, so the attacker need not guess it.
- **The CORS-preflight "mitigation" does not apply.** A cross-origin `fetch` with
  `Content-Type: application/json` would be a *non-simple* request and trigger a preflight
  the no-CORS server fails — but Fable.Remoting.Server (verified against the resolved
  `5.42.0`) does **not** enforce `application/json`. Its dispatcher requires `POST` for
  argument-taking methods, then the only content-type branch is `multipart/form-data`; every
  other type — including `text/plain` — falls through to
  `StreamReader.ReadToEndAsync()` → `JsonConvert.DeserializeObject<JToken>` and is dispatched.
  A cross-origin `text/plain` POST is a CORS **simple request** (no preflight), so its
  JSON-array body reaches the method. The attacker can't read the response (that *is*
  CORS-blocked), which is irrelevant for a state-changing call.

### Impact ceiling (why this is Low, not High)

- The exposure is **pre-existing** — this is a latent design property of the remoting
  handler, not a regression.
- For the roots endpoints specifically: `removeRoot` is a blind guess (the attacker must
  supply the exact canonical watched path and can't enumerate it, since `getRoots`' response
  is CORS-blocked from being read); `addRoot` only accepts a directory that already exists on
  the victim's disk and its effect is deferred to the next server restart. Worst realistic
  outcome there is polluting/clearing one config key or a wasteful filesystem walk on
  restart — integrity/availability, never code execution.
- The real value of acting is the **process-launching** endpoints, where a forged call has
  materially higher impact.
- A later endpoint — `shareCanvasDoc` (canvas doc sharing; see `docs/spec/canvas-sharing.md`
  §"Security Posture", finding F16) — adds a **data-egress** class to this surface: a forged call
  publishes a local canvas file to an internet-reachable blob SAS URL. It stays Low (the response is
  CORS-unreadable and the target worktree path is machine-specific / non-enumerable), but it is the
  first member whose forged invocation *exfiltrates* rather than only mutating or launching — a further
  reason to land the central fix.

## Goals

- A cross-origin browser page **cannot** invoke any `IWorktreeApi` method.
- The fix is applied **once, at the remoting pipeline**, so every method (current and future)
  is covered without per-endpoint changes.
- **No friction for legitimate callers**: the same-origin web client and the `tm` CLI keep
  working unchanged — no client change was needed.
- Production keeps its well-known port and loud-on-conflict behavior (see
  `docs/spec/future/port-management.md`); this is orthogonal to where it binds.

## Technical Approach

`Server.HttpSecurity` is a small module whose core is a **pure decision function**, so the whole
policy is unit-testable without HTTP plumbing:

- `isSameOriginRequest (origin: string option) (referer: string option)` — **Origin** is
  authoritative when present, **Referer** consulted only when Origin is absent. A present value must
  parse as an absolute URL whose host is loopback (`localhost`, IPv4 `127.0.0.0/8`, or IPv6 `::1` via
  `IPAddress.IsLoopback`); anything else — a public host, a LAN IP, the literal `null` browsers emit
  for an opaque origin, or an unparseable value — is rejected. A **missing** pair returns `true`
  (allowed).
- `isRequestAllowed method origin referer` — safe methods (GET/HEAD/OPTIONS) always pass; any other
  (state-changing) method must be same-origin.
- `csrfGuard: HttpHandler` — reads the two headers, calls `isRequestAllowed`, and returns `403` on a
  reject (logging the origin) or falls through to the next handler.

`csrfGuard` is composed **before** the Fable.Remoting handler and **before** each state-changing canvas
route (`POST /api/canvas/register`, `POST /api/canvas/attribute`) in `Program.fs` — the `POST` filter
comes first, so the guard only ever evaluates the request it protects.

The **missing-header carve-out** is what keeps legitimate clients working: the non-browser `tm` CLI and
the Node canvas-bridge send neither Origin nor Referer (verified), and the same-origin SPA sends a
loopback Origin; a cross-origin browser page sends a non-loopback Origin and is rejected. Kestrel binds
localhost-only, so this closes the browser cross-origin vector without any client change.

Content-type enforcement was weighed as defense-in-depth but **not** added: the Origin check already
blocks the vector, and coupling to the clients' exact content-type risks breaking the CLI (which the
verified `text/plain` fall-through below shows the library does not constrain).

## Decisions

- **Fix at the pipeline, not per-endpoint.** The vulnerability is a property of the shared
  handler; a single middleware covers the dangerous pre-existing endpoints too. Per-endpoint
  guards would be incomplete and easy to forget on the next method added.
- **Origin/Referer over CSRF tokens.** A stateless Origin allowlist needs no session, token
  store, or client plumbing — appropriate for a single-user localhost tool. CSRF tokens would
  be over-engineered here.
- **Allow header-less requests.** The non-browser `tm` CLI legitimately sends no `Origin`;
  CSRF is exclusively a browser-driven attack, so absence-of-Origin is safe to allow and
  avoids breaking the CLI.
- **`application/json` enforcement is not the control.** Kept only as optional extra
  hardening; the verified `text/plain` fall-through means it can't stand alone, so it was not added.
- **The canvas POST routes carry the same guard.** `register`/`attribute` are the same cross-origin
  surface — and `attribute`'s owner sessionId flows into a `--resume` launch — so the *same*
  `csrfGuard` fronts them; the bridge's header-less Node `fetch` still passes via the carve-out.

## Key Files

- **`src/Server/HttpSecurity.fs`** — the guard: pure `isSameOriginRequest` / `isRequestAllowed`
  decision + `csrfGuard: HttpHandler`.
- **`src/Server/Program.fs`** — composes `HttpSecurity.csrfGuard` before the remoting handler and
  before the `POST /api/canvas/register` and `/api/canvas/attribute` routes.
- **`src/Server/CanvasDocServer.fs`** — the canvas register/attribute handlers the guard fronts.
- **`src/Shared/Types.fs`** — `IWorktreeApi` (every method is covered by the single pipeline guard).
- **`src/Tests/HttpSecurityTests.fs`** — unit tests for the pure loopback / same-origin decision.

## Verification

- `HttpSecurityTests` unit-covers the pure decision: loopback Origin (localhost / 127.0.0.1 / `[::1]`,
  any port, http or https) allowed; public host, LAN IP, opaque `null`, and unparseable rejected;
  Referer consulted only when Origin is absent; safe methods bypass; a missing pair allowed.
- End-to-end on a fixtures server: no-Origin (CLI / bridge) and loopback-Origin (SPA) POSTs to the
  remoting surface and to both canvas routes → `200`; a cross-origin Origin → `403`.
- Full suite (Unit + Fast + E2E) stays green; E2E exercises the same-origin client path end to end.
