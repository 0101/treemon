# Embedded Terminal

Status: compatibility spike, not production functionality.

## Goals

- Display one writable terminal inside the Treemon workspace.
- Start the terminal shell in the selected worktree directory.
- Reuse the three-pane workspace layout proven by the ACP chat branch: `Terminal | Canvas | Dashboard`, with equal thirds, a wide-center `1:2:1` mode, and `1:1` or `2:1` layouts when the terminal pane is closed.
- Use stock `ttyd` so the spike does not implement a pseudoterminal, terminal renderer, or WebSocket protocol.
- Let the user start and evaluate Copilot CLI manually before Treemon adds Copilot-specific behavior.

## Expected Behavior

Opening the embedded terminal from a worktree binds the single terminal pane to that worktree and starts `pwsh` with the worktree as its current directory. The pane header identifies the bound worktree, and closing the pane terminates only the `ttyd` process tree owned by the spike.

The workspace renders the terminal, canvas, and dashboard in fixed left-to-right order. With all panes open, the user can switch between equal thirds and a wide canvas. With the terminal closed, the canvas and dashboard use the corresponding `1:1` or `2:1` layout. The existing external Windows Terminal action remains available.

The spike does not start Copilot automatically. The user starts `copilot` in the embedded shell and evaluates its interactive behavior separately.

## Technical Approach

Treemon manages one stock Windows `ttyd` child process at a time. The process binds only to `127.0.0.1` on an OS-assigned non-production port, enables writable mode and Origin checking, disables URL-controlled commands, starts a fixed `pwsh` command, and receives the selected worktree through `ttyd`'s working-directory option. A deterministic setup command installs an exact `ttyd` version into a gitignored repository tools cache rather than committing the executable.

The client embeds the `ttyd` page in a dedicated iframe and adapts the pane shell, width model, CSS ratios, responsive behavior, and geometry tests from `Q:\code\tm-embed-chat`. Terminal state is limited to closed, starting, running, and failed; the client displays a specific inline error when setup or launch fails.

The server owns process creation, readiness detection, endpoint publication, and cleanup. Cleanup is PID-scoped and must never affect Treemon production, port 5000, Windows Terminal, or unrelated `ttyd` or shell processes.

## Decisions

- This is a disposable feasibility spike, not the production terminal architecture.
- There is one terminal process total, opened explicitly for a worktree. Ordinary dashboard focus changes do not replace a running terminal.
- Browser refresh and dashboard restart reattachment are out of scope.
- Authentication beyond loopback binding and `ttyd` Origin enforcement is out of scope; the spike must not be exposed remotely.
- Copilot launch, prompt injection, status parsing, attachments, terminal tabs/splits, elevation, and session persistence are out of scope.

## Related Specs

- [Canvas pane](../canvas-pane.md) defines the existing iframe pane and workspace presentation patterns.
- [Native session management](../native-session-management.md) remains authoritative for external Windows Terminal sessions.
- [Process execution](../process-execution.md) defines process-launch boundaries and argument safety.
- [Port management](port-management.md) defines non-production port allocation and collision avoidance.
