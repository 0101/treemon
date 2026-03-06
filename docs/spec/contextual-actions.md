# Contextual Action Buttons

## Goals

- Launch a coding tool (Claude/Copilot) in interactive mode directly from dashboard card badges
- Target three specific workflows: fix unresolved PR comments, fix failing builds, create a PR
- Reuse existing session management — spawn window if none exists, open new tab if tracked window exists
- Buttons disabled when coding tool is already active (Working/WaitingForUser)

## Expected Behavior

### Action Buttons

Three contextual action buttons appear on cards when conditions are met:

| Condition | Button Location | Prompt Sent |
|-----------|----------------|-------------|
| Unresolved PR comments (AzDo: `unresolved > 0`, GitHub: `total > 0` on open PRs only) | Next to thread/comment badge | `/pr <pr-url>` |
| Failed build | Next to failed build badge | `/fix-build <build-url>` |
| No PR exists (suppressed on main/master branches) | PR row area (where badge would be) | `Commit all changes, push to origin, and create a pull request for this branch` |

### Disabled State

All action buttons disabled when `CodingTool = Working` or `CodingTool = WaitingForUser`, matching the sync button pattern. Tooltip shows "{provider} is active".

### Session Handling

Single smart API endpoint (`launchAction`) handles both cases:
- **No tracked window**: Spawns new Windows Terminal window with coding tool + prompt (reuses `launchSession` path)
- **Tracked window exists**: Focuses window, opens new tab with coding tool + prompt

### Interactive Mode

Sessions must stay open for user to review and interact:
- Claude: `claude "<prompt>"` (positional argument, not `-p`)
- Copilot: `copilot -i "<prompt>"` (interactive flag, not `-p`)

## Technical Approach

### Server: Smart Launch Endpoint

New `launchAction` API endpoint on `IWorktreeApi`:
```
launchAction: LaunchRequest -> Async<Result<unit, string>>
```

Takes existing `LaunchRequest = { Path: WorktreePath; Prompt: string }`. Implementation checks `SessionManager` for tracked HWND:
- Found + valid → focus window, open new tab with provider-aware command
- Not found → spawn new window with provider-aware command

### Server: Provider-Aware Command Construction

`SessionManager.openNewTabInWindow` extended to accept an optional command string. New helper builds the interactive launch command per provider:
- Reads `CodingToolProvider` from per-worktree state (`CodingToolData` map in `PerRepoState`)
- Constructs `claude '<escaped>'` or `copilot -i '<escaped>'`
- Falls back to Claude when no provider detected

### Client: Single Msg + Button Functions

One new `Msg` variant: `LaunchAction of path: WorktreePath * prompt: string`. Update handler calls `worktreeApi.launchAction`.

Action button rendering functions take `dispatch`, `wt: WorktreeStatus`, and condition-specific data (PR URL, build URL). Each checks `wt.CodingTool` for disabled state.

## Decisions

- **Single smart endpoint over separate spawn/tab endpoints**: Client doesn't need to know session state — server checks HWND and picks the right path
- **Positional argument over stdin/SendInput**: `claude "prompt"` starts interactive mode reliably; no stdin pipe exists to wt.exe-launched processes
- **Disabled over hidden**: Action buttons show as disabled (not hidden) when coding tool is active — user sees the actions exist but understands why they can't click

## Key Files

| File | Changes |
|------|---------|
| `src/Shared/Types.fs` | Add `launchAction` to `IWorktreeApi` |
| `src/Server/SessionManager.fs` | Extend `openNewTabInWindow` with optional command, add `LaunchAction` message |
| `src/Server/WorktreeApi.fs` | Wire `launchAction` endpoint with provider-aware command construction |
| `src/Client/App.fs` | Add `LaunchAction` msg, action button rendering functions, integrate into PR row and build badges |
| `src/Client/index.html` | CSS for action button styles |

## Related Specs

- `docs/spec/native-session-management.md` — Session management foundation, explicitly designed for "future contextual actions"
- `docs/spec/worktree-monitor.md` — Card layout, PR/build badge rendering
