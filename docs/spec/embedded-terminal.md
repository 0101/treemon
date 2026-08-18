# Embedded Terminal

## Goals

- Provide one writable embedded PowerShell terminal per worktree.
- Keep every opened worktree terminal alive while the user switches among terminal tabs or hides the
  pane.
- Preserve terminal processes when browser frames disconnect or Treemon restarts.
- Keep each terminal's ttyd WebSocket open until the user explicitly closes the terminal.
- Rediscover running terminals from a separately lived durable host.
- Bound reconnect replay and lifecycle diagnostics without persisting terminal content.
- Terminate only the ttyd and PowerShell process tree owned by the closed terminal.
- Keep the terminal pane free of an outer document scrollbar while preserving xterm scrollback.

## Expected Behavior

Opening an embedded terminal creates its tab or activates the existing tab for that worktree.
Starting and running tabs are reused; opening a failed tab creates a replacement session in the
same tab position. Each tab is labelled with its worktree display name and shows starting, running,
or failed state independently. Closing a tab terminates only its durable session and owned process
tree, then selects a deterministic neighbour; closing the final tab hides the terminal pane.
Deleting or archiving a worktree through Treemon closes its terminal before changing the worktree.

The pane header has a Hide action matching the chat pane. Hiding collapses the terminal pane without
closing tabs, stopping processes, disconnecting iframes, or changing the active tab. Opening any
worktree terminal reveals the pane and activates that worktree's existing or newly started tab.
While the pane is visible, selecting a worktree card by mouse, keyboard, or programmatic navigation
activates that worktree's existing terminal tab. If it has no terminal, the pane shows an empty
state with a Start terminal action; existing terminals for other worktrees remain alive and
available in the tab strip. Selection does not start a terminal by itself and does not reveal a
hidden pane.

The iframe connects to a per-session loopback proxy endpoint rather than to ttyd. Each running tab
keeps its iframe mounted, but only the active iframe is visible, so switching tabs does not
reconnect or discard browser terminal state. If an iframe is removed or recreated, only the
browser-facing WebSocket closes. The durable host keeps its upstream ttyd WebSocket open,
continuously drains output into a bounded in-memory replay buffer, and keeps the same PowerShell
process alive. A replacement browser receives ttyd's cached title and preferences, bounded recent
output, and a resize that prompts full-screen applications to redraw. Perfect scrollback
reconstruction is not guaranteed after the raw replay buffer truncates old output.

Treemon starts the durable host lazily on the first terminal request. A normal Treemon shutdown does
not stop the host or its sessions. On restart, Treemon reads the host's authenticated loopback
control state and reconstructs the same terminal snapshot, including the existing browser
endpoints. A failed or unsupported host response is surfaced on the affected terminal tab rather
than treated as a healthy session.

The workspace renders `Terminal | Canvas | Dashboard` in fixed order, with equal-thirds and
wide-center layouts. The terminal pane has a single horizontally scrolling tab strip with roving
keyboard focus and Left/Right/Home/End navigation. The iframe document suppresses its inert outer
scrollbar while xterm's inner viewport remains independently scrollable. The existing external
Windows Terminal action remains available.

Each embedded terminal starts `pwsh`, not Copilot. The user chooses what to run.

## Technical Approach

### Durable host

`scripts/durable-terminal-host.mjs` is a small Node sidecar using the built-in HTTP server and the
`ws` package. It is independent of the Treemon web-server process and owns:

- a session registry keyed by canonical worktree path;
- one stock ttyd 1.7.7 process per session;
- one stable upstream WebSocket using ttyd's `tty` subprotocol;
- a replaceable, single-writer browser WebSocket;
- cached title and preferences frames;
- a 1 MiB raw-output replay buffer;
- ttyd, PowerShell, attachment, close, and heartbeat metadata.

The host sends ttyd's initial JSON size handshake itself. A browser's replacement handshake becomes
a resize frame rather than a second ttyd connection, so it cannot create a replacement PowerShell.
Browser PAUSE/RESUME is handled at the attachment boundary: the host continues draining ttyd while
the browser is paused and catches the browser up from retained frames on resume.

ttyd runs with writable mode, Origin checking, and single-client/exit-on-disconnect behavior. The
durable host is that sole client. Explicit session close closes the upstream socket; ttyd then
terminates its ConPTY child and exits. The host waits for both processes and uses a PID-specific
fallback only if ttyd does not exit after its socket closes.

PowerShell writes its PID to a per-session metadata file during the fixed bootstrap command. The
bootstrap uses short environment variable names to stay below ttyd 1.7.7's 256-byte Windows child
command buffer while still exposing `TREEMON_TERMINAL_SESSION_ID` to the shell for future session
binding.

### Treemon control client

`Server.EmbeddedTerminal` is an authenticated control client, not a process registry. It:

- discovers `.agents/durable-terminal/host.json`;
- validates the host protocol, PID, and dynamic non-production control port;
- starts the Node host when no healthy host exists;
- lists, starts, and closes sessions through the loopback control API;
- records each Treemon process reconnect in the host diagnostics;
- keeps the last known snapshot only to surface host failures explicitly.

The client/server wire type remains `Starting | Running endpoint | Failed error`; durable-host
session IDs and process IDs stay internal to the control boundary. Start and close operations
return the same authoritative full snapshot shape used by polling.

### Client terminal tabs

