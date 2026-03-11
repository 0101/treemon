# GitHub Review Thread Resolution

## Goals
- Show resolved/unresolved thread counts for GitHub PRs, matching the existing ADO behavior
- Dashboard should display "2/3 threads" instead of "4 comments" for GitHub PRs

## Expected Behavior
- GitHub PRs render the same `"{unresolved}/{total} threads"` badge format as ADO PRs
- When all threads are resolved, the badge is dimmed (same as ADO)
- The "Fix PR comments" action button only appears when unresolved threads exist
- Merged PRs show `WithResolution(0, 0)` (no threads)
- PRs with zero review threads show no badge (same as ADO's `total = 0` case)

## Technical Approach

Replace the GitHub REST API call (`/repos/{owner}/{repo}/pulls/{n}`) with a GraphQL query that fetches `PullRequestReviewThread.isResolved`:

```graphql
{
  repository(owner: "{owner}", name: "{repo}") {
    pullRequest(number: {n}) {
      reviewThreads(first: 100) {
        nodes { isResolved }
      }
    }
  }
}
```

Invoked via `gh api graphql -f query="..."` through the existing `ProcessRunner.run`.

Parse the response, count resolved vs unresolved, return `WithResolution(unresolved, total)` instead of `CountOnly(total)`.

## Key Files
- `src/Server/GithubPrStatus.fs` — Replace `fetchPrCommentCount`/`parsePrCommentCounts` with GraphQL-based equivalents
- `src/Tests/GithubFixtureTests.fs` — Replace `ParsePrCommentCountsTests` with `ParseReviewThreadsTests`
- `src/Tests/fixtures/github/` — Add `review-threads.json` fixture
- `src/Tests/MultiRepoApiTests.fs` — Update `GitHub PR uses CountOnly comment format` to assert `WithResolution`
- `src/Tests/SmokeTests.fs` — Update `GitHub section uses comments format` to assert "threads" format

## Decisions
- **GraphQL over REST**: REST API does not expose thread resolution status; GraphQL is the only option
- **No client changes needed**: `CommentSummary.WithResolution` and the UI rendering already exist from ADO support
- **`first: 100` limit**: Acceptable — PRs rarely have 100+ review threads
