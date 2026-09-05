# Session Status via Push Model

## Overview

Treemon receives Copilot CLI lifecycle and activity events through a passive reporting extension and
folds them into durable per-process session-instance state. Instances sharing one durable Copilot
`SessionId` are collapsed only at worktree and Resume boundaries. Worktree cards, live Overview
agent groups, activity titles, context usage, terminal ownership, and session resume all use this
shared state; no session-log parsing remains.

## Goals

- Derive status and card activity from explicit SDK events rather than log-file timing heuristics.
- Keep reporting passive: no tools, prompts, or transcript changes.
- Preserve live status, footer activity, messages, context usage, and resume identity across server
  restarts.
- Support multiple concurrent sessions in one worktree without losing per-process activity.
- Represent concurrent CLI processes for the same durable Copilot session independently so their
  lifecycle state, liveness, and terminal origins cannot overwrite each other.
- Keep a parent session Working while any reported background agent is still active.
- Keep liveness independent from representative-session selection.
- Retain an optional exact terminal origin for sessions launched inside a host-owned terminal.
- Record monotonic exact-instance closure from live SDK shutdown or authoritative terminal teardown
  while retaining durable conversation history for later Resume.
- Let auto-sync target an existing session through `SessionBridge` before launching another CLI.
- Filter synthetic user-channel content before it can change durable session state.
- Bound untrusted input and durable storage while keeping the fold deterministic.

## Expected Behavior

### Status and liveness

| Situation | Worktree status | Dot |
|---|---|---|
| An open session is actively running | Working | red |
| An open session is waiting on `ask_user` | WaitingForUser | yellow |
| All open sessions are between turns | Idle | blue |
| No session has reported recently | NoSession | grey |

- A session status is only `Working`, `WaitingForUser`, or `Idle`; `NoSession` exists only after
  collapsing a worktree with no open sessions.
- `turn_start`, genuine user prompts, and assistant messages set Working. An input request opens an
  independent wait clock. Input completion, a genuine reply, or a subsequent assistant message
  closes it. `turn_end` and `session.idle` set the base status to Idle, but an unresolved newer
  request still projects as WaitingForUser. Otherwise, an active background agent overlays Working
  on an Idle parent until its matching completion or failure. WaitingForUser has higher priority
  than background Working. There is no durable Done state.
- Background lifecycle is keyed by the SDK `toolCallId`. Start and terminal timestamps merge
  independently, so duplicate and out-of-order delivery is deterministic. Sub-agent content remains
  excluded; only explicit Copilot background-agent lifecycle affects the parent status.
- Each CLI process is a distinct session instance identified by the exact Copilot process PID and
  process-start identity resolved when its reporting extension connects. The complete lifecycle
  fold, activity fields, liveness, closure, and terminal origin belong to that instance. A durable
  Copilot `SessionId` may therefore have several simultaneous instances without shared mutable
  state.
- An instance is open while it has not been explicitly closed and its latest server-received
  presence observation is newer than `openWindow` (3 minutes). A defensive `stalenessTimeout`
  (5 minutes) can downgrade stale active conversation state, and `idleWindow` (2 hours) bounds the
  in-memory conversation map.
- The extension sends an acknowledged, idempotent `session_present` bootstrap until the server has
  recorded the instance, then liveness-only heartbeats every 60 seconds. Presence creates the
  instance even when the session has no title or replayable lifecycle event; heartbeats update only
  that instance's receipt-time liveness and never create an anonymous conversation. Each endpoint
  starts its own heartbeat cadence immediately after acknowledging presence; its replay and another
  endpoint's presence or replay cannot delay that cadence. A heartbeat transport failure invalidates
  that endpoint's in-flight replay generation and restarts its presence handshake.
- A live `session.shutdown` stops heartbeats and closes its exact process instance. Terminal
  lifecycle orchestration also closes every exact instance whose terminal teardown and survivor
  verification completed, including when graceful SDK shutdown was unavailable, rejected, or timed
  out. Both paths are monotonic: later reports from that closed process cannot reopen it. Resuming
  the same durable session creates a new process identity and therefore a new open instance.
- Accepted usage reports preserve conversation state without becoming lifecycle events; instance
  presence remains on the dedicated presence path.

