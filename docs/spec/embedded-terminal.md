# Embedded Terminal

## Goals

- Allow each canonical worktree path to own multiple independently selectable and closable writable
  PowerShell terminals.
- Keep terminals alive across browser attachment changes and ordinary Treemon server restarts.
- Run the terminal on one small, separately running F#/.NET `TerminalHost` executable with no Node
  or PowerShell productization stack. The whole terminal runtime (`src/TerminalHost`,
  `src/TerminalHostLayout`, `src/Server/TerminalHost*.fs`,
  `src/Server/TerminalSessionActivity.fs`, `src/Server/EmbeddedTerminal.fs`, and any terminal-specific
  runtime script) stays at or below 4,000 nonblank production lines. Product-level launch policy
  (`TerminalLaunch.fs`, `SessionManager.fs`, `WorktreeApi.fs`) routes to that runtime and is outside
  both it and the budget.
- Give every terminal an exact kernel-owned process boundary established before ttyd executes.
- Keep lifecycle control loopback-only, authenticated, versioned, and limited to the five endpoints
  listed below.
- Preserve terminal tabs across a host update by resuming only the Copilot session owned by each
  exact terminal.
- Route every prompted or automatic agent launch through the embedded host while retaining Windows
  Terminal only for the card's explicit `>` / Enter and tracked-window `+` actions.
- Make server-created terminals discoverable from an initially empty browser snapshot without
  stealing dashboard focus.
- Apply host updates at naturally idle Copilot boundaries without draining work, blocking new work,
  or treating unrelated shell activity as a gate.
- Keep development and verification isolated from production state, ports, and processes.

## Expected Behavior

### Terminal lifetime and attachments

Opening a terminal always starts a new terminal for the canonical worktree path. Existing terminals
remain available until explicitly closed. The
`TerminalHost` runs independently from the Treemon server, so a compatible server restart or deploy
rediscovers the same host, ttyd processes, and terminal tabs instead of replacing them. Server
shutdown alone never closes the host.

The host owns one ttyd process tree per terminal, and a worktree may own several terminals. It
creates each ttyd suspended, assigns it to an
in-process Windows Job Object configured to kill its members when the owning handle closes, and only
then resumes ttyd. Process ownership comes only from that Job Object and retained exact handles;
process names and ancestry are never discovery or cleanup authority.
The registry mailbox is the sole owner that closes retained process and Job Object handles.
An upstream exit posts its exact terminal session ID back to that mailbox, so pruning, explicit
close, shutdown, and stale upstream notices are serialized and cannot close one handle concurrently.

For each terminal, the host is ttyd's sole upstream WebSocket client for the terminal lifetime. It
continuously drains ttyd into a small bounded raw replay buffer and accepts one replaceable browser
attachment. Replacing or losing the browser attachment does not replace the shell. A new attachment
receives the bounded replay and a resize so full-screen applications can redraw. Attachment routing
is data-plane behavior, not an additional lifecycle state. Output older than the buffer, terminal
scrollback, and browser-rendered state are not durable.
The host does not publish a terminal as started until that upstream has delivered its first terminal
output frame within the terminal startup timeout; a bound ttyd TCP port alone is not evidence that
PowerShell is ready for input. Upstream output is streamed into protocol-valid chunks, so the
1 MiB replay capacity is only a retention bound: one larger WebSocket message evicts old replay
rather than ending the upstream and terminal.
If a paused attachment falls behind the replay window, resume resets and clears the emulator, shows
a visible omission notice, and then sends the surviving frames instead of silently splicing
discontinuous output into the existing state.

The terminal pane normally follows the currently focused worktree card. Clicking a card's embedded
terminal action explicitly targets that worktree without changing dashboard focus or the Canvas
pane; the next card selection restores normal focus-following. Its tab strip shows only the targeted
worktree's terminals and labels each one with the freshest display-safe activity from that exact
terminal's representative live Copilot session: reported `assistant.intent` or session title.
Until either exists, the label falls back to `Terminal 1`, `Terminal 2`, and so on in opening order.
It remembers the selected terminal independently for each worktree. **New** starts another terminal
for the targeted worktree; the empty state offers **Start terminal**. Switching worktrees hides the
other worktrees' tabs without closing their terminals, and running iframes stay mounted so their
browser state survives. Closing the last visible tab leaves the pane open in its empty state; only
the persistent top-bar **Terminal** control hides or shows the pane, using the same active treatment
as the **Canvas** control.

