# Resume Last Session

## Goals

- After a machine reboot (all terminal sessions gone), allow one-click resume of the last coding session from any worktree card
- Resume the exact stored Copilot session, reusing its running embedded terminal when already live
  and otherwise starting a new one
- Show a resume button only on cards where a session can actually be resumed

## Expected Behavior

### Resume Button Visibility

The resume button appears on a worktree card when ALL of these conditions are true:
- No tracked terminal window exists (`HasActiveSession = false`)
- A previous session message exists (`LastUserMessage.IsSome`) — proves there was a real session to resume
- The coding tool is not actively running (`CodingTool = Idle` or `NoSession`)

The button is **hidden** (not disabled) when conditions aren't met — unlike contextual card actions,
which remain visible and temporarily disable during their launch cooldown.

### Button Appearance

- Icon: connector/plug SVG icon (provided by user)
- Keyboard shortcut: `R` (when card is focused)
- Position: in the card header button group, after the terminal button but before the editor button

### Resume Action

When clicked:
1. Server reads the configured provider from `.treemon.json` (Copilot CLI is the only supported provider)
2. Server runs a scalar durable-store query for the greatest `(UpdatedAt, SessionId)` so
   liveness-only heartbeats cannot change the resume target
3. Server builds `copilot --yolo --resume <id>` via
   `CodingToolCli.build provider (Resume sessionId)`, falling back to `--continue` when no retained
   session exists
4. When the exact stored session is already live in a running embedded terminal, the server returns
   that terminal without starting another Copilot process
5. Otherwise the server starts a new embedded terminal and submits the resume command
6. Client opens the terminal pane and selects the exact returned terminal

### Edge Cases

- If provider cannot be determined: fall back to `CodingToolProvider.Default` (Copilot)
- If the durable row was pruned or never existed: launch with `--continue`
- A different live session or terminal in the same worktree does not suppress Resume
- If command submission fails: the exact new terminal is closed and the launch reports an error

## Technical Approach

### Server: Resume Command Construction

`SessionActivityStore.LatestSessionIdForWorktree` reads only the newest durable session id for the
worktree, independent of the two-hour live window. The query orders by `(UpdatedAt, SessionId)` and
does not hydrate status content. Background-agent lifecycle is process-local and therefore has no
resume-store projection. `LastSeen` remains the liveness, freshness, and retention clock and cannot
influence resume selection.

`CodingToolCli.build` in `CodingToolCli.fs` unifies all coding-tool CLI invocations across the server (Interactive prompts, Resume, NonInteractive). For the resume case, it takes a provider and an optional session ID via the `Resume` `InvocationMode`:
- With session ID: `copilot --yolo --resume <id>` (targets the exact session)
- Without: `copilot --yolo --continue` (fallback)

The permission-skip flag is always included so resumed sessions run unattended, matching the
behavior of fresh sessions launched from the dashboard.

### Server: API Endpoint

`IWorktreeApi.resumeSession` returns the reconciled embedded-terminal snapshot plus the exact
started terminal ID.

Implementation in `WorktreeApi.fs`:
1. Validate path against known worktrees
2. Read the provider from `.treemon.json`, defaulting to Copilot
3. Read the greatest durable `(UpdatedAt, SessionId)` through the scalar store lookup
4. Build the resume command via `CodingToolCli.build provider (Resume sessionId)`
5. Join that exact session identity to the current running-terminal snapshot through
   `TerminalSessionActivity`
6. Return the existing terminal when the exact session is live; otherwise call the shared embedded
   command-launch boundary and return its exact result

It reuses the same embedded command launch as contextual actions and `tm launch`; native
`SessionManager` state is unchanged.

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
- **Hidden over disabled**: Unlike contextual card actions (which remain visible and temporarily disable during launch cooldown), the resume button is hidden when not applicable — it targets a specific scenario (post-reboot) and showing a disabled "resume" button when a session IS active would be confusing
- **Existing fields still decide visibility:** `HasActiveSession`, `LastUserMessage`, and
  `CodingTool` remain sufficient for the button predicate. The remoting contract separately returns
  the shared exact embedded-launch result needed for terminal selection.
- **Exact-session idempotency:** Resume is a no-op when the durable target is already live in a
  running embedded terminal: it returns and selects that exact terminal. Other terminals in the
  worktree do not block Resume, and the existing client in-flight guard still suppresses repeated
  clicks while a launch is pending.

## Key Files

| File | Role |
|------|---------|
| `src/Shared/Types.fs` | `resumeSession` API contract |
| `src/Server/SessionActivityStore.fs` | Scalar durable worktree-session lookup |
| `src/Server/TerminalSessionActivity.fs` | Exact live session-to-terminal join |
| `src/Server/CodingToolStatus.fs` | Provider configuration and card status collapse |
| `src/Server/CodingToolCli.fs` | Unified CLI invocation builder — `Resume` mode handles the resume command |
| `src/Server/WorktreeApi.fs` | `resumeSession` endpoint implementation |
| `src/Client/App.fs` | `ResumeSession` update arm and keyboard shortcut |
| `src/Client/CardViews.fs` | `resumeButton` rendering and `canResumeSession` |
| `src/Client/index.html` | CSS for `.resume-btn` |

## Related Specs

- `docs/spec/worktree-monitor.md` — Contextual card-action visibility and launch behavior
- `docs/spec/embedded-terminal.md` — command-capable embedded launch and exact terminal selection
