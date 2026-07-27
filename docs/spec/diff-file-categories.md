# Diff File Categories

## Goals

- Let each repository declare an ordered, nested categorization of changed files so a large diff opens as an architectural outline instead of one flat list.
- Keep Git comparison semantics, opaque file identities, layer filtering, and one-patch-at-a-time rendering unchanged.
- Stay purely additive: a repository without categorization keeps today's flat accordion.
- Share one categorization across every linked worktree of a repository.
- Let the user generate that categorization from the diff view by asking the worktree's agent session to analyze the repository and write the configuration.

## Expected Behavior

### Configuration

- The repository-root `.treemon.json` holds an optional `diffCategories` array of category nodes. Linked worktrees consume the repo-root value and never need their own copy, matching `baseBranch` and `upstreamRemote`.
- The top-level array must be non-empty. A category node has a string `name` plus exactly one of `patterns` (a non-empty array of strings for a leaf) or `children` (a non-empty array of nodes for a branch). A node carrying both, neither, a wrong JSON type, or an empty array is invalid.
- Names are trimmed before they are stored, displayed, or placed in `categoryPath`. Sibling names must be non-blank and unique after trimming with ordinal case-insensitive comparison. Root nodes are depth 1; the maximum path depth is 4. A repository may contain at most 200 nodes, a leaf at most 50 patterns, and a pattern at most 200 UTF-16 code units; exceeding any bound is invalid.
- The viewer reserves the trailing group label **Other** for unmatched files, so a top-level node named `Other` (case-insensitive) is invalid.
- Configuration resolves to exactly one of three states. **Missing** (no `diffCategories` key) and **Invalid** (unparseable file, wrong shape, or a violated bound) both render the existing flat list; Invalid additionally shows a concise, non-blocking warning above the list naming the reason and, when the view has canvas transport, offering the configure action. Browser-facing reasons are bounded validation messages and never include a filesystem path, raw JSON, or exception text. **Configured** renders the hierarchy.
- Configuration is read and validated on every summary request, so Refresh reflects an edited or agent-written `.treemon.json` immediately without waiting for a scheduler cycle.

Example:

```json
{
  "diffCategories": [
    {
      "name": "Production code",
      "children": [
        { "name": "Client", "patterns": ["src/Client/**"] },
        { "name": "Server", "patterns": ["src/Server/**"] },
        { "name": "Shared", "patterns": ["src/Shared/**"] }
      ]
    },
    { "name": "Tests", "patterns": ["src/Tests/**", "**/*Tests.fs"] },
    { "name": "Docs", "patterns": ["docs/**"] },
    { "name": "Instructions", "patterns": ["AGENTS.md", ".github/instructions/**"] }
  ]
}
```

### Matching

- Patterns are repository-relative and matched against the whole `/`-normalized path using ordinal case-sensitive comparison; they never match a substring implicitly.
- The pattern language is a bounded glob subset: literal text, `?` for one character other than `/`, `*` for any run of characters within one path segment, and `**` for zero or more complete path segments. `**` is special only as a whole segment; written inside a segment it is just the within-segment wildcard. Thus `src/**/File.fs` matches both `src/File.fs` and `src/A/B/File.fs`, while `src/*/File.fs` matches only one intervening segment; `**/*.fs` also matches a root-level `.fs` file. No other metacharacter is special, and user-authored regular expressions are never accepted.
- Leaves are visited depth-first in configuration order and the first matching leaf wins, so precedence is explicit and reorderable by the author.
- A renamed entry is matched on its new path first and falls back to its old path only when the new path matches nothing, placing moved files in their destination category.
- A file matching no leaf is unmatched and appears in the trailing **Other** group.
- Files keep their existing summary order within a group; groups follow configuration order, with **Other** last.

### Presentation

- Each category renders a header button carrying `aria-expanded`, its name, and the count of files in its whole subtree. Branch headers nest their children; leaf headers contain the existing file rows. Category names are repository-authored and are rendered as text, never as markup.
- The synthetic top-level **Other** group is rendered only when unmatched files exist and follows the same disclosure rule as a leaf.
- Initial disclosure is computed per summary for every node top-down, favoring an architectural overview over exposed file rows:
  - A leaf expands when it holds at most 5 files and collapses when it holds more.
  - A branch collapses when it has more than 5 direct child categories.
  - Otherwise a branch expands, revealing its child headers. When that branch's subtree holds more than 5 files, its direct children start collapsed regardless of their own size; when the subtree holds at most 5 files, disclosure recurses so small hierarchies open through to their files.
  - Top-level categories are evaluated individually by these same rules; nothing forces them collapsed on behalf of the diff as a whole.
  - Forcing applies only to the level directly below the branch that triggered it. Deeper nodes keep their own computed state, so manually expanding a forced-collapsed category reveals its normal default rather than a fully collapsed subtree. A branch collapsed by the direct-child rule forces nothing, so opening it shows its children in their own default state.
  - Worked example: `Production code` (3 children, 8 files) expands and forces `Client` (3), `Server` (4), and `Shared` (1) collapsed; a sibling `Tests` leaf with 6 files collapses; sibling `Docs` (2) and `Instructions` (1) leaves expand.
