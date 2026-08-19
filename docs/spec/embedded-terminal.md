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
- Preserve generation-scoped empty-job evidence across host and Treemon-manager death.
- Pin every host generation to an immutable, content-addressed host/supervisor runtime bundle.
- Treat supervisor cleanup evidence as untrusted until a crash-safe terminal promotion.
- Serialize terminal lifecycle and worktree mutation ownership independently per canonical worktree.
- Drain protocol-1 and pre-bundle protocol-2 hosts without stranding their live sessions or stale
  manifests.
- Reject invalid generation identities and evidence paths before any filesystem access.
- Keep the terminal pane free of an outer document scrollbar while preserving xterm scrollback.

## Expected Behavior

Opening an embedded terminal creates its tab or activates the existing tab for that worktree.
Starting and running tabs are reused; opening a failed or interrupted tab creates a replacement
session in the same tab position. Each tab is labelled with its worktree display name and shows
starting, running, failed, or interrupted state independently. Closing a live tab terminates only
its durable session's kernel-owned Job Object, then selects a deterministic neighbour. A cleanup
failure leaves the tab failed and retryable instead of pretending it closed. Closing the final tab
hides the terminal pane. Deleting or
archiving a worktree through Treemon first holds a cross-process canonical-key worktree lock. A
protocol-3 host then grants a lease that rejects starts for that worktree, authoritatively closes its
terminal, remains renewed through the worktree mutation, and is explicitly released on success or
error. With no live host, the same per-key OS lock bridges startup of a protocol-3 host; its lease is
active before the mutation begins. A pre-bundle protocol-2 host may grant its existing per-key lease
for already-running sessions, while a capability-bearing protocol-1 host uses the bounded drain
path. A host that does not declare Job Object ownership remains listable but cannot authorize strict
mutation. Unrelated keys use different locks. The manager returns the acquired lease before
the Git/config mutation begins, so the
singleton control mailbox remains available for Start/Get/Close on unrelated keys while the
mutation is blocked. The acquired OS-lock handle transfers with the lease and remains held through
renewal, mutation, cancellation-shielded explicit release, and final disposal; those steps run
outside the mailbox. OS-lock acquisition also runs outside the singleton mailbox. Each pending
acquisition has a request token and cancellation registration; a second lock-acquiring request for
that canonical key receives a busy error, unrelated keys continue immediately, and stale
completions dispose any acquired handle without starting or reserving a terminal. Discovery,
reservation, transport, parsing, and Job Object cleanup failures abort the worktree operation with an
actionable error. Public tab close is non-reserving and never grants permission to mutate a
worktree. Closing a key already absent from the public snapshot is an unchanged success, including
after an interrupted tab was dismissed; strict delete/archive cleanup retains its authoritative
failure semantics. Mutation and renewal run under linked cancellation: renewal failure cancels the
mutation immediately, waits for it to settle while the canonical-key lock remains held, then
attempts cancellation-shielded release before disposing that lock. No same-key start can pass during
that cancellation or settlement; unrelated keys remain responsive.

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
control state, validates its protocol-3 bundle hash, component hashes, supervisor generation, and
capabilities, and reconstructs the same terminal snapshot, including the existing browser
endpoints. A failed or unsupported host response is surfaced on the affected terminal tab rather
than treated as a healthy session. If a previously observed host dies, every last-known tab remains
visible as an interrupted tab until that canonical key is restarted or explicitly dismissed. An
exact manifest generation/PID/start-identity change is detected before `KnownHost` is replaced:
every prior-generation Starting or Running tab becomes Interrupted, and ordinary polling retains
those tabs by canonical key even when the replacement registry omits them or already contains a
same-key session. Restarting one key explicitly adopts the replacement generation's session without
dropping the other interrupted tabs. Dismissing an interrupted tab is local UI state and does not
claim process cleanup. Strict close/delete/archive never treats replacement-registry absence as
cleanup evidence: it first proves the prior exact host identity is dead, then validates every
matching generation record against an atomic helper-written empty witness and the exact supervisor
PID/start identity. This check reads durable records directly, so it remains effective in a fresh
Treemon manager with no in-memory prior-generation map and when a replacement host has already
started. A missing, forged, mixed-trust, or still-running supervisor witness returns an actionable
error instead of licensing cleanup.

