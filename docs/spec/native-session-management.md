# Native Session Management

## Goals

- Keep one explicitly opened Windows Terminal window per worktree, tracked by HWND.
- Focus or close the exact tracked window.
- Restore still-valid tracked windows after a Treemon server restart.
- Keep prompted and automatic agent launches in the embedded-terminal subsystem.

## Expected Behavior

### Card actions

The card's `>` action and Enter key are the only ways to open or focus a tracked native terminal.
With no tracked window, Treemon starts:

`wt.exe --window new -- pwsh -NoExit -EncodedCommand <base64>`

The decoded PowerShell script only changes directory to the worktree. With a valid tracked HWND,
the same action focuses that window instead of starting another one.

The card has no native new-tab action. Its always-visible, icon-only robot-head Agent action and the
focused-card `a` or `A` shortcut start a fresh embedded terminal and submit exactly
`copilot --yolo`. This action neither requires nor changes a tracked native HWND; see
`docs/spec/embedded-terminal.md` for exact-terminal selection and delivery-failure cleanup.

The Agent action, Resume, contextual actions, Canvas launches, create-worktree prompts, AutoSync
fallback, and `tm launch` use embedded terminals instead.

### Focus, close, and persistence

- Focus restores a minimized window and uses foreground-thread attachment plus
  `SetForegroundWindow` and `SwitchToThisWindow`.
- Close sends `WM_CLOSE` to the exact HWND. It never kills `WindowsTerminal.exe`, whose process may
  own unrelated windows.
- The normalized worktree-path-to-HWND map is persisted atomically in `data/sessions.json`.
- Startup discards malformed state and HWNDs that no longer pass `IsWindow`.
- `WorktreeStatus.HasActiveSession` describes only this native tracked-window state.

## Technical Approach

`SessionManager` owns a `MailboxProcessor<Map<string, nativeint>>`. Every request normalizes the
worktree path, removes invalid HWNDs, performs one operation, and persists the map when it changes.

To discover the HWND created by `wt.exe`, Treemon snapshots Windows Terminal windows before launch,
waits for the launcher to exit, and polls for the new hosting-window HWND. `buildScript` doubles
single quotes in the native path, and `encodeCommand` carries the script as UTF-16 PowerShell
`-EncodedCommand` data.

`Win32.fs` contains the P/Invoke boundary for window enumeration, validation, activation, restore,
thread attachment, and `WM_CLOSE`.

## Decisions

- **One tracked window per worktree:** HWNDs are reliable window identities; tab identities are not.
- **Directory through encoded PowerShell:** the native launch does not pass `-d`. A quoted
  `Set-Location` script avoids Windows Terminal argument ambiguity for worktree paths.
- **WM_CLOSE instead of process termination:** all Windows Terminal windows can share one process.
- **Mailbox-owned persistence:** one serialized state owner is sufficient for the small map.
- **Explicit native scope:** only the card terminal action uses this subsystem.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/SessionManager.fs` | Native spawn, HWND tracking, focus, close, and persistence |
| `src/Server/Win32.fs` | Windows window-management P/Invoke boundary |
| `src/Server/TerminalLaunch.fs` | Typed native-versus-embedded launch boundary |
| `src/Server/WorktreeApi.fs` | Native API wiring and `HasActiveSession` population |
| `src/Client/CardViews.fs` | Explicit native card-terminal control |

## Related Specs

- `docs/spec/embedded-terminal.md` - embedded shells and all agent-bearing launches.
- `docs/spec/worktree-monitor.md` - card behavior and worktree lifecycle.
