# Embedded Terminal

## Goals

- Allow each canonical worktree path to own multiple independently selectable and closable writable
  PowerShell terminals.
- Keep terminals alive across browser attachment changes and ordinary Treemon server restarts.
- Run the terminal on one small, separately running F#/.NET `TerminalHost` executable with no Node
  or PowerShell productization stack. The whole terminal runtime (`src/TerminalHost`,
  `src/TerminalHostLayout`, `src/Server/TerminalHost*.fs`,
  `src/Server/TerminalSessionActivity.fs`, `src/Server/EmbeddedTerminal.fs`, and any terminal-specific
  runtime script) stays at or below 5,200 nonblank production lines. Product-level launch and
  user-authorized cleanup policy remain outside that budget.
- Give every terminal an exact kernel-owned process boundary established before ttyd executes.
- Keep lifecycle control loopback-only, authenticated, versioned, and limited to health, list,
  start, close, and host-wide shutdown.
- Route every prompted or automatic agent launch through the embedded host while retaining Windows
  Terminal only for the card's explicit `>` / Enter action.
- Apply a staged TerminalHost update only after the user clicks the plainly labelled toolbar action.
- Acquire one global maintenance lock before snapshotting, reject every new embedded-terminal start
  route while locked, restart only currently hosted durable sessions, and omit terminals without an
  open durable session.
- Release each path-scoped cleanup reservation through a bounded, uncancelled mailbox
  acknowledgement, and log when acknowledgement cannot be obtained.
- Once accepted, complete the update as one forward-only transaction. Any first failure leaves the
  server process permanently locked with a fatal UI that directs the user to redeploy or restart
  Treemon manually.
- Keep development and verification isolated from production state, ports, and processes.

## Expected Behavior

### Terminal lifetime and attachments

Starting a terminal creates a new terminal for the canonical worktree path. A terminal remains
available until its shell exits or it is explicitly closed. The
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
The next healthy registry reconciliation removes that exact terminal tab and advances its
worktree-local selection to a remaining sibling when one exists.

For each terminal, the host is ttyd's sole upstream WebSocket client for the terminal lifetime. It
continuously drains ttyd into a small bounded raw replay buffer and accepts one replaceable browser
attachment. Replacing or losing the browser attachment does not replace the shell. A new attachment
receives the bounded replay, the latest observed interactive DEC modes, and a resize so full-screen
applications can redraw. The host restores an active alternate-screen buffer and output-rendering
modes before replay, then reapplies mouse tracking and encoding, cursor visibility, focus reporting,
bracketed paste, and other input modes afterward. It never sends an inactive alternate-screen reset
after replay because that reset can restore stale cursor state. A full terminal reset clears the
retained projection; a soft reset clears only the modes xterm resets while preserving its active
buffer and mouse modes. The scanner recognizes seven-bit `ESC [` control sequences and does not
interpret UTF-8 continuation bytes as eight-bit controls. Attachment routing is data-plane behavior,
not an additional lifecycle state. Output older than the buffer, terminal scrollback, and
browser-rendered state are not durable.
The host does not publish a terminal as started until that upstream has delivered its first terminal
output frame within the terminal startup timeout; a bound ttyd TCP port alone is not evidence that
PowerShell is ready for input. Upstream output is streamed into protocol-valid chunks, so the
1 MiB replay capacity is only a retention bound: one larger WebSocket message evicts old replay
rather than ending the upstream and terminal.
If a paused attachment falls behind the replay window, resume resets and clears the emulator, shows
a visible omission notice, and then sends the surviving frames instead of silently splicing
discontinuous output into the existing state.

