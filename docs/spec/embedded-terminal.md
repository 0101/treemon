# Embedded Terminal

## Goals

- Provide one writable embedded PowerShell terminal per canonical worktree path.
- Keep terminals alive across browser attachment changes and ordinary Treemon server restarts.
- Run the terminal on one small, separately running F#/.NET `TerminalHost` executable with no Node
  or PowerShell productization stack. The whole terminal runtime (`src/TerminalHost`,
  `src/TerminalHostLayout`, `src/Server/TerminalHost*.fs`,
  `src/Server/TerminalSessionActivity.fs`, `src/Server/EmbeddedTerminal.fs`, and any terminal-specific
  runtime script) stays at or below 4,000 nonblank production lines.
- Give every terminal an exact kernel-owned process boundary established before ttyd executes.
- Keep lifecycle control loopback-only, authenticated, versioned, and limited to the five endpoints
  listed below.
- Preserve terminal tabs across a host update by resuming only the Copilot session owned by each
  exact terminal.
- Apply host updates at naturally idle Copilot boundaries without draining work, blocking new work,
  or treating unrelated shell activity as a gate.
- Keep development and verification isolated from production state, ports, and processes.

## Expected Behavior

### Terminal lifetime and attachments

Opening a terminal starts one for the canonical worktree path or reuses the existing one. The
`TerminalHost` runs independently from the Treemon server, so a compatible server restart or deploy
rediscovers the same host, ttyd processes, and terminal tabs instead of replacing them. Server
shutdown alone never closes the host.

The host owns one ttyd process tree per worktree. It creates ttyd suspended, assigns it to an
in-process Windows Job Object configured to kill its members when the owning handle closes, and only
then resumes ttyd. Process ownership comes only from that Job Object and retained exact handles;
process names and ancestry are never discovery or cleanup authority.

For each terminal, the host is ttyd's sole upstream WebSocket client for the terminal lifetime. It
continuously drains ttyd into a small bounded raw replay buffer and accepts one replaceable browser
attachment. Replacing or losing the browser attachment does not replace the shell. A new attachment
receives the bounded replay and a resize so full-screen applications can redraw. Attachment routing
is data-plane behavior, not an additional lifecycle state. Output older than the buffer, terminal
scrollback, and browser-rendered state are not durable.

The client keeps running terminal iframes mounted while tabs are hidden, preserving normal tab
switching behavior. Tab order is opening order, tab labels are worktree display names, and selection
remains client state.

### Control and discovery

The host exposes a stable authenticated loopback control API under an explicit version. Its complete
lifecycle surface is:

- health and version;
- authoritative terminal list;
- start or reuse by canonical worktree path;
- close one terminal; and
- shutdown, used only for committed host replacement or an explicit administrative request.

Start, close, and list return or reconcile against the authoritative registry rather than asking the
server to merge lifecycle fragments. Browser attachment endpoints ride terminal data returned by
that registry; there is no separate attach/detach lifecycle API.

A machine-level discovery manifest contains only the exact host identity (PID and process start
identity), loopback endpoint and bearer token, host version, control API version, and the version of
any staged executable. Terminal registry state is read from the live host, not copied into the
manifest. The server rejects malformed or stale identities, non-loopback endpoints, unknown or
unsafe worktree paths, invalid attachment endpoints, and unexpected browser origins.

### Exact Copilot ownership and idle

Each started terminal receives a stable `TREEMON_TERMINAL_SESSION_ID` in its environment. A Copilot
session launched inside that terminal inherits the value, and the passive reporting extension sends
it as the optional `TerminalSessionId` origin on activity reports. Session activity persists that
origin, allowing Treemon to join a Copilot `SessionId` to one host-owned terminal without guessing
from worktree path.

Only Copilot sessions whose `TerminalSessionId` appears in the current authoritative host registry
participate in host-update gating. A session is non-idle when its existing `SessionActivity`
effective per-session state is `Working` or `WaitingForUser`. `Idle`, closed, missing, and
non-Copilot sessions do not gate. An unrelated Copilot session in the same worktree does not gate
unless it carries that terminal's exact origin.

