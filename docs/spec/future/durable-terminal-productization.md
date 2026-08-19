# Durable Terminal Productization

Status: **Future work.** The proxy-first prototype has proved browser reconnect and Treemon
redeploy survival with an active Copilot process. The 24-hour upstream-connection observation is
still a release gate.

## Goals

- Ship the durable terminal host as an installed, versioned runtime component rather than a script
  loaded from a source checkout.
- Rediscover the same terminal sessions from any compatible Treemon checkout after restart,
  deploy, or rollback.
- Upgrade the terminal host without terminating sessions owned by an older host generation.
- Restore a coherent bounded terminal screen after reconnect without persisting terminal output.
- Guarantee bounded memory, deterministic process-tree cleanup, and explicit failure reporting.
- Model terminal process health separately from browser attachment and Copilot conversation state.
- Keep the terminal and control surfaces loopback-only, capability-scoped, and safe against
  cross-worktree access.

## Prototype Baseline and Gaps

The current implementation is deliberately checkout-scoped:

- the source runtime entry point is `scripts/durable-terminal-host.mjs`; server builds/publishes copy
  it, `terminal-runtime-lock.ps1`, `terminal-job-supervisor.ps1`,
  `terminate-owned-process.ps1`, the exact locked `ws` runtime, and checksum-pinned ttyd. Staging is
  exact-owner identified and promoted only after complete verification into checkout-local
  `.agents/durable-terminal/terminal-runtime-bundles/<extended-bundle-hash>/`. Treemon hands
  `FileShare.Read` locks to a separately lived owner without a gap; that owner launches Node and
  retains write/delete exclusion over every bundle file. A running protocol-3 host resolves `ws`
  bundle-relatively and spawns only its locked supervisor/helper/ttyd, but the bundle is not yet a
  machine-global installed terminal-host artifact;
- candidate files still resolve from the output/publish copy or source checkout. They are copied
  into the immutable bundle and are never runtime fallbacks, but the staged runtime is not
  independently installable;
- `ws` is exact-pinned by the committed root lockfile; a dedicated terminal-host package boundary
  remains future organization rather than a dependency-integrity gap;
- host discovery lives in the checkout's `.agents/durable-terminal/`;
- reconnect uses a bounded raw-byte ring plus resize rather than serialized terminal state;
- one session-lifetime attachment capability is returned in the iframe endpoint;
- the shared UI lifecycle is `Starting | Running | Failed | Interrupted`, with interrupted tabs
  retained and independently dismissible/restartable across host replacement;
- checkout-local startup is serialized by a state-directory lock and each live host manifest has a
  unique runtime generation, exact host and lock-owner PID/start identities, a previous-server
  compatibility bundle view, additive extended dependency hashes, supervisor protocol generation,
  and capabilities, but discovery is still confined to one checkout rather than a machine-global
  artifact generation;
- every session starts ttyd suspended under a retained Windows Job Object supervisor with
  kill-on-close/no-breakaway policy; explicit close retains a failed session until the supervisor
  exits and supplies an authenticated session-bound empty-job proof. `STARTUPINFOEX` whitelists only
  the child `NUL` standard handle, so ttyd cannot inherit any supervisor control or unrelated
  inheritable handle. Generation manifests start untrusted, are quarantined write-ahead on protocol
  failure, and become cleanup authority only after a nonce-bound helper witness, clean authenticated
  transcript, exact zero supervisor exit, and atomic `trusted-empty` promotion. A witness without
  promotion remains untrusted after host or Treemon-manager crash; delete/archive hold a renewable
  per-worktree host reservation from authoritative cleanup through mutation;
- dead/replaced generation records are retained until every recorded supervisor has a matching
  terminal trusted state, durable witness, and exact exit identity, then compacted by an
  exact-owner claim that commits record deletion before best-effort witness cleanup. Stale current
  claims and bounded legacy host/manager claims recover without stealing a live compactor.
  Referenced runtime bundles remain immutable. Excess unreferenced bundles are atomically renamed
  to exact-owner, phase-leased deletion tombstones that are never restored; unlocked tombstones and
  staging directories are stale even if the former owner process remains alive, and are safely
  removed. The checkout-local
  store refuses a 65th unresolved generation rather than discarding evidence; dead protocol-1 and
  pre-bundle protocol-2 migration records fail strict cleanup closed until the operator
  authoritatively drains their terminals (or restarts the machine) and manually removes those
  records;
- protocol-1 and pre-bundle protocol-2 manifests remain listable; existing-session
  close/reuse/drain is permitted only when they declare the same Job Object ownership capability,
  and neither may accept a new key. Protocol 2 retains its per-key reservation path, while an empty
  legacy host drains before a protocol-3 generation starts;