Absence of a never-started host remains an empty pane.

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
durable host is that sole client. ttyd is never started directly. The host first spawns the
plainly-invoked, bundle-pinned `terminal-job-supervisor.ps1` over private stdin/stdout pipes with a
random per-supervisor control token; every message also carries the exact terminal session identity and
supervisor-protocol generation (currently generation 2). Before every helper spawn the host
revalidates the pinned file's SHA-256 and containment inside its immutable bundle; it never resolves
a mutable publish-directory sibling. The supervisor creates a Windows Job Object with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and neither breakaway flag, creates ttyd with
`CREATE_SUSPENDED`, assigns it to the job, and only then resumes its first thread. Launch uses
`STARTUPINFOEX` with `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` and
`EXTENDED_STARTUPINFO_PRESENT`; the whitelist contains only the intended inheritable `NUL` handle
used for the three child standard handles. Clearing inheritance on the supervisor's control
stdin/stdout/stderr remains a checked defense in depth. No other inheritable supervisor handle can
reach ttyd, so no job member can consume supervisor commands, emit authenticated protocol output,
or keep a control pipe open. ttyd and every
PowerShell/Copilot descendant therefore inherit one kernel-enforced session boundary before any of
their code can run.

Before a session can launch, the host atomically records its host generation, bundle identity,
canonical worktree, session ID, SHA-256 commitment to an unguessable per-supervisor witness nonce,
and helper PID in
`terminal-generations/<generation>.json`. The exact helper creation identity is added before Running
publication; an early helper failure supplies that identity in its witness. Every new session is
durably `in-progress`, which is untrusted for recovery. Once the helper observes
`ActiveProcesses == 0`, it atomically writes and flushes
`terminal-empty-witnesses/<generation>/<session>.json`, bound to the generation, worktree, session,
nonce, and exact helper identity, before sending an authenticated empty event or exiting. Startup
failures use the same witness path before Job creation, while the root is suspended, after
assignment, after resume, and after the ready handshake.

The host retains both the authenticated supervisor channel and its Node `ChildProcess` handle for
the session lifetime. Shell PID discovery accepts the bootstrap metadata only after the supervisor
confirms that PID is a member of the session job. Explicit close first disconnects the upstream,
then asks the supervisor to terminate the job. Registry removal requires both supervisor exit and
an authenticated, session-bound, generation-matched `empty: true` proof, accepted from either the
explicit termination response or the natural root-exit event. Exit alone is never cleanup proof;
buffered final protocol lines are drained through stream closure within the shared cleanup
deadline. Wrong authentication/session/generation, malformed framing, unsolicited messages, and
contradictory duplicates make the channel sticky-failed and synchronously quarantine the generation
record before any later proof can be considered; a later valid-looking proof cannot restore trust.
If quarantine persistence fails, the callback catches the error, keeps the in-memory channel
quarantined, and leaves the prior durable `in-progress` state untrusted. Exact duplicate events
remain idempotent. Timeout, pipe loss, protocol failure, or failed
acknowledgement without a valid late proof leaves the session failed and retryable. Host exit or
pipe loss makes the supervisor terminate the job;
helper failure still closes its retained job handle, so kill-on-close prevents descendants escaping
the owning host. Unsupported operating systems fail explicitly rather than falling back to
descendant enumeration. Targeted PID/start-identity inspection remains only for host/verifier
metadata and is not a terminal ownership or cleanup authority.

After an authenticated empty transcript, the host waits for the exact supervisor exit and closed
output, validates the nonce-bound witness from its contained path, and atomically promotes the
session to `trusted-empty` with the exact zero exit identity. Cleanup/removal proceeds only after
that promotion. A crash before the promotion, including a crash after witness creation, therefore
leaves durable state untrusted. A failed promotion write quarantines the session and returns cleanup
failure rather than exposing an optimistic success.

Start, close, failed-session replacement, and reservation transitions are serialized by canonical
worktree key. A close queued behind a live start waits for it and then closes the exact published
session. Concurrent failed-session retries recheck registry ownership after cleanup, so only one
replacement can be created. Upstream close/error and supervisor-exit handlers synchronously revoke
startup before queuing cleanup on that key. Immediately before publishing Running, the host
rechecks registry ownership, recorded failure, the retained supervisor, upstream readiness,
job-confirmed shell identity, and the public listener without yielding; failed validation performs
authoritative startup cleanup and leaves the session failed for retry. Closed upstreams are
excluded from heartbeat publication. Different keys have independent queues.

Startup failure performs the same authoritative cleanup and closes its public listener and upstream
as part of the same failed transition. A conclusive helper-written witness, clean transcript,
helper exit, and terminal trust promotion leave the failed tab retryable and eligible for public
close or strict reservation; cleanup without proof
leaves the tab failed and blocks those transitions without affecting other keys.
`startup-failure-empty` carries the failed-launch result in the authenticated protocol while the
supervisor exits zero: zero means only that every possible artifact was proved absent, the
generation/session/nonce/exact-supervisor witness was durably written, and the proof event was
successfully emitted. Any unproved cleanup, witness failure, or proof-write failure exits nonzero
and cannot be promoted.

