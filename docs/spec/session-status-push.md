# Session Status via Push Model

## Goals

- Replace **all** coding-tool log parsing with a **push model**: the Copilot CLI extension
  observes the SDK session event stream and reports status to Treemon over HTTP.
- Status must be **immediate and exact** (driven by explicit lifecycle events), not inferred
  from log-file mtime heuristics.
- The agent must be **completely unaware** — reporting is passive (never injects context,
  never calls `session.send`), with negligible per-event cost.
- **Durable**: live status survives a server restart; the raw event stream is persisted so
  the historical Overview aggregation can be computed from it.
- **Simple by design**: model events as a closed union and push all ambiguity-resolving
  filtering to the source, so the server-side logic is a tiny pure fold with no branching
  for sub-agents, injections, or bracket depth.
- Delete the three log-parsing detectors (`CopilotDetector`, `ClaudeDetector`,
  `VsCodeCopilotDetector`) — the end state is push-only (the user is CLI-only). The build is
  push-only from the start; there is **no in-process parser and no source flag**. The
  big-bang refactor is de-risked by running this whole build as a **second Treemon instance
  side-by-side with the current `main` instance** (see Technical Approach) and comparing the
  two dashboards before switching over.

## Expected Behavior

### Status reporting

- Each Copilot CLI session, via the Treemon extension, reports session-activity events to
  `POST /api/session/activity` as they occur. The card reflects the new status on the
  client's next poll (existing polling; no SSE).
- The status shown per worktree is exactly what the fold derives:
  - `assistant.turn_start` / a genuine user prompt / an assistant message → **Working**
  - `user_input.requested` (i.e. `ask_user`) → **WaitingForUser**
  - `assistant.turn_end` → **Done**
  - `session.idle` → **Idle**
- **Current skill**, **last user message**, and **last assistant message** are surfaced from
  the same session that owns the shown status (never a mix across sessions).
- A session that dies without emitting `session.idle` (crash, closed laptop) is treated as
  **Idle** once its `last_seen` is older than the **staleness timeout (~5 min**, a few missed
  heartbeats — faster than the old 30-min mtime staleness, since the extension heartbeats
  every ~30–120s). `last_seen` is the direct analogue of the old file mtime.
- The **idle window** for `pickActive` and restart `loadLiveStatuses` is **2h** (reuses the
  existing idle cutoff): sessions quiet longer than this are not considered live.

### Multiple sessions in one worktree

The card collapses all live sessions for a worktree to one status:
- **Drop Idle, then the most-recent *active* session wins; all Idle → Idle.** (Not raw
  latest-update — a session that just went Idle must not hide an actively-Working sibling.)
- Every displayed field (status, skill, last user, last assistant) is read from that one
  winning session, by construction.
- Only sessions whose `last_seen` is within the idle window are considered.

### Resume

`Resume last session` picks the most-recent session **regardless of active/idle** (the
session the user last touched), distinct from the display pick. It reads the session id from
the store instead of scanning log directories.

### Restart

On server start, live per-session status is rebuilt from SQLite before serving, so cards are
correct immediately without waiting for new events.

### Agent-unaware

The extension only *subscribes*; it never sends the session a message or injects context for
status reporting. The agent transcript is identical with reporting on or off.

## Technical Approach

### Domain model (make illegal states unrepresentable)

The server owns the domain; the extension is a thin forwarder. A pushed report:

```fsharp
type SessionId   = SessionId of string
type EventId     = EventId of string
type PushProvider = CopilotCli            // extensible: | CopilotApp | ...

type Message = { Text: string; At: DateTimeOffset }

/// The only events that bear on status. Anything else the extension never sends,
/// so the server has no "irrelevant event" branch to carry.
type SessionEvent =
    | TurnStarted
    | UserPrompt of Message               // a genuine user prompt (never a skill-context injection)
    | AssistantMessage of Message
    | SkillInvoked of name: string
    | AwaitingUserInput of question: Message option  // ask_user; carries the question text to surface
    | TurnEnded
    | WentIdle

type SessionActivityReport =
    { SessionId: SessionId
      WorktreePath: WorktreePath
      Provider: PushProvider
      EventId: EventId
      OccurredAt: DateTimeOffset
      Event: SessionEvent }
```

