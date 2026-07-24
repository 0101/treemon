# Session Status via Push Model

## Overview

Treemon derives each worktree's coding-tool status from a **push model**: the Copilot CLI extension
observes the SDK session event stream and POSTs status events to the server, which folds them into
live per-session state. This replaced the old log-file-parsing detectors entirely. The status dot is
a pure function of that live state: **Working** (red), **WaitingForUser** (yellow), **Idle** (blue —
an open CLI session between turns), or **NoSession** (grey — no live session).

## Goals

- **Push, not parse.** Status comes from explicit lifecycle events — immediate and exact, never
  inferred from log-file mtime heuristics. The three log-parsing detectors (`CopilotDetector`,
  `ClaudeDetector`, `VsCodeCopilotDetector`) are deleted; the build is push-only.
- **Passive.** The extension only subscribes, never calls `session.send`
  or injects context. The transcript is identical with reporting on or off.
- **Durable where useful.** Parent/base status, footer data, ask-user clocks, and context usage
  survive a server restart; the lifecycle/content event stream is persisted for historical
  Overview aggregation. Background-agent ordering clocks are intentionally process-local and are
  forgotten on restart to keep the store and recovery path simple.
- **Simple.** Events are a closed union; the server logic is a tiny pure fold with no branching for
  inferred bracket depth or synthetic messages. Sub-agent content is filtered at the extension
  boundary; explicit background-agent lifecycle reaches the process-local F# fold as closed events.
- **Four-way status dot** driven purely by push state (below).

## Expected Behavior

### Status dot

The single source of truth is the worktree's collapsed coding-tool status:

| Situation | Status | Dot |
|---|---|---|
| Agent mid-turn (open session, Working) | Working | red `#ff0000` |
| Agent parked on `ask_user` (open session) | WaitingForUser | yellow `#f9e2af` |
| Agent finished / between prompts, **CLI still open** | Idle | blue `#89b4fa` |
| No live session (never reported, or the CLI exited/crashed) | NoSession | grey `#585b70` |

The fold derives a session's status from reports:
- `assistant.turn_start` / a genuine user prompt / an assistant message → **Working**
- `elicitation.requested` / `user_input.requested` (`ask_user`) → **WaitingForUser**
- `assistant.turn_end` / `session.idle` → base **Idle**; a still-open ask-user request remains
  **WaitingForUser** until its completion or genuine user reply arrives; otherwise any active
  background agent keeps the effective status **Working**

An active background agent keeps an otherwise-idle parent session **Working** until its matching
`subagent.completed` or `subagent.failed`. **WaitingForUser** remains higher priority because it is
already active and requires user action.

Only Copilot sub-agent lifecycle is included. Detached background shells are outside this status
calculation.

There is **no durable `Done`** — a finished turn reads as Idle. During active work the next
`turn_start` re-asserts Working within ≤0.1 s, so the mid-loop Idle window is invisible to polling;
only a genuinely-finished turn settles on Idle. `NoSession` is never a per-session status; it is only
ever the collapse result for a worktree with no open session.

### Openness (blue vs grey) and the crash net

A session is **open** iff `now − last_seen < openWindow` (~3 min — a few missed heartbeats). An open
CLI heartbeats even while idle, so its `last_seen` stays fresh; a closed/crashed one goes stale and
drops out. Only open sessions drive the status dot; with none open the worktree is **NoSession**.

`openWindow` (3 min) is deliberately smaller than the `stalenessTimeout` crash-net (5 min): a dead
Working session drops out of openness (→ grey) before the crash-net would rewrite it to Idle, so it
never lingers blue. The crash-net (a quiet Working/WaitingForUser → Idle) remains as a defensive net
but rarely fires. Its stale projection suppresses both pending-user-input and background-agent
overlays, so old lifecycle clocks cannot keep a dead session active. A longer **idle window** (2 h)
bounds restart hydration; durable rows remain available for footer/resume until retention pruning.

### Footer persists (decoupled from the dot)

The card footer — freshest source-tagged activity, running skill, last user message, last assistant
message — is sourced from the active winner, or otherwise the session with the greatest
`UpdatedAt` of any status, NOT from the status-dot collapse. Going Idle or losing the open session
therefore does **not** blank that session's retained footer fields; durable fallback keeps them
available beyond the live window.

Worktree auto-sync uses a related but delivery-aware target pick: the active open winner first,
otherwise the greatest-`UpdatedAt` open session, and only then a retained/offline identity when no
open session exists. The prompt is delivered by `SessionBridge`, while the reporting extension stays
passive and never calls `session.send`.

