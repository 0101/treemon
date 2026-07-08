# Remoting CSRF / Origin Hardening

Status: **Future / Deferred** — design only. Surfaced by focused-review (finding C-13 /
A-13, *Confirmed · Low*) on the `tm-config-audit` branch. Not implemented.

The branch that surfaced this only **added** three endpoints (`addRoot`/`removeRoot`/
`getRoots`) to an already-exposed surface; it did not create the gap. This spec is about
hardening the *whole* remoting surface, not those three endpoints.

## Problem Statement

The server exposes `IWorktreeApi` over Fable.Remoting with **no authentication and no
Origin/CSRF validation**, bound to loopback. A malicious web page open in the operator's
browser can therefore issue cross-origin `POST http://localhost:5000/IWorktreeApi/<method>`
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
  working unchanged (or with a single, central client change).
- Production keeps its well-known port and loud-on-conflict behavior (see
  `docs/spec/future/port-management.md`); this is orthogonal to where it binds.

## Technical Approach

Add a small middleware *in front of* the Fable.Remoting handler in `buildRemotingHandler`
(or wrapping it in the Saturn pipeline) that rejects forged cross-origin requests before they
reach any method.

**Primary control — Origin/Referer allowlist (no client change):**

- If an `Origin` header is present and is **not** same-origin (`http://localhost:{port}` /
  `http://127.0.0.1:{port}`), reject with `403`.
- If `Origin` is absent, fall back to a `Referer` same-origin check.
- Requests with **neither** header (the `tm` CLI via `HttpClient`, server-to-server) are
  allowed — non-browser clients don't forge CSRF.

Browsers attach `Origin` to cross-origin POSTs (and to same-origin POSTs), so the legitimate
web client passes and the malicious page is blocked, with **zero client changes**. This is
the lowest-friction option.

**Defense in depth — required custom header (one central client change):**

- Require a non-simple custom request header (e.g. `x-requested-by: treemon`) on every
  remoting call. A CORS *simple request* cannot set a custom header without triggering a
  preflight the no-CORS server will fail, so cross-origin pages are blocked at the browser.
- This needs the Fable.Remoting client to be configured to send the header on every call
  (one place, via the client's header/route customization), and the `tm` CLI's `HttpClient`
  to add the same header.

Requiring `Content-Type: application/json` is **necessary but not sufficient** on its own
(confirmed the library doesn't enforce it and `text/plain` bypasses it), so it is not the
control — the Origin check and/or custom-header are.

Recommended: ship the **Origin/Referer allowlist** first (smallest, no client change), and
optionally add the custom-header check as belt-and-suspenders.

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
  hardening; the verified `text/plain` fall-through means it can't stand alone.

## Key Files

- **Server**: `src/Server/Program.fs` — `buildRemotingHandler` (add the Origin/Referer
  middleware here) and the Saturn app builder (`use_router/url/use_static/use_gzip`); the
  loopback `serverUrl` + default port feed the same-origin allowlist value.
- **Shared contract (context)**: `src/Shared/Types.fs` — `IWorktreeApi` (all methods covered
  by the pipeline fix; the roots methods that surfaced this are `addRoot`/`removeRoot`/
  `getRoots`).
- **Implementations (context)**: `src/Server/WorktreeApi.fs` — `addRootToConfig` /
  `removeRootFromConfig` / `writeWorktreeRoots` and the pre-existing process-launching members.
- **Client (only if custom-header option is chosen)**: the Fable.Remoting client setup in
  `src/Client/` (central header/route config) and the `tm` CLI `HttpClient` in
  `src/Cli/Program.fs`.

## Verification

- A cross-origin `text/plain` POST to `removeRoot`/`addRoot` (and a process-launching method)
  is rejected `403` once the middleware is in place.
- The same-origin web client and `tm add` / `tm remove` continue to work unchanged (Origin
  option) or after the single client header change (custom-header option).
- Full suite stays green (Unit + Fast + E2E); E2E exercises the same-origin client path end
  to end.