### Launch routing and command startup

The card's `>` / Enter action remains the explicit native Windows Terminal choice, and its `+`
action opens another tab in that tracked native window. The dedicated embedded-terminal action and
the terminal pane's **New** action continue to start plain embedded PowerShell terminals.

Every agent-bearing process launch uses an embedded terminal: Resume, contextual card actions,
explicit Canvas session launch, create-worktree prompt launch, AutoSync fallback, queued Canvas
fallback, and `tm launch`. A browser need not be open for a CLI or background launch; the host owns
the terminal until a dashboard attaches later.

Direct dashboard actions that start an agent open and target the terminal pane, selecting the exact
returned terminal. Resume first joins its durable target session ID to the authoritative running
terminal snapshot; when that exact session is already live, it returns the existing terminal and
starts no second Copilot process. Other terminals in the worktree do not suppress Resume. Repeating
the terminal-open or Resume action while that worktree already has a start in flight re-targets the
pane without issuing a second launch; the in-flight state clears on both success and failure, so a
rejected launch never wedges the action.
Background and CLI launches never steal dashboard focus. The browser polls the
authoritative terminal registry on its normal activity cadence even when its current snapshot is
empty, so the first background-created terminal becomes visible without a reload. That poll is
single-flight: a tick starts no new registry request while one is outstanding, and the next tick
resumes polling once the request settles, whether it succeeded or failed.

Embedded terminals do not change `WorktreeStatus.HasActiveSession` or add another card-level
active-session indicator. That flag and its terminal-button glow, focus label, native `+`
visibility, and delete/archive native-kill prompt remain tied only to a tracked Windows Terminal
window. Existing coding-tool status continues to show whether an embedded agent is working.

Interactive agent-launch prompts containing control characters, including newlines, are
UTF-8/base64 encoded as inert data and decoded by a fixed PowerShell expression. The resulting
shell command is one control-free line while the coding tool receives the original prompt text
unchanged.

The raw terminal-input boundary rejects blank or control-character-bearing commands and commands
whose complete UTF-8 ttyd input frame (`0` prefix, command, and carriage return) exceeds 16,384
bytes, before creating a terminal. It then creates one terminal through the existing lifecycle API
and submits the validated command through that terminal's authenticated command-only attachment.
That attachment skips browser replay and output forwarding, so a short-lived sender cannot race
shell startup or replay delivery. Treemon then authoritatively relists the registry and reports
success only while the exact new terminal remains registered. Failed submission or retention closes
that exact terminal when possible and reports the launch as failed rather than claiming success.

### Control and discovery

The host exposes a stable authenticated loopback control API under an explicit version. Its complete
lifecycle surface is:

- health and version;
- authoritative terminal list;
- start a new terminal by canonical worktree path;
- close one terminal; and
- shutdown, used only for committed host replacement or an explicit administrative request.

Start, close, and list return or reconcile against the authoritative registry rather than asking the
server to merge lifecycle fragments. Browser attachment endpoints ride terminal data returned by
that registry; there is no separate attach/detach lifecycle API.
Registry and data-plane mailbox calls have bounded replies. Each message failure is contained before
the next message is processed, so a cleanup failure cannot wedge later list, close, or shutdown
requests. Mailbox diagnostics identify only the mailbox and exception type, never terminal content,
paths, environment values, or credentials.

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

The same exact-origin join supplies terminal tab titles. Among the live sessions attributed to one
terminal, the active session wins; otherwise the most recently active live session is
representative. Its freshest reported intent or session title is exposed through the same activity
selection and display formatting used by the worktree card. An unrelated session in the same
worktree cannot label the tab.

`WaitingForUser` remains non-idle without a timeout. There is no forced replacement or operator
override that discards a waiting Copilot session.

Arbitrary shell commands, child or background jobs, terminal output, browser attachment state, and
other non-Copilot activity never gate an update. Treemon neither inspects nor warns about foreground
shell work before replacement, and does not wait for every session associated with a worktree.

