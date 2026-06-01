# Canvas Pane — Phase 2+3: UX Polish, Logging, Multi-doc & Discovery

Phase 2 and 3 of the canvas pane feature, building on the MVP (`docs/spec/canvas-pane-mvp.md`).

## Goals

- **Persist canvas state**: toggling the canvas pane writes `canvasPaneOpen: bool` to `~/.treemon/config.json`; reloading the page restores the pane to that state
- **Lifecycle logging**: server logs canvas scanner results, watcher file events, doc-server requests (path + status code), and bridge message forwarding — sufficient to debug any canvas issue from logs alone
- **Multi-doc support**: when a worktree has multiple `.html` files in `.agents/canvas/`, all appear in a tab bar; selecting a tab switches the iframe; single doc = no tab bar
- **Empty canvas overview**: when canvas pane is open but no focused worktree has docs, show a clickable list of all worktrees with canvas docs grouped by repo

> **C key behavior**: already correct — `C` toggles the pane and the iframe shows the focused card's doc. No implementation needed; covered by existing E2E tests.

## Expected Behavior

### Canvas State Persistence

- `CanvasPaneOpen: bool` persisted in global config (`~/.treemon/config.json`) alongside `canvasPosition` and `collapsedRepos`
- On page load, restored from `DashboardResponse` (same pattern as `CollapsedRepos`)
- Toggling dispatches save to server (same pattern as `saveCanvasPosition`)

### C Key Opens Focused Card's Canvas

- Already works correctly — `C` toggles pane, iframe already shows focused card's doc via `focusedWorktreeCanvasDoc`
- No implementation change needed

### Lifecycle Logging

Server (`Log.log`):
- **CanvasScanner**: log when a doc is found/changed/removed during scan
- **CanvasWatcher**: log individual file events (created/changed/deleted), not just watcher lifecycle
- **Canvas doc server**: log each request with path + status code (200/400/404)
- **CanvasBridge**: already logs registration and failures — add message forwarding success

Client:
- No additional logging needed beyond existing `console.error` for canvas message errors

### Multi-doc Support

- **Server**: `CanvasScanner.scan` returns ALL `.html` files (not just first), each with filename + contentHash
- **Shared types**: `WorktreeStatus.CanvasDoc: CanvasDoc option` → `WorktreeStatus.CanvasDocs: CanvasDoc list`
- **Client model**: track `ActiveCanvasDoc: Map<string, string>` (scopedKey → filename) for which doc tab is selected per worktree
- **Client view**: tab bar in canvas pane when worktree has multiple docs; single doc = no tab bar
- **Tab selection**: `SelectCanvasDoc` Msg; persisted per worktree in client state (not server — ephemeral)

### Empty Canvas Overview

When the canvas pane is open but no focused worktree has docs (or no worktree is focused):
- Show a list of all worktrees across all repos that have canvas docs
- Grouped by repo name
- Sorted by most-recently-updated (use `LastCommitTime` as proxy, or doc file mtime if available)
- Sorted by most-recently-updated canvas doc (use file `LastWriteTimeUtc` from server, exposed as `LastModified: DateTimeOffset` on `CanvasDoc`)
- Each entry shows: branch name, doc count, repo name
- Clicking an entry focuses that worktree card AND shows its canvas doc
- Data source: `model.Repos` already contains all worktrees with their `CanvasDocs` — server adds `LastModified` to `CanvasDoc` during scan

## Technical Approach

### State Persistence (follows `saveCollapsedRepos` pattern exactly)

1. **Types.fs**: add `CanvasPaneOpen: bool` to `DashboardResponse`, add `saveCanvasPaneOpen: bool -> Async<unit>` to `IWorktreeApi`
2. **WorktreeApi.fs**: `readCanvasPaneOpen`/`writeCanvasPaneOpen` using `withConfigDocument`/global config write pattern
3. **App.fs**: read from `DashboardResponse` on first load, save on toggle

### Logging

1. **RefreshScheduler.fs**: add `Log.log "CanvasScanner"` in scan results
2. **RefreshScheduler.fs**: add `Log.log "CanvasWatcher"` in file event callbacks
3. **Program.fs**: add `Log.log "Canvas"` for each doc server request with path + status

### Multi-doc

1. **RefreshScheduler.fs**: change `CanvasScanner.scan` to return `CanvasDoc list` (all `.html` files)
2. **Types.fs**: `WorktreeStatus.CanvasDocs: CanvasDoc list` (breaking change — update all consumers)
3. **App.fs**: add `ActiveCanvasDoc` to model, `SelectCanvasDoc` Msg
4. **CanvasPane.fs**: render tab bar when multiple docs, dispatch `SelectCanvasDoc`

### Empty Canvas Overview

1. **CanvasPane.fs**: new `overviewView` function that renders grouped worktree list
2. **CanvasPane.fs**: `view` function takes `allRepos: RepoModel list` to access all worktree data
3. **App.fs**: pass `model.Repos` to `CanvasPane.view` for the overview

## Key Files

| File | Changes |
|------|---------|
| `src/Shared/Types.fs` | `CanvasDocs` list, `CanvasPaneOpen` in response, `saveCanvasPaneOpen` API |
| `src/Server/WorktreeApi.fs` | `readCanvasPaneOpen`/`writeCanvasPaneOpen`, wire into API |
| `src/Server/RefreshScheduler.fs` | Multi-doc scan, watcher event logging, scanner logging |
| `src/Server/Program.fs` | Canvas doc server request logging |
| `src/Client/App.fs` | State persistence, `ActiveCanvasDoc`, `SelectCanvasDoc`, pass repos to pane |
| `src/Client/CanvasPane.fs` | Tab bar, overview view, updated `view` signature |
| `src/Client/index.html` | Tab bar CSS, overview CSS |
| `src/Tests/CanvasPaneTests.fs` | Update for multi-doc, new E2E tests |

## Related Specs

- `docs/spec/canvas-pane.md` — Full feature spec (all phases)
- `docs/spec/canvas-pane-mvp.md` — Phase 1 MVP spec

## Decisions

- **E2E fixture null-list fix**: Test fixtures omitting `CanvasDocs` caused the Fable.Remoting client to fail silently during deserialization (`Cannot convert null to ["List",null]`). F# list types cannot be null, but Newtonsoft.Json's deserialization of missing JSON fields produces null. Fix: (1) fixture JSON always includes `"CanvasDocs": []`, (2) server's `loadFixtures` sanitizes null `CanvasDocs` to `[]` defensively. This was the root cause of all E2E tests timing out — the page rendered but stayed in skeleton/loading state because `DataFailed` was dispatched with no visible error.
