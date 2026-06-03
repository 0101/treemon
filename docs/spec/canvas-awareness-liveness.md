# Canvas Awareness & Liveness (Phase 4)

## Goals

1. Users **notice new/updated canvas docs** without actively checking — badge, tab dimming, card notifications, idle auto-display
2. Users can **see which docs have live sessions** — liveness indicator per worktree
3. Messages to dead sessions are **queued and delivered** when the bridge reconnects
4. Users can **start a new session** with a canvas doc as context (as a new tab, not replacing existing terminal)

## Expected Behavior

### Unviewed Doc Tracking
- Server persists `LastViewedHashes: Map<worktreePath, Map<filename, contentHash>>` in global config
- When a poll returns a doc whose contentHash differs from the last-viewed hash (or has no entry), the doc is "unviewed"
- `MarkDocViewed` fires when the user views a doc:
  - Selects it in the tab bar (`SelectCanvasDoc`)
  - It's the active/visible doc when the canvas pane opens (`ToggleCanvasPane`)
  - It's the active/visible doc when poll data arrives and pane is open (`DataLoaded`)
- On first load (`LoadLastViewedHashes`), seed any existing doc that has no entry with its current contentHash — pre-existing docs should not appear as "unviewed"

### Canvas Header Badge
- The Canvas toggle button in the top bar shows a badge count of total unviewed docs across all worktrees
- Badge disappears when count is 0
- Styled like existing PR/build badges — small circle with number

### Canvas Tab Bar: Viewed Doc Dimming
- In the tab bar, doc buttons that have been viewed (contentHash matches `LastViewedHashes`) render at **opacity 0.5**
- The currently selected tab always renders at full opacity regardless of viewed state
- Unviewed docs stay at full opacity — they stand out naturally against the dimmed viewed tabs

### Auto-Display When Idle
- When a poll detects **any contentHash change** (new doc or updated existing doc) AND the user is idle (no mouse/keypress/click/scroll in past 60 seconds):
  - Open the canvas pane if not already open
  - Focus the worktree that has the changed doc
  - Select the changed doc
- "Idle" uses the existing `LastActivityTime` tracking — compare against `autoDisplayIdleMs` (60s), separate from the existing 180s `idleThresholdMs` used for polling
- The idle check is sufficient spam protection — any user activity resets `LastActivityTime` and stops auto-display. No per-doc suppression needed.
- If multiple docs change simultaneously, display the most recently modified one

### Card Console Notification
- When a worktree's canvas doc list changes (new doc or updated contentHash), show a notification in the card's event area
- Format: "Xm ago published *docname*" (new) or "Xm ago updated *docname*" (changed)
- Clicking the notification opens the canvas pane focused on that doc
- **Max 1 row per document** — deduplicate by filename, keep only the most recent event
- When canvas events are present, they **replace the agent's last message** (`LastUserMessage`) in the card footer — the agent message reappears when events expire
- Events expire after **5 minutes**
- **Yellow color** (`#f9e2af`) for canvas event text to stand out from other events
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
- Clicking **opens a new tab** in the worktree's existing terminal window with the current doc content as context — same pattern as Open PR / Fix Build actions
- Button is hidden when a live bridge exists (session already running)

## Technical Approach

### LastViewedHashes Persistence (Server)
- **WorktreeApi.fs**: `saveLastViewedHashes` / `loadLastViewedHashes` on `IWorktreeApi`. Stored as `lastViewedHashes` key in global config JSON — value is `Map<string, Map<string, string>>` (worktreePath → filename → contentHash).
- **Types.fs**: API method signatures.

### Unviewed Doc Tracking (Client)
- **App.fs Model**: `LastViewedHashes: Map<string, Map<string, string>>`. `MarkDocViewed of scopedKey:string * filename:string` Msg.
- **init**: Load `LastViewedHashes` from server on startup. On `LoadLastViewedHashes`, seed existing docs that have no entry.
- **update**: On `MarkDocViewed`, update local map + persist to server. On `SelectCanvasDoc`, fire `MarkDocViewed`. On `ToggleCanvasPane` (open), fire `MarkDocViewed` for active visible doc. On `DataLoaded` when pane is open, fire `MarkDocViewed` for active visible doc.
- Helper: `unviewedDocsByScopedKey: Model -> Map<string, string list>` returns worktree scopedKey → list of unviewed filenames.

### Header Badge (Client)
- **App.fs view**: Compute unviewed count, pass to canvas toggle button. Add badge span.
- **index.html**: `.canvas-badge` CSS.

### Tab Bar Viewed Dimming (Client)
- **CanvasPane.fs**: Apply `.canvas-tab-viewed` class to tab buttons where contentHash matches `LastViewedHashes`, except the active tab.
- **index.html**: `.canvas-tab-viewed { opacity: 0.5; }`.

### Auto-Display When Idle
- **App.fs**: In `DataLoaded` handler, compare `PreviousCanvasHashes` against current hashes. If any hash differs AND `now - LastActivityTime > autoDisplayIdleMs`: dispatch `SetFocus`, `SelectCanvasDoc`, open pane for the most recently modified changed doc.
- No `AutoDisplayedDocs` tracking needed — idle check is sufficient.

### Card Console Notification
- **App.fs**: Track `CanvasEvents: Map<string, CanvasEvent list>` where `CanvasEvent = { Filename; Timestamp; IsNew }`. Populate on poll diff. Deduplicate by filename (keep most recent). Expire after 5 minutes.
- **Card rendering**: When canvas events exist for a worktree, hide `LastUserMessage`. Render canvas events in card event area with click handler.
- **index.html**: `.canvas-event` with yellow color (`#f9e2af`).

### Liveness Indicator
- **extension.mjs**: Include `sessionId` in registration payload (from `joinSession` result).
- **CanvasBridge.fs**: `SessionId: string option` on `BridgeEntry`. `isAlive` helper (heartbeat age < 60s).
- **Types.fs**: `BridgeLiveness` type.
- **CanvasPane.fs**: 🟢/⚪ dot in tab bar and overview.

### Message Queue (Server)
- **CanvasBridge.fs**: `messageQueue: ConcurrentDictionary<string, Queue>` with max 10 entries, 5min TTL. Drain on register.
- **Types.fs**: `CanvasMessageResult` DU with Ok/Error/Queued.
- **App.fs**: Handle Queued result — "waiting for session…" banner. Client-side 5min expiry.

### Start New Session Button
- **CanvasPane.fs**: "▶ Start session" button in header when liveness is dead/unknown.
- **App.fs**: `LaunchCanvasSession of scopedKey:string` Msg. Opens new tab in existing terminal (same as Open PR / Fix Build pattern).

## Key Files

| File | Changes |
|---|---|
| `src/Shared/Types.fs` | API methods, BridgeLiveness type, CanvasMessageResult |
| `src/Server/WorktreeApi.fs` | LastViewedHashes persistence |
| `src/Server/CanvasBridge.fs` | SessionId in BridgeEntry, message queue, isAlive, drain on register |
| `src/Server/PathUtils.fs` | Shared canvas path validation |
| `src/Client/App.fs` | Model fields, unviewed tracking, auto-display, MarkDocViewed, card notifications, launch session |
| `src/Client/CanvasPane.fs` | Liveness dot, start session button, waiting banner, tab dimming |
| `src/Client/index.html` | CSS for badge, liveness, waiting state, canvas events, tab dimming |
| `src/Extension/extension.mjs` | Include sessionId in registration |

## Related Specs

- `docs/spec/canvas-pane.md` — full architecture, phase tracking
- `docs/spec/canvas-bridge-resilience.md` — heartbeat/reconnect (already shipped)
