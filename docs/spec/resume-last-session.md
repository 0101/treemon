# Resume Last Session

## Goals

- After a machine reboot (all terminal sessions gone), allow one-click resume of the last coding session from any worktree card
- Launch the correct assistant (Claude or Copilot) with a "continue" flag in a new tracked terminal window
- Show a resume button only on cards where a session can actually be resumed

## Expected Behavior

### Resume Button Visibility

The resume button appears on a worktree card when ALL of these conditions are true:
- No tracked terminal window exists (`HasActiveSession = false`)
- A previous session message exists (`LastUserMessage.IsSome`) — proves there was a real session to resume
- The coding tool is not actively running (`CodingTool = Idle` or `Done`)

The button is **hidden** (not disabled) when conditions aren't met — unlike contextual action buttons which show disabled.

### Button Appearance

- Icon: connector/plug SVG icon (provided by user)
- Keyboard shortcut: `R` (when card is focused)
- Position: in the card header button group, after the terminal button but before the editor button

### Resume Action

When clicked:
1. Server resolves which provider was last active (from scheduler state or `.treemon.json` config)
2. Server looks up the specific session ID for that worktree:
   - Claude: most recent `.jsonl` filename in `~/.claude/projects/{encoded-path}/`
   - Copilot: most recent session directory name in `~/.copilot/session-state/` matching the worktree's `cwd`
3. Server builds a resume command: `claude --resume <id>` or `copilot --resume <id>` (falls back to `--continue` if no session ID found)
4. Server spawns a new tracked Windows Terminal window with the resume command
5. The worktree card transitions to `HasActiveSession = true`

### Edge Cases

- If provider cannot be determined: fall back to `CodingToolProvider.Default` (Copilot)
- If the session files were cleaned up and resume fails: the terminal stays open for the user to start fresh — `pwsh -NoExit` ensures this

## Technical Approach

### Server: Resume Command Construction

New functions in `CodingToolStatus.fs`:

`getLastSessionId` — dispatches by provider to find the most recent session ID:
- Claude: most recent parent JSONL filename (sans extension) from `ClaudeDetector`
- Copilot: most recent session directory name from `CopilotDetector`

`buildResumeCommand` — takes provider + optional session ID:
- With session ID: `claude --resume <id>` / `copilot --resume <id>` (targets exact session)
- Without: `claude --continue` / `copilot --continue` (fallback)

No prompt escaping needed — session IDs are UUIDs.

### Server: API Endpoint

New endpoint on `IWorktreeApi`:
```
resumeSession: WorktreePath -> Async<Result<unit, string>>
```

Implementation in `WorktreeApi.fs`:
1. Validate path against known worktrees
2. Resolve provider from scheduler state (`resolveProvider`)
3. Build resume command via `buildResumeCommand`
4. Call `SessionManager.spawnSession` to spawn a new tracked terminal with the command

Reuses the existing `launchSession` flow (spawn tracked terminal with command) — no new `SessionManager` messages needed.

### Client: Resume Button

New `Msg` variant: `ResumeSession of WorktreePath`

Button rendering function `resumeButton` in `App.fs`:
- Connector/plug SVG icon
- CSS class: `resume-btn`
- Tooltip: "Resume last session (R)"
- onClick dispatches `ResumeSession wt.Path`

### Client: Keyboard Shortcut

Add to `keyBinding`:
```
| Card scopedKey, "r" -> ... ResumeSession ...
```

Same visibility condition as the button: only fires when resume is available.

### Client: Visibility Logic

Helper function `canResumeSession`:
```fsharp
let canResumeSession (wt: WorktreeStatus) =
    not wt.HasActiveSession
    && wt.LastUserMessage.IsSome
    && wt.CodingTool <> Working
    && wt.CodingTool <> WaitingForUser
```

Used in both card renderers (`worktreeCard`, `compactWorktreeCard`) and `keyBinding`.

### CSS

Minimal styling for `.resume-btn` — matches existing button styles (`.terminal-btn`, `.editor-btn`).

## Decisions

- **`--resume <id>` over `--continue`**: `--continue` is supposed to resume the most recent session in the current directory, but in practice Copilot's `--continue` doesn't reliably scope to the working directory — it can resume sessions from other worktrees. Using `--resume <session-id>` with the specific UUID ensures the correct session is targeted. Falls back to `--continue` if no session ID is found.
- **Hidden over disabled**: Unlike contextual actions (which show disabled when tool is active), the resume button is hidden when not applicable — it targets a specific scenario (post-reboot) and showing a disabled "resume" button when a session IS active would be confusing
- **No new shared types needed**: Client already has `HasActiveSession`, `LastUserMessage`, and `CodingTool` — enough to determine visibility. Server resolves provider at request time.
- **Spawn (not new-tab)**: Resume always spawns a new terminal window since the precondition is "no tracked terminal exists"

## Key Files

| File | Changes |
|------|---------|
| `src/Shared/Types.fs` | Add `resumeSession` to `IWorktreeApi` |
| `src/Server/CodingToolStatus.fs` | Add `buildResumeCommand` |
| `src/Server/WorktreeApi.fs` | Wire `resumeSession` endpoint |
| `src/Client/App.fs` | Add `ResumeSession` msg, `resumeButton`, `canResumeSession`, keyboard shortcut |
| `src/Client/index.html` | CSS for `.resume-btn` |

## Related Specs

- `docs/spec/contextual-actions.md` — Similar pattern for action buttons with provider-aware command construction
- `docs/spec/native-session-management.md` — Session spawning/tracking foundation
