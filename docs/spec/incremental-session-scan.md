# Incremental session scan — accurate skill & last-user-message

## Goals

Report a worktree's **current skill** and **last user message** correctly regardless of session
size, by scanning Copilot CLI `events.jsonl` **incrementally** (append-only) with a per-session
cache, instead of a fixed ~1 MB backward tail.

Fixes two observed defects that share one root cause:
1. The card's **user-message line disappears** on long-running working agents.
2. Long / sub-agent-heavy skill sessions (e.g. `bd-execute`) report **generic Working** instead of
   the skill the user actually invoked.

## Background — why the tail window fails

`FileUtils.scanBackward` / `readTailLines` read only the last **~1 MB** of `events.jsonl`
(16 × 64 KB chunks). But in Copilot CLI:

- A `/skill` invocation is written **once, at start**, as a `skill.invoked` event **plus** a
  `user.message` with `source: "skill-<name>"` and `<skill-context …>` content. There is no plain
  `/skill` user line later.
- Sub-agents (Task tool) emit `subagent.started` / `subagent.completed` + tool/hook/assistant
  events, **never** `user.message`s.

Measured on real sessions: the skill invocation and the only `user.message` sit **2.2 – 11.7 MB**
back; sessions run **2 – 207 MB**. So once an agent produces >1 MB of autonomous work, the tail
window holds neither the `skill.invoked` nor any `user.message`, and both `getCurrentSkill` and
`getLastUserMessage` return `None`.

## Expected Behavior

- **Current skill persists** from its `skill.invoked` until a genuine new top-level `user.message`
  supersedes it, no matter how many bytes of sub-agent/tool output accumulate in between.
- A **finished skill still does not linger**: a genuine new top-level request (not an `ask_user`
  reply, not a `<skill-context>` injection) clears the skill.
- An **`ask_user` reply mid-skill** does not clear the skill (the same skill resumes).
- **Sub-agent skills are not the agent's skill.** A `skill.invoked` emitted **inside** a
  `subagent.started`/`subagent.completed` block reports as the sub-agent's, not the parent's — the
  top-level skill the user invoked (e.g. `bd-execute`) is what surfaces. *(Confirmed in a real parent
  `events.jsonl` — see Step 0 finding below.)*
- **`<skill-context>` injections never appear as the last user message.** The card user line shows
  the genuine last user prompt; when a skill is running it surfaces the skill (or the pre-skill
  prompt) instead of the injection.
- Status (`Working` / `WaitingForUser` / `Done` / `Idle`) is unchanged in meaning, including the
  existing mtime grace (Done→Working <15 s) and staleness (Working→Idle >30 min; >2 h→Idle) rules.
- Behavior is **identical to today** for small sessions (equivalence preserved).

## Technical Approach

### 1. Skill/message detection as a FORWARD fold (oldest→newest)

`events.jsonl` is append-only, so process events chronologically and carry state. This is equivalent
to today's backward `scanSkill` but is what incremental appending needs. Reuse the existing
`classifySkillEvent` / `isSkillContextMessage` classifiers.

