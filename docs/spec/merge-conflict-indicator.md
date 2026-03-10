# PR Merge Conflict Indicator

## Goals

- Show at-a-glance whether a PR has merge conflicts, directly on the dashboard card
- Support both Azure DevOps and GitHub PRs
- No additional API calls for AzDo (field already in response); minimal extra calls for GitHub

## Expected Behavior

- When a PR has merge conflicts, a yellow warning icon (triangle with exclamation mark) appears next to the PR badge
- The icon uses the existing `warning.svg` asset
- Icon has a tooltip: "Merge conflicts"
- Merged PRs never show the conflict icon
- When merge status is unknown/computing (AzDo `queued`, GitHub `null`), treat as no conflicts — will resolve on next poll cycle

## Technical Approach

### Shared Types

Add `HasConflicts: bool` to the `PrInfo` record in `src/Shared/Types.fs`.

### Azure DevOps

Parse the `mergeStatus` field from the existing `az repos pr list` JSON response in `parsePrList` (`src/Server/PrStatus.fs`). The field is already returned by the API — just not parsed.

- `"conflicts"` → `HasConflicts = true`
- All other values (`"succeeded"`, `"queued"`, `"notSet"`, etc.) → `HasConflicts = false`

Pass through `ParsedPr` → `HasPr` construction.

### GitHub

The PR list endpoint (`GET /repos/{owner}/{repo}/pulls`) does not include `mergeable`. For each relevant open (non-merged) PR, fetch `gh api /repos/{owner}/{repo}/pulls/{number}` and read the `mergeable` field.

- `false` → `HasConflicts = true`
- `true` or `null` (not yet computed) → `HasConflicts = false`

In `fetchGithubPrStatuses`, start this fetch in parallel with the existing `fetchActionRuns` call (both run per relevant PR in the `List.map ... |> Async.Parallel` block), adding no sequential latency. For merged PRs, skip the fetch and default to `HasConflicts = false`.

### Client UI

In `prBadgeContent` (`src/Client/App.fs`), when `HasConflicts = true` and the PR is not merged, render an inline SVG element (the warning triangle from `warning.svg` in repo root) between the PR link and the thread/comment badge. Use Feliz `Svg.svg` with the path data inlined. Style via CSS class `conflict-icon` in `index.html`: yellow/amber fill, sized to match surrounding text, with `title` attribute "Merge conflicts".

### Key Files

- `src/Shared/Types.fs` — `PrInfo` record
- `src/Server/PrStatus.fs` — AzDo parsing, `ParsedPr`, `HasPr` construction
- `src/Server/GithubPrStatus.fs` — GitHub parsing, `ParsedGithubPr`, merge status fetch
- `src/Client/App.fs` — `prBadgeContent` rendering
- `src/Client/index.html` — CSS for `.conflict-icon`
- `src/Tests/fixtures/azdo/pr-list.json` — fixture update
- `src/Tests/AzDoFixtureTests.fs` — parsing tests
- `src/Tests/GithubParsingTests.fs` or `GithubFixtureTests.fs` — parsing tests