The terminal pane normally follows the currently focused worktree card. Clicking a card's embedded
terminal action, or pressing `T` while that card is focused, explicitly targets that worktree
without changing the selected dashboard card or Canvas document. It opens the pane on the remembered
terminal and moves browser focus into it when that worktree already has one, or starts, selects, and
focuses a terminal when none exists; the next card selection restores normal focus-following. Its
tab strip shows only the targeted worktree's terminals and labels each one with the freshest
display-safe activity from that exact terminal's representative live Copilot session: reported
`assistant.intent` or session title.
Each tab also exposes the distinct live durable Copilot `SessionId` values from its exact terminal
origins, sorted ordinally. Duplicate physical instances of one durable session contribute one ID;
closed, stale, external, and plain-shell sessions contribute none.
Until either exists, the label falls back to `Terminal 1`, `Terminal 2`, and so on in opening order.
It remembers the selected terminal independently for each worktree. **New** starts another terminal
for the targeted worktree; the empty state offers **Start terminal**. Selecting a canvas doc whose
effective session matches a listed terminal changes this remembered selection without opening,
revealing, or focusing the Terminal pane. Switching worktrees hides the other worktrees' tabs
without closing their terminals, and running iframes stay mounted so their browser state survives.
Closing the last visible tab leaves the pane open in its empty state; only the persistent top-bar
**Terminal** control hides or shows the pane, using the same active treatment as the **Canvas**
control. Middle-clicking a tab or pressing Ctrl+W while its terminal has focus invokes the same
exact-terminal close action as its close button. Close removes the tab and iframe immediately and
remembers that exact terminal ID so stale registry, start, or cleanup responses cannot restore it
while authoritative teardown continues. A registry read that no longer lists the
terminal confirms teardown and releases the dismissal; a failed teardown releases it at once so the
next authoritative registry read restores the tab.

When a running terminal becomes visible through pane open, tab selection, or worktree selection,
the client routes one activation through Elmish and sends an exact-origin message to that iframe
after its visible DOM state has committed. It repeats the signal when the top-level document becomes
visible or focused after an interruption such as RDP reconnect. The terminal page accepts the
message only from its parent and a configured dashboard origin. It reloads immediately when ttyd's
exact manual reconnect overlay is present, or checks for that exact overlay during one coalesced,
bounded recovery window when page initialization or the transport-close event trails the visibility
signal. Repeated visibility signals refresh that window, while a document-local reload latch
prevents paired browser events from replacing the same attachment twice. A receiver-initiated
reload writes a marker that the new terminal document consumes and removes during initialization.
The resulting suppression decision stays document-local and can suppress at most one iframe-load
activation; when browser storage is unavailable, that decision fails closed rather than looping.
Deactivation clears the child recovery window so a hidden pane, terminal, worktree, or browser tab
cannot reclaim the single attachment. Healthy shell prompts, partially typed commands, password
prompts, and full-screen applications receive no input and are not reloaded.

Beside **New**, a selected running terminal with a validated attachment endpoint shows
**Reconnect view**. The action replaces only that terminal's browser iframe and attachment while
preserving its terminal ID, endpoint, selected tab, shell, agent, and every sibling iframe. It is
hidden for an empty selection, an interrupted terminal, or a rejected endpoint. The replacement
restores iframe focus only when that view generation is still current, the terminal remains
selected, the pane is visible, and no modal overlay is active when the deferred focus effect runs;
ordinary worktree focus, tab selection, closure, pane hiding, and newer reconnects cancel the
pending focus request so a late load cannot reclaim focus after the user moves elsewhere. Reconnect
does not call start, close, resume, command-input, host-replacement, or process APIs, and an iframe
load does not create a connected-success state. The new attachment receives the host's current
alternate-screen mode, bounded raw replay, and remaining
interaction-mode projection, so the full-screen background, mouse input, cursor visibility, focus
reporting, and bracketed paste survive even when their enabling sequences are older than the
retained screen output.

### Launch routing and command startup

The card's `>` / Enter action remains the explicit native Windows Terminal choice. The always-visible
robot-head Agent action and focused-card `a` / `A` shortcut always start a fresh embedded terminal,
submit exactly `copilot --yolo`, open the terminal pane, and select the exact returned terminal. The
dedicated embedded-terminal action and `t` shortcut reuse that worktree's remembered embedded
terminal when one exists and otherwise start a plain embedded PowerShell terminal. The terminal
pane's **New** action and Ctrl+N from its active terminal always start and focus another plain
embedded terminal for that worktree.

Every agent-bearing process launch uses an embedded terminal: the robot-head Agent card action,
Resume, contextual card actions, explicit Canvas session launch, create-worktree prompt launch,
AutoSync fallback, queued Canvas fallback, and `tm launch`. A browser need not be open for a CLI or
background launch; the host owns the terminal until a dashboard attaches later.

