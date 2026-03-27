# Review Fixes: Path Identity & Code Quality

## Goals

Resolve all significant findings from Review 4 (Mar 27) so the next focused-review returns clean. Specifically:

1. Eliminate path identity inconsistencies by normalizing in domain type constructors
2. Fix the TREEMON_PORT crash regression in treemon.ps1
3. Apply terminal sanitization consistently to all server-sourced output
4. Apply minor style fixes (tuple matching, string interpolation, trivial helper)

## Expected Behavior

- `TREEMON_PORT=abc ./treemon.ps1 status` falls back to port 5000 (no crash)
- `getBranches` with non-canonical RepoId string (trailing slash, `..`, mixed case on Windows) finds the repo via Map lookup
- `RepoId` Map lookups succeed regardless of path casing on Windows
- All server-sourced fields (repo name, branch, path) are sanitized before terminal output
- `resolvePort` uses tuple match pattern
- No `eprintfn "format: %s"` anywhere — use interpolation (Cli/Program.fs and Server/Program.fs)
- `containsPathCI` inlined at its single call site

## Technical Approach

### Path normalization in constructors (core fix)

Move `normalizePath` into `WorktreePath.create` and `RepoId.create` so normalization is enforced once at construction. Then remove redundant `normalizePath` calls at 5+ call sites. This fixes `getBranches` (finding #8), `RepoId` case sensitivity (#16), and prevents future variants.

`normalizePath` should lowercase on Windows to ensure Map key equality:
```fsharp
let normalizePath (path: string) =
    let p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then p.ToLowerInvariant() else p
```

### TREEMON_PORT safe parse (regression fix)

Replace `[int]$env:TREEMON_PORT` with `Int32.TryParse` + fallback, matching CLI-side `resolvePort` behavior.

### Terminal sanitization (coverage fix)

Apply `sanitizeForTerminal` to `repo.RootFolderName`, `wt.Branch`, and `wt.Path` in worktrees output.

### Style fixes (quickfixes)

- Tuple match in `resolvePort`
- `$"Server error: {ex.Message}"` interpolation
- Inline `containsPathCI` at its single usage
- Replace hardcoded `\n` with `Environment.NewLine` where appropriate (note: `\n` in `printfn` format strings is fine for console output)

## Key Files

- `src/Shared/PathUtils.fs` — `normalizePath`, `pathEquals`
- `src/Shared/Types.fs` — `WorktreePath.create`, `RepoId.create`
- `src/Server/WorktreeApi.fs` — API endpoints, `getBranches`, `containsPathCI`
- `src/Cli/Program.fs` — `resolvePort`, `sanitizeForTerminal`, error handling
- `src/Server/Program.fs` — `eprintfn` calls (port/argument errors)
- `treemon.ps1` — `$DefaultPort` initialization

## Decisions

- **Lowercase on Windows only**: `normalizePath` lowercases paths on Windows where the filesystem is case-insensitive. On Linux/macOS it preserves case. This matches `pathEquals` behavior.
- **Remove call-site normalization**: Once constructors normalize, call-site `normalizePath` calls become redundant and should be removed to avoid double-normalization.
- **`\n` in printfn is fine**: `printfn` writes to stdout which handles `\n` on all platforms. The `Environment.NewLine` rule applies to string building, not console format strings.
- **GitWorktree.parseWorktreeList keeps normalizePath**: `WorktreeInfo.Path` is a raw `string`, not a domain type. It's used throughout `RefreshScheduler.fs` and `WorktreeApi.fs` for string comparisons against `WorktreePath.value` results. Removing normalization here causes path mismatches (forward vs backslash, case differences) because `pathEquals` only handles case, not separator normalization. The call is NOT redundant.
- **VsCodeCopilotDetector keeps normalizePath**: Uses normalization for `Dictionary<string,string>` keys, not domain types. Still required for key lookup consistency.
- **PathUtils.fs must compile before Types.fs**: Since `RepoId.create`/`WorktreePath.create` now call `PathUtils.normalizePath`, `PathUtils.fs` must appear first in the F# compilation order in `Shared.fsproj`.