### Multiple sessions and card fields

- Openness and status are derived from open instances. The active winner is the open non-Idle
  instance with the greatest `(UpdatedAt, SessionId, ProcessIdentity)`. Presence recency therefore
  cannot replace the instance that most recently performed real activity.
- If no open instance is active, all open instances collapse to Idle; with no open instances the
  worktree is NoSession.
- The footer is independent of the dot. Skill, activity, and last messages come from the active
  winner or otherwise the session with the greatest `(UpdatedAt, SessionId)`, including the durable
  retained representative. They therefore survive Idle, NoSession, and server restart.
- Card activity is the freshest source-tagged value from `assistant.intent` or the session title.
  Intent is optional enrichment; the title is the reliable fallback and is restored from
  `metadata.snapshot().summary` when no live title event arrived during startup.
- User-message projection uses `UserMessageFormatting`: runtime `<system_reminder>` content is
  hidden, `[canvas] ` payloads retain a Canvas glyph and readable action text, and the same
  projection is used for duplicate suppression against activity text.
- `WorktreeStatus.Sessions` contains one marker for every open physical instance after freshness
  adjustment, ordered Working -> WaitingForUser -> Idle, then by `SessionId` and process identity
  within an equal status. Each marker carries an opaque exact-instance ID for stable client keys,
  plus its own skill and context usage, so duplicate processes for one durable conversation remain
  visible instead of collapsing into one marker.
- The live Overview counts and groups physical instances independently. One worktree can contribute
  processes to several activity groups at once.
- `CodingToolSince` is captured whenever the collapsed worktree status changes and remains stable
  across liveness-only heartbeats and sibling-instance updates that leave that status unchanged. A
  process that re-presents after its prior openness window starts a new transition. The Overview
  drill-down shows that timestamp only when the worktree has exactly one open instance; members of a
  multi-instance worktree omit duration because a collapsed worktree transition cannot represent
  each physical process's category transition.
- Auto-sync defers entirely while any open session is mid-turn or has been idle for less than
  `settleWindow` (30 s). Otherwise it takes the greatest-`UpdatedAt` open session that has settled,
  and only then a retained identity when no session is open. That ordering keeps any fallback prompt
  on an existing bridge rather than launching a second CLI. An open target is delivered to its
  exact process registration; a retained offline identity is used only for a guarded launch.

### Terminal origin

- Presence reports carry an optional `TerminalSessionId` sourced from the reporting process's
  inherited `TREEMON_TERMINAL_SESSION_ID`. TerminalHost injects that value into each owned terminal,
  so it is present only for Copilot instances launched inside that terminal; sessions launched
  elsewhere omit it.
- The origin is stored with the process instance, not the durable conversation row. Terminal
  lifecycle code therefore joins one exact CLI process to one host-owned terminal, while two
  concurrent processes for the same Copilot `SessionId` remain distinct. Treemon never infers
  terminal ownership from worktree path.
- The embedded-terminal API uses the bounded live-session projection to label each tab with the
  representative exact session's freshest display-safe activity. An active session wins; otherwise
  the most recently active live session is representative. Activity uses the same reported-intent
  or session-title choice as the worktree card; when neither exists the tab keeps its numbered
  fallback.
- `TerminalSessionId` is process-instance attribution metadata for exact host-terminal joins. It
  does not participate in conversation status folding or representative ordering. Explicit Resume
  uses it only after selecting the durable target session: if an open instance of that exact
  session exists in a running terminal, the existing terminal is returned instead of launching
  another process.

### Context usage, resume, and restart

- `session.usage_info` updates a durable per-process-instance context gauge without changing status. Its
  ordering clock is separate from lifecycle ordering, so older delayed gauges cannot replace newer
  values and usage cannot block a lifecycle transition.
- Usage values and their ordering timestamp are stored on `session_instances`; usage is not appended
  to `activity_events` and cannot establish presence for an unknown process identity.
- Explicit worktree Resume selects the greatest `(UpdatedAt, SessionId)` from all durable sessions
  for the worktree, regardless of current status or heartbeat recency. Sessions older than the live
  window remain manually resumable until retention removes them. Automatic TerminalHost replacement
  is narrower: it resumes only an open exact-origin process instance, as defined in
  `docs/spec/embedded-terminal.md`.