`WaitingForUser` remains non-idle without a timeout. There is no forced replacement or operator
override that discards a waiting Copilot session.

Arbitrary shell commands, child or background jobs, terminal output, browser attachment state, and
other non-Copilot activity never gate an update. Treemon neither inspects nor warns about foreground
shell work before replacement, and does not wait for every session associated with a worktree.

### Opportunistic host updates

A newly published host executable is staged in a simple versioned directory while the current host
and terminals continue normally. Treemon does not mark the old host drain-only and does not refuse,
queue, delay, or proactively block new terminals, prompts, or Copilot sessions.

Whenever all currently owned Copilot sessions are naturally idle, Treemon captures the authoritative
host registry revision and the owned-session activity epoch, then immediately rechecks both. It
commits replacement only when the registry and activity are unchanged and no owned session is
non-idle. If a terminal or activity report wins the race before commit, the attempt is abandoned
without side effects and waits for the next natural idle window. Once commit begins, the brief
terminal outage is the replacement itself; no pre-commit drain period exists.

A failed staged version is suppressed for one minute to avoid a hot replacement loop, then becomes
eligible for retry. A timed-out mailbox reply is inconclusive because the commit may still finish;
the next poll rediscovers authoritative host state instead of suppressing that staged version.
Replacement I/O runs outside the lifecycle mailbox after the mailbox enters a replacing phase.
During that phase registry reads return the last authoritative snapshot without contacting a host
between generations, while start and close requests fail immediately with a retryable error rather
than waiting or remaining queued after their caller has gone away.

Before committing replacement, Treemon captures for every terminal the latest open or resumable
Copilot `SessionId` whose stored `TerminalSessionId` exactly matches that terminal. It then shuts
down the old host, starts the staged host, and recreates terminals in their worktree directories.
For a terminal with a resumable session, Treemon uses the existing provider-specific
`CodingToolCli` resume command. A terminal without one restarts as a plain PowerShell shell.

Stopping the old host may discard arbitrary shell state, running commands, raw replay, and
scrollback. Existing terminal-pane behavior preserves tab labels, order, and selection where
possible; process state and scrollback do not survive host replacement.

A compatible Treemon deployment reconnects to the running host. When it carries a newer compatible
host executable, that executable is staged and replaced only through the opportunistic flow above.
An incompatible control-API deployment is blocked while the old host has terminals. The system does
not run concurrent host generations or build a multi-protocol bridge, so the control API remains
deliberately stable.

### Worktree lifecycle and failure

Deleting or archiving a worktree first closes that exact worktree's terminal through the
authoritative host API and proceeds only after successful cleanup. Other worktrees are unaffected.
The lifecycle mailbox holds a short-lived in-memory reservation for the canonical worktree path
from before terminal close through the delete/archive mutation. Another cleanup or terminal start
for that path receives a retryable busy error, while unrelated worktrees remain available; the
reservation is released after both successful and failed mutations.
An attempt made during committed host replacement fails without mutating the worktree or archive
state; the client reconciles from the authoritative worktree snapshot and leaves the action
available to retry after replacement.

If the host crashes, closing its Job Object handles kills every owned ttyd tree. Treemon keeps the
affected tabs visible as interrupted, reports the loss, and can start fresh terminals. It does not
claim cross-host process recovery or accept absence in a replacement registry as proof that an old
process survived or was recovered.

### Production safety

Development and tests use isolated dynamic ports, isolated temporary state, and fixture worktrees.
They never bind production port 5000, read or mutate production terminal state, or stop production
Treemon, and they never disturb another worktree's running Treemon or long-running reliability
process. Cleanup targets only fixture-owned exact PIDs and process-start identities; it never kills
by name or broad ancestry.

## Technical Approach

### Terminal host

`src/TerminalHost` is a small F#/.NET executable published with Treemon but launched as an
independent process. Its in-memory registry is keyed by canonical worktree path and owns the terminal
session ID, ttyd Job Object and process handles, sole upstream WebSocket, one browser attachment, and
bounded replay bytes.