Direct dashboard actions that start an agent open and target the terminal pane, selecting the exact
returned terminal. When invoked, Resume first joins its durable target session ID to the
authoritative running terminal snapshot; when that exact session is already live, it returns the
existing terminal and starts no second Copilot process. An embedded terminal without an open coding
session does not suppress Resume; any open coding session does. Repeating the terminal-open or
Resume action while that worktree already has a start in flight re-targets the pane without issuing
a second launch; the in-flight state clears on both success and failure, so a rejected launch never
wedges the action. Agent actions do not coalesce with an in-flight start: each one queues a fresh
Copilot launch and selects and focuses its exact returned terminal in order.
Background and CLI launches never steal dashboard focus. The browser polls the
authoritative terminal registry on its normal activity cadence even when its current snapshot is
empty, so the first background-created terminal becomes visible without a reload. That poll is
single-flight: a tick starts no new registry request while one is outstanding, and the next tick
resumes polling once the request settles, whether it succeeded or failed.

Embedded terminals do not change `WorktreeStatus.HasActiveSession` or add another card-level
active-session indicator. That flag and its terminal-button glow, focus label, and delete/archive
native-kill prompt remain tied only to a tracked Windows Terminal window. Existing coding-tool
status continues to show whether an embedded agent is working.

Interactive agent-launch prompts containing control characters, including newlines, are
UTF-8/base64 encoded as inert data and decoded by a fixed PowerShell expression. The resulting
shell command is one control-free line while the coding tool receives the original prompt text
unchanged.

Every Copilot command submitted through the host includes `--experimental`, so the CLI discovers
Treemon's user-scoped reporting extension from the active Copilot config directory. A fresh
isolated `COPILOT_HOME` therefore uses the same deterministic extension path as a normal launch
rather than relying on a project extension or a persisted experimental setting. The isolated
harness accepts the CLI's disposable folder-trust confirmation before evaluating extension
startup.
Exact durable Resume uses `--session-id=<id>` so the selected durable identity is established before
extension startup rather than through a foreground-session switch.

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
- shutdown, used only for a user-requested host update or an explicit administrative request.

Start, close, and list return or reconcile against the authoritative registry rather than asking the
server to merge lifecycle fragments. Browser attachment endpoints ride terminal data returned by
that registry; there is no separate attach/detach lifecycle API.
Registry and data-plane mailbox calls have bounded replies. Each message failure is contained before
the next message is processed, so a cleanup failure cannot wedge later list, close, or shutdown
requests. Mailbox diagnostics identify only the mailbox and exception type, never terminal content,
paths, environment values, or credentials.
Start has a 150-second reply budget because its serialized workflow may include host startup,
terminal creation, command delivery, authoritative confirmation, and compensating close. Other
ordinary get, close, and cleanup operations retain the shared 60-second budget; replacement commit
keeps its separate 300-second budget.

A machine-level discovery manifest contains only the exact host identity (PID and process start
identity), loopback endpoint and bearer token, host version, control API version, and the version of
any staged executable. Terminal registry state is read from the live host, not copied into the
manifest. The server rejects malformed or stale identities, non-loopback endpoints, unknown or
unsafe worktree paths, invalid attachment endpoints, and unexpected browser origins.

### Exact Copilot ownership and update snapshot

Each started terminal receives a stable `TREEMON_TERMINAL_SESSION_ID`. A Copilot process launched
inside that terminal inherits the value, and the passive reporting extension stores it on the exact
process instance. Terminal ownership is therefore joined by terminal ID, never inferred from the
worktree path.

When the user starts an update, Treemon intersects the current authoritative TerminalHost registry
with open exact session instances and selects at most one durable Copilot `SessionId` per terminal,
using greatest durable activity. `Working`, `WaitingForUser`, and `Idle` sessions are all eligible:
the labelled update action is consent to interrupt them. A terminal with no open durable identity is
omitted from the restart snapshot. Multiple live processes in one terminal are terminated with the
host, but only the selected durable conversation is restarted.

Explicit terminal close, worktree delete, and worktree archive retain their user-authorized graceful
session-shutdown and exact cleanup flow. The update transaction does not use that per-session path;
it asks TerminalHost to shut down the complete host and its owned process trees.

### User-requested TerminalHost updates

A valid staged executable makes exactly one standard header button, **Apply TerminalHost update**,
appear immediately left of Sort. Its tooltip states that hosted durable sessions will be
interrupted and restarted; `Unavailable` renders no action.

Clicking the action immediately removes it and shows a focused, non-dismissible overlay with a
visible progress spinner. Client terminal messages and shortcuts become no-ops, while the
`EmbeddedTerminal` mailbox enters `WaitingForCleanup` before acknowledging `Updating`; all new
embedded or native terminal mutations and cleanup reservations are therefore locked at the server
boundary too.

