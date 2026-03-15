---
autofix: false
model: inherit
applies-to: "docs/spec/**"
---
# Spec Hygiene

## Rule

Specs in `docs/spec/` must be long-lived reference documents. Single-purpose specs that exist only for one task should be consolidated or removed.

## Why

Spec files that describe a single narrow change (e.g., "rename X to Y", "change return type of Z") become stale immediately after implementation and clutter the spec directory. They make it harder to find the actual architectural documentation. Specs should capture *design decisions and architecture*, not individual tasks.

## Requirements

Flag specs in the diff that violate any of these:

### 1. One-off specs that should be deleted

A spec is one-off if it:
- Describes a single refactoring, cleanup, migration, or bug fix
- Is a branch-specific implementation plan with no long-lived value
- Has no lasting architectural significance beyond "we did this"
- Would never be consulted again after the change is implemented

**Name red flags:** filenames containing `-fixes`, `-cleanup`, `-improvements`, `-refactoring`, `-migration`, `-update` almost always indicate one-off work, not lasting architecture. Flag these.

**Action:** DELETE. Absorb any reusable design decisions into the parent spec first. The change narrative belongs in PR descriptions or commit messages, not in `docs/spec/`.

### 2. Specs that should be subsections, not standalone files

A spec doesn't need its own file if it:
- Describes a feature that's part of an existing architectural subsystem
- Could be a paragraph or section in a broader spec instead of its own file
- Describes a minor feature or UI element (e.g., a header bar, a tooltip, a button)

**Action:** Find the existing spec where this content belongs (search all specs including subdirectories), add it as a subsection there, then delete the standalone file.

### 3. Specs not updated to reflect the current branch's changes

If the branch modifies behavior covered by an existing spec, the spec must be updated to match. Check:
- Do any changed files fall under a feature that has a spec?
- Does the spec still accurately describe the current behavior after the changes?
- Are new architectural decisions or patterns introduced that should be documented?

**Action:** Update the spec to reflect the new state of the code.

## Check

1. Look at specs added or modified in the diff
2. Check filenames for red-flag patterns (`-fixes`, `-cleanup`, `-improvements`, etc.)
3. For each spec in the diff, assess: is this a long-lived architectural reference or a one-off task description?
4. For one-off specs, find the existing parent spec where the content belongs (search `docs/spec/` including subdirectories)
5. Cross-reference changed source files against existing specs to find stale documentation
6. Report findings with specific actions (delete, move to subsection of X, update section Y)
