# Resume Last Session

## Goals

- Resume the most recently active durable Copilot session for a worktree.
- Reuse the exact running embedded terminal when that session is already live.
- Keep Resume hidden when the worktree is not resumable.

## Expected Behavior

The Resume control appears only when the worktree has a previous user message, no tracked native
terminal, and `CodingToolStatus.NoSession`. It is available from the card and the `R` key.

When invoked, Treemon:

1. Reads the configured coding-tool provider.
2. Selects the durable session with the greatest `(UpdatedAt, SessionId)` for the worktree.
3. Returns the running embedded terminal already owning that exact session, when one exists.
4. Otherwise starts an embedded terminal and submits
   `copilot --experimental --yolo --session-id=<session-id>`.
5. Falls back to `copilot --experimental --yolo --continue` when no durable session ID remains.
6. Opens the terminal pane and selects the exact returned terminal.

Any open coding session in the worktree suppresses Resume; an embedded terminal with no open coding
session does not. Repeated input while a launch is in flight retargets the pane without issuing
another launch. Command-delivery failure closes the newly created terminal and reports the launch
failure.

## Technical Approach

`SessionActivityStore.LatestSessionIdForWorktree` performs the scalar durable lookup independently
of heartbeat recency and the live-session window, ranking exact process instances together with the
pre-upgrade identities retained in `resume_sessions`. `TerminalSessionActivity.tryFindLiveTerminalId`
joins that selected Copilot session to a running terminal through its exact
`TREEMON_TERMINAL_SESSION_ID` origin.

`CodingToolCli` builds the provider-specific resume command with the CLI's direct startup selector,
`--session-id=<id>`, so extensions join the durable target identity from process start.
`WorktreeApi.resumeSession` either returns the matching terminal or uses the shared embedded
command-launch operation.
`CardViews.canResumeSession` is the single visibility predicate used by both mouse and keyboard
entry points.

## Decisions

- **Direct session selector over foreground switching:** `--session-id=<id>` starts under the
  durable identity before extensions join; `--continue` is only the missing-ID fallback.
- **Idempotent exact-session resume:** an already-live target returns its terminal instead of
  starting a second Copilot process.
- **Hidden over disabled:** Resume represents a narrow applicable state rather than a generally
  available action.
- **Durable activity ordering:** heartbeat-only `LastSeen` updates cannot change the selected resume
  identity.

## Key Files

| File | Purpose |
|---|---|
| `src/Server/SessionActivityStore.fs` | Durable latest-session lookup |
| `src/Server/TerminalSessionActivity.fs` | Exact live session-to-terminal join |
| `src/Server/CodingToolCli.fs` | Resume command construction |
| `src/Server/WorktreeApi.fs` | Resume endpoint |
| `src/Client/CardViews.fs` | Visibility and card control |
| `src/Client/App.fs` | Launch state, result handling, and keyboard binding |

## Related Specs

- `docs/spec/session-status-push.md` - durable session identity and terminal origin.
- `docs/spec/embedded-terminal.md` - command launch, exact terminal selection, and failure cleanup.
