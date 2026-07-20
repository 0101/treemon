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
- **Agent-unaware.** Reporting is passive: the extension only subscribes, never calls `session.send`
  or injects context. The transcript is identical with reporting on or off.
- **Durable.** Live status survives a server restart (rebuilt from SQLite); the raw event stream is
  persisted so the historical Overview can be aggregated from it.
- **Simple.** Events are a closed union; the server logic is a tiny pure fold with no branching for
  sub-agents, injections, or bracket depth — all ambiguity is filtered at the source.
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

The fold derives a session's status from events:
- `assistant.turn_start` / a genuine user prompt / an assistant message → **Working**
- `elicitation.requested` / `user_input.requested` (`ask_user`) → **WaitingForUser**
- `assistant.turn_end` / `session.idle` → **Idle**

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
but rarely fires. A longer **idle window** (2 h) bounds the in-memory live map and the resume pick.

### Footer persists (decoupled from the dot)

The card footer — freshest source-tagged activity, running skill, last user message, last assistant
message — is sourced from the **most-recent session that has them** (most-recent-*any*), NOT from the
status-dot collapse. Going Idle or losing the open session therefore does **not** blank the footer; it
stays populated while any session for the worktree remains in the store (retention / idle window).

### Multiple sessions in one worktree

A worktree's live sessions collapse to one card via two decoupled picks:
- **Status dot** — among *open* sessions, drop Idle and the most-recent *active* session wins; all
  Idle → Idle; no open session → NoSession. (Not raw latest-update — a session that just went Idle
  must not hide an actively-Working sibling.)
- **Footer** — the active winner if one runs, else the most-recent session of any status.

### Overview "Agents" dimension

- **Working** agents grouped by classified activity (Investigating/Executing/…) — red.
- **WaitingForUser** agents — yellow.
- **Idle (open)** agents — a blue group, each chip showing **time since it entered Idle**
  (`CodingToolSince`, frozen at the transition into Idle).
- **NoSession** worktrees are excluded (they are not agents); their card just shows the grey dot.

### Resume

`Resume last session` picks the most-recent session **regardless of active/idle** (the session the
user last touched) — distinct from both the display and footer picks. It reads the session id from
the durable store (so it survives a restart even for a session last active > 2 h ago) and issues
`copilot --resume <id>`, or `--continue` when the worktree never reported.

### Restart

On server start, live per-session status is rebuilt from SQLite before serving, so cards are correct
immediately without waiting for new events.

## Technical Approach

### Domain model (make illegal states unrepresentable)

The server owns the domain; the extension is a thin forwarder. `SessionActivity.fs`:

```fsharp
type SessionEvent =
    | TurnStarted
    | UserPrompt of Message                 // a genuine user prompt (never a skill-context injection)
    | AssistantMessage of Message
    | SkillInvoked of name: string
    | IntentReported of Message             // SDK assistant.intent
    | TitleReported of Message              // SDK session.title_changed
    | AwaitingUserInput of question: Message option   // ask_user; carries the question to surface
    | TurnEnded
    | WentIdle
    | Heartbeat                              // liveness only; bumps last_seen, no status fold

type SessionActivityReport =
    { SessionId; WorktreePath; Provider; EventId; OccurredAt; Event }
```

A per-session status is `SessionLevelStatus = Working | WaitingForUser | Idle` (no `NoSession` — that
is a worktree-level collapse result, made unrepresentable per session). The fold state is small (no
`SubagentDepth`, no `LastAssistantWasAskUser`):

```fsharp
type SessionStatus =
    { Status: SessionLevelStatus; Skill: string option
      Intent: Message option; Title: Message option
      LastUserMessage: Message option; LastAssistantMessage: Message option }
```

`fold`: `TurnStarted` / `AssistantMessage` / `UserPrompt` → Working, `SkillInvoked` → set skill,
`IntentReported` / `TitleReported` update their status-neutral fields while preserving the original
change time when identical text is re-emitted, `AwaitingUserInput` → WaitingForUser (question folded
into `LastAssistantMessage`), `TurnEnded` / `WentIdle` → Idle, `Heartbeat` → no-op. A `UserPrompt`
replying to an `ask_user` keeps the running skill; any other prompt starts fresh. `effectiveActivity`
selects the newer intent/title and returns a source-tagged `AgentActivity` (`Intent` or
`SessionTitle`) so the collapse boundary never mislabels a title as intent. The fold is pure and
append-friendly — folding a later batch onto an earlier result equals folding the whole stream.

`freshnessAdjusted` is the **crash net** only: a Working/WaitingForUser status whose `last_seen` is
older than `stalenessTimeout` reads as Idle. `session.idle` already sets Idle directly.

### Source-side filtering (why the server stays simple)

The extension forwards **only** what the fold needs, so three sources of complexity never reach the
server:
1. **Sub-agent events** — every SDK event carries `agentId` (absent for the root); the extension
   drops any event that has one. → no depth tracking on the server.