Production lifecycle commands are a separate process-ownership boundary. `treemon.ps1` refuses
`restart`, `deploy`, and a `start` that would launch production when the caller inherited
`TREEMON_TERMINAL_SESSION_ID`, before stopping production or building deployment candidates. A
`start` against an already-running server remains an informational no-op. Any production process
launched from that shell would inherit the terminal's kill-on-close Job Object and later die when
the terminal closes, the host is replaced, or the host crashes. `add` and `remove` still persist
successful root changes, but skip their automatic production restart in this context and direct the
user to restart from an external PowerShell window. `stop`, `status`, `log`, development, and demo
commands remain available.

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
scrollback. Recreated terminals keep the captured opening order, and the client remaps each
worktree's selected tab to the same sibling ordinal where possible. Process state and scrollback do
not survive host replacement.

A compatible Treemon deployment reconnects to the running host. When it carries a newer compatible
host executable, that executable is staged and replaced only through the opportunistic flow above.
An incompatible control-API deployment is blocked while the old host has terminals. The system does
not run concurrent host generations or build a multi-protocol bridge, so the control API remains
deliberately stable.

### Worktree lifecycle and failure

Deleting or archiving a worktree first closes every terminal owned by that exact worktree through
the authoritative host API. The worktree mutation proceeds only after every close succeeds; a
partial close failure leaves the worktree intact and reconciles the authoritative remaining
terminals. Other worktrees are unaffected.
The lifecycle mailbox holds a short-lived in-memory reservation for the canonical worktree path
from before its terminal closes through the delete/archive mutation. Another cleanup, terminal
start for that path receives a retryable busy error, while unrelated worktrees remain available;
the reservation is released after both successful and failed mutations.
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

### Launch routing

`TerminalLaunch` is the single server-side boundary for starting user terminals.
`SessionManager` and `EmbeddedTerminal` are backend implementations, not policy call sites.
It exposes separately typed native open/new-tab and embedded plain/command operations, preserving
each backend's result type for callers: native operations use `SessionManager`; embedded operations
use `EmbeddedTerminal`. Browser headers, `HttpContext`, and `TREEMON_TERMINAL_SESSION_ID` do not
participate in this decision.

Command-capable embedded start retains the exact `TerminalRecord` returned by
`TerminalHostClient.startTerminalOnHost`, submits an optional command through the existing
`SendTerminalCommand` function, authoritatively confirms the exact ID after delivery, and returns
both the reconciled snapshot and exact started terminal ID. The TerminalHost v2 control request
remains `{ worktreePath }`; command text never becomes lifecycle API input.

Every start — plain or command-bearing — carries that exact terminal ID out to its caller, so the
browser selects the started terminal by identity. Comparing registry snapshots taken before and
after a start cannot distinguish it from a terminal a background launch created in the same window.

`CodingToolCli` keeps control-free interactive prompts readable as single-quoted PowerShell
arguments. An interactive prompt containing controls is encoded as UTF-8/base64 and decoded only by
a fixed expression in the emitted command. `TerminalHostClient` separately validates the raw
command and mirrors the host's 16,384-byte attachment-message cap against the complete transmitted
input frame; no command chunking or acknowledgement protocol is added.

### Terminal host

`src/TerminalHost` is a small F#/.NET executable published with Treemon but launched as an
independent process. Its in-memory registry is keyed by terminal session ID; each entry carries its
canonical worktree path and owns the ttyd Job Object and process handles, sole upstream WebSocket,
one browser attachment, and bounded replay bytes. This makes worktree-to-terminal ownership
one-to-many while close and upstream-exit handling remain exact-session operations.

`TerminalDataPlane` owns only the replay and attachment mailbox, with `createCore` as its focused
state-machine seam. `TerminalProxy` owns the ttyd/browser WebSocket pumps, HTTP forwarding, and
attachment endpoint. It and `ControlApi` use one `LoopbackHost` bootstrap for the shared Kestrel
loopback binding, request-size limit, server-header policy, and dynamic-port discovery. Startup
waits for the first ttyd output frame using the launcher's configured startup timeout before
exposing the attachment endpoint. The upstream pump forwards fragmented output incrementally and
restores ttyd's protocol prefix on continuation chunks instead of buffering a whole WebSocket
message under the replay limit. Browser attachments use ttyd's `tty` subprotocol and receive replay;
server command attachments use the authenticated `treemon-command` subprotocol and are input-only.

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

