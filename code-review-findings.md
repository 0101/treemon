# Task Review: VS Code Copilot detector integration (changes-20260310-143624.diff)
**Reviewed at**: 2026-03-10T13:36:31.394Z
**Files Changed**: `src/Server/CodingToolStatus.fs`, `src/Server/Server.fsproj`, `src/Server/VsCodeCopilotDetector.fs`, `src/Server/WorktreeApi.fs`

## Critical Issues
### Issue: Last user-message timestamp is derived from assistant completion time
- **File**: `src/Server/VsCodeCopilotDetector.fs:387-388`
- **Type**: Logic
- **Severity**: Critical
- **Description**: `getLastUserMessage` timestamps `req.UserText` with `req.CompletedAt`. That is the assistant completion timestamp, not the user message timestamp. `CodingToolStatus.getRefreshData` then picks the "latest" user message across providers by timestamp, so this can surface an older user prompt just because its assistant reply completed later.
- **Fix**: Track user-message timestamp in `ReqState` during reconstruction and use that in `getLastUserMessage`. Only fall back to file mtime when no user timestamp exists.

### Issue: Detached worktrees collide into a single sync-status key
- **File**: `src/Server/WorktreeApi.fs:381-385`
- **Type**: Logic
- **Severity**: Critical
- **Description**: The new logic includes detached worktrees by forcing branch `"(detached)"` into `scopedBranchKey`. Multiple detached worktrees in the same repo collapse to the same key (`repo/(detached)`), and `Map.ofList` keeps only one path. This can attach sync/tool events to the wrong detached card and drop events for others.
- **Fix**: Use a unique scoped key for detached entries (e.g., include worktree path), or move all sync-status keys to path-based identity.

### Issue: Complex mutation-log replay added with no tests
- **File**: `src/Server/VsCodeCopilotDetector.fs:148-318`, `src/Tests` (no matching tests)
- **Type**: Logic
- **Severity**: Critical
- **Description**: The new detector introduces complex state reconstruction (snapshot/set/push replay, path parsing, cache behavior) with zero test coverage. Parse failures are swallowed (`with _ -> ()`), so format drift or edge cases can silently misreport `Working/Done` and stale message content.
- **Fix**: Add fixture-driven tests for kind `0/1/2` replay order, malformed lines, missing fields, timestamp semantics, and stale-session cutoffs.

## Architectural Improvements
### Issue: Duplicated helper logic and dead code in detector
- **File**: `src/Server/VsCodeCopilotDetector.fs:116-146`, `src/Server/FileUtils.fs:6-40`
- **Type**: Architecture
- **Severity**: Should Fix
- **Description**: `VsCodeCopilotDetector` duplicates `readLastLines` and `truncateMessage` behavior already centralized in `FileUtils`, and its local `readLastLines` is currently unused.
- **Fix**: Reuse `FileUtils.readLastLines`/`FileUtils.truncateMessage` and remove unused local helpers.

---

# Task Review: VS Code Copilot Detection (Comprehensive Multi-Model Review)
**Reviewed at**: 2026-03-10T13:36:31Z
**Files Changed**: `src/Server/CodingToolStatus.fs`, `src/Server/Server.fsproj`, `src/Server/VsCodeCopilotDetector.fs`, `src/Server/WorktreeApi.fs`

## Critical Issues

### Issue: `readLastLines` is dead code (not just duplicated)
- **File**: `src/Server/VsCodeCopilotDetector.fs:118-146`
- **Type**: Logic
- **Severity**: Critical
- **Description**: `readLastLines` is defined but **never called** anywhere in VsCodeCopilotDetector. Grep confirms only `readAllLines` (line 217) is used (at line 315 via `getReconstructed`). This is 29 lines of dead code that is also an exact duplicate of `FileUtils.readLastLines`. It appears to have been copy-pasted from an earlier iteration but the full-file reconstruction approach made it unnecessary.
- **Fix**: Delete `readLastLines` entirely (lines 116-146).

