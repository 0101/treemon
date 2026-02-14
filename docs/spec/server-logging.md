# Server Logging

## Goals

- Make it easy to diagnose why data is missing or wrong (PRs not showing, beads counts zero, etc.)
- Log file rewritten each server run so it doesn't grow unbounded
- No changes to existing behavior — logging is purely additive diagnostics

## Expected Behavior

- On startup, server creates/truncates a log file at `.agents/server.log` (already gitignored)
- Startup info logged: worktree root path, server URL
- Every external process invocation (git, az, bd) logs: command, exit code, stdout (truncated), and on failure the stderr output
- Every JSON parse failure logs: what was being parsed and the exception message
- Every URL parse failure logs: the input URL and why it failed
- Every filesystem operation failure (directory listing, file mtime reads) logs: path and exception message
- Per-worktree assembly errors are logged and the worktree returns degraded data (defaults for failed parts) rather than crashing the entire response
- Cache hits/misses are NOT logged (too noisy, not useful for debugging)
- Log entries include timestamps and a short context label (e.g., `[PR]`, `[Git]`, `[Beads]`, `[Claude]`, `[API]`)

## Technical Approach

- Single `Log` module in a new `src/Server/Log.fs` file with a `log: string -> string -> unit` function (takes context label and message)
- On startup, truncate the log file via `File.WriteAllText`. Each log call uses `File.AppendAllText` — no held StreamWriter state
- Replace silent `with _ ->` and `with :? Win32Exception ->` handlers to call `Log.log` before returning their default values
- Wrap `assembleWorktreeStatus` in `WorktreeApi.fs` with try/with so one worktree failure doesn't prevent returning data for the rest
- Log startup info from `Program.fs`
- No external logging framework — just `System.IO.File` operations

## Key Files

- `src/Server/Log.fs` — new, the logging module
- `src/Server/Program.fs` — log startup info (worktree root, server URL)
- `src/Server/GitWorktree.fs` — add logging to `runGit`, `getLastCommit`, `getUpstreamBranch`
- `src/Server/BeadsStatus.fs` — add logging to `runBd`, `parseCountResponse`
- `src/Server/PrStatus.fs` — add logging to `runProcess`, `parseAzureDevOpsUrl`, `parsePrList`, `countUnresolvedThreads`, `parseBuildStatus`
- `src/Server/ClaudeStatus.fs` — wrap filesystem reads in try/with, log failures
- `src/Server/WorktreeApi.fs` — wrap `assembleWorktreeStatus` with try/with, log and return degraded status on failure
- `src/Server/Server.fsproj` — add `Log.fs` to compilation order (before all other modules)
