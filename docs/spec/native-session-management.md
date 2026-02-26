# Native Session Management

## Goals

1. **Focus existing terminal windows** — navigate from dashboard to the correct agent window among many, by HWND
2. **Track spawned windows** — maintain HWND-to-worktree mapping so focus/kill work reliably
3. **Spawn terminal windows** — launch new Windows Terminal windows tied to worktrees, tracked by HWND
4. **Kill and respawn** — replace a session with a new task by killing the old window and spawning fresh (future: contextual actions)
5. **Native Windows** — all sessions run in native PowerShell with full git and Visual Studio access (no WSL)

## Non-Goals

- Session persistence (detach/reattach) — not achievable with Windows Terminal
- Reading terminal output (`get-text`) — Claude status comes from JSONL session logs, already implemented
- Tab-based management — each worktree gets its own WT window, not a tab

## Expected Behavior

### Terminal Button (existing `>` button)
- **No tracked session**: opens a plain PowerShell window via `openTerminal` — spawns `wt.exe --window new -d <path>` (new window, not a tab), HWND tracked by SessionManager so subsequent clicks focus instead of spawning again
- **Tracked session exists**: calls `focusSession` to bring the tracked window to foreground via `SetForegroundWindow`
- This is the **single primary button** — no separate "launch" or "focus" buttons on the card

### Spawn (API-level, triggered by contextual actions)
- `launchSession` API spawns `wt.exe --window new -d <worktree-path> -- <coding-tool> "task description"`
- Coding tool resolved from `CodingToolProvider` on `WorktreeStatus`, not hardcoded (future work — currently hardcodes `claude`)
- Server records the mapping: worktree path → HWND
- If a session already exists for this worktree, kill it first (one window per worktree)
- **Not directly exposed as a card button in this phase** — future contextual actions (e.g., "fix build", "look at PR comments") will call this API

### openTerminal vs launchSession
- `openTerminal` opens a **plain PowerShell window** — no coding tool, no prompt. Used by the terminal button.
- `launchSession` opens a window **running a coding tool with a prompt** — used by future contextual actions.
- Both go through SessionManager for HWND tracking. Both use `--window new` (never `-w 0 new-tab`).
- The difference is only what command runs inside the window: pwsh vs coding-tool.

### Focus
- `focusSession` API calls `SetForegroundWindow(hwnd)` on the tracked HWND
- Window comes to foreground immediately
- Exposed through the terminal button when a tracked session exists

### Kill
- `killSession` API kills the window by PID, removes HWND from tracking
- Future: may be exposed as a button on cards with active sessions

### Status Integration
- Existing `ClaudeDetector.fs` continues to monitor JSONL session files for Working/WaitingForUser/Done/Idle
- `WorktreeStatus` gains `HasActiveSession: bool` field — true when a tracked HWND exists and passes `IsWindow` check
- Dashboard shows whether a tracked window exists (spawned vs. not spawned) alongside Claude activity status

## Technical Approach

### HWND Resolution (Critical Unknown)

`wt.exe` is a launcher — it sends commands to the running Windows Terminal process via IPC then exits. The actual window is owned by `WindowsTerminal.exe`. Resolution strategy:

1. Enumerate all top-level windows before spawn (`EnumWindows`)
2. Spawn `wt.exe --window new -- pwsh ...`
3. Poll `EnumWindows` for new windows matching `WindowsTerminal.exe` class
4. New HWND = diff between before and after sets

Alternative: spawn `pwsh.exe` directly, get its PID, find its console window via `GetConsoleWindow` after attaching. This avoids the WT launcher indirection but loses WT features.

If neither approach is reliable, this is a **blocker** — stop and reassess.

### Win32 P/Invoke

New module `Win32.fs` with:
- `EnumWindows` + callback to list top-level windows
- `SetForegroundWindow` to focus a window
- `GetWindowThreadProcessId` to map HWND to PID/thread
- `IsWindow` to check if tracked HWND is still valid
- `GetClassName` to filter for Windows Terminal windows (class: `CASCADIA_HOSTING_WINDOW_CLASS`)
- `keybd_event` to simulate ALT keypress before SetForegroundWindow (foreground lock workaround)
- `GetForegroundWindow`, `IsWindowVisible`, `ShowWindow`, `BringWindowToTop` as supporting calls

