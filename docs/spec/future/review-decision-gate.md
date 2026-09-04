# Review Decision Gate

## Goals

- Prevent automated fix flows from implementing findings that require a product or architecture
  decision.
- Preserve reviewer evidence while making the user, not the suggested mechanism, the decision
  authority.
- Keep confirmed mechanical fixes automatable.

## Expected Behavior

Each reviewed finding has an executable disposition: confirmed fix, disregard, document for later,
or needs decision. A needs-decision finding is never passed to an implementation agent merely
because its review record contains a suggestion.

The review surface explains the competing outcomes and waits for an explicit user action. The user
may choose a concrete approach, disregard the finding, or document it for later. Only that selected
action becomes implementation input.

Batch fix operations include confirmed fixes and explicitly resolved decisions only. Unresolved
decision findings remain visible and cannot silently ride along with another selected action.

## Technical Approach

Model disposition as structured data in the focused-review result and action-validation boundary.
The validator rejects a fix request containing an unresolved decision finding and returns the
human-readable finding title and required decision.

The Canvas report remains the decision surface. Once the user selects an approach, persist that
choice in the review run state so a re-render or resumed session cannot lose or reinterpret it.

## Decisions

- **Suggestion is evidence, not approval:** reviewer prose never authorizes implementation.
- **Gate at validation:** every UI, CLI, or orchestration path crosses one enforcement boundary.
- **Explicit per-finding resolution:** a broad "fix selected" action cannot infer a design choice.

## Related Specs

- `docs/spec/canvas-pane.md` - interactive report hosting and action transport.