- On server start, durable conversations and recently observed instances are loaded from SQLite.
  Durable titles, intents, skill, footer messages, context gauges, and their ordering clocks are
  restored. The activity endpoint is made available before replacement reconciliation begins, and
  surviving extensions retry `session_present` until acknowledged. A recently open exact instance
  with a current terminal origin remains pending reconciliation until the same process identity
  re-presents, that exact process is proven dead, its terminal origin leaves the authoritative
  registry, or `openWindow` expires. Pending reconciliation gates automatic replacement without
  introducing another lifecycle status. A process proven dead is monotonically closed and removed
  from the scheduler's exact live collection; missing-origin and expired rows simply stop
  participating in terminal ownership. Retained conversations outside the live window remain
  available for footer and explicit Resume selection, and a closed identity cannot reopen.
- Background-agent clocks are process-instance-local and durable within retention. Restart restores
  unfinished background work for each exact identity, so an Idle parent remains effectively Working
  until its matching terminal event or stale-gap cleanup.

## Technical Approach

### Reporting extension

The passive reporting package joins the current Copilot session and sends the parent process PID.
`extension.mjs` owns the process/environment and bounded HTTP boundary;
`reporting-runtime.mjs` owns per-endpoint acknowledged presence, replay, reconnect, heartbeat, and
live shutdown. It forwards `subagent.started`, `subagent.completed`, and `subagent.failed` as
explicit background lifecycle, then drops all other sub-agent content. It also drops skill-context
injections, blank messages, invalid usage gauges, and invalid or overlong background tool-call IDs
before sending. The canvas bridge's shared `request-body.mjs` reader caps `/inject` and `/_message`
at 1 MiB and `/shutdown` at 4 KiB, and rejects request-stream errors.

The extension maps lifecycle, skill, message, `assistant.intent`, `session.title_changed`, ask-user,
background-agent, and usage events onto the closed wire contract. Background reports use
`background_agent_started` / `background_agent_finished` with an opaque `toolCallId` of at most 512
UTF-16 code units. It forwards ask-user request, completion, and idle events as facts; the server's
persisted request/completion clocks resolve their effective status independently of delivery order.
Every report carries the parent Copilot PID and the optional inherited
`TREEMON_TERMINAL_SESSION_ID`. Each configured Treemon endpoint tracks presence independently:
transport failure, an explicitly retryable presence acknowledgement, or an ordinary
`recorded=false, retryable=true` result re-establishes presence and replays only that endpoint.
An unmonitored endpoint or an unresolvable parent process is terminal for that destination and does
not enter a retry storm. A monitored ordinary `recorded=false, retryable=false` result remains
best-effort delivery, preserving intentionally ignored reports such as system reminders. Reports
from pre-deploy extensions that omit the parent PID are rejected and do not gate replacement. The
server acknowledges `session_present` only after its mailbox has persisted the exact instance;
ordinary event delivery remains idempotent after that bootstrap. The live `session.shutdown` event
stops heartbeat emission and retains `session_closed` behind any in-flight unacknowledged presence.
Acknowledged presence is followed immediately by closure; transport or retryable outcomes continue
the same idempotent presence report until definitive, while a permanent or unmonitored rejection
terminates that destination without closure. Historical shutdown events are not replayed as current
closure.

Subscriptions are attached before replay. The first successful `getEvents()` result is mapped
through one runtime-scoped compact last-write-wins accumulator and cached for the process lifetime.
It retains the latest lifecycle, skill, usage, title, intent, user and assistant message facts;
independent ask-user request/completion clocks; and active or recently completed background-agent
clocks. Every endpoint replay merges that shared historical snapshot with a fresh compact
current-process snapshot. A failed history read is not cached, so a later reconnect can retry it.
Endpoint presence, retry, heartbeat, and delivery state remain independent, and heartbeat delivery
uses a separate lane from historical replay.