- deployment preserves a running host and its sessions; a different candidate bundle makes it
  drain-only for new keys and replaces it after it empties. Checkout-local state still cannot run
  multiple live host generations concurrently, so new sessions wait for that bounded drain instead
  of immediately using the new artifact;
- operations are standalone Node scripts rather than `tm` commands.

These choices are accepted for feasibility but are not the target production contract. A following
session should preserve the proven proxy ownership model while replacing these packaging, state,
reconnect, lifecycle, and operations shortcuts.

## Expected Behavior

### Installation and startup

The published Treemon artifact includes the terminal-host JavaScript, its locked production
dependencies, and a manifest describing its protocol and artifact generation. Runtime lookup is
relative to the installed server artifact, never `Directory.GetCurrentDirectory()` or a source
checkout. Setup verifies Node and the checksum-pinned ttyd binary before the embedded-terminal
action becomes available and reports a specific remediation when either is missing.

Treemon starts a host lazily when the first embedded terminal is requested. Concurrent Treemon
processes attempting to start the same generation converge on one host through an exclusive
machine-level generation lock. A stale manifest is reclaimed only after PID identity and control
health prove that its host is gone.

### Machine-global discovery

Host manifests and lifecycle metadata live under the machine-level Treemon state directory
(`~/.treemon` or `$TREEMON_CONFIG_DIR` in an isolated test), not under a worktree checkout. A
Treemon server started from another branch, a newly deployed publish directory, or a rollback build
therefore discovers the same compatible running sessions.

Persisted metadata contains only host/session identity, canonical worktree identity, protocol and
artifact generation, PIDs, ports, timestamps, lifecycle, and close diagnostics. Control tokens are
stored separately with current-user-only access. Terminal output, prompts, environment contents,
and browser attachment capabilities are never persisted.

### Host generations and deployment

Each immutable host artifact has a generation derived from its protocol version and content hash.
A deployment follows these rules:

- Existing host generations and their sessions remain unchanged.
- New sessions use the current deployed generation.
- A compatible Treemon server lists and controls sessions from every live generation.
- A host generation with no sessions drains, exits, and removes its manifest.
- No live ConPTY/ttyd session migrates between host processes.
- A rollback uses the stable control protocol and does not stop newer or older compatible hosts.

A breaking control protocol starts a new generation. The server retains the smallest versioned
client surface required to list and explicitly close older live sessions until they drain; it does
not reinterpret an unknown protocol as an empty registry. The checkout-local implementation's
protocol-1/pre-bundle-protocol-2 parser is the concrete bounded precedent: it starts no new legacy
keys, preserves authenticated list/close/drain, and rejects fresh-manager cleanup from legacy
optimistic state.

The first productized deployment adopts a running, Job-capability-bearing checkout-local
protocol-3 bundled host as a **legacy-draining generation**. It validates and retains that host's
checkout-local immutable bundle while listing and controlling its sessions, but sends every new
terminal to the machine-global current generation. Pre-bundle protocol-2 state remains listable
evidence only and cannot authorize fresh-manager cleanup. Once the compatible legacy host has no
sessions, it exits and its checkout-local state is removed. This is a bounded one-time migration,
not a permanent scan of arbitrary worktrees.

### Terminal reconnect

The host remains ttyd's sole WebSocket client for the full terminal lifetime. Browser frames are
replaceable, single-writer attachments. On attach, the browser receives:

1. cached ttyd title and preferences;
2. a serialized bounded terminal snapshot at output sequence `N`;
3. buffered output after `N`;
4. live output.

The handoff cannot omit or duplicate bytes. The host tracks the last terminal dimensions while
detached and sends a resize after snapshot replay so full-screen applications redraw.

A slow or paused browser never blocks ttyd or grows host memory without limit. Browser output uses
a bounded queue and explicit high/low watermarks; a client that cannot catch up is detached and can
reattach from a fresh snapshot. Host-side parsing likewise owns ttyd PAUSE/RESUME when its bounded
terminal-state pipeline is backpressured.

### Lifecycle and cleanup

Browser close, iframe replacement, dashboard refresh, and Treemon shutdown change attachment state
only. The terminal process ends on explicit terminal close, worktree delete/archive, natural shell
exit, host failure, or deliberate host shutdown.

Closing a live terminal asks for confirmation that PowerShell/Copilot will stop. A confirmed close
removes the session from the host registry only after ttyd and its complete ConPTY process tree have
exited. Cleanup failures remain visible and retryable; they are never returned as an empty,
successful registry.

