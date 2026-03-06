# Task Review: Session Replay Status Detection
**Reviewed at**: 2026-03-06
**Files Changed**:
- `docs/spec/session-replay-status-test.md`
- `src/Server/ClaudeDetector.fs`
- `src/Tests/ClaudeSessionReplayTests.fs`
- `src/Tests/Tests.fsproj`
- `src/Tests/fixtures/claude/multi-session/*` (fixture data)

---

# Comprehensive Review: Coding Tool Subagent Detection
**Reviewed at**: 2026-03-06T13:27:42Z
**Review scope**: `fix/coding-tool-subagent-detection` branch diff vs `origin/main`

## Critical Issues

### Issue: `classifyFile` is redundant and fragile
- **File**: `src/Server/ClaudeDetector.fs:25-30`
- **Type**: Logic
- **Severity**: Critical
- **Description**: `classifyFile` re-parses the file path to determine Parent vs Subagent, but `findAllJsonlFiles` already knows which files came from subagent directories because it enumerated them separately. The path check is also fragile: `Path.Combine("subagents", "")` returns `"subagents"` (no trailing separator on Windows), making the first `Contains` check subsume the second. The function does unnecessary work that the caller already has the answer to.
- **Fix**: Remove `classifyFile`. Tag files with their kind at enumeration point:
  ```fsharp
  let topLevel = ... |> Array.map (fun f -> FileInfo(f), Parent)
  let subagentFiles = ... |> Array.map (fun f -> FileInfo(f), Subagent)
  ```

### Issue: `getFileStatus` double-computed for subagent files
- **File**: `src/Server/ClaudeDetector.fs:225-238`
- **Type**: Logic
- **Severity**: Critical
- **Description**: `getStatusFromFiles` calls `bestStatusByKind now Parent files` which computes `getFileStatus` for parent files. When parent is Done/Idle, it then manually filters subagent files and calls `getFileStatus` again with `List.exists`. But `bestStatusByKind` could serve both. Worse, the manual check only looks for `Working` — a subagent in `WaitingForUser` state is silently ignored when parent is Done/Idle. This is likely a logic bug.
- **Fix**: Use `bestStatusByKind` for subagents too:
  ```fsharp
  | Done | Idle ->
      let subagentStatus = bestStatusByKind now Subagent files |> Option.defaultValue Idle
      match subagentStatus with
      | Working -> Working
      | _ -> parentStatus
  ```

### Issue: `findAllJsonlFiles` called 4 times per worktree per poll
- **File**: `src/Server/ClaudeDetector.fs:245,303,314,441`
- **Type**: Performance
- **Severity**: Critical
- **Description**: `getStatus`, `getSessionMtime`, `getLastMessage`, and `getLastUserMessage` each independently call `findAllJsonlFiles`, re-enumerating the filesystem. On a machine with many old sessions, this multiplies I/O 4x per worktree per 15-second poll.
- **Fix**: Have the orchestrator call `findAllJsonlFiles` once and pass results to each function. Or consolidate into a single function that returns all needed data.

## General Codebase Issues

### Issue: Repeated boilerplate for parent-file operations
- **File**: `src/Server/ClaudeDetector.fs:299-308, 310-327, 437-447`
- **Type**: Duplication
- **Description**: Three functions follow identical pattern: compute projectDir, call findAllJsonlFiles, filter to Parent, process, sort by timestamp desc, tryHead. This is 3x near-identical pipelines.
- **Fix**: Extract helper:
  ```fsharp
  let private withParentFiles worktreePath f =
      let encoded = encodeWorktreePath worktreePath
      let projectDir = Path.Combine(claudeProjectsDir, encoded)
      findAllJsonlFiles projectDir
      |> List.choose (fun (fi, kind) -> if kind = Parent then f fi else None)
  ```

### Issue: `getSessionMtime` uses O(n log n) sort for max
- **File**: `src/Server/ClaudeDetector.fs:307-308`
- **Type**: Performance
- **Description**: `List.sortDescending |> List.tryHead` is O(n log n) to find the maximum element. Same pattern in `getLastMessage:320-321` and `getLastUserMessage:445-446`.
- **Fix**: For non-empty lists, use pattern match + `List.maxBy` which is O(n).

## Architectural Improvements

### Pure/impure separation is well-executed
- **Description**: The refactoring of `getStatusFromFiles` as a pure function taking `now` and `SessionFileData list` is the right pattern. Matches CopilotDetector's `getStatusFromEventsFile` approach. Enables all the regression tests without any I/O.

### `bestStatusByKind` should be used consistently or inlined
- **File**: `src/Server/ClaudeDetector.fs:217-223`
- **Description**: Only called once (for Parent). The subagent path uses manual filter+exists instead. Either use it for both kinds (preferred — see critical issue #2) or inline it since it has a single caller.

## Functional Improvements

### Timeline replay uses O(n) list append in fold
- **File**: `src/Tests/ClaudeSessionReplayTests.fs:297`
- **Description**: `transitions @ [ transition ]` copies the entire list on each append. For ~200 entries this is negligible, but it's an anti-pattern in F#.
- **Fix**: Use `transition :: transitions` and `List.rev` after the fold.

## Test Assessment

### Well-structured test fixtures
- `SessionReplayTests`: 8 tests using real captured fixture data
- `ParentAuthoritativeResolutionTests`: 9 combinatorial tests with synthetic JSON entries
- `TimelineReplayTests`: 3 tests including golden-file regression

### Test coverage is thorough
The priority resolution matrix is well-tested: Parent Working/WaitingForUser/Done/Idle x Subagent Working/Done combinations are all covered. Edge cases (empty files, stale files, 2-hour cutoff, Done-to-Working conversion) are tested.

### Minor: `List.iteri` assertion pattern
- **File**: `src/Tests/ClaudeSessionReplayTests.fs:343-350`
- **Description**: Using `List.iteri` with assertions reports only the first failure. Consider `CollectionAssert.AreEqual` or compare the full lists for better diagnostics. Acceptable as-is given the per-field error messages.

## Overall Assessment

The core change — multi-file status detection with parent/subagent priority — is architecturally sound and well-tested. The pure/impure separation enables excellent regression testing.

**Priority guidance:**
1. **Must fix**: Remove `classifyFile` and tag at enumeration (simplification, removes fragile path logic)
2. **Must fix**: Fix double-computation + potential `WaitingForUser` subagent oversight in `getStatusFromFiles`
3. **Should fix**: Extract repeated parent-file boilerplate (3 near-identical pipelines)
4. **Nice to fix**: `List.maxBy` over sort+head, list prepend over append in tests