Generation records outlive a dead or replaced host manifest. Strict cleanup scans every current and
retired record relevant to the canonical worktree and requires every matching supervisor's terminal
`trusted-empty` state, exact nonce witness, zero exit transcript, and dead process identity. Legacy,
in-progress, and quarantined records never authorize cleanup. Fully witnessed dead generations are
compacted by atomically renaming the canonical record to a claim whose filename
binds the record generation and a unique compactor generation/PID/exact-start identity. The owned
claim is compared and deleted first; only after both canonical record and claim are confirmed absent
does best-effort witness cleanup begin. No path can restore a record after witness cleanup starts.
A competing manager waits for a live exact owner and recovers only a stale claim, so parallel
compactors converge without discarding proof. Bounded claims from the former manager grammar and
the former host `*.reclaim.json` grammar are restored as evidence before normal record enumeration;
unknown or conflicting claims are preserved and fail closed. Witnesses left by a crash after the
record commit are orphaned and removed idempotently by later manager cleanup. At most 64 unresolved
generation records are retained. Reaching that bound refuses another host generation rather than
dropping evidence. A dead protocol-1, pre-bundle protocol-2, or pre-trust generation has no
sufficient proof and blocks matching strict cleanup until the operator authoritatively drains it or
manually removes the retained migration record after a machine restart.

The immutable bundle contains the host, supervisor, process-identity helper, checksum-pinned ttyd
executable, and the exact locked `ws` ESM runtime. Every included file is named and hashed in the
manifest; the bundle identity also commits the ttyd hash, aggregate `ws` package hash, host and
supervisor protocol versions, and capability set. The host is launched from the bundle, resolves
`ws` by an explicit bundle-relative URL, and includes local throwing guards for `bufferutil` and
`utf-8-validate` while disabling both optional imports before loading `ws`, so optional-module
resolution cannot climb into mutable parent `node_modules`.
Immediately before each supervisor request the host revalidates the bundled ttyd bytes and passes
only that path. Source, build-output, and publish copies are candidate inputs only and are never a
runtime fallback.

Bundles referenced by the current host manifest or any live/retired generation record are never
removed. Startup retains at most eight additional unreferenced bundles and compacts only older
unreferenced directories after an atomic claim and before/after recursive content comparison.
Deploy and rollback may therefore replace publish files while old pinned artifacts coexist until
their host and evidence drain; an existing hash directory is verified, never rewritten. A change
to host code, supervisor/helper code, `ws`, ttyd, protocol, or capabilities produces a different
bundle. A Start reuses an existing session on the running bundle, but a different deployed
candidate makes that host drain-only for new keys. Once its registry is empty, it shuts down before
the new bundle generation starts.

Delete/archive reservations are per-key five-minute leases. Acquisition is itself serialized,
publishes the lease before terminal cleanup, and releases it if cleanup fails. Treemon renews the
lease while the worktree mutation runs. Mutation completion is raced against renewal: if renewal
fails first, linked cancellation stops the mutation promptly and Treemon waits for mutation
settlement while retaining the per-key OS lock. It then always attempts explicit release,
including after caller cancellation, operation failure, or renewal failure. Release runs without
caller cancellation, and the matching lock is disposed only after that attempt. The lock covers
current Treemon callers before a host exists, during legacy drain, cancellation/settlement, and
throughout the mutation; neither mechanism blocks another key. An abandoned host lease expires.

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
- validates control protocol 3, runtime generation, exact PID start identity, immutable bundle and
  component hashes, supervisor generation/capabilities, and dynamic non-production control port; a
  manifest/health pair without `ownershipBoundary:
  "windows-job-v1"` stays listable but cannot start a terminal, authorize strict cleanup, or shut
  down through the current manager;
- recognizes authenticated protocol-1 and protocol-2 manifests by PID, start timestamp, endpoint,
  and credential, keeps existing sessions listable/reusable/closable, and permits bounded drain only
  when the same kernel-ownership capability is present; neither protocol may accept a new key;
- serializes host creation across Treemon processes with an OS-held exclusive
  `.agents/durable-terminal/host.lock`, then starts the Node host when no healthy host exists;
- resolves candidate files from the server output/publish `scripts` directory, falling back to the
  source checkout for development; it validates the pinned ttyd checksum and locked `ws` version,
  hashes every runtime file into `terminal-runtime-bundles/<bundle-hash>/`, atomically installs or
  verifies that immutable set, then launches the bundled host with bundled dependency paths and
  hashes;