Host crash closes the sole upstream WebSocket, causing ttyd to terminate its PTY tree. Treemon
reports the session as interrupted and may offer **Resume Copilot** from the exact known Copilot
conversation. It never labels a replacement process as a terminal reconnect.

Machine reboot ends all terminal processes. Stale manifests become interrupted evidence and are
cleaned after process identity checks; live process recovery across reboot is not claimed.

### UI and session identity

The shared terminal model separates:

- terminal process state: `Starting`, `Running`, `Exited`, `Interrupted`, `Failed`, `Closing`;
- attachment state: `Attached`, `Detached`;
- terminal identity: Treemon-generated and stable for one PTY lifetime;
- Copilot identity: optional foreground conversation plus retained resumable conversations.

Tabs show process state independently from attachment state. A detached healthy terminal is not
shown as failed. **Reconnect terminal** reattaches to the same process; **Resume Copilot** starts a
replacement process from conversation history.

The reporting extension includes `TREEMON_TERMINAL_SESSION_ID` with Copilot registration so the
server binds a Copilot conversation to its exact terminal without guessing from worktree path.

### Operations and diagnostics

Terminal administration is exposed through `tm terminal` commands rather than requiring direct
knowledge of host-state files:

- list hosts, generations, sessions, process identities, attachment state, and heartbeat age;
- close one worktree terminal;
- stop an empty host generation;
- deliberately stop all terminal hosts with an explicit destructive confirmation.

Output redacts bearer tokens and attachment capabilities. Lifecycle diagnostics use a versioned,
bounded metadata schema with rotation and retention. They record upstream open/close, close
code/reason, host and process liveness, Treemon discovery, attachment count, replay truncation,
backpressure, and heartbeat age—never terminal content.

## Technical Approach

### Runtime layout and dependency lock

Promote the existing checkout-local content-addressed bundle into a cohesive
`src/TerminalHost/` component with its own minimal package manifest. The current bundle already
contains the exact root-lockfile `ws` runtime, optional-native guards, checksum-pinned ttyd, the
runtime-lock owner, and their licenses. Its stable protocol-3 compatibility fields remain readable
by the immediately previous server while the additive extended identity and long-lived file locks
bind the bytes that execute. Productization moves that closed set into an independently installable
machine-global artifact rather than changing its integrity model. Keep Node plus `ws`; the proxy
has already proved the lifecycle contract, so replacing it with a custom PTY host is not a
productization task. Add and license headless xterm and serialization only when bounded snapshots
are implemented.

Deployment installs the same closed host/lock-owner/supervisor/helper/ttyd/`ws` manifest the
checkout-local manager already verifies into the machine-global immutable artifact store at
`terminal-host/artifacts/<generation>`, with a generation manifest. `treemon.ps1` verifies that
artifact before replacing the web server. The current host keeps its files until its sessions
drain; deployment never mutates or removes an active generation directory.

`Server.EmbeddedTerminal` uses `AppContext.BaseDirectory` only to locate the candidate artifact
shipped with that server, then resolves the installed generation from the machine-global registry.
Environment overrides exist only for isolated tests and diagnostics, with their scope documented.

### Registry and control protocol

Use one authenticated loopback HTTP control endpoint per host generation. Define explicit
versioned DTOs for health, list, start, grant attachment, close, drain, and shutdown. Every request
and response has a byte limit and closed validation; worktree paths are canonicalized and checked
against Treemon's known worktrees before session creation.

The machine-global registry is a set of atomic artifact-generation manifests. Extend the current
state-directory protocol—an OS-held exclusive startup lock, unique runtime generation, and exact
PID/start identity—across installed artifact generations rather than replacing it with PID-only
discovery.

Treemon treats the host's full session snapshot as authoritative. A control failure preserves the
last known tabs as failed/interrupted evidence instead of returning an empty snapshot. Start, close,
and discovery remain idempotent by terminal/worktree identity.

### Attachment capabilities

Keep the control bearer private to the server. Treemon mints a short-lived, session-scoped,
single-use attachment grant. The proxy exchanges it for a session-specific HTTP-only cookie used by
the stock ttyd page and reconnecting WebSocket. Grants cannot attach to another worktree, and a
replacement writable attachment explicitly takes over from the previous one.

Validate exact loopback `Host` and `Origin`, the ttyd subprotocol, frame type, message size, terminal
dimensions, and command kind. Keep ttyd bound to loopback with Origin checking, one-client mode,
and a host-only credential so a guessed ttyd port cannot bypass the proxy.