The session title is the reliable activity source: after joining, subscribing, and replaying
persisted history, the reporting extension reads `session.rpc.metadata.snapshot().summary` and
reports a nonblank summary when no live `session.title_changed` arrived during startup. Live
title-change events continue to update it afterward. `assistant.intent` is optional enrichment only;
the CLI does not emit it for every ordinary turn, so its absence is expected and never blocks title
display.

### Multiple sessions in one worktree

A worktree's live sessions collapse to one card via two decoupled picks:
- **Status dot** — among *open* sessions, drop Idle and the active session with the greatest
  `UpdatedAt` wins; all Idle → Idle; no open session → NoSession. Idle is filtered before ordering,
  so a newly-idled session cannot hide an actively-Working sibling.
- **Footer** — the active winner if one runs, else the session with the greatest `UpdatedAt` of any
  status.
- **Auto-sync target** — the active open winner if one runs, else the open session with the greatest
  `UpdatedAt`; only when no session is open may the retained/footer identity be used.

### Overview "Agents" dimension

- **Working** agents grouped by classified activity (Investigating/Executing/…) — red.
- **WaitingForUser** agents — yellow.
- **Idle (open)** agents — a blue group, each chip showing **time since it entered Idle**
  (`CodingToolSince`, frozen at the transition into Idle).
- **NoSession** worktrees are excluded (they are not agents); their card just shows the grey dot.

### Resume

`Resume last session` picks the session with the greatest `UpdatedAt` **regardless of active/idle**
(the session the user last touched) — distinct from both the display and footer picks. It reads the
session id from the durable store (so it survives a restart even for a session last active > 2 h
ago) and issues `copilot --resume <id>`, or `--continue` when the worktree never reported.

### Restart

On server start, persisted parent/base status, user-input clocks, intent/title metadata, footer
messages, and last known context usage are rebuilt from SQLite before serving. Background-agent
start/finish clocks are deliberately not persisted: an unfinished agent is forgotten and the card
falls back to the persisted parent/base status, typically Idle after the parent turn ended. This
matches the pre-background-agent restart behavior and is an intentional simplicity trade-off.

An already-persisted lifecycle event replayed after restart remains an `EventId` duplicate and does
not reconstruct forgotten state. A genuinely new lifecycle report starts a new in-memory clock.
Within one server process, the first report after a stale gap clears the old in-memory lifecycle
before folding, so a resumed session does not retain crashed work.

## Technical Approach

### Domain model (make illegal states unrepresentable)

The server owns the persisted status domain; the extension is a stateless SDK-to-wire mapper.
`SessionActivity.fs`:

```fsharp
type SessionEvent =
    | TurnStarted
    | UserPrompt of Message                 // genuine after extension + server boundary filtering
    | AssistantMessage of Message
    | SkillInvoked of name: string
    | IntentReported of Message             // SDK assistant.intent
    | TitleReported of Message              // SDK session.title_changed
    | TitleBootstrap of Message             // metadata.snapshot().summary state hydration
    | AwaitingUserInput of question: Message option * at: DateTimeOffset
    | UserInputCompleted of at: DateTimeOffset
    | BackgroundAgentStarted of toolCallId: string * at: DateTimeOffset
    | BackgroundAgentFinished of toolCallId: string * at: DateTimeOffset
    | TurnEnded
    | WentIdle
    | UsageInfo of currentTokens: int * tokenLimit: int   // gauge only; preserves status
    | Heartbeat                              // liveness only; bumps last_seen, no status fold

type SessionActivityReport =
    { SessionId; WorktreePath; Provider; EventId; OccurredAt; Event }

type BackgroundAgentLifecycle =
    { StartedAt: DateTimeOffset option
      FinishedAt: DateTimeOffset option }
```

A per-session status is `SessionLevelStatus = Working | WaitingForUser | Idle` (no `NoSession` — that
is a worktree-level collapse result, made unrepresentable per session). The fold state is small (no
`SubagentDepth`, no `LastAssistantWasAskUser`):

```fsharp
type SessionStatus =
    { Status: SessionLevelStatus; Skill: string option
      Intent: Message option; Title: Message option
      LastUserMessage: Message option; LastAssistantMessage: Message option
      ContextUsage: ContextUsage option
      AwaitingUserSince: DateTimeOffset option
      UserInputCompletedAt: DateTimeOffset option
      BackgroundAgentClocks: Map<string, BackgroundAgentLifecycle> }
```

