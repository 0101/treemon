# Idle-only coding-tool status (blue idle / grey no-session)

Refines the push-model coding-tool status (spec: session-status-push.md) to match what the Copilot
CLI actually models: a session is **Working**, **WaitingForUser**, or **Idle** — there is no durable
"Done". The transient `Done` state (from `assistant.turn_end`) is retired; a finished turn reads as
**Idle**. The worktree status dot gains a clean four-way meaning, and idle agents become first-class
in the Overview with a "time since idle" readout.

## Goals

- Drop the `Done` coding-tool status entirely. A finished turn (`turn_ended`) and an explicit
  `session.idle` (`went_idle`) both map to **Idle**. Verified safe: `turn_ended` is a clean idle edge
  — during active work the next `turn_started` re-asserts Working within ≤0.1 s, so the mid-loop Idle
  window is invisible to polling; only a genuinely-finished turn settles on Idle.
- Give the worktree status dot a four-way meaning driven purely by the coding-tool status:
  - **red** — Working (agent actively running), grouped by activity as today.
  - **yellow** — WaitingForUser (parked on an `ask_user`).
  - **blue** — Idle **with an open session** (the CLI is running, the agent is idle waiting for you).
  - **grey** — **No open session** (the worktree is tracked but has no live Copilot session).
- Surface idle agents in the Overview "Agents" dimension as a distinct **blue Idle group**, each agent
  showing **time since it entered Idle** (reusing the per-agent "time in category" chip from #119,
  `CodingToolSince`). Worktrees with no open session are **not** agents and stay out of the dimension.
- Keep the existing crash safety-net: a Working/WaitingForUser session whose heartbeat has lapsed
  decays so a dead session never shows as actively running.
- **Card footer persists whenever we have data.** The last user prompt, last assistant message, and
  running skill stay on the card footer for a worktree whenever that data exists — going Idle (or
  losing the open session) must NOT blank the footer. This is a bug in the current push build: the
  status collapse (`pickActive`) drops Idle sessions, so an idle worktree returns the blank
  `idlePushResult` and the footer/event-log disappears. Footer data must be sourced from the
  most-recent session (most-recent-*any*, like resume), decoupled from the status-dot's open-session
  collapse.

## Expected Behavior

