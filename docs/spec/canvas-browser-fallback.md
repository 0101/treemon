# Canvas Browser Fallback

## Goals

When the canvas-bridge extension runs in a directory **not monitored by Treemon**, fall back to serving canvas HTML docs over HTTP so they render in a browser with working `postMessage` interactions — without any changes to how agents author canvas docs.

## Expected Behavior

1. **Treemon mode (unchanged)**: Extension registers with Treemon, heartbeats; canvas docs display in the Treemon canvas pane as today.
2. **Browser fallback mode**: When Treemon is unreachable **or** reports that the current directory is not monitored, the extension:
   - Serves contract-valid `.agents/canvas/*.html` files over HTTP with injected transport shim and content-polling reload scripts.
   - Does not post canvas-write notifications to the session, avoiding repeated or competing agent prompts while fallback mode is active.
   - Receives interactions at `POST /_message/:filename` and forwards them via `session.send()`.
     Source coordinates come from the startup worktree and validated endpoint filename, not authored fields.
3. **Same HTML, same API**: `canvasSend` is the primary authoring API and raw
   `window.parent.postMessage(...)` is its transport substrate. In a top-level fallback window, the
   transport shim intercepts self-posted messages and forwards them via HTTP. Zero agent-side
   changes.
4. **Host-aware UX**: Browser fallback remains session-silent whether Treemon is unreachable or the directory is explicitly unmonitored.

## Technical Approach

### Treemon Detection

At startup the extension POSTs `worktreePath`, `injectUrl`, `sessionId`, its parent Copilot PID,
optional inherited terminal ID, and its opaque loopback shutdown endpoint/capability to
`/api/canvas/register`. Treemon requires a validated durable session ID and canonical monitored
worktree. Parent PID/start and terminal origin are optional location hints; unverifiable process
hints do not reject canvas routing. Its response reports whether the worktree is monitored:
`{ registered: bool, monitored: bool }`. An accepted monitored registration returns HTTP 200 with
`{ registered: true, monitored: true }`; an otherwise acceptable request for an unmonitored
worktree returns HTTP 200 with `{ registered: false, monitored: false }`. Malformed requests,
invalid IDs, worktree paths, loopback endpoints, or shutdown capabilities return
HTTP 400. The extension enters **browser fallback mode** when registration is unreachable, returns
any non-2xx response, or reports `monitored === false`. For backward compatibility with older
Treemon servers that return a non-JSON body, a successful (200) response with no `monitored` field
is treated as monitored (Treemon mode). In Treemon mode, behavior is unchanged — the existing
`/inject` endpoint and heartbeat remain active.

`monitored` is computed server-side (`canvasRegisterHandler`) by checking whether the
normalized `worktreePath` matches any worktree the scheduler currently tracks
(`PerRepoState.KnownPaths`). A monitored worktree is registered with the canvas bridge and
returns `{ registered: true, monitored: true }`; an unmonitored worktree is **not** registered
(no bridge session is created) and returns `{ registered: false, monitored: false }`. Either
accepted outcome returns HTTP 200, so HTTP success alone is not sufficient to conclude the canvas
pane will display the docs.

### HTTP Endpoints (browser mode only)

| Endpoint | Purpose |
|---|---|
| `GET /canvas/:filename` | Read `.agents/canvas/<filename>` from disk, inject transport shim + content-poll script before `</head>`, serve as HTML |
| `GET /canvas/:filename/hash` | Return MD5/SHA256 hex of file content (for change detection) |
| `POST /_message/:filename` | Validate the bare filename and payload, derive authoritative source from that filename and startup worktree, and use the same serialized canvas send path as `/inject` |

Both `POST` sinks (`/_message/:filename` and the always-on `/inject`) are hardened against cross-origin browser
abuse: they require `Content-Type: application/json` (so a cross-origin call becomes a preflighted
request the server never answers — the browser blocks it, closing the `text/plain` simple-request
CSRF vector) and reject any request carrying a non-loopback `Origin`. The legitimate callers already
comply — Treemon's server-side POST to `/inject` sends `application/json` and no `Origin`, and the
same-origin transport shim posts the filename-scoped endpoint as `application/json`. Both use the shared capped
request-body reader with a 1 MiB default and reject request-stream errors.

### Injected Scripts

