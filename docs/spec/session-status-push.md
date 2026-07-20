# Session Status via Push Model

## Overview

Treemon receives Copilot CLI lifecycle and activity events through a passive reporting extension and
folds them into durable per-session state. Worktree cards, Overview agent groups, history, activity
titles, context usage, and session resume all use this shared state; no session-log parsing remains.

## Goals

- Derive status and card activity from explicit SDK events rather than log-file timing heuristics.
- Keep reporting passive: no tools, prompts, or transcript changes.
- Preserve live status, footer activity, messages, and resume identity across server restarts.
- Support multiple concurrent sessions in one worktree without losing per-session activity.
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
- `turn_start`, user prompts, and assistant messages set Working. An input request sets
  WaitingForUser. `turn_end` and `session.idle` set Idle. There is no durable Done state.
- A session is open while its heartbeat is newer than `openWindow` (3 minutes). A defensive
  `stalenessTimeout` (5 minutes) can downgrade stale active state, and `idleWindow` (2 hours) bounds
  the in-memory session map.
- The extension sends liveness-only heartbeats every 60 seconds after a real status has been
  established. Heartbeats update `last_seen` without changing status or adding `activity_events`;
  their compact `session_liveness` rows preserve historical openness.

### Multiple sessions and card fields

- The status dot chooses the most-recent open active session; if every open session is Idle, the
  worktree is Idle. With no open sessions it is NoSession.
- The footer is independent of the dot. Skill, activity, and last messages come from the active
  winner or the most-recent session of any status, so they survive Idle and NoSession.
- Card activity is the freshest source-tagged value from `assistant.intent` or the session title.
  Intent is optional enrichment; the title is the reliable fallback and is restored from
  `metadata.snapshot().summary` when no live title event arrived during startup.
- `SessionStatuses` contains every open session after freshness adjustment, ordered
  Working -> WaitingForUser -> Idle. Each entry retains its own skill and context usage.
- The Overview counts and groups sessions independently. One worktree can contribute sessions to
  several activity groups at once.
- `CodingToolSince` is captured when the collapsed worktree status changes and remains stable while
  Idle heartbeats advance `last_seen`.

### Context usage, resume, and restart

- `session.usage_info` updates an ephemeral per-session context gauge without changing status. Its
  ordering clock is separate from status ordering; usage is not persisted across restarts.
- Resume selects the most-recent durable session for the worktree regardless of current status.
  Sessions older than the 2-hour live window remain resumable until retention removes them.
- On server start, still-live session rows are loaded from SQLite and published to the scheduler
  before new events arrive. Durable titles, intents, skill, and footer messages are restored.

## Technical Approach

### Reporting extension

`src/Extension/reporting/extension.mjs` joins the current Copilot session passively, replays prior
events, subscribes to live events, and posts the same wire format for both paths. It drops sub-agent
events, skill-context injections, blank messages, and invalid usage gauges before sending.

The extension maps lifecycle, skill, message, `assistant.intent`, `session.title_changed`, and usage
events onto the closed wire contract. After subscriptions and replay are active, it reads
`session.rpc.metadata.snapshot().summary` in a non-blocking background task and emits
`title_bootstrap` only when no nonblank live title was seen. A failed or slow metadata request cannot
block heartbeat or normal reporting.

An `ask_user` latch suppresses transient `session.idle` events while a question is unanswered.
Reports fan out to `TREEMON_PORTS`, then `TREEMON_PORT`, then port 5000, allowing production and
validation instances to observe the same session.

### Domain and ingestion

`SessionActivity` defines the closed event union and pure fold. The fold retains status, skill,
intent, title, last messages, and context usage. Repeated identical intent/title text keeps its
original change timestamp; older values cannot replace newer ones. `effectiveActivity` chooses the
newer intent or title while preserving its `AgentActivity` source.

`POST /api/session/activity` validates the DTO, provider, known worktree, event-specific fields, and
CSRF origin. Future timestamps beyond five minutes are clamped to server time, and nested message
timestamps are normalized to that value before ordering. Free text is bounded before persistence.

A `SessionActivityService` mailbox is the single writer. Event IDs provide idempotency. Lifecycle
status, activity fields, and usage use independent ordering paths:

- Lifecycle reports append history and update the lifecycle last-write-wins clock.
- Intent and live title reports persist source activity without blocking older lifecycle transitions.
- Title bootstrap hydrates durable state without appending source history or advancing lifecycle time.
- Usage is live-only and status-preserving.
- Heartbeats only advance `last_seen`.

