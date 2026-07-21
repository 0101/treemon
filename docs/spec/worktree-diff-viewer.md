# Worktree Diff Viewer

## Goals

- Open a browsable worktree-to-base diff from a Treemon card without launching an editor.
- Show committed branch changes plus staged, unstaged, and untracked worktree changes.
- Display the changed-file list within one second on a warm local server for the verification fixture: 250 changed paths, including at least 25 untracked paths.
- Keep rendering responsive by loading and displaying one selected file at a time.
- Keep the server contract independent of diff2html so the renderer can change without redefining Git semantics.

## Expected Behavior

- Every non-archived worktree card has a **Diff** action. It remains disabled with a "Diff view not ready" tooltip until the generated `diff.html` SystemView appears in the scanned canvas-doc inventory; activation revalidates that inventory before opening and targeting the view. The same view can open in a standalone browser tab.
- The view loads a changed-file summary first, then restores the previous file selection or selects the first file. A clean worktree shows an explicit empty state.
- Treemon's generated `.agents/canvas/diff.html` is excluded from both tracked and untracked summary entries, so provisioning the viewer cannot make an otherwise clean worktree appear changed even when `.agents/` is not ignored.
- The comparison base uses the repository's configured base branch and upstream-remote resolution: prefer the remote-tracking ref, then fall back to the local base branch. A missing base, failed merge-base, or failed Git command produces a visible error state and no partial summary. A timeout produces an explicit retry-oriented timeout state and no partial summary or patch.
- The selected file renders as a unified diff by default. Users can switch to split view, and the preference persists.
- Syntax highlighting loads after the plain patch is visible, so highlighting never blocks initial rendering. A failed highlighter load leaves the plain patch usable and a later file selection or view toggle retries the load without requiring a page reload.
- Renamed entries expose old and new paths. Deleted, binary, oversized, truncated, untracked, and symlink entries have explicit states rather than disappearing or failing silently. Untracked symlinks are never dereferenced.
- Selecting diff text exposes the generic SystemView Explain, Remove, and Comment actions. The payload includes structured diff source context: file, hunk header, and old/new line ranges.
- A summary with more than 1,000 changed paths returns a `too-many-files` state and no partial file list. A selected file returns at most 2 MiB and 20,000 diff lines. If either capture limit is reached, the server returns an explicit `oversized` or `truncated` state and does not send a partial patch to diff2html. Every Git operation times out after 10 seconds, and the API preserves that outcome as a `timeout` state rather than collapsing it into `git-error`.
- Each loaded page creates an opaque viewer instance ID and sends it on summary and file requests. File requests accept only identities issued to that viewer for the known worktree, so refreshing one page makes only that page's prior identities stale; pane and standalone viewers cannot invalidate each other. Unknown, forged, or stale identities return 404 without exposing repository paths or content.
- The canvas server rejects every request whose `Host` is not `localhost` or a loopback IP address before dispatching document, diff-data, or renderer-asset routes.

## Technical Approach

`WorktreeDiff` owns the renderer-neutral result types and live comparison workflow. It reuses `GitWorktree`'s upstream-remote and base-ref selection rules, without fetching on a viewer request, computes `git merge-base HEAD <baseRef>`, and compares that commit to the live worktree. A single `git diff` from the merge base to the working tree includes committed, staged, and unstaged tracked changes. Untracked files come from `git ls-files --others --exclude-standard -z` and are represented as additions after binary, size, and symlink checks. The exact generated viewer path is removed from both parsed result sets before file-count limits and identity issuance.

The canvas doc server exposes renderer-neutral `diff-summary` and `diff-file` endpoints for known worktrees. Every request carries a page-instance UUID in the `X-Treemon-Diff-Viewer` header. The summary stores a server-owned identity snapshot keyed by normalized worktree path and viewer UUID, then returns opaque identities plus status metadata; the file endpoint resolves only through that snapshot, never from a browser-supplied root, Git ref, or filesystem path. Refreshing the summary replaces only that viewer's snapshot, making its old identities stale. Each worktree retains at most eight viewer snapshots, evicting the least recently used snapshot. Successful worktree deletion removes all snapshots for that path, while scheduler reconciliation prunes snapshots for paths absent from the all-repositories-ready known-worktree set.

The canvas server applies a shared loopback-host predicate as middleware before routing. Host validation parses IP literals without DNS resolution and accepts only `localhost`, IPv4 loopback addresses, or IPv6 loopback addresses.