`fold`: `TurnStarted` / `AssistantMessage` / `UserPrompt` → Working, `SkillInvoked` → set skill,
`IntentReported` / `TitleReported` / `TitleBootstrap` update their status-neutral fields while
preserving the original change time when identical text is re-emitted and rejecting an older value
after a newer one, and `TurnEnded` / `WentIdle` → base Idle. `AwaitingUserInput` and
`UserInputCompleted` advance independent monotonic clocks; `effectiveStatus` overlays WaitingForUser
when the latest request is newer than the latest completion, regardless of report arrival order.
Otherwise it overlays Working while any tool's start clock is newer than its finish clock.
Background lifecycle reports merge each per-tool clock by maximum event time, so duplicate and
out-of-order delivery is deterministic in both directions within one server process. Completed
clocks remain in the live state so a delayed older start cannot resurrect an agent; they are cleared
after a stale gap or server restart. A genuine `UserPrompt` also advances the completion clock,
keeps a running ask-user skill, and sets the base status to Working. `UsageInfo` updates only
`ContextUsage`, and `Heartbeat` → no-op.
Background-agent events change only lifecycle clocks and effective status; they never replace the
parent skill, intent, title, or footer messages.
`effectiveActivity` selects the newer intent/title and returns a source-tagged
`AgentActivity` (`Intent` or `SessionTitle`) so the collapse boundary never mislabels a title as
intent. The fold is pure and append-friendly — folding a later batch onto an earlier result equals
folding the whole stream.

The persisted `StoredStatus` carries `UpdatedAt` for base lifecycle ordering and representative
selection, and `ContextUsageAt` for usage ordering; user-input clocks and intent/title message times
are persisted independently. Background-agent clocks remain only in the service's live map and are
overlaid after authoritative store rereads. Completed clocks retain five minutes of late-event
ordering; older lifecycle reports are ignored, so pruning a terminal clock cannot resurrect its
agent. A slightly-earlier ask-user request can still override a later-arriving idle report. Metadata
or usage cannot block lifecycle state, and heartbeats advance `LastSeen` while pruning expired
completed clocks.
Equal `UpdatedAt` values use `SessionId` as a stable tie-breaker.

`freshnessAdjusted` is the **crash net** only: a Working/WaitingForUser status whose `last_seen` is
older than `stalenessTimeout` reads as Idle and closes any pending wait in the read projection.

### Transport filtering (why the fold stays simple)

The extension and server ingestion boundary ensure only genuine lifecycle events reach the fold:
1. **Sub-agent content** — messages, skills, intent, and assistant turns carrying `agentId` are
   dropped. `subagent.started`, `subagent.completed`, and `subagent.failed` are mapped to explicit
   background-agent lifecycle reports before that filter. → no inferred bracket depth or content
   attribution on the server.
2. **Skill-context injections** — a skill's `<skill-context>` injection arrives as a `user.message`;
   the extension drops it (source starts `skill-` AND content starts `<skill-context`).
3. **System reminders** — runtime `<system_reminder>` instructions also arrive through the
   `user.message` channel. The server classifies and ignores them before the single-writer mailbox,
   so they cannot replace the last genuine prompt, change status, clear a skill, or enter history.
4. **Irrelevant events** — only events mapping to the closed wire contract are forwarded; all other
   SDK events are ignored. `title_bootstrap` is metadata-generated and `heartbeat` is
   timer-generated. → the `SessionEvent` union has no catch-all.

### Ingestion: endpoint + single-writer mailbox

- `POST /api/session/activity` mirrors `canvasRegisterHandler`: JSON DTO → domain
  `SessionActivityReport`, validate, known-worktree guard, `HttpSecurity.csrfGuard`.
- A monitored `<system_reminder>` `user_prompt` is a valid but synthetic report: the handler returns
  `recorded=false, monitored=true` and does not submit it to the mailbox. This filtering is
  server-owned because user-message projection and persisted-footer cleanup are server-owned too.
