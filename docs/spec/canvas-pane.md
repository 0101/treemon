# Canvas Pane

## Goals

- Rich visual channel for agents to present interactive HTML documents to users
- Per-worktree doc management with tabs, archive, and overview-driven discovery
- Awareness so users notice new or updated docs through badges, auto-display, and card notifications
- Per-doc liveness so users can see which docs still have a live author session
- Per-doc message routing so interactions reach the session that authored the doc
- Restart-safe rendering from disk so docs survive session, app, and machine restarts
- Separate origin for doc content so canvas JavaScript is isolated from the app API

## Expected Behavior

### Doc Lifecycle

- Agents create or update `.html` files in `.agents/canvas/`.
- `RefreshScheduler.CanvasScanner` scans those files and computes `ContentHash` from file bytes.
- `CanvasWatchers` keeps one `FileSystemWatcher` per worktree `.agents/canvas/` directory and posts `UpdateCanvasDoc` into the scheduler mailbox when files change.
- The canvas doc server serves each file at `http://127.0.0.1:5002/{encodedWorktreePath}/{filename}`.
- The client renders the selected doc in an iframe whose URL includes the current `contentHash`.
- Disk files are the source of truth, so docs keep rendering after Treemon, session, or computer restart.

### Pane UI

- The pane opens and closes from the header Canvas button and the `C` key.
- Open or closed state persists in global config.
- Position selector supports left, right, top, and bottom docking, and the selected position persists.
- The pane is scoped to the focused worktree. If that worktree has docs, the pane shows its active doc.
- Worktrees with multiple docs show tab buttons. A single doc skips the tab bar.
- Selecting a tab marks that doc viewed.
- Viewed but inactive tabs render at 0.5 opacity. The active tab stays full opacity.
- The archive button moves the active doc to `.agents/canvas/archive/`.

### Canvas Overview

- When no worktree is focused, or the focused worktree has no docs, the pane shows a canvas overview.
- The overview groups worktrees with docs by repository and orders entries by latest canvas activity.
- Clicking a worktree entry focuses that card.
- Clicking a doc entry focuses the worktree, opens the pane if needed, and selects that doc.

### Doc Awareness

- The server persists `LastViewedHashes: Map<worktreePath, Map<filename, contentHash>>` in global config.
- On first load, existing docs are seeded into `LastViewedHashes` so pre-existing docs do not appear new.
- A doc is unviewed when its current `contentHash` differs from the last viewed hash, or no viewed hash exists.
- The Canvas header button shows the total unviewed doc count across all worktrees and hides the badge when the count is 0.
- When the pane is open, selecting a doc or showing the active visible doc marks it viewed.
- If any doc changes while the user has been idle for at least 60 seconds, Treemon opens the pane, focuses the changed worktree, and selects the most recently modified changed doc.
- Worktree cards show yellow canvas notifications for new or updated docs. Notifications expire after 5 minutes, deduplicate by filename, replace `LastUserMessage` while present, and click through to the relevant worktree or doc.
- Viewed tabs dim to 0.5 opacity so unviewed docs stand out.

### Liveness and Session Routing

- Current shipped behavior uses a per-worktree bridge registry keyed by normalized worktree path.
- Session registration for the same worktree is last-writer-wins.
- Each doc records its author `sessionId` via `CanvasDocOwnership.fs`.
- The liveness dot shown in tabs and overview reflects the selected doc's `OwnerSessionId` against `BridgeLiveness`, so liveness is per-doc rather than per-worktree.
- When no live bridge exists for the focused worktree, the pane shows a `▶ Start session` button.
- `LaunchCanvasSession` uses the existing action-launch flow and includes the full absolute doc path plus canvas context in the prompt.
- Canvas messages route to the author session for the selected doc.
- If the author session is dead, Treemon resumes or replaces that specific session without changing doc identity.

### Message Flow

