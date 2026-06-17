# Canvas Browser Fallback

## Goals

When the canvas-bridge extension runs in a directory **not monitored by Treemon**, fall back to serving canvas HTML docs over HTTP so they render in a browser with working `postMessage` interactions — without any changes to how agents author canvas docs.

## Expected Behavior

1. **Treemon mode (unchanged)**: Extension registers with Treemon, heartbeats; canvas docs display in the Treemon canvas pane as today.
2. **Browser fallback mode**: When Treemon is unreachable **or** reports that the current directory is not monitored, the extension:
   - Serves `.agents/canvas/*.html` files over HTTP with injected transport shim and content-polling reload scripts.
   - After the agent writes a canvas file, injects `additionalContext` via `onPostToolUse` with the serving URL.
   - Receives `postMessage`-originated interactions at `POST /_message` and forwards them to the agent session via `session.send()`.
3. **Same HTML, same API**: Canvas docs use `window.parent.postMessage(...)` in both modes. The transport shim intercepts self-posted messages in top-level browser windows and forwards via HTTP. Zero agent-side changes.
4. **Agent controls UX**: The agent decides whether to open the browser or output a ctrl+clickable URL.

## Technical Approach

### Treemon Detection

At startup the extension POSTs to `/api/canvas/register`. Treemon's response now reports
whether the worktree is actually monitored: `{ registered: bool, monitored: bool }`, always
with HTTP 200. The extension enters **browser fallback mode** when registration is
unreachable/fails **or** `monitored === false`. For backward compatibility with older Treemon
servers that return a non-JSON body, a successful (200) response with no `monitored` field is
treated as monitored (Treemon mode). In Treemon mode, behavior is unchanged — the existing
`/inject` endpoint and heartbeat remain active.

`monitored` is computed server-side (`canvasRegisterHandler`) by checking whether the
normalized `worktreePath` matches any worktree the scheduler currently tracks
(`PerRepoState.KnownPaths`). A monitored worktree is registered with the canvas bridge and
returns `{ registered: true, monitored: true }`; an unmonitored worktree is **not** registered
(no bridge session is created) and returns `{ registered: false, monitored: false }`. Either
way the call returns 200, so HTTP success alone is not sufficient to conclude the canvas pane
will display the docs.

### HTTP Endpoints (browser mode only)

| Endpoint | Purpose |
|---|---|
| `GET /canvas/:filename` | Read `.agents/canvas/<filename>` from disk, inject transport shim + content-poll script before `</head>`, serve as HTML |
| `GET /canvas/:filename/hash` | Return MD5/SHA256 hex of file content (for change detection) |
| `POST /_message` | Parse JSON body, forward as `session.send({ prompt: "[canvas] " + JSON.stringify(body) })` |

### Injected Scripts

**Transport shim** — intercepts `window.parent.postMessage()` calls (which post to `window` itself when no parent frame) and forwards via `fetch POST` to `/_message`:
```js
if (window.parent === window) {
  window.addEventListener('message', function(e) {
    if (e.source === window && e.data && typeof e.data.action === 'string') {
      fetch('http://127.0.0.1:PORT/_message', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(e.data)
      });
    }
  });
}
```

**Content-polling reload** — polls `/hash` every 3s and reloads on change:
```js
(function() {
  var lastHash = null;
  setInterval(function() {
    fetch(location.href + '/hash').then(r => r.text()).then(function(hash) {
      if (lastHash && hash !== lastHash) location.reload();
      lastHash = hash;
    }).catch(function() {});
  }, 3000);
})();
```

### `onPostToolUse` Hook

Registered via `joinSession({ hooks: { onPostToolUse } })` (hooks must be nested under the `hooks` key per the SDK `SessionHooks` contract). Detects file creates/edits targeting `.agents/canvas/*.html` and returns `additionalContext` with the serving URL + instructions for the agent.

### Path Security

`GET /canvas/:filename` must validate the resolved path stays within `.agents/canvas/` (no `..` traversal). Reject filenames containing path separators or `..`.

## Decisions

- **Hook-driven, not file-watcher**: `onPostToolUse` detects writes instead of `fs.watch`. Simpler, no OS-specific edge cases.
- **No auto-open**: Agent opens browser or outputs URL. Extension stays simple.
- **Content polling over SSE**: 3s polling is simpler than SSE and adequate for agent file writes.
- **Detect once at startup**: v1 does not switch modes mid-session. If Treemon starts later, it won't be detected until the extension restarts.

## Key Files

- `src/Extension/extension.mjs` — mode detection, HTTP serving, hook, message endpoint
- `src/Server/CanvasDocServer.fs` — `canvasRegisterHandler` returns `{ registered, monitored }`; `isKnownWorktree` checks the scheduler's `KnownPaths`
- `src/Extension/skill/SKILL.md` — minor update noting browser fallback