Cleanup already in progress is a sequencing dependency, not a rejection. Its apply and acknowledged
release messages remain processable while the global lock is held. The update request returns
`Updating` immediately, waits for every existing reservation to release, and then starts the
host-wide transaction exactly once without another click. With no existing cleanup it starts
immediately. Reservations have no blind expiry because they may still protect running teardown.

The worker performs exactly one sequence:

1. Ask the current TerminalHost to shut down, which closes every host-owned terminal and Job Object.
2. Launch the staged TerminalHost executable once and await its health endpoint.
3. Recreate one terminal per captured durable session in captured registry order.
4. Submit the provider-specific direct session command (`--session-id=<id>`) to each recreated
   terminal.
5. Apply the new authoritative registry and return to `Unlocked` only after every recreation and
   command succeeds.

No optional literal `resume` prompt is sent. Shell process state, terminal scrollback, and terminals
without a captured durable session do not survive the update.

Any first file, process, control-API, host-start, terminal-start, or command-delivery failure enters
`Fatal(error)`. The global terminal lock and blocking overlay remain for the life of that Treemon
server process. Treemon performs no retry, rollback, old-host restoration, staged-host cleanup,
second generation, or partial-session recovery; the fatal message directs the user to redeploy or
restart Treemon manually from an external PowerShell window.

### Worktree lifecycle and failure

Explicit terminal close, worktree deletion, and worktree archive are user-authorized teardown
operations. Outside the lifecycle mailbox and under its existing cleanup reservation, they request
graceful shutdown for every open exact instance owned by each target terminal, then close the
terminal even when graceful shutdown is unavailable, rejected, or timed out. Exact survivor cleanup
remains authoritative: after it succeeds, the activity service monotonically closes all and only
the exact owned instances before the API returns. An unresolved survivor leaves the terminal
registered and returns teardown failure.

A forced process kill can leave Copilot's on-disk in-use marker behind. Treemon neither deletes nor
overrides that marker. The next Resume may stop at Copilot's visible `Force resume?` confirmation;
that prompt is an accepted degraded recovery path as long as the prior exact process is gone and no
duplicate CLI was launched.

Deleting or archiving a worktree closes every terminal owned by that exact worktree through this
sequence. The worktree mutation proceeds only after every close succeeds; a partial close failure
leaves the worktree intact and reconciles the authoritative remaining terminals. Other worktrees
are unaffected.
The lifecycle mailbox holds a short-lived in-memory reservation for the canonical worktree path
from before its terminal closes through the delete/archive mutation. Another cleanup, terminal
start for that path receives a retryable busy error, while unrelated worktrees remain available;
the reservation is acquired only when the cleanup workflow starts and is released after both
successful and failed mutations. Its `finally` performs a bounded, uncancelled request/reply so
normal completion, typed failure, exception, and caller cancellation all attempt acknowledged
removal before returning. Acquisition timeout makes the same release attempt because the mailbox
may already have granted the lease. A failed acknowledgement is logged rather than hidden, and no
blind expiry can clear cleanup that may still be active. Constructing an async close or cleanup
workflow is inert.
An attempt made during terminal maintenance fails without mutating the worktree or archive state.
After a successful update terminal actions are available again; after a fatal update the blocking
overlay remains until Treemon is manually restarted.

Reservation and update telemetry correlates acquisition, release, queued waiting, transaction
start, duplicate requests, and acknowledgement failures with bounded opaque lease IDs, ages, kinds,
and counts. It excludes worktree paths, terminal content, commands, prompts, capabilities, and
bearer values.

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
It exposes separately typed native-open and embedded plain/command operations, preserving each
backend's result type for callers: the native operation uses `SessionManager`; embedded operations
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

Windows process creation uses `CREATE_SUSPENDED`, `CREATE_UNICODE_ENVIRONMENT`, and
`CREATE_NO_WINDOW`, followed by immediate `AssignProcessToJobObject` and `ResumeThread` in the host
process. The Job Object uses kill-on-close without a breakaway policy. Because externally launched
programs can still establish process ownership outside that job, terminal teardown captures exact
Job membership before stopping the data plane, then recaptures Job membership and bounded observed
descendants after that graceful stop and before closing the Job handle. It waits for captured
identities, terminates only survivors whose PID and start ticks still match, and retains those
identities for a retry when cleanup remains incomplete. The registry removes a terminal whenever
exact process cleanup succeeds; a data-plane stop failure remains a diagnostic, while proxy
application and client cleanup are still attempted. Host shutdown requests application exit only
after the registry and pending cleanup set are empty. A failed shutdown keeps the live host
start-capable and may be retried against retained entries. Process names alone are never cleanup
authority.

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

