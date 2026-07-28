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
- Byte-oriented callers receive exit code, raw stdout/stderr, and which streams were truncated. The
  `text`/`textResult` functions return UTF-8-decoded, trailing-whitespace-trimmed stdout on exit 0
  and trimmed stderr (or a described runner failure) otherwise, and additionally fail when the
  stdout they would return was truncated, because their callers parse that string. `exitResult`
  returns `unit` on exit 0 and ignores truncation entirely, because it discards the output.
- Timeout follows the call site, not a refresh/request rule. Most commands — the status collectors'
  `runGit`/`runGitResult`, `gh`, `az`, `bd`, and `git worktree add` — use the 60 s default; the diff
  endpoint's sequential commands share one monotonic 10 s response deadline; the post-fork hook uses
  its explicit 5-minute cap. Two short Git probes use `InteractiveDeadline`, which creates a
  fresh 10 s deadline per call: `GitWorktree.localComparisonContent` and `GitWorktree.probeRef`.
  Both are reached from the refresh timer — `localComparisonContent` as one of the five
  `Async.StartChild` children of `collectCommonGitData` (whose four siblings run on the 60 s
  default), `probeRef` via `resolveBaseRef` in `collectWorktreeGitData` — and `probeRef` is also
  reached from the fork request in `forkWorktree`.

## Technical Approach

### The API

Everything about a run except its arguments lives in a `Spawn` record — `FileName`, `Context`,
`Limits`, `Deadline`, `WorkingDirectory`. A module builds one per command with
`Spawn.create fileName` and overrides individual fields at the call sites that differ, so a call
site names only what is unusual about it:

```fsharp
let private git = { Spawn.create "git" with Context = "Git" }

ProcessRunner.text git ("-C" :: workingDir :: arguments)

ProcessRunner.capture
    { git with Limits = CaptureLimits.tiny; Deadline = InteractiveDeadline }
    [ "-C"; worktreePath; "status"; "--porcelain" ]
```

Four functions in `src/Server/ProcessRunner.fs` take a `Spawn` and a `string list`, and differ only
in what they return: `capture` (exit code plus raw stdout/stderr bytes and which streams were
truncated), `text` (`string option`), `textResult` (`Result<string, string>`), and `exitResult`
(`Result<unit, string>`). Every result shape works with every deadline, because the timeout is a
field rather than a separate entry point.

`Deadline` is a DU: `DefaultTimeout` (60 s), `Timeout of ms`, `InteractiveDeadline` (a fresh 10 s
response deadline per call), and `SharedDeadline` (a `ResponseDeadline` established before the call
and spent across several sequential runs). Only `SharedDeadline` can be exhausted on arrival, which
yields `TimedOut` without spawning anything.

`CaptureLimits` names the three byte-cap policies in use: `data` (16 MiB stdout / 64 KiB stderr) for
status collectors, JSON output, and diff summaries; `small` (64 KiB / 64 KiB) for short single-value
reads; and `tiny` (1 KiB / 1 KiB) for probes that only ask whether there was any output. A call site
that needs a different cap overrides one field — the diff viewer's file route uses
`{ CaptureLimits.data with StdoutBytes = maxWorktreeDiffBytes }`.

Byte-returning results expose `ArgumentListOutput` (exit code, raw streams, `Truncated`) or
`ArgumentListFailure` — the two outcomes with no exit code at all, `StartFailed` and `TimedOut`.
The text and exit wrappers layer decoding and `Result` mapping over the same core. The diff viewer
uses `capture` directly because it parses NUL-delimited machine output, and keeps its own strict
UTF-8 decoder because malformed output is a domain error there.

### Constructing arguments

- **A glued token stays one element.** `HEAD..{baseRef}`, `{baseRef}...HEAD`, and
  `--format=%H%n%s%n%aI` are single arguments built by concatenation — they are not two arguments
  because they contain a separator. The same holds for a GraphQL document, a REST URL with a query
  string, and an `az --route-parameters k=v` item.
- **An optional argument splices a list, never a string.** A value that is absent must contribute
  *zero* elements and, when present, may contribute *several* — `--top n` is
  `Option.map (fun n -> [ "--top"; string n ]) >> Option.defaultValue []`. This is the only shape
  that changes argument *count*, so it cannot be handled by de-quoting alone.
- **Never quote a value.** There is no parser to quote for; a wrapping `"` reaches the child as a
  literal character.

## Decisions

- **Truncation is a property of a completed run, not a failure.** A capture limit bounds memory; it
  says nothing about whether the command worked, and reporting it as `Error` makes exit 0
  unrepresentable — a verbose-but-successful post-fork hook surfaces as a failed step on the
  worktree card. `ArgumentListOutput.Truncated` moves the judgement to the caller: `text`/
  `textResult` still fail on truncated stdout, the diff viewer maps truncation to its typed
  `GitCaptureLimitExceeded`, and `exitResult` ignores it.
- **No string-arguments compatibility API.** Keeping one would let new callers bypass the structural
  guarantee. Correctness would otherwise depend on every caller quoting every interpolated value and
  validating characters that can alter parsing — obligations that are not type-checked, and Git
  accepts ref characters (including `"`) that make hand-built command lines unsafe. With argument
  lists, `validateBranchName` stays a product-input policy rather than a process-safety control.
- **Timeout policy is per call site, not unified.** It is a separate question from argv safety, so
  every call site keeps the timeout it has. The result is deliberately uneven: part of refresh-timer
  status collection runs on the interactive 10 s deadline while the rest runs on the 60 s default.
- **Open question: should the refresh-path 10 s probes move to the 60 s default?** A 10 s cap on a
  refresh-timer collector can make refresh flakier under load, which is why the bulk of status
  collection sits on the 60 s default; `localComparisonContent` and `probeRef` do not follow that
  reasoning. Aligning them is a behavioural change to `GitWorktree`, not a documentation fix.
  Decide it from evidence about refresh timeouts on large repos rather than from symmetry.
- **Byte caps are named policies, not per-caller constants.** The right cap depends on the
  operation — a probe asking "was there output" needs 1 KiB, a JSON collector up to 16 MiB — so a
  single universal default would be wrong in both directions. Naming the three policies in
  `CaptureLimits` keeps that intent explicit at each call site while removing the twelve
  near-duplicate constants the per-caller form had accumulated. A genuinely different cap is a
  one-field override.
- **Run configuration is a record, not positional parameters or a class.** The previous form passed
  seven positional arguments of which one typically varied, with two adjacent `int`s and two
  adjacent `string`s that could be swapped without a compile error. A record makes each value
  named at the site that sets it; copy-and-update comes free, which a class with optional
  parameters would need a hand-written `With` member to imitate.
- **`buildRemoteUrlArgs` returns `string list`.** It exists to be unit-tested independently of
  process spawning; returning the argument list keeps that property while making the assertions
  exact element equality instead of substring matches against a concatenated command line.

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
| `src/Server/ProcessRunner.fs` | The only process-execution API: `Spawn`/`Deadline`/`CaptureLimits`, the four run functions, bounded capture, process-tree kill, response deadlines |
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
