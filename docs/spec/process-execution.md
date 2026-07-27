# Process Execution

How Treemon spawns child processes to collect data (git, `gh`, `az`, `bd`) and to run repo hooks.

There is **one** data-capturing mechanism: `Server.ProcessRunner`'s argument-list API. It builds
`argv` element by element via `ProcessStartInfo.ArgumentList`; every entry point accepts
`arguments: string list`, so callers cannot construct a command line by interpolation.

Interactive UI launches (terminal, editor) are a different concern and are **not** covered — see
*Out of Scope*.

## Goals

- **Argv safety is structural, not conventional.** A value flowing into a child process cannot be
  split into extra arguments, and cannot silently break on a path containing a space, regardless of
  what the caller remembered to do. The compiler enforces this because no string-arguments entry
  point exists.
- **Every data-collecting subprocess has bounded output capture.** The dashboard refresh loop runs
  `git`, `gh`, `az`, and `bd` per watched repo on a timer; none of them may allocate without limit.
- **One mechanism.** Two ways to spawn a process means the safety property depends on which one a
  new call site happens to pick.

## Expected Behavior

- Each list element reaches the child as exactly one argument. Spaces, quotes, newlines, and Git
  syntax such as `HEAD..origin/main` are preserved as data rather than re-parsed as a command line.
- Stdout and stderr are captured as bytes up to caller-supplied limits. A stream that exceeds its
  limit is drained fully and reported as truncated on the output, not as a failed run: the child
  exited, so its exit code is preserved and each caller decides whether the missing bytes matter.
- Timeout cancellation kills the complete process tree and returns `TimedOut`.
- Byte-oriented callers receive exit code, raw stdout/stderr, and which streams were truncated.
  The `Result`-returning text wrappers receive UTF-8-decoded, trailing-whitespace-trimmed stdout on
  exit 0 and trimmed stderr (or a described runner failure) on a non-zero exit; the
  `option`-returning `runArgumentListText` collapses any non-zero exit or runner failure to `None`.
  The exit-code wrappers return `unit` on exit 0 and trimmed stderr (or a described runner failure)
  on a non-zero exit. A text wrapper additionally fails when the stdout it would return was
  truncated, because its callers parse that string; the exit-code wrappers never do, because they
  discard the output.
- Background refresh commands use the 60 s default, request-serving diff commands share a monotonic
  10 s response deadline, and the post-fork hook uses its explicit 5-minute cap.

## Technical Approach

### The API

`ProcessRunner` exposes one family, all backed by `runArgumentListCore`:

| Entry point | Timeout source | Use for |
|---|---|---|
| `runArgumentList` | The 10 s interactive response deadline (`argumentListResponseDeadlineMs`) | Work serving a user request |
| `runArgumentListWithTimeout` | Explicit `timeoutMs` | Background refresh, long hooks |
| `runArgumentListWithinResponseDeadline` | A `ResponseDeadline` shared across several sequential calls | Multi-command request handling (the diff endpoint) |

All take `arguments: string list`, explicit `stdoutLimitBytes` / `stderrLimitBytes`, and return
`Async<Result<ArgumentListOutput, ArgumentListFailure>>`:

```fsharp
type ArgumentListOutput =
    { ExitCode: int
      Stdout: byte[]
      Stderr: byte[]
      Truncated: CaptureStream list }   // stdout first when both capture limits were hit

type ArgumentListFailure =
    | StartFailed of string
    | TimedOut
```

`ArgumentListFailure` holds only the outcomes with no exit code at all. Capture is drained
recursively even after a limit is reached, so a child process cannot block on a full pipe. Timeout
cancellation kills the whole process tree.

### Text-capturing convenience

`ArgumentListOutput` carries `byte[]`, which is what the diff viewer needs (it parses NUL-delimited
machine output). The status collectors — `GitWorktree`, `PrStatus`, `GithubPrStatus`, `BeadsStatus` —
all want decoded text, and the post-fork hook and `git worktree add` want neither. Five wrappers
provide that shared behavior:

| Entry point | Timeout | Return |
|---|---|---|
| `runArgumentListText` | 60 s default | `Async<string option>` |
| `runArgumentListTextResult` | 60 s default | `Async<Result<string, string>>` |
| `runArgumentListTextResultWithTimeout` | Explicit `timeoutMs` | `Async<Result<string, string>>` |
| `runArgumentListExitResult` | 60 s default | `Async<Result<unit, string>>` |
| `runArgumentListExitResultWithTimeout` | Explicit `timeoutMs` | `Async<Result<unit, string>>` |

All five take explicit stdout/stderr byte limits and delegate to
`runArgumentListWithTimeout`. The text wrappers decode stdout or stderr with
`Encoding.UTF8.GetString` (replacement fallback for invalid sequences) and apply `TrimEnd`,
matching the status collectors' text contract. The exit-code wrappers decode and trim stderr only
for a non-zero exit. The diff viewer retains its strict UTF-8 decoder because malformed machine
output is a domain error there.

The text wrappers return stdout, so a truncated stdout capture is an `Error` — a prefix would read
as a complete answer to the parser consuming it. The `Exit` wrappers return `unit`: they exist for
callers that run a command for its effect (the post-fork hook, `git worktree add`), where the exit
code is the only signal and output volume must never override it.

The `Result` wrappers turn a runner failure into one distinct message per case; the `TimedOut`
message names the configured timeout, which is what a failed post-fork hook surfaces to the user.

