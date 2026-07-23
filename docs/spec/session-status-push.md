# Session Status via Push Model

## Overview

Treemon receives Copilot CLI lifecycle and activity events through a passive reporting extension and
folds them into durable per-session state. Worktree cards, live Overview agent groups, activity
titles, context usage, and session resume all use this shared state; no session-log parsing remains.

## Goals

- Derive status and card activity from explicit SDK events rather than log-file timing heuristics.
- Keep reporting passive: no tools, prompts, or transcript changes.
- Preserve live status, footer activity, messages, context usage, and resume identity across server
  restarts.
- Support multiple concurrent sessions in one worktree without losing per-session activity.
- Keep liveness independent from representative-session selection.
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
  request still projects as WaitingForUser. There is no durable Done state.
- A session is open while its latest event or liveness observation is newer than `openWindow`
  (3 minutes). A defensive `stalenessTimeout` (5 minutes) can downgrade stale active state, and
  `idleWindow` (2 hours) bounds the in-memory session map.
- The extension sends liveness-only heartbeats every 60 seconds. The server accepts them only for a
  session with existing durable state, updates `last_seen` without changing `UpdatedAt`, status, or
  `activity_events`, and can thereby rehydrate a retained session omitted from the restart live
  window.
- Accepted usage reports also advance `last_seen` without becoming lifecycle events.

### Multiple sessions and card fields

- Openness is determined from `LastSeen`, but the active winner is the open non-Idle session with
  the greatest `(UpdatedAt, SessionId)`. Heartbeat recency therefore cannot replace the session that
  most recently performed real activity.
- If no open session is active, all open sessions collapse to Idle; with no open sessions the
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
- `SessionStatuses` contains every open session after freshness adjustment, ordered
  Working -> WaitingForUser -> Idle. Each entry retains its own skill and context usage; `LastSeen`
  may order equal-status markers but never the shared footer or resume identity.
- The live Overview counts and groups sessions independently. One worktree can contribute sessions
  to several activity groups at once.
- `CodingToolSince` is captured when the collapsed worktree status changes and remains stable while
  Idle heartbeats advance `last_seen`.

### Context usage, resume, and restart

- `session.usage_info` updates a durable per-session context gauge without changing status. Its
  ordering clock is separate from lifecycle ordering, so older delayed gauges cannot replace newer
  values and usage cannot block a lifecycle transition.
- Usage values and their ordering timestamp are stored on `session_status`; usage is not appended to
  `activity_events`. A usage report can rehydrate retained durable state outside the live window,
  preserving its status, messages, activity, and lifecycle clock.
- Resume selects the greatest `(UpdatedAt, SessionId)` from all durable sessions for the worktree,
  regardless of current status or heartbeat recency. Sessions older than the live window remain
  resumable until retention removes them.
- On server start, still-live rows are loaded from SQLite and published to the scheduler before new
  events arrive. Durable titles, intents, skill, footer messages, context gauges, and their ordering
  clocks are restored. Retained rows outside the live window remain available for footer and resume
  selection and can re-enter live state through heartbeat, usage, title, or lifecycle reports.

## Technical Approach

### Reporting extension

`src/Extension/reporting/extension.mjs` joins the current Copilot session passively, replays prior
events, subscribes to live events, and posts the same wire format for both paths. It drops sub-agent
events, skill-context injections, blank messages, and invalid usage gauges before sending.

The extension maps lifecycle, skill, message, `assistant.intent`, `session.title_changed`, ask-user,
and usage events onto the closed wire contract. It forwards ask-user request, completion, and idle
events as facts; the server's persisted request/completion clocks resolve their effective status
independently of delivery order.

After subscriptions and replay are active, the extension reads
`session.rpc.metadata.snapshot().summary` in a non-blocking background task and emits
`title_bootstrap` only when no nonblank live title was seen. A failed or slow metadata request cannot
block heartbeat or normal reporting. Reports fan out to `TREEMON_PORTS`, then `TREEMON_PORT`, then
port 5000, allowing production and validation instances to observe the same session.

### Domain and ingestion

`SessionActivity` defines the closed event union and pure fold. The fold retains status, skill,
intent, title, last messages, context usage, and independent ask-user request/completion clocks.
Repeated identical intent/title text keeps its original change timestamp; older values cannot
replace newer ones. `effectiveActivity` chooses the newer intent or title while preserving its
`AgentActivity` source.

`POST /api/session/activity` validates the DTO, provider, known worktree, event-specific fields, and
CSRF origin. Future timestamps beyond five minutes are clamped to server time, and nested message
timestamps are normalized to that value before ordering. Free text is bounded before persistence.

Runtime `<system_reminder>` messages arrive through the genuine `user.message` channel, so the
server classifies them after validation and the known-worktree guard but before the single-writer
mailbox. A monitored reminder returns `recorded=false, monitored=true`; it cannot replace the last
user message, complete an ask-user wait, clear a skill, change status, or enter durable events.
Persisted reminders from older versions are also hidden by the shared footer projection.

A `SessionActivityService` mailbox is the single writer. Event IDs provide idempotency. Lifecycle,
activity, interaction, usage, bootstrap, and liveness reports use independent ordering paths:

- Lifecycle reports append an idempotent event record and update the lifecycle last-write-wins
  clock. Older reports may be retained for event-ID deduplication but cannot regress live state.
- Intent and live title reports append their accepted event while resolving content by message time
  without blocking older lifecycle transitions.
- Ask-user request/completion reports merge their monotonic clocks and advance `UpdatedAt` only
  forward, so arrival order cannot change the effective wait.