Control API version 2 is exactly:

- `GET /api/v2/health`;
- `GET /api/v2/terminals`;
- `POST /api/v2/terminals` with the sole JSON field `worktreePath`;
- `DELETE /api/v2/terminals/{sessionId}`; and
- `POST /api/v2/shutdown`.

Health returns the host PID, process-start ticks, host version, and control API version. List, start,
and close return the same authoritative `{ revision, terminals }` snapshot, where each terminal has
only its stable `sessionId`, canonical `worktreePath`, and live `attachmentEndpoint`. A path is a
known worktree only when it is an existing, fully-qualified directory with a `.git` marker and
`git rev-parse --show-toplevel` resolves to that exact canonical directory.
Every successful start appends exactly one fresh session ID. The server reads the registry before
the request and authoritatively relists afterward, so it can identify the new terminal and resolve an
ambiguous response without conflating it with an existing sibling in the same worktree.

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
Every attachment HTTP response limits framing through a `Content-Security-Policy: frame-ancestors`
directive built from the validated dashboard origins, or `'none'` when none are configured. A
missing `Origin` remains valid for authenticated loopback non-browser protocol requests.
The proxy injects a small style into ttyd's root HTML response that hides the native
`.xterm-viewport` scrollbar without changing its overflow or scrollback. The terminal document is a
separate origin, so the dashboard cannot apply this styling itself.

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
The client stores active terminal IDs and in-flight start state per worktree. Registry refreshes
retain exact selections while IDs remain valid, choose the same-worktree neighbor after a close,
and preserve the selected sibling ordinal across replacement.
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
The script uses the inherited terminal session ID as a fail-closed origin marker for production
launches and restarts; this check is independent of host compatibility and Copilot activity state.
Compatibility probing uses the manifest-declared API version for health, list, and shutdown, so an
empty older host can be retired safely while a non-empty one remains available to its matching
server until its terminals are closed.
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
policy plus the optional display-safe activity for each terminal. The remoting API enriches host
registry snapshots from the scheduler's bounded live-session map; the TerminalHost registry and
control API remain unaware of agent activity. Replacement policy remains opaque: wait, or proceed
with the epoch and optional shell command keyed by exact terminal session ID. It applies generic
openness and freshness decay only to non-waiting states; an effective
`WaitingForUser` remains non-idle from its request/completion clocks regardless of `last_seen`, while
exact terminal-ID filtering keeps the query bounded. It owns provider selection and `CodingToolCli`
command construction; terminal replacement only rechecks the epoch, recreates terminals, and
delivers supplied commands. Hourly retention prunes live status and origin epochs, retaining epochs
only for durable origins or the latest authoritative host registry; observing a registry immediately
discards epochs for terminals no longer in it while the global counter remains monotonic.
Once a session has an exact terminal origin, a later report that omits the optional origin retains
the known value in memory and durable storage; there is no implicit clear operation.
Activity ingestion accepts a Copilot `SessionId` only when it is 1–128 ASCII characters from
`[A-Za-z0-9._:-]`, so the persisted resume identity is bounded and cannot carry terminal control
input.

Replacement snapshots terminal presentation and exact resumable Copilot ownership before stopping
the old host. `TerminalSessionActivity` uses `CodingToolCli` to prepare provider-specific commands;
terminal replacement delivers those opaque commands in captured opening order. The replacement
registry receives fresh session IDs, so the client preserves each worktree's selection by sibling
ordinal rather than by stale identity.

### Deliberate simplicity

There is one host and one current registry. The design has no generation journals, empty witnesses,
content-addressed bundles, runtime-lock process, tombstones, leases, concurrent host generations,
legacy protocol migration, or live process-state migration. It does not retain a Node runtime,
PowerShell lifecycle helpers, or compatibility shims.

## Verification

Run `npm run test:embedded-launch-routing` to build and execute the isolated launch-routing harness.
It exercises every agent-bearing entry point, prints bearer-redacted raw host registry JSON and raw
copilot-recorder JSON for each route, verifies native HWND preservation and exact failed-delivery
rollback with no AutoSync acceptance, and removes only its exact fixture terminals, processes,
ports, and state.

## Decisions