After subscriptions and replay are active, the extension reads
`session.rpc.metadata.snapshot().summary` in a non-blocking background task and emits
`title_bootstrap` only when no nonblank live title was seen. A failed or slow metadata request cannot
block heartbeat or normal reporting. Reports fan out to `TREEMON_PORTS`, then `TREEMON_PORT`, then
port 5000, allowing production and validation instances to observe the same session.

### Domain and ingestion

`ProcessIdentity.fs` owns the generic PID/start-tick identity, its opaque client encoding, the
injectable resolver contract, and the default operating-system resolver. `SessionActivity` owns
validated durable session and terminal-origin identities, the closed event union, and the pure
fold. The fold retains status, skill, intent, title, last messages, context usage, and independent
ask-user request/completion clocks. Repeated identical intent/title text keeps its original change
timestamp; older values cannot replace newer ones. `effectiveActivity` chooses the newer intent or
title while preserving its `AgentActivity` source.

The fold also retains process-local background lifecycle clocks per `toolCallId`. A background
agent is active only when its latest start is newer than its latest terminal event. Active clocks
keep an otherwise-Idle parent Working; an outstanding ask-user request still wins. Background
events never replace parent-authored skill, title, intent, or footer messages.

`POST /api/session/activity` validates the DTO, provider, known worktree, parent Copilot PID,
event-specific fields, and CSRF origin. The server resolves the PID's process-start identity and
uses server receipt time for presence while retaining bounded producer timestamps for lifecycle
ordering. Free text is bounded before persistence. An optional terminal origin must be the
canonical 32-hex TerminalHost session ID and is normalized to lowercase; blank values mean no
origin. Exact process resolution is one injected shared boundary used by activity ingestion,
SessionBridge registration, shutdown waiting, and survivor verification so every subsystem compares
the same PID/start identity and PID-reuse behavior remains deterministic in tests.

Runtime `<system_reminder>` messages arrive through the genuine `user.message` channel, so the
server classifies them after validation and the known-worktree guard but before the single-writer
mailbox. A monitored reminder returns `recorded=false, monitored=true`; it cannot replace the last
user message, complete an ask-user wait, clear a skill, change status, or enter durable events.
Persisted reminders from older versions are also hidden by the shared footer projection.

A `SessionActivityService` mailbox is the single writer. Event idempotency is scoped to the process
instance so a newly resumed CLI can replay a durable session's existing event IDs into its own
fold. Lifecycle, activity, interaction, usage, bootstrap, presence, closure, and liveness reports
use independent ordering paths:

- Lifecycle reports append an idempotent event record and update the lifecycle last-write-wins
  clock. Older reports may be retained for event-ID deduplication but cannot regress live state.
- Intent and live title reports append their accepted event while resolving content by message time
  without blocking older lifecycle transitions.
- Ask-user request/completion reports merge their monotonic clocks and advance `UpdatedAt` only
  forward, so arrival order cannot change the effective wait.
- Background lifecycle reports merge start/terminal clocks independently, append their event IDs,
  and advance `UpdatedAt` only forward. Completed clocks remain for the five-minute late-event
  window; stale lifecycle reports are ignored so an old start cannot resurrect a finished agent.
- Title bootstrap hydrates durable state without appending an event or advancing lifecycle time.
- Usage persists only the latest gauge on its own ordering clock.
- Presence resolves the exact Copilot process start identity, creates or refreshes one instance with
  server receipt time, advances the terminal-origin activity epoch, and replies only after the
  store update succeeds.
- Heartbeats refresh only a known, still-open instance. They affect openness and automatic
  replacement eligibility but never order footer selection or explicit Resume ownership, which use
  conversation `UpdatedAt`.
- Closure is monotonic for one process identity whether it came from live `session.shutdown` or
  terminal-authoritative teardown. A report from that same closed process remains closed even when
  it arrives later; only a different process identity can create a new instance. Graceful,
  timeout, and survivor-cleanup outcomes remain terminal-orchestration diagnostics rather than
  changing the lifecycle fold.

Ingestion paths consult the exact process-instance row whenever prior state is needed. This
preserves concurrent process state without allowing one CLI to steal another process's terminal
origin, mark another instance Idle, or reopen an explicitly closed instance. Before advancing an
instance after a stale gap, the service clears its old background clocks.