- lists, starts, and closes sessions through the loopback control API;
- records each Treemon process reconnect in the host diagnostics;
- keeps the last known snapshot plus per-key prior-generation ownership evidence so replacement
  reconciliation cannot erase or authorize cleanup of older tabs;
- reads generation manifests and helper-written witnesses for every strict close/reservation,
  independent of process-local prior-generation state, and compacts only fully witnessed retired
  generations.

The lock file is durable but the exclusive handle is not: a crashed starter releases ownership
automatically. The owning Treemon publishes the spawned host's exact PID/start identity into the
lock claim; the host copies it into its uniquely generated manifest and health response. Stale
host-discovery manifests are reclaimed only while holding the lock and only when the on-disk
generation and PID/start identity still match. Their generation evidence is retained first (or an
unknown-session migration record is created when an older host never wrote one). The host likewise
deletes its manifest only when it still owns that exact identity, so a late old host cannot erase a
replacement.

Compatibility is bounded to protocol 1 and the immediately preceding pre-bundle protocol 2 schema.
A live legacy host never accepts a new canonical key from current Treemon: existing keys are reused,
public close drains and stops the host when its last session closes, and protocol 2 can provide its
existing per-key cleanup lease without stopping unrelated sessions. Protocol 1 uses its bounded
whole-host drain for strict mutation because it has no reservation endpoint. A dead legacy manifest
is reclaimed for replacement startup only after its PID/start evidence proves the recorded process
is gone, and compare-before-delete includes its endpoint credential so legacy cleanup cannot remove
a replacement. Before reclamation it becomes a bounded retired migration record. Protocol 1 has no
generation/session empty witnesses, and protocol 2 lacks terminal trust/bundle identity, so neither
record can authorize fresh-manager cleanup merely from an old witness. Unknown or matching live
session evidence remains fail-closed until the operator authoritatively drains it or manually
removes the retained record after a machine restart. Ownership
change is distinct
from I/O failure: when another Treemon installs a healthy protocol-3 manifest while the legacy host
exits, retirement succeeds and strict close/reservation continues against that replacement without
deleting it. This parser and drain path can be removed once supported installations can no longer
contain a live or persisted protocol-1/protocol-2 `host.json`; it is not a general old-protocol
compatibility layer.

Generation, bundle, session, and witness filenames use bounded ASCII identities. Host generations
must match `[A-Za-z0-9_-]{1,128}` at every producer, parser, record, and supervisor boundary. Before
reading, writing, moving, or deleting evidence, both host and manager resolve it under the intended
state root, require the expected direct-child/descendant shape, reject rooted input, separators,
`..`, alternate data streams, Unicode lookalikes, and reparse points, and compare paths using the
platform's filesystem rules. An invalid host manifest is preserved as quarantined evidence and
prevents replacement startup.

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

The detached observation records the host generation, bundle/component/ttyd/`ws` hashes,
supervisor generation/capabilities, manifest PID/start identity, actual process creation identity,
endpoint, and control credential (the credential is omitted from displayed output). Stop re-reads
all of those fields and revalidates the actual process identity immediately before sending
shutdown. A replaced runtime, manifest, or reused PID is reported as changed ownership; no shutdown
is sent and no wait is performed against the replacement.

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
  authoritative close, and renewal failure cancels and settles the mutation before the matching OS
  lock reaches cancellation-shielded release and disposal.
- **State-directory lock plus runtime generation** — concurrent checkout-local Treemon starters
  converge on one host without treating a PID alone as ownership.
- **Content-addressed runtime bundle** — deployment can replace mutable publish files without
  changing a running host/helper/dependency contract. Bundle directories contain the exact host,
  supervisor, process helper, ttyd, and locked `ws` files; all are hashed, and live/current/retired
  generation references retain them while only excess unreferenced bundles are compacted through a
  compare-before-delete claim.
- **Job Object ownership before execution** — a per-session supervisor creates ttyd suspended,
  assigns it to a kill-on-close/no-breakaway Job Object, and resumes only after assignment.
  `STARTUPINFOEX` whitelists only the shared child `NUL` handle. The retained authenticated pipe,
  exact supervisor identity, durable nonce-bound empty witness, clean exit transcript, and atomic
  `trusted-empty` promotion are the cleanup authority; PID ancestry is never used to decide
  membership or successful cleanup.