The per-session fold state — deliberately smaller than today's `SessionScanCache`
(no `SubagentDepth`, no `LastAssistantWasAskUser`, no separate raw-status type):

```fsharp
type SessionStatus =
    { Status: CodingToolStatus            // fold sets this directly, incl. Idle from WentIdle
      Skill: string option
      LastUserMessage: Message option
      LastAssistantMessage: Message option }

let fold (s: SessionStatus) (e: SessionEvent) : SessionStatus =
    match e with
    | TurnStarted            -> { s with Status = Working }
    | AssistantMessage m     -> { s with Status = Working; LastAssistantMessage = Some m }
    | SkillInvoked name      -> { s with Skill = Some name }
    | AwaitingUserInput q    -> { s with Status = WaitingForUser; LastAssistantMessage = (q |> Option.orElse s.LastAssistantMessage) }
    | TurnEnded              -> { s with Status = Done }
    | WentIdle               -> { s with Status = Idle }
    | UserPrompt m ->
        // A reply to an ask_user keeps the running skill; any other prompt is a new request.
        let keepSkill = s.Status = WaitingForUser
        { s with
            Status = Working
            Skill = (if keepSkill then s.Skill else None)
            LastUserMessage = Some m }
```

This fold is **the same state machine** as `CopilotDetector.foldForwardEvent`, minus the
sub-agent and injection handling — those are eliminated at the source (see below), not
branched on here. It is pure and append-friendly, so folding a later batch onto an earlier
result equals folding the whole stream.

The `WentIdle` case sets `Idle` from the explicit `session.idle` event. A staleness wrapper
is only a **crash safety-net**: a `Working`/`WaitingForUser`/`Done` status whose `last_seen`
is older than the timeout reads as `Idle`.

### Source-side filtering (why the server stays simple)

The extension forwards **only** what the fold needs, so three sources of complexity never
reach the server:
1. **Sub-agent events** — every SDK event carries `agentId` (absent for the root agent). The
   extension drops any event with an `agentId`. → the server has no depth tracking.
2. **Skill-context injections** — a skill's own `<skill-context>` injection arrives as a
   `user.message`; the extension drops it (same `source`+content markers the parser used). →
   the server has no injection branch; every `UserPrompt` it sees is genuine.
3. **Irrelevant events** — the extension maps only the ~7 relevant SDK event types; all
   others are ignored. → the server's `SessionEvent` union has no catch-all.

### Ingestion: single-writer mailbox + endpoint

- `POST /api/session/activity` handler mirrors `canvasRegisterHandler`: JSON DTO →
  domain `SessionActivityReport`, validate, known-worktree guard, `HttpSecurity.csrfGuard`.
- **Wire contract — the single coupling point between `reporting.mjs` (producer) and the
  handler (consumer); both tasks MUST implement this exact set.** The POST body is one report:
  `{ sessionId, worktreePath, provider, eventId, occurredAt, kind, message?, skillName? }`,
  where `kind` is exactly one of the seven the fold consumes and maps 1:1 onto `SessionEvent`
  (no catch-all):
  `turn_started`→`TurnStarted`, `user_prompt`→`UserPrompt`, `assistant_message`→
  `AssistantMessage`, `skill_invoked`→`SkillInvoked`, `awaiting_user_input`→`AwaitingUserInput`,
  `turn_ended`→`TurnEnded`, `went_idle`→`WentIdle`. `message` (`{ text; at }`) is present for
  `user_prompt`, `assistant_message`, and `awaiting_user_input` (the ask_user question, folded
  into `LastAssistantMessage`); `skillName` only for `skill_invoked`; `turn_started` /
  `turn_ended` / `went_idle` carry neither. An unknown `kind` is a validation error (rejected),
  never silently dropped.