### Issue: `truncateMessage` duplicates `FileUtils.truncateMessage`
- **File**: `src/Server/VsCodeCopilotDetector.fs:342-345`
- **Type**: Architecture
- **Severity**: Critical
- **Description**: Exact byte-for-byte reimplementation of `FileUtils.truncateMessage`. Every other detector (`CopilotDetector`, `ClaudeDetector`) uses `FileUtils.truncateMessage`. This one uses a private copy.
- **Fix**: Replace `truncateMessage` usages at lines 373 and 388 with `FileUtils.truncateMessage`, then delete the private copy.

### Issue: Detached HEAD key collision in WorktreeApi (confirms earlier finding)
- **File**: `src/Server/WorktreeApi.fs:380-385`
- **Type**: Logic
- **Severity**: Critical
- **Description**: Confirmed via code inspection: `Map.ofList` silently keeps only the last entry when keys collide. Multiple detached HEAD worktrees all map to `"repoId/(detached)"`. The old `List.choose` code correctly skipped these. Note that other usages of `scopedBranchKey` (lines 344, 369) guard against detached heads with `List.tryFind (fun wt -> wt.Branch = Some branch)` — this change is inconsistent with those patterns.
- **Fix**: Either revert to `List.choose` (skip detached), or use a unique key for detached heads: `$"{RepoId.value repoId}/detached:{wt.Path}"`.

### Issue: No `WaitingForUser` status detection — feature gap
- **File**: `src/Server/VsCodeCopilotDetector.fs:337-340`
- **Type**: Logic
- **Severity**: Critical
- **Description**: `CopilotDetector` detects `WaitingForUser` via `ask_user` tool requests (line 113). `VsCodeCopilotDetector` only reports `Working` (modelState 0 or None) or `Done` (any other modelState) — never `WaitingForUser`. If VS Code Copilot is waiting for user input, the dashboard incorrectly shows "Working". The `ResponseKinds` field is already collected but unused for status determination.
- **Fix**: Inspect VS Code's response parts for tool-call markers. If `ResponseKinds` contains tool requests requiring user confirmation and modelState is complete, report `WaitingForUser`.

## Architectural Improvements

### Issue: `resolveStatus` change is correct but undocumented
- **File**: `src/Server/CodingToolStatus.fs:54-60`
- **Type**: Architecture
- **Severity**: Minor
- **Description**: The old code used `List.tryFind` (first match by provider, regardless of status). The new code uses `pickActiveProvider` (filter non-Idle, sort by mtime). This is a meaningful behavioral improvement — when both `CopilotDetector` and `VsCodeCopilotDetector` report `Provider = Copilot`, the new code correctly picks the most recently active one. However, the reason for the change is non-obvious without context.
- **Fix**: Add comment: `// Multiple results may match same provider (CLI + VS Code); pick most recently active`

### Issue: `"(detached)"` magic string in 3 locations
- **File**: `src/Server/GitWorktree.fs:225`, `src/Server/WorktreeApi.fs:38,382`
- **Type**: Architecture
- **Severity**: Minor
- **Description**: The literal `"(detached)"` appears in 3 files with no shared constant.
- **Fix**: Extract to `Shared/Types.fs`: `let [<Literal>] DetachedBranch = "(detached)"`

## Functional Improvements

### Issue: Silent exception swallowing in workspace index builder
- **File**: `src/Server/VsCodeCopilotDetector.fs:69-70`
- **Type**: Logic
- **Severity**: Minor
- **Description**: `with _ -> ()` inside the `Array.iter` callback (line 69) swallows all exceptions when parsing individual workspace.json files. Consistent with CopilotDetector's approach but makes debugging workspace mapping failures invisible.
- **Fix**: Log at debug level: `with ex -> Log.log "VsCodeCopilot" $"Skipped {hashDir}: {ex.Message}"`

### Issue: Mutable `ReqState` — pragmatic but worth noting
- **File**: `src/Server/VsCodeCopilotDetector.fs:165-170`
- **Type**: Logic
- **Severity**: Low
- **Description**: `ReqState` uses 5 mutable fields for JSONL replay. Given the mutation-based semantics where patches target specific fields, this is pragmatically justified. The state is private, well-encapsulated, and only accessed through `getReconstructed` which caches results. A `List.fold` with immutable records would be idiomatic but add `with` boilerplate at every mutation site for marginal benefit.
- **Fix**: Acceptable as-is. If refactored later, use `List.fold` with an immutable accumulator.

