# Canvas Authoring DX & Pane UX

Implements roadmap **Phase 6** (Canvas Doc Authoring DX) and **Phase 8** (Pane UX) from
`docs/spec/future/canvas-roadmap.md`. Full feature spec for the pane: `docs/spec/canvas-pane.md`.

## Goals

- Lower the bar to author an effective canvas doc: an on-theme doc with zero CSS boilerplate
  and a first-class message helper instead of hand-rolled `postMessage`.
- Make doc-side JS failures visible instead of silently breaking inside the iframe.
- Always surface the active doc as a labeled tab — even when there's only one — and show how
  fresh it is on disk.
- No regressions to existing canvas behavior (ownership routing, morph, liveness, the beads
  SystemView, keep-iframes-alive).

## Expected Behavior

All four items extend existing, well-isolated seams. Today every served doc is rewritten at the
`</head>` injection point in `CanvasDocServer.handleCanvasRequest`, with the per-kind injection
chosen by `buildInjection` (`src/Server/CanvasDocServer.fs`):

| Doc kind | Injected today |
|---|---|
| `SystemView` | `baseStyle` (scrollbar CSS only) + `linkInterceptor` |
| `AgentDoc` | the above + `bridgeScript` + idiomorph runtime + morph controller |

### 1. Base dark-theme CSS reset (Phase 6.1)

`baseStyle` grows from scrollbar-only CSS to a small **zero-specificity** base reset: dark
background/foreground, system font stack, and sensible defaults for `body`, headings, code,
tables, and links — injected for **both** doc kinds (same slot as today).

- Defaults must be overridable: a doc that sets its own styles wins. The reset is injected at the
  `</head>` point (`CanvasDocServer.handleCanvasRequest`) — i.e. **after** any `<head>` styles the
  doc (or the `SystemView` template) already declares — so an equal-specificity element rule in the
  doc would otherwise *lose* the source-order tiebreak to the injected reset. To guarantee the doc
  always wins, wrap every reset selector in **`:where(...)`** so it carries **zero specificity**
  (like the existing `*` scrollbar rule); any real doc rule — even a bare `body { }` element
  selector — then overrides it regardless of source order. Use **no `!important`**.
- This is also what keeps the beads `SystemView` (`BeadspaceTemplate.html`) visually unchanged: its
  own `body { background: var(--bg-deep) }` (element specificity 0,0,1) beats the `:where(body)`
  reset (specificity 0,0,0), so the injected dark default never paints over the dashboard.
- **Acceptance:** (a) a canvas doc whose body is `<body>plain text</body>` renders dark-themed and
  readable with no doc-authored CSS; (b) a doc that declares its own `body { background: … }` in
  `<head>` keeps *its* colour, not the reset's; (c) the beads `SystemView` body background is
  unchanged (`var(--bg-deep)`).

### 2. Injected `window.canvasSend(action, payload)` helper (Phase 6.2)

A tiny script injected **alongside `bridgeScript` (AgentDoc only)** exposes
`window.canvasSend(action, payload)` that wraps the existing flat message contract
`window.parent.postMessage({ action, ...payload }, '*')`.

- The message shape stays flat: `canvasSend('navigate-canvas-doc', { filename })` posts
  `{ action: 'navigate-canvas-doc', filename }` — byte-identical in effect to the raw message the
  pane already handles.
- Validates serialized payload size against the client cap (`MaxPayloadBytes = 64_000`,
  `src/Client/CanvasPane.fs`) using the **same metric the client enforces** —
  `JSON.stringify({ action, ...payload }).length` (UTF-16 code units, the JS `String.length` the
  client checks at the `postMessage DROPPED: payload too large` path) — so the doc-side verdict is
  identical to the client's drop decision. Oversized messages are **not** posted; the helper logs a
  clear console error doc-side so the author gets immediate feedback instead of the silent
  client-side drop. (See open question on whether the cap should become a true UTF-8 byte count.)
- `src/Extension/skill/SKILL.md` is updated to teach `canvasSend` as the **primary** API; the raw
  `window.parent.postMessage` shape stays documented as the underlying contract / fallback.
- **Acceptance:** a doc that calls `canvasSend('navigate-canvas-doc', { filename })` switches tabs
  identically to the current raw-`postMessage` path.

### 3. JS error overlay (Phase 6.3)

An injected handler (**AgentDoc only**, alongside the bridge) installs `window.onerror` and an
`unhandledrejection` listener that forward doc-side JS errors to the parent as a
`{ action: 'canvas-doc-error', message, source, line, col }` message. The pane surfaces them as a
**non-blocking, dismissible banner**, reusing the existing `canvas-error-banner` visual pattern
(`src/Client/CanvasPane.fs`) but driven by a **distinct source** — doc JS errors, separate from
the message-delivery failures already shown via `CanvasSendState.Failed`.

- The banner must never cover doc content and must be dismissible.
- The banner is **doc-scoped**: the stored error is stamped with the doc visible when it arrived,
  and the pane shows the banner **only while that same doc is focused**, so navigating away — by
  tab (`SelectDoc`), card, or keyboard focus — never shows a stale error over a different doc (nor
  over the overview). `SelectDoc` additionally clears it so a tab switch (and switching back) never
  re-shows it. It reappears only if a doc throws again.
