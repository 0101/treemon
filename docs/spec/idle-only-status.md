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
   *(Evaluated and deferred during the `79w` heartbeat work: it is NOT trivial — none of the seven
   wire kinds signals "closed", so an instant-grey close-ping would need a NEW server-side kind, out
   of scope for the extension-only task. The openness window covers clean exit and hard crash alike,
   so `cleanup` stays a pure timer-clear + unsubscribe.)*
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

Resolved during the `azk` implementation (openness + time-since-idle):

7. **Footer keeps the ACTIVE winner when one runs, else falls back to most-recent-any.** The spec's
   "footer from most-recent-*any*" is specifically the fix for the idle/no-session case where the old
   `pickActive`-only collapse returned `None` and blanked everything. When an active session IS
   running, its fields are still the footer (so an older Working session isn't hidden by a newer,
   empty, just-idled sibling — the existing `pickActive` contract). So `fromPushSessions` sources the
   footer from `pickActive` when it wins, else the most-recent session of any status. Decouples the
   footer from the dot only where it matters (idle/no-session), keeping the active-session footer
   intact.
8. **`CodingToolSince` is in-memory server state, stamped event-driven in the scheduler.** Because an
   open idle session heartbeats every ~60 s (its `last_seen` keeps advancing), the transition
   timestamp can't be read live. `DashboardState.CodingToolSinceByWorktree` (a per-worktree
   `Map<path, DateTimeOffset>`) is stamped once by `stampIdleSince` when `UpdateSessionStatus`
   re-collapses a worktree into `Idle` (using the triggering event's time as the turn-end stamp),
   frozen across subsequent idle heartbeats, and cleared when the status leaves Idle. It is fed to
   `WorktreeStatus.CodingToolSince` only while the authoritative (real-`now` openness) status is still
   `Idle`, so a stale stamp for a worktree that has since decayed to grey is ignored. In-memory only:
   a restart re-stamps from the reloaded sessions (chip resets on restart — acceptable; persisting the
   transition timestamp was the heavier alternative and not required). Working/WaitingForUser chips
   stay `None` on the push path (time-in-*activity* needs the client's activity classification and is
   out of this task's scope — time-since-idle is the target).

Resolved during the `frvsched` implementation (F10/F11 lifecycle hygiene for `CodingToolSinceByWorktree`):

9. **Prune the global idle stamp on worktree removal (F10/C-13).** `CodingToolSinceByWorktree` lives on
   `DashboardState` (global), so it can't be pruned inside `removeWorktreeData` (which only touches a
   `PerRepoState`). The `RemoveWorktree` and `UpdateWorktreeList` handlers now `Map.remove` the removed
   path(s) from it directly. Without this a removed-then-recreated path inherited a stale FROZEN stamp
   (`stampIdleSince` freezes existing keys), overstating the chip on reuse. Impact was masked (WorktreeApi
   only surfaces the stamp while the real-`now` status is Idle), but the leak is now closed and a
   path-reuse regression test guards it.
10. **Restart seeding stamps from the NEWEST per-worktree session, not the oldest replayed (F11/C-14).**
   `LoadLiveStatuses` replays oldest-first; feeding rows one-by-one through `UpdateSessionStatus` let the
   oldest idle row stamp and FREEZE the chip, locking in a stale timestamp so the chip OVERSTATED
   time-since-idle for the whole post-restart idle span (not merely reset). Fixed with a batch
   `SeedSessionStatuses` scheduler message (posted by `SessionActivityService.Start`) that seeds the map
   in one shot (identical final live set — eviction measures against the global newest either way) and
   stamps each worktree's chip from its newest session's `last_seen`, collapsed at that time. This
   delivers the accepted "resets on restart" behaviour of Decision #8 **without** reversing the seed
   order to DESC.

Resolved during the `frvw` implementation (worktree-monitor.md `Done`-vocab reconciliation, F4/F5/F6/F7/F12):

11. **`worktree-monitor.md` stale `Done` reconciled by marking dead sections legacy, not by a blind
   `Done`→`Idle` swap.** The card dot (F4) and the `Done`/`Idle`→`Working` Decisions line (F12) describe
   the live model and were updated directly (`Working / WaitingForUser / Idle / NoSession`; "upgrade
   `Idle` to `Working`"). The remaining hits (F5/F6/F7 plus the neighbouring grace-period/parent-subagent
   lines) live in the **Coding Tool Detection** section, which documents the *removed* log-parsing
   detectors (`ClaudeDetector`/`CopilotDetector`/`VsCodeCopilotDetector`/`getStatusFromFiles`, superseded
   by the push model). Per the review's rule-quality note, that section got a single **"Legacy —
   superseded by the push model"** marker and its `Done` references were folded to the historical
   framing (transient `Done` → `Idle`) rather than swapped in place to look current. **Divergence from
   the F5 reviewer suggestion:** the per-provider/per-session enumeration (line 73) is
   `Working/WaitingForUser/Idle` — **not** `…/NoSession` — because `NoSession` is only ever a
   *worktree-level* collapse result, never a stored per-session status (matches the `Types.fs`
   `CodingToolStatus` domain comment and F6's pull-model note that a missing session is `Idle`). The
   broader push-model rewrite of that section (and the stale Key-Files/detector inventory) stays out of
   scope — the orphaned `expected-statuses.jsonl` fixture is still untouched per Decision #6.

Resolved during the `frvf9` implementation (file-size-limit F9/C-03):

12. **Time-since-idle tests extracted to `CodingToolSinceTests.fs`; goal is "stop this diff from bloating
   the file", not an absolute sub-1000 `SchedulerTests.fs`.** The `file-size-limit` rule flags a file only
   when it is **both** over 1000 lines **and** the diff significantly grows it — "already-large files that
   aren't growing much are not flagged". The idle-only feature added the whole `CodingToolSince` test block
   to `SchedulerTests.fs`, so the fix is to move that diff-added block out. All four cohesive time-since-idle
   fixtures — `StampIdleSinceTests`, `CodingToolSinceByWorktreeTests`, `CodingToolSincePruningTests`,
   `SeedSessionStatusesTests` — plus their shared `wtA`/`storedWt` helpers moved verbatim to the new
   `src/Tests/CodingToolSinceTests.fs` (module `Tests.CodingToolSinceTests`), registered in `Tests.fsproj`
   **before** `SchedulerTests.fs`. The new module is self-contained: `createAgent`/`emptyStatus`/`stampIdleSince`
   come from the opened `Server.*` modules, and the two tiny local helpers it also needs (`testRepoId`,
   `makeWorktree`) are re-declared `let private` (they stay in `SchedulerTests.fs` too — no shared-helper
   coupling across the compile boundary). **Divergence from the finding's "keep the remaining file under the
   limit" wording:** `SchedulerTests.fs` lands at ~1376 lines (was 1604), still nominally over 1000, but the
   remaining bulk (scheduler core + `BuildTaskList` phase family + eviction) is *pre-existing* and untouched by
   this diff, so the rule no longer fires — driving it under 1000 would mean extracting unrelated fixtures,
   which is scope creep beyond the finding. No behavior change; 89 CodingToolSince + Scheduler tests pass.

## Related Specs

- `docs/spec/session-status-push.md` — the push model this refines (feature s16).
- `docs/spec/beads-overview-band.md` / `docs/spec/overview-drilldown.md` — the Overview Agents band.
- PR #119 (merged) — per-agent "time in category" (`CodingToolSince`), reused here for time-since-idle.
