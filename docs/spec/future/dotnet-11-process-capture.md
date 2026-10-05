# .NET 11 Process Capture

## Goals

- Qualify a deliberate .NET 11 server runtime change without adding multi-second Windows
  subprocess startup costs to diff loading and background data collection.
- Preserve bounded stdout/stderr capture, exact arguments, explicit errors, and timeout cleanup.
- Keep runtime upgrades explicit; an SDK pin or successful compilation is not runtime qualification.

## Expected Behavior

The server stays on its declared .NET 10.0 runtime family with patch-only roll-forward until a
.NET 11 candidate passes process-capture and application checks. A runtime change must identify
the actual loaded runtime, not infer it from `TargetFramework`, the SDK, or an installed-runtime list.

Captured Git operations must not acquire repeatable seconds-long startup overhead merely because
stdout or stderr is redirected. Diff summaries and selected-file patches retain their existing
semantics and deadlines; the warm 250-path summary fixture still completes within five seconds.

Qualification uses isolated processes, temporary state, and non-production ports. It never
restarts, deploys, or profiles production without separate immediate user approval, and never
changes security-product settings to make a benchmark pass.

## Technical Approach

### Windows redirection change

The installed .NET 11 release candidate `11.0.0-rc.1.26425.128` requests asynchronous read handles
for redirected stdout and stderr. Its Windows `SafeFileHandle.CreateAnonymousPipe` implementation
uses named pipes for asynchronous endpoints, unlike .NET 10's synchronous anonymous-pipe path.
Inspect the source revision embedded in the candidate's process-library product version; newer
.NET 11 builds may differ.

Controlled measurements on Windows with .NET 10.0.12 and that release candidate localized the
penalty to synchronous `Process.Start`, not Git computation, bounded reads, or exit waiting:

| Identical no-output Git command | .NET 10 startup | .NET 11 RC startup |
| --- | ---: | ---: |
| Neither output stream redirected | 4.8-7.7 ms | 4.7-9.2 ms |
| Stdout only | 4.8-5.4 ms | 1.23-1.26 s |
| Stderr only | 4.7-5.1 ms | 0.69-0.84 s |
| Both streams | About 5 ms | 1.50-2.00 s |

All redirection variants ran in the same isolated process for each runtime, with identical Git
arguments, worktree, file, exit code, and zero output. A separate same-binary capture comparison
returned the same patch in 76-80 ms on .NET 10 versus 2.05-2.68 seconds on .NET 11, and switching
back restored the fast result. Synchronous waiting, a different reader, and explicit UTF-8 encoding
did not remove the penalty.

The asynchronous named-pipe setup is the leading source-supported explanation, not a confirmed
specific native-call or security-product defect. Pipe creation, endpoint connection, and handle
setup were not separately timed. Live-versus-isolated HTTP latency is supplementary evidence,
not a runtime-only experiment, because workload and deployment context also differ.

### Upgrade qualification

Compare the same compiled probe and arguments under both runtimes, with repeated and reversed
runtime selection. Within each candidate process, vary only redirection flags for an identical
no-output command. Measure `Process.Start`, capture initialization, exit waiting, and stream
completion separately; retain the actual runtime and process-library revision with the results.

Then exercise `ProcessRunner.capture` under its existing output limits and cancellation rules,
followed by the real diff endpoints and browser performance fixture. Change the target/runtime
policy only after the candidate has no material redirected-startup regression. Do not replace the
bounded capture mechanism with temporary-file output, unbounded readers, longer timeouts, or
reflection-based framework workarounds merely to make .NET 11 usable.

## Sources

- [.NET 11 redirected-handle setup](https://github.com/dotnet/dotnet/blob/3551975be08744f0418857c5bed8ab1545c5dd47/src/runtime/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.cs#L1211-L1255)
- [.NET 11 Windows asynchronous pipe implementation](https://github.com/dotnet/dotnet/blob/3551975be08744f0418857c5bed8ab1545c5dd47/src/runtime/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Windows.cs#L23-L104)
- [.NET 10 synchronous process-redirection implementation](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/runtime/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Windows.cs#L736-L790)

## Related Specs

- `docs/spec/worktree-monitor.md` - authoritative server runtime and SDK policy.
- `docs/spec/process-execution.md` - argument safety, bounded capture, deadlines, and cleanup.
- `docs/spec/worktree-diff-viewer.md` - endpoint semantics and the warm-summary performance fixture.