`TerminalDataPlane` owns only the replay and attachment mailbox, with `createCore` as its focused
state-machine seam. `TerminalProxy` owns the ttyd/browser WebSocket pumps, HTTP forwarding, and
attachment endpoint. It and `ControlApi` use one `LoopbackHost` bootstrap for the shared Kestrel
loopback binding, request-size limit, server-header policy, and dynamic-port discovery.

Windows process creation uses `CREATE_SUSPENDED`, immediate `AssignProcessToJobObject`, and
`ResumeThread` in the host process. The Job Object uses kill-on-close without a breakaway policy, so
host loss and explicit close have the same exact ownership boundary. No supervisor script or
descendant enumeration participates.

PowerShell explicitly sets its location from `TREEMON_TERMINAL_WORKTREE` at startup because ttyd's
Windows working-directory option alone does not establish the child shell's location.

The host serves the small control API and the terminal attachment proxy on authenticated loopback
endpoints. Control DTOs and limits are versioned. Path canonicalization, known-worktree validation,
endpoint validation, request-size bounds, bearer authentication, and exact `Host`/`Origin` checks
occur before lifecycle or terminal input is accepted.

Control API version 1 is exactly:

- `GET /api/v1/health`;
- `GET /api/v1/terminals`;
- `POST /api/v1/terminals` with the sole JSON field `worktreePath`;
- `DELETE /api/v1/terminals/{sessionId}`; and
- `POST /api/v1/shutdown`.

Health returns the host PID, process-start ticks, host version, and control API version. List, start,
and close return the same authoritative `{ revision, terminals }` snapshot, where each terminal has
only its stable `sessionId`, canonical `worktreePath`, and live `attachmentEndpoint`. A path is a
known worktree only when it is an existing, fully-qualified directory with a `.git` marker and
`git rev-parse --show-toplevel` resolves to that exact canonical directory.

