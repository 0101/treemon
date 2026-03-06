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

---

# Comprehensive Review (Post-Fix): Coding Tool Subagent Detection
**Reviewed at**: 2026-03-06T15:50:00Z
**Review scope**: `fix/coding-tool-subagent-detection` branch diff vs `origin/main` (post-fix iteration)
**Files Changed**:
- `src/Server/ClaudeDetector.fs` (177 → ~250 lines net change)
- `src/Server/CodingToolStatus.fs` (89 lines added)
- `src/Server/RefreshScheduler.fs` (3 lines changed)
- `src/Tests/ClaudeSessionReplayTests.fs` (367 lines new)
- `src/Tests/fixtures/claude/multi-session/*` (fixture data)
- `docs/spec/session-replay-v2.md` (new spec)
- `docs/spec/session-replay-status-test.md` (new spec)

## Previous Review Issue Resolution

All 3 critical issues from the previous review have been addressed:
1. ~~`classifyFile` redundancy~~ → **FIXED**: Removed entirely; files tagged as Parent/Subagent at enumeration point in `findAllJsonlFiles`.
2. ~~`getFileStatus` double-computed / subagent WaitingForUser bug~~ → **FIXED**: `getStatusFromFiles` now uses `bestStatusByKind` for both Parent and Subagent.
3. ~~`findAllJsonlFiles` called 4x per poll~~ → **PARTIALLY FIXED**: Scheduler path consolidated via `getRefreshData`. Two other call sites still enumerate independently (see below).

Previous "nice to fix" items also addressed:
- `List.maxBy` used instead of sort+head → **FIXED**
- List prepend + `List.rev` in fold → **FIXED**

## Critical Issues in Changes

### Issue: `ProviderEntry` abstraction is now a dead pattern for Claude
- **File**: `src/Server/CodingToolStatus.fs:8-13, 22-27`
- **Type**: Architecture
- **Severity**: Critical
- **Description**: `claudeProvider` (lines 22-27) populates `ProviderEntry` with `ClaudeDetector.getStatus`, `getLastMessage`, etc., but **none of these functions are ever called through the provider entry**. Every `match entry.Provider with | Claude ->` branch (lines 87, 110, 147, 167) bypasses the entry's functions and calls `ClaudeDetector.enumerateFiles` + `*FromFiles` directly. The `claudeProvider` record is dead code — it exists only to be pattern-matched past.
- **Fix**: Remove `claudeProvider` from the `providers` list entirely. Handle Claude as a special case outside the provider abstraction, or restructure so both providers use the same call pattern.

### Issue: `enumerateFiles` still called 3 separate times in non-scheduler paths
- **File**: `src/Server/CodingToolStatus.fs:89, 149, 169`
- **Type**: Performance/Duplication
- **Severity**: Critical
- **Description**: `gatherResults` (line 89), `getLastMessage` (line 149), and `getLastUserMessage` (line 169) each independently call `ClaudeDetector.enumerateFiles`. The scheduler path was consolidated via `getRefreshData`, but these three call sites—used by `getStatus` (line 98) and the WorktreeApi event path (line 395 of WorktreeApi.fs)—still enumerate independently. Each enumeration scans the filesystem directory tree.
- **Fix**: Pass `claudeFiles` as a parameter, or create a consolidated function for each call path that enumerates once.

### Issue: `getRefreshData` duplicates `gatherResults` logic
- **File**: `src/Server/CodingToolStatus.fs:103-135 vs 84-96`
- **Type**: Duplication
- **Severity**: Critical
- **Description**: `getRefreshData` (lines 107-118) contains a near-identical copy of `gatherResults` (lines 85-96) — same `List.map` with same `match entry.Provider with | Claude -> ... | Copilot -> ...` pattern. The only difference is that `getRefreshData` also returns `lastUserMsg`. This is two copies of the same provider-dispatch code.
- **Fix**: Reuse `gatherResults` inside `getRefreshData`:
  ```fsharp
  let getRefreshData worktreePath =
      let configured = readConfiguredProvider worktreePath
      let claudeFiles = ClaudeDetector.enumerateFiles worktreePath
      // Pass claudeFiles to gatherResults or inline
      let results = gatherResultsFromFiles claudeFiles worktreePath providers
      let status, provider = resolveStatus configured results
      ...
  ```

## General Codebase Issues