- **Wire contract — the single coupling point between `extension.mjs` (producer) and the
  handler (consumer).** The POST body is one report:
  `{ sessionId, worktreePath, provider, eventId, occurredAt, kind, message?, skillName?,
  toolCallId?, currentTokens?, tokenLimit? }`. Exactly fifteen `kind` strings are accepted:
  `turn_started`→`TurnStarted`, `user_prompt`→`UserPrompt`, `assistant_message`→
  `AssistantMessage`, `skill_invoked`→`SkillInvoked`, `awaiting_user_input`→`AwaitingUserInput`,
  `user_input_completed`→`UserInputCompleted`,
  `background_agent_started`→`BackgroundAgentStarted`,
  `background_agent_finished`→`BackgroundAgentFinished`,
  `intent_reported`→`IntentReported`, `title_reported`→`TitleReported`,
  `title_bootstrap`→`TitleBootstrap`,
  `turn_ended`→`TurnEnded`, `went_idle`→`WentIdle`, `usage_info`→`UsageInfo`, and
  `heartbeat`→`Heartbeat`. `message` (`{ text; at }`) is mandatory for `user_prompt`,
  `assistant_message`, `intent_reported`, `title_reported`, and `title_bootstrap`, and optional only for
  `awaiting_user_input` (the ask_user question). `skillName` applies only to `skill_invoked`;
  `toolCallId` is mandatory for both background-agent lifecycle kinds and is limited to 512 UTF-16
  code units. Overlong values are rejected rather than truncated so distinct opaque identity keys
  cannot alias; values within the bound are preserved exactly.
  `currentTokens` and `tokenLimit` apply only to `usage_info`, with `tokenLimit > 0` and negative
  `currentTokens` normalized to zero. All other kinds carry none of those event-specific fields.
  An unknown `kind` is a validation error, never silently dropped.
- A dedicated `SessionActivity` `MailboxProcessor` is the **single writer** (the only mutable
  boundary). History-bearing reports append + upsert atomically, then feed `RefreshScheduler`.
  Transactional writes reread the authoritative persisted row; the service overlays its
  process-local background clocks before publishing that row to the scheduler/live map. Duplicate
  `EventId`s are complete no-ops.
- Accepted lifecycle reports use `UpdatedAt`: an older report within the five-minute late-event
  window is appended to `activity_events` for historical reconstruction but cannot regress the live
  aggregate. `IntentReported` and `TitleReported` are also appended, but load any retained durable
  aggregate, preserve `UpdatedAt`, and resolve independently by their message timestamps.
- `AwaitingUserInput` and `UserInputCompleted` use the same independent ordering path: they merge
  their clocks by maximum event time while advancing `UpdatedAt` monotonically. Fire-and-forget
  delivery order therefore cannot change whether the session is waiting or regress representative
  activity ordering.
- Background-agent lifecycle uses the same order-independent discipline per `toolCallId` in the
  live fold: start and terminal clocks merge by maximum event time, and an agent is active only when
  its start is newer than its terminal. Lifecycle changes append event-time history separately from
  current-state reconstruction. In-order rows use the running fold; delayed rows use the same
  event-local stale-event policy as other history-bearing reports (folding the event onto an empty
  state), so a newer live status or skill is never attributed to an older event.
- A lifecycle report may be the first report received for a session because POST delivery is
  fire-and-forget. A start creates an Idle-base shell whose effective status is Working; a terminal
  creates or updates an inactive in-memory clock.
- Completed clocks are retained for the five-minute `stalenessTimeout` window and pruned on any
  subsequent report, including heartbeats. Active clocks are never removed by this cleanup.
  Lifecycle reports at or before the resulting cutoff are ignored, so a delayed older start cannot
  resurrect an agent after its terminal clock has been pruned.
- Accepted lifecycle reports advance `UpdatedAt` and `LastSeen` monotonically for representative
  selection and openness, while preserving all parent-authored footer and activity fields.
- Before any report path advances a stale session's `LastSeen`, the service clears its process-local
  background clocks and folds the incoming report onto that clean state. The incoming report may
  itself begin new background work.
- `heartbeat` is liveness-only: it advances `last_seen` for an existing session without folding,
  changing `updated_at`, influencing representative-session selection, or appending history.
  `usage_info` arrives only on the live SDK stream but is durably status-preserving: it persists
  `ContextUsage`, `context_usage_at`, and forward-only `last_seen` for an existing live session using
  a separate last-write-wins clock, without changing `updated_at` or appending history. The store
  returns the authoritative row, so an older delayed gauge cannot replace a newer one.
- `title_bootstrap` is durable state hydration, not source history: it updates the persisted title
  and forward-only `last_seen` without appending `activity_events` or advancing the lifecycle
  `updated_at` clock. The service loads the session's durable row regardless of the two-hour live
  cutoff, preserving status, skill, intent, and footer messages before applying the title. With no
  durable row it creates an Idle shell with a minimum lifecycle timestamp so every real SDK event
  can still apply.