The mailbox also maintains a process-local monotonic activity sequence per terminal origin. An
instance report stamps its terminal origin, so presence or closure changes that terminal's epoch.
Its narrow query accepts the complete current authoritative terminal-ID set and returns open
instances joined to durable conversation state plus their maximum epoch.
`TerminalSessionActivity` derives effective state and selects the greatest-activity open instance
per terminal. Concurrent instances of one durable session cannot overwrite each other's origin or
liveness. Unrelated origins, closed instances, stale instances, and sessions with no origin cannot
change the replacement join result. Hourly retention removes old instance rows and epochs not
backed by retained instances or the latest authoritative registry; pruning never resets the global
epoch sequence.

Exact-instance ingestion accepts only a resolvable parent process identity. Reports without one are
rejected rather than folded under a synthetic identity.

`LifecycleDiagnostics` receives only validated `SessionId`, `TerminalSessionId`, exact
`ProcessIdentity`, bounded counts, and closed outcome unions. A successful presence write emits
`first_seen` or `reconnected` only after the exact row is durable. Presence and process-keyed bridge
registration also report normal distinct-session concurrency separately from neutral
same-`SessionId` physical multiplicity. Identity and session lists show at most eight sorted values
while retaining full total and omitted counts. Paths, URLs, capabilities, prompts, messages, tokens,
environment values, exception text, and raw reports are not diagnostic inputs.

### Persistence

`SessionActivityStore` uses SQLite WAL with short-lived connections:

- `session_instances` stores the complete folded state keyed by exact Copilot process identity:
  durable session identity, optional terminal origin, worktree, provider, lifecycle state, activity,
  messages, context gauge, independent clocks, receipt-time liveness, and monotonic closure.
- Existing `session_status` rows migrate into bounded `retained_sessions` history keyed by durable
  `SessionId`. These rows preserve footer and explicit Resume data but never represent a process,
  establish liveness, or participate in automatic replacement. The table is migration-only: new
  exact sessions remain in `session_instances`, and retention only removes old migrated history.
- `worktree_representatives` stores one materialized footer row plus its ordering key per worktree.
  It is the bounded read source for dashboard and auto-sync retained-session projection.
- `activity_events` retains accepted history-bearing events under process-instance plus event ID so
  retrying one process remains idempotent while a resumed process can replay the same durable
  session history into its independent fold. Canonical Overview history uses direct 30-second
  snapshots and never reads this table.
- Background-agent start/finish clocks are stored on the process instance so server restart cannot
  misclassify an Idle parent with active delegated work.

Store construction creates `session_instances`, copies legacy one-row-per-session history
idempotently into `retained_sessions`, transactionally rebuilds `activity_events` with a
process-instance key, drops the obsolete `session_status` table, and rebuilds one materialized
`worktree_representatives` row per worktree from exact and migration-only history. Legacy event rows
are discarded because their exact producer identity cannot be recovered and their folded state is
already preserved in `retained_sessions`. Runtime exact-instance writes update the winning
representative in the same transaction. Source indexes are present before the startup
representative rebuild. The terminal-origin index follows
`(terminal_session_id, updated_at DESC, session_id DESC)`; its leading origin key supports
retained-origin scans used when pruning process-local activity epochs.

Event append/status upsert and context updates are transactional and reread the authoritative
persisted row. Hourly retention bounds durable session and event data, then rebuilds the
representative table so a removed winner falls back to surviving history. Dashboard and auto-sync
reads scan only that worktree-keyed table rather than ranking the 60-day process-instance history.
Overview history remains independent. `SqliteStorage` owns shared UTC timestamp encoding/parsing and
immutable reader draining. Removed Overview rollup, liveness, task-snapshot, staging, and
reconstruction infrastructure is not part of this store.

### Worktree projection

`SchedulerState.SessionInstances` is keyed by exact process identity and never deduplicates a
durable `SessionId`. `CodingToolStatus.collapseByWorktree` is the single projection from those
instances to card fields. `WorktreeApi` derives markers and aggregate status only from open exact
instances, then separately joins each worktree's materialized greatest durable
`(UpdatedAt, SessionId, ProcessIdentity)` representative for footer content. `retained_sessions` and
closed prior process instances remain eligible only for footer and explicit Resume history; they
have no liveness, terminal-origin, or process-address fields in the application read model.

