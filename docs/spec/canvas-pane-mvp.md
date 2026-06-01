# Canvas Pane — Phase 1 (MVP)

Phase 1 of the canvas pane feature (`docs/spec/canvas-pane.md`). Validates the core loop end-to-end with minimal scope: agent writes HTML → Treemon detects + serves it → user sees it in an iframe → user interacts → message reaches agent.

## Goals

- Prove the architecture works end-to-end before adding complexity
- Agent writes a single HTML file to `.agents/canvas/` in a worktree → it appears in Treemon
- User can interact with the doc → actions reach the agent's live session
- Docs survive Treemon restarts (served from disk)
- Doc is served from a **separate origin** (`:5002`) for security isolation

## Phase 1 Simplifications

These are **intentional cuts** vs the full spec — each has a planned later phase:

| Full spec | Phase 1 | Later phase |
|-----------|---------|-------------|
| Multiple docs per worktree + tabs | One doc per worktree (first `.html` found) | Phase 2 |
| `registry.json` with structured metadata | Direct filesystem scan (find first `.html`) | Phase 2 |
| "New doc" badges on worktree cards | No badges | Phase 2 |
| DOM-morph (idiomorph) on content change | Iframe reload on hash change | Phase 2 |
| Per-doc session liveness (🟢/⚪ dot) | No liveness tracking — assume session is alive | Phase 3 |
| Resume-on-interact + message queue | No queue — interaction fails silently if no bridge | Phase 3 |
| Multi-session ownership | One session per worktree assumed | Phase 3 |
| `worktreeId` = `base64url(sha256(path))` | URL-encoded path | Phase 2 |
| Bundled reference templates | No templates | Phase 4 |

## Expected Behavior

### Canvas Pane (Client)

- A collapsible side pane in the dashboard, toggled via a button in the header or keyboard shortcut
- Shows the canvas doc for the **currently focused worktree** (by `FocusedElement`)
- When the focused worktree has no `.agents/canvas/*.html` file, the pane shows an empty state ("No canvas doc")
- The doc renders in an `<iframe>` pointing to the separate-origin canvas server (`:5002`)
- When the doc's `contentHash` changes (detected via polling), the iframe reloads
- Changing focused worktree switches the iframe to that worktree's doc

### Doc Serving (Separate Origin)

