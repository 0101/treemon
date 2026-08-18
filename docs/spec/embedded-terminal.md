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
- Serialize terminal lifecycle and worktree mutation ownership independently per canonical worktree.
- Drain protocol-1 hosts without stranding their live sessions or stale manifests.
- Keep the terminal pane free of an outer document scrollbar while preserving xterm scrollback.

## Expected Behavior

Opening an embedded terminal creates its tab or activates the existing tab for that worktree.
Starting and running tabs are reused; opening a failed or interrupted tab creates a replacement
session in the same tab position. Each tab is labelled with its worktree display name and shows
starting, running, failed, or interrupted state independently. Closing a live tab terminates only
its durable session and owned process tree, then selects a deterministic neighbour. A cleanup
failure leaves the tab failed and retryable
instead of pretending it closed. Closing the final tab hides the terminal pane. Deleting or
archiving a worktree through Treemon first holds a cross-process canonical-key worktree lock. A
protocol-2 host then grants a lease that rejects starts for that worktree, authoritatively closes its
terminal, remains renewed through the worktree mutation, and is explicitly released on success or
error. With no live host, or while draining protocol 1, the same per-key OS lock bridges startup of
a protocol-2 host; its lease is active before the mutation begins. Unrelated keys use different
locks. The manager returns the acquired lease before the Git/config mutation begins, so the
singleton control mailbox remains available for Start/Get/Close on unrelated keys while the
mutation is blocked. The acquired OS-lock handle transfers with the lease and remains held through
renewal, mutation, cancellation-shielded explicit release, and final disposal; those steps run
outside the mailbox. OS-lock acquisition also runs outside the singleton mailbox. Each pending
acquisition has a request token and cancellation registration; a second lock-acquiring request for
that canonical key receives a busy error, unrelated keys continue immediately, and stale
completions dispose any acquired handle without starting or reserving a terminal. Discovery,
reservation, transport, parsing, and process-cleanup failures abort the worktree operation with an
actionable error. Public tab close is non-reserving and never grants permission to mutate a
worktree. Closing a key already absent from the public snapshot is an unchanged success, including
after an interrupted tab was dismissed; strict delete/archive cleanup retains its authoritative
failure semantics.

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
than treated as a healthy session. If a previously observed host dies, every last-known tab remains
visible as an interrupted tab until that canonical key is restarted or explicitly dismissed.
Restarting one key merges the new host snapshot with the other interrupted tabs, and polling keeps
those unresolved tabs without duplication. Dismissing an interrupted tab is local UI state and does
not claim process cleanup; delete/archive still requires authoritative reservation. Absence of a
never-started host remains an empty pane.

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
Attachment takeover revokes the old attachment before its socket is closed. Every input, resize,
pause, resume, close, and error handler checks that it still owns the session attachment, so queued
frames and late close/error events from the replaced browser cannot reach ttyd or clear the new
attachment.

ttyd runs with writable mode, Origin checking, and single-client/exit-on-disconnect behavior. The
durable host is that sole client. Explicit session close closes the upstream socket; ttyd then
terminates its ConPTY child and exits. The host captures ttyd's exact process-creation identity
immediately after spawn. On Windows, each descendant query opens and retains the expected parent's
`Process` handle, verifies its creation time, and captures exact child creation identities while
that non-reusable parent identity remains retained. A changed or exited parent is a distinct
non-owned result and contributes no children; recursion always supplies the complete expected
parent identity rather than a bare PID. Cleanup snapshots descendants
before closing the upstream, retains captured processes after reparenting, and iterates discovery
and identity-bound termination to convergence under one end-to-end cleanup deadline spanning the
initial snapshot, upstream close, graceful wait, rediscovery, and forced termination. Every discovery,
identity inspection, and termination helper receives only the remaining budget; expiry is an
explicit failure that retains the session for retry. Termination never accepts a PID alone. The
plainly-invoked checked-in Windows helper also verifies exact UTC start ticks, calls
`Kill(entireProcessTree = true)` through the retained object, and waits; an identity mismatch is a
safe no-op. A spawned root whose identity cannot be captured is stopped
only through its retained Node `ChildProcess` handle. Linux discovery uses `/proc` identities but
returns an explicit unsupported cleanup error where no stable termination handle is available. A
reused PID is treated as the owned process having exited and is never signalled; a surviving
captured descendant keeps the session failed and registered. No process-name scan or
inspect-then-PID signal participates.