Browser-mode AgentDocs receive four scripts injected before `</head>`:

- **Transport shim** — in a top-level window it forwards self-posted flat messages to the served
  filename's message endpoint. The receiving server supplies source independently of payload;
  authors need no browser-specific code.
- **`canvasSend`** — the same canonical `src/Extension/canvas-send.js` runtime embedded by the
  Treemon server, so authored interactions use one action check, serialization guard, payload
  merge, size cap, and return contract in both hosts.
- **Selected-text contextual actions** — the same canonical runtime the Treemon doc server embeds,
  calling only the shared `canvasSend` helper. SystemView filenames are excluded through the shared
  `src/Extension/canvas-doc-kinds.json` configuration rather than a fallback-only filename check.
  Fallback HTML remains unframeable (`frame-ancestors 'none'`).
- **Content-polling reload** — polls `/canvas/:filename/hash` every 3s and reloads the page when the hash changes.

### Canvas-write detection (session events)

The native runtime no longer supports SDK hook callbacks (`joinSession({ hooks })` fails the
internal `session.resume`), so Canvas writes are observed via **session events**. The extension calls `joinSession()` and subscribes with
`session.on("tool.execution_start", …)` / `session.on("tool.execution_complete", …)`. The completion
event carries neither the tool name nor its arguments, so supported canvas targets are captured from
the **start** event (keyed by `toolCallId`) and acted on once the matching completion reports success.
Create/edit arguments contribute one destination; `apply_patch` contributes canvas HTML destinations
from Add/Update/Move headers. In browser mode the extension serves written docs without injecting a
session notification. In Treemon mode it declares ownership instead.

Observation and bare-filename claims are confined to the startup worktree's `.agents/canvas`
folder. Successful durable declarations wake queues; save failure reports non-success and retains
the previous owner. No cross-worktree attribution or owner-schema migration occurs.

`/inject` receives `{kind:"canvas",prompt,source:{worktreePath,filename}}`; browser fallback derives
that same identity locally. Both yield `[canvas] {source,payload,authoringReminder?}` as escaped
JSON data. Reminder classification uses the authoritative filename, not authored `doc` or `source`.

### Path Security

`GET /canvas/:filename` accepts only a bare name matching the shared
`src/Extension/canvas-filename-contract.json` pattern. Spaces, quotes, control characters,
directory separators, and traversal paths are rejected before file resolution; the resolved-path
containment check remains defense in depth. Canvas-write detection applies the same validator after
confirming the write target is directly inside `.agents/canvas/`, and `canvas_take_ownership`
accepts only the bare filename rather than stripping a path down to its final segment.

## Decisions

- **Event-driven, not file-watcher**: `session.on("tool.execution_*")` detects writes instead of
  `fs.watch`, avoiding OS-specific watcher behavior.
- **No write notification**: Browser fallback serves canvas docs without injecting prompts into the session.
- **Content polling over SSE**: 3s polling is simpler than SSE and adequate for agent file writes.
- **Detect once at startup**: v1 does not switch modes mid-session. If Treemon starts later, it won't be detected until the extension restarts.

## Key Files

- `src/Extension/extension.mjs` — mode detection, session registration, HTTP serving, local ownership, runtime injection, and filename-scoped messages
- `src/Extension/request-body.mjs` — shared capped request-body reader for injection, message, and shutdown endpoints
- `src/Extension/shutdown-endpoint.mjs` — capability-guarded loopback routine-shutdown endpoint
- `src/Extension/canvas-send.js` — canonical `window.canvasSend` runtime shared with the server
- `src/Extension/canvas-selection-context.js` — canonical selected-text interaction runtime shared with the server
- `src/Extension/canvas-doc-kinds.json` — canonical SystemView filename list shared with the server
- `src/Extension/canvas-filename-contract.json` — canonical filename pattern shared with the server
- `src/Extension/canvas-filename.mjs` — exact-match filename validation for serving and ownership
- `src/Extension/canvas-ownership.mjs` — extracted session-event canvas-write watcher and apply-patch destination parsing
- `src/Server/CanvasDocServer.fs` — `canvasRegisterHandler` returns `{ registered, monitored }`; `isKnownWorktree` checks the scheduler's `KnownPaths`
- `src/Extension/skill/SKILL.md` — minor update noting browser fallback
