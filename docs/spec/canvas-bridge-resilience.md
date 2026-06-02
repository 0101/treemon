# Canvas Bridge Resilience & UX Polish

## Goals

1. Canvas message errors are **persistently visible** to the user — not a 2-second flash
2. Extension **auto-reconnects** after server restart so the bridge registry survives deploys
3. Archive button uses the **same icon** as worktree card archive (SVG, not 🗑️ emoji)

## Expected Behavior

### Persistent Error Banner
- When `sendCanvasMessage` returns an error, a banner appears **below the canvas header bar**
- Banner text: the error message (e.g. "no bridge registered for this worktree")
- Banner stays visible until: (a) user dismisses it, (b) a new successful message send clears it, or (c) a new error replaces it
- Styled as a subtle but noticeable strip — dark red-ish background, not intrusive but not ignorable
- Remove the existing 2-second flash animation (`canvas-error-flash`)

### Extension Auto-Reconnect
- Extension sends periodic heartbeat to Treemon server (`POST /api/canvas/heartbeat` or re-register)
- On HTTP failure (server restarted), extension retries with exponential backoff
- On successful re-register after failure, log it so the user can see reconnection happened
- Heartbeat interval: ~30 seconds (configurable constant)
- Server tracks last heartbeat timestamp per bridge entry

### Bridge Health Endpoint
- `GET /api/canvas/bridge-status?worktreePath=...` returns whether a bridge is registered and its last heartbeat age
- Client can use this to show liveness indicator in future phases (not required now, but the endpoint should exist)

### Archive Icon Fix
- Canvas archive button in `CanvasPane.fs` headerBar uses `ArchiveViews.archiveIcon` SVG instead of 🗑️ emoji
- Styled to match the header bar (small, subtle, same color as other controls)

## Technical Approach

### Error Banner (Client)
- **App.fs**: Change `CanvasMessageError: bool` to `CanvasMessageError: string option` in the `Model` record. On `CanvasMessageResult (Error msg)`, set `CanvasMessageError = Some msg`. On successful send, set `None`. Remove `ClearCanvasMessageError` delayed dispatch. Add `DismissCanvasMessageError` Msg for manual dismiss.
- **CanvasPane.fs**: Render error banner div below headerBar when error is `Some`. Include dismiss button (✕).
- **index.html**: Add `.canvas-error-banner` CSS. Remove `canvas-error-flash` animation and `.canvas-msg-error` class usage.

### Extension Reconnect
- **extension.mjs**: After initial registration, start a `setInterval` heartbeat loop. Each tick: POST to `/api/canvas/register` (re-register is idempotent). On failure: log warning, increase interval (exponential backoff, cap at 2 min). On success after failure: log "reconnected", reset interval to 30s.
- **CanvasBridge.fs**: Add `lastHeartbeat` timestamp to registry entries (change from `ConcurrentDictionary<string,string>` to a record with `injectUrl` and `lastHeartbeat`). Update timestamp on each register call.
- **Program.fs**: Add `GET /api/canvas/bridge-status` endpoint that returns `{ registered: bool, lastHeartbeatAge: float option }` for a given worktree path.

### Archive Icon
- **CanvasPane.fs**: Import/reference `ArchiveViews.archiveIcon`, replace `prop.text "🗑️"` with `prop.children [ ArchiveViews.archiveIcon ]`.
- **index.html**: Adjust `.canvas-archive-btn` CSS to size the SVG icon properly (match `.btn-icon` sizing used elsewhere).

## Key Files

| File | Changes |
|---|---|
| `src/Client/CanvasPane.fs` | Error banner rendering, archive icon swap |
| `src/Client/App.fs` | Error state `bool` → `string option`, dismiss msg, remove flash timer |
| `src/Client/index.html` | Error banner CSS, remove flash animation, archive icon sizing |
| `src/Server/CanvasBridge.fs` | Registry entry record with lastHeartbeat, bridge-status query |
| `src/Server/Program.fs` | Bridge-status endpoint |
| `src/Extension/extension.mjs` | Heartbeat loop with reconnect logic |