- Explicit expand/collapse choices survive Refresh and layer-filter changes within the same page instance and are keyed by a collision-free serialization of the category path, not by joining names with a delimiter. They are not persisted to browser storage, so reopening the view returns to the computed default.
- Every file panel stays collapsed unless a remembered file selection exists. Restoring a remembered file expands its ancestor categories, overriding the computed default.
- Collapsing a category that contains the open file collapses that file panel and aborts or ignores its in-flight patch request, so no hidden patch stays selected.
- Arrow, Home, and End navigation moves only between file rows that are currently visible.
- The Added/Modified/Removed totals continue to summarize the whole selected diff and stay above the hierarchy.

### Configuring from the diff view

- An embedded diff view with an available canvas transport carries an icon-only toolbar action labeled `Analyze repository and configure diff groups`, using the same neutral action-button treatment as the existing toolbar controls. A dead control is not rendered when the transport helper is unavailable.
- Activating it sends the fixed message `configure-diff-categories` through the SystemView canvas transport. Treemon's existing routing delivers it to the view's interaction owner, resumes that session, or starts one when none exists, and the existing waiting and delivery-error banners report progress.
- The request text is fixed in the generated template. It instructs the agent to analyze the repository, locate the root worktree that owns the shared `.treemon.json`, preserve every existing field, and modify only `diffCategories` using ordered repository-relative globs covering production, test, documentation, and instruction areas without catch-all patterns, because Treemon supplies **Other**. It states the non-empty node schema, the four-level bound, sibling-name uniqueness, the reserved top-level `Other` name, and the supported `?`, `*`, and `**` glob subset. No part of the request is derived from repository content, and no path is supplied by the browser.
- A standalone top-level diff tab has no dashboard parent to receive the message, so the action is not rendered there. Grouping and diff browsing are unaffected.

## Technical Approach

`DiffCategories` owns the category concept end to end: the parsed model, validation, glob compilation, and classification. It reads `diffCategories` through `TreemonConfig`, which remains the only reader of `.treemon.json`. The accessor distinguishes a missing file or key from malformed JSON and returns an owned value (or parses through a callback while the document is alive), never a `JsonElement` borrowed from a disposed document. The parsed model is a discriminated union so a branch and a leaf cannot be confused, and the configuration result distinguishes `Missing`, `Invalid`, and `Configured` rather than collapsing a malformed file into "no categories". Each leaf pattern compiles once per read into a matcher for the bounded glob subset. Matching is a direct bounded algorithm: the pattern is simulated as a set of reachable positions, first over path segments and then over the characters of one segment, so a star-crowded pattern costs pattern length times input length instead of backtracking, and repository text is never executed as regular-expression syntax. `DiffCategories` exposes classification and grouping as one pass over the changed files, so patterns compile once per summary rather than once per file and the caller cannot order files by a rule the classifier does not know.

The canvas server already resolves a request's worktree from one scheduler snapshot. That lookup also yields the owning `RepoId`, whose value is the normalized repository root, so the server reads the categorization for that root and passes the resolved configuration to the diff summary handler alongside the existing comparison context. `WorktreeDiff` is untouched: Git enumeration, layer composition, the 1,000-path cap, deadlines, and identity semantics all keep their current behavior.

Classification happens in the summary handler, after the diff service returns entries and before opaque identities are issued, so the stored viewer snapshot and the browser agree on both order and grouping. Every browser-facing file gains a `categoryPath` array, empty for unmatched files and for every file in Missing or Invalid mode. Every ready summary gains `"categorization": { "status": "missing" | "configured" | "invalid", "reason": string | null }`; `reason` is non-null only for Invalid. Raw patterns are never sent to the browser: the server owns validation and matching, and the browser receives only ordered structure plus display labels. Because the server emits files already ordered by group, the viewer reconstructs the tree of *present* categories from consecutive category paths and never renders a configured category that has no changed files.

The generated `diff.html` stays byte-identical across repositories, so `DiffProvisioner` keeps its exact-content synchronization: all repository-specific data arrives at runtime in the summary response. The viewer builds its category tree from the response, renders nested sections, computes initial disclosure, and merges that with a page-instance map of explicit toggles keyed by category path. Each category is one section holding its header button plus a sibling panel of child sections or file rows, so collapsing a header hides exactly that subtree and a depth custom property supplies indentation without per-level rules. Because that structure is regular, a row's reachability is the `aria-expanded` chain of its ancestor headers — the same source of truth the collapsing CSS rule uses — so keyboard navigation and selection restoration both read disclosure rather than measured layout. The `Invalid` warning is one fixed viewer sentence completed by the server-supplied reason, which keeps repository-authored text out of the message while giving the configure action a place to attach. The single-open file accordion is unchanged and simply becomes nested inside category sections; category disclosure is a second, independent dimension layered around it, and collapsing a header that contains the open file runs the existing collapse path so the in-flight patch request is aborted and a late response cannot render into a hidden panel.

## Decisions

