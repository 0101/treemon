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

### Canvas Doc Kinds

Every `CanvasDoc` carries a `Kind` (`src/Shared/Types.fs`), set when `CanvasScanner` scans the file via `CanvasDocKind.classify filename`:

- **`AgentDoc`** — authored and owned by a coding session; interactive and file-driven. This is the default for any `.html` an agent writes to `.agents/canvas/`.
- **`SystemView`** — server-generated, data-driven, with no owner session. Currently only the beads dashboard (`beads.html`; see `docs/spec/beadspace-canvas.md`). `classify` is the single place to register future generated views (e.g. a CI/build view).

The session-document machinery exists for an interactive document authored and owned by a live session. A `SystemView` is none of those, so the behaviors below are gated on `Kind` — making misfit states (a permanently "dead" liveness dot, a meaningless Start-session, a morph that stomps a self-rendering dashboard) unrepresentable rather than emergent from `OwnerSessionId = None`:

| Behavior | `AgentDoc` | `SystemView` |
|---|---|---|
| Iframe, docking/position, pane real-estate | yes | yes |
| Link interception + scrollbar styling | yes | yes |
| Tab-strip entry | normal tab | distinct far-left `.canvas-system-tab` (BD glyph + issue count), no liveness dot |
| Liveness dot | yes | no |
| `▶ Start session` button | yes | no |
| Message bridge (heartbeat + session routing) | yes | no |
| DOM morph (idiomorph runtime + controller + signal) | yes | no |
| Content-hash awareness (unviewed badge, auto-display, card notification) | yes | no — beads "newness" lives on the card as `BeadsSummary` |
| Archive button | yes | no (server-regenerated, not user-owned) |

The beads dashboard sits in three layers relative to the generic pane, which is why the gating above lands where it does:

- **Genuinely shared (kept):** scan + hash (`CanvasScanner`), serve + inject (`CanvasDocServer` on `:5002`), the pane shell (tabs, iframe, docking, overview), and disk-as-source-of-truth.
- **Beads-specific (already special):** auto-provisioning (`BeadspaceProvisioner`) and the private `/beads-data` JSON endpoint.
- **Inherited but a misfit (gated off for `SystemView`):** the liveness dot (always "dead" — no owner session), `▶ Start session` (meaningless for a generated view), the message bridge (no author session to route to), content-hash awareness (inert — the file hash is stable while the data changes), and morph (redundant with the dashboard's own refresh, and actively harmful when it fires: a deploy/template change would morph the live, JS-rendered dashboard back down to the empty template shell).

A `SystemView` drives its own updates: the beads dashboard polls `/beads-data` every 30s and refreshes in place, so it needs neither morph nor the bridge heartbeat.

### Doc Lifecycle

- Agents create or update `.html` files in `.agents/canvas/`.
- `RefreshScheduler.CanvasScanner` scans those files and computes `ContentHash` from file bytes.
- `CanvasWatchers` keeps one `FileSystemWatcher` per worktree `.agents/canvas/` directory and posts `UpdateCanvasDoc` into the scheduler mailbox when files change.
- The canvas doc server serves each file at `http://127.0.0.1:5002/{encodedWorktreePath}/{filename}`.
- The client renders the selected doc in an iframe with a stable URL (no cache-buster query param). Content updates are delivered via morph signaling (see DOM Morph and State Persistence, Level 3).
- Disk files are the source of truth, so docs keep rendering after Treemon, session, or computer restart.

### Pane UI

- The pane opens and closes from the header Canvas button and the `C` key.
- Open or closed state persists in global config.
- Position selector supports left, right, top, and bottom docking, and the selected position persists.
- The pane is scoped to the focused worktree. If that worktree has docs, the pane shows its active doc.
- Worktrees with multiple docs show tab buttons. A single `AgentDoc` skips the tab bar; a lone `SystemView` still shows its `.canvas-system-tab` entry so its beads-count badge stays visible.
- Selecting a tab marks that doc viewed.
- Viewed but inactive tabs render at 0.5 opacity. The active tab stays full opacity.
- The archive button moves the active doc to `.agents/canvas/archive/`. It is shown only when the active doc is an `AgentDoc` — a `SystemView` is server-regenerated, not user-owned, so it has no archive button.

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
- `SystemView` docs are excluded from content-hash awareness at the source (the `awarenessDocs` filter in `CanvasAwareness.fs`): they never contribute to unviewed counts, card notifications, seeded viewed-hashes, or the idle auto-display target. The beads file hash is stable while its data changes, so it would never signal real newness — and it would morph-signal spuriously on a template/deploy edit. Beads "newness" is surfaced on the worktree card as `BeadsSummary` instead.
- Viewed tabs dim to 0.5 opacity so unviewed docs stand out.

### Liveness and Session Routing

- The bridge registry is keyed by `sessionId`, so multiple sessions in one worktree coexist instead of overwriting a single per-worktree slot (see `docs/spec/canvas-doc-ownership.md`).
- Each doc records its author `sessionId` via `CanvasDocOwnership.fs`.
- The liveness dot shown in tabs and overview reflects the selected doc's `OwnerSessionId` against `BridgeLiveness`, so liveness is per-doc rather than per-worktree. It renders only for `AgentDoc` docs (via `livenessDotFor`); a `SystemView` has no owner session and shows no liveness dot.
- When no live bridge exists for the focused worktree, the pane shows a `▶ Start session` button — only when the active doc is an `AgentDoc` (starting a session for a server-generated `SystemView` is meaningless).
- `LaunchCanvasSession` uses the existing action-launch flow and includes the full on-disk doc path (`{worktree}/.agents/canvas/{filename}`) plus canvas context in the prompt, so the agent is pointed at the real file the doc server serves. That path is built once by `CanvasPrompt.continueWorking` in `src/Shared/Types.fs` — the single source of truth shared by the client launch and server auto-spawn flows.
- Canvas messages route to the author session for the selected doc.
- If the author session is dead, Treemon resumes or replaces that specific session without changing doc identity.

### Message Flow

- A canvas doc sends interaction data with `window.parent.postMessage(...)`.
- The Elmish client accepts only messages from `http://127.0.0.1:5002`, validates the payload shape, and turns it into Elmish messages.
- The client forwards valid payloads through Fable.Remoting with `sendCanvasMessage`.
- The server forwards live messages by HTTP POST to the registered bridge `/inject` endpoint.
- The extension bridge calls `session.send()` with the canvas payload.
- Client send state is modeled as `CanvasSendState = Idle | Waiting of scopedKey: string | Failed of message: string`. The `Waiting` case carries the target worktree's `scopedKey` rather than a timestamp, so the banner is cleared only by that worktree's delivery (see Message Queue and the `CanvasSendState` decision).

### Message Queue

- If no live bridge can take the message, the server queues it per worktree (cap 10, 5-min TTL) and returns `Queued`. Draining is owner-aware — see `docs/spec/canvas-doc-ownership.md`.
- While queued, the client shows a `Waiting for session…` banner instead of an immediate error.
- The banner clears to `Idle` only when the target worktree's session actually delivers (never flipped to `Failed` by a wall-clock timer). The user may dismiss it manually, and the server may silently expire the message after its TTL.

### Bridge Protocol

- The session bridge is the extension process started inside a coding session.
- It calls `POST /api/canvas/register` with `worktreePath`, `injectUrl`, and `sessionId`.
- Registration is loopback-only: `/api/canvas/register` accepts an `injectUrl` only when it is an absolute `http(s)` URL whose host is a loopback IP (`IPAddress.IsLoopback`) or the literal `localhost` (rejected `400` otherwise), and only for a known worktree (`isKnownWorktree`, mirroring the heartbeat and doc routes; unknown worktree → `404`). The route is wired with the scheduler agent, so demo mode (no agent) omits it entirely.
- After startup it re-registers every 30 seconds as a heartbeat.
- Failed extension heartbeats back off exponentially up to 120 seconds, then reset after reconnect.
- Served docs receive an injected heartbeat script that posts to `/bridge/heartbeat` every 30 seconds.
- `CanvasBridge` keeps separate `sessionRegistry` and `pollRegistry` maps so poll heartbeats do not overwrite session registrations.
- `GET /api/canvas/bridge-status?worktreePath=...` exposes bridge registration, heartbeat age, liveness, and session ID.

### Doc Server

- The canvas doc server runs on port 5002 and serves HTML from `.agents/canvas/` only.
- Requests use `/{encodedWorktreePath}/{filename}` and are rejected unless the worktree is known and the filename resolves inside `.agents/canvas/`.
- `GET /{encodedWorktreePath}/beads-data` serves beads issue data as JSON for the beadspace dashboard (see `docs/spec/beadspace-canvas.md`).
- The server injects into `</head>` per doc kind via `CanvasDocServer.buildInjection`: both kinds receive scrollbar CSS and the canvas link interceptor; an `AgentDoc` additionally receives the bridge heartbeat script, the idiomorph runtime, and the morph controller, whereas a `SystemView` receives none of those three.
- `</head>` replacement is case-insensitive by using `StringComparison.OrdinalIgnoreCase`.
- If no `<head>` close tag exists, the injected content is prepended.
- Running the docs on `:5002` isolates doc JavaScript from the app API on `:5000`.

### DOM Morph and State Persistence

Three layers of state preservation:

**Level 1 — In-page data refresh (shipped):**
- Per-template incremental DOM updates. Beadspace template shipped with stat-in-place, tbody-only table refresh, and scroll/nav/filter/panel preservation.

**Level 2 — Auto-display guard:**
- When the canvas pane is open and showing a doc, the auto-display idle trigger (60s idle + changed doc) must not steal focus to a different worktree.
- Auto-display may still fire when the pane is closed or showing the overview.
- Without this guard, the iframe unmounts when focus switches, destroying all JS state. On switch-back the iframe remounts fresh, resetting nav tabs (e.g., beadspace "All Issues" jumps to "Dashboard").

**Level 3 — Canvas-wide iframe morph (general case):**
- On `contentHash` change, the open doc morphs in place (via injected idiomorph) instead of reloading the iframe, so scroll position, focused elements, and in-progress inputs stay intact. The client sends a `content-updated` postMessage on hash change and keeps the iframe `src` stable rather than reloading it.
- Applies to `AgentDoc` docs only — a `SystemView` is served without the morph runtime and is never sent a morph signal, because it self-refreshes from its own data endpoint (see Canvas Doc Kinds).

**Level 4 — Tab switch persistence:**
- When switching between canvas doc tabs in the same worktree, keep the previous iframe mounted but hidden (`display: none`) instead of unmounting it.
- On switch back, unhide the existing iframe — all JS state, scroll, and form inputs are intact.
- Limit to a reasonable cap (e.g., 3 live iframes) to avoid memory bloat; evict least-recently-used beyond the cap.

### Link Handling

- Relative `.html` links and same-origin canvas doc links are intercepted and converted into `navigate-canvas-doc` messages for tab switching.
- The interceptor resolves a same-origin `.html` link to a bare filename even when the href carries a `?query` or `#hash` suffix, so `status.html?tab=errors` and `status.html#top` both navigate to the `status.html` tab.
- External links open in the system browser.

## Technical Approach

- The client is Elmish/Fable. It polls `DashboardResponse`, stores canvas UI state locally, and routes iframe messages through Elmish before calling Remoting.
- The server keeps `CanvasDoc list` in scheduler state beside git, PR, and coding-tool data. `CanvasScanner` computes hashes and `CanvasWatchers` pushes filesystem changes into the same mailbox.
- `Program.fs` hosts the main app/API on port 5000 and the canvas doc server on port 5002. `CanvasBridge` owns live registration, queueing, forwarding, and liveness.
- Security relies on separate origin plus `postMessage`: docs cannot call privileged app endpoints directly, and the parent validates sender origin before forwarding anything.
- Canvas awareness helpers stay pure: `detectCanvasEvents` and `expireCanvasEvents` take `now` as an argument instead of capturing the clock internally.
- The Elmish `update` reads no wall-clock; time enters only via message payloads (e.g. a `Tick` carrying `now`), keeping `update` a pure function of `(model, msg)`.

## Key Files

| File | Purpose |
|---|---|
| `src/Shared/Types.fs` | Shared canvas domain types (incl. `CanvasDocKind` + `CanvasDocKind.classify`), API methods, bridge liveness, send results, pane position |
| `src/Client/App.fs` | Elmish `init`/`update` logic and views for awareness, auto-display, routing, archive, and launch actions (the `Model`/`Msg` types and shared plumbing live in `AppTypes.fs`; the canvas model slice in `CanvasState.fs`; the canvas `update`-arm bodies in `CanvasUpdate.fs` — each canvas arm here is a one-line delegation) |
| `src/Client/AppTypes.fs` | Foundation module: the Elmish `Model` + `Msg` types plus shared plumbing (`worktreeApi` lazy proxy, `findWorktree`, `saveCollapsedReposCmd`) used by both `App.fs` and the canvas update arms. Compiled after `CanvasState.fs` and before `CanvasUpdate.fs`/`App.fs` so canvas update logic can be lifted out of `App.fs` without a cyclic reference. Type relocation only — `update` stays a single function in `App.fs`. |
| `src/Client/CanvasUpdate.fs` | Canvas `update`-arm bodies extracted from `App.fs` (Toggle/SetPosition/Select/Open/Archive(+Result)/Navigate/MessageReceived/SendResult/Dismiss/LaunchCanvasSession/Morph*), the shared canvas helpers (`activeVisibleDoc`, `isKnownCanvasDoc`, `markVisibleDocCmd`), and the `messageListener` subscription glue. App.fs delegates one arm → one function. Compiled after `AppTypes.fs` and before `App.fs`. Body extraction only — `update` stays one function (no sub-`Msg`/`Cmd.map`). |
| `src/Client/CanvasState.fs` | Canvas pane model slice — the `CanvasState` record (compiled before `App.fs`, nested as `Model.Canvas`) plus pure helpers `touchVisitedDoc`, `canvasDocKind`, `activeVisibleDoc`, `markVisibleDocCmd`, and the `MaxLiveIframes` cap |
| `src/Client/CanvasPane.fs` | Pane layout, overview, tab bar, liveness dot, iframe, banners, and message listener |
| `src/Client/Navigation.fs` | `CanvasSendState` DU |
| `src/Client/CanvasAwareness.fs` | Pure helpers for doc awareness: seeding viewed hashes, unviewed detection, canvas events, auto-display |
| `src/Client/index.html` | Canvas layout, badge, tab, banner, liveness, and overview styling |
| `src/Server/RefreshScheduler.fs` | Canvas scanning, content hashing, watcher lifecycle, scheduler state updates |
| `src/Server/WorktreeApi.fs` | Canvas config persistence, archive endpoint, send routing, bridge-liveness API wiring |
| `src/Server/CanvasBridge.fs` | Session registry, poll registry, queueing, liveness, and bridge forwarding |
| `src/Server/BeadspaceTemplate.fs` | Reads the embedded `BeadspaceTemplate.html` resource at startup for auto-provisioning |
| `src/Server/BeadspaceTemplate.html` | Beadspace dashboard HTML — single source of truth (embedded into the Server assembly) |
| `src/Server/Program.fs` | Canvas register endpoint, bridge status endpoint, doc server, HTML injection, heartbeat route |
| `src/Server/PathUtils.fs` | Canvas path normalization and validation |
| `src/Extension/extension.mjs` | Session bridge registration, `/inject` server, heartbeat, and reconnect backoff |
| `src/Extension/skill/SKILL.md` | Authoring contract for agent-created canvas docs |

## Decisions

- **Separate origin + postMessage** — docs run on `:5002`, the app stays on `:5000`, and Elmish is the only privileged message gate.
- **File as source of truth** — Treemon renders HTML from disk and derives `contentHash` from file bytes instead of keeping a separate live-doc state.
- **Split bridge registry** — `sessionRegistry` and `pollRegistry` are separate so iframe heartbeats cannot clobber session-backed routing.
- **Injected heartbeat script** — agent-authored docs participate in liveness and queued-message drain without extra per-doc setup.
- **`CanvasSendState` DU** — send state is `Idle`, `Waiting of scopedKey`, or `Failed of message`, avoiding illegal combinations of optional fields. `Waiting` carries **only** the target worktree's `scopedKey` (`WorktreePath.value`, the same key space as `agentChangedDocs`); the earlier `queuedAt` timestamp and the wall-clock failure timer were removed (Finding C-02) because a queued message lives in the server-side queue and is delivered when its *target* session registers, so `Waiting` is cleared on delivery (`clearWaitingOnDelivery`) and is never reported as a failure on a timer. `CanvasSendResult` likewise dropped its `now` argument, removing two `Date.now()` reads from the send command and keeping `update` wall-clock-free.
- **Per-doc author routing** — docs persist ownership by `sessionId`, canvas messages route to the selected doc's owner session, and liveness/resume operate per doc instead of per-worktree.
- **Two canvas doc kinds** — `CanvasDoc.Kind` (`AgentDoc | SystemView`, classified by filename in `CanvasScanner`) gates the session-document machinery. A `SystemView` (currently only the beads dashboard) opts out of liveness, Start-session, the message bridge, morph, content-hash awareness, and archiving, and gets a distinct far-left `.canvas-system-tab` affordance instead of a normal doc tab. This makes the misfit states unrepresentable rather than emergent from `OwnerSessionId = None`.
- **Tab switch lazy morph** — when switching to a previously hidden iframe, unconditionally dispatch `MorphActiveDoc` so the morph controller fetches fresh content. If the content hasn't changed, idiomorph diffs to zero changes (no-op). This avoids tracking per-iframe content hashes while keeping hidden iframes up to date.
- **`Model`+`Msg` lifted into `AppTypes.fs`** — the Elmish `Model` and `Msg` types, plus the shared plumbing the canvas update arms need (`worktreeApi`, `findWorktree`, `saveCollapsedReposCmd`), live in `src/Client/AppTypes.fs` (compiled after `CanvasState.fs`, before `CanvasUpdate.fs`/`App.fs`). This is a pure type/value relocation that creates a compile-order seam: the canvas update arms are extracted into `CanvasUpdate.fs` (compiled between `AppTypes.fs` and `App.fs`) without a cyclic reference, while `update` remains a single function in `App.fs` (no sub-`Msg`/`Cmd.map` split). Consumers that previously reached these via `open App` (three test files) add `open AppTypes`; nothing references them by `App.`-qualified name except `App.computeActivityLevel`, which stays in `App.fs`.
- **Canvas `update` arms extracted into `CanvasUpdate.fs`** — the canvas `update`-arm bodies (`ToggleCanvasPane`, `SetCanvasPosition`, `SelectCanvasDoc`, `OpenCanvasDoc`, `ArchiveCanvasDoc`, `ArchiveCanvasDocResult`, `NavigateCanvasDoc`, `CanvasMessageReceived`, `CanvasSendResult`, `DismissCanvasMessageError`, `LaunchCanvasSession`, `MorphActiveDoc`, `MorphComplete`), the shared canvas helpers (`activeVisibleDoc`, `isKnownCanvasDoc`, `markVisibleDocCmd`), and the `messageListener` subscription glue move to `src/Client/CanvasUpdate.fs` (compiled after `AppTypes.fs`, before `App.fs`). Each canvas arm in `App.fs` is now a one-line delegation (`| ToggleCanvasPane -> CanvasUpdate.toggleCanvasPane model`). This is **body extraction**, not a `Cmd.map` sub-component split: `update` stays a single function over the flat `Msg`, and each helper takes the whole `Model` and returns `Model * Cmd<Msg>` (data-last `model` parameter). `FocusOverviewCard` stays inline in `App.fs` — it is an overview-card focus arm, not a doc/morph/archive arm, and is outside the moved set. The `isKnownCanvasDoc` consumer in the tests adds `open CanvasUpdate`. Realized line counts: `App.fs` 2015 → 1861 (canvas update logic, ~150 lines, removed); it does **not** reach `main` size (1635) because the canvas **view** code (`canvasEventEntry`, `canvasEventLog`, `focusedWorktreeCanvasDoc`, and the pane-view dispatch wiring) and the canvas params threaded through `worktreeCard`/`renderCard`/`repoSection` remain — a separate view extraction, since completed. The stale "~430 lines / main size" estimate in the original task conflated this deferred view extraction with the update-arm extraction; only the update arms are in scope here. The structural gate (each canvas arm is a one-line delegation; bodies live in `CanvasUpdate.fs`) is what proves the extraction.
- **Canvas model slice as a nested record** — the canvas Model-field group is extracted as a nested record `Canvas: CanvasState.CanvasState` on `App.Model` (mirroring the existing `CreateModal`/`ConfirmModal` nesting precedent). The four pure helpers (`touchVisitedDoc`, `canvasDocKind`, `activeVisibleDoc`, `markVisibleDocCmd`) plus the `MaxLiveIframes` literal live in `src/Client/CanvasState.fs` (compiled before `App.fs`); they take pure slices (`repos`/`focused`/`activeCanvasDoc`) rather than the whole `Model`, and `markVisibleDocCmd` is parameterized over the message constructor so the module needs no concrete `Msg` type. Thin `App.fs` wrappers keep `update` call sites unchanged. This is field-path nesting only — **not** the larger `Cmd.map` sub-component split (no sub-`Msg`/sub-`update`; `update` stays one function), which is out of scope.
- **Cross-platform canvas doc path** — `CanvasPrompt.continueWorking` (`src/Shared/Types.fs`) builds the canvas-session launch path with forward slashes (`{worktree}/.agents/canvas/{filename}`), which resolve correctly on Windows, Linux, and macOS. `System.IO.Path.Combine` is deliberately not used because `src/Shared` is Fable-compiled to JavaScript and cannot reference `System.IO`.

## Related Specs

- `docs/spec/worktree-monitor.md` — parent dashboard architecture spec
- `docs/spec/beadspace-canvas.md` — beads dashboard integration in the canvas pane
- `docs/spec/future/canvas-roadmap.md` — remaining canvas work (authoring DX, templates)
