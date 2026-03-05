# Session Replay Status Detection Test

## Goals

- Prove that `ClaudeDetector.getStatus` correctly reports `Working` whenever any session file in the project directory shows active work — even when subagent files have completed
- Create a regression guard using a real captured session log that exercises the full detection timeline
- Make the status detection logic pure (injectable time) so tests can simulate time progression and staleness timeouts

## Expected Behavior

Given a directory with multiple `.jsonl` files (parent session + subagent sessions), the detector should:

1. **Never transition from Working to Done/Idle while any file shows subsequent activity** — if the parent session's last entry is a `Task` tool_use, the status must remain `Working` even if a completed subagent file has a newer mtime
2. **Return the highest-priority status across all recent files** — priority: `Working > WaitingForUser > Done > Idle`
3. **Apply staleness and age cutoffs per-file** — each file's entries are evaluated independently, then results are merged by priority
4. **Transition to Done/Idle only when all files are done/stale** — no single completed subagent should override an active parent

## Technical Approach

### 1. Capture fixture from `Q--code-tm-actions` session

Copy the full set of `.jsonl` files from a real session that exercised subagents. This becomes a checked-in fixture under `src/Tests/fixtures/claude/`. The fixture contains:
- Parent session file (spawns Task tools, has tool_use entries)
- Subagent session files (created by Task, end with `end_turn`)

### 2. Extract pure status logic

Refactor `ClaudeDetector.getStatus` to separate pure logic from I/O:

- **Pure core** (`internal getStatusFromFiles`): takes `(now: DateTimeOffset, files: (DateTimeOffset * string list) list)` → returns `CodingToolStatus`. Each tuple is `(fileLastWriteUtc, lastLinesReversed)` — one per JSONL file. Computes per-file status using existing `tryParseEntryKind`/`statusFromEntry`, applies staleness timeout (30 min) and Done-to-Working conversion (10s) per file, applies 2-hour file age cutoff, returns highest-priority status across all files (Working > WaitingForUser > Done > Idle).
- **Impure wrapper** (`public getStatus`): finds ALL `.jsonl` files in project dir (not just latest by mtime), reads last 20 lines of each, calls `getStatusFromFiles` with `DateTimeOffset.UtcNow`.

Follow the existing pattern from `CopilotDetector.getStatusFromEventsFile` which already accepts a `now` parameter.

### 3. Test with fixture data

The test loads fixture files and writes targeted test cases against `getStatusFromFiles`:
1. All files present with parent actively working (last entry = tool_use) and subagent completed (last entry = end_turn) — assert `Working`
2. All files present, all completed — assert `Done`
3. All files stale (>30 min) — assert `Idle`
4. Single active file among completed files — assert `Working`

Each test constructs the `(DateTimeOffset * string list) list` input directly from fixture data, no timeline simulation needed.

## Key Files

- `src/Server/ClaudeDetector.fs` — detection logic to refactor
- `src/Tests/fixtures/claude/` — new fixture directory for captured session
- `src/Tests/ClaudeSessionReplayTests.fs` — new replay test

## Decisions

- Fixture files are checked in and immutable — they represent a known-good captured session
- The replay test runs in CI (Category=Fast) — no external dependencies, pure logic
- CopilotDetector has the same single-file bug but is lower priority — fix Claude first, apply same pattern to Copilot later if needed