- Title bootstrap hydrates durable state without appending an event or advancing lifecycle time.
- Usage persists only the latest gauge on its own ordering clock and advances `last_seen` forward.
- Heartbeats only advance `last_seen`; because representative selection uses `UpdatedAt`, they
  affect openness but not footer or resume ownership.

Ingestion paths consult the live map and then the durable row by session id whenever prior state is
needed. This preserves retained state when a heartbeat, usage report, title bootstrap, activity
report, or later lifecycle event arrives after restart.

### Persistence

`SessionActivityStore` uses SQLite WAL with short-lived connections:

- `session_status` stores the latest folded status, messages, intent, title, context gauge,
  independent clocks, and session identity for live rebuild, footer data, and resume.
- `activity_events` retains accepted history-bearing events under unique event IDs so duplicate
  reports remain full no-ops. Canonical Overview history uses direct 30-second snapshots and never
  reads this table.

Store construction creates the current schema, applies missing additive columns idempotently, then
runs bounded legacy normalization. This order lets databases predating activity, context, or
ask-user columns start safely. Nullable context fields make legacy rows restore a plain status dot
until a gauge arrives.

Event append/status upsert and context updates are transactional and reread the authoritative
persisted row. Hourly retention bounds durable session and event data without coordinating with
Overview history. `SqliteStorage` owns shared UTC timestamp encoding/parsing and immutable reader
draining. Removed Overview rollup, liveness, task-snapshot, staging, and reconstruction
infrastructure is not part of this store.

### Worktree projection

`CodingToolStatus.collapseByWorktree` is the single projection from session state to card fields.
`WorktreeApi` merges each worktree's greatest-`UpdatedAt` durable representative into the live
candidate set before collapsing it. The representative's own `LastSeen` still decides whether it
contributes an open dot, while its `UpdatedAt` keeps it eligible for footer and resume ownership.

The projection keeps the activity source through the `AgentActivity` union and exposes every open
session for status/context rendering. Overview snapshot capture uses the same live session
projection but persists the complete count-only aggregate independently; see
`docs/spec/overview-activity-history.md`.

## Decisions

| Decision | Choice |
|---|---|
| Source of truth | Push events only; log-parsing detectors are removed. |
| Session model | Working, WaitingForUser, Idle; NoSession only at worktree collapse. |
| Synthetic messages | Filter server-side before ingestion with the shared user-message classifier. |
| Ask-user ordering | Persist independent request/completion clocks; do not keep lifecycle state in the extension. |
| Liveness | Heartbeats and accepted usage update `last_seen` without lifecycle event writes. |
| Representative ordering | Use `(UpdatedAt, SessionId)`; never let heartbeat-only `LastSeen` choose footer or resume ownership. |
| Multiple sessions | Preserve per-session status, skill, and context usage; collapse only card-level fields. |
| Footer | Decouple from the status dot and merge a retained durable representative. |
| Activity | Use freshest source-tagged intent/title; bootstrap title from metadata, never infer intent. |
| Context usage | Persist the last-known gauge and ordering timestamp; do not append it to activity events. |
| Persistence | Store latest session state plus idempotent accepted events in SQLite WAL. |
| Overview history | Capture canonical direct snapshots every 30 seconds; never reconstruct from activity events. |
| Resume | Query durable most-recent activity identity, not the bounded live cache or heartbeat recency. |
| Explicit close | Not required; heartbeat expiry handles clean exit and crashes uniformly. |
| Window state | Keep terminal/window `HasActiveSession` separate from push-session openness. |

## Key Files

| File | Role |
|---|---|
| `src/Extension/reporting/extension.mjs` | SDK filtering, wire mapping, replay, metadata bootstrap, usage, and heartbeat. |
| `src/Extension/reporting/reporting-core.mjs` | Pure nonblank message-report construction. |
| `src/Server/SessionActivity.fs` | Event domain, pure fold, effective activity/status, freshness, and active selection. |
| `src/Server/SessionActivityService.fs` | Request validation, synthetic filtering, independent ordering paths, mailbox ingestion, and lifecycle. |
| `src/Server/UserMessageFormatting.fs` | System-reminder classification and user/canvas footer projection. |
| `src/Server/SqliteStorage.fs` | Shared SQLite UTC timestamp encoding/parsing and immutable reader draining. |
| `src/Server/SessionActivityStore.fs` | Session persistence, additive migration, idempotent event append, representative queries, and retention. |
| `src/Server/CodingToolStatus.fs` | Per-worktree collapse, heartbeat-independent activity/footer projection, and resume lookup. |
| `src/Server/RefreshScheduler.fs` | Live session state and `CodingToolSince` transitions. |
| `src/Server/WorktreeApi.fs` | Card assembly, retained-session merge, direct snapshot history API, and resume command wiring. |
| `src/Shared/Types.fs` | `AgentActivity`, context usage, per-session markers, and worktree wire types. |
| `src/Shared/OverviewData.fs` | Shared per-session Overview grouping. |
| `src/Client/OverviewPresentation.fs` | Client-only Overview selection and visual mappings. |
| `src/Client/CardViews.fs` | Status dots, activity/footer text, and per-session context display. |

## Related Specs

- `docs/spec/worktree-monitor.md` - dashboard architecture and refresh model.
- `docs/spec/beads-overview-band.md` - live task and agent aggregation.
- `docs/spec/overview-activity-history.md` - durable canonical Overview snapshots.
- `docs/spec/overview-drilldown.md` - per-group session details.
- `docs/spec/resume-last-session.md` - resume command behavior.
- `docs/spec/remoting-csrf-hardening.md` - endpoint origin protection.
- `docs/spec/native-session-management.md` - terminal/window liveness, distinct from push openness.