Deployment fingerprints the complete non-PDB TerminalHost bundle. The nested host publish excludes
repository source-revision metadata, so an unrelated Treemon commit does not change that fingerprint;
any changed host assembly or runtime file does. An equal live fingerprint skips staging, while a
different fingerprint is copied and verified in one digest-derived version directory.

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

`TerminalLaunch` is the sole product-level start boundary. Native card actions use `SessionManager`;
every agent-bearing or embedded launch uses `EmbeddedTerminal`, so the maintenance lock covers normal
start, agent start, Resume, contextual actions, Canvas launches, AutoSync fallback, create-worktree
prompts, and `tm launch` without route-specific checks. When TerminalHost replacement is configured,
the Worktree API also serializes native open/focus/kill operations through the same mailbox before
calling `SessionManager`; an action ordered before the update may finish, while one ordered after the
lock is installed returns without executing.

`EmbeddedTerminal` owns the authoritative cached snapshot, cleanup reservations, and the maintenance
state `Unlocked | WaitingForCleanup of PendingUpdate | Updating of RestartSession list | Fatal of
string`. Installing `WaitingForCleanup` is the atomic global lock and immediately acknowledges
`Updating`. The last acknowledged release posts one internal begin message; snapshot preparation
then runs in the mailbox's serialized turn, and forward-only host I/O runs outside it. Only the
mailbox installs the success registry or fatal transition, and no HTTP reply channel is retained
across cleanup or replacement.

`TerminalSessionActivity` queries only the exact terminal origins in the captured registry. It
applies the ordinary open-instance rule, chooses the greatest-activity durable session per terminal,
and builds the provider-specific direct Resume command.

The client receives `TerminalHostUpdateState` with the normal dashboard response. Elmish owns the
single Available button, immediate `Updating` transition, terminal-interaction gate, trigger command,
success refresh, animated progress overlay, and permanent fatal overlay. Polling keeps the overlay
installed through queued cleanup and replacement. If the acknowledgement transport fails, the
client remains locked in `Updating` until an authoritative dashboard response reports the actual
state instead of inventing a local fatal result.

`treemon.ps1` still publishes the host and stages a changed complete host bundle in a versioned
directory. Deployment compatibility preflight remains separate: an incompatible live host with
terminals still blocks deployment. The running host monitors staging only to publish availability;
it never initiates replacement.

TerminalHost control API version 2 remains unchanged. Update recreation uses the existing start
endpoint and authenticated command attachment; session commands never become control-API input.
Production lifecycle commands still require an external PowerShell window when the caller inherited
`TREEMON_TERMINAL_SESSION_ID`.

SessionBridge's graceful shutdown capability remains available for explicit close/delete/archive.
The update path does not contact individual bridges, so a partial bridge shutdown cannot leave the
old host connected while reporter heartbeats disappear.

### Deliberate simplicity

Update coordination uses one host, one authoritative registry, one restart-session snapshot, and one
four-case mailbox state. `WaitingForCleanup` adds sequencing without another worker or retry path.
Success installs the new registry and unlocks the mailbox; any failure transitions it to `Fatal` for
the rest of the server process.

## Verification

All lifecycle verification uses isolated temporary worktrees, TerminalHost state, activity stores,
and dynamically allocated non-production ports. Tests never bind production port 5000 or invoke
`treemon.ps1 deploy`, `start`, `stop`, or `restart`.

- `EmbeddedTerminalUpdateTests` covers staged availability, immediate `Updating` acknowledgement,
  the one-pass happy transition, omission of terminals without captured durable sessions, rejection
  of new starts/cleanup and idempotent duplicates while locked, cleanup-held queueing, automatic
  single transaction after acknowledged release, safe queue/age/correlation logs, and permanent
  fatal state after the first failure.
- `TerminalOwnershipQueryTests` covers active and idle durable-session selection, stale-session
  exclusion, one latest conversation per terminal, and no literal agent `resume` prompt.
