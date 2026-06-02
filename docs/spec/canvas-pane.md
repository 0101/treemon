# Treemon Canvas Pane Investigation

## Problem Statement

Add an interactive **canvas pane** to the Treemon dashboard that gives agents running
in tracked worktrees a rich, visual channel to the user — for decision making, planning,
design, and review — instead of walls of markdown (see
https://thariqs.github.io/html-effectiveness for the style we want to enable).

Requirements:

- A pane in Treemon that can be **open (visible) or closed (hidden)**.
- The pane **belongs to the currently-selected worktree**.
- It supports **multiple long-lived HTML documents**, shown as **tab buttons** to switch between.
- Agents in the worktrees can **write to these docs at any time**.
- Docs **live on disk** and are **picked up after session/computer restart**.
- Docs are **interactive**: user interaction in the HTML can **send messages back to the
  agent's CLI session**.
- Treemon **badges which worktrees have new/updated docs** to view.

The leading implementation idea was a **Copilot CLI extension** (modeled on
`Q:\code\html-extension`) that hosts HTML and bridges messages, keeping it compatible with
other tools.

## Symptoms / Drivers

- Markdown is a poor medium for spatial/comparative/interactive artifacts (diffs, design
  options, plans, dashboards). HTML reads better and can be interactive.
- Treemon already centralizes visibility across worktrees but has no surface for an agent
  to present a rich artifact or collect a structured decision from the user.
- Agents need a durable, restart-safe place to leave artifacts, and a way to receive the
  user's response.

## Evidence Gathered

### Reference: `Q:\code\html-extension` ("HTML Interact")

A Copilot extension (single Node ES-module, `@github/copilot-sdk/extension`) that serves
interactive HTML and exchanges structured data with it.

- **Registration**: code-based via `joinSession({ tools, canvases, hooks })`. No manifest.
  - `extension\extension.mjs:662-708`
- **Tools (CLI mode)**:
  - `show_html_page(html, launch_browser?)` → returns `{ page_id, url }`.
    `extension.mjs:665-684`, return at `:534-540`
  - `send_to_page(page_id, event?, data)` → pushes an event to a connected page.
    `extension.mjs:686-699`
- **Canvas action (GitHub app mode)**: `page` canvas via `createCanvas`, with a `push`
  action. `extension.mjs:557-649`
- **HTTP server**: `http.createServer` bound to **127.0.0.1 only**, on a **deterministic
  high port (49152–65535) seeded from `extensionId + sessionId`**, ephemeral fallback.
  - `extension.mjs:28-33`, `:390-460`, listen at `:395-408`
  - Routes: `GET /page`, `GET /events` (SSE), `POST /_message`, `OPTIONS`.
    `extension.mjs:302-386`
- **URL formats**:
  - tool page: `http://127.0.0.1:<port>/page?token=<page_id>` — `extension.mjs:88-90`
  - canvas page: `http://127.0.0.1:<port>/page?instance=<id>&token=<token>` — `:91-92`
- **Page → agent messaging**: page calls `fetch('/_message')`; server reads body and calls
  `session.send({ prompt })` with the payload tagged
  `[html-interact page_id=<token>] <json>`. Routes **only to the session that owns that
  extension process**. — `extension.mjs:122-128`, `:369-382`, `:208-215`
- **Agent → page**: SSE via `/events`, with `Last-Event-ID` replay of missed updates.
  `extension.mjs:181-206`, `:335-366`
- **Framing**: `/page` sets **no `X-Frame-Options` and no CSP**, and
  `Access-Control-Allow-Origin: *`. So the page **can be embedded cross-origin in an
  iframe**. — `extension.mjs:45-50`, `:302-386`

#### Persistence / restart behavior (critical)

- **Tool pages (`show_html_page`) do NOT persist.** State lives only in an in-memory `pages`
  map; `page_id` is a fresh `randomUUID()` per call. Nothing survives a restart.
  `extension.mjs:34-35`, `:515-540`
- **Canvas pages DO persist to disk** (GitHub-app mode), keyed by `sha256(sessionId\0instanceId)`
  under `COPILOT_HOME/extensions/html-interact/artifacts/<hash>.json`, atomic write.
  `extension.mjs:22-23`, `:217-235`, `:609-625`
- **Port is deterministic only within the same `sessionId`.** A new session (e.g. after a
  computer restart) → **new port and new token → the previous URL is dead.**
  `extension.mjs:28-33`, `:425-440`
- Messaging is bound to the owning live session; **a long-running server does not
  multiplex to other sessions**. New session = new server/session binding.
  `extension.mjs:208-215`, `:662-707`

**Conclusion**: the extension's hosted URL is an *ephemeral live-session artifact*, not a
durable address. It is unsuitable as the source of truth for restart-safe documents.

### Treemon architecture (`Q:\code\tm-canvas48`)

- **Domain types**: `src\Shared\Types.fs:8-196`.
  - `WorktreeStatus` (per-card record): `:108-124` — exposes `Path`, `HasActiveSession`,
    `LastUserMessage`, `CodingTool`, etc.
  - `RepoWorktrees`: `:150-156`; `DashboardResponse`: `:163-171`;
    `IWorktreeApi` contract: `:177-196`.
- **No "selected worktree" in shared state.** Selection is client UI focus only:
  `Navigation.FocusTarget` (`RepoHeader | Card scopedKey`) — `src\Client\Navigation.fs:7-10`;
  stored as `Model.FocusedElement` — `src\Client\App.fs:13-34`. Card click dispatches
  `SetFocus (Some (Card scopedKey))` — `App.fs:1157-1160`.
- **Client is Elmish** (`Program.mkProgram init update view`) — `App.fs:1633-1638`.
  - `Model`: `App.fs:13-34`; `Msg`: `:36-68`.
  - Layout: header `viewAppHeader` `:1548-1595`; `.dashboard` container `:1597-1631`;
    `repoSection` per repo `:1481-1497`; modals `:1625-1628`. A new side pane fits inside
    `view` under `.dashboard`, alongside the repo list/footer/modals `:1600-1629`.
- **API is Fable.Remoting over HTTP** (no REST/SSE/SignalR). Handler built at
  `src\Server\Program.fs:113-120`, mounted with Saturn `use_router` `:174-180`. Methods
  implemented in `src\Server\WorktreeApi.fs:342-564` (e.g. `launchSession`, `focusSession`,
  `killSession`, `resumeSession`, `saveCollapsedRepos`).
- **Real-time updates = client polling + adaptive server refresh.** Client polls worktrees
  every ~1000ms (15000ms when deep-idle) — `App.fs:488-537`. Server scheduler refreshes
  worktree state on an adaptive loop — `src\Server\RefreshScheduler.fs:523-583`. **No push
  channel.**
- **Worktree discovery**: `git worktree list --porcelain` per root — `GitWorktree.fs:64-73`;
  scheduler builds `PerRepoState` (`WorktreeList`, `KnownPaths`) — `RefreshScheduler.fs:10-20`,
  `:345-353`. The server already scans each worktree during refresh — a natural hook to also
  read a per-worktree canvas registry file.
- **Session tracking is path-keyed**, not a durable session id. `SessionManager` maps
  normalized worktree path → window `HWND` — `src\Server\SessionManager.fs:17-20`, `:168-235`.
  Persisted in `data\sessions.json`. `HasActiveSession` is computed by membership in the
  active-session set — `WorktreeApi.fs:66-84`. **Copilot sessions do not survive a computer
  restart.**
- **Per-worktree indicators already exist** (the pattern for a "new docs" badge):
  `cardClassName` adds `has-session` `App.fs:560-564`; coding-tool dot `ct-dot` `:1124-1128`;
  `LastUserMessage` footer `:1203-1212`; event-log entries `:703-753`.
- **On-disk config patterns to reuse**: per-repo `<repoRoot>\.treemon.json`
  (`src\Server\TreemonConfig.fs:11-76`) and global `~\.treemon\config.json` (`:106-140`);
  collapsed-repos persistence via `WorktreeApi.fs:156-185`. All use atomic JSON read/write
  helpers.

### Affected / relevant components

- `src\Shared\Types.fs:108-124,177-196` — add `CanvasDocs` to `WorktreeStatus` and new
  `IWorktreeApi` methods.
- `src\Server\WorktreeApi.fs:342-564` — implement new API methods; populate `CanvasDocs`.
- `src\Server\RefreshScheduler.fs:345-353,475-489` — read each worktree's canvas registry
  during refresh.
- `src\Server\Program.fs:113-180` — add non-remoting HTTP routes for static doc serving
  and the message-forwarding endpoint.
- `src\Client\App.fs:13-68,488-537,1597-1631` — pane state, polling-driven doc list,
  pane view + badges.
- New: a small Copilot CLI extension (bundled in this repo) acting as the **live message
  bridge**.

## Root Cause Analysis (why the obvious approach doesn't fully work)

The intuitive design — "extension `html_host(file)` → URL; agent calls
`treemon_canvas(URL, file)`; Treemon iframes that URL" — fails the **restart-safety**
requirement because the extension's hosted URL is **ephemeral**: it is in-memory, the port
is reseeded per session, and the token is regenerated. After a session/computer restart the
stored URL is dead, so Treemon would render blank/stale iframes.

It also conflates two things that have different lifetimes:

- **Durable**: the document content + identity + "last updated" (must survive restart).
- **Ephemeral**: the live serving URL/port/token and the message channel into a session
  (only valid while a session is alive).

Persisting the ephemeral live URL as if it were durable state is the core mistake. The
correct model separates them: a durable doc registry on disk, and a live "bridge" that is
discovered/owned at runtime.

### Contributing factors

- Treemon is **path-keyed**; the extension is **session-process-keyed**. Mapping between
  them is implicit and breaks when multiple sessions run in one worktree.
- Treemon has **no push channel** and **no inbound generic HTTP endpoint** today (only
  Fable.Remoting), so message-forwarding needs new server routes.
- Liveness cannot be reliably inferred from a cross-origin iframe load.

## Chosen Solution — Option C′: Treemon hosts display; extension is the live bridge

*(Selected by the user. Recommended by the investigation and the design critique.)*

**Split durable vs. ephemeral. Treemon owns durable display + UI + liveness. The extension
owns only the ephemeral message bridge into a live session.**

### Components

1. **Agent writes HTML to disk** in the worktree at **`.agents/canvas/`** (e.g.
   `.agents/canvas/design-options.html`). These are the durable artifacts.

2. **Durable per-worktree registry** `.agents/canvas/registry.json`, written atomically
   (temp + rename) by the extension/agent, listing docs only by **durable identity**:
   ```json
   {
     "version": 1,
     "docs": [
       { "id": "design-options", "path": "design-options.html",
         "title": "Design Options" }
     ]
   }
   ```
   No `url`/`port`/`token`/`contentHash` here — those are ephemeral or computed by Treemon
   (hash from file bytes; see resolved decision #4). Only durable identity is persisted.

3. **Treemon hosts all display** at stable URLs on a **separate origin** (`:5002`), e.g.
   `GET /{worktreePath}/{docId}` served from disk under `.agents/canvas/`
   (path-validated to stay within that directory). Survives restart; one render path.
   The separate origin ensures doc JS cannot reach the Fable.Remoting API on `:5000`.

4. **Treemon scheduler reads `registry.json`** per worktree during its normal refresh and
   includes the doc list in `WorktreeStatus.CanvasDocs`. Treemon computes `contentHash`
   from file bytes (not from the registry). The client polls as today — no new push channel
   needed for discovery.

5. **Canvas pane (client)**: a collapsible pane bound to the focused worktree, showing
   **tab buttons per doc** and an **iframe to Treemon's stable URL**. **Badges** mark
   worktrees whose `contentHash` (Treemon-computed) changed since the client's "last viewed"
   state (hash-based, not live-URL based).

6. **Live message bridge (the only extension responsibility)**:
   - On startup in a worktree (cwd), the extension **registers with Treemon's server**
     ("worktree X is live; my inject endpoint is `http://127.0.0.1:<port>/inject`") as a
     short-lived **lease/heartbeat**, so Treemon — not the iframe — owns liveness state
     (`WorktreeStatus.CanvasLive`).
   - User interaction in a doc calls `window.parent.postMessage(...)` to the Elmish parent
     (works across origins); the client validates `event.origin`, dispatches a `Msg`, and
     forwards via Remoting to the server, which **POSTs** to the registered live bridge,
     which calls `session.send(...)` into the owning session.
   - When no live bridge exists (post-restart), the pane is **read-only** and offers
     **launch/resume session** (reusing Treemon's existing `launchSession`/`resumeSession`)
     to restore interactivity.

7. **Bundled reference templates**: ship a few `html-effectiveness`-style templates
   (planning / review / design) so agents produce consistent-looking docs.

### Pros

- **Restart-safe display**: docs always render from disk at stable URLs; no dead iframes.
- **Single render path** (Treemon-served), avoiding extension-vs-fallback divergence in
  base URLs, CORS, and relative assets.
- **Central liveness + message routing** in Treemon; the iframe is never the source of truth.
- **Clean separation** of durable doc state from ephemeral session bridge.
- **Fits Treemon's existing model**: per-worktree files read during refresh; client polling;
  path-keyed worktrees; existing per-card badge patterns; existing launch/resume.
- Still **extension-based** for the agent-facing/interactive part, so it stays usable by
  other tools that adopt the same registry + bridge contract.

### Cons

- More **Treemon server work** than the pure-extension approach: static doc serving + a
  message-forwarding endpoint + an in-memory liveness/lease registry (new non-remoting
  HTTP routes in `Program.fs`).
- Requires a small **bidirectional bridge protocol** between extension and Treemon
  (register/heartbeat + forward).
- Relative-asset and path-traversal **policy** must be defined for served docs.

### Implementation Considerations

- **Doc identity**: prefer stable `id` + normalized relative `path` + `contentHash` over
  filename alone (rename-safe, collision-safe, traversal-safe). Validate resolved paths stay
  under `.agents/canvas/`.
- **Atomic registry I/O**: temp-file + rename; include `version`; on parse failure keep the
  previous good snapshot to avoid flicker (mirror `TreemonConfig.fs` patterns).
- **Multiple sessions per worktree**: define explicit ownership — a single interactive
  bridge per worktree selected by Treemon, or per-doc owner — using **leases with
  heartbeats**, not last-writer-wins, to avoid wrong-session routing.
- **Liveness**: derive `CanvasLive` from the Treemon-side lease/heartbeat (and/or a
  server-side probe), **not** from iframe load behavior.
- **Badges**: compare registry `updatedAt`/`contentHash` against a client-persisted
  "last viewed" map per `(worktree, docId)`.
- **End-to-end wiring of a new remoting method** (per the explored pattern): add to
  `IWorktreeApi` (`Types.fs:177-196`) → implement in `WorktreeApi.fs:384-564` (reuse
  `withValidatedPath` `:362-373`) → add `Msg` + `Cmd.OfAsync` in `App.fs` → render in `view`.
- **Selected-worktree binding**: resolve the focused `WorktreeStatus` from
  `Model.FocusedElement` to drive which worktree's docs the pane shows.

### Decision Notes

- **`sandbox` = false (original user choice — now superseded).** The critique flagged that
  agent-written HTML which can inject prompts into a live session is a **trust boundary**. The
  resolution (see *Resolved Design Decisions*) is stronger than sandboxing: serve docs from a
  **separate origin** so they cannot reach the app API at all, and route the single allowed
  action through the Elmish parent via `postMessage`. The iframe need not be sandboxed for
  interactivity, because origin isolation already removes its dangerous capabilities.
- **Interactivity after restart = read-only + launch/resume** (user choice): honest UI —
  "read-only: no live agent session", with a button to launch/resume.

## Resolved Design Decisions (post GPT‑5.5 review)

Decisions captured interactively (via an html-interact decisions page). These resolve the
review's blocking gaps and **supersede** the earlier `sandbox=false` / same-origin notes.

| # | Decision | Choice |
|---|----------|--------|
| 1 | Message routing / ownership | **Per-doc author session (by sessionId)** + liveness + resume-on-interact (see below) |
| 2 | Doc origin & security | **Separate origin / port** (e.g. `127.0.0.1:5002`) serving only static docs + one message route |
| 3 | Message path (doc → agent) | **doc → `parent.postMessage` → Elmish client** → Remoting → server → session |
| 4 | contentHash authority | **Treemon computes hash from file bytes**; registry holds only `id`/`path`/`title` |
| 5 | On-change update | **DOM-morph** (idiomorph/morphdom), preserving scroll/focus/inputs |
| 6 | `worktreeId` scheme | **`base64url(sha256(normalizedPath))`** (normalize case/sep first) |

### Ownership model (refined by user — supersedes "per-worktree single owner")

Each doc records its **authoring `sessionId`**. Routing and liveness are per-doc:

- **Liveness dot** — Treemon maps each author `sessionId` → alive/dead via the extension
  bridge **heartbeat**. 🟢 alive / ⚪ dead, shown in the doc/tab UI.
- **Resume-on-interact** — interacting with a doc whose author session is dead **resumes that
  session in the worktree terminal** (Treemon already does this, see
  `docs/spec/resume-last-session.md` / `native-session-management.md`), **queues** the message,
  and delivers it once the bridge re-registers.
- **"Start new session with this doc as context"** — a separate button that launches a fresh
  session seeded with the doc, independent of the original author.
- **Phaseable** — v1 can ship as: dot + "author offline → queue & show waiting" without
  auto-resume; resume/new-session land later.

Three details to pin down during planning (not blockers):

1. **Resume must re-bind the same `sessionId`** so a queued message routes back to the right
   doc/worktree — otherwise the resumed process is effectively a new session and the queue
   can't target it. Confirm the resume mechanism preserves/reports the original id.
2. **Queue needs expiry + UI feedback** ("waiting for session…") and **click coalescing** so
   repeated clicks don't pile up duplicate prompts.
3. **Liveness requires the bridge to heartbeat its `sessionId`** so Treemon can map
   `sessionId` → alive; the bridge contract must include it.

### Why separate-origin + postMessage (security, and how they compose)

The doc is treated as **untrusted**. Two layers, browser-enforced where possible:

- **Separate origin** (docs on `:5002`, app + Fable.Remoting API on `:5000`): the browser's
  same-origin policy stops doc JS from reaching `launchSession`/`killSession` or any app API.
  The doc's only same-origin surface is static files — no app capabilities at all. This is
  strictly stronger than `sandbox=false` on the app origin and **resolves the trust-boundary
  concern without sandboxing interactivity**.
- **postMessage → parent** is *not* blocked by separate origin — `postMessage` is designed to
  cross origins. The doc calls `window.parent.postMessage(payload, "http://127.0.0.1:5000")`
  (pinning the target origin); the Elmish client's `window` `message` subscription validates
  `event.origin === "http://127.0.0.1:5002"` (pinning the sender) before dispatching a `Msg`.
  **Elmish is the parent page and the single trusted gatekeeper** — the doc can do nothing but
  hand it a message; the client decides what's allowed and forwards via Remoting.

So the doc has exactly **one** outbound capability (post a message to its parent), and the
client is the only thing that can call privileged APIs. (The rejected "direct fetch"
alternative would bypass Elmish and force the doc to carry the endpoint + a token.)

## Update Model (interaction → content refresh)

**Chosen: the agent is the sole writer of document content; Treemon never writes it.**
Content flows one way (agent → file → Treemon → page); messages flow the other way
(page → Treemon → extension → agent). There is **no direct agent→DOM push** in the base
design.

### Round-trip for an in-doc action (e.g. "expand on this")

1. User clicks a control in the doc → doc calls `window.parent.postMessage({ docId, action, … })`
   (doc is served from the **separate canvas origin**; it cannot call the app API directly).
   The Elmish client's `message` subscription validates `event.origin` and dispatches a `Msg`.
2. The client forwards via Remoting; Treemon makes a **one-shot POST** to the **author
   session's** extension `/inject` endpoint → extension calls `session.send("[canvas <id>] …")`.
   If that session is **dead**, Treemon **resumes** it and **queues** the message until its
   bridge re-registers (see Ownership model above).
3. The agent receives the turn and **edits the HTML file on disk** in `.agents/canvas/`.
4. Treemon detects the changed `contentHash` and the open page **re-fetches its stable URL
   and DOM-morphs** (idiomorph/morphdom-style) so only changed nodes update — scroll, focus,
   and form inputs are preserved. Feels near-live without a second source of truth.

### Why file-as-source-of-truth (not live DOM patching)

- **Restart-safe by construction** — disk is canonical; no replay log of updates to persist.
- **No divergence** between what's on screen and what's on disk.
- **Agents are already excellent at editing files**; no bespoke DOM-patch protocol needed.
- The flicker/state-loss cost of a naive reload is removed by the DOM-morph step.
- Live agent→DOM streaming is intentionally **out of scope for v1**; add it later only if a
  streaming use case (token stream, animation) demands it, as an opt-in layer.

Purely-local interactions (toggle a section, tick a checkbox) need **no round trip** — that
is just in-page JS. The extension bridge is used **only** when the user wants the agent to
change/add content or to record a decision.

### File watching → scheduler state machine

Treemon's server state is an actor: `MailboxProcessor<StateMsg>` with an immutable
`DashboardState` fold (`RefreshScheduler.fs:54`, `:99`, `:179`). The scheduler already
drives it via `agent.Post(...)`. A file-watcher slots straight in:

- Add a `StateMsg` case, e.g. `UpdateCanvasDocs of RepoId * path * CanvasDoc list`.
- Run **one `FileSystemWatcher` per worktree's `.agents/canvas/` dir** (filter
  `registry.json` + `*.html`). On change, re-read that worktree's registry and
  `agent.Post(UpdateCanvasDocs …)`; `processMessage` folds it into state, and the next
  client poll carries the new docs/hashes.
- **Watch all worktrees, not just the open doc**: the "new doc" badges on *non-selected*
  cards need change events across all worktrees. The open doc is just one consumer.

Caveats (all minor):
- `FileSystemWatcher` fires **multiple events per save** (temp+rename, repeated `Changed`).
  Harmless here — a re-read with an unchanged `contentHash` is a no-op; optional light
  debounce.
- Handle the `Error`/buffer-overflow event by triggering a **full re-scan** of that worktree.
- One handle per worktree dir (fine for dozens); **dispose** on shutdown and when a root is
  removed.
- The watcher freshens **server** state instantly (great for badges and beats the 15 s
  deep-idle poll backoff). The **browser** still learns via its existing ~1 s active poll
  carrying the hash — adequate for v1. A browser SSE push is an optional later upgrade for
  sub-second open-doc morphing while idle.

### Client (MVU / Elmish)

The client is already Elmish, and the pane is a natural MVU fit:

- **Model**: `CanvasPaneOpen`, `ActiveDocByWorktree`, `LastViewedHashes`. The doc list itself
  arrives on the existing `DashboardResponse` poll and flows through `update`.
- **Msg**: `ToggleCanvasPane`, `SelectCanvasDoc`, `MarkDocViewed`, `CanvasMessageFromDoc`.
- **View**: pure render of tabs/badges/iframe from the model.

Two edges are imperative and handled the standard Elmish way (not in the VDOM diff):

- **The iframe content + morph-reload** is a foreign-DOM *island*. Trigger the fetch+morph
  from a `Feliz.useEffect` keyed on the open doc's `contentHash` (or a `Cmd`).
- **User clicks inside the iframe** arrive via `postMessage`, not `dispatch`. Capture them
  with a **subscription** (a `window` `message` listener) that converts them into a
  `CanvasMessageFromDoc` `Msg`; `LastViewedHashes` persistence is a `Cmd` side effect.

## Impact Assessment

- **Scope**: new client pane + state; new shared types; new server static + bridge routes +
  registry reading in the scheduler; a new bundled extension. No change to existing worktree
  discovery/refresh semantics.
- **Risk Level**: Medium. The bridge protocol, multi-session ownership, and the new
  non-remoting HTTP routes are the riskiest parts; display/registry reading is low-risk and
  fits existing patterns.
- **Breaking Changes**: None to existing behavior. Adding fields to `WorktreeStatus` and
  methods to `IWorktreeApi` requires recompiling client+server together (Fable.Remoting
  contract), which is the normal flow here.
- **Security**: docs are served from a **separate origin/port** so doc JS cannot reach the
  Fable.Remoting API (browser-enforced); the only outbound channel is `postMessage` to the
  Elmish parent, which validates `event.origin` and is the sole privileged caller. Validate
  served paths stay within `.agents/canvas/` (path-traversal). This supersedes the earlier
  `sandbox=false` same-origin note.
- **Testing Requirements**: registry parse/atomic-write + path validation (unit); scheduler
  surfacing `CanvasDocs` (unit); message forwarding bridge happy-path + no-live-bridge
  fallback; E2E (Playwright) for pane open/close, tab switching, badge on update, and
  read-only state when no session — asserting on CSS classes/DOM structure per repo convention.

## Verification Strategy

- **Display**: write a doc to `.agents/canvas/`, confirm it appears as a tab and renders in
  the iframe from a stable Treemon URL; restart Treemon and confirm it still renders.
- **Badges**: update a doc's content/`updatedAt`; confirm the worktree badges as "new" until
  viewed.
- **Interactivity (live)**: with a session running, click a control in the doc and confirm
  the prompt arrives in the owning session.
- **Restart/no-session**: kill the session; confirm the pane goes read-only and the
  launch/resume affordance restores interactivity.
- **Robustness**: write the registry concurrently while the scheduler reads; confirm no
  flicker/parse errors (atomic write + previous-snapshot fallback).
- **Multi-session**: open two sessions in one worktree; confirm messages route to the
  Treemon-selected owner and the registry isn't clobbered.

## Phasing

Phase 1 (MVP) is specified separately in `docs/spec/canvas-pane-mvp.md` and tracked as
beads feature `tm-canvas48-4cn`. It validates the core end-to-end loop with one doc per
worktree, no registry, no badges, iframe reload (not morph), and no liveness/queue.

Completed phases:

2. **UX polish + logging** ✅ (`tm-canvas48-441`): canvas position selector, persist canvas
   open/closed state, comprehensive lifecycle logging (CanvasScanner, CanvasWatcher, doc
   server, CanvasBridge). `C` key already worked — covered by existing E2E tests.
3. **Multi-doc + discovery** ✅ (`tm-canvas48-441`): multiple docs per worktree with tabs
   (single doc = no tab bar), `LastModified` on `CanvasDoc`, `CanvasDocs` list throughout
   pipeline. **Empty canvas overview** — all worktrees with docs grouped by repo, clickable
   to focus. 17 canvas E2E tests passing.
3.5. **Toolbar consolidation + doc archive** ✅ (`tm-canvas48-l7t`, spec:
   `docs/spec/canvas-toolbar-archive.md`): unified header bar (tabs + position buttons at
   0.4 opacity), doc archive, inline doc names in overview, canvas authoring skill,
   diagnostics logging. 22 canvas E2E tests passing.
3.6. **Bridge resilience + UX polish** ✅ (`tm-canvas48-8ge`, spec:
   `docs/spec/canvas-bridge-resilience.md`): extension auto-reconnect heartbeat (30s with
   backoff), persistent error banner, bridge health endpoint, archive icon fix, scrollbar
   CSS injection.

Roadmap for future phases is maintained in the canvas doc `.agents/canvas/roadmap.html`.
