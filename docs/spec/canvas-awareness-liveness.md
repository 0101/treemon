# Canvas Awareness & Liveness (Phase 4)

## Goals

1. Users **notice new canvas docs** without actively checking — badges, dots, idle auto-display
2. Users can **see which docs have live sessions** — liveness indicator per worktree
3. Messages to dead sessions are **queued and delivered** when the bridge reconnects
4. Users can **start a new session** with a canvas doc as context

## Expected Behavior

### Unviewed Doc Tracking
- Server persists `LastViewedHashes: Map<worktreePath, Map<filename, contentHash>>` in global config
- When a poll returns a doc whose contentHash differs from the last-viewed hash (or has no entry), the doc is "unviewed"
- `MarkDocViewed` fires when the user views a doc (selects it in the tab bar or it's the active doc when canvas opens). Updates the hash in local state and persists to server.

### Canvas Header Badge
- The Canvas toggle button in the top bar shows a badge count of total unviewed docs across all worktrees
- Badge disappears when count is 0
- Styled like existing PR/build badges — small circle with number

### Worktree Card Dots
- Each worktree card that has unviewed canvas docs shows a dot indicator (reuse `ct-dot` pattern)
- Dot disappears when all docs for that worktree are viewed

### Auto-Display New Docs When Idle
- When a poll detects a new canvas doc (filename not seen before in that worktree) AND the user is idle (no mouse/keypress/click/scroll in past 60 seconds):
  - Open the canvas pane if not already open
  - Focus the worktree that has the new doc
  - Select the new doc
- "Idle" uses the existing `LastActivityTime` tracking — just compare against a 60s threshold (separate from the existing 180s `idleThresholdMs` used for polling)
- Only auto-display once per doc (track which docs have been auto-displayed to avoid re-triggering)
- If multiple new docs arrive simultaneously, display the most recently modified one

### Card Console Notification
- When a worktree's canvas doc list changes (new doc or updated contentHash), show a notification in the card's event area
- Format: "Xm ago published *docname*" (or "Xm ago updated *docname*")
- Clicking the notification opens the canvas pane focused on that doc
- Notifications follow the same rendering pattern as other card events (git commits, PR updates)

### Per-Doc Liveness Indicator
- Extension bridge registration includes `sessionId` so the server can track which session owns the bridge
- Server exposes liveness per worktree: registered + heartbeat age < 60s = alive
- Canvas tab bar shows a subtle 🟢/⚪ dot next to doc tabs indicating whether the owning session is alive
- Overview shows liveness per worktree entry

### Message Queue
- When `sendCanvasMessage` fails with "no bridge registered", the message is queued server-side per worktree
- Queue has max size (10 messages) and expiry (5 minutes)
- When a bridge registers/re-registers, queued messages are drained and forwarded
- Client shows "waiting for session…" indicator when a message is queued (instead of immediate error)
- If queue expires, show error "Message expired — no session responded"

### Start New Session Button
- Canvas pane header shows a "▶ Start session" button when no live bridge exists for the focused worktree
- Clicking launches a new Copilot session in that worktree's terminal with the current doc content as context
- Reuses existing `launchSession` infrastructure

## Technical Approach

### LastViewedHashes Persistence (Server)
- **WorktreeApi.fs**: Add `saveLastViewedHashes` / `loadLastViewedHashes` to `IWorktreeApi`. Store as `lastViewedHashes` key in global config JSON — value is `Map<string, Map<string, string>>` (worktreePath → filename → contentHash).
- **Types.fs**: Add API method signatures.

### Unviewed Doc Tracking (Client)
- **App.fs Model**: Add `LastViewedHashes: Map<string, Map<string, string>>` and `AutoDisplayedDocs: Set<string * string>` (worktree × filename). Add `MarkDocViewed of scopedKey:string * filename:string` Msg.
- **init**: Load `LastViewedHashes` from server on startup.
- **update**: On `MarkDocViewed`, update local map + persist to server. On `SelectCanvasDoc`, also fire `MarkDocViewed`.
- Helper: `unviewedDocs: Model -> Map<string, string list>` returns worktree scopedKey → list of unviewed filenames by comparing current CanvasDocs contentHashes against LastViewedHashes.

### Header Badge + Card Dots (Client)
- **App.fs view**: Compute unviewed count, pass to canvas toggle button. Add badge span.
- **Card rendering**: Pass per-worktree unviewed status, render dot (reuse `ct-dot` CSS class).
- **index.html**: Add `.canvas-badge` CSS, reuse `.ct-dot` pattern for card dots.

### Auto-Display When Idle
- **App.fs**: In `Tick` handler (poll response), compare new `CanvasDocs` against previous state. If new doc detected AND `DateTime.Now - LastActivityTime > 60s` AND doc not in `AutoDisplayedDocs`: dispatch `SetFocus`, `SelectCanvasDoc`, open pane. Add doc to `AutoDisplayedDocs`.
- Use 60s threshold constant (`autoDisplayIdleMs`), separate from existing 180s idle threshold.

### Card Console Notification
- **App.fs**: Track `CanvasEvents: Map<string, CanvasEvent list>` where `CanvasEvent = { Filename; Timestamp; IsNew }`. Populate on poll diff. Render in card event area with click handler.
- Follows existing card event rendering patterns.

### Liveness Indicator
- **extension.mjs**: Include `sessionId` in registration payload (from `joinSession` result).
- **CanvasBridge.fs**: Add `SessionId: string option` to `BridgeEntry`. Expose `isAlive` (heartbeat age < 60s).
- **Types.fs**: Add `BridgeLiveness` to the worktree API response or as separate endpoint.
- **CanvasPane.fs**: Render 🟢/⚪ dot in tab bar and overview based on liveness.

### Message Queue (Server)
- **CanvasBridge.fs**: Add `messageQueue: ConcurrentDictionary<string, Queue>` with max 10 entries, 5min TTL. On `sendMessage` failure, queue the message. On `register`, drain queued messages for that worktree.
- **Types.fs**: Add `MessageQueued` result variant so client can show "waiting" state.
- **App.fs**: Handle `MessageQueued` result — show "waiting for session…" instead of error.

### Start New Session Button
- **CanvasPane.fs**: Show "▶ Start session" button in header when liveness is dead/unknown.
- **App.fs**: Add `LaunchCanvasSession of scopedKey:string` Msg. Call existing session launch infrastructure.

## Key Files

| File | Changes |
|---|---|
| `src/Shared/Types.fs` | API methods, BridgeLiveness type |
| `src/Server/WorktreeApi.fs` | LastViewedHashes persistence, liveness in API response |
| `src/Server/CanvasBridge.fs` | SessionId in BridgeEntry, message queue, isAlive, drain on register |
| `src/Client/App.fs` | Model fields, unviewed tracking, auto-display, MarkDocViewed, card notifications, launch session |
| `src/Client/CanvasPane.fs` | Badge, dots, liveness dot, start session button, waiting indicator |
| `src/Client/index.html` | CSS for badges, dots, liveness, waiting state |
| `src/Extension/extension.mjs` | Include sessionId in registration |

## Related Specs

- `docs/spec/canvas-pane.md` — full architecture, badge/liveness design decisions
- `docs/spec/canvas-bridge-resilience.md` — heartbeat/reconnect (already shipped)