- **Recursive nodes over flat composite names**: `Production code > Client` is genuine structure, so it is modeled as nesting rather than encoded into a separated name. This keeps subtree counts, ancestor expansion, and per-level disclosure structural instead of string parsing.
- **A non-empty bounded tree over an empty configured state**: omitting `diffCategories` is the explicit flat-view opt-out; an empty or excessively large tree is more likely an authoring error and would add a fourth behavior with no user value. The depth, node, and pattern limits also bound per-request validation, matching, and rendering work.
- **Server-side classification**: one typed, unit-testable matcher instead of duplicating validation and glob semantics in the viewer's plain JavaScript, and no raw configuration crosses to the browser.
- **Bounded glob subset over regular expressions**: globs are what configuration authors and agents write naturally, and refusing arbitrary regular expressions removes a catastrophic-backtracking surface that the existing worktree-ignore patterns do not have to face at per-file scale.
- **First match wins, depth-first, configuration order**: precedence is visible in the file and adjustable by reordering, which is far easier to reason about than specificity scoring.
- **Runtime metadata over per-repository templates**: templating the hierarchy into `diff.html` would break the provisioner's byte-identical model and require reprovisioning after every configuration edit.
- **Overview-first disclosure over a per-node threshold**: applying "more than 5 collapses" to every node independently would hide the very structure the feature adds, because a large parent would collapse before showing its children.
- **Disclosure computed from the intrinsic tree**: every node's default depends only on its own subtree, and forcing is decided by whether a branch is intrinsically expanded, never by whether an ancestor forced it closed. A forced-collapsed branch therefore still forces its own children, and what a reader finds after opening it is exactly the outline that node would have shown on its own — the alternative, suppressing forcing below a forced node, would make opening it reveal a *different* layout than the one the rules describe.
- **Page-instance collapse state**: the computed default is the feature's main affordance, so persisted expansion state would mostly serve to hide it after the first visit; the page-instance map exists only so Refresh does not discard deliberate choices.
- **Selection-driven ancestor expansion is not an explicit toggle**: restoring a remembered file opens its ancestors in the rendered tree but records nothing in the toggle map, so the override is re-derived from the remembered selection on every render. Recording it would pin those categories open after the selection is gone — and it is unnecessary, because collapsing an ancestor of the open file already clears the selection.
- **Reuse of the SystemView canvas message**: `sendCanvasMessage` already resolves the owner, queues, resumes, launches, and reports failure, so a categorization request needs no new action kind, prompt builder, dashboard control, or process-launch path.
- **Unmatched files carry an empty category path**: the trailing **Other** group is a viewer label rather than a synthetic configuration node, which keeps the wire format honest about what the repository actually declared.
- **Fixed validation reasons**: an `Invalid` reason is one of a closed set of sentences and never quotes the offending name, pattern, or parser message. Naming the offending node would read better but would carry repository-authored text into the warning, so the reason describes the violated rule and the author finds the node in their own file.

## Verification

- `dotnet test src/Tests/Tests.fsproj --filter "FullyQualifiedName~DiffCategories"` covers schema validation, whole-path and case-sensitive glob behavior (including zero-segment `**`), depth-first precedence, rename fallback, and group ordering as pure functions.
- `dotnet test src/Tests/Tests.fsproj --filter "FullyQualifiedName~DiffEndpoint"` covers the wire contract: category paths and categorization status in summary responses, unchanged identity and file-lookup behavior, and per-request re-reading of the repository configuration.
- `dotnet test src/Tests/Tests.fsproj --filter "FullyQualifiedName~DiffViewer"` covers nested rendering, initial disclosure, explicit toggle persistence within a page instance, selection restoration through collapsed ancestors, delayed patch completion after ancestor collapse, keyboard navigation over visible rows, and the configure action.
- End-to-end verification runs against a real Git fixture, a real repository-root `.treemon.json`, and a real diff server bound to a free port, so classification, ordering, and immediate re-reading after a configuration edit are proven through the actual request path rather than through routed fixtures. Verification never deploys, restarts, or binds the production instance or port 5000.

## Key Files

| File | Role |
|------|------|
| `src/Server/DiffCategories.fs` | Category model, validation, glob compilation, classification, group ordering |
| `src/Server/TreemonConfig.fs` | Sole reader of `.treemon.json`; exposes an owned `diffCategories` read result |
| `src/Server/CanvasDocServer.fs` | Resolves the owning `RepoId` for a request and reads the categorization per summary |
| `src/Server/WorktreeDiffApi.fs` | Classifies and orders entries before identity issuance; serializes `categoryPath` and status |
| `src/Shared/Types.fs` | `DiffFileSummary.CategoryPath` |
| `src/Server/DiffTemplate.html` | Nested category rendering, disclosure, collapse state, configure action |

## Related Specs

- `docs/spec/worktree-diff-viewer.md` — the diff viewer that hosts this grouping, its layers, identities, and accordion
- `docs/spec/worktree-monitor.md` — `.treemon.json` per-repository configuration and repo-root resolution
- `docs/spec/canvas-interaction-routing.md` — SystemView message routing used by the configure action
