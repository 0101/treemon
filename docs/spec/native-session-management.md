# Native Session Management

## Goals

- Keep one explicitly opened Windows Terminal window per worktree, tracked by HWND.
- Focus, add a tab to, or close the exact tracked window.
- Restore still-valid tracked windows after a Treemon server restart.
- Keep prompted and automatic agent launches in the embedded-terminal subsystem.

## Expected Behavior

### Card actions

The card's `>` action and Enter key are the only ways to open or focus a tracked native terminal.
With no tracked window, Treemon starts:

`wt.exe --window new -- pwsh -NoExit -EncodedCommand <base64>`

The decoded PowerShell script only changes directory to the worktree. With a valid tracked HWND,
the same action focuses that window instead of starting another one.

The card's `+` action is available only for a valid tracked window. It focuses that window and runs:

`wt.exe -w 0 new-tab -- pwsh -NoExit -EncodedCommand <base64>`

Resume, contextual actions, Canvas launches, create-worktree prompts, AutoSync fallback, and
`tm launch` use embedded terminals instead.

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
- **Directory through encoded PowerShell:** neither native launch passes `-d`. A quoted
  `Set-Location` script avoids Windows Terminal argument ambiguity for worktree paths.
- **WM_CLOSE instead of process termination:** all Windows Terminal windows can share one process.
- **Mailbox-owned persistence:** one serialized state owner is sufficient for the small map.
- **Explicit native scope:** only the card terminal and native new-tab actions use this subsystem.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/SessionManager.fs` | Native spawn, HWND tracking, focus, tab creation, close, and persistence |
| `src/Server/Win32.fs` | Windows window-management P/Invoke boundary |
| `src/Server/TerminalLaunch.fs` | Typed native-versus-embedded launch boundary |
| `src/Server/WorktreeApi.fs` | Native API wiring and `HasActiveSession` population |
| `src/Client/CardViews.fs` | Card terminal and native new-tab controls |

## Related Specs

- `docs/spec/embedded-terminal.md` - embedded shells and all agent-bearing launches.
- `docs/spec/worktree-monitor.md` - card behavior and worktree lifecycle.
