# Embedded Terminal

## Goals

- Provide one writable embedded PowerShell terminal per worktree.
- Preserve the terminal process when its browser frame disconnects or Treemon restarts.
- Keep each terminal's ttyd WebSocket open until the user explicitly closes the terminal.
- Rediscover running terminals from a separately lived durable host.
- Bound reconnect replay and lifecycle diagnostics without persisting terminal content.
- Terminate only the ttyd and PowerShell process tree owned by the closed terminal.

## Expected Behavior

Opening an embedded terminal creates its tab or activates the existing tab for that worktree.
Starting and running tabs are reused; opening a failed tab creates a replacement session in the
same tab position. Tabs remain available while the user switches worktrees or hides the pane.
Closing a tab, deleting its worktree, or archiving it explicitly closes that terminal's durable
session and process tree without affecting another terminal.

The iframe connects to a per-session loopback proxy endpoint rather than to ttyd. Removing or
recreating the iframe closes only the browser-facing WebSocket. The durable host keeps its upstream
ttyd WebSocket open, continuously drains output into a bounded in-memory replay buffer, and keeps
the same PowerShell process alive. A replacement browser receives ttyd's cached title/preferences,
bounded recent output, and a resize that prompts full-screen applications to redraw. Perfect
scrollback reconstruction is not guaranteed when the raw replay buffer has truncated old output.

Treemon starts the durable host lazily on the first terminal request. A normal Treemon shutdown does
not stop the host or its sessions. On restart, Treemon reads the host's authenticated loopback
control state and reconstructs the same terminal snapshot, including the existing browser endpoint.
A failed or unsupported host response is surfaced on the affected terminal tab rather than treated
as a healthy session.

The workspace remains `Terminal | Canvas | Dashboard`. The terminal pane keeps one mounted iframe
for the active tab, uses xterm's own scrollback, and retains the existing external Windows Terminal
action. Each embedded terminal starts `pwsh`; the user chooses what to run.

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
session IDs and process IDs stay internal to the control boundary.

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

- **Protocol-aware proxy before custom ConPTY** — the stable upstream WebSocket fixes ttyd's
  connection-owned process lifetime with much less code than a new terminal backend.
- **Separately lived host** — Treemon restart is a control-plane event, not a terminal-lifetime
  event.
- **Stock ttyd page through the proxy** — retains the existing xterm rendering, input, resize, and
  terminal compatibility.
- **Bounded raw replay plus resize** — sufficient for the feasibility prototype; headless terminal
  serialization remains a production hardening option.
- **One writable browser attachment** — replacement is explicit and avoids undefined multi-writer
  input semantics.
- **No host shutdown on Treemon shutdown** — only explicit terminal close, worktree lifecycle,
  host shutdown, process exit, or host failure ends a session.
- **No host-crash recovery claim** — losing the durable host closes ttyd's sole WebSocket and ends
  the process; diagnostics report interruption instead of pretending to reattach.
- **Metadata-only observation** — durability evidence is retained without creating a terminal
  transcript or credential-leak surface.

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
| `src/Client/TerminalPane.fs` | Existing iframe pane pointed at the durable proxy endpoint |
| `src/Tests/EmbeddedTerminalTests.fs` | Host discovery, Treemon restart, selective close, and API validation tests |
| `scripts/durable-terminal-host.test.mjs` | Protocol, replay-bound, and metadata-projection tests |

## Related Specs

- [Durable terminal productization](future/durable-terminal-productization.md) defines packaging,
  machine-global discovery, host generations, reconnect state, and release work required after the
  proxy feasibility prototype.
- [Canvas pane](canvas-pane.md) defines the workspace iframe and layout patterns.
- [Native session management](native-session-management.md) remains authoritative for the external
  Windows Terminal fallback.
- [Process execution](process-execution.md) defines the separate data-capture process boundary.
- [Port management](future/port-management.md) defines non-production port allocation.
