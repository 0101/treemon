# Canvas Roadmap (Future Work)

Status: **Future / Deferred** — captured from the now-stale `.agents/canvas/roadmap.html`
after the canvas pane feature shipped. This lists canvas work **not yet implemented**.

Full feature spec: `docs/spec/canvas-pane.md`. Beads dashboard: `docs/spec/beadspace-canvas.md`.

## Baseline — what already ships

For context, the canvas pane already delivers: multi-doc pane with tab bar / dock positions,
per-doc ownership + owner-aware routing, liveness + Start-session, archive, the Beadspace
dashboard, in-place idiomorph morph on content change, and keep-iframes-alive across tab switches.

Every served canvas doc is rewritten at the `</head>` injection point in
`CanvasDocServer.buildInjection`. Today that injection is:

| Doc kind | Injected today |
|---|---|
| `SystemView` (e.g. beads) | `baseStyle` (scrollbar CSS only) + link interceptor |
| `AgentDoc` | the above + bridge heartbeat + idiomorph runtime + morph controller |

The items below extend that injection (Phase 6) and the authoring ecosystem (Phase 7).

## Phase 6 — Canvas Doc Authoring DX

Goal: lower the bar for agents and users to create effective canvas docs. Today every doc needs
~30 lines of CSS boilerplate to match the dark theme and hand-rolled `window.parent.postMessage`
calls to talk back to the pane.

### Base dark-theme CSS reset injection — effort S

Extend `baseStyle` in `CanvasDocServer.fs` from scrollbar-only CSS to a small base reset:
dark background/foreground, system font stack, sensible defaults for `body`, headings, code,
tables, and links — so a doc renders on-theme with zero boilerplate.

- Inject for both doc kinds (same slot as today's `baseStyle`).
- Must be overridable: a doc that sets its own styles wins (inject low-specificity defaults, no `!important`).
- Acceptance: a canvas doc with `<body>plain text</body>` renders dark-themed and readable.

### Injected `window.canvasSend(action, payload)` helper — effort S

Inject a tiny helper (alongside `bridgeScript`, AgentDoc only) exposing
`window.canvasSend(action, payload)` that wraps the `window.parent.postMessage({ action, ... }, '*')`
contract, with payload-size validation matching the client cap (`MaxPayloadBytes = 64_000` in `CanvasPane.fs`).

- Replaces the manual postMessage pattern currently documented in `src/Extension/skill/SKILL.md`.
- Update `SKILL.md` to teach `canvasSend` as the primary API (keep raw postMessage as the fallback contract).
- Acceptance: a doc calls `canvasSend('navigate-canvas-doc', { filename })` and the pane reacts identically to the raw message.

### JS error overlay — effort M

Inject an `window.onerror` / `unhandledrejection` handler (AgentDoc) that forwards doc-side JS
errors to the parent as a `canvas-doc-error` message; the client surfaces them as a **non-blocking
banner** in the pane, reusing the persistent canvas-error-banner pattern already in `CanvasPane.fs`
(distinct source: doc JS errors vs. message-delivery errors).

- Banner is dismissible and must never cover the doc content.
- Acceptance: a doc that throws on load shows the error text in a banner without breaking the pane.

## Phase 7 — Templates & Ecosystem

Goal: standardize the canvas-doc look and make scaffolding trivial. Agents already produce decent
HTML; templates raise the floor and the consistency. Lower priority than Phase 6.

### Bundled reference templates — effort M

Ship a small set of theme-matched templates (e.g. planning, review, design, decision-matrix),
modeled on the existing `src/Server/BeadspaceTemplate.html` precedent. Each demonstrates the
injected base theme and `canvasSend` (Phase 6) so it works with no extra boilerplate.

### Template picker / scaffold command — effort S

A way for an agent (or user) to scaffold a doc from a template into `.agents/canvas/`. Candidate
homes: a `treemon.ps1` subcommand, or guidance + a copy step in the canvas authoring `SKILL.md`.

### Template rendering E2E tests — effort M

Playwright coverage that each bundled template renders and its interactions work. Model on
`src/Tests/BeadspaceCanvasTests.fs`, which uses Playwright **route interception** to serve a
template + mock data from disk (self-contained, no live server — runs in CI under `Category=E2E`).

## Phase 8 — Pane UX

Goal: polish the canvas pane chrome itself (independent of doc authoring/templates).

### Always-visible doc tab with last-modified age — effort S

Two tightly-coupled tab-bar tweaks that ship together:

1. **Always render the doc tab — even for a single doc.** Today `CanvasPane.fs` (the `tabs`
   binding, ~line 269) only renders tabs when `wt.CanvasDocs.Length > 1 || hasSystemView`,
   so a lone **AgentDoc** shows *no* tab and its iframe fills the pane (a lone SystemView
   already renders, to keep its beads badge). Change the condition so the active doc's tab
   always renders.

2. **Show the doc's on-disk freshness inside the tab.** Render a compact relative age next to
   the tab label from `doc.LastModified` (already on `CanvasDoc`, `Types.fs:117`) — e.g.
   `3m`, `2d`. A `relativeTime` formatter already exists (`Components.fs:8`) but emits the
   `"3m ago"` long form; add/adapt a **compact** variant (`3m`, `2h`, `2d`, no `" ago"`) for
   the tab. The age recomputes on the pane's existing render cadence (the dashboard already
   calls `relativeTime` with `System.DateTimeOffset.Now` per render).

- Scope the age to **AgentDoc** tabs (consistent with the AgentDoc-only liveness dot); the
  SystemView "BD" badge is data-driven and carries no authored-file age.
- Acceptance: a worktree with a single canvas doc shows its tab button (not a bare iframe),
  and each AgentDoc tab shows a compact age label reflecting the file's `LastModified`
  (e.g. `3m`, `2d`) that updates as the pane re-renders.

## Considered but not carried forward

Recorded so future readers know these were evaluated, not missed.

- **Phase 5 — DOM morph, keep-iframes-alive, dev hot-reload — SHIPPED.** Idiomorph injection +
  in-place morph on content change and cross-tab iframe persistence landed with the feature. "Dev
  hot reload" is subsumed: a content change bumps the content hash, which already drives a morph.
- **Morph state-preservation polish — not needed.** idiomorph already preserves matched nodes
  including `<input>`/`<textarea>` values and focus during a morph. Revisit only if input loss is
  actually observed in practice.
- **`worktreeId` stable-identity hashing (`base64url(sha256(normalizedPath))`) — superseded.**
  The idea was to replace the raw worktree path used as canvas identity. Today `iframeSrc`
  (`CanvasPane.fs`) builds `http://127.0.0.1:5002/{encodeURIComponent(worktreePath)}/{filename}`
  and the React iframe `prop.key` is the raw `path + "/" + filename`. The original purpose —
  *stable identity for routing and morph* — is already met: paths are normalized once
  (`PathUtils.normalizePath`), the full path is a stable React key (morph/keep-alive tests pass),
  and this feature re-keyed message routing by `sessionId`. The only residual benefit would be
  *cosmetic*: opaque canvas URLs that don't expose filesystem paths in the iframe `src` — at the
  cost of a server-side hash↔path lookup in the doc server and bridge. Deferred unless opaque
  canvas URLs become a desired goal in their own right.

## Open questions

From the roadmap's feedback prompts — worth answering before picking up this work:

- Phase 6: which authoring pain point hurts most (CSS boilerplate, postMessage ergonomics, or debugging)?
- Phase 7: which template types would actually get used?