Start, close, failed-session replacement, and reservation transitions are serialized by canonical
worktree key. A close queued behind a live start waits for it and then closes the exact published
session. Concurrent failed-session retries recheck registry ownership after cleanup, so only one
replacement can be created. Upstream close/error and child-exit handlers synchronously revoke
startup before queuing cleanup on that key. Immediately before publishing Running, the host
rechecks registry ownership, recorded failure, the retained child, upstream readiness, shell
identity, and the public listener without yielding; failed validation performs authoritative
startup cleanup and leaves the session failed for retry. Closed upstreams are excluded from
heartbeat publication. Different keys have independent queues.

Delete/archive reservations are per-key five-minute leases. Acquisition is itself serialized,
publishes the lease before terminal cleanup, and releases it if cleanup fails. Treemon renews the
lease while the worktree mutation runs and always attempts explicit release afterward, including
after caller cancellation, operation failure, or renewal failure. Release runs without caller
cancellation, and the matching per-key OS lock is disposed only after that attempt. The lock covers
current Treemon callers before a host exists, during legacy drain, and throughout the mutation;
neither mechanism blocks another key. An abandoned host lease expires.

The control listener becomes mutation-unavailable as soon as host shutdown begins. New starts,
closes, and diagnostic mutations receive HTTP 503; starts already in flight settle before the host
snapshots and closes every session. The host removes its manifest and exits only after all cleanup
succeeds. Cleanup failure retains both the registry and manifest for retry.

PowerShell writes its PID to a per-session metadata file during the fixed bootstrap command. The
bootstrap uses short environment variable names to stay below ttyd 1.7.7's 256-byte Windows child
command buffer while still exposing `TREEMON_TERMINAL_SESSION_ID` to the shell for future session
binding.

### Treemon control client

`Server.EmbeddedTerminal` is an authenticated control client, not a process registry. It:

- discovers `.agents/durable-terminal/host.json`;
- validates control protocol 2, runtime generation, exact PID start identity, and dynamic
  non-production control port;
- recognizes authenticated protocol-1 manifests by PID, start timestamp, endpoint, and credential,
  reuses their existing sessions, and keeps them listable and closable in drain-only mode;
- serializes host creation across Treemon processes with an OS-held exclusive
  `.agents/durable-terminal/host.lock`, then starts the Node host when no healthy host exists;
- lists, starts, and closes sessions through the loopback control API;
- records each Treemon process reconnect in the host diagnostics;
- keeps the last known snapshot only to surface host failures explicitly.

The lock file is durable but the exclusive handle is not: a crashed starter releases ownership
automatically. The owning Treemon publishes the spawned host's exact PID/start identity into the
lock claim; the host copies it into its uniquely generated manifest and health response. Stale
manifests are reclaimed only while holding the lock and only when the on-disk generation and
PID/start identity still match. The host likewise deletes its manifest only when it still owns that
exact identity, so a late old host cannot erase a replacement.

Protocol-1 compatibility is bounded to manifests written by the immediately preceding host schema.
A live legacy host never accepts a new canonical key from current Treemon: existing keys are reused,
public close drains and stops the host when its last session closes, and strict delete/archive shuts
down the whole legacy host while
holding canonical-key ownership until a protocol-2 lease spans the mutation. A dead legacy manifest
is reclaimed only after its PID/start evidence proves the recorded process is gone, and
compare-before-delete includes its
endpoint credential so legacy cleanup cannot remove a replacement. Ownership change is distinct
from I/O failure: when another Treemon installs a healthy protocol-2 manifest while the legacy host
exits, retirement succeeds and strict close/reservation continues against that replacement without
deleting it. This parser and drain path can be removed once supported installations can no longer
contain a live or persisted protocol-1 `host.json`; it is not a general old-protocol compatibility
layer.

