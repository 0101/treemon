# Native Session Management

## Goals

1. **Focus existing terminal windows** — navigate from dashboard to the correct window among many, by HWND
2. **Track spawned windows** — maintain HWND-to-worktree mapping so focus/kill work reliably
3. **Spawn terminal windows** — launch new Windows Terminal windows tied to worktrees, tracked by HWND
4. **Kill and respawn** — replace a session by killing the old window and spawning fresh
5. **Survive server restarts** — persist tracked sessions to disk, restore and validate on startup
6. **Native Windows** — all sessions run in native PowerShell with full git and Visual Studio access (no WSL)

## Non-Goals

- Session persistence (detach/reattach) — not achievable with Windows Terminal
- Reading terminal output — Claude status comes from JSONL session logs, already implemented
- Tab-based session management — each worktree gets its own WT window (not a tab), though `launchAction` may open action tabs in an existing tracked window
- Cross-machine portability (HWNDs are machine-local)

## Expected Behavior

### Terminal Button (`>` on card)
- **No tracked session**: spawns `wt.exe --window new new-tab -d <path>`, HWND tracked by SessionManager
- **Tracked session exists**: `SetForegroundWindow` to bring window to foreground
- Single button — no separate launch/focus/kill buttons on the card

### launchSession (API-level)
- Spawns `wt.exe --window new new-tab -d <path> -- <coding-tool> "prompt"`
- If session already exists for worktree, kills it first (one window per worktree)
- Used by `launchAction` (see `docs/spec/contextual-actions.md`) when no tracked window exists for the worktree; `launchAction` handles the new-tab-in-existing-window path separately

### Focus / Kill
- `focusSession` calls `SetForegroundWindow(hwnd)` with ALT keypress workaround for foreground lock
- `killSession` sends `WM_CLOSE` to the specific window (not `Process.Kill`, which would kill ALL WT windows)

### Persistence
- On every state change (spawn, kill, validation), write `Map<string, nativeint>` to `data/sessions.json`
- On startup, read file, validate each HWND with `IsWindow`, seed MailboxProcessor with surviving sessions
- Missing/corrupt file → start with empty map
- Atomic write (temp file + rename) to prevent corruption

### Status Integration
- `WorktreeStatus.HasActiveSession: bool` — true when tracked HWND passes `IsWindow` check
- Dashboard shows green left border on cards with active sessions

## Technical Approach

### HWND Resolution
`wt.exe` is a launcher that sends IPC to `WindowsTerminal.exe` then exits. Resolution:
1. `EnumWindows` before spawn to snapshot existing windows
2. Spawn `wt.exe --window new new-tab -d <path>`
3. Poll `EnumWindows` for new `CASCADIA_HOSTING_WINDOW_CLASS` windows
4. New HWND = diff between before/after sets (200-300ms typical latency)

### Win32 P/Invoke (`Win32.fs`)
`EnumWindows`, `SetForegroundWindow`, `GetWindowThreadProcessId`, `IsWindow`, `GetClassName`, `keybd_event`, `PostMessage` (WM_CLOSE), `ShowWindow`, `BringWindowToTop`

### Server State
`Map<string, nativeint>` in a `MailboxProcessor` (`SessionManager.fs`). HWNDs validated on each API call. All PostAndAsyncReply calls use explicit timeouts (30s spawn, 10s others).

### Persistence Format
```json
{ "sessions": { "Q:\\code\\AITestAgent": 12345678 } }
```
`data/sessions.json`, full rewrite on every state change. `System.Text.Json` serialization.

## Decisions

- **One window per worktree** — HWNDs are reliable identifiers, tab indices are not
- **keybd_event ALT for focus** — simplest reliable workaround for Windows foreground lock (3 lines, no thread attachment)
- **WM_CLOSE for kill** — all WT windows share one process; `Process.Kill` would terminate ALL windows
- **Explicit `new-tab` subcommand** — `wt.exe --window new new-tab -d "path"` required; implicit default silently drops `-d`
- **CreateNoWindow for launcher** — wt.exe launcher is just IPC; hiding its console avoids a flash
- **Full rewrite persistence** — map is small, atomic rewrite is simpler than incremental updates
- **No locking beyond MailboxProcessor** — writes only inside single-threaded agent, no concurrent races
- **P/Invoke EntryPoint attributes** — DLL export names (`IsWindow`, `PostMessageW`) differ from F# binding names; missing EntryPoint crashes the MailboxProcessor silently

## Key Files

- `src/Server/Win32.fs` — P/Invoke declarations, HWND resolution, focus/kill helpers
- `src/Server/SessionManager.fs` — MailboxProcessor state agent, spawn/focus/kill/persist logic
- `src/Server/WorktreeApi.fs` — API wiring, `HasActiveSession` population