- **One separately running F# host:** ordinary Treemon restarts remain control-plane events while
  the implementation has one language, one process owner, and no script/runtime handoff.
- **Job Object before execution:** kernel membership established before ttyd resumes is the only
  terminal-tree ownership authority.
- **External production ownership:** production launch and restart require a caller outside an
  embedded terminal because the terminal Job Object deliberately has no breakaway policy. The
  inherited terminal session ID blocks self-owned production before destructive work; this is
  independent of compatible-host idle gating and incompatible-host deployment refusal.
- **One serialized cleanup owner:** only the registry closes retained process and Job Object handles.
  Data-plane upstream exit is a fire-and-forget exact-session notice, avoiding a mailbox dependency
  cycle while making stale notices harmless. Per-message recovery and bounded replies keep both
  mailboxes responsive; type-only diagnostics preserve the no-terminal-content logging boundary.
- **One upstream and one browser writer per terminal:** the host preserves each shell across browser
  reconnects without defining multi-writer input semantics.
- **Separate state from proxy hosting:** the replay/attachment mailbox remains independently
  testable while HTTP/WebSocket hosting shares one loopback-only Kestrel bootstrap with the control
  API, preventing security-sensitive host configuration from drifting.
- **Raw bounded replay:** reconnect gets useful recent output without persisting terminal content or
  introducing a terminal-state serializer. Replay capacity never doubles as an upstream transport
  limit; large output messages are streamed while old retained frames are evicted.
- **Explicit replay discontinuities:** replay reads distinguish a complete suffix from one whose
  requested prefix was evicted. Resuming across that gap resets and clears the emulator and shows an
  omission notice before the retained output.
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
- **No replacement escape hatches:** `WaitingForUser` gates indefinitely for every session owned by
  a current terminal; most-recent selection applies only to the resume identity. Non-Copilot work is
  deliberately ignored and may be terminated without warning once the Copilot gate is idle.
- **Stable API over compatibility layers:** compatible servers reconnect; an incompatible deploy
  waits until no terminals exist instead of carrying old protocol clients or migrating live state.
- **Explicit versioned wire contracts:** the candidate deployment preflight is a parsed server run
  mode with a named JSON result, and the host maps registry domain records to dedicated control API
  v2 response property sets. Version 2 defines every start as creation of one fresh terminal, so a
  server never silently reconnects to singleton-style start semantics. Exact-property regression
  tests prevent internal fields from leaking
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
- **Origin-scoped attachment framing:** attachment responses use CSP `frame-ancestors` with every
  configured dashboard origin, rather than same-origin framing that would reject the legitimate
  cross-port dashboard iframe.
- **Proxy-owned terminal scrollbar chrome:** the attachment proxy adds one CSS override to ttyd's
  root page instead of carrying a forked custom index. It hides the rendered xterm scrollbar while
  preserving wheel, keyboard, and programmatic scrollback.
- **Resume without widening control API:** after each replacement terminal is recreated, Treemon
  briefly attaches through the existing authenticated ttyd protocol and submits the opaque command
  selected by `TerminalSessionActivity`. A terminal without an exact resumable session receives no
  input and remains a plain PowerShell shell. Submitted terminal input is a raw shell boundary:
  direct commands carrying a control character are rejected rather than written, while
  `CodingToolCli` first converts control-bearing prompt data to a control-free UTF-8/base64 form.
  A stored Copilot `SessionId` therefore cannot inject an extra command line into a recreated shell.
- **Typed launch-operation routing:** `TerminalLaunch` is the only product-level start boundary.
  Its native operations return only native results and its embedded operations return exact
  embedded-start results. Explicit native card operations remain native; every agent-bearing launch
  uses the embedded backend, including external `tm launch`.
- **Command delivery after lifecycle start:** normal agent launches reuse the same authenticated
  attachment input boundary as replacement resume. The stable TerminalHost v2 lifecycle protocol
  remains unchanged, the server rejects any complete input frame above the host's mirrored
  16,384-byte attachment cap before lifecycle start, waits for output-backed shell readiness, and
  confirms the exact registry entry after delivery. The command-only subprotocol skips replay and
  output forwarding; failed delivery or retention closes only the exact new terminal. No chunking
  or acknowledgement protocol is introduced.
