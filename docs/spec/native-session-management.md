# Native Session Management

## Goals

1. **Focus existing terminal windows** — navigate from the card's explicit native action to the
   correct window among many, by HWND
2. **Track spawned windows** — maintain HWND-to-worktree mapping so focus/kill work reliably
3. **Spawn explicit native terminals** — launch Windows Terminal only from the card's `>` / Enter
   action
4. **Open native tabs** — use the card's `+` action to add a plain PowerShell tab to the tracked
   window
5. **Survive server restarts** — persist tracked sessions to disk, restore and validate on startup
6. **Native Windows** — all sessions run in native PowerShell with full git and Visual Studio access (no WSL)

## Non-Goals

- Session persistence (detach/reattach) — not achievable with Windows Terminal
- Prompted or automatic agent launch — those sessions run in the embedded host
- Reading terminal output — coding-tool status comes from push activity
- Per-tab lifecycle management — each worktree gets one tracked WT window; native `+` tabs are not
  independently addressed
- Cross-machine portability (HWNDs are machine-local)

## Expected Behavior

### Terminal Button (`>` on card)
- **No tracked session**: spawns `wt.exe --window new new-tab -d <path>`, HWND tracked by SessionManager
- **Tracked session exists**: `SetForegroundWindow` to bring window to foreground
- Single button — no separate launch/focus/kill buttons on the card

### Native New Tab (`+` on card)

The button appears only while the worktree has a valid tracked native window. It focuses that
window and asks `wt.exe` to add one plain PowerShell tab in the worktree directory. Agent prompts,
Resume, contextual actions, background fallbacks, and `tm launch` do not use this path.

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
- The flag remains native-only. It drives the terminal-button focus label/glow, native `+`
  visibility, and delete/archive native-kill prompt.
- Embedded terminals do not set this flag or add another card active-session indicator; coding-tool
  status continues to represent agent activity.

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
`Map<string, nativeint>` in a `MailboxProcessor` (`SessionManager.fs`). HWNDs are validated on each
API call. The mailbox owns native spawn, focus, new-tab, kill, and persistence only.

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
- **Explicit native scope** — only the card's `>` / Enter and tracked-window `+` actions launch
  through `SessionManager`; agent-bearing launches use `TerminalLaunch` and the embedded backend

## Key Files

- `src/Server/Win32.fs` — P/Invoke declarations, HWND resolution, focus/kill helpers
- `src/Server/SessionManager.fs` — native window spawn/focus/new-tab/kill/persistence
- `src/Server/TerminalLaunch.fs` — product-level backend selection
- `src/Server/WorktreeApi.fs` — explicit native API wiring and `HasActiveSession` population
- `src/Client/CardViews.fs` — native terminal and new-tab actions