- A Kestrel listener on **port 5002** (same process as the main server) serves canvas docs
- URL pattern: `GET /{url-encoded-worktree-path}/{filename}`
- Only serves `.html` files from `.agents/canvas/` within known worktrees
- Path traversal validation: resolved path must stay within `.agents/canvas/`
- Sets `X-Frame-Options: SAMEORIGIN` scoped to the app origin (or omits it — the separate origin is the isolation)
- CORS: no `Access-Control-Allow-Origin` needed (iframe doesn't need CORS; postMessage crosses origins natively)

### Canvas Doc Detection (Server)

- During worktree refresh, the scheduler checks if `.agents/canvas/` exists
- If it does, finds the first `.html` file (alphabetical), computes `SHA256` hash of file bytes
- Populates `WorktreeStatus.CanvasDoc: CanvasDoc option` (path, filename, contentHash)
- A `FileSystemWatcher` per worktree's `.agents/canvas/` directory triggers re-scan on change → instant state update via `StateMsg`
- Watchers created/disposed as worktrees appear/disappear

### Interaction (postMessage → Extension Bridge)

- The agent-authored HTML calls `window.parent.postMessage({ action, ... }, "http://127.0.0.1:5000")`
- The Elmish client has a `window` `message` subscription:
  - Validates `event.origin === "http://127.0.0.1:5002"` (canvas origin)
  - Dispatches `CanvasMessageReceived` Msg
- `update` handler calls `sendCanvasMessage` via Fable.Remoting
- Server looks up the registered bridge for that worktree and POSTs to its `/inject` endpoint
- Extension calls `session.send("[canvas] {json}")` to deliver the message as an agent turn

### Bridge Registration (Server)

- Extension POSTs to `POST /api/canvas/register` with `{ worktreePath, injectUrl }`
- Server stores `Map<WorktreePath, InjectUrl>` in memory (not persisted — ephemeral by design)
- No heartbeat in Phase 1 — registration is one-shot, assumed alive
- On `sendCanvasMessage`: look up bridge → POST to injectUrl → return result

### Extension (Minimal Bridge)

- A Copilot CLI extension (Node.js, `@github/copilot-sdk/extension`) bundled in this repo
- On session start: registers with Treemon at `POST http://127.0.0.1:5000/api/canvas/register`
- Exposes `/inject` endpoint: receives JSON payload, calls `session.send()` to deliver as agent turn
- Provides no agent-facing tools in Phase 1 — agents write files with their built-in file tools

## Technical Approach

### Shared Types (`src/Shared/Types.fs`)

```fsharp
type CanvasDoc =
    { Filename: string
      ContentHash: string }
```

Add to `WorktreeStatus`: `CanvasDoc: CanvasDoc option`

Add to `IWorktreeApi`: `sendCanvasMessage: CanvasMessageRequest -> Async<Result<unit, string>>`

Where `CanvasMessageRequest = { WorktreePath: WorktreePath; Payload: string }`

### Server: Doc Scanning (`RefreshScheduler.fs`)

New `StateMsg` case: `UpdateCanvasDoc of RepoId * path: string * CanvasDoc option`

During worktree refresh (alongside git/beads/coding-tool), scan `.agents/canvas/` for the first `.html` file, hash it. Post `UpdateCanvasDoc` to fold into `PerRepoState.CanvasData: Map<string, CanvasDoc option>`.

### Server: FileSystemWatcher

One watcher per known worktree targeting `.agents/canvas/`. On `Changed`/`Created`/`Deleted`/`Renamed` → re-scan and post `UpdateCanvasDoc`. Dispose when worktree is removed.

Watchers managed alongside the scheduler — created in the worktree-list refresh handler when new paths appear, disposed on removal.

### Server: Canvas Doc Server (`:5002`)

Add a second `IWebHost` or `WebApplication` in `Program.fs` bound to `:5002`. Minimal pipeline:
- Single route: `GET /{worktreePath}/{filename}` — URL-decode worktree path, validate against known worktrees, resolve to `.agents/canvas/{filename}`, validate path stays within that dir, serve with `text/html` content type.

### Server: Bridge Registration + Message Forwarding

Non-Remoting HTTP routes on the main server (`:5000` / `:5001`):
- `POST /api/canvas/register` — accepts `{ worktreePath, injectUrl }`, stores in an in-memory `ConcurrentDictionary` (or a `MailboxProcessor` message)
- The `sendCanvasMessage` Remoting method: looks up bridge URL, POSTs the payload, returns `Ok ()` or `Error "no bridge registered"`

### Client: Canvas Pane (`App.fs`)

Model additions:
```
CanvasPaneOpen: bool
```

New Msg variants:
```
ToggleCanvasPane
CanvasMessageReceived of payload: string
CanvasMessageResult of Result<unit, string>
```

View: render a side pane (or bottom pane) with the iframe. The iframe `src` is derived from the focused worktree's `CanvasDoc`: `http://127.0.0.1:5002/{urlEncode wt.Path}/{doc.Filename}`.

Subscription: `window` `message` event listener that validates origin and dispatches `CanvasMessageReceived`.

### Extension (`src/Extension/`)

Minimal `extension.mjs`:
- `joinSession({ tools: [], hooks: { onSessionStart } })`
- `onSessionStart`: POST to `http://127.0.0.1:5000/api/canvas/register` with `{ worktreePath: cwd, injectUrl }`
- HTTP server on ephemeral port exposing `POST /inject` → `session.send()`

## Decisions

- **One doc per worktree for MVP**: avoids tabs, badges, registry complexity; validates the core loop
- **Iframe reload over DOM-morph**: simplest correct behavior; morph is a Phase 2 polish
- **No heartbeat**: registration is one-shot; if the extension dies, `sendCanvasMessage` will fail and return an error — acceptable for MVP
- **`ConcurrentDictionary` for bridge registry**: simple, no persistence needed (ephemeral by design), accessed from Remoting handler threads
- **Extension provides no tools**: agents already have file-writing tools; the extension's only job is the bridge
- **Extension SDK mismatch**: Spec mentions `@github/copilot-sdk/extension` with `joinSession()` but implementation uses a simpler `export default function activate(session)` pattern and depends on unused `@anthropic-ai/claude-code`. The Copilot CLI extensions dir (`~/.copilot/extensions/`) uses the `@github/copilot-sdk/extension` SDK (see `html-interact` extension as reference). The extension needs to be updated to use `joinSession` from the SDK, or the `activate(session)` pattern needs validation against the CLI's actual extension loading mechanism. The `@anthropic-ai/claude-code` dependency should be removed.

## Key Files

| File | Changes |
|------|---------|
| `src/Shared/Types.fs` | `CanvasDoc`, `CanvasMessageRequest`, add to `WorktreeStatus` + `IWorktreeApi` |
| `src/Server/RefreshScheduler.fs` | `UpdateCanvasDoc` StateMsg, canvas scanning, `FileSystemWatcher` management |
| `src/Server/Program.fs` | Second Kestrel on `:5002`, bridge registration route, canvas serving route |
| `src/Server/WorktreeApi.fs` | `sendCanvasMessage` implementation |
| `src/Client/App.fs` | `CanvasPaneOpen`, `ToggleCanvasPane`, `CanvasMessageReceived`, pane view, postMessage subscription |
| `src/Client/styles.css` | Canvas pane styles |
| `src/Extension/extension.mjs` | Minimal bridge extension (new file) |

## Related Specs

- `docs/spec/canvas-pane.md` — Full feature spec (all phases)
- `docs/spec/native-session-management.md` — Session spawning/tracking
- `docs/spec/resume-last-session.md` — Resume patterns (Phase 3)