The client stores the terminal snapshot and active worktree separately. Opening selects the
requested worktree, and worktree selection projects to an existing tab only while the pane is
visible. Switching tabs is a pure client transition, and polling refreshes lifecycle state without
changing selection when the active tab still exists. The tab strip reuses the accessible,
horizontally scrolling chat-tab pattern, including roving keyboard focus and Left/Right/Home/End
tab navigation. Running background iframes remain mounted, while iframe browser-chrome suppression
hides the outer document scrollbar without affecting xterm's scrollable viewport.

### Security and diagnostics

The control server binds to `127.0.0.1` on an OS-assigned port and requires a random bearer token
stored in the gitignored host-state file. Each terminal endpoint uses a separate random attachment
capability. The initial iframe request exchanges it for a strict, HTTP-only session cookie, and the
browser WebSocket additionally requires the exact `127.0.0.1:<session-port>` Origin and ttyd
subprotocol. ttyd's direct port accepts no second WebSocket client.

`.agents/durable-terminal/diagnostics.jsonl` is capped at 1 MiB and contains timestamps, terminal
session identity, host/ttyd/PowerShell PIDs and liveness, attachment state, upstream open/close
metadata, reconnect events, replay truncation, and heartbeat age. It never stores terminal bytes,
prompts, environment contents, worktree paths, or attachment/control capabilities.

## Decisions

- A terminal is identified by canonical worktree path; duplicate tabs for one worktree are invalid.
- Control mutations return an authoritative full snapshot so the browser never has to merge
  independently completed per-tab responses.
- Tab order is opening order; restarting a failed tab retains its position.
- Open terminal count is not artificially capped; terminals exist only after explicit user actions.
- Starting a failed key restarts it; only starting and running keys are reused.
- Worktree deletion and archival close that worktree's terminal instead of leaving a stale shell.
- Tab selection is browser state, not durable-host process state.
- Running background iframes stay mounted to preserve xterm scrollback and connection state.
- **Protocol-aware proxy before custom ConPTY** — the stable upstream WebSocket fixes ttyd's
  connection-owned process lifetime with much less code than a new terminal backend.
- **Separately lived host** — Treemon restart is a control-plane event, not a terminal-lifetime
  event.
- **Stock ttyd page through the proxy** — retains the existing xterm rendering, input, resize, and
  terminal compatibility.
- **Bounded raw replay plus resize** — sufficient for the current architecture; headless terminal
  serialization remains a future hardening option.
- **One writable browser attachment** — replacement is explicit and avoids undefined multi-writer
  input semantics.
- **No host shutdown on Treemon shutdown** — only explicit terminal close, worktree lifecycle,
  host shutdown, process exit, or host failure ends a session.
- **No host-crash recovery claim** — losing the durable host closes ttyd's sole WebSocket and ends
  the process; diagnostics report interruption instead of pretending to reattach.
- **Metadata-only observation** — durability evidence is retained without creating a terminal
  transcript or credential-leak surface.
- The terminal remains loopback-only and is not a remote shell service.
- Stock ttyd 1.7.7 has a 256-byte Windows child-command buffer, so child arguments remain
  path-independent and the worktree travels through the environment.
- End-to-end production-safety checks attribute activity through fixture commands, requests,
  process ancestry, connections/listeners, and termination targets. Unrelated global port-owner or
  Copilot-process churn is recorded for context but does not fail the fixture; ancestry and cleanup
  evidence includes process creation time so PID reuse cannot attach an unrelated process. The
  isolated server also exports its API port through `TREEMON_PORT` and `TREEMON_PORTS`, preventing
  inherited shell profiles or extensions from falling back to production port 5000.

## Key Files

| File | Purpose |
|---|---|
| `scripts/durable-terminal-host.mjs` | Durable ttyd WebSocket owner, browser proxy, replay, lifecycle, and diagnostics |
| `scripts/durable-terminal-control.mjs` | Authenticated status and graceful PID-scoped host shutdown |
| `scripts/durable-terminal-observation.mjs` | Detached 24-hour heartbeat/liveness observation and final status evaluation |
| `scripts/verify-durable-terminal-runtime.mjs` | Isolated real-ttyd reconnect and explicit-cleanup verification |
| `scripts/verify-durable-terminal-treemon.mjs` | Browser reload and two-process Treemon restart demonstration |
| `src/Server/EmbeddedTerminal.fs` | Durable-host discovery and control client |
| `src/Server/Program.fs` | Creates the control client without closing durable sessions on shutdown |
| `src/Client/TerminalPane.fs` | Accessible multi-tab iframe pane pointed at durable proxy endpoints |
| `src/Tests/EmbeddedTerminalTests.fs` | Host discovery, Treemon restart, selective close, and API validation tests |
| `scripts/durable-terminal-host.test.mjs` | Protocol, replay-bound, and metadata-projection tests |

## Related Specs

- [Durable terminal productization](future/durable-terminal-productization.md) defines packaging,
  machine-global discovery, host generations, reconnect state, and release work required beyond the
  current host architecture.
- [Canvas pane](canvas-pane.md) defines the workspace iframe and layout patterns.
- [Native session management](native-session-management.md) remains authoritative for the external
  Windows Terminal fallback.
- [Process execution](process-execution.md) defines the separate data-capture process boundary.
- [Port management](future/port-management.md) defines non-production port allocation.
