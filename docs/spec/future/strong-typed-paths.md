# Strong-Typed Paths

Status: **Deferred** — cost/benefit doesn't justify the complexity right now.

## Goals

- Eliminate path comparison bugs caused by mixed separators, casing, or trailing slashes by normalizing once at construction time
- Make it impossible to pass a raw `string` where a file-system path is expected — the compiler catches mistakes, not runtime
- Single canonical representation (`AbsolutePath`) used everywhere — no ad-hoc `Path.GetFullPath` / `TrimEnd` scattered through code

## Trade-off Analysis

### Benefits

Prevents a class of bug where paths with different separators or casing fail to match in dictionary lookups, Set membership, or equality checks. One such bug was found in CopilotDetector (git returns `Q:/code/foo`, Copilot YAML has `Q:\code\foo`).

### Costs

- **~400 lines changed across 13 files** for a bug class that has occurred once
- **Unwrapping noise**: ~25+ call sites need `AbsolutePath.value` to pass strings to git/bd/az CLIs
- **Fable `#if !FABLE_COMPILER` guards**: `create`/`combine`/`fileName` use `System.IO.Path` unavailable in Fable — the type behaves differently on client vs server
- **Custom equality boilerplate**: ~15-20 lines of `[<CustomEquality; CustomComparison>]` ceremony
- **Serialization risk**: Fable.Remoting with private-constructor DUs and custom equality needs testing
- **Subtle Map/Set behavior**: case-insensitive comparison is implicit and non-obvious to readers

### Chosen alternative

Normalize paths with `Path.GetFullPath` at the 3-4 system entry points (CLI args, git worktree parsing, CopilotDetector YAML, config reading). ~10 lines of change, catches the same bugs, no type complexity.

## Technical Approach

### AbsolutePath type (new file: Shared/AbsolutePath.fs)

Single-case DU with private constructor, custom equality, and smart constructor that normalizes. Guard `create`/`combine`/`fileName` behind `#if !FABLE_COMPILER` because they use `System.IO.Path` APIs that Fable cannot compile. The client never constructs paths -- it only deserializes them and unwraps with `value`.

```fsharp
[<CustomEquality; CustomComparison>]
type AbsolutePath = private AbsolutePath of string
    with
        override this.Equals(other) = (* OrdinalIgnoreCase compare *)
        override this.GetHashCode() = (* OrdinalIgnoreCase hash *)
        interface IComparable with (* OrdinalIgnoreCase compare *)

module AbsolutePath =
    let value (AbsolutePath p) = p

    #if !FABLE_COMPILER
    let create (raw: string) =
        raw |> Path.GetFullPath
            |> _.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            |> AbsolutePath

    let combine (AbsolutePath parent) (relative: string) =
        Path.Combine(parent, relative) |> create

    let fileName (AbsolutePath p) = Path.GetFileName(p)
    #endif
```

### Boundary conversions

| Entry point | Current | After |
|---|---|---|
| CLI args (Program.fs) | `string list` | `AbsolutePath list` via `AbsolutePath.create` |
| git worktree output | `parseWorktreeList` returns `string` | Returns `AbsolutePath` |
| .treemon.json paths | `string` from JSON | `AbsolutePath.combine worktreePath relativePath` |
| Hardcoded dirs (Claude, Copilot) | `Path.Combine(...)` | `AbsolutePath.create (Path.Combine(...))` |
| CopilotDetector YAML cwd | `string` from YAML | `AbsolutePath.create cwd` |
| Test fixtures | `Path.Combine + GetFullPath` | `AbsolutePath.create` or `AbsolutePath.combine` |

### Types and collections to update

- `WorktreeStatus.Path: string` → `AbsolutePath`
- `RepoId` inner value → `AbsolutePath`
- `RefreshScheduler.KnownPaths: Set<string>` → `Set<AbsolutePath>`
- `CopilotDetector.WorkspaceIndex` dictionary key → `AbsolutePath`
- All `Map<string, ...>` keyed by path → `Map<AbsolutePath, ...>`

### No RelativePath needed

Relative paths appear only in `.treemon.json` `testSolution` (resolved immediately via combine) and `logs/server.log` (hardcoded at startup). Neither needs a type.

## Key Files

**New file**: `src/Shared/AbsolutePath.fs`

**Core conversion** (must change atomically): `src/Shared/Types.fs`, `src/Server/GitWorktree.fs`, `src/Server/RefreshScheduler.fs`, `src/Server/WorktreeApi.fs`, `src/Server/Program.fs`, `src/Client/App.fs`.

**Leaf conversions**: `src/Server/ClaudeDetector.fs`, `src/Server/CopilotDetector.fs`, `src/Server/CodingToolStatus.fs`, `src/Server/SyncEngine.fs`, `src/Server/BeadsStatus.fs`, `src/Server/PrStatus.fs`.

**Not converted**: `src/Server/Log.fs`, `src/Server/ProcessRunner.fs`, `src/Server/GithubPrStatus.fs`.