### Bounded terminal state and flow control

Add `@xterm/headless` plus the compatible serialization addon inside the Node host. Feed every ttyd
output frame through the headless terminal while retaining a bounded output sequence after the
latest snapshot. Bound terminal rows, raw bytes, browser queue bytes, parser work, and diagnostic
events independently.

Pin exact compatible xterm package versions and prove the Copilot alternate-screen TUI against
their serializer before adopting them. If the headless/serialization packages cannot produce a
stable screen under the release matrix, stop for an explicit renderer-state decision rather than
falling back silently to the raw ring.

Snapshot generation and live forwarding use one serialized per-session queue. This makes the
snapshot-at-`N` cutover atomic without locking shared mutable collections. Browser PAUSE/RESUME
controls only that attachment; host PAUSE/RESUME controls ttyd based on the headless/parser queue.

Binary transfer modes and terminal features that cannot be represented safely by serialized replay
must be either verified end to end or disabled explicitly. They must not silently corrupt the
reconnect screen or bypass memory limits.

### Process ownership

Preserve the current two-layer boundary. ttyd `--once` remains the graceful path when the host
closes its upstream WebSocket, while a per-session supervisor is the ownership authority: it creates
ttyd suspended, assigns it to a kill-on-close/no-breakaway Windows Job Object before first resume,
retains the job handle, and launches through `STARTUPINFOEX` with a one-handle
`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`; clearing inheritance on authenticated stdin/stdout/stderr
remains defense in depth. It validates shell PID membership and atomically flushes a
generation/worktree/session/nonce/exact-identity empty witness only after `ActiveProcesses == 0`,
before reporting success or exit. Cleanup requires that witness, a clean authenticated transcript,
the exact zero supervisor exit, and atomic terminal `trusted-empty` promotion. Sticky protocol
failure is durably quarantined before later output and cannot be repaired. Host/control-pipe loss drives supervisor
cleanup, and supervisor failure closes the job handle. Productization must package the generation
record, witness store, bounded compaction, and launch boundary with the immutable host artifact
rather than reverting to descendant enumeration or PID termination.

The host prepares ports, public listener, generation/session metadata, witness paths, pinned ttyd
path, and the complete authenticated payload before spawning that supervisor. Spawn and start are
one transition; an authenticated `startup-failed` message is also valid as the first message so a
post-spawn transport failure can prove the untouched boundary empty. This ordering and the
long-lived runtime file locks are part of the ownership boundary and move with the artifact.

Track host, supervisor, ttyd, and PowerShell identity from owned startup metadata for diagnostics,
but never discover or kill by process name and never use PID ancestry as cleanup proof. Add
isolated fault-injection tests for host kill, supervisor kill, ttyd kill, control timeout, and close
during sustained output. A surviving or unacknowledged job keeps the session failed and retryable.

### Copilot binding and recovery

Extend reporting/session activity with an optional terminal session ID sourced from
`TREEMON_TERMINAL_SESSION_ID`. The host/session projection retains the exact last live Copilot
conversation ID. On interruption, the UI can start a new terminal with
`copilot --resume <conversation-id>` after explicit user action; automatic resume and automatic
retry of in-flight tools remain forbidden.

## Decisions

- **Continue the ttyd proxy.** Browser and Treemon restart survival are proven. Reconsider
  `node-pty`/custom ConPTY only if the long observation fails for an inherent proxy reason, bounded
  headless replay cannot make reconnect usable, or deterministic cleanup cannot be achieved.
- **Machine-global state over checkout-local state.** Deployment and rollback must discover live
  sessions regardless of which worktree supplied the web server.
- **Immutable draining generations over live host replacement.** Running ConPTY cannot migrate
  safely between processes; old sessions drain while new sessions use new code.
- **Headless terminal state over a raw byte ring.** Raw truncation can begin inside an escape
  sequence or omit earlier state and is not a stable reconnect contract.
- **One writable attachment.** Multi-writer terminal input is undefined and unnecessary for the
  product.
- **Explicit destructive close.** Closing the terminal stops Copilot; hiding or detaching does not.
- **No terminal content on disk.** Conversation storage belongs to Copilot, not Treemon terminal
  diagnostics.
- **Preserve Job Object ownership.** The checkout-local runtime already requires assign-before-resume
  kernel ownership, a STARTUPINFOEX handle whitelist, and durable generation-scoped empty witnesses;
  productization changes their packaging and generation scope, not those guarantees.

## Implementation Sequence

1. **Install the frozen protocol-3 runtime globally.** Promote the current self-contained bundle
   manifest and publish staging into the machine-global artifact store, resolve paths from the
   installed artifact, and document prerequisites.