- A dedicated `SessionActivity` `MailboxProcessor` is the **single writer** (the only mutable
  boundary). Per report: `fold` the event onto that session's state → update the in-memory
  `Map<SessionId, SessionStatus>` → persist (upsert + append) → feed `RefreshScheduler`
  (`UpdateSessionStatus`). Live reads are served from the `Map`; SQLite is the durable mirror.
- Idempotency/ordering: upsert only when `OccurredAt >= updated_at`; `activity_events` dedupes
  on `EventId`. Serialised writes mean no lock contention.

### Persistence (SQLite, WAL)

```sql
session_status(session_id PK, worktree_path, provider, status, current_skill,
  last_user_msg, last_user_ts, last_asst_msg, last_asst_ts, updated_at, last_seen);
CREATE INDEX ix_status_worktree ON session_status(worktree_path);

activity_events(event_id PK, session_id, worktree_path, provider, kind, status, skill, ts);
CREATE INDEX ix_events_ts ON activity_events(ts);
```

PRAGMAs `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000`; driver
`Microsoft.Data.Sqlite`. Functions: `upsertStatus`, `appendEvent`, `loadLiveStatuses`
(restart rebuild, `last_seen` within the idle window), `pruneOld` (retention timer),
`queryWindow` (history substrate — see Decisions). WAL lets `queryWindow` read concurrently
with the mailbox writer.

**Store contract (as built in `SessionActivityStore.fs`).** The store is a class
`SessionActivityStore(dbPath)` (IDisposable; creates the schema on construction) whose ops each
run on their own short-lived `Pooling=false` connection — thread-safe against the single-writer
mailbox and concurrent WAL readers; a keep-alive connection holds the file/WAL open for the
store's lifetime. Row shapes: `StoredStatus` (`SessionId`/`WorktreePath`/`Provider`/`SessionStatus`
+ `UpdatedAt`/`LastSeen`) and `ActivityEventRow` (event fields + post-fold `Status`/`Skill`). All
timestamps persist as **UTC round-trip ("O") strings**, so lexical string comparison equals
chronological order — the `ts`/`last_seen` range filters depend on this. `upsertStatus` is
last-write-wins via `ON CONFLICT(session_id) … WHERE excluded.updated_at >= session_status.updated_at`
(a stale report is a full no-op, not even bumping `last_seen`); `appendEvent` is `INSERT OR IGNORE`
and returns whether it inserted (false = duplicate `event_id`); `pruneOld(cutoff)` trims **both**
tables past the cutoff (`activity_events.ts` and `session_status.last_seen`) and returns the total
rows deleted.

### Multi-session collapse

One function returns the whole winning record, so per-field cherry-picking is
unrepresentable:

```fsharp
/// Drop Idle, most-recent (by last_seen) active wins; all Idle → None.
let pickActive : (SessionStatus * DateTimeOffset) list -> SessionStatus option
```

This is `CodingToolStatus.mostRecentActive` reused across a worktree's sessions instead of
across three detector surfaces.

### Push-only repoint + delete

- `CodingToolStatus` / `WorktreeApi` source the card's coding-tool fields (status, skill,
  last user, last assistant) from the `SessionActivity` live state via `pickActive`;
  `getLastSessionId` reads the store.
- `RefreshScheduler` stops scheduling `RefreshCodingTool`.
- **Delete** `CopilotDetector.fs`, `ClaudeDetector.fs`, `VsCodeCopilotDetector.fs`, the
  three-surface resolution scaffolding in `CodingToolStatus.fs`, and their tests — keeping
  only the pure fold in `SessionActivity.fs`. No source flag, no in-process parser: the build
  is push-only.

### Side-by-side validation (a second instance)