- **Future timestamps are clamped, not trusted.** `last_seen` comes from `occurredAt`; a future value
  would make the freshness net never decay. `parseReport` clamps any `occurredAt` beyond `now + 5 min`
  down to `now`, then normalizes every message-bearing event's nested `Message.At` to that same value
  so a replayed future timestamp cannot pin stale intent/title text in the freshest-activity choice.
- **Free text is length-capped server-side** (8 KB) before persistence — defence in depth above the
  extension's 2000-char POST-body cap; truncated (not rejected) so an over-long field never drops the
  whole event and regresses the live fold. (Display truncation to 80/120 chars stays downstream in
  `CodingToolStatus`.)

### Persistence (SQLite, WAL)

`SessionActivityStore(dbPath)` (IDisposable, creates the schema on construction) at
`data/session-activity-{port}.db` — keyed by the server's `--port`, so a side-by-side instance never
collides:

```sql
session_status(session_id PK, worktree_path, provider, status, current_skill,
  last_user_msg, last_user_ts, last_asst_msg, last_asst_ts,
  intent_text, intent_ts, title_text, title_ts, updated_at, last_seen,
  context_current_tokens NULL, context_token_limit NULL, context_usage_at NULL,
  awaiting_user_since NULL, user_input_completed_at NULL);
activity_events(event_id PK, session_id, worktree_path, provider, kind, status, skill, ts);
```

PRAGMAs `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000`; each op runs on its own
short-lived connection (thread-safe against the single-writer mailbox and concurrent WAL readers).
Construction inspects `PRAGMA table_info(session_status)` and individually adds any missing
`intent_text`, `intent_ts`, `title_text`, `title_ts`, `context_current_tokens`,
`context_token_limit`, `context_usage_at`, `awaiting_user_since`, or
`user_input_completed_at` column. Background-agent lifecycle has no durable schema.

All timestamps persist as UTC round-trip (`"O"`) strings, so lexical comparison equals chronological
order. Lifecycle upserts use `updated_at`, metadata retains the newest per-field timestamp, and
usage upserts use `context_usage_at`. `upsertStatus` inserts the complete aggregate for a new row and
preserves existing context columns on conflict.
`upsertContextUsage` inserts the complete aggregate when a retained in-memory session outlives its
pruned row, otherwise atomically replaces the three context fields and advances `last_seen` only for
an equal-or-newer usage timestamp. Transactional history-bearing writes and context writes reread
and return the authoritative persisted row, which is then used for the scheduler and service maps so
persisted metadata/context winners cannot diverge from live state; the service then restores its
process-local lifecycle clocks on the returned row. `appendEvent` is `INSERT OR IGNORE`; usage
remains a last-known gauge and is not appended to history. Background lifecycle events use the same
append/upsert path as other independently ordered events: history and persisted base state remain
durable, while start/finish clocks remain in memory only. `pruneOld` trims old status and event rows.
`loadLiveStatuses` rebuilds persisted status, usage, user-input clocks, intent/title metadata, and
their ordering state on restart for rows within the idle window; every loaded lifecycle map starts
empty by design.
`RetainedByWorktree` selects one greatest-`(UpdatedAt, SessionId)` footer representative per
worktree in SQLite. Resume uses a dedicated scalar query for that same ordering and does not load
status content. Retained rows outside the live window remain available until pruning. The service is
started only in the real monitoring path — demo/fixture mode serves synthetic data and takes no
posts.

### Collapse to card fields (`CodingToolStatus.fs`)

Each worktree's durable `UpdatedAt` winner is merged into the live candidate set by session id before
`collapseByWorktree` groups statuses by worktree path. The row's own `LastSeen` still determines
whether it contributes an open dot; independently, it remains eligible for the fallback footer when
a heartbeat-kept live sibling is not the representative session. `fromPushSessions` then applies
the two decoupled picks (openness-driven status dot + `UpdatedAt`-ordered active winner/fallback
footer, above).
It also exposes every open session as its own status marker with that session's skill and optional
`ContextUsage`; a reported gauge renders as a context-window donut, while `None` renders as a plain
status dot. Persisted gauges restore donuts after a server restart without waiting for a fresh
`session.usage_info` event.
The footer exposes `AgentActivity` as a source-tagged union: the freshest intent/title value keeps
its original source while the card renders either as the activity line with its relative time and an
optional running-skill pill. An activity identical to the last user message is suppressed rather
than duplicated. The last user message crosses the dashboard wire as
`UserFooterMessage { Glyph; Text; Timestamp }`. `UserMessageFormatting` suppresses
`<system_reminder>` text, recognizes the `[canvas] ` transport prefix, preserves the semantic Canvas
glyph, prioritizes the `request` from first-party `canvas-selection` actions, summarizes known
actions, and formats unknown valid JSON structurally without rewriting punctuation inside string
values. The same projection is applied to `AgentActivity` before truncation, so duplicate
suppression compares equivalent representations and previously persisted reminders remain hidden.
Assistant footer messages use a direct `(text, timestamp)` value; the enclosing
`CodingToolProvider` supplies the rendered provider label. The push provider is Copilot-only today,
so an active card reads `Copilot`.