2. **Introduce machine-global discovery and generations.** Add atomic manifests, locking, stale
   identity checks, the bounded legacy-host adoption, multi-generation listing,
   current-generation routing, drain, and rollback compatibility.
3. **Implement bounded terminal snapshots and flow control.** Add headless xterm state,
   snapshot/sequence cutover, upstream and browser watermarks, and truncation behavior.
4. **Harden security and process lifecycle.** Package the Job Object supervisor with each immutable
   host generation, add short-lived grants, host-only ttyd auth, control bounds, and fault-injection
   cleanup tests.
5. **Expand shared state and UI.** Separate process/attachment state, add destructive-close
   confirmation, interruption/recovery UX, and truthful reconnect wording.
6. **Bind Copilot identity.** Carry terminal session identity through reporting and implement
   explicit exact-session resume after interruption.
7. **Productize operations and verification.** Add `tm terminal` commands, release diagnostics,
   multi-terminal/security/backpressure tests, deploy/rollback tests, and the release soak.

## Soak and Evidence Policy

The running checkout-local observation is a **prototype gate**. Leave its host and session
untouched until at least 24 elapsed hours have passed or the observer reports failure. Normal
Treemon deployment and production-terminal use are independent because they use separate host-state
directories.

The prototype observation passes only when all of these remain true at or after its due time:

- host, ttyd, and PowerShell identities match the start record;
- the original upstream-open timestamp is unchanged;
- the upstream WebSocket has no close event;
- protocol ping/pong remains fresh;
- lifecycle diagnostics stay within their configured bound and contain metadata only.

On pass, capture the final `observation.json` and bounded diagnostics, update the prototype status
record, then stop the exact recorded host through the observation controller. On failure, capture
status before stopping anything, preserve close code/reason and process outcomes, and treat the
product release as blocked until the failure is classified. Do not restart the observation in
place or overwrite its evidence.

This prototype pass does not certify later productization changes. Start a new 24-hour observation
after packaging/global-registry work, after control-protocol or replay/backpressure changes, and for
the final release candidate. The release-candidate soak must include an active Copilot process plus
at least one browser detach/reattach and one Treemon deploy while retaining the same terminal
process identity.

## Release Gates

- The staged production artifact starts without any file from the source checkout.
- A terminal opened from one checkout is rediscovered after deployment from another checkout.
- Browser reload, iframe replacement, Treemon restart, deploy, and rollback preserve the same ttyd,
  PowerShell, and Copilot processes.
- New sessions use the new host generation while old sessions remain on and drain from the old one.
- Explicit close, worktree delete/archive, host crash, ttyd crash, and deliberate host shutdown
  leave no owned descendant process.
- Reconnect restores a coherent screen without gaps or duplicate output, including alternate-screen
  applications and output produced while detached.
- Sustained output and a stalled browser keep host memory within measured bounds and do not block
  Copilot.
- Multiple worktrees remain isolated by control identity and attachment capability.
- Tokens, worktree paths, terminal bytes, prompts, and environment contents do not enter public
  logs or diagnostics.
- Security tests cover forged control calls, capability reuse, cross-worktree attachment, invalid
  Origin/Host, oversized frames, and stale manifests.
- Copilot approvals, tool execution, ask-user waits, slash-command pickers, model selection,
  alternate-screen redraw, Ctrl+C/Escape, resize, Unicode/IME, selection, and clipboard remain
  usable before and after reattach.
- An isolated RDP disconnect/reconnect and browser tab discard preserve the same process identity;
  verification uses non-production ports and never touches the production instance.
- Unit, fast, real-ttyd, browser, deploy/rollback, and cleanup suites pass on isolated dynamic ports
  without touching production.
- The same upstream WebSocket and process identity remain healthy for at least 24 elapsed hours, or
  the release remains blocked with captured failure metadata.

## Non-Goals

- Preserving a running terminal across machine reboot or terminal-host process crash.
- Remote terminal access, multiple writable clients, elevation, tabs/splits inside one terminal,
  or exact Windows Terminal application parity.
- Migrating a live ConPTY session between host generations.
- Persisting terminal output as a transcript or deriving Copilot state by scraping terminal bytes.

## Related Specs

- `docs/spec/embedded-terminal.md` — current proxy prototype behavior.
- `docs/spec/native-session-management.md` — external Windows Terminal fallback.
- `docs/spec/process-execution.md` — process-launch and argument-safety boundaries.
- `docs/spec/future/port-management.md` — dynamic non-production port policy.