- A canvas doc sends interaction data with `window.parent.postMessage(...)`.
- The Elmish client accepts only messages from `http://127.0.0.1:5002`, validates the payload shape, and turns it into Elmish messages.
- The client forwards valid payloads through Fable.Remoting with `sendCanvasMessage`.
- The server forwards live messages by HTTP POST to the registered bridge `/inject` endpoint.
- The extension bridge calls `session.send()` with the canvas payload.
- Client send state is modeled as `CanvasSendState = Idle | Waiting of queuedAt | Failed of message`.

### Message Queue

- If no session-backed bridge is registered, the server queues the message per worktree and returns `Queued`.
- The queue keeps at most 10 messages per worktree and expires entries after 5 minutes.
- Queued messages drain when a session bridge registers and when a poll heartbeat drains pending work.
- While queued, the client shows a `Waiting for session…` banner instead of an immediate error.
- If the waiting window passes 5 minutes, the client surfaces `Message expired — no session responded`.

### Bridge Protocol

- The session bridge is the extension process started inside a coding session.
- It calls `POST /api/canvas/register` with `worktreePath`, `injectUrl`, and `sessionId`.
- After startup it re-registers every 30 seconds as a heartbeat.
- Failed extension heartbeats back off exponentially up to 120 seconds, then reset after reconnect.
- Served docs receive an injected heartbeat script that posts to `/bridge/heartbeat` every 30 seconds.
- `CanvasBridge` keeps separate `sessionRegistry` and `pollRegistry` maps so poll heartbeats do not overwrite session registrations.
- `GET /api/canvas/bridge-status?worktreePath=...` exposes bridge registration, heartbeat age, liveness, and session ID.

### Doc Server

- The canvas doc server runs on port 5002 and serves HTML from `.agents/canvas/` only.
- Requests use `/{encodedWorktreePath}/{filename}` and are rejected unless the worktree is known and the filename resolves inside `.agents/canvas/`.
- `GET /{encodedWorktreePath}/beads-data` serves beads issue data as JSON for the beadspace dashboard (see `docs/spec/beadspace-canvas.md`).
- The server injects scrollbar CSS, the canvas link interceptor, and the bridge heartbeat script into `</head>`.
- `</head>` replacement is case-insensitive by using `StringComparison.OrdinalIgnoreCase`.
- If no `<head>` close tag exists, the injected content is prepended.
- Running the docs on `:5002` isolates doc JavaScript from the app API on `:5000`.

### 🔮 DOM Morph and State Persistence

Two levels of state preservation are needed:

**Level 1 — In-page data refresh (beadspace and similar polling docs):**
- Docs that poll their own data endpoint (e.g., beadspace polls `beads-data` every 30s) must update the DOM incrementally instead of full teardown-and-rebuild.
- Today, `BeadspaceTemplate.html` does `app.textContent = ''` + `insertAdjacentHTML` on every poll, destroying scroll position, expanded panels, and visual filter state.
- Fix: refactor template render to update stat numbers in-place, replace only `<tbody>`, and preserve scroll/nav/filter state across polls.
- This is per-template work — each polling doc must handle its own incremental updates.

**Level 2 — Canvas-wide iframe morph (general case):**
- On `contentHash` change, an open doc morphs in place instead of reloading the iframe.
- Scroll position, focused elements, and in-progress inputs stay intact across updates.
- Implementation: inject a morph library (e.g., idiomorph) via the existing `Program.fs` `</head>` injection point.
- This is not implemented today; the current system reloads the iframe by changing the `?v={contentHash}` URL.

### Link Handling

- Relative `.html` links and same-origin canvas doc links are intercepted and converted into `navigate-canvas-doc` messages for tab switching.
- External links open in the system browser.

## Technical Approach

- The client is Elmish/Fable. It polls `DashboardResponse`, stores canvas UI state locally, and routes iframe messages through Elmish before calling Remoting.
- The server keeps `CanvasDoc list` in scheduler state beside git, PR, and coding-tool data. `CanvasScanner` computes hashes and `CanvasWatchers` pushes filesystem changes into the same mailbox.
- `Program.fs` hosts the main app/API on port 5000 and the canvas doc server on port 5002. `CanvasBridge` owns live registration, queueing, forwarding, and liveness.
- Security relies on separate origin plus `postMessage`: docs cannot call privileged app endpoints directly, and the parent validates sender origin before forwarding anything.
- Canvas awareness helpers stay pure: `detectCanvasEvents` and `expireCanvasEvents` take `now` as an argument instead of capturing the clock internally.

