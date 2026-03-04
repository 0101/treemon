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

Use the `/spec-management` skill to evaluate the spec directory. Flag:

### 1. Single-purpose specs that should be removed or consolidated

A spec is single-purpose if it:
- Describes a single refactoring step (rename, type change, parameter change)
- Has no lasting architectural significance beyond "we did this"
- Would never be consulted again after the change is implemented
- Could be a paragraph in a broader spec instead of its own file

**Action:** Either incorporate the relevant design decisions into a related broader spec, or delete the file if it has no lasting value.

### 2. Specs not updated to reflect the current branch's changes

If the branch modifies behavior covered by an existing spec, the spec must be updated to match. Check:
- Do any changed files fall under a feature that has a spec?
- Does the spec still accurately describe the current behavior after the changes?
- Are new architectural decisions or patterns introduced that should be documented?

**Action:** Update the spec to reflect the new state of the code.

## Check

1. List all specs in `docs/spec/` (including subdirectories)
2. For each spec, assess whether it's a long-lived reference or a single-purpose task description
3. Cross-reference changed files in the branch against specs to find stale documentation
4. Report findings with specific recommendations (consolidate into X, delete, update section Y)