The big-bang refactor is de-risked at the **instance** level, not in-process: run this
push-only build as a **second Treemon instance** next to the current `main` instance and
compare the two dashboards. Dev mode already uses separate ports (5001 API / 5002 canvas /
5174 Vite vs prod 5000). Two things make it clean:
- **Reporting-only extension, fanned out.** During validation a new *reporting-only*
  extension is installed **alongside** the untouched `canvas-bridge`; it POSTs status to a
  **configurable list** of Treemon ports, fanning out to both instances so the *same* sessions
  feed both dashboards (`main` 404s the status route — harmless). It does no canvas, so
  `canvas-bridge` and the canvas path stay completely unchanged during validation.
- **Instance-specific SQLite path.** The DB file is keyed to the instance's port/data dir so
  the validation instance can't collide with anything. All other shared data (worktree scan,
  session logs, canvas files) is read-only.

Once the new dashboard matches, switch over by making it the primary instance.

### Overview-history unification (Agents dimension)

Prerequisite: rebased on `activity-history`. Refactor `OverviewHistory.fs` so the **Agents**
counts in each historical bucket are aggregated on read from `activity_events` (for each
bucket, each session's status as of that time → active/idle, skill → `Activity.classify`),
instead of being logged as pre-aggregated snapshots every scheduler cycle. **Tasks** counts
(beads) stay snapshot-based. The read path merges Tasks (snapshot) + Agents (event-derived)
into the unchanged `OverviewSnapshot` shape; `getOverviewHistory` and `OverviewChart.fs` are
untouched. Remove the agent half of the per-cycle snapshot logging in `RefreshScheduler.loop`.

### Extension (two phases: additive, then consolidated)

**Phase 1 — reporting-only, additive.** A new `treemon-reporting` extension contains only
`reporting.mjs`: subscribe → filter (drop `agentId` sub-agent events + `<skill-context>`
injections) → POST `/api/session/activity` to a **configurable list of Treemon ports**
(fan-out; default one) → `getEvents()` replay on join → status-ping heartbeat carrying
`last_seen`; on `awaiting_user_input` it carries the ask_user question text (surfaced as
`LastAssistantMessage`). It is **passive** and registers **no canvas and no tools** and never
calls `/api/canvas/register`, so it is fully orthogonal to the existing `canvas-bridge` —
canvas is untouched during validation. Both extensions load per session (a harmless double
`joinSession`), but since the reporting one doesn't touch canvas there is **no
`canvas_take_ownership` tool collision and no double canvas-write handling**. Installed
**alongside** `canvas-bridge`, which is left in place.

**Phase 2 — consolidate (post-validation).** Fold canvas + reporting into one `treemon-bridge`
(`extension.mjs` bootstrap + `canvas.mjs` + `reporting.mjs`); `Install-Extension` installs it
and **deletes both `canvas-bridge` and the interim `treemon-reporting` dir** (else double
`joinSession` / duplicate tools). End state: one extension doing canvas + status.

## Decisions

- **Push-only, clean cutover.** All parsing deleted in this effort; the push model is the
  sole status source. Rationale: user is CLI-only; explicit events are strictly better than
  mtime inference; three detectors collapse to one pure fold.
- **Side-by-side validation is instance-level, not in-process.** The build is push-only with
  no parser and no source flag; validation is done by running it as a **second Treemon
  instance** next to `main` and comparing dashboards (enabled by extension port fan-out + an
  instance-specific SQLite path). This keeps the code simple — no dual-source/Compare
  machinery — while still de-risking the cutover.
- **Filter at the source, fold on the server.** Sub-agent (`agentId`) and injection filtering
  live in the extension so the server fold has no branching for them — the single biggest
  simplification vs the current parser.
- **Reuse the F# fold; don't rewrite in JS.** The risky logic is the fold; it stays server-F#
  (compiler assistance + ported tests). The extension is a thin forwarder — no Fable/TS.
- **`session.idle` sets Idle directly; freshness is only a crash net.** Unlike the parser,
  which derived Idle purely from age.
- **Display pick ≠ resume pick.** Display = most-recent-active; resume = most-recent-any.
- **Overview-history unification (rebased onto `activity-history`).** This feature is based
  on top of the `activity-history` branch (landed first), so `OverviewHistory.fs`,
  `OverviewData.fs`, `OverviewChart.fs`, and the `getOverviewHistory` remoting contract
  exist. An `OverviewSnapshot` is `{ Tasks; Agents }`; only the **Agents** dimension is
  derivable from `activity_events` (session status + skill → `Activity.classify`). The
  **Tasks** dimension is beads planning counts, *not* event-sourced here. So the refactor:
  compute the **Agents** history by aggregating `activity_events` over the window on read
  (retiring agent-activity snapshotting), and **keep Tasks snapshot-based**. The read side
  merges the two into the existing `OverviewSnapshot` shape, so `OverviewChart.fs` and
  `getOverviewHistory` are unchanged.
- **One extension in the end state; two during validation.** The end state is a single
  `treemon-bridge` (canvas + reporting) to avoid a permanent double `joinSession`. During
  validation the new reporting lives in a *separate reporting-only* extension installed
  alongside the untouched `canvas-bridge` — keeping the canvas path byte-for-byte unchanged
  and avoiding a `canvas_take_ownership` collision between two canvas-capable extensions.
  Consolidation to one is the final post-validation step.
- **Ingestion concretions (as built in `SessionActivityService.fs`).** The DB path is
  `data/session-activity-{port}.db` (keyed by the server's `--port`, so a side-by-side
  validation instance never collides). The service is started only in the **real monitoring
  path** — demo mode has no scheduler agent, and fixture mode serves synthetic data and takes
  no activity posts (mirrors skipping the scheduler background loop). `LastSeen` and
  `UpdatedAt` are both set to the report's `OccurredAt` on every ingested event (an event is
  also the heartbeat). The in-memory live map applies the **same last-write-wins ordering
  guard** as the store: an out-of-order (older) event is still appended to `activity_events`
  (history substrate) but does not regress the live fold state, the shown card, or the
  `session_status` row. Retention timer: `pruneOld(now − 14d)` every 1h, on the store's own
  connection (WAL + `busy_timeout` handle the concurrent delete); the service owns the store's
  lifetime and disposes it on shutdown.
