# Embedded Terminal

## Goals

- Provide one writable embedded terminal per worktree.
- Keep every opened worktree terminal alive while the user switches among terminal tabs.
- Start each shell in its worktree and terminate only the process tree owned by a closed tab.
- Reuse stock `ttyd` for ConPTY, WebSocket transport, resizing, and terminal rendering.
- Keep the terminal pane free of an outer document scrollbar while preserving xterm scrollback.

## Expected Behavior

Opening an embedded terminal from a worktree creates its tab or activates the existing tab for that
worktree. Tabs remain alive and retain their terminal state while another tab is selected. Each tab
is labelled with its worktree display name and shows starting, running, or failed state
independently; closing it terminates only its owned `ttyd` process tree and selects a deterministic
neighbour tab. Closing the final tab hides the terminal pane. Opening a worktree whose tab is
starting or running reuses that tab and its live process; opening a worktree whose tab has failed
replaces the failed entry with a fresh start, so a failure is always recoverable without a restart
of Treemon. Deleting or archiving a worktree through Treemon closes its terminal before changing
the worktree. A browser refresh rediscovers the server-owned tabs and selects one of them
deterministically instead of creating new processes.

The pane header has a Hide action matching the chat pane. Hiding collapses the terminal pane without
closing tabs, stopping processes, disconnecting iframes, or changing the active tab. Opening any
worktree terminal reveals the pane and activates that worktree's existing or newly started tab.
While the pane is visible, selecting a worktree card by mouse, keyboard, or programmatic navigation
activates that worktree's existing terminal tab. If it has no terminal, the pane shows an empty
state with a Start terminal action; existing terminals for other worktrees remain alive and
available in the tab strip. Selection does not start a terminal by itself and does not reveal a
hidden pane.

The workspace renders `Terminal | Canvas | Dashboard` in fixed order, with equal-thirds and
wide-center layouts. The terminal pane has a single horizontal tab strip and one visible iframe.
Only xterm's terminal viewport scrolls; the iframe document does not show a second inert scrollbar.
The existing external Windows Terminal action remains available.

Treemon starts `pwsh`, not Copilot. The user chooses what to run in each worktree terminal.

## Technical Approach

The server owns a mailbox-confined registry keyed by canonical `WorktreePath`. Each entry contains
one public tab state and its startup cancellation/process ownership. Starting an existing key
returns the current registry without restarting it; closing a key cancels or kills only that entry.
Process exit changes only the matching tab to failed, and server shutdown cleans every owned entry.
Registry snapshots keep insertion order stable across lifecycle changes and failed-tab restarts.
Start and close return the same full snapshot shape used by polling; start uses `Result` only for a
rejected request, while an accepted process-launch failure is the matching tab's failed lifecycle.
Close does not complete until that tab's owned process tree has exited, without blocking mailbox
operations for other tabs.

The client stores the terminal snapshot and active worktree separately. Opening selects the requested
worktree, and worktree selection projects to an existing tab only while the pane is visible.
Switching tabs is a pure client transition, and polling refreshes lifecycle state without changing
selection when the active tab still exists. The tab strip reuses the accessible,
horizontally scrolling chat-tab pattern. Each running tab keeps its iframe mounted but only the active
iframe is visible, so switching does not reconnect or discard browser terminal state.

Each `ttyd` process binds only to `127.0.0.1` on an OS-assigned port, enables writable mode and
Origin checking, and starts a fixed PowerShell command. The validated worktree is passed through
`ttyd -w` and `TREEMON_TERMINAL_WORKTREE`; a compact post-profile `Set-Location -LiteralPath`
restores it after profiles run. Arguments use `ProcessStartInfo.ArgumentList`.

## Decisions

- A terminal is identified by canonical worktree path; duplicate tabs for one worktree are invalid.
- Registry mutations return an authoritative full snapshot so the browser never has to merge
  independently completed per-tab responses.
- Tab order is opening order; restarting a failed tab retains its position.
- Open terminal count is not artificially capped; terminals exist only after explicit user actions.
- Starting a failed key restarts it; only starting and running keys are reused.
- Worktree deletion and archival close that worktree's terminal instead of leaving a stale shell.
- Tab selection is browser state, not server process state.
- Running background iframes stay mounted to preserve xterm scrollback and connection state.
- Browser refresh can rediscover server-owned tabs, but dashboard/server restart persistence is out
  of scope.
- The terminal remains loopback-only and is not a remote shell service.
- Stock ttyd 1.7.7 has a 256-byte Windows child-command buffer, so child arguments remain
  path-independent and the worktree travels through the environment.

## Related Specs

- [Canvas pane](canvas-pane.md) defines the workspace iframe and layout patterns.
- [Native session management](native-session-management.md) remains authoritative for the external
  Windows Terminal fallback.
- [Process execution](process-execution.md) defines process-launch and argument-safety boundaries.
- [Port management](future/port-management.md) defines non-production port allocation.