The remoting contract exposes `toggleAutoSync`. When enabled and the branch falls behind, `AutoSync`
reads open exact instances separately from retained history. A session mid-turn — or one that went
idle within the settle window — makes the worktree busy, and the observation is deferred without
delivering anything. Otherwise — no session, or one that has settled or is waiting on its user —
AutoSync attempts the bounded mechanical path defined in `docs/spec/worktree-monitor.md`. Agent
fallback addresses the selected open process through `SessionBridge.SendTarget.ExactProcess`;
retained identity is consulted only when no process is open and can cause only a guarded launch.
Transient delivery failure queues the exact-process prompt for retry. The passive reporting
extension never originates prompts.

The projection keeps the activity source through the `AgentActivity` union and exposes every open
session for status/context rendering. Overview snapshot capture uses the same live session
projection but persists the complete count-only aggregate independently; see
`docs/spec/overview-activity-history.md`.

Terminal origin remains on the process-instance record for exact terminal joins and is not folded
into lifecycle status.

## Decisions

| Decision | Choice |
|---|---|
| Source of truth | Push events only; log-parsing detectors are removed. |
| Session model | Working, WaitingForUser, Idle; NoSession only at worktree collapse. |
| Synthetic messages | Filter server-side before ingestion with the shared user-message classifier. |
| Ask-user ordering | Persist independent request/completion clocks; do not keep lifecycle state in the extension. |
| Liveness | Acknowledged presence and heartbeats update one process instance using server receipt time; each endpoint starts heartbeat on acknowledgement and sends it independently of replay; usage does not establish presence. |
| Representative ordering | Use instance `(UpdatedAt, SessionId, ProcessIdentity)`; liveness gates openness and automatic replacement eligibility but never replaces lifecycle ordering. |
| Multiple instances | Preserve concurrent CLI processes for one durable session as separate full fold rows; never let one event or heartbeat replace another instance's status, origin, or closure. |
| Ownership boundary | Session activity owns reporting, exact-instance state, liveness, and monotonic closure; embedded-terminal orchestration owns shutdown policy, authoritative teardown, rollback, and survivor cleanup. |
| Startup reconciliation | Keep each recently open terminal-owned instance pending until that exact identity re-presents, dies, loses its terminal origin, or reaches `openWindow`; never use one global startup delay. |
| Background agents | Persist per-tool start/finish clocks on each exact instance; WaitingForUser outranks background Working; stale-gap cleanup bounds abandoned clocks. |
| Footer | Decouple from the status dot and merge a transactionally maintained, worktree-keyed durable representative. |
| Activity | Use freshest source-tagged intent/title; bootstrap title from metadata, never infer intent. |
| Context usage | Persist the last-known gauge and ordering timestamp; do not append it to activity events. |
| Persistence | Store exact process-instance folds and instance-scoped events separately from bounded retained conversation history; rebuild legacy primary keys transactionally. |
| Overview history | Capture canonical direct snapshots every 30 seconds; never reconstruct from activity events. |
| Auto-sync | Wait while any open session is working or has not settled; otherwise prefer the settled open bridged session, then retained identity only when no session is open; launch only when delivery has no live target. |
| Explicit Resume | Query durable most-recent activity identity, then use bounded live exact-origin state only to reuse that target's running terminal instead of launching a duplicate process. |
| Terminal origin | Validate and persist optional `TerminalSessionId` on the process instance; a focused terminal module derives exact ownership for tab activity, Resume idempotency, and replacement, never from worktree inference. |
| Explicit close | Live `session.shutdown` or confirmed terminal teardown closes one exact process instance monotonically; heartbeat expiry remains the crash fallback. |
| Window state | Keep terminal/window `HasActiveSession` separate from push-session openness. |

## Key Files