Card status dot (single source of truth = the worktree's collapsed coding-tool status):

| Situation | Status | Dot |
|---|---|---|
| Agent mid-turn (open session, Working) | Working | red |
| Agent parked on `ask_user` (open session) | WaitingForUser | yellow |
| Agent finished / between prompts, **CLI still open** | Idle | **blue** |
| No live Copilot session for this worktree (never reported, or the CLI exited) | NoSession | **grey** |

Overview "Agents" dimension:
- **Working** agents grouped by classified activity (Investigating/Executing/…) — red, unchanged.
- **WaitingForUser** agents — yellow, unchanged.
- **Idle (open)** agents — a new **blue** group, each chip showing time-since-idle (`CodingToolSince`).
- **NoSession** worktrees are excluded (they are not agents); their card simply shows the grey dot.

Transitions (one poll cycle ≈ seconds):
- `turn_started`/`user_prompt`/`assistant_message` → Working (red). `skill_invoked` sets the activity.
- `turn_ended` → Idle (blue) — instantly re-asserted to Working by the next turn during active work.
- `awaiting_user_input` → WaitingForUser (yellow); the answer (`user_prompt`) → Working.
- CLI exits / crashes (heartbeat lapses) → the session stops being "open" → the worktree collapses to
  **NoSession** (grey).
- A Working/WaitingForUser session whose heartbeat lapses past the crash-net → Idle, then NoSession
  once no session remains open.

## Technical Approach

### 1. Domain: retire `Done`, add `NoSession`
`CodingToolStatus` becomes `Working | WaitingForUser | Idle | NoSession` (drop `Done`). `Idle` = an
open-but-idle session (blue); `NoSession` = no open session for the worktree (grey), replacing today's
`Idle`-default blank card.

> **Atomicity:** removing the `Done` case is a cross-project compile break — Client, Server, Cli and
> the (single) Tests project that references all three fail together. So the DU change and *every*
> consumer (domain maps, CLI formatter, the card dot + Overview groups in §4, the demo fixture, and
> all affected tests) land in **one** task to stay build-green. The openness collapse (§3) and
> time-since-idle wiring are additive server changes that don't reshape the DU, so they are a separate
> follow-up task on top.
- `SessionActivity.fold`: `TurnEnded -> Idle`, `WentIdle -> Idle` (drop `Done`). A session's own status
  is only ever Working/WaitingForUser/Idle — `NoSession` is a *worktree-level* collapse result, never a
  stored per-session status.
- `SessionActivity.freshnessAdjusted`: keep the single crash-net (Working/WaitingForUser older than
  `stalenessTimeout` → Idle). No `Done` branch.
- `SessionActivityStore` status string map: drop `"done"` (persist only working/waiting/idle).

### 2. Extension: heartbeat idle sessions (openness signal)
Today the heartbeat fires only for `working`/`waiting`, so an idle session's `last_seen` freezes —
indistinguishable from a closed one. Extend `heartbeatTick` to also re-assert **idle** (re-send
`went_idle`, a status-preserving no-op fold) every `HEARTBEAT_INTERVAL_MS`. Then an *open* idle session
keeps refreshing `last_seen`, while a *closed* session's `last_seen` goes stale — the server's openness
signal. (Optional, decided below: also emit an explicit close on SIGTERM/SIGINT for an instant grey.)

### 3. Server: openness → blue-vs-grey + time-since-idle
- **Openness window**: a session is "open" iff `now - last_seen < openWindow` (a few missed heartbeats,
  e.g. ~3 min — distinct from the 2 h `idleWindow` used for memory eviction/resume). `fromPushSessions`
  collapses only **open** sessions; with no open session the worktree is **NoSession** (grey).
- **Time-since-idle**: the `CodingToolSince` field from #119 exists but is fed `None` on the push path
  (the helper was never wired to push), so this **builds** the transition-stamping. Stamp the moment a
  worktree's collapsed status **enters Idle** — the `Working`/`WaitingForUser`→`Idle` transition, i.e.
  the last *active* `last_seen` (turn-end time) — and **freeze** it until the status changes; feed
  `WorktreeStatus.CodingToolSince` (currently `None` in `WorktreeApi`) from it. **Do not** recompute it
  from the current `last_seen`: an open idle session heartbeats every ~60 s so its `last_seen` keeps
  advancing, and reading it live would reset the chip to ~0 every poll (time-since-*last-write*, not
  time-*in-category*). Capture once at the transition and hold it (per-worktree server state or a
  persisted transition timestamp); a new `Working` turn clears/moves it.
- **Footer data persists (bug fix)**: today `fromPushSessions` reads *all* card fields — status **and**
  the last user/assistant messages + skill — from the single `pickActive` winner, and `pickActive`
  drops Idle sessions, so an idle-only worktree returns the blank `idlePushResult` and the card
  footer/event-log vanishes. **Decouple** the two: the status dot comes from the open-session collapse
  (above), but `LastUserMessage` / last-assistant / `CurrentSkill` are sourced from the **most-recent
  session that has them** (most-recent-*any*, the same pick `getLastSessionId` uses for resume), so the
  footer stays populated while the session is idle and while any session for the worktree remains in
  the store (retention/`idleWindow`). The client footer is already data-driven (`hasContent` in
  `CardViews.fs` keys off `LastUserMessage`/`CurrentSkill`), so no client change is needed once the
  server stops blanking the data — but confirm the footer renders for Idle and NoSession worktrees that
  have retained messages.

### 4. Client: dots + Overview Idle group
- Card dot (`CardViews.fs` `ctClassName`/`ctTooltip`, `index.html` `.ct-dot.*`): `Idle -> blue`
  (`#89b4fa`, reusing the retired `.done`/`.activity-stopped` blue), `NoSession -> grey` (`#585b70`);
  remove `Done`.
- Overview Agents (`OverviewData.fs` `AgentGroupKind`, `OverviewBand.fs`): replace the dead
  `Stopped` (Done) group with an **Idle** group (blue), included via `CodingTool = Idle`, each member
  carrying `CodingToolSince` for the time chip; `NoSession` excluded like today's `Idle`.

## Decisions

Resolved during planning:

1. **`NoSession` as a `CodingToolStatus` case vs a separate flag.** **Decided: a 4th DU case** so the
   dot is a pure function of one status (illegal states unrepresentable), over keeping 3 statuses plus
   a `HasOpenSession` bool on `WorktreeStatus`.
2. **Crash-net vs openness for a dead *Working* session.** **Decided: `openWindow ≈ 3 min <
   stalenessTimeout` (5 min)** so a dead/crashed agent stops being open and shows **grey** (gone) after
   ~3 min rather than lingering blue. The crash-net stays as a defensive Working/WaitingForUser→Idle
   net but, with openness filtering applied first, it rarely fires.
3. **Explicit session-closed event.** **Decided: optional / nice-to-have.** A close-ping on
   SIGTERM/SIGINT gives an instant grey on clean exit; implement only if trivial, otherwise the
   openness window (~3 min) covers both clean exit and hard crash. Not required for correctness.
4. **`HasActiveSession` reconciliation.** **Decided: leave `HasActiveSession` (tmux/window) as-is** —
   it stays distinct from push "open session"; the coding-tool dot uses push openness only. No merge.

Resolved during the `abd` implementation (the atomic DU reshape):

5. **`idlePushResult` renamed to `noSessionPushResult`.** Its `Status` is now `NoSession` (the blank
   grey default a quiet worktree collapses to until openness lights up blue `Idle`), so the old
   `idle*` name was misleading. Same value, clearer name; call sites in `CodingToolStatus.fs`,
   `WorktreeApi.fs`, and `CodingToolPushSourceTests.fs` updated together.
6. **Orphaned `expected-statuses.jsonl` fixture left untouched.** `src/Tests/fixtures/claude/multi-session/expected-statuses.jsonl`
   still carries a trailing `"status": "Done"`, but no F#/JS code references it (its `getStatusFromFiles`
   replay test described in `worktree-monitor.md` doesn't exist in the tree). It belongs to the
   extension/worktree-monitor domain (task `azk`), so it's out of scope here and was not modified.

## Related Specs

- `docs/spec/session-status-push.md` — the push model this refines (feature s16).
- `docs/spec/beads-overview-band.md` / `docs/spec/overview-drilldown.md` — the Overview Agents band.
- PR #119 (merged) — per-agent "time in category" (`CodingToolSince`), reused here for time-since-idle.