2. **Skill-context injections** — a skill's `<skill-context>` injection arrives as a `user.message`;
   the extension drops it (source starts `skill-` AND content starts `<skill-context`). → every
   `UserPrompt` the server sees is genuine.
3. **Irrelevant events** — only the 10 relevant wire kinds are mapped; all others are ignored. → the
   `SessionEvent` union has no catch-all.

### Ingestion: endpoint + single-writer mailbox

- `POST /api/session/activity` mirrors `canvasRegisterHandler`: JSON DTO → domain
  `SessionActivityReport`, validate, known-worktree guard, `HttpSecurity.csrfGuard`.
- **Wire contract** — the one coupling point between `extension.mjs` (producer) and the handler
  (consumer). The body is one report:
  `{ sessionId, worktreePath, provider, eventId, occurredAt, kind, message?, skillName? }`. `kind` is
  one of `turn_started`, `user_prompt`, `assistant_message`, `skill_invoked`, `awaiting_user_input`,
  `intent_reported`, `title_reported`, `turn_ended`, `went_idle`, `heartbeat`, mapping 1:1 onto
  `SessionEvent`. `message` (`{ text; at }`) is mandatory for user, assistant, intent, and title
  reports and optional for the ask_user question; `skillName` applies only to `skill_invoked`. An
  unknown `kind` is a validation error, never silently dropped.
- A dedicated `SessionActivity` `MailboxProcessor` is the **single writer** (the only mutable
  boundary). Per report: `fold` onto that session's state → update the in-memory
  `Map<SessionId, SessionStatus>` → persist (upsert + append) → feed `RefreshScheduler`
  (`UpdateSessionStatus`). Live reads come from the map; SQLite is the durable mirror.
- **Ordering guard** (both map and store): upsert only when `OccurredAt >= updated_at`; an
  out-of-order (older) event is still appended to `activity_events` (history substrate) but does not
  regress the live fold or the shown card. `activity_events` dedupes on `EventId`.
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
  intent_text, intent_ts, title_text, title_ts, updated_at, last_seen);