- Dashboard browser tests cover the exact one-button copy and position immediately before Sort,
  hidden Unavailable state, immediate `Updating` acknowledgement, uninterrupted polling-driven
  blocking overlay, visible spinner animation, success removal, and non-dismissible fatal recovery
  overlay. Elmish tests cover every terminal action/shortcut message, subscription suppression, late
  focus, and queued-launch suppression while locked.
- Existing launch-routing tests prove every embedded and agent-bearing API route reaches the shared
  `TerminalLaunch`/`EmbeddedTerminal` boundary.
- Run the focused tests first, then `dotnet test src/Tests/Tests.fsproj --filter "Category=Fast"`.

## Decisions

- **One separately running F# host:** ordinary Treemon restarts remain control-plane events while
  terminals and their owned process trees stay under one small host.
- **Job Object ownership:** ttyd is assigned before resume, and exact survivor cleanup remains the
  authoritative explicit-close fallback.
- **One upstream and one browser writer per terminal:** attachment replacement preserves the shell
  without defining multi-writer input semantics.
- **Bounded raw replay:** reconnect restores useful recent output and terminal modes without
  persisting terminal content or serializing complete screen state.
- **Stable versioned control API:** compatible Treemon servers reconnect; incompatible deployment is
  refused while the old host owns terminals.
- **Typed launch routing:** every product-level embedded start crosses `TerminalLaunch` and the one
  `EmbeddedTerminal` mailbox gate.
- **User action over automatic eligibility:** staged availability produces one standard header
  **Apply TerminalHost update** button immediately before Sort, with interruption detail in its
  tooltip rather than another status label. Treemon never waits for an idle window or surprises the
  user with replacement.
- **Host-wide shutdown over per-session orchestration:** clicking the action consents to interrupting
  active durable sessions; one host request closes every owned process tree and avoids partial bridge
  shutdown split-brain.
- **Durable sessions only:** the snapshot selects one greatest-activity durable conversation per
  current terminal. Terminals without an open durable session and additional conversations are not
  recreated.
- **Forward-only fatal failure:** the first failed operation enters permanent `Fatal`, retains the
  lock and overlay, and requires manual redeploy/restart. No retry, rollback, recovery, or second host
  generation can repeat or disguise a partial transaction.
- **Mailbox serialization is the atomic lock:** `WaitingForCleanup` is installed before the request
  is acknowledged, so later mutations are rejected while earlier cleanup completion/release messages
  drain. Snapshot capture starts in one serialized turn after the reservations reach zero; host I/O
  then runs asynchronously under `Updating`.
- **Captured current state:** current registry membership plus current open exact-session rows fully
  define the restart set.
- **Direct session selector:** recreated Copilot sessions use `--session-id=<id>` so the durable
  identity exists before extensions join. No free-form recovery prompt is sent.
- **External production ownership:** deploy/restart remains forbidden from an embedded terminal
  because the caller would inherit the host's kill-on-close Job Object.
- **Candidate-first deployment and plain staging:** inactive complete bundles are staged before server
  replacement; the dashboard transaction consumes that existing staged executable without adding a
  second bundle or journal mechanism.
- **Bundle content over repository revision:** staging uses the complete non-PDB host fingerprint,
  while source-revision metadata is excluded from the nested publish. Unrelated commits cannot offer
  an update, but a changed host assembly or runtime file does.
- **Native-only card session state:** embedded terminals do not change `HasActiveSession`; coding-tool
  activity remains the agent indicator.
- **Cleanup sequencing before host-wide replacement:** delete/archive retains its canonical-path
  reservation until mailbox-acknowledged release. An accepted update locks all new terminal
  mutations immediately, waits for those existing reservations, then starts the complete-host
  replacement once. Cleanup is a sequencing dependency, not a rejection reason, and no lease TTL
  can clear work that may still be running.

## Key Files