- **Acceptance:** a doc that throws on load shows the error text in a banner, and the pane (tabs,
  position controls, other docs) keeps working.

### 4. Always-visible doc tab with last-modified age (Phase 8)

Two coupled tab-bar changes in `src/Client/CanvasPane.fs`:

1. **Always render the active doc's tab — even for a single doc.** Today the `tabs` binding
   renders tabs only when `wt.CanvasDocs.Length > 1 || hasSystemView`, so a lone **AgentDoc**
   shows no tab and its iframe fills the pane. Change the condition so the active doc always has a
   visible, labeled tab. (The header bar itself already always renders; only the tab buttons were
   suppressed.)
2. **Show on-disk freshness inside each AgentDoc tab.** Render a compact relative age next to the
   tab label from `doc.LastModified` (already on `CanvasDoc`, `src/Shared/Types.fs`) — e.g. `3m`,
   `2h`, `2d`. Computed from `System.DateTimeOffset.Now` at render time (same pattern as the
   dashboard's `relativeTime System.DateTimeOffset.Now wt.LastCommitTime`), so it refreshes on the
   pane's existing render cadence.

- The age is scoped to **AgentDoc** tabs, matching the AgentDoc-only liveness dot. The
  `SystemView` "BD" badge is data-driven and carries no authored-file age.
- **Acceptance:** a worktree with a single canvas doc shows its tab button (not a bare iframe), and
  each AgentDoc tab shows a compact age (`3m`, `2d`) reflecting the file's `LastModified`.

## Technical Approach

### Server injection — `src/Server/CanvasDocServer.fs`
- **Base CSS reset:** extend the `baseStyle` string literal. Wrap every reset selector in
  `:where(...)` (zero specificity), no `!important`, so doc rules and the `SystemView` template's
  own element rules always win the cascade despite the reset being injected after them at
  `</head>`. Still injected for both kinds via `buildInjection`.
- **`canvasSend`:** add a new injected script constant (mirroring `bridgeScript`'s IIFE style) and
  append it in the `AgentDoc` arm of `buildInjection` only. Implement the size check with the same
  metric the client uses — `JSON.stringify({ action, ...payload }).length` compared against
  `64_000` — so the helper never blocks a payload the client would accept nor passes one it would
  drop.
- **Error overlay:** add a new injected script constant (AgentDoc arm) installing `window.onerror`
  + `unhandledrejection` → `postMessage({ action: 'canvas-doc-error', ... }, '*')`.
- Keep `MaxPayloadBytes` as the single source of truth for the cap; reference its value in the
  injected helper (literal kept in sync with `CanvasPane.fs`).

### Client — `src/Client/CanvasPane.fs` + `src/Client/Components.fs`
- **Doc-error banner:** add model state for the latest doc error — a record **stamped with the
  doc that was visible when it arrived** (`DocJsError { ScopedKey; Filename; Message }`, not a bare
  `Some message`) — a message handler for `canvas-doc-error`, a dismiss action, and a
  `canvas-doc-error-banner` element next to the existing `errorBanner`. Reuse the banner/dismiss CSS
  classes (add a doc-error class if a distinct accent is wanted). Wire the new callback through
  `CanvasPaneCallbacks`. The banner is rendered **only when the stamp matches the focused doc**, so
  every navigation path (tab/card/keyboard) hides a stale error with no per-reducer clear; `SelectDoc`
  still clears the state outright so a tab switch (and back) never re-shows it. The handler (the
  `canvas-doc-error` arm of `CanvasPane.messageListener`) is pane-internal — it must **not** be
  forwarded to the session like a normal doc payload.
- **Always-visible tab:** change the `tabs` condition so the active doc's tab always renders;
  preserve the existing SystemView-first ordering and the lone-SystemView behavior.
- **Compact age:** add `Components.relativeTimeCompact` (a sibling of `relativeTime`) returning
  `now`/`3m`/`2h`/`2d` (no `" ago"`). Render it inside `agentTab` from
  `System.DateTimeOffset.Now` and `d.LastModified`.

### Docs — `src/Extension/skill/SKILL.md`
- Replace the primary `postMessage` example with `canvasSend`; keep the raw contract documented as
  the underlying mechanism.

## Verification

End-to-end Playwright coverage modeled on `src/Tests/BeadspaceCanvasTests.fs` (route interception,
self-contained, CI `Category=E2E`), plus unit tests for pure logic:
- Unit: `buildInjection` output per kind contains/omits the right injected pieces; `canvasSend`
  size-cap boundary; `relativeTimeCompact` formatting across thresholds.
- E2E: the four acceptance criteria above (themed plain doc, `canvasSend` tab switch, error banner
  on throwing doc, single-doc tab + visible age), plus the two cascade guards for item 1 — a doc
  that sets its own `body` background keeps it, and the beads `SystemView` body background stays
  `var(--bg-deep)`.

## Related Specs
- `docs/spec/canvas-pane.md` — the canvas pane feature this extends.
- `docs/spec/future/canvas-roadmap.md` — source roadmap (Phases 6 & 8).
- `docs/spec/beadspace-canvas.md` — the beads `SystemView` that must stay visually unchanged.
