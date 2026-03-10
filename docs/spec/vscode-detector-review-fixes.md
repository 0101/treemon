# VS Code Copilot Detector Review Fixes

## Goals

- Fix all correctly-flagged review violations in `VsCodeCopilotDetector.fs` and `WorktreeApi.fs`
- Align `VsCodeCopilotDetector.fs` with codebase functional style (immutable state, no loops, pure functions)
- Maintain identical runtime behavior — same API surface, same detection results

## Expected Behavior

After fixes:
- Detached HEAD worktrees no longer collide in sync status map
- `VsCodeCopilotDetector` uses immutable records, `fold`-based reconstruction, `Map` instead of `Dictionary`
- No duplicate utility functions — uses `FileUtils.truncateMessage` instead of local copy
- Magic integers replaced with a discriminated union for model state
- Dead `readLastLines` function removed (only `readAllLines` is used)
- Redundant comments removed
- All existing tests continue to pass

## Technical Approach

### Task 1: Fix detached HEAD key collision (WorktreeApi.fs)

The `getSyncStatus` function at line 382 uses `Option.defaultValue "(detached)"` for worktrees without a branch, causing key collisions in `Map.ofList`. Fix by including the worktree path in the key for detached worktrees, e.g. `$"(detached@{wt.Path})"`.

### Task 2: Rewrite VsCodeCopilotDetector.fs to functional style

This is the bulk of the work. Follow patterns established by `CopilotDetector.fs` and `ClaudeDetector.fs`:

**Immutable types:**
- Make `ReqState` an immutable record (no `mutable` fields)
- Replace `Dictionary<string, string>` in `WorkspaceIndex` with `Map<string, string>`
- Add `type ModelState = InProgress | Complete` DU to replace magic integers (0, 1, 4)

**Fold-based reconstruction:**
- Replace imperative `for`/`while` loops in `reconstructLastRequest` with `List.fold` over JSONL lines
- `applyResponseParts`, `applyModelState`, `applyRequestObject` return new `ReqState` records instead of mutating

**Remove duplicates:**
- Delete private `readLastLines` (dead code — never called)
- Delete private `truncateMessage` — use `FileUtils.truncateMessage`

**Separate concerns:**
- Extract pure state→CardEvent mapping from `getLastMessage` so it doesn't mix file lookup with formatting
- Push logging to boundary functions; keep parsing/transformation pure

**Clean up comments:**
- Remove comments that restate field names or obvious code

**Flatten tuple matching:**
- Replace nested match on `TryGetProperty("kind")`/`TryGetProperty("value")` with tuple match

### Task 3: Add unit tests for JSONL reconstruction

The 388-line module has zero test coverage. Add tests for the core parsing logic, particularly `reconstructLastRequest` which handles 3 mutation kinds. Test with representative JSONL fixture data covering: kind=0 (snapshot), kind=1 (set value at path), kind=2 (push/splice -- both appending new requests and pushing response parts). Kind=3 (delete property) is intentionally not handled and can be ignored.

## Decisions

- **Keep `readAllLines` in VsCodeCopilotDetector** — it reads all lines (not last N) and is specific to this module's JSONL format. Not a candidate for FileUtils.
- **`reqCache` unbounded growth (P3)** — skip for now. The cache is keyed by file path and worktrees are finite. Not a practical concern.
- **`WaitingForUser` detection (P2)** — skip for now. This is a feature gap, not a bug in existing code. Can be added separately.
- **User message timestamp** — fix as part of Task 2 rewrite. Use request creation time or file mtime instead of `CompletedAt`.
- **Silent exception swallowing** — fix as part of Task 2 by adding `Log.log` calls in catch blocks.

## Key Files

- `src/Server/VsCodeCopilotDetector.fs` — primary file being rewritten
- `src/Server/WorktreeApi.fs` — detached HEAD bug fix
- `src/Server/FileUtils.fs` — shared utilities to use instead of duplicates
- `src/Server/CopilotDetector.fs` — reference for target style
- `src/Server/ClaudeDetector.fs` — reference for target style