Fold state:
```
{ CurrentSkill: string option
  LastUserMessage: (string * DateTimeOffset) option      // genuine prompts only (no injection)
  LastAssistantMessage: (string * DateTimeOffset) option
  RawStatus: CodingToolStatus                            // last decisive status event
  LastAssistantWasAskUser: bool                          // ask_user-reply detection
  SubagentDepth: int }
```
Per classified event:
- `subagent.started` → depth + 1 ; `subagent.completed` → `max 0 (depth − 1)`
- `SkillSignal name` (skill.invoked or a `skill` tool-call) → if `depth = 0` set
  `CurrentSkill = Some name`, else ignore (sub-agent's); reset `LastAssistantWasAskUser`
- `AssistantAskUser` → `LastAssistantWasAskUser = true`; `RawStatus = WaitingForUser`; record msg
- `AssistantWork` → `LastAssistantWasAskUser = false`; `RawStatus = Working`; record msg
- `UserRequest` (genuine — not a `<skill-context>` injection):
  - if `LastAssistantWasAskUser` → ask_user reply → keep `CurrentSkill`; record `LastUserMessage`
  - else → new top-level request → `CurrentSkill = None`; record `LastUserMessage`
  - `RawStatus = Working`; clear the flag
- `<skill-context>` injection user.message → transparent: not a boundary, **not** recorded as
  `LastUserMessage`
- `assistant.turn_end` → `RawStatus = Done`

Equivalence: newest-wins, ask_user-reply transparency, and injection transparency are all preserved;
the additions are `SubagentDepth` gating and genuine-only `LastUserMessage`.

### 2. Incremental per-session cache

`ConcurrentDictionary<string (eventsPath), SessionScanCache>` where the cache = fold state + `Length`
(bytes consumed).
- On query build `FileInfo(path)`:
  - cached and `cached.Length <= fi.Length` (not truncated) → read bytes `[cached.Length, fi.Length)`,
    split into complete lines up to the last `\n`, fold onto the cached state, advance `Length` to the
    last consumed newline offset (a partial trailing line is left for next time), store, return.
  - otherwise (no cache / length shrank = rotation) → full rescan from 0.
- Prune entries whose file is missing or `mtime > 2 h` (matches the Idle cutoff).
- The fold is pure; the dictionary is the sole mutable boundary — consistent with the existing
  `workspaceIndex` `ref`+Dictionary pattern. Re-reading appended bytes is idempotent (last-write-wins
  under concurrency).

### 3. Wiring (CopilotDetector.fs / CodingToolStatus.fs)

Add `getSessionScan (worktreePath) : SessionScanCache option` over the most-recent events file and
derive each field from it, **replacing the four separate 1 MB scans**:
- `getCurrentSkill` → `scan.CurrentSkill`
- `getLastUserMessage` → `scan.LastUserMessage`
- `getLastMessage` → `scan.LastAssistantMessage`
- `getStatusFromEventsFile` → keep the mtime grace/staleness wrapper but feed it `scan.RawStatus`
  instead of a fresh backward scan.

`CodingToolStatus.fs` assembles `CodingToolData` from **one scan per worktree per refresh** instead
of four calls. Claude / VS Code Copilot detectors are unchanged (out of scope — the user runs Copilot
CLI).

### 4. Client display

The server now yields a genuine `LastUserMessage` (never `<skill-context>`) and a correct
`CurrentSkill`. In `CardViews.fs`, when `CurrentSkill` is `Some`, surface the skill label (e.g.
`▶ investigate`) as/next to the user line; otherwise show `LastUserMessage`. Exact styling to confirm
during implementation.

## Decisions

- **Approach B (incremental cache)** over a larger/unbounded backward scan — O(new bytes) per refresh
  rather than re-reading up to a full 200 MB file each cycle.
- **Forward fold** replaces the backward `scanSkill` (equivalent, but append-friendly).
- **Suppress `<skill-context>` injections** from the displayed last user message; surface the running
  skill / pre-skill prompt instead.
- **Sub-agent depth gating is KEPT.** Confirmed in Step 0: a sub-agent's `skill.invoked` **does**
  bubble into the parent `events.jsonl`, nested between that sub-agent's `subagent.started` /
  `subagent.completed`, and the `skill.invoked` event carries **no** parent/depth marker of its own —
  the only signal that it belongs to a sub-agent is the enclosing subagent bracket. Without
  `SubagentDepth` gating a forward fold would let the deeper, later sub-agent skill overwrite the
  top-level one. So the fold must gate `skill.invoked` on `depth = 0`.

## Step 0 — investigate before coding

Verify sub-agent event nesting in a real parent `events.jsonl`: do `subagent.started` /
`subagent.completed` bracket the sub-agent's events, and does a sub-agent's `skill.invoked` appear
**between** them? (Session `8bc3c80f` showed `vs-local-development` 3.4 MB back under a `bd-execute`
session — likely a sub-agent's.) The finding decides whether `SubagentDepth` stays.

### Finding (session `8bc3c80f-b9cd-424d-a87f-45a740e049b4`, 20.7 MB / 7876 events)

**Yes on both — `SubagentDepth` stays.** The event stream:

| line | ~offset | depth | event |
|---|---|---|---|
| 3    | 59 KB  | 0 | `user.message` `/bd-execute AITestAgent-9ce` (source `None` — genuine) |
| 13   | 85 KB  | 0 | `skill.invoked` **`bd-execute`** (the top-level user skill) |
| 14   | 91 KB  | 0 | `user.message` source `skill-bd-execute` (`<skill-context>` injection) |
| 3007 | 8.39 MB| 1 | `subagent.started` toolCallId `toolu_01FNMZ2ogEU63bBbJUNe2f3h`, agent `bd-phase-executor` |
| 3116 | 8.77 MB| 1 | `skill.invoked` **`vs-local-development`** (the SUB-agent's skill) |
| 3563 | 9.85 MB| 1 | `subagent.completed` **same** toolCallId `toolu_01FNMZ2ogEU63bBbJUNe2f3h` |

- (a) `subagent.started` / `subagent.completed` **do bracket** the sub-agent's events — the pair
  shares an identical `toolCallId`, and all of that sub-agent's assistant/tool/hook events (incl. its
  `skill.invoked`) fall strictly between them. Blocks nest cleanly (observed depths 0→2). One outer
  block was still open at capture (54 started / 53 completed) — harmless.
- (b) A sub-agent's `skill.invoked` **does appear between** them in the parent file (line 3116, inside
  the 3007–3563 bracket). Its own `data` keys are
  `name, path, content, source, description, trigger, model` — **no** `parentAgentTaskId`/depth field.
  So the enclosing `subagent.*` bracket is the *only* way to tell it is a sub-agent's.
- Sub-agents emit **no** `user.message` (all three `user.message`s are at depth 0 near the top),
  matching the "sub-agents never write user.message" assumption.

Consequence: a naive newest-wins forward fold would report `vs-local-development` (deeper, 8.77 MB
back) instead of the user's `bd-execute` (85 KB in). Gating `skill.invoked` on `depth = 0` via
`SubagentDepth` is required and is retained.

## Key Files

| Concern | File |
|---|---|
| Session/skill scan + incremental cache | `src/Server/CopilotDetector.fs` |
| Tail/append reads | `src/Server/FileUtils.fs` |
| Coding-tool assembly | `src/Server/CodingToolStatus.fs` |
| Refresh loop | `src/Server/RefreshScheduler.fs` |
| Card display | `src/Client/CardViews.fs` |
| Unit tests | `src/Tests/CopilotDetectorTests.fs` |

## Related Specs

- `docs/spec/worktree-monitor.md` — base session scanning + the "parent session files only" rule.
- `docs/spec/beads-overview-band.md` — Corrections v1.1 (i)/(j) skill-freshness this supersedes with
  a whole-session (not 1 MB tail) determination; the band consumes the now-accurate `CurrentSkill`.