## Overall Assessment

The feature is well-conceived. The JSONL mutation-replay approach correctly handles VS Code's `objectMutationLog` format and the integration into `CodingToolStatus.fs` is clean — both Copilot sources report `Provider = Copilot` and `pickActiveProvider` correctly selects the most recently active one.

**Priority fixes:**
1. **Delete dead `readLastLines`** — 30s, zero risk
2. **Replace duplicate `truncateMessage` with `FileUtils.truncateMessage`** — 30s, zero risk
3. **Fix detached HEAD key collision** — real bug causing data loss with multiple detached worktrees
4. **Add tests** — critical for 389-line parsing module with 4 mutation kinds
5. **Add `WaitingForUser` detection** — feature gap affecting dashboard accuracy
6. **Add comment explaining `resolveStatus` change** — code clarity

---

# Task Review: VS Code Copilot Integration (Additional Findings)
**Reviewed at**: 2026-03-10T15:20:00Z
**Files Changed**: `src/Server/CodingToolStatus.fs`, `src/Server/Server.fsproj`, `src/Server/VsCodeCopilotDetector.fs`, `src/Server/WorktreeApi.fs`

## Critical Issues
### Issue: Ambiguous Provider Identity
- **File**: `src/Server/CodingToolStatus.fs`
- **Type**: Logic
- **Severity**: Medium
- **Description**: Both CLI Copilot and VS Code Copilot return `Provider = Copilot`. While `resolveStatus` merges them, it is impossible to distinguish which one is active in the UI. If both are active, they might flip-flop based on slight timestamp differences.
- **Fix**: Consider adding a `Variant` string to `CodingToolResult` or splitting the `CodingToolProvider` enum (e.g., `CopilotCli`, `CopilotVsCode`) if distinction is needed. For now, ensure `pickActiveProvider` logic is stable (it sorts by timestamp, which is good).

## Architecture & Design
### Issue: Duplicated Workspace Indexing Logic
- **File**: `src/Server/VsCodeCopilotDetector.fs:123` vs `src/Server/CopilotDetector.fs:20`
- **Type**: Architecture
- **Severity**: Medium
- **Description**: The `WorkspaceIndex` pattern with a mutable `ref` and a time-based expiration (`refreshIndex`) is duplicated almost exactly between `VsCodeCopilotDetector` and `CopilotDetector`.
- **Recommendation**: Extract this "Expiring Cache" pattern into a generic helper in `Shared` or `Server` to reduce duplication.

## Performance
### Issue: Full File Read on Every Check
- **File**: `src/Server/VsCodeCopilotDetector.fs:283`
- **Type**: Performance
- **Severity**: Medium
- **Description**: `readAllLines` reads the entire session file. Unlike the CLI Copilot logs which are append-only events, the VS Code logs are mutation patches, requiring full replay. For long-running sessions, this could be slow.
- **Recommendation**: The `reqCache` mitigates this, but monitor performance for large session files.

## Tests
### Issue: Missing Unit Tests for Complex Parser
- **File**: `src/Server/VsCodeCopilotDetector.fs`
- **Type**: Quality
- **Severity**: High
- **Description**: The state machine logic (`applyLine`, `reconstructLastRequest`) is complex and fragile.
- **Recommendation**: Strongly recommend adding `src/Tests/VsCodeCopilotParsingTests.fs` to cover the mutation log replay logic.

---

# Task Review: VS Code Copilot Detector — Comprehensive Opus Review
**Reviewed at**: 2026-03-10T15:04:52Z
**Files Changed**: `src/Server/CodingToolStatus.fs`, `src/Server/Server.fsproj`, `src/Server/VsCodeCopilotDetector.fs`, `src/Server/WorktreeApi.fs`

## Previously Flagged Issues — Status Update
- ✅ **Dead `readLastLines`** — removed from current code
- ✅ **Duplicate `truncateMessage`** — now uses `FileUtils.truncateMessage`
- ✅ **Detached HEAD key collision** — now uses `(detached@{wt.Path})` for unique keys
- ✅ **Missing tests** — `VsCodeCopilotDetectorTests.fs` exists (321 lines, 29+ tests, 9 fixture files)
- ⚠️ **User-message timestamp from file mtime** — still present (see below)
- ⚠️ **No `WaitingForUser` detection** — still present (architectural limitation)

