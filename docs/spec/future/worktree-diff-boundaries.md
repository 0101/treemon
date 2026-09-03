# Worktree Diff Module Boundaries

## Goals

- Keep `WorktreeDiff` focused on comparison orchestration and renderer-neutral results.
- Isolate Git-entry parsing and bounded untracked-file handling into cohesive modules.
- Preserve every existing diff semantic, limit, deadline, and error shape.

## Expected Behavior

The diff summary and selected-file APIs remain byte-for-byte compatible at their tagged JSON
boundary. Layer composition, rename handling, generated-view exclusion, file-count limits,
untracked symlink safety, capture limits, and request deadlines do not change.

Tracked raw/numstat parsing has one owner. Untracked enumeration, bounded content reads, binary and
symlink classification, and synthesized additions have one owner. `WorktreeDiff` coordinates those
results and retains the public domain types used by `WorktreeDiffApi`.

## Technical Approach

Extract the existing pure tracked-entry parser into a module named for Git diff entries, and extract
untracked-file enumeration/content handling into a module named for untracked diffs. Pass immutable
inputs and return new collections; neither module owns request routing or identity snapshots.

Reuse the current `ProcessRunner`, deadline, limits, and Git invocation builders. Move tests with
the behavior they cover, while keeping end-to-end layer-composition tests at the orchestration
boundary.

## Decisions

- **Responsibility split, not line-count split:** extraction follows two existing cohesive concepts.
- **No duplicate Git path:** the same commands and parsers remain the sole implementation.
- **Wire and domain stability:** this is a behavior-preserving refactor, not a diff redesign.

## Related Specs

- `docs/spec/worktree-diff-viewer.md` - comparison semantics, limits, and API behavior.
- `docs/spec/process-execution.md` - bounded argument-list Git execution.