The client/server wire type is
`Starting | Running endpoint | Failed error | Interrupted error`; durable-host session IDs and
process IDs stay internal to the control boundary. Start and close operations return the same
authoritative full snapshot shape used by polling.

Control requests use a 30-second deadline, exceeding the host's bounded ttyd, upstream, and shell
startup windows. A transport timeout or malformed start/close response is still ambiguous, so the
client immediately lists the authoritative registry and reconciles by canonical worktree key.
Retrying a start therefore reuses the disclosed starting/running session rather than spawning a
duplicate.

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

The detached observation records the host generation, manifest PID/start identity, actual process
creation identity, endpoint, and control credential (the credential is omitted from displayed
output). Stop re-reads all of those fields and revalidates the actual process identity immediately
before sending shutdown. A replaced manifest or reused PID is reported as changed ownership; no
shutdown is sent and no wait is performed against the replacement.

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
- **Per-key lease around worktree mutation** — delete and archive proceed only while a renewable
  host reservation rejects starts for the same canonical worktree; acquisition includes
  authoritative close, and the matching OS lock stays held until cancellation-shielded release is
  attempted on every result path.
- **State-directory lock plus runtime generation** — concurrent checkout-local Treemon starters
  converge on one host without treating a PID alone as ownership.
- **Stable identity handles over PID signals** — the Windows helper retains and verifies the exact
  parent while its parent-filtered query captures child identities, including later-reparented
  processes; termination verifies creation time and kills through one retained `Process` object, so
  a reused PID is never signalled.
- **One-version drain compatibility** — protocol 1 is list/close/reuse compatible for existing
  sessions only and is removed after its persisted/live population has drained.
- **Reconcile ambiguous mutations** — bounded timeouts are necessary, but a canonical-key registry
  read is what prevents an undisclosed or duplicate session after a lost response.
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
| `scripts/terminate-owned-process.ps1` | Windows exact-process inspection, parent-handle-bound child discovery, and identity-bound tree termination |
| `scripts/durable-terminal-control.mjs` | Authenticated status and graceful PID-scoped host shutdown |
| `scripts/durable-terminal-observation.mjs` | Detached 24-hour heartbeat/liveness observation and final status evaluation |
| `scripts/verify-durable-terminal-runtime.mjs` | Isolated real-ttyd reconnect and explicit-cleanup verification |
| `scripts/verify-durable-terminal-treemon.mjs` | Browser reload and two-process Treemon restart demonstration |
| `scripts/verify-ttyd-runtime.mjs` | Import-safe stock-ttyd verifier with identity-bound cleanup and retained-child fallback |
| `src/Server/EmbeddedTerminal.fs` | Durable-host discovery and control client |
| `src/Server/Program.fs` | Creates the control client without closing durable sessions on shutdown |
| `src/Client/TerminalPane.fs` | Accessible multi-tab iframe pane pointed at durable proxy endpoints |
| `src/Tests/EmbeddedTerminalTests.fs` | Host discovery, Treemon restart, selective close, and API validation tests |
| `scripts/durable-terminal-host.test.mjs` | Protocol, replay-bound, and metadata-projection tests |
| `scripts/durable-terminal-observation.test.mjs` | Observation replacement, credential, and PID-reuse ownership tests |
| `scripts/verify-ttyd-runtime.test.mjs` | Verifier helper visibility and identity-capture cleanup fallback tests |

## Related Specs

- [Durable terminal productization](future/durable-terminal-productization.md) defines packaging,
  machine-global discovery, host generations, reconnect state, and release work required beyond the
  current host architecture.
- [Canvas pane](canvas-pane.md) defines the workspace iframe and layout patterns.
- [Native session management](native-session-management.md) remains authoritative for the external
  Windows Terminal fallback.
- [Process execution](process-execution.md) defines the separate data-capture process boundary.
- [Port management](future/port-management.md) defines non-production port allocation.