### Issue: 4x repetition of `match entry.Provider with | Claude -> ... | Copilot -> ...`
- **File**: `src/Server/CodingToolStatus.fs:87-96, 110-118, 147-152, 167-172`
- **Type**: Duplication
- **Description**: The same `match entry.Provider with | Claude -> <direct call> | Copilot -> <entry function call>` pattern appears 4 times. This defeats the purpose of the `ProviderEntry` abstraction. Each new function added to the provider interface requires updating 4 match expressions.
- **Fix**: Either make the abstraction work (give Claude a provider entry that uses `*FromFiles` with cached file list) or remove the abstraction and use a simple function that dispatches by provider.

### Issue: `parentFiles` helper pattern inconsistency
- **File**: `src/Server/ClaudeDetector.fs:53-54`
- **Type**: Style
- **Description**: `parentFiles` uses `List.choose (fun (fi, kind) -> if kind = Parent then Some fi else None)`. This is more idiomatically `List.filter ... |> List.map fst` or a pattern match. But more importantly, this is only used for parent-only operations (`getSessionMtimeFromFiles`, `getLastMessageFromFiles`, `getLastUserMessageFromFiles`), yet the function throws away the `SessionFileKind` tag without checking — if someone accidentally passes all-subagent files, the filter would return empty, silently producing `None`. This is fine since all callers control their inputs, but the function could be a simple pipeline with `List.choose` using pattern matching:
  ```fsharp
  let private parentFiles files =
      files |> List.choose (function (fi, Parent) -> Some fi | _ -> None)
  ```

## Functional Improvements

### Issue: `|> function` pattern used as poor man's match
- **File**: `src/Server/ClaudeDetector.fs:221-223, 301-303, 314-316, 439-441`
- **Type**: Style
- **Description**: The `|> function | [] -> None | items -> items |> List.max |> Some` pattern appears 4 times. This is `List.tryReduce` territory but F# doesn't have that in the stdlib. The pattern is readable and consistent; no change needed — flagging only for awareness. Consider a one-line helper if it bothers you:
  ```fsharp
  let private tryMaxBy f = function [] -> None | xs -> xs |> List.maxBy f |> Some
  ```

## Test Assessment

### Tests are well-structured and comprehensive
- **`SessionReplayTests`**: 8 tests covering real fixture data scenarios
- **`ParentAuthoritativeResolutionTests`**: 10 tests covering the full parent/subagent status matrix
- **`TimelineReplayTests`**: 3 tests with golden-file regression guard
- All tests are pure (no I/O beyond fixture loading), correctly categorized as `Unit`/`Fast`

### `parseTimestampFromLine` duplicates production `tryParseTimestamp`
- **File**: `src/Tests/ClaudeSessionReplayTests.fs:228-237` vs `src/Server/ClaudeDetector.fs:121-127`
- **Type**: Duplication
- **Description**: The test file has its own `parseTimestampFromLine` that does JSON parsing + timestamp extraction — functionally identical to `ClaudeDetector.tryParseTimestamp` (which takes a `JsonElement` root) combined with the JSON parsing. The test version parses the JSON string, which is what it needs for the timeline, so the duplication is justified since the production version works on a pre-parsed `JsonElement`. Not a must-fix.

## Architectural Improvements

### The core design is sound
The parent/subagent priority system is well-designed:
- `SessionFileKind` DU cleanly models the file classification
- `SessionFileData` enables pure testing
- `getStatusFromFiles` is a pure function with clear resolution rules
- `bestStatusByKind` handles the "best status across multiple files" concern
- `enumerateFiles` / `*FromFiles` split enables single-enumeration patterns

### `getRefreshData` is the right direction but incomplete
The consolidation of status + provider + lastUserMessage into a single call for the scheduler is correct. The remaining call sites (`getLastMessage` in WorktreeApi event merging, `getLastUserMessage` for other paths) should follow the same pattern.

## Overall Assessment

Previous critical issues are all resolved. The code is functionally correct and well-tested. The main remaining concern is structural: the `ProviderEntry` abstraction is now effectively bypassed for Claude in 4 places, creating a maintenance trap where the abstraction suggests a pattern that isn't actually followed. This should be resolved by either making the abstraction work with cached file lists or removing it in favor of direct dispatch.

**Priority guidance:**
1. **Must fix**: Remove or restructure `ProviderEntry` — the Claude branch is dead code in the provider entry, and all 4 dispatch sites bypass it
2. **Must fix**: Eliminate `getRefreshData`/`gatherResults` duplication (same code copied twice)
3. **Should fix**: Pass `claudeFiles` through remaining call sites (`getLastMessage`, `getLastUserMessage`) to avoid redundant filesystem enumeration
4. **Nice to fix**: Use pattern matching in `parentFiles`, consolidate `|> function` pattern
