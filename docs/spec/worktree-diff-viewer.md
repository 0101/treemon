# Worktree Diff Viewer

## Goals

- Open a browsable worktree-to-base diff from a Treemon card without launching an editor.
- Show committed branch changes plus staged, unstaged, and untracked worktree changes.
- Display the changed-file list within one second on a warm local server for the verification fixture: 250 changed paths, including at least 25 untracked paths.
- Keep rendering responsive by loading and displaying one selected file at a time.
- Keep the server contract independent of diff2html so the renderer can change without redefining Git semantics.

## Expected Behavior

- Every non-archived worktree card has a **Diff** action that opens the canvas pane and focuses its generated `diff.html` SystemView. The same view can open in a standalone browser tab.
- The view loads a changed-file summary first, then restores the previous file selection or selects the first file. A clean worktree shows an explicit empty state.
- The comparison base uses the repository's configured base branch and upstream-remote resolution: prefer the remote-tracking ref, then fall back to the local base branch. A missing base, failed merge-base, failed Git command, or timeout produces a visible error state and no partial summary.
- The selected file renders as a unified diff by default. Users can switch to split view, and the preference persists.
- Syntax highlighting loads after the plain patch is visible, so highlighting never blocks initial rendering.
- Renamed entries expose old and new paths. Deleted, binary, oversized, truncated, untracked, and symlink entries have explicit states rather than disappearing or failing silently. Untracked symlinks are never dereferenced.
- Selecting diff text exposes the generic SystemView Explain, Remove, and Comment actions. The payload includes structured diff source context: file, hunk header, and old/new line ranges.
- A summary with more than 1,000 changed paths returns a `too-many-files` state and no partial file list. A selected file returns at most 2 MiB and 20,000 diff lines. If either capture limit is reached, the server returns an explicit `oversized` or `truncated` state and does not send a partial patch to diff2html. Every Git operation times out after 10 seconds.
- File requests accept only opaque identities issued by a summary for that known worktree. Unknown, forged, or stale identities return 404 without exposing repository paths or content.

## Technical Approach

Treemon reuses the existing upstream-remote and base-branch resolution, without fetching on a viewer request, computes `git merge-base HEAD <baseRef>`, and compares that commit to the live worktree. A single `git diff` from the merge base to the working tree includes committed, staged, and unstaged tracked changes. Untracked files come from `git ls-files --others --exclude-standard -z` and are represented as additions after binary, size, and symlink checks.

The canvas doc server exposes renderer-neutral `diff-summary` and `diff-file` endpoints for known worktrees. The summary stores a bounded server-owned identity map for the worktree and returns opaque identities plus status metadata; the file endpoint resolves only through that map, never from a browser-supplied root, Git ref, or filesystem path. Refreshing the summary replaces the map, making old identities stale.

`ProcessRunner` gains an additive argument-list API with timeout and bounded stdout/stderr capture; existing string-based callers do not need to migrate. Diff Git calls use `ProcessStartInfo.ArgumentList`, `--` before paths, NUL-delimited machine output, `--no-ext-diff`, `--no-textconv`, and rename detection.

`diff.html` is provisioned and classified like the Beadspace SystemView. It uses self-hosted, version-pinned diff2html assets to render one bounded patch at a time, with a Treemon-owned file navigator and persisted view preference. Syntax highlighting is loaded lazily after the unhighlighted diff appears.

## Decisions

- diff2html is the MVP renderer because startup latency and implementation size matter more than editor-grade features.
- Unified view is the default because it fits the canvas pane; split view remains available and persistent.
- Over-limit summaries and patches are rejected as explicit states; partial patches are not rendered because an incomplete patch is not reliable input for diff2html.
- Agent-mediated review uses generic SystemView selection interactions rather than renderer-specific comment widgets.
- The generated diff view remains a SystemView, not an AgentDoc, so it stays non-archivable, non-shareable, and independent of authored-document morphing.

## Key Files

| File | Purpose |
|---|---|
| `src/Shared/Types.fs` | Diff summary/file result types and canvas document classification |
| `src/Server/ProcessRunner.fs` | Argument-list process execution with bounded output and timeout |
| `src/Server/GitWorktree.fs` | Base resolution and exact live-worktree comparison |
| `src/Server/CanvasDocServer.fs` | Known-worktree diff data routes and generated view serving |
| `src/Server/DiffProvisioner.fs` | Keeps `diff.html` synchronized with the embedded template |
| `src/Server/DiffTemplate.html` | File navigator and diff2html rendering shell |
| `src/Client/CardViews.fs` | Worktree-card Diff action |

## Related Specs

- `docs/spec/canvas-system-view-interactions.md` — selection actions, source metadata, and interaction-session routing
- `docs/spec/canvas-pane.md` — generic SystemView hosting and navigation
- `docs/spec/beadspace-canvas.md` — generated SystemView provisioning and same-origin data pattern
- `docs/spec/worktree-monitor.md` — worktree-card behavior and base-branch resolution