Older lifecycle events remain available for history but cannot regress live state.

### Persistence

`SessionActivityStore` uses SQLite WAL with short-lived connections:

- `session_status` stores the latest folded status, messages, intent, title, and session identity for
  live rebuild, footer data, and resume.
- `activity_events` stores post-event status and skill needed to reconstruct agent history without
  replaying the full fold.
- `session_liveness` stores heartbeat and usage timestamps separately from status history, allowing
  a quiet open session to remain open in historical reconstruction.
- `task_snapshots` stores count-only Overview task values on change.

Store construction applies additive schema changes idempotently. Context usage values are deliberately
absent from the schema, but their liveness timestamps use `session_liveness`. Event append/status
upsert and liveness/status updates are transactional. Hourly retention removes redundant rows older
than 60 days while preserving the latest old status event for each retained session and the latest old
task snapshot as history baselines.

### Worktree projection

`CodingToolStatus.collapseByWorktree` is the single live projection from session state to card
fields. It keeps the activity source through the `AgentActivity` union and exposes each open session
for status/context rendering. `WorktreeApi` reuses the collapse for dashboard assembly and retained
footer fallback. `OverviewHistory` uses the same openness collapse and per-session grouping; see
`docs/spec/overview-activity-history.md`.

## Decisions

| Decision | Choice |
|---|---|
| Source of truth | Push events only; log-parsing detectors are removed. |
| Session model | Working, WaitingForUser, Idle; NoSession only at worktree collapse. |
| Liveness | Dedicated heartbeat updates `last_seen` and `session_liveness` without re-folding or status-history writes. |
| Multiple sessions | Preserve per-session status, skill, and context usage; collapse only card-level fields. |
| Footer | Decouple from the active status winner so retained activity and messages remain visible. |
| Activity | Use freshest source-tagged intent/title; bootstrap title from metadata, never infer intent. |
| Ordering | Separate lifecycle, activity, usage, and title-bootstrap ordering paths. |
| Context usage | Live-only gauge; do not persist or append it to activity history. |
| Persistence | Store latest session state plus a post-fold event stream in SQLite WAL. |
| Resume | Query durable most-recent session identity, not the bounded live cache. |
| Explicit close | Not required; heartbeat expiry handles clean exit and crashes uniformly. |
| Window state | Keep terminal/window `HasActiveSession` separate from push-session openness. |

## Key Files

| File | Role |
|---|---|
| `src/Extension/reporting/extension.mjs` | SDK filtering, wire mapping, replay, metadata bootstrap, and heartbeat. |
| `src/Extension/reporting/reporting-core.mjs` | Pure nonblank message-report construction. |
| `src/Server/SessionActivity.fs` | Event domain, pure fold, effective activity, freshness, and active selection. |
| `src/Server/SessionActivityService.fs` | Request validation, ordering paths, mailbox ingestion, and lifecycle. |
| `src/Server/SessionActivityStore.fs` | SQLite persistence, migrations, queries, and retention. |
| `src/Server/CodingToolStatus.fs` | Per-worktree collapse, activity/footer projection, and resume lookup. |
| `src/Server/RefreshScheduler.fs` | Live session state and `CodingToolSince` transitions. |
| `src/Server/WorktreeApi.fs` | Card assembly, history API, and resume command wiring. |
| `src/Shared/Types.fs` | `AgentActivity`, context usage, per-session markers, and worktree wire types. |
| `src/Shared/OverviewData.fs` | Shared per-session Overview grouping. |
| `src/Client/OverviewPresentation.fs` | Client-only Overview selection and visual mappings. |
| `src/Client/CardViews.fs` | Status dots, activity/footer text, and per-session context display. |

## Related Specs

- `docs/spec/worktree-monitor.md` - dashboard architecture and refresh model.
- `docs/spec/beads-overview-band.md` - live task and agent aggregation.
- `docs/spec/overview-activity-history.md` - history from activity events and task snapshots.
- `docs/spec/overview-drilldown.md` - per-group session details.
- `docs/spec/resume-last-session.md` - resume command behavior.
- `docs/spec/remoting-csrf-hardening.md` - endpoint origin protection.
- `docs/spec/native-session-management.md` - terminal/window liveness, distinct from push openness.