| File | Purpose |
|---|---|
| `src/TerminalHostLayout/Layout.fs` | Shared state/staging paths, version-directory grammar, executable names, and required host bundle members |
| `src/TerminalHost/TerminalHost.fsproj` and `src/TerminalHost/*.fs` | F#/.NET host project: Job Object launch, ttyd ownership, proxy, replay, registry, and control API |
| `src/Server/TerminalHostProcess.fs`, `TerminalHostEndpoint.fs`, `TerminalHostManifest.fs`, `TerminalHostClient.fs`, and `TerminalHostReplacement.fs` | Host process/identity, shared loopback endpoint shape, authenticated control client, staged-executable selection, and the one-pass update sequence |
| `src/Server/TerminalLaunch.fs` | Sole product-level launch policy and native-versus-embedded backend selection |
| `src/Server/EmbeddedTerminal.fs` | Terminal lifecycle mailbox, cleanup reservation, command-capable start, and authoritative snapshot reconciliation |
| `src/Server/WorktreeCleanup.fs` | Product-level explicit terminal/worktree teardown, graceful exact-session coordination, host close, and closure publication |
| `src/Server/ProcessIdentity.fs` | Shared exact PID/start-tick identity and resolver used by activity ingress and process lifecycle checks |
| `src/Server/SessionActivity.fs` | Per-process instance lifecycle fold, validated session/origin identities, liveness, and closure |
| `src/Server/LifecycleDiagnostics.fs` | Bounded structured presence, bridge, graceful-shutdown, exact-closure, and teardown diagnostics |
| `src/Server/SessionActivityProtocol.fs`, `SessionActivityIngestion.fs`, and `SessionActivityService.fs` | Exact activity wire parsing, fold application, acknowledged presence, bounded live state, and mailbox-serialized terminal ownership queries |
| `src/Server/TerminalSessionActivity.fs` | Exact terminal-origin projection for tab activity and distinct live SessionIds, live-terminal reuse, and one durable restart session per terminal |
| `src/Server/SessionActivityStoreSchema.fs` and `SessionActivityStore.fs` | Durable process-instance schema/migration, resume identity, event dedupe keys, and retention |
| `src/Extension/reporting/extension.mjs` | Acknowledged process presence, passive activity, heartbeat, background lifecycle, and shutdown reports |
| `src/Extension/extension.mjs`, `shutdown-endpoint.mjs`, and `src/Server/SessionBridge.fs` | Shared exact registration plus capability-guarded graceful shutdown endpoint and bounded typed server control client |
| `src/Server/CodingToolCli.fs` | Provider-specific exact-session resume command construction |
| `src/Server/Program.fs` | Host/API lifecycle and TerminalHost restart-session query wiring |
| `treemon.ps1` | Published host staging, deployment compatibility preflight, and embedded-terminal production-lifecycle guard |
| `src/Client/AppTypes.fs` and `src/Client/App.fs` | Reconnect view generation, Elmish messages, guarded load completion, and focus effect |
| `src/Client/TerminalPane.fs` | Terminal tabs, mounted iframes, activity labels, live SessionIds, Canvas-driven selection without pane focus, and interruption UI |
| `src/Tests/EmbeddedTerminalTests.fs` and `src/Tests/TerminalHostTests.fs` | Isolated host lifecycle plus update transaction, command delivery, control rejection, UTF-8 frame boundaries, crash, security, and cleanup coverage |
| `src/Tests/SessionIsolationVerifier/` and `scripts/verify-session-isolation.ps1` | Durable five-phase concurrent same-session process-isolation harness and clean-checkout runner |
| `src/Tests/WorktreeApiLaunchTests.fs` | Worktree API typed-operation routing, exact result identity, control-free AgentDoc/SystemView/create-worktree prompt commands, and post-fork launch ordering |
| `src/Tests/EmbeddedLaunchEndToEndTests.fs`, `src/Tests/TestAgentRecorder`, and `scripts/verify-embedded-launch-routing.ps1` | Reproducible isolated real-host launch matrix, exact argv recorder, raw route evidence, forced-delivery rollback, native HWND preservation, and exact cleanup |
| `src/Tests/TerminalPaneTests.fs` and `src/Tests/WorkspaceLayoutTests.fs` | Terminal selection, reconnect generation/focus guards, and selected-only iframe replacement |
| `src/Tests/SessionActivityServiceTests.fs` | Exact terminal ownership, activity labels, and provider-specific durable restart snapshot coverage |
| `scripts/treemon-deployment.test.ps1` | Isolated staging, compatibility-preflight, candidate-first ordering, and embedded-terminal lifecycle refusal coverage |

## Related Specs

- `docs/spec/session-status-push.md` — authoritative process-instance reporting, persistence,
  liveness, terminal origin, and monotonic closure; this spec owns shutdown policy and terminal
  teardown.
- `docs/spec/native-session-management.md` — explicit card `>` / Enter Windows Terminal behavior.
- `docs/spec/worktree-monitor.md` — worktree lifecycle and dashboard integration.