The machine discovery file is `%LOCALAPPDATA%\Treemon\TerminalHost\host.json` by default (tests and
isolated hosts override the state directory). Its exact fields are `pid`,
`processStartTimeUtcTicks`, `endpoint`, `bearerToken`, `hostVersion`, `controlApiVersion`, and the
optional `stagedExecutableVersion`. Plain staged executables live at
`<state>\staged\<version>\TerminalHost.exe`; the valid direct version directory with the newest
last-write time and every required bundle member is reported, and the running host refreshes the
manifest when staging changes. `TerminalHostLayout` is the single authority for the default state
directory, manifest and staging paths, executable names, direct-version grammar, and required
bundle members. Server and host reference that contract directly; deployment PowerShell consumes
the candidate server's serialized layout rather than reconstructing it. Treemon publish output
carries the independent host under `terminal-host\`.

The replay buffer is raw and capped at 1 MiB in memory. Terminal bytes, prompts, environment
contents, and attachment credentials are never persisted or written to diagnostics. The control
bearer exists only in host memory, the required discovery manifest, and live attachment URLs
returned from the registry; it is never copied into durable state or written to diagnostics.

Each terminal record carries an `attachmentEndpoint` on a dedicated dynamic loopback port. Its path
contains the terminal session ID and the existing host bearer, so ttyd's relative HTTP `/token` and
WebSocket `/ws` requests remain authenticated without creating a browser cookie or another
credential. The host validates the prefix, strips it before proxying to ttyd, and applies the same
loopback, exact Host/Origin, bearer, and request-size checks as the control API.

### Treemon integration

The server terminal runtime has one-way module boundaries: `TerminalHostProcess` owns process
configuration, launch, and exact identity defaults; `TerminalHostEndpoint` owns the common
loopback-HTTP endpoint shape; `TerminalHostManifest` validates discovery; `TerminalHostClient` owns
authenticated control and attachment requests; `TerminalHostReplacement` coordinates replacement;
and `TerminalSessionActivity` derives the exact owned-session replacement policy from raw activity
facts. `Server.EmbeddedTerminal` retains only the mailbox, authoritative snapshot
reconciliation, and public terminal lifecycle surface. It lazily starts a host only when none is
healthy, and ambiguous start or close responses are resolved by listing the registry again.
Its cleanup bracket records only exact canonical paths and opaque operation tokens in mailbox state;
the delete/archive operation runs outside the mailbox so unrelated paths remain concurrent, and a
`finally` release prevents failed or cancelled mutations from leaving a path busy.
The mailbox grants one replacement phase, keeps serving cached reads and bounded rejection replies
while replacement runs asynchronously, then alone applies the replacement's registry transition.
Development startup passes its actual Vite port through `--dashboard-port`; `Program` expands that
port into the loopback dashboard origins supplied to `EmbeddedTerminal`. Production omits the
option and allows only the configured server origin aliases, so the terminal client never infers a
dashboard port from a server-port convention.

`treemon.ps1` publishes the host and stages a changed executable in a plain versioned directory. It
preflights control-API compatibility before replacing the Treemon server; a deployment that cannot
control a live host is refused while terminals remain. When an exact incompatible host proves its
authoritative registry is empty, the candidate shuts it down and confirms that exact process
identity exited before allowing deployment. Server, frontend, and host candidates are built outside
their active destinations, and the candidate Treemon's own compiled control client probes the exact
live host before any active server files or processes are replaced. The staged directory carries the
complete framework-dependent host publication alongside `TerminalHost.exe`.
Replacement always derives `ttyd.exe` from the exact host executable being launched: a staged host
uses its staged sibling and rollback uses the old host's sibling. A configured path from another
bundle generation can never override that pairing.
Lazy host startup accepts only the explicit `TREEMON_TERMINAL_HOST_EXECUTABLE` deployment input or
the `terminal-host` directory beside the published Treemon executable. Development startup sets the
explicit input to its local TerminalHost build; shipped server code never probes source-tree
Debug/Release output, and a missing published host fails at the deployment path without another
fallback.

The reporting extension reads `TREEMON_TERMINAL_SESSION_ID` and adds it as optional origin metadata.
`SessionActivityService` and `SessionActivityStore` retain that value without changing status
folding, representative selection, liveness, or worktree projection. The activity mailbox maintains
a bounded live-status cache and a process-local monotonic counter per terminal origin. Its narrow
terminal query filters the live cache to the caller's complete authoritative terminal-ID set before
overlaying indexed durable rows, and returns only those raw rows plus their maximum epoch.
`TerminalSessionActivity` owns the terminal-specific projection and returns an opaque replacement
policy: wait, or proceed with the epoch and optional shell command keyed by exact terminal session
ID. It owns provider selection and `CodingToolCli` command construction; terminal replacement only
rechecks the epoch, recreates terminals, and delivers supplied commands. Hourly retention prunes
live status and origin epochs, retaining epochs only for durable origins or the latest authoritative
host registry; observing a registry immediately discards epochs for terminals no longer in it while
the global counter remains monotonic.
Once a session has an exact terminal origin, a later report that omits the optional origin retains
the known value in memory and durable storage; there is no implicit clear operation.
Activity ingestion accepts a Copilot `SessionId` only when it is 1–128 ASCII characters from
`[A-Za-z0-9._:-]`, so the persisted resume identity is bounded and cannot carry terminal control
input.

Replacement snapshots terminal presentation and exact resumable Copilot ownership before stopping
the old host. `TerminalSessionActivity` uses `CodingToolCli` to prepare provider-specific commands;
terminal replacement delivers those opaque commands while preserving the same terminal-pane
ordering and selection model already used during normal polling.

### Deliberate simplicity

There is one host and one current registry. The design has no generation journals, empty witnesses,
content-addressed bundles, runtime-lock process, tombstones, leases, concurrent host generations,
legacy protocol migration, or live process-state migration. It does not retain a Node runtime,
PowerShell lifecycle helpers, or compatibility shims.

## Decisions

- **One separately running F# host:** ordinary Treemon restarts remain control-plane events while
  the implementation has one language, one process owner, and no script/runtime handoff.
- **Job Object before execution:** kernel membership established before ttyd resumes is the only
  terminal-tree ownership authority.
- **One upstream and one browser writer:** the host preserves the shell across browser reconnects
  without defining multi-writer input semantics.
- **Separate state from proxy hosting:** the replay/attachment mailbox remains independently
  testable while HTTP/WebSocket hosting shares one loopback-only Kestrel bootstrap with the control
  API, preventing security-sensitive host configuration from drifting.
- **Raw bounded replay:** reconnect gets useful recent output without persisting terminal content or
  introducing a terminal-state serializer.
- **Bearer in the in-memory attachment URL:** a path-scoped copy of the existing host bearer lets
  ttyd's unmodified iframe client authenticate every relative HTTP and WebSocket request without a
  persistent cookie or a second capability.
- **Exact session-origin gating:** only Copilot activity attributed to a current terminal can delay
  replacement; worktree co-location and non-Copilot process activity are irrelevant.
- **Bounded ownership-query state:** terminal replacement consumes a focused projection over only
  current authoritative terminal IDs. Live status follows the existing idle-window bound,
  per-origin epochs are pruned by durable retention and current registry membership without
  resetting the global sequence, and SQLite uses the terminal-origin/activity-order index.
- **Opportunistic replacement, not draining:** normal work is never rejected in anticipation of an
  update. A race cancels the attempt rather than delaying the work.
- **No replacement escape hatches:** `WaitingForUser` gates indefinitely, while non-Copilot work is
  deliberately ignored and may be terminated without warning once the Copilot gate is idle.
- **Stable API over compatibility layers:** compatible servers reconnect; an incompatible deploy
  waits until no terminals exist instead of carrying old protocol clients or migrating live state.
- **Explicit versioned wire contracts:** the candidate deployment preflight is a parsed server run
  mode with a named JSON result, and the host maps registry domain records to dedicated control API
  v1 response property sets. Exact-property regression tests prevent internal fields from leaking
  onto either wire contract.
- **Truthful failure:** host loss kills owned trees and becomes an interruption, never a claimed
  reconnect to an unproven process.
- **Fail closed on unverified live discovery:** a malformed manifest or an exact live process that
  fails health validation blocks lifecycle requests rather than starting a competing host. Lazy
  startup is limited to a missing manifest with no previously verified live identity, or a
  manifest whose exact process identity is dead.
- **Exact Git top-level validation:** the bearer authorizes a lifecycle request, but it does not turn
  an arbitrary directory into a known worktree; the requested canonical path must be Git's exact
  top-level path before the registry is touched.
- **Plain machine discovery and staging:** one bounded manifest and direct version directories are
  sufficient. Registry state stays in the live host, and no generation journal or bundle store is
  recreated. One compiled layout contract defines the state and staging paths, accepted version
  names, executable names, and complete bundle membership for the host, server, and deployment
  script.
- **Candidate-first deployment:** publish and preflight in inactive directories, then atomically
  stage the complete host publication before swapping server files. Treemon receives that stable
  staged executable as its lazy-start path; if an exact live host still runs from an older publish
  directory, those files are preserved until that process exits rather than overwritten in place.
- **Deployment-owned executable selection:** production resolves only an explicit deployment input
  or the published `terminal-host` layout. The development script supplies its source build path
  explicitly, so the shipped assembly contains no build-machine checkout fallback.
- **Startup-owned dashboard topology:** development passes the Vite port that it actually launches
  into `Program`, which derives both loopback dashboard origins. The terminal client has no
  dev-port convention, and production supplies no additional dashboard origin.
- **Resume without widening control API:** after each replacement terminal is recreated, Treemon
  briefly attaches through the existing authenticated ttyd protocol and submits the opaque command
  selected by `TerminalSessionActivity`. A terminal without an exact resumable session receives no
  input and remains a plain PowerShell shell. Submitted terminal input is a shell boundary: a
  command carrying a control character is rejected rather than written, so a stored Copilot
  `SessionId` can never inject an extra command line into a recreated shell.
- **Truthful lifecycle state on failure:** only evidence that the exact host was lost interrupts
  every tab. A rejected single-terminal request keeps the authoritative registry and leaves other
  terminals running, and a replacement that stops short of a proven live host never reports its
  terminals as running.
- **Executable-path replacement identity:** Treemon captures the exact running executable path
  before commit, waits for that exact process identity to exit, and verifies the replacement is
  running from the selected direct staging directory. The captured path is also the rollback target
  when the staged process cannot be launched.
- **One-way server terminal modules:** process/configuration, manifest, control client, replacement,
  focused session-policy projection, and mailbox form an acyclic dependency graph. Replacement
  returns a commit transition for the mailbox to apply, so only `EmbeddedTerminal` reconciles
  `ManagerState` and no module cycle is required.
- **Non-blocking replacement phase:** the mailbox grants the commit boundary but does not perform
  replacement I/O inline. Reads use its last authoritative snapshot until completion, mutations
  receive an immediate retryable error, and only the mailbox applies the final transition. This
  avoids timeout mismatches and stale lifecycle requests without allowing a registry race during
  replacement.
- **Exact in-memory cleanup exclusion:** delete/archive uses a mailbox-owned canonical-path
  reservation around strict terminal close plus mutation. Same-path lifecycle mutations fail
  retryably until a `finally` release, while unrelated worktrees stay concurrent; no persistent
  lease, supervisor, or cross-process cleanup protocol is required.

## Key Files

| File | Purpose |
|---|---|
| `src/TerminalHostLayout/Layout.fs` | Shared state/staging paths, version-directory grammar, executable names, and required host bundle members |
| `src/TerminalHost/TerminalHost.fsproj` and `src/TerminalHost/*.fs` | F#/.NET host project: Job Object launch, ttyd ownership, proxy, replay, registry, and control API |
| `src/Server/TerminalHostProcess.fs`, `TerminalHostEndpoint.fs`, `TerminalHostManifest.fs`, `TerminalHostClient.fs`, and `TerminalHostReplacement.fs` | Host process/identity, shared loopback endpoint shape, discovery validation, authenticated control client and compatibility preflight, and replacement coordination |
| `src/Server/EmbeddedTerminal.fs` | Terminal lifecycle mailbox, authoritative snapshot reconciliation, and public start/get/close surface |
| `src/Server/SessionActivity.fs` | Effective per-session state used by the idle gate |
| `src/Server/SessionActivityService.fs` | Activity ingestion, terminal-origin validation, bounded live state, pruned raw origin epochs, and mailbox-serialized terminal activity queries |
| `src/Server/TerminalSessionActivity.fs` | Exact owned-session projection, idle gate, and opaque resume policy |
| `src/Server/SessionActivityStore.fs` | Durable Copilot session state, optional terminal origin, and indexed exact-origin queries |
| `src/Extension/reporting/extension.mjs` | Passive activity reports sourced from `TREEMON_TERMINAL_SESSION_ID` |
| `src/Server/CodingToolCli.fs` | Provider-specific exact-session resume command construction |
| `src/Server/Program.fs` | Host client and replacement-loop lifecycle without terminal shutdown on server stop |
| `treemon.ps1` | Published host staging and deployment compatibility preflight |
| `src/Client/TerminalPane.fs` | Terminal tabs, mounted iframes, labels, order, selection, and interruption UI |
| `src/Tests/EmbeddedTerminalTests.fs` | Isolated host, replacement race, opaque command delivery, plain-shell restart, crash, security, and cleanup coverage |
| `src/Tests/SessionActivityServiceTests.fs` | Exact terminal ownership, idle policy, and provider-specific resume-plan coverage |
| `scripts/treemon-deployment.test.ps1` | Isolated staging, compatibility-preflight, and candidate-first deployment ordering coverage |

## Related Specs

- `docs/spec/session-status-push.md` — authoritative per-session Copilot activity and terminal-origin
  reporting.
- `docs/spec/native-session-management.md` — external Windows Terminal fallback.
- `docs/spec/worktree-monitor.md` — worktree lifecycle and dashboard integration.