- **Generation changes preserve evidence** — identity replacement interrupts and retains old active
  tabs. Polling cannot replace those tabs or use a new registry's absence as cleanup proof. Strict
  cleanup scans bounded retired records even in a fresh manager and compacts a generation only after
  every recorded supervisor is witnessed empty and exactly exited.
- **Bounded compatibility** — protocol 1 and pre-bundle protocol 2 remain listable;
  close/reuse/drain requires their manifest and health response to declare the same Job Object
  boundary. Neither can start a new key or authorize fresh-manager cleanup from optimistic legacy
  state.
- **Reconcile ambiguous mutations** — bounded timeouts are necessary, but a canonical-key registry
  read is what prevents an undisclosed or duplicate session after a lost response.
- **No host-crash recovery claim** — losing the durable host closes ttyd's sole WebSocket and ends
  the process; diagnostics report interruption instead of pretending to reattach.
- **Metadata-only observation** — durability evidence is retained without creating a terminal
  transcript or credential-leak surface.
- The terminal remains loopback-only and is not a remote shell service.
- Stock ttyd 1.7.7 has a 256-byte Windows child-command buffer, so child arguments remain
  path-independent and the worktree travels through the environment.
- End-to-end production-safety checks attribute activity through unique fixture commands, owned
  control channels, connections/listeners, and Job Object acknowledgements. Unrelated global
  port-owner or Copilot-process churn is never a cleanup input. The isolated server exports its API
  port through both `TREEMON_PORT` and `TREEMON_PORTS`, preventing inherited shell profiles or
  extensions from falling back to production port 5000.

## Key Files

| File | Purpose |
|---|---|
| `scripts/durable-terminal-host.mjs` | Durable ttyd WebSocket owner, browser proxy, replay, lifecycle, and diagnostics |
| `scripts/terminal-job-supervisor.ps1` | Windows P/Invoke supervisor: STARTUPINFOEX handle-whitelist launch, assign-before-resume Job Object ownership, membership checks, and atomic empty-witness publication |
| `scripts/terminate-owned-process.ps1` | Targeted exact PID/start-identity inspection and isolated-verifier fallback termination; not terminal-session cleanup |
| `scripts/durable-terminal-control.mjs` | Authenticated status and graceful PID-scoped host shutdown |
| `scripts/durable-terminal-observation.mjs` | Detached 24-hour heartbeat/liveness observation and final status evaluation |
| `scripts/verify-durable-terminal-runtime.mjs` | Isolated real-ttyd reconnect and explicit-cleanup verification |
| `scripts/verify-durable-terminal-treemon.mjs` | Browser reload and two-process Treemon restart demonstration |
| `scripts/durable-terminal-verifier-env.mjs` | Pure isolated-server environment construction, including both reporting port variables |
| `scripts/verify-ttyd-runtime.mjs` | Import-safe stock-ttyd verifier using the same Job Object supervisor boundary |
| `src/Server/EmbeddedTerminal.fs` | Runtime-bundle materialization/retention, durable-host discovery, trusted generation/witness recovery, contained compaction, reservation cancellation, and control client |
| `src/Server/Server.fsproj` | Copies the host, Job Object supervisor, process helper, locked `ws` runtime, and checksum-pinned ttyd into build/publish layouts |
| `package.json` / `package-lock.json` | Exact `ws` dependency and committed package-resolution lock |
| `src/Server/Program.fs` | Creates the control client without closing durable sessions on shutdown |
| `src/Client/TerminalPane.fs` | Accessible multi-tab iframe pane pointed at durable proxy endpoints |
| `src/Tests/EmbeddedTerminalTests.fs` | Host generation races, reservation cancellation/locking, discovery, restart, selective close, and API validation |
| `scripts/durable-terminal-host.test.mjs` | Supervisor protocol/policy, cleanup acknowledgement, isolation, replay, and host lifecycle tests |
| `scripts/durable-terminal-observation.test.mjs` | Observation replacement, credential, and PID-reuse ownership tests |
| `scripts/verify-ttyd-runtime.test.mjs` | Verifier Job Object cleanup and failure propagation tests |
| `scripts/durable-terminal-verifier-env.test.mjs` | Isolated reporting-port/config/state environment test |

## Related Specs

- [Durable terminal productization](future/durable-terminal-productization.md) defines packaging,
  machine-global discovery, host generations, reconnect state, and release work required beyond the
  current host architecture.
- [Canvas pane](canvas-pane.md) defines the workspace iframe and layout patterns.
- [Native session management](native-session-management.md) remains authoritative for the external
  Windows Terminal fallback.
- [Process execution](process-execution.md) defines the separate data-capture process boundary.
- [Port management](future/port-management.md) defines non-production port allocation.