activity_events(event_id PK, session_id, worktree_path, provider, kind, status, skill, ts);
```

PRAGMAs `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000`; each op runs on its own
short-lived connection (thread-safe against the single-writer mailbox and concurrent WAL readers).
Construction applies a bounded, idempotent additive migration for the four intent/title columns so
upgrading an existing durable database preserves retained footer and resume state.
All timestamps persist as UTC round-trip (`"O"`) strings, so lexical comparison equals chronological
order — the `ts` / `last_seen` range filters depend on it. `upsertStatus` is last-write-wins;
`appendEvent` is `INSERT OR IGNORE`; `pruneOld(now − 14d)` runs hourly and trims both tables.
`loadLiveStatuses` rebuilds the live map on restart (rows within the idle window). The service is
started only in the real monitoring path — demo/fixture mode serves synthetic data and takes no posts.

### Collapse to card fields (`CodingToolStatus.fs`)

`collapseByWorktree` groups the live session-statuses by worktree path; `fromPushSessions` collapses
each group with the two decoupled picks (openness-driven status dot + most-recent-any footer, above).
The footer exposes `AgentActivity` as a source-tagged union: the freshest intent/title value keeps
its original source while the card may render both identically. Assistant footer messages use a
direct `(text, timestamp)` value; the enclosing `CodingToolProvider` supplies the rendered provider
label. The push provider is Copilot-only today, so an active card reads `Copilot`.

`CodingToolSince` (time-since-idle) is stamped the moment a worktree's collapsed status **enters**
Idle and frozen until it changes — **not** recomputed from `last_seen` (which an open idle session
keeps advancing via heartbeat, which would reset the chip to ~0 each poll). `WorktreeApi` reads the
frozen stamp for Idle worktrees; a new Working turn clears it.

`getLastSessionId` is the distinct resume pick — most-recent-any by `last_seen` from the **durable
store** (not the idle-window live cache, so a session last active > 2 h ago still resolves after a
restart), returning the stored session id. Provider for command-building comes from a per-worktree
`.treemon.json` read (`CodingToolStatus.readConfiguredProvider`), not the retired detectors.

### Overview-history unification (Agents dimension)

The **Agents** counts in each historical bucket are aggregated on read from `activity_events` (each
session's status as of that time → active/idle, skill → `Activity.classify`), rather than logged as
pre-aggregated snapshots every scheduler cycle. **Tasks** counts (beads) stay snapshot-based. The
read path merges the two into the unchanged `OverviewSnapshot` shape, so `OverviewChart.fs` and
`getOverviewHistory` are untouched.

### Reporting extension (`src/Extension/reporting/`)

A passive reporting-only extension (`extension.mjs` + `@treemon/reporting` `package.json`), installed
to `~/.copilot/extensions/treemon-reporting` **alongside** the untouched `canvas-bridge` by
`Install-ReportingExtension` in `treemon.ps1`. It joins with no tools/canvas and never calls
`session.send`.

- **Fan-out ports** from `TREEMON_PORTS` (comma-separated) → `TREEMON_PORT` → `5000`; each report is a
  fire-and-forget POST to every port (a non-owning instance 404s — swallowed). This lets a validation
  instance run side-by-side with prod, both fed by the same sessions.
- **SDK → wire mapping:** `assistant.turn_start`→`turn_started`, `assistant.message` (non-blank)→
  `assistant_message`, genuine `user.message` (non-blank)→`user_prompt`, `skill.invoked`→
  `skill_invoked`, `elicitation.requested` / `user_input.requested`→`awaiting_user_input`,
  `assistant.intent` (non-blank)→`intent_reported`, `session.title_changed` (non-blank)→
  `title_reported`, `assistant.turn_end`→`turn_ended`, `session.idle`→`went_idle`. (`ask_user` emits
  `elicitation.requested` / `.completed` in Copilot CLI 1.0.71+ with the prompt in `data.message`;
  older builds used `user_input.*` with `data.question` — both are handled, question reads
  `data.message ?? data.question`.) Blank-text messages are dropped.
- **ask_user exactness:** a live-only `pendingAskUser` flag (set on the request, cleared on the
  completion or a genuine `user_prompt`) suppresses `went_idle` while a prompt is unanswered, so the
  card stays WaitingForUser even though `session.idle` is ephemeral. Because the flag is not rebuilt on
  a rejoin, the suppression also fires when the last reported status was `waiting`.
- **Heartbeat (60 s)** re-asserts liveness for **any** established session — working, waiting, or idle
  — via the dedicated liveness-only `heartbeat` kind, which bumps `last_seen` without re-folding
  status, moving the last-write-wins clock, or appending to history. An open idle session thus keeps
  refreshing `last_seen` and stays blue; a closed session stops heartbeating, its `last_seen` goes
  stale, and the worktree collapses to grey NoSession (matching the old mtime freeze). Kept distinct
  from real events so it can never overtake a slightly-earlier real event and drop it via the ordering
  guard.

## Decisions

- **Push-only, clean cutover.** All parsing deleted; the push model is the sole status source. The
  user is CLI-only, explicit events beat mtime inference, and three detectors collapse to one pure
  fold.
- **Filter at the source, fold on the server.** Sub-agent (`agentId`) and injection filtering live in
  the extension so the server fold has no branch for them — the single biggest simplification vs the
  old parser.
- **Reuse the F# fold; don't rewrite in JS.** The risky logic stays server-side F# (compiler help +
  ported tests); the extension is a thin forwarder (no Fable/TS).
- **`session.idle` sets Idle directly; freshness is only a crash net** — unlike the old parser, which
  derived Idle purely from file age.
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
- **Footer decoupled from the dot.** Card messages/skill come from the most-recent-any session, so an
  idle or session-less worktree keeps its footer — fixing the earlier bug where the `pickActive`-only
  collapse blanked idle worktrees.
- **Display pick ≠ footer pick ≠ resume pick.** Display = most-recent *open active*; footer =
  most-recent-any (live); resume = most-recent-any (durable store, survives restart).
- **Future timestamps are normalized, not trusted; free text is length-capped server-side** (see
  Technical Approach) — the loopback ingest endpoint clamps `occurredAt` and uses that normalized
  value for nested message timestamps before any fold or freshest-activity comparison.
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
| `src/Server/SessionActivityStore.fs` | SQLite (WAL) schema + `upsertStatus` / `appendEvent` / `loadLiveStatuses` / `pruneOld` / `queryWindow`. |
| `src/Server/SessionActivityService.fs` | `SessionActivity` mailbox (single writer) + `POST /api/session/activity` handler + startup rebuild + retention timer. |
| `src/Server/CodingToolStatus.fs` | `fromPushSessions` / `collapseByWorktree` (openness dot + decoupled footer), `getLastSessionId` (resume), `readConfiguredProvider`. |
| `src/Server/RefreshScheduler.fs` | `UpdateSessionStatus`; idle-window eviction of the live map; `CodingToolSinceByWorktree` stamps. |
| `src/Server/WorktreeApi.fs` | Builds the card's coding-tool fields + `CodingToolSince` from push state. |
| `src/Server/Program.fs` | Routes `/api/session/activity`; starts the service + rebuild. |
| `src/Client/CardViews.fs`, `src/Client/index.html` | `ct-dot` classes/colours (idle blue, nosession grey); Overview Idle group. |
| `src/Extension/reporting/` | Passive reporting-only extension (`extension.mjs` + `package.json`). |
| `treemon.ps1` | `Install-ReportingExtension` — installs `treemon-reporting` alongside `canvas-bridge`. |

## Related Specs

- `docs/spec/worktree-monitor.md` — architecture, domain types, refresh model.
- `docs/spec/beads-overview-band.md` / `docs/spec/overview-drilldown.md` — the Overview Agents band (Idle group).
- `docs/spec/resume-last-session.md` — consumes `getLastSessionId`.
- `docs/spec/remoting-csrf-hardening.md` — the `csrfGuard` the endpoint reuses.
- `docs/spec/native-session-management.md` — `HasActiveSession` (window-based; distinct from push openness).