- **Repoint concretions (as built in `CodingToolStatus.fs` + `WorktreeApi.fs`).** The card's
  coding-tool fields now come from the push live state, not the detectors:
  `CodingToolStatus.collapseByWorktree` groups `DashboardState.SessionStatuses` (the store's
  in-memory reflection) by worktree path and `fromPushSessions` collapses each group —
  freshness-adjust each session (the 5-min staleness net rewrites a quiet Working/Waiting/Done
  to Idle), then `SessionActivity.pickActive` (drop Idle, most-recent active wins) with **all**
  fields (status/skill/last-user/last-assistant/provider) taken from the one winner. The push
  provider is Copilot-only, so an active card always reads `Copilot`; last-user/last-assistant
  keep the detectors' 80/120-char single-line truncation and `"copilot"` source. The 5-min
  freshness **subsumes** the 2h idle window for the *display* pick (anything quiet >5 min is
  already Idle and dropped), so `fromPushSessions` needs no separate window filter. The collapse
  map is built once per `getWorktrees` / `getSyncStatus` call (SessionStatuses is global, keyed by
  the same normalised path as `WorktreeInfo.Path`) and feeds both the worktree cards and the
  recent-messages endpoint. `getLastSessionId` is the **distinct resume pick** — most-recent-any
  by `last_seen` over the worktree's `SessionStatuses` (no drop-Idle, no freshness), returning the
  stored session id (→ `copilot --resume <id>`, or `--continue` when the worktree never reported).
  `resolveProvider` still reads the detector-fed `CodingToolData` for command-building; it
  harmlessly degrades to the `Copilot` default once the detectors stop populating it (task 9k8).
