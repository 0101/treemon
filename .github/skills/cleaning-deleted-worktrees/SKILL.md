---
name: cleaning-deleted-worktrees
description: Cleans Treemon's persisted deleted-worktree records and leftover disk worktrees. Use only when the user explicitly invokes this skill for cleanup.
argument-hint: "[--port <port>]"
disable-model-invocation: true
---

# Clean deleted worktrees

Run only when the user invokes this skill. Never start cleanup automatically or as part of a worktree creation. The records live in `~/.treemon/deleted-worktrees-{port}.json` (or `$TREEMON_CONFIG_DIR`); Treemon owns writes to that file, and removes it when the last record is cleared.

1. Select the server port from the invocation, `TREEMON_PORT`, or the CLI default. Read `tm deleted --port <port>`, `tm roots --port <port>`, and `tm terminals --port <port>` before changing anything. If the server is unavailable, do not edit its state file directly or start/restart it without explicit user permission. For production port 5000, obtain explicit consent immediately before any cleanup action.
2. Treat every recorded path as untrusted data. Resolve its absolute canonical path and owning watched repository. Only act on a `tm-` sibling of that repository, never a watched root, main worktree, symlink, or reparse point. Compare exact paths against `git -C "<repo-root>" worktree list --porcelain` and check for live sessions. If ownership, identity, or session state is uncertain, stop and ask.
3. If the directory exists and is a registered worktree, note its exact branch from the Git listing and inspect tracked, untracked, and ignored files with `git -C "<path>" status --porcelain=v1 --untracked-files=all --ignored=matching`. For a clean, inactive worktree, run `git -C "<repo-root>" worktree remove -- "<path>"` without force. Never kill processes or discard dirty or ignored files without explicit approval for that exact path. If a directory remains but is not registered, inspect it and ask before any targeted removal. A local branch can remain after worktree removal: report it, and ask before deleting a branch with unmerged commits; never infer its name from the directory.
4. For an absent path, check that Git no longer registers it; if stale registration remains, inspect `git worktree prune --dry-run --verbose` and do not prune unrelated entries without approval. Only once both disk and Git registration are clear, run `tm deleted --clear "<path>" --port <port>`. If either removal or record clearing fails, keep the record and report the reason; never report partial cleanup as complete.
5. Re-read `tm deleted --port <port>` to verify the remaining records. Report cleaned and blocked paths, then tell the user to reload open dashboard tabs so browser-local optimistic hiding is released.