- **Background discovery without focus theft:** the client polls the registry even from an empty
  snapshot. Background and CLI launches become attachable without opening or retargeting a user's
  pane.
- **Native-only card session state:** embedded terminal presence does not feed
  `HasActiveSession` or add a second card indicator. The existing card state continues to describe
  only the explicitly tracked Windows Terminal window; coding-tool activity describes agents.
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
  reservation around all owned terminal closes plus mutation. Same-path lifecycle mutations fail
  retryably until a `finally` release, while unrelated worktrees stay concurrent; no persistent
  lease, supervisor, or cross-process cleanup protocol is required.

## Key Files

| File | Purpose |
|---|---|
| `src/TerminalHostLayout/Layout.fs` | Shared state/staging paths, version-directory grammar, executable names, and required host bundle members |
| `src/TerminalHost/TerminalHost.fsproj` and `src/TerminalHost/*.fs` | F#/.NET host project: Job Object launch, ttyd ownership, proxy, replay, registry, and control API |
| `src/Server/TerminalHostProcess.fs`, `TerminalHostEndpoint.fs`, `TerminalHostManifest.fs`, `TerminalHostClient.fs`, and `TerminalHostReplacement.fs` | Host process/identity, shared loopback endpoint shape, discovery validation, authenticated control client and compatibility preflight, and replacement coordination |
| `src/Server/TerminalLaunch.fs` | Sole product-level launch policy and native-versus-embedded backend selection |
| `src/Server/EmbeddedTerminal.fs` | Terminal lifecycle mailbox, command-capable start, authoritative snapshot reconciliation, and public start/get/close surface |
| `src/Server/SessionActivity.fs` | Effective per-session state used by the idle gate |
| `src/Server/SessionActivityService.fs` | Activity ingestion, terminal-origin validation, bounded live state, pruned raw origin epochs, and mailbox-serialized terminal activity queries |
| `src/Server/TerminalSessionActivity.fs` | Exact owned-session projection for tab activity, idle gating, and opaque resume policy |
| `src/Server/SessionActivityStore.fs` | Durable Copilot session state, optional terminal origin, and indexed exact-origin queries |
| `src/Extension/reporting/extension.mjs` | Passive activity reports sourced from `TREEMON_TERMINAL_SESSION_ID` |
| `src/Server/CodingToolCli.fs` | Provider-specific exact-session resume command construction |
| `src/Server/Program.fs` | Host client and replacement-loop lifecycle without terminal shutdown on server stop |
| `treemon.ps1` | Published host staging, deployment compatibility preflight, and embedded-terminal production-lifecycle guard |
| `src/Client/TerminalPane.fs` | Terminal tabs, mounted iframes, labels, order, selection, and interruption UI |
| `src/Tests/EmbeddedTerminalTests.fs` and `src/Tests/TerminalHostTests.fs` | Isolated host lifecycle plus real proxy command delivery, control rejection, UTF-8 frame boundaries, replacement, crash, security, and cleanup coverage |
| `src/Tests/WorktreeApiLaunchTests.fs` | Worktree API typed-operation routing, exact result identity, control-free AgentDoc/SystemView/create-worktree prompt commands, and post-fork launch ordering |
| `src/Tests/EmbeddedLaunchEndToEndTests.fs`, `src/TestAgentRecorder`, and `scripts/verify-embedded-launch-routing.ps1` | Reproducible isolated real-host launch matrix, exact argv recorder, raw route evidence, forced-delivery rollback, native HWND preservation, and exact cleanup |
| `src/Tests/TerminalPaneTests.fs` | Exact server-returned terminal selection and direct Canvas launch routing |
| `src/Tests/SessionActivityServiceTests.fs` | Exact terminal ownership, idle policy, and provider-specific resume-plan coverage |
| `scripts/treemon-deployment.test.ps1` | Isolated staging, compatibility-preflight, candidate-first ordering, and embedded-terminal lifecycle refusal coverage |

## Related Specs

- `docs/spec/session-status-push.md` — authoritative per-session Copilot activity and terminal-origin
  reporting.
- `docs/spec/native-session-management.md` — explicit card `>` / Enter and tracked-window `+`
  Windows Terminal behavior.
- `docs/spec/worktree-monitor.md` — worktree lifecycle and dashboard integration.