- **Delete concretions (task 9k8, as built).** Detectors `CopilotDetector.fs`/`ClaudeDetector.fs`/
  `VsCodeCopilotDetector.fs` and their tests are deleted; the three-surface resolution scaffolding
  (`ProviderResult`, `mostRecentActive`, `pickActiveProvider`, `pickActiveSkill`, `resolveStatus`,
  `SessionResults`, `getClaudeResult`, `gatherResultsFromFiles`, `getRefreshData`) is removed from
  `CodingToolStatus.fs` — kept: `readConfiguredProvider`, `CodingToolResult`, the prompt builders
  (`configureTestsPrompt`/`skillInvocation`/`actionPrompt`), and the push funcs. `RefreshScheduler`
  drops the `RefreshCodingTool` task entirely (union case + task-list wiring + `executeTask` branch);
  the now-unfed `UpdateCodingTool` message and `CodingToolData` map are **retained** (out of this
  task's scope) — `effectiveActivity` and `WorktreeApi.resolveProvider` read the now-always-empty
  map and harmlessly degrade (no coding-tool activity signal; `Copilot` provider default). Tests:
  the detector test files + `ClaudeSessionReplayTests.fs` are deleted, `CodingToolOrchestratorTests`
  keeps only `ReadConfiguredProviderTests` (the scaffolding fixtures go), `ServerParsingTests` drops
  `EncodeWorktreePathTests` (its `encodeWorktreePath` lived in the deleted `ClaudeDetector`), and
  `SchedulerTests` per-worktree task counts go 3→2 (Git+Beads). Build is push-only.

## Key Files

| File | Changes |
|------|---------|
| `src/Server/SessionActivity.fs` | **New.** Domain (`SessionEvent`, `SessionActivityReport`, value types), `SessionStatus`, pure `fold`, freshness wrapper, `pickActive`. |
| `src/Server/SessionActivityStore.fs` | **New.** SQLite (WAL) schema + `upsertStatus`/`appendEvent`/`loadLiveStatuses`/`pruneOld`/`queryWindow`. |
| `src/Server/SessionActivityService.fs` | **New.** `SessionActivity` mailbox (single writer) + `POST /api/session/activity` handler + startup rebuild. |
| `src/Server/CodingToolStatus.fs` | Source status/skill/messages/`getLastSessionId` from push live state; drop the three-surface resolution scaffolding (incl. `mostRecentActive`/`ProviderResult`) — the collapse logic lives in `SessionActivity.pickActive`. |
| `src/Server/RefreshScheduler.fs` | `UpdateSessionStatus` message; stop scheduling `RefreshCodingTool`. |
| `src/Server/WorktreeApi.fs` | Build `WorktreeStatus` coding-tool fields from push state. |
| `src/Server/Program.fs` | Route `/api/session/activity`; start the SessionActivity service + rebuild. |
| `src/Server/Server.fsproj` | Add `Microsoft.Data.Sqlite`; add/remove `<Compile>` entries. |
| `src/Server/CopilotDetector.fs`, `ClaudeDetector.fs`, `VsCodeCopilotDetector.fs` | **Deleted.** |
| `src/Extension/` (`reporting.mjs`; later `+canvas.mjs`/`extension.mjs`) | Phase 1: reporting-only extension. Phase 2: consolidate canvas + reporting into one `treemon-bridge`. |
| `treemon.ps1` | Phase 1: install `treemon-reporting` alongside `canvas-bridge`. Phase 2: install unified `treemon-bridge`, delete `canvas-bridge` + `treemon-reporting`. |
| `src/Tests/` | Port fold tests to `SessionActivity`; store tests; remove detector tests. |

## Related Specs

- `docs/spec/worktree-monitor.md` — architecture, domain types, refresh model.
- `docs/spec/resume-last-session.md` — consumes `getLastSessionId`; must keep working.
- `docs/spec/canvas-pane.md` / `canvas-browser-fallback.md` — the extension being extended.
- `docs/spec/remoting-csrf-hardening.md` — the `csrfGuard` the new endpoint reuses.
- `docs/spec/native-session-management.md` — `HasActiveSession` (window-based; unchanged).