`CodingToolSince` (time-since-idle) is stamped the moment a worktree's collapsed status **enters**
Idle and frozen until it changes — **not** recomputed from `last_seen` (which an open idle session
keeps advancing via heartbeat, which would reset the chip to ~0 each poll). `WorktreeApi` reads the
frozen stamp for Idle worktrees; a new Working turn clears it.

`SessionActivityStore.LatestSessionIdForWorktree` is the distinct resume pick — a scalar
greatest-`(UpdatedAt, SessionId)` query against the **durable store** (not the idle-window live
cache, so a session last active > 2 h ago still resolves after a restart). Provider for
command-building comes from a per-worktree `.treemon.json` read
(`CodingToolStatus.readConfiguredProvider`), not the retired detectors.

### Overview-history unification (Agents dimension)

The **Agents** counts in each historical bucket are aggregated on read from `activity_events` (each
session's status as of that time → active/idle, skill → `Activity.classify`), rather than logged as
pre-aggregated snapshots every scheduler cycle. **Tasks** counts (beads) stay snapshot-based. The
read path merges the two into the unchanged `OverviewSnapshot` shape, so `OverviewChart.fs` and
`getOverviewHistory` are untouched.

### Reporting extension (`src/Extension/reporting/`)

A passive reporting-only extension (`extension.mjs` + `reporting-core.mjs` +
`@treemon/reporting` `package.json`), installed to
`~/.copilot/extensions/treemon-reporting` **alongside** the untouched `canvas-bridge` by
`Install-ReportingExtension` in `treemon.ps1`. It joins with no tools/canvas and never calls
`session.send`.

- **Fan-out ports** from `TREEMON_PORTS` (comma-separated) → `TREEMON_PORT` → `5000`; each report is a
  fire-and-forget POST to every port (a non-owning instance 404s — swallowed). This lets a validation
  instance run side-by-side with prod, both fed by the same sessions.
- **SDK → wire mapping:** `assistant.turn_start`→`turn_started`, `assistant.message` (non-blank)→
  `assistant_message`, genuine `user.message` (non-blank)→`user_prompt`, `skill.invoked`→
  `skill_invoked`, `elicitation.requested` / `user_input.requested`→`awaiting_user_input`,
  `elicitation.completed` / `user_input.completed`→`user_input_completed`,
  `subagent.started`→`background_agent_started`,
  `subagent.completed` / `subagent.failed`→`background_agent_finished`,
  `assistant.intent` (non-blank)→`intent_reported`, `session.title_changed` (non-blank)→
  `title_reported`, `assistant.turn_end`→`turn_ended`, `session.idle`→`went_idle`,
  `session.usage_info`→`usage_info` (`currentTokens` + `tokenLimit`). (`ask_user` emits
  `elicitation.requested` / `.completed` in Copilot CLI 1.0.71+ with the prompt in `data.message`;
  older builds used `user_input.*` with `data.question` — both are handled, question reads
  `data.message ?? data.question`.) Blank-text messages and invalid usage gauges are dropped.
- **Background-agent activity:** `subagent.started`, `subagent.completed`, and `subagent.failed`
  map directly to background-agent lifecycle wire reports carrying `data.toolCallId`. The extension
  drops blank or over-512-code-unit IDs before POST without trimming or truncating accepted IDs. It
  keeps no active-agent set and performs no status suppression; the server fold and persistence own
  live ordering, source-event deduplication/history, and effective-status precedence. Lifecycle
  clocks are intentionally not recovered after a Treemon restart.
- **Title bootstrap:** after live subscriptions are installed and persisted history is replayed,
  the extension installs heartbeat/cleanup handling and reads `session.rpc.metadata.snapshot()` in
  a caught background task. If no nonblank live `session.title_changed` arrived during startup, a
  nonblank `summary` is emitted as `title_bootstrap`. The server persists it on an independent
  state-hydration path, so it neither pollutes source-event history nor blocks older replay/status
  transitions. This recovers the current title on join/rejoin even though `session.title_changed`
  is ephemeral and absent from `getEvents()`. A slow or failed metadata RPC cannot block heartbeat
  or other reporting.
- **Intent is opportunistic:** nonblank `assistant.intent` events are still accepted and persisted,
  but ordinary turns are not expected to emit one. The extension does not synthesize intent from
  assistant prose, tools, or skills.
- **ask_user exactness:** the extension forwards request, completion, and idle events without local
  lifecycle state. The server compares persisted request/completion clocks, so WaitingForUser is
  correct even when a later idle POST arrives first. A genuine `user_prompt` also completes the
  wait.
- **Heartbeat (60 s)** re-asserts liveness via the dedicated `heartbeat` kind, which bumps
  `last_seen` without re-folding status, moving the last-write-wins clock, or appending to history.
  Heartbeats sent before a session has a status row are ignored. An open idle session thus stays
  blue; a closed session stops heartbeating, goes stale, and collapses to grey NoSession.

## Decisions

- **Push-only, clean cutover.** All parsing deleted; the push model is the sole status source. The
  user is CLI-only, explicit events beat mtime inference, and three detectors collapse to one pure
  fold.
- **Filter before the fold, at the boundary with enough context.** Sub-agent (`agentId`) and
  skill-context content filtering remain in the extension, where trusted SDK metadata is available.
  Explicit background-agent lifecycle is forwarded because it affects status.
  Runtime system reminders are filtered by the server's shared user-message classifier before
  ingestion, so display policy and state policy cannot diverge.
- **Background lifecycle is intentionally process-local.** The extension forwards facts without
  local state; the server fold keeps per-tool start/finish clocks and effective-status precedence
  while Treemon is running. Those clocks are not stored or reconstructed after restart. This
  knowingly allows a mid-agent restart to fall back to the persisted parent/base status, chosen to
  avoid a lifecycle table, hydration, and special transaction paths.
- **Opaque lifecycle IDs are bounded, never transformed.** Both producer and server enforce the same
  512-code-unit `toolCallId` limit. The producer drops overlong events and the server rejects them;
  accepted IDs are preserved exactly because truncation could merge distinct agents.
- **`session.idle` is server-resolved; freshness is only a crash net.** Idle updates the base
  lifecycle status, while independent request/completion and per-agent lifecycle clocks decide
  whether WaitingForUser or Working still overlays it.
- **WaitingForUser outranks background Working.** An outstanding user-input request remains the
  effective status even while background agents run; background activity only overrides base Idle.
- **Completed clocks have a five-minute in-memory lifetime.** Keeping both start and terminal clocks
  during the supported late-event window preserves duplicate/out-of-order behavior without allowing
  a continuously-active session to accumulate them indefinitely. Ordinary reports and heartbeats
  prune older completed clocks while preserving every active agent; lifecycle events older than the
  same cutoff are ignored. Restart still clears all lifecycle state.
- **A stale resume clears the crashed run in memory.** Every report path shares one pre-fold
  stale-session check. It drops the prior process-local lifecycle before folding the resumed report,
  preventing the reused session id from retaining crashed work while preserving genuinely new starts.
- **No durable `Done`.** A finished turn (`turn_ended`) reads as Idle; the next `turn_start` re-asserts
  Working within ≤0.1 s, so the mid-loop Idle window is invisible to polling. This matches what the CLI
  actually models (Working / WaitingForUser / Idle).
- **`NoSession` is a 4th `CodingToolStatus` case**, not a `HasOpenSession` flag on `WorktreeStatus` —
  so the dot is a pure function of one status (illegal states unrepresentable).
- **`openWindow` (3 min) < `stalenessTimeout` (5 min).** A dead/crashed agent stops being open and
  shows grey after ~3 min rather than lingering blue; the crash-net stays as a defensive backstop.
- **Time-since-idle is stamped at the transition, not read live.** An open idle session heartbeats, so
  its `last_seen` advances; reading it live would reset the chip every poll. Capture once when the
  collapsed status enters Idle and hold it.
- **Footer decoupled from the dot.** Card messages/skill come from the active winner, otherwise the
  session with the greatest `UpdatedAt`; retained fallback covers sessions outside the live window.
- **Auto-sync prioritizes delivery liveness over footer identity.** It uses the active open winner,
  then the greatest-`UpdatedAt` open idle session, and falls back to retained identity only when
  nothing is open. This avoids launching a second session while an idle CLI can accept the prompt.
- **Display pick ≠ footer pick ≠ resume pick.** Display = greatest-`UpdatedAt` *open active*; footer =
  active winner or greatest-`UpdatedAt` fallback; resume = greatest-`UpdatedAt` session from the
  durable store via a scalar query. `LastSeen` continues to drive openness, freshness, retention,
  and per-session dot ordering, but never representative footer/resume selection.
- **Future timestamps are normalized, not trusted; free text is length-capped server-side** (see
  Technical Approach) — the loopback endpoint clamps `occurredAt` and uses it for nested message
  timestamps before folding or comparing activity freshness.
- **Context usage is a durable last-known gauge, not activity history.** Persist the token values and
  their independent ordering timestamp on `session_status`; restore them on restart, but never append
  usage snapshots to `activity_events`.
- **Title is bootstrapped; intent is optional.** Metadata summary supplies the durable join/rejoin
  title when the ephemeral live event was missed. Bootstrap is persisted as state, not lifecycle
  history, and has an independent ordering path. `assistant.intent` remains source-authored
  enrichment and is never inferred by Treemon.
- **`HasActiveSession` (tmux/window) stays distinct from push openness** — the coding-tool dot uses
  push openness only; no merge.
- **Explicit session-closed event deferred.** None of the wire kinds signals "closed"; an instant-grey
  close-ping would need a new server kind. The openness window covers clean exit and hard crash alike,
  so it is not required for correctness.
- **Two extensions today, one in the end state.** Reporting lives in its own `treemon-reporting`
  extension alongside the untouched `canvas-bridge`, keeping the canvas path unchanged and avoiding a
  `canvas_take_ownership` collision. Folding canvas + reporting into a single `treemon-bridge` is a
  future consolidation step.

## Key Files

| File | Role |
|------|------|
| `src/Server/SessionActivity.fs` | Domain (`SessionEvent`, `SessionActivityReport`), `SessionStatus`, pure `fold`, `freshnessAdjusted`, `pickActive`; the `openWindow` / `stalenessTimeout` / `idleWindow` timings. |
| `src/Server/SessionActivityStore.fs` | SQLite (WAL) persisted base state, retained-footer and scalar-resume queries, pruning, source-event dedupe, and history. |
| `src/Server/SessionActivityService.fs` | Wire parsing and single-writer integration for root, user-input, and background-agent lifecycle plus independent metadata, context, and liveness paths. |
| `src/Server/UserMessageFormatting.fs` | Server-owned user-message classification: suppress system reminders and project canvas prompts for activity and footer display. |
| `src/Server/CodingToolStatus.fs` | `fromPushSessions` / `collapseByWorktree` (openness dot + decoupled footer), `readConfiguredProvider`. |
| `src/Server/RefreshScheduler.fs` | `UpdateSessionStatus`; idle-window eviction of the live map; `CodingToolSinceByWorktree` stamps. |
| `src/Server/WorktreeApi.fs` | Builds the card's coding-tool fields + `CodingToolSince` from push state. |
| `src/Server/Program.fs` | Routes `/api/session/activity`; starts the service + rebuild. |
| `src/Shared/Types.fs` | `AgentActivity`, `ContextUsage`, and per-session `SessionDot` wire types used by cards and Overview. |
| `src/Client/CardViews.fs`, `src/Client/index.html` | Intent/title activity line, skill pill, per-session dots/context donuts, and status colours. |
| `src/Extension/reporting/` | Stateless SDK-to-wire mapping, including background-agent lifecycle, plus title bootstrap, usage gauge, heartbeat, and package metadata. |
| `treemon.ps1` | `Install-ReportingExtension` — installs `treemon-reporting` alongside `canvas-bridge`. |

## Related Specs

- `docs/spec/worktree-monitor.md` — architecture, domain types, refresh model.
- `docs/spec/beads-overview-band.md` / `docs/spec/overview-drilldown.md` — the Overview Agents band (Idle group).
- `docs/spec/resume-last-session.md` — consumes the store's durable scalar resume lookup.
- `docs/spec/remoting-csrf-hardening.md` — the `csrfGuard` the endpoint reuses.
- `docs/spec/native-session-management.md` — `HasActiveSession` (window-based; distinct from push openness).