## Critical Issues

### Issue: `applyResponseParts` match ordering drops text from typed response parts
- **File**: `src/Server/VsCodeCopilotDetector.fs:134-143`
- **Type**: Logic
- **Severity**: Critical
- **Description**: The pattern match `| (true, k), _ ->` catches ALL items with a `kind` property, even when they also have a string `value`. Response parts shaped like `{ "kind": "markdownContent", "value": "actual text" }` will only have their kind string recorded — the text value is silently discarded. Only items WITHOUT a `kind` property can contribute to `ResponseText`. This means completed sessions whose response parts all have a `kind` field will show no response text, falling through to `None` in `toLastMessageEvent` and producing no `CardEvent`.
- **Fix**: Reorder the match to handle the combined case first:
  ```fsharp
  | (true, k), (true, v) when v.ValueKind = JsonValueKind.String ->
      let kv = k.GetString()
      let text = v.GetString()
      let withKind = if List.contains kv acc.ResponseKinds then acc
                     else { acc with ResponseKinds = kv :: acc.ResponseKinds }
      if String.IsNullOrWhiteSpace(text) then withKind
      else { withKind with ResponseText = Some text }
  | (true, k), _ ->
      let kv = k.GetString()
      if List.contains kv acc.ResponseKinds then acc
      else { acc with ResponseKinds = kv :: acc.ResponseKinds }
  | _, (true, v) when v.ValueKind = JsonValueKind.String ->
      let text = v.GetString()
      if String.IsNullOrWhiteSpace(text) then acc
      else { acc with ResponseText = Some text }
  | _ -> acc
  ```
- **Test gap**: Add a fixture with response parts that have both `kind` and `value` to verify text extraction.