The card Diff action sets an explicit canvas worktree target while leaving dashboard card focus unchanged. The normal pane behavior still follows the focused card; the next explicit card selection clears this target override.
In-document canvas links resolve and validate their destination against that active pane target, so navigation remains within the targeted worktree when card focus differs.

The routes are `GET /<encoded-known-worktree>/diff-summary` with no query parameters and `GET /<encoded-known-worktree>/diff-file?identity=<opaque-id>` with no other parameters. Both require one valid UUID-valued `X-Treemon-Diff-Viewer` header. Valid semantic results are tagged JSON responses; malformed headers or queries return 400, while unknown worktrees and absent, forged, evicted, removed, or stale identities return generic 404 responses without repository content. Clean and error summaries clear only the requesting viewer's prior identity snapshot.

`ProcessRunner` provides an additive argument-list API with timeout and bounded stdout/stderr capture; its recursive capture drains streams even after a limit is reached so child processes cannot block on full pipes. Existing string-based callers do not need to migrate. Diff Git calls use `ProcessStartInfo.ArgumentList`, `--` before paths, NUL-delimited machine output, `--no-ext-diff`, `--no-textconv`, and rename detection.

`diff.html` is provisioned and classified like the Beadspace SystemView. It uses self-hosted diff2html 3.4.52 assets to render one bounded patch at a time, with a Treemon-owned file navigator and persisted view preference. The core renderer draws the unhighlighted patch first; the slim UI bundle is then loaded lazily for syntax highlighting. Since opaque identities change whenever the summary is refreshed, prior selection restoration matches the change kind plus old/new display paths and uses the newly issued identity.

## Decisions

- diff2html is the MVP renderer because startup latency and implementation size matter more than editor-grade features.
- Unified view is the default because it fits the canvas pane; split view remains available and persistent.
- diff2html 3.4.52 is vendored and served from a versioned immutable local route; no renderer asset is fetched from a third-party origin.
- Over-limit summaries and patches are rejected as explicit states; partial patches are not rendered because an incomplete patch is not reliable input for diff2html.
- Agent-mediated review uses generic SystemView selection interactions rather than renderer-specific comment widgets.
- The generated diff view remains a SystemView, not an AgentDoc, so it stays non-archivable, non-shareable, and independent of authored-document morphing.
- Only the exact Treemon-owned `.agents/canvas/diff.html` artifact is excluded; other `.agents/` files remain visible worktree changes.
- Diff HTTP results use stable status-tagged JSON rather than serializing F# discriminated unions directly, keeping the browser contract explicit while the shared domain model remains strongly typed.
- Opening Diff targets the pane independently of dashboard card focus so action-event propagation cannot silently select or launch the card.
- The uniform 10-second Git deadline remains the bounded-response contract. Timeout results are explicit and retry-oriented; operation-specific tuning should be introduced only if real-repository evidence shows the deadline is routinely too short.
- Viewer identity is an in-memory page UUID rather than persisted browser state, because pane and standalone page instances must remain independent. Eight snapshots per worktree bound retained identity maps while allowing the pane and several standalone tabs to coexist.

## Key Files

| File | Purpose |
|---|---|
| `src/Shared/Types.fs` | Diff summary/file result types and canvas document classification |
| `src/Server/ProcessRunner.fs` | Argument-list process execution with bounded output and timeout |
| `src/Server/GitWorktree.fs` | Shared upstream-remote/base-ref selection and general worktree Git operations |
| `src/Server/WorktreeDiff.fs` | Renderer-neutral diff types and exact live-worktree comparison |
| `src/Server/WorktreeDiffApi.fs` | Opaque identity snapshots, tagged JSON mapping, and guarded diff route handlers |
| `src/Server/CanvasDocServer.fs` | Known-worktree diff data routes and generated view serving |
| `src/Server/DiffAssets.fs` | Versioned self-hosted renderer asset routes |
| `src/Server/DiffProvisioner.fs` | Keeps `diff.html` synchronized with the embedded template |
| `src/Server/DiffTemplate.html` | File navigator and diff2html rendering shell |
| `src/Client/CardViews.fs` | Worktree-card Diff action |

## Related Specs

- `docs/spec/canvas-system-view-interactions.md` — selection actions, source metadata, and interaction-session routing
- `docs/spec/canvas-pane.md` — generic SystemView hosting and navigation
- `docs/spec/beadspace-canvas.md` — generated SystemView provisioning and same-origin data pattern
- `docs/spec/worktree-monitor.md` — worktree-card behavior and base-branch resolution