### Server State

In-memory `Map<string, nativeint>` mapping worktree path to HWND, managed by a dedicated `MailboxProcessor` in `SessionManager.fs`. HWNDs validated with `IsWindow` on each API call (windows can be closed by user at any time). The refresh scheduler reads this state to populate `HasActiveSession` on each `WorktreeStatus`.

### API Changes

Extend `IWorktreeApi`:
- `launchSession: LaunchRequest -> Async<Result<unit, string>>` — spawn agent with prompt
- `focusSession: string -> Async<Result<unit, string>>` — focus window by worktree path
- `killSession: string -> Async<Result<unit, string>>` — kill window by worktree path

`openTerminal` opens a plain PowerShell window (no agent) but goes through SessionManager for HWND tracking.

### Client Changes

- **Terminal button becomes context-aware**: if `HasActiveSession` is true, clicking the `>` button calls `focusSession` instead of `openTerminal`
- Visual indicator (e.g., green border) showing whether a tracked session window exists
- No separate launch/focus/kill buttons on cards — the `launchSession` API is infrastructure for future contextual actions (e.g., buttons next to failing builds or PR comment threads that spawn targeted Claude tasks)

## Decisions

- **One window per worktree**, not tabs -- HWNDs are reliable identifiers, tab indices are not
- **Experiment-first** -- validate HWND resolution and SetForegroundWindow before building features
- **No send-keys mid-session** -- initial task is passed as CLI argument at spawn; new tasks kill and respawn
- **Server-side Win32** -- all P/Invoke lives in the F# server; client is a pure PWA
- **Focus approach: keybd_event ALT** -- simulated ALT keypress before SetForegroundWindow is the simplest reliable workaround for Windows foreground lock (see experiment results below)

## Experiment Results

Validated in `src/Tests/Win32ExperimentTests.fs` (Category=Local, must run on machine with Windows Terminal).

### HWND Resolution (Experiment 1)
- **Status: WORKS**
- EnumWindows diff reliably detects new CASCADIA_HOSTING_WINDOW_CLASS windows after `wt.exe --window new` spawn
- Resolution latency: 200-300ms typical (polling at 100ms intervals)
- Same approach works for Claude spawn (Experiment 3)

### SetForegroundWindow (Experiments 2, 2b-2e)
- **Direct SetForegroundWindow**: May be blocked by Windows foreground lock when calling process is not the foreground owner
- **AllowSetForegroundWindow**: Does not help (caller cannot grant itself permission)
- **AttachThreadInput workaround**: WORKS -- attach calling thread to foreground thread, call SetForegroundWindow, detach
- **keybd_event ALT workaround**: WORKS -- simulate ALT keypress to bypass foreground lock, then SetForegroundWindow. Simplest approach (3 lines)
- **ShowWindow + BringWindowToTop combo**: WORKS -- ShowWindow(SW_RESTORE) + BringWindowToTop + SetForegroundWindow
- **Combined approach**: WORKS -- all techniques together (overkill but validates each)

**Recommended for production**: `keybd_event ALT` approach. It is the simplest (no thread attachment/detachment needed) and reliable. Fallback to AttachThreadInput if edge cases are found.

### Claude Spawn (Experiment 3)
- **Status: WORKS**
- `wt.exe --window new -d <path> -- claude "prompt"` spawns correctly
- HWND resolution works identically to plain pwsh spawn

## Risks

- **HWND resolution timing**: RESOLVED -- polling at 100ms with 10s timeout works reliably (200-300ms typical)
- **SetForegroundWindow restrictions**: RESOLVED -- keybd_event ALT workaround bypasses foreground lock
- **Window clutter**: 10+ windows in taskbar/Alt-Tab -- acceptable for now, reassess if it becomes a problem
- **Race conditions**: User closes window between HWND capture and focus attempt (mitigated by `IsWindow` check)
