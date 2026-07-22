# Resume Last Session

## Goals

- After a machine reboot (all terminal sessions gone), allow one-click resume of the last coding session from any worktree card
- Launch the Copilot CLI with the exact stored session id in a new tracked terminal window
- Show a resume button only on cards where a session can actually be resumed

## Expected Behavior

### Resume Button Visibility

The resume button appears on a worktree card when ALL of these conditions are true:
- No tracked terminal window exists (`HasActiveSession = false`)
- A previous session message exists (`LastUserMessage.IsSome`) — proves there was a real session to resume
- The coding tool is not actively running (`CodingTool = Idle` or `NoSession`)

The button is **hidden** (not disabled) when conditions aren't met — unlike contextual action buttons which show disabled.

### Button Appearance

- Icon: connector/plug SVG icon (provided by user)
- Keyboard shortcut: `R` (when card is focused)
- Position: in the card header button group, after the terminal button but before the editor button

### Resume Action

When clicked:
1. Server reads the configured provider from `.treemon.json` (Copilot CLI is the only supported provider)
2. Server loads the worktree's durable `session_status` rows and selects the greatest
   `(UpdatedAt, SessionId)` so liveness-only heartbeats cannot change the resume target
3. Server builds `copilot --yolo --resume <id>` via
   `CodingToolCli.build provider (Resume sessionId)`, falling back to `--continue` when no retained
   session exists
4. Server spawns a new tracked Windows Terminal window with the resume command
5. The worktree card transitions to `HasActiveSession = true`

### Edge Cases

- If provider cannot be determined: fall back to `CodingToolProvider.Default` (Copilot)
- If the durable row was pruned or never existed: launch with `--continue`
- If resume fails: the terminal stays open for the user to start fresh — `pwsh -NoExit` ensures this

## Technical Approach

### Server: Resume Command Construction

`SessionActivityStore.StatusesForWorktree` loads all durable rows for the worktree, independent of
the two-hour live window. `getLastSessionId` in `CodingToolStatus.fs` selects the greatest
`(UpdatedAt, SessionId)` and returns that exact Copilot session id. `LastSeen` remains the liveness,
freshness, and retention clock and cannot influence resume selection.

`CodingToolCli.build` in `CodingToolCli.fs` unifies all coding-tool CLI invocations across the server (Interactive prompts, Resume, NonInteractive). For the resume case, it takes a provider and an optional session ID via the `Resume` `InvocationMode`:
- With session ID: `copilot --yolo --resume <id>` (targets the exact session)
- Without: `copilot --yolo --continue` (fallback)

The permission-skip flag is always included so resumed sessions run unattended, matching the
behavior of fresh sessions launched from the dashboard.

### Server: API Endpoint

`IWorktreeApi` exposes:
```
resumeSession: WorktreePath -> Async<Result<unit, string>>
```

Implementation in `WorktreeApi.fs`:
1. Validate path against known worktrees
2. Read the provider from `.treemon.json`, defaulting to Copilot
3. Load durable worktree sessions and select the greatest `(UpdatedAt, SessionId)`
4. Build the resume command via `CodingToolCli.build provider (Resume sessionId)`
5. Call `SessionManager.spawnSession` to spawn a new tracked terminal with the command

Reuses the existing `launchSession` flow (spawn tracked terminal with command) — no new `SessionManager` messages needed.

### Client: Resume Button

New `Msg` variant: `ResumeSession of WorktreePath`

Button rendering function `resumeButton` in `CardViews.fs`:
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

| File | Role |
|------|---------|
| `src/Shared/Types.fs` | `resumeSession` API contract |
| `src/Server/SessionActivityStore.fs` | Durable worktree-session lookup |
| `src/Server/CodingToolStatus.fs` | Selects the greatest `(UpdatedAt, SessionId)` for resume |
| `src/Server/CodingToolCli.fs` | Unified CLI invocation builder — `Resume` mode handles the resume command |
| `src/Server/WorktreeApi.fs` | `resumeSession` endpoint implementation |
| `src/Client/App.fs` | `ResumeSession` update arm and keyboard shortcut |
| `src/Client/CardViews.fs` | `resumeButton` rendering and `canResumeSession` |
| `src/Client/index.html` | CSS for `.resume-btn` |

## Related Specs

- `docs/spec/contextual-actions.md` — Similar pattern for action buttons with provider-aware command construction
- `docs/spec/native-session-management.md` — Session spawning/tracking foundation