### Issue: User-message timestamp derived from file mtime (still present)
- **File**: `src/Server/VsCodeCopilotDetector.fs:348-357`
- **Type**: Logic
- **Severity**: Critical
- **Description**: `getLastUserMessage` returns `(truncatedText, fileMtime)`. The timestamp is the file's last write time, not when the user actually sent the message. `CodingToolStatus.getRefreshData` sorts across providers by timestamp to pick the "latest" user message — using file mtime can surface an older user prompt from a more recently written file over a genuinely newer prompt from another provider.
- **Fix**: Track user-message timestamp during reconstruction (from the snapshot's request timestamp or mutation timestamp) and use that instead of file mtime.

## Architectural Improvements

### Issue: `resolveStatus` semantic change needs documentation
- **File**: `src/Server/CodingToolStatus.fs:54-60`
- **Type**: Architecture
- **Severity**: Should Fix
- **Description**: Changed from `List.tryFind` (returns first match by provider, ignoring status) to `List.filter` + `pickActiveProvider` (filters non-Idle, sorts by mtime, returns most recent). This is correct — with both CLI and VS Code Copilot producing `Provider = Copilot` results, the old code would unpredictably return whichever appeared first in the list. The new code deterministically picks the most recently active one.
- **Fix**: Add comment: `// Multiple results may match (CLI + VS Code); pick most recently active`

### Issue: `getPath` uses fragile string manipulation instead of typed API
- **File**: `src/Server/VsCodeCopilotDetector.fs:195`
- **Type**: Architecture
- **Severity**: Minor
- **Description**: `e.GetRawText().Trim('"')` works for simple identifiers and numbers but would mangle strings with JSON escapes (e.g., `\"` sequences). Path segments in the mutation log are simple identifiers (`"requests"`, `"modelState"`, `"response"`) so this is safe in practice.
- **Fix**: Use conditional extraction:
  ```fsharp
  Seq.map (fun e ->
      if e.ValueKind = JsonValueKind.String then e.GetString()
      else e.GetRawText())
  ```

## Functional Improvements

### Issue: O(n) list append in push handler
- **File**: `src/Server/VsCodeCopilotDetector.fs:238-240`
- **Type**: Performance
- **Severity**: Low
- **Description**: `reqs @ [ applyRequestObject req emptyReq ]` inside `Seq.fold` is O(n) per append. For sessions with many requests, initial parse of an uncached file could be slow. The `reqCache` mtime-based caching mitigates repeated reads.
- **Fix**: Low priority. If performance becomes an issue, use `ResizeArray` internally during reconstruction and convert to list at the end.

### Issue: `WaitingForUser` status never reported
- **File**: `src/Server/VsCodeCopilotDetector.fs:304-307`
- **Type**: Logic
- **Severity**: Low
- **Description**: `statusFromReqState` returns only `Working` or `Done`. The `ResponseKinds` field is collected but unused. If VS Code Chat's schema doesn't expose an `ask_user` equivalent, this is an inherent limitation worth documenting.
- **Fix**: Add comment explaining the limitation.

## Overall Assessment

The VS Code Copilot detector is well-structured with clean separation between JSONL replay, caching, and public API. Previous review findings about dead code and duplicate helpers have been addressed. Test coverage is comprehensive (29+ tests, 9 fixtures). The orchestrator integration is correct — `pickActiveProvider` properly handles multiple `Copilot` results.

**Priority:**
1. **Fix `applyResponseParts` match ordering** — potential silent data loss for typed response parts
2. **Add comment on `resolveStatus`** — explains non-obvious behavioral change
3. **Consider `getPath` robustness** — minor, safe in practice

---

# Task Review: VS Code Copilot integration follow-up (changes-20260310-160444.diff)
**Reviewed at**: 2026-03-10T15:08:51.7739361Z
**Files Changed**: `src/Server/CodingToolStatus.fs`, `src/Server/Server.fsproj`, `src/Server/VsCodeCopilotDetector.fs`, `src/Server/WorktreeApi.fs`

## Critical Issues
### Issue: Response parsing drops text when a part carries both `kind` and `value`
- **File**: `src/Server/VsCodeCopilotDetector.fs:129-143`
- **Type**: Logic
- **Severity**: Critical
- **Description**: `applyResponseParts` pattern-matches `(kind, value)` as mutually exclusive branches. When a response item contains both fields, the `kind` branch wins and `value` is ignored, so `ResponseText` can remain `None` even for completed responses. That cascades into missing assistant message cards in `getLastMessage`.
- **Fix**: Parse `kind` and `value` independently within each response part (apply both updates to the accumulator when present).

### Issue: Last user-message timestamp is derived from file mtime, not user-message time
- **File**: `src/Server/VsCodeCopilotDetector.fs:348-357`, `src/Server/CodingToolStatus.fs:102-115`
- **Type**: Logic
- **Severity**: Critical
- **Description**: `getLastUserMessage` returns `(text, fileMtime)`. `CodingToolStatus` then chooses the newest user message across providers by timestamp. File mtime is an assistant-session activity proxy, not the user prompt timestamp, so cross-provider ordering can be wrong and stale prompts can be surfaced as latest.
- **Fix**: Extend reconstructed request state to capture user-message timestamp and return that from `getLastUserMessage` (fallback to file mtime only when timestamp is missing).

## Architectural Improvements
### Issue: Request append path is O(n^2) in mutation replay
- **File**: `src/Server/VsCodeCopilotDetector.fs:236-240`
- **Type**: Performance
- **Severity**: Should Fix
- **Description**: `Seq.fold` with `reqs @ [ item ]` reallocates on each append. Larger sessions will degrade disproportionately.
- **Fix**: Accumulate new requests in a temporary list/array and append once, or build in reverse and `List.rev`.

## Functional Improvements
### Issue: User-message API bypasses the active-session staleness gate
- **File**: `src/Server/VsCodeCopilotDetector.fs:295-303`, `src/Server/VsCodeCopilotDetector.fs:348-357`
- **Type**: Logic
- **Severity**: Medium
- **Description**: `getStatus`/`getLastMessage` use `tryGetActiveSession` (2-hour cutoff), but `getLastUserMessage` reads the latest file without that cutoff. Idle sessions can still contribute "latest" prompts.
- **Fix**: Route `getLastUserMessage` through `tryGetActiveSession`, or apply the same staleness check before returning user text.
