# Session Replay v2: Parent/Subagent Detection + Timeline Replay Tests

## Goals

- `getStatusFromFiles` distinguishes parent vs subagent sessions: parent WaitingForUser is never overridden by subagent Working; subagent Working upgrades parent Done/Idle to Working
- `findAllJsonlFiles` discovers subagent files in `*/subagents/*.jsonl` and tags them as Subagent; top-level `.jsonl` files are tagged as Parent
- `getLastMessage`/`getLastUserMessage`/`getSessionMtime` use only parent session files (subagent messages are not user-facing)
- A timeline replay test replays all fixture entries chronologically through `getStatusFromFiles` and asserts each status transition matches a checked-in expected-statuses fixture (regression guard)

## Expected Behavior

### Parent/Subagent Detection

The Claude session directory structure is:
```
~/.claude/projects/{encoded-path}/
├── {sessionUuid}.jsonl                          ← parent session
├── {sessionUuid}/subagents/agent-{id}.jsonl     ← subagent files
├── {otherSessionUuid}.jsonl                     ← another parent session
└── ...
```

Subagent files are identified by **path**: any `.jsonl` file inside a `subagents/` subdirectory. Top-level `.jsonl` files are parent sessions.

Status resolution rules:
1. Compute per-file status as before (staleness, Done-to-Working, age cutoff) for both parent and subagent files
2. Take the highest-priority parent file status (existing maxBy logic, filtered to Parent files)
3. If parent status is `Working` or `WaitingForUser` -- return it directly (definitive user-facing states)
4. If parent status is `Done` or `Idle` -- check all subagent files: if any subagent is `Working`, return `Working` (the parent file hasn't been written to while the subagent runs)
5. Otherwise return the parent status

Parent `WaitingForUser` is never overridden by subagent `Working`. Parent `Done`/`Idle` can be upgraded to `Working` if any subagent is still active.

### Timeline Replay Test

Given the 4 fixture JSONL files (parent + 3 subagents):

1. **Merge phase**: Read all files, parse each line's timestamp, merge into a single timeline sorted by timestamp. Each entry knows which file it came from.
2. **Replay phase**: Walk the timeline entry by entry. At each step, the "current state" of each file is all entries up to and including this timestamp. Compute `getStatusFromFiles` using:
   - File mtime = timestamp of the latest entry in that file so far
   - Lines = last N entries seen so far for that file (reversed)
   - `now` = current entry's timestamp
3. **Record phase** (one-time generation): Write a JSONL file where each line is `{"timestamp": "...", "status": "Working", "trigger": "parent-session.jsonl:42"}`. This becomes the expected-status fixture.
4. **Test phase**: The test loads all fixture files + the expected-status file, replays the timeline, and asserts each computed status matches the recorded expectation.

The expected-status file is checked in and manually reviewable. If the algorithm changes, re-run the generator, diff the output, and decide if the new behavior is correct.

### Consecutive Deduplication

Adjacent entries that produce the same status are redundant for testing. The recorded expected-status file should deduplicate consecutive same-status entries — only record when the status *changes*. This keeps the fixture small and makes diffs meaningful.

## Technical Approach

### 1. Update `getStatusFromFiles` signature

Change `getStatusFromFiles` to accept file metadata that includes a parent/subagent flag:

```fsharp
type SessionFileKind = Parent | Subagent

type SessionFileData =
    { Kind: SessionFileKind
      LastWriteUtc: DateTimeOffset
      LastLinesReversed: string list }

let getStatusFromFiles (now: DateTimeOffset) (files: SessionFileData list) : CodingToolStatus
```

Apply resolution rules: parent status is authoritative; subagent Working can upgrade parent Done/Idle.

### 2. Update `findAllJsonlFiles` to discover subagents

Scan both top-level `.jsonl` files and `*/subagents/*.jsonl`. Tag each with `SessionFileKind`.

For `getLastMessage` / `getLastUserMessage` / `getSessionMtime`: keep using only parent session files (subagent messages aren't user-facing).

### 3. Timeline replay generator

Create a script/tool (can be an F# test helper) that:
- Reads all fixture JSONL files from `src/Tests/fixtures/claude/multi-session/`
- Parses timestamps from each entry
- Merges into timeline, replays, computes status at each change point
- Writes `src/Tests/fixtures/claude/multi-session/expected-statuses.jsonl`

### 4. Timeline replay test

NUnit test that:
- Loads fixture files and expected-statuses.jsonl
- Replays timeline using `getStatusFromFiles`
- Asserts each status transition matches expectations
- Category=Fast (pure logic, no I/O beyond fixture loading)

## Key Files

- `src/Server/ClaudeDetector.fs` — detection logic, `SessionFileKind` type, updated discovery
- `src/Tests/fixtures/claude/multi-session/expected-statuses.jsonl` — recorded expected statuses
- `src/Tests/ClaudeSessionReplayTests.fs` — updated replay tests

## Decisions

- Parent status is authoritative — subagents can only upgrade Done/Idle to Working, never downgrade WaitingForUser
- `getLastMessage`/`getLastUserMessage` stay parent-only (subagent messages aren't meaningful to the user)
- Expected-status file is checked in and diff-reviewable — algorithm changes require explicit approval of status shifts
- Fixture files from the first feature (treemon-9cs) are reused as-is (flat naming); tests tag them as Parent or Subagent by filename convention (files starting with `subagent-` are Subagent, others are Parent)
- Path-based detection only — no need to parse content for `"isSidechain"` since directory structure is sufficient