## Key Files

| File | Purpose |
|---|---|
| `src/Shared/Types.fs` | Shared canvas domain types, API methods, bridge liveness, send results, pane position |
| `src/Client/App.fs` | Elmish model and update logic for pane state, awareness, auto-display, routing, archive, and launch actions |
| `src/Client/CanvasPane.fs` | Pane layout, overview, tab bar, liveness dot, iframe, banners, and message listener |
| `src/Client/Navigation.fs` | `CanvasSendState` DU |
| `src/Client/CanvasAwareness.fs` | Pure helpers for doc awareness: seeding viewed hashes, unviewed detection, canvas events, auto-display |
| `src/Client/index.html` | Canvas layout, badge, tab, banner, liveness, and overview styling |
| `src/Server/RefreshScheduler.fs` | Canvas scanning, content hashing, watcher lifecycle, scheduler state updates |
| `src/Server/WorktreeApi.fs` | Canvas config persistence, archive endpoint, send routing, bridge-liveness API wiring |
| `src/Server/CanvasBridge.fs` | Session registry, poll registry, queueing, liveness, and bridge forwarding |
| `src/Server/BeadspaceTemplate.fs` | Beadspace dashboard HTML template constant for auto-provisioning |
| `src/Server/BeadspaceTemplate.html` | Source HTML for beadspace dashboard template |
| `src/Server/Program.fs` | Canvas register endpoint, bridge status endpoint, doc server, HTML injection, heartbeat route |
| `src/Server/PathUtils.fs` | Canvas path normalization and validation |
| `src/Extension/extension.mjs` | Session bridge registration, `/inject` server, heartbeat, and reconnect backoff |
| `src/Extension/skill/SKILL.md` | Authoring contract for agent-created canvas docs |

## Decisions

- **Separate origin + postMessage** — docs run on `:5002`, the app stays on `:5000`, and Elmish is the only privileged message gate.
- **File as source of truth** — Treemon renders HTML from disk and derives `contentHash` from file bytes instead of keeping a separate live-doc state.
- **Split bridge registry** — `sessionRegistry` and `pollRegistry` are separate so iframe heartbeats cannot clobber session-backed routing.
- **Injected heartbeat script** — agent-authored docs participate in liveness and queued-message drain without extra per-doc setup.
- **`CanvasSendState` DU** — send state is `Idle`, `Waiting`, or `Failed`, avoiding illegal combinations of optional fields.
- **Per-doc author routing** — docs persist ownership by `sessionId`, canvas messages route to the selected doc's owner session, and liveness/resume operate per doc instead of per-worktree.

## Implementation Status

- Shipped: Phase 1-4, multi-doc discovery, overview, toolbar positioning, archive, bridge resilience, awareness, per-doc author routing, liveness indicators, queueing, auto-resume, and review fixes.
- Shipped: Per-doc author routing is implemented via `CanvasDocOwnership.fs` (refactored to MailboxProcessor).
- Shipped: Beadspace canvas dashboard — template customization, same-origin `beads-data` endpoint, auto-provisioning, incremental DOM refresh, empty-DB suppression, E2E verification (17 unit + E2E tests).
- Shipped: Code quality refactors — `CanvasScanner` extracted from `RefreshScheduler`, `CanvasDocServer` extracted from `Program.fs`, `CanvasEventKind` DU replacing bool, tuple pattern matching in `App.fs`.
- 🔮 DOM morph (Level 2 — canvas-wide iframe morph via idiomorph) is not implemented yet. Level 1 (in-page incremental refresh) is shipped for beadspace.

## Related Specs

- `docs/spec/worktree-monitor.md` — parent dashboard architecture spec
- `docs/spec/beadspace-canvas.md` — beads dashboard integration in the canvas pane