Callers that need bytes keep using the byte-returning entry points directly.

### Constructing arguments

Each argument is one list element. Three patterns need care, because the string form hid them:

- **A glued token stays one element.** `HEAD..{baseRef}`, `{baseRef}...HEAD`, and
  `--format=%H%n%s%n%aI` are single arguments built by concatenation — they are not two arguments
  because they contain a separator.
- **An optional argument splices a list, never a string.** A value that is absent must contribute
  *zero* elements and, when present, may contribute *several*:
  ```fsharp
  let topArguments = top |> Option.map (fun n -> [ "--top"; string n ]) |> Option.defaultValue []
  ```
  Interpolating `$" --top {n}"` into a larger string is the shape this replaces; it is the one
  conversion that changes argument *count*, so it cannot be done by mechanical de-quoting.
- **Quotes in the old string were for the parser, not the value.** `\"{worktreePath}\"` becomes the
  bare element `worktreePath`. Leaving the quotes in would pass them through as literal characters.

### Why argument lists are mandatory

A string-arguments API makes correctness depend on every caller quoting every interpolated value
and validating characters that can alter parsing. Those obligations are not type-checked, and Git
accepts some ref characters (including `"`) that make hand-built command lines unsafe. Argument
lists remove the parser boundary entirely: quoting is unnecessary, and validation such as
`validateBranchName` remains a product-input policy rather than a process-safety control.

## Decisions

- **Truncation is a property of a completed run, not a failure.** A capture limit bounds memory; it
  says nothing about whether the command worked. Reporting it as `Error` made exit 0 unrepresentable
  for every caller, so a verbose-but-successful post-fork hook or `git worktree add` was surfaced as
  a failed step on the worktree card. `ArgumentListOutput.Truncated` moves the judgement to the
  caller: the text wrappers still fail on truncated stdout (they hand a parser a string), the diff
  viewer still maps truncation to its typed `GitCaptureLimitExceeded`, and the exit-code wrappers
  ignore it.
- **No string-arguments compatibility API.** Keeping one would let new callers bypass the structural
  guarantee; its absence makes the compiler reject that implementation shape.
- **Preserve each call site's existing timeout instead of unifying on the 10 s response deadline.**
  Status collection runs off the refresh timer, not a user request; moving it onto the interactive
  deadline would make refresh flakier under load. Timeout policy is a separate question from argv
  safety and is not bundled into this change.
- **Text-capturing wrappers live in `ProcessRunner`, not in a shared helper module.** Decoding a
  captured stream is part of running a process; putting it beside the capture code keeps one owner.
- **Byte caps stay explicit.** The right cap depends on the operation — `localComparisonContent`
  needs 1 KiB because it only asks "was there output", while JSON/status collectors and diff
  summaries allow up to 16 MiB. A universal default would be wrong in both directions.
- **`buildRemoteUrlArgs` returns `string list`.** It exists to be unit-tested independently of
  process spawning; returning the argument list keeps that property while making the assertions
  exact (element equality) instead of substring matches against a concatenated command line.

## Out of Scope

Three call sites spawn processes for **interactive UI**, not data capture, and deliberately do not
use `ProcessRunner`:

| Location | Purpose |
|---|---|
| `src/Server/SessionManager.fs` (`spawnWtAndResolve`, `openNewTabInWindow`) | Launch Windows Terminal windows/tabs for coding sessions |
| `src/Server/WorktreeApi.fs` (`openEditor`) | Launch the user's configured editor |

They pass a command line to `ProcessStartInfo` as a string, need no output capture, and in the
editor case intentionally go through `cmd.exe /c` to resolve a user-configured command. Converting
them is a separate question with different constraints — their argument handling is a live concern,
not a settled one.

## Key Files

| File | Role |
|---|---|
| `src/Server/ProcessRunner.fs` | The only process-execution API: argument-list entry points, bounded capture, process-tree kill, response deadlines, text and exit-code wrappers |
| `src/Server/GitWorktree.fs` | `runGit`/`runGitResult` wrappers; worktree creation and the post-fork hook |
| `src/Server/PrStatus.fs` | `runAz` wrapper, `buildRemoteUrlArgs`, Azure DevOps PR/build queries |
| `src/Server/GithubPrStatus.fs` | `runGh` wrapper, GitHub REST and GraphQL queries |
| `src/Server/BeadsStatus.fs` | `bd list --json` invocation |
| `src/Server/Program.fs` | Startup deploy-branch read (`git rev-parse --abbrev-ref HEAD`) |
| `src/Server/WorktreeDiff.fs` | Byte-level argument-list caller; per-operation error mapping and capture-limit constants |
| `src/Tests/ProcessRunnerTests.fs` | Argument-list execution, limits, timeout, start-failure, and text/exit-wrapper tests |
| `src/Tests/WorktreeDiffTests.fs` | Diff-viewer Git consumers over the byte-oriented entry points |
| `src/Tests/CreateWorktreeServerTests.fs` | Git worktree and post-fork argv regression tests |
| `src/Tests/UpstreamRemoteTests.fs` | `buildRemoteUrlArgs` argument-list assertions |
| `src/Tests/SmokeTests.fs` | Non-production live-server proof that Git collection populates dashboard data |

## Related Specs

- `docs/spec/worktree-diff-viewer.md` — request-scoped deadline and byte-oriented Git consumers.
- `docs/spec/remoting-csrf-hardening.md` — the other pipeline-level hardening of the same
  process-launching surface.