| File | Role |
|---|---|
| `src/Extension/reporting/extension.mjs` | Copilot SDK join, parent-PID/environment capture, and bounded HTTP transport. |
| `src/Extension/reporting/reporting-runtime.mjs` | Independent endpoint presence/retry state, shared compact reconnect replay, per-endpoint heartbeat, metadata bootstrap, and live shutdown. |
| `src/Extension/reporting/reporting-core.mjs` | Pure wire mapping plus the compact replay accumulator. |
| `src/Server/ProcessIdentity.fs` | Generic exact PID/start-tick identity, opaque client encoding, injectable resolver contract, and default operating-system resolver. |
| `src/Server/SessionActivity.fs` | Validated session and terminal-origin identities, event domain, pure fold, terminal-origin epoch state, background lifecycle, effective activity/status, freshness, and active selection. |
| `src/Server/LifecycleDiagnostics.fs` | Bounded structured lifecycle events over validated session, terminal, and process identities only. |
| `src/Server/SessionActivityProtocol.fs` | Bounded activity wire DTO parsing and exact-instance event mapping. |
| `src/Server/SessionActivityIngestion.fs` | Exact-instance fold application, independent ordering paths, scheduler publication, and startup reconciliation. |
| `src/Server/SessionActivityService.fs` | Known-worktree filtering, acknowledged presence, mailbox lifecycle, retention, and raw exact-origin queries. |
| `src/Server/TerminalSessionActivity.fs` | Exact owned-session and startup-reconciliation projection for embedded-terminal tab activity and TerminalHost replacement policy. |
| `src/Server/UserMessageFormatting.fs` | System-reminder classification and user/canvas footer projection. |
| `src/Server/SqliteStorage.fs` | Shared SQLite UTC timestamp encoding/parsing and immutable reader draining. |
| `src/Server/SessionActivityStoreSchema.fs` | Transactional exact-instance schema creation, retained-history migration, worktree-representative storage, legacy retirement, and event-key rebuild. |
| `src/Server/SessionActivityStore.fs` | Exact process-instance persistence, bounded retained-footer reads, representative maintenance, and retention. |
| `src/Server/CodingToolStatus.fs` | Per-worktree collapse, heartbeat-independent activity/footer projection, and resume lookup. |
| `src/Server/SchedulerState.fs` | Exact live process-instance state and collapsed `CodingToolSince` transitions. |
| `src/Server/WorktreeApi.fs` | Card assembly, retained-session merge, direct snapshot history API, and resume command wiring. |
| `src/Server/SessionBridge.fs` | Process-keyed session registration, durable-session/worktree lookup, separate poll registration, exact prompt/shutdown delivery, retry queue, and bridge liveness. |
| `src/Server/WorktreeCleanup.fs` | User-authorized terminal/worktree teardown sequencing, host cleanup, and exact survivor verification. |
| `src/Server/TerminalSessionCleanup.fs` | Session-activity-backed exact shutdown requests, post-host reconciliation, monotonic closure recording, and lifecycle diagnostics for terminal cleanup. |
| `src/Extension/extension.mjs`, `shutdown-endpoint.mjs`, and `request-body.mjs` | Exact bridge identity registration, shared bounded request reading, and capability-guarded loopback routine shutdown. |
| `src/Server/AutoSync.fs` | Delivery-aware session selection and guarded sync-prompt fallback launch. |
| `src/Shared/Types.fs` | `AgentActivity`, context usage, exact-instance marker IDs, and worktree wire types. |
| `src/Shared/WorktreeApi.fs` | Remoting contract, including `toggleAutoSync` and direct Overview history. |
| `src/Shared/OverviewData.fs` | Shared per-physical-instance Overview grouping. |
| `src/Client/OverviewPresentation.fs` | Client-only Overview selection and visual mappings. |
| `src/Client/CardViews.fs` | Status dots, activity/footer text, and exact-instance context display. |

## Related Specs

- `docs/spec/embedded-terminal.md` - authoritative shutdown policy, terminal teardown, replacement,
  rollback, and exact survivor cleanup.
- `docs/spec/worktree-monitor.md` - dashboard architecture and refresh model.
- `docs/spec/beads-overview-band.md` - live task and agent aggregation with per-group membership.
- `docs/spec/overview-activity-history.md` - durable canonical Overview snapshots.
- `docs/spec/resume-last-session.md` - resume command behavior.
- `docs/spec/native-session-management.md` - terminal/window liveness, distinct from push openness.
