module Server.GitWorktree

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open FsToolkit.ErrorHandling
open Shared


type WorktreeInfo =
    { Path: string
      Head: string
      Branch: string option }

type CommitInfo =
    { Hash: string
      Message: string
      Time: DateTimeOffset }

type GitData =
    { Path: string
      Branch: string
      LastCommitMessage: string
      LastCommitTime: DateTimeOffset
      UpstreamBranch: string option
      MainBehindCount: int
      IsDirty: bool
      WorkMetrics: Shared.WorkMetrics option }

type WorktreeDiffStatus =
    | Added
    | Modified
    | Deleted
    | Renamed
    | Untracked

type WorktreeDiffEntry =
    { Path: string
      OldPath: string option
      Status: WorktreeDiffStatus }

type WorktreeDiffSummary =
    { BaseRef: string
      MergeBase: string
      Files: WorktreeDiffEntry list }

type WorktreeDiffOperation =
    | ResolveRemote
    | ResolveBase
    | ResolveMergeBase
    | EnumerateTracked
    | EnumerateUntracked
    | LoadFile

type WorktreeDiffError =
    | BaseNotFound of baseBranch: string * remoteRef: string
    | GitStartFailed of WorktreeDiffOperation
    | GitTimedOut of WorktreeDiffOperation
    | GitFailed of WorktreeDiffOperation * exitCode: int
    | GitCaptureLimitExceeded of WorktreeDiffOperation * ProcessRunner.CaptureStream
    | InvalidGitOutput of WorktreeDiffOperation
    | TooManyFiles of minimumCount: int
    | FileUnavailable

type WorktreeDiffFile =
    | Text of patch: string
    | DeletedFile of patch: string
    | Binary
    | Oversized
    | Truncated
    | Symlink of patch: string option

type private BoundedFileRead =
    | FileBytes of byte[]
    | FileTooLarge
    | FileReadFailed

let maxWorktreeDiffFiles = 1_000
let maxWorktreeDiffBytes = 2 * 1024 * 1024
let maxWorktreeDiffLines = 20_000

/// Result of a successful worktree creation: the path of the new worktree (so
/// callers can act on the exact location — e.g. launch a session there) alongside
/// any non-fatal warnings surfaced during creation.
type CreateWorktreeResult =
    { WorktreePath: string
      Warnings: CreateWorktreeWarnings }

let private runGit (workingDir: string) (arguments: string) =
    ProcessRunner.run "Git" "git" $"-C \"{workingDir}\" {arguments}"

let private runGitResult (workingDir: string) (arguments: string) =
    ProcessRunner.runResult "Git" "git" $"-C \"{workingDir}\" {arguments}" None

let parseWorktreeList (porcelainOutput: string) =
    porcelainOutput.Split(
        [| Environment.NewLine + Environment.NewLine; "\n\n" |],
        StringSplitOptions.RemoveEmptyEntries
    )
    |> Array.choose (fun block ->
        let lines =
            block.Split([| Environment.NewLine; "\n" |], StringSplitOptions.RemoveEmptyEntries)

        let findValue (prefix: string) =
            lines
            |> Array.tryFind _.StartsWith(prefix)
            |> Option.map (fun l -> l[prefix.Length..])

        let isPrunable = lines |> Array.exists _.StartsWith("prunable")

        match findValue "worktree ", findValue "HEAD ", isPrunable with
        | Some path, Some head, false ->
            let branch =
                findValue "branch refs/heads/"

            Some
                { Path = Server.PathUtils.normalizePath path
                  Head = head
                  Branch = branch }
        | _ -> None)
    |> Array.toList

/// Discovers the repo's worktrees. Returns `None` when the underlying git command
/// failed (timeout / non-zero exit / failed-to-start), so callers can distinguish a
/// transient git failure from a repo that genuinely has zero worktrees (`Some []`)
/// and retain last-known-good instead of blanking the list on a git hiccup.
let listWorktrees (repoRoot: string) : Async<WorktreeInfo list option> =
    async {
        let! output = runGit repoRoot "worktree list --porcelain"

        return
            output
            |> Option.map (parseWorktreeList >> List.filter (fun wt -> Directory.Exists(wt.Path)))
    }

let parseCommitOutput (worktreePath: string) (output: string option) =
    output
    |> Option.bind (fun raw ->
        if raw = "" then
            None
        else
            let lines = raw.Split([| Environment.NewLine; "\n" |], StringSplitOptions.None)

            match lines with
            | [| hash; message; timeStr |] ->
                match DateTimeOffset.TryParse(timeStr) with
                | true, time ->
                    Some
                        { Hash = hash
                          Message = message
                          Time = time }
                | _ ->
                    Log.log "Git" $"getLastCommit({worktreePath}): failed to parse time '{timeStr}'"
                    None
            | _ ->
                Log.log "Git" $"getLastCommit({worktreePath}): expected 3 lines (hash/message/time), got {lines.Length}"
                None)

let getLastCommit (worktreePath: string) =
    async {
        let! branchLocal = runGit worktreePath "log --first-parent --no-merges -1 --format=%H%n%s%n%aI"

        match parseCommitOutput worktreePath branchLocal with
        | Some commit -> return Some commit
        | None ->
            let! fallback = runGit worktreePath "log -1 --format=%H%n%s%n%aI"
            return parseCommitOutput worktreePath fallback
    }

let private tryFastForwardMain (repoRoot: string) (baseBranch: string) (mainRef: string) =
    async {
        let! currentBranch = runGit repoRoot "rev-parse --abbrev-ref HEAD"

        match currentBranch |> Option.map _.Trim() with
        | Some branch when branch = baseBranch ->
            let! result = runGitResult repoRoot $"merge --ff-only {mainRef}"

            match result with
            | Ok _ -> Log.log "Git" $"Fast-forwarded {baseBranch} in {repoRoot}"
            | Error msg -> Log.log "Git" $"Fast-forward skipped in {repoRoot}: {msg}"
        | _ -> ()
    }

let mainRef (upstreamRemote: string) (baseBranch: string) = $"{upstreamRemote}/{baseBranch}"

let fetchUpstream (repoRoot: string) (upstreamRemote: string) (baseBranch: string) =
    async {
        let! _ = runGit repoRoot $"fetch {upstreamRemote} -- {baseBranch}"
        do! tryFastForwardMain repoRoot baseBranch (mainRef upstreamRemote baseBranch)
    }

let getMainBehindCount (worktreePath: string) (mainRef: string) =
    async {
        let! output = runGit worktreePath $"rev-list --count HEAD..{mainRef}"

        return
            output
            |> Option.bind (fun s ->
                match Int32.TryParse(s.Trim()) with
                | true, count -> Some count
                | _ -> None)
            |> Option.defaultValue 0
    }

let getUpstreamBranch (worktreePath: string) =
    async {
        let! output = runGit worktreePath "rev-parse --abbrev-ref @{u}"

        return
            output
            |> Option.bind (fun s ->
                let trimmed = s.Trim()
                if String.IsNullOrEmpty(trimmed) then None else Some trimmed)
    }

let isDirty (worktreePath: string) =
    async {
        let! output = runGit worktreePath "status --porcelain -uno"

        return
            output
            |> Option.map (fun s -> s.Trim().Length > 0)
            |> Option.defaultValue false
    }

let getCommitCount (worktreePath: string) (mainRef: string) =
    async {
        let! output = runGit worktreePath $"rev-list --count --no-merges {mainRef}..HEAD"

        return
            output
            |> Option.bind (fun s ->
                match Int32.TryParse(s.Trim()) with
                | true, count -> Some count
                | _ -> None)
            |> Option.defaultValue 0
    }

let private extractRegexInt (pattern: string) (text: string) =
    let m = System.Text.RegularExpressions.Regex.Match(text, pattern)
    if m.Success then Int32.Parse(m.Groups[1].Value: string) else 0

let parseDiffStats (output: string option) =
    output
    |> Option.bind (fun s ->
        match s.Trim() with
        | "" -> None
        | trimmed ->
            Some(
                extractRegexInt @"(\d+) insertion" trimmed,
                extractRegexInt @"(\d+) deletion" trimmed
            ))
    |> Option.defaultValue (0, 0)

let getDiffStats (worktreePath: string) (mainRef: string) =
    async {
        let! output = runGit worktreePath $"diff --shortstat {mainRef}...HEAD"
        return parseDiffStats output
    }

let collectWorktreeGitData (worktreePath: string) (branch: string option) (mainRef: string) =
    async {
        let! commitChild = Async.StartChild(getLastCommit worktreePath)
        let! upstreamChild = Async.StartChild(getUpstreamBranch worktreePath)
        let! dirtyChild = Async.StartChild(isDirty worktreePath)
        let! commitCountChild = Async.StartChild(getCommitCount worktreePath mainRef)
        let! diffStatsChild = Async.StartChild(getDiffStats worktreePath mainRef)
        let! mainBehindChild = Async.StartChild(getMainBehindCount worktreePath mainRef)

        let! commit = commitChild
        let! upstream = upstreamChild
        let! mainBehind = mainBehindChild
        let! dirty = dirtyChild
        let! commitCount = commitCountChild
        let! (linesAdded, linesRemoved) = diffStatsChild

        let upstreamBranch =
            upstream
            |> Option.map (fun u ->
                match u.IndexOf('/') with
                | -1 -> u
                | i -> u[(i + 1)..])

        let workMetrics : Shared.WorkMetrics option =
            match commitCount with
            | 0 -> None
            | _ ->
                Some
                    { CommitCount = commitCount
                      LinesAdded = linesAdded
                      LinesRemoved = linesRemoved }

        return
            { Path = worktreePath
              Branch = branch |> Option.defaultValue WorktreeStatus.DetachedBranchName
              LastCommitMessage = commit |> Option.map _.Message |> Option.defaultValue ""
              LastCommitTime = commit |> Option.map _.Time |> Option.defaultValue DateTimeOffset.MinValue
              UpstreamBranch = upstreamBranch
              MainBehindCount = mainBehind
              IsDirty = dirty
              WorkMetrics = workMetrics }
    }

let private selectUpstreamRemote
    (configuredRemote: string option)
    (remoteOutput: string option)
    =
    match configuredRemote with
    | Some remote -> remote
    | None ->
        let hasUpstream =
            remoteOutput
            |> Option.exists (fun output ->
                output.Split(
                    [| '\n'; '\r' |],
                    StringSplitOptions.RemoveEmptyEntries
                )
                |> Array.exists (fun remote -> remote.Trim() = "upstream"))

        if hasUpstream then "upstream" else "origin"

let resolveUpstreamRemote (repoRoot: string) =
    async {
        match TreemonConfig.readUpstreamRemote repoRoot with
        | Some remote -> return remote
        | None ->
            let! output = runGit repoRoot "remote"
            return selectUpstreamRemote None output
    }

let private isWorktreePrunable (repoRoot: string) (worktreePath: string) =
    async {
        let! listOutput = runGit repoRoot "worktree list --porcelain"
        let normalizedPath = Server.PathUtils.normalizePath worktreePath

        return
            listOutput
            |> Option.exists (fun output ->
                output.Split(
                    [| Environment.NewLine + Environment.NewLine; "\n\n" |],
                    StringSplitOptions.RemoveEmptyEntries)
                |> Array.exists (fun block ->
                    let lines = block.Split([| Environment.NewLine; "\n" |], StringSplitOptions.RemoveEmptyEntries)
                    let hasPath =
                        lines |> Array.exists (fun line ->
                            line.StartsWith("worktree ")
                            && Server.PathUtils.normalizePath (line.Substring(9)) = normalizedPath)
                    let hasPrunable = lines |> Array.exists _.StartsWith("prunable")
                    hasPath && hasPrunable))
    }

let private cleanupDirectory (path: string) =
    try
        if Directory.Exists(path) then
            Directory.Delete(path, recursive = true)
        if Directory.Exists(path) then Error "directory still exists after cleanup"
        else Ok ()
    with ex ->
        Error $"cleanup failed: {ex.Message}"

let private tryPruneAndClean (repoRoot: string) (worktreePath: string) (removeMsg: string) =
    asyncResult {
        if Directory.Exists(Path.Combine(worktreePath, ".git")) then
            return! Error "Cannot delete the main worktree"

        let! prunable = isWorktreePrunable repoRoot worktreePath

        if not prunable then
            return! Error $"git worktree remove failed: {removeMsg}"

        do! runGitResult repoRoot "worktree prune"
            |> AsyncResult.mapError (fun pruneMsg ->
                $"git worktree remove failed: {removeMsg} (prune also failed: {pruneMsg})")
            |> AsyncResult.ignore

        do! cleanupDirectory worktreePath
            |> Result.mapError (fun msg ->
                $"git worktree remove failed: {removeMsg} ({msg})")
    }

let removeWorktree (repoRoot: string) (worktreePath: string) (branch: string option) =
    asyncResult {
        do! runGitResult repoRoot $"""worktree remove --force "{worktreePath}" """
            |> AsyncResult.ignore
            |> AsyncResult.orElseWith (tryPruneAndClean repoRoot worktreePath)

        match branch with
        | None -> ()
        | Some b ->
            do! runGitResult repoRoot $"branch -D -- \"{b}\""
                |> AsyncResult.mapError (fun msg -> $"Worktree removed but git branch -D failed: {msg}")
                |> AsyncResult.ignore
    }

let branchSortKey (baseBranch: string) (name: string) =
    match name with
    | n when n = baseBranch -> (0, name)
    | "main" -> (1, name)
    | "master" -> (1, name)
    | "develop" -> (2, name)
    | n when n.StartsWith("dev") -> (3, name)
    | _ -> (4, name)

let private validBranchNamePattern = System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9][a-zA-Z0-9._/-]*$")

let validateBranchName (branchName: string) =
    if validBranchNamePattern.IsMatch(branchName) then
        Ok branchName
    else
        Error $"Invalid branch name: '{branchName}'"

let private gitRefExists (repoRoot: string) (gitRef: string) =
    async {
        let! output = runGit repoRoot $"rev-parse --verify --quiet \"{gitRef}\""
        return output |> Option.exists (fun s -> s.Trim().Length > 0)
    }

let private selectBaseRef
    (upstreamRemote: string)
    (baseBranch: string)
    (remoteExists: bool)
    (localExists: bool)
    =
    if remoteExists then
        Some(mainRef upstreamRemote baseBranch)
    elif localExists then
        Some baseBranch
    else
        None

/// Resolves the base branch to a concrete git ref to fork from. Prefers the
/// remote-tracking ref (e.g. `upstream/main`) so a new worktree forks from the
/// upstream tip rather than a possibly-stale local branch, falling back to the
/// local branch when no remote-tracking ref exists. Does not require any worktree
/// to currently have the base checked out.
let resolveBaseRef (repoRoot: string) (upstreamRemote: string) (baseBranch: string) =
    async {
        let remoteRef = mainRef upstreamRemote baseBranch
        let! remoteExists = gitRefExists repoRoot $"refs/remotes/{remoteRef}"
        let! localExists =
            if remoteExists then async.Return false
            else gitRefExists repoRoot $"refs/heads/{baseBranch}"

        return
            selectBaseRef upstreamRemote baseBranch remoteExists localExists
            |> Result.requireSome
                $"Base branch '{baseBranch}' not found as '{remoteRef}' or as a local branch"
    }

/// Best-effort fetch of the base branch from upstream so the remote-tracking ref
/// reflects the latest upstream tip. Connectivity/remote failures are ignored —
/// worktree creation must not depend on the network.
let private fetchBaseBranch (repoRoot: string) (upstreamRemote: string) (baseBranch: string) =
    async {
        let! _ = runGit repoRoot $"fetch {upstreamRemote} -- {baseBranch}"
        return ()
    }

let private worktreeDir (repoRoot: string) (branchName: string) =
    let parentDir = Path.GetDirectoryName(repoRoot)
    let dirName = branchName.Replace('/', '-')
    Path.Combine(parentDir, $"tm-{dirName}")

/// Builds the git command that forks `branchName` from `baseRef` into a
/// `tm-`prefixed sibling of the repo root. Returns the command and the new
/// worktree path. `--no-track` stops git's default `autoSetupMerge` from making
/// the new branch inherit `baseRef`'s upstream: when `baseRef` is a remote-tracking
/// ref like `origin/feature`, a tracking branch would point `@{u}` at the base's
/// remote branch, and Treemon — which keys PR detection off `@{u}` — would then
/// show the base branch's PR on the new worktree until it is first pushed. A freshly
/// forked branch has no remote of its own yet, so it correctly starts with no upstream.
let resolveWorktreeCommand (repoRoot: string) (baseRef: string) (branchName: string) =
    let worktreePath = worktreeDir repoRoot branchName
    let arguments = $"-C \"{repoRoot}\" worktree add -b \"{branchName}\" --no-track \"{worktreePath}\" \"{baseRef}\""
    "git", arguments, worktreePath

let private legacyForkScriptWarning (scriptName: string) (exists: bool) =
    if exists then
        Some $"'{scriptName}' is no longer used — Treemon now creates worktrees itself. Move any setup steps into 'post-fork.ps1'/'post-fork.sh'."
    else
        None

/// Timeout for the post-fork setup hook — it runs `npm install` and `bd init`,
/// which exceed the short default used for quick git probes, but a run dragging
/// past this cap is treated as a failure (surfaced on the card) rather than
/// blocking the auto-launch indefinitely.
let private postForkTimeoutMs = 5 * 60 * 1000

/// Card label for the post-fork setup hook. Single source of truth for the
/// OS-specific script name so file resolution always tracks the hook.
let postForkScriptName =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "post-fork.ps1" else "post-fork.sh"

/// Absolute path to the OS-appropriate `post-fork` setup hook when one exists in
/// the repo root, otherwise None. Callers use this to decide whether to run — and
/// surface a card lifecycle for — a post-fork step at all.
let postForkScriptPath (repoRoot: string) : string option =
    let scriptPath = Path.Combine(repoRoot, postForkScriptName)
    if File.Exists scriptPath then Some scriptPath else None

/// Runs the optional `post-fork` setup script inside a freshly created worktree,
/// passing the worktree path, the source repo root, the base ref and the branch
/// name, capped at `timeoutMs` (a run that exceeds it is killed and returns a
/// timeout Error). Returns Ok when the script succeeds or is absent, and Error
/// with the process failure when it exits non-zero — the worktree already
/// exists, so a failure is never fatal, only surfaced on the card.
let runPostForkWithTimeout (timeoutMs: int) (repoRoot: string) (worktreePath: string) (baseRef: string) (branchName: string) : Async<Result<unit, string>> =
    async {
        match postForkScriptPath repoRoot with
        | None -> return Ok ()
        | Some scriptPath ->
            let fileName, arguments =
                if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                    "pwsh", $"-NoProfile -File \"{scriptPath}\" \"{worktreePath}\" \"{repoRoot}\" \"{baseRef}\" \"{branchName}\""
                else
                    "bash", $"\"{scriptPath}\" \"{worktreePath}\" \"{repoRoot}\" \"{baseRef}\" \"{branchName}\""

            let! result = ProcessRunner.runResultWithTimeout timeoutMs "PostFork" fileName arguments (Some worktreePath)
            return result |> Result.map ignore
    }

/// Runs the post-fork hook with the production 5-minute cap (see
/// `runPostForkWithTimeout`).
let runPostFork (repoRoot: string) (worktreePath: string) (baseRef: string) (branchName: string) : Async<Result<unit, string>> =
    runPostForkWithTimeout postForkTimeoutMs repoRoot worktreePath baseRef branchName

type ForkResult =
    { WorktreePath: string
      BaseRef: string
      Warnings: string list }

/// Forks `branchName` from `baseBranch` into a `tm-`prefixed sibling of the repo
/// root and returns as soon as `git worktree add` succeeds. Treemon owns the
/// forking: it fetches the base from upstream and forks from the remote-tracking
/// ref when available. The `post-fork` setup hook is intentionally NOT run here
/// (see `runPostFork`) so callers can run it in the background without blocking.
/// `Warnings` carries only the synchronous legacy-fork-script advisory.
let forkWorktree (repoRoot: string) (baseBranch: string) (branchName: string) : Async<Result<ForkResult, string>> =
    asyncResult {
        let! name = validateBranchName branchName
        let! validBase = validateBranchName baseBranch
        let! upstreamRemote = resolveUpstreamRemote repoRoot
        do! fetchBaseBranch repoRoot upstreamRemote validBase
        let! baseRef = resolveBaseRef repoRoot upstreamRemote validBase

        let fileName, arguments, worktreePath = resolveWorktreeCommand repoRoot baseRef name

        do!
            ProcessRunner.runResult "CreateWorktree" fileName arguments None
            |> AsyncResult.ignore

        let legacyScriptName = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "fork.ps1" else "fork.sh"
        let legacyScriptExists = File.Exists(Path.Combine(repoRoot, legacyScriptName))

        return
            { WorktreePath = worktreePath
              BaseRef = baseRef
              Warnings = List.choose id [ legacyForkScriptWarning legacyScriptName legacyScriptExists ] }
    }

let private diffStderrLimitBytes = 64 * 1024
let private summaryCaptureLimitBytes = 16 * 1024 * 1024
let private smallGitCaptureLimitBytes = 64 * 1024
let private strictUtf8 = UTF8Encoding(false, true)

let private mapDiffProcessFailure
    (operation: WorktreeDiffOperation)
    (failure: ProcessRunner.ArgumentListFailure)
    =
    match failure with
    | ProcessRunner.StartFailed _ -> GitStartFailed operation
    | ProcessRunner.TimedOut -> GitTimedOut operation
    | ProcessRunner.CaptureLimitExceeded stream ->
        GitCaptureLimitExceeded(operation, stream)

let private runDiffGit
    (operation: WorktreeDiffOperation)
    (stdoutLimitBytes: int)
    (repoRoot: string)
    (arguments: string list)
    =
    async {
        let gitArguments =
            [ "-C"; repoRoot; "-c"; "core.quotepath=false" ] @ arguments

        let! result =
            ProcessRunner.runArgumentList
                stdoutLimitBytes
                diffStderrLimitBytes
                "WorktreeDiff"
                "git"
                gitArguments
                None

        return
            match result with
            | Error failure -> Error(mapDiffProcessFailure operation failure)
            | Ok output when output.ExitCode <> 0 ->
                Log.log
                    "WorktreeDiff"
                    $"Git {operation} failed with exit {output.ExitCode} and {output.Stderr.Length} stderr bytes"

                Error(GitFailed(operation, output.ExitCode))
            | Ok output -> Ok output.Stdout
    }

let private decodeGitOutput
    (operation: WorktreeDiffOperation)
    (bytes: byte[])
    =
    try
        Ok(strictUtf8.GetString(bytes))
    with :? DecoderFallbackException ->
        Error(InvalidGitOutput operation)

let private trimSingleLine
    (operation: WorktreeDiffOperation)
    (bytes: byte[])
    =
    decodeGitOutput operation bytes
    |> Result.bind (fun output ->
        let lines =
            output.Trim().Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

        match lines with
        | [| line |] -> Ok line
        | _ -> Error(InvalidGitOutput operation))

let private runRefExists (repoRoot: string) (gitRef: string) =
    async {
        let! result =
            ProcessRunner.runArgumentList
                smallGitCaptureLimitBytes
                diffStderrLimitBytes
                "WorktreeDiff"
                "git"
                [ "-C"
                  repoRoot
                  "rev-parse"
                  "--verify"
                  "--quiet"
                  gitRef ]
                None

        return
            match result with
            | Error failure -> Error(mapDiffProcessFailure ResolveBase failure)
            | Ok output when output.ExitCode = 0 -> Ok true
            | Ok output when output.ExitCode = 1 -> Ok false
            | Ok output ->
                Log.log
                    "WorktreeDiff"
                    $"Git {ResolveBase} failed with exit {output.ExitCode} and {output.Stderr.Length} stderr bytes"

                Error(GitFailed(ResolveBase, output.ExitCode))
    }

let private resolveDiffRemote (repoRoot: string) =
    async {
        match TreemonConfig.readUpstreamRemote repoRoot with
        | Some remote -> return Ok remote
        | None ->
            let! remotes =
                runDiffGit ResolveRemote smallGitCaptureLimitBytes repoRoot [ "remote" ]

            return
                remotes
                |> Result.bind (fun bytes ->
                    decodeGitOutput ResolveRemote bytes
                    |> Result.map (Some >> selectUpstreamRemote None))
    }

let private resolveDiffBaseRef
    (repoRoot: string)
    (upstreamRemote: string)
    (baseBranch: string)
    =
    async {
        let remoteRef = mainRef upstreamRemote baseBranch
        let! remoteExists = runRefExists repoRoot $"refs/remotes/{remoteRef}"

        match remoteExists with
        | Error error -> return Error error
        | Ok remoteExists ->
            let! localResult =
                if remoteExists then
                    async.Return(Ok false)
                else
                    runRefExists repoRoot $"refs/heads/{baseBranch}"

            return
                match localResult with
                | Error error -> Error error
                | Ok localExists ->
                    selectBaseRef
                        upstreamRemote
                        baseBranch
                        remoteExists
                        localExists
                    |> Result.requireSome (BaseNotFound(baseBranch, remoteRef))
    }

let private parseNulTokens
    (operation: WorktreeDiffOperation)
    (bytes: byte[])
    =
    decodeGitOutput operation bytes
    |> Result.map (fun output ->
        let tokens = output.Split([| '\000' |], StringSplitOptions.None)

        if tokens.Length = 0 then
            []
        elif tokens[tokens.Length - 1] = "" then
            if tokens.Length = 1 then
                []
            else
                tokens[..tokens.Length - 2] |> Array.toList
        else
            tokens |> Array.toList)

let private trackedEntry (status: char) (paths: string list) =
    match status, paths with
    | ('R' | 'C'), oldPath :: newPath :: rest ->
        Ok(
            { Path = newPath
              OldPath = Some oldPath
              Status = if status = 'R' then Renamed else Added },
            rest
        )
    | ('A' | 'M' | 'D' | 'T' | 'U' | 'X' | 'B'), path :: rest ->
        let diffStatus =
            match status with
            | 'A' -> Added
            | 'D' -> Deleted
            | _ -> Modified

        Ok(
            { Path = path
              OldPath = None
              Status = diffStatus },
            rest
        )
    | _ -> Error(InvalidGitOutput EnumerateTracked)

let private parseTrackedEntries (bytes: byte[]) =
    parseNulTokens EnumerateTracked bytes
    |> Result.bind (fun tokens ->
        let rec parse
            (entries: WorktreeDiffEntry list)
            (remaining: string list)
            =
            match remaining with
            | [] -> Ok(List.rev entries)
            | statusToken :: paths when statusToken.Length > 0 ->
                trackedEntry (statusToken.Chars(0)) paths
                |> Result.bind (fun (entry, rest) -> parse (entry :: entries) rest)
            | _ -> Error(InvalidGitOutput EnumerateTracked)

        parse [] tokens)

let private parseUntrackedEntries (bytes: byte[]) =
    parseNulTokens EnumerateUntracked bytes
    |> Result.map (
        List.map (fun path ->
            { Path = path
              OldPath = None
              Status = Untracked })
    )

let private sortDiffEntries (entries: WorktreeDiffEntry list) =
    entries
    |> List.sortWith (fun left right ->
        StringComparer.Ordinal.Compare(left.Path, right.Path))

let getWorktreeDiffSummary
    (repoRoot: string)
    : Async<Result<WorktreeDiffSummary, WorktreeDiffError>> =
    asyncResult {
        let! upstreamRemote = resolveDiffRemote repoRoot
        let baseBranch = TreemonConfig.readBaseBranch repoRoot
        let! baseRef = resolveDiffBaseRef repoRoot upstreamRemote baseBranch

        let! mergeBaseBytes =
            runDiffGit
                ResolveMergeBase
                smallGitCaptureLimitBytes
                repoRoot
                [ "merge-base"; "HEAD"; baseRef ]

        let! mergeBase = trimSingleLine ResolveMergeBase mergeBaseBytes

        let! trackedBytes =
            runDiffGit
                EnumerateTracked
                summaryCaptureLimitBytes
                repoRoot
                [ "diff"
                  "--name-status"
                  "-z"
                  "--find-renames"
                  "--no-ext-diff"
                  "--no-textconv"
                  mergeBase ]

        let! tracked = parseTrackedEntries trackedBytes

        if tracked.Length > maxWorktreeDiffFiles then
            return! Error(TooManyFiles tracked.Length)

        let! untrackedBytes =
            runDiffGit
                EnumerateUntracked
                summaryCaptureLimitBytes
                repoRoot
                [ "ls-files"
                  "--others"
                  "--exclude-standard"
                  "-z"
                  "--" ]

        let! untracked = parseUntrackedEntries untrackedBytes
        let files = tracked @ untracked

        if files.Length > maxWorktreeDiffFiles then
            return! Error(TooManyFiles files.Length)

        return
            { BaseRef = baseRef
              MergeBase = mergeBase
              Files = sortDiffEntries files }
    }

let private diffLineCount (bytes: byte[]) =
    if bytes.Length = 0 then
        0
    else
        let newlineCount =
            bytes
            |> Array.sumBy (fun value -> if value = 0x0Auy then 1 else 0)

        if bytes[bytes.Length - 1] = 0x0Auy then newlineCount else newlineCount + 1

let private isBinaryPatch (patch: string) =
    patch.Split('\n')
    |> Array.exists (fun line ->
        line.StartsWith("Binary files ", StringComparison.Ordinal)
        && line.TrimEnd('\r').EndsWith(" differ", StringComparison.Ordinal))

let private isSymlinkPatch (patch: string) =
    patch.Split('\n')
    |> Array.takeWhile (fun line -> not (line.StartsWith("@@", StringComparison.Ordinal)))
    |> Array.exists (fun line ->
        let header = line.TrimEnd('\r')

        header.EndsWith(" 120000", StringComparison.Ordinal)
        || header = "old mode 120000"
        || header = "new mode 120000"
        || header = "new file mode 120000"
        || header = "deleted file mode 120000")

let private classifyTrackedPatch entry bytes =
    if diffLineCount bytes > maxWorktreeDiffLines then
        Ok Truncated
    else
        match decodeGitOutput LoadFile bytes with
        | Error _ -> Ok Binary
        | Ok patch when isBinaryPatch patch -> Ok Binary
        | Ok patch when entry.Status = Deleted -> Ok(DeletedFile patch)
        | Ok patch when isSymlinkPatch patch -> Ok(Symlink(Some patch))
        | Ok patch -> Ok(Text patch)

let private trackedDiffPaths entry =
    match entry.OldPath with
    | Some oldPath when oldPath <> entry.Path -> [ oldPath; entry.Path ]
    | _ -> [ entry.Path ]

let private getTrackedDiffFile
    (repoRoot: string)
    (mergeBase: string)
    (entry: WorktreeDiffEntry)
    =
    async {
        let! patchResult =
            runDiffGit
                LoadFile
                maxWorktreeDiffBytes
                repoRoot
                ([ "diff"
                   "--no-ext-diff"
                   "--no-textconv"
                   "--find-renames"
                   "--full-index"
                   "--no-color"
                   mergeBase
                   "--" ]
                 @ trackedDiffPaths entry)

        return
            match patchResult with
            | Error(GitCaptureLimitExceeded(LoadFile, ProcessRunner.StandardOutput)) ->
                Ok Oversized
            | Error error -> Error error
            | Ok bytes when bytes.Length = 0 -> Error FileUnavailable
            | Ok bytes -> classifyTrackedPatch entry bytes
    }

let private resolveUntrackedPath (repoRoot: string) (relativePath: string) =
    try
        let root = Path.GetFullPath(repoRoot)
        let fullPath = Path.GetFullPath(Path.Combine(root, relativePath))
        let relative = Path.GetRelativePath(root, fullPath)
        let parentPrefix = ".." + string Path.DirectorySeparatorChar

        if
            Path.IsPathRooted(relative)
            || relative = ".."
            || relative.StartsWith(parentPrefix, StringComparison.Ordinal)
        then
            Error FileUnavailable
        else
            Ok fullPath
    with _ ->
        Error FileUnavailable

let private isSymbolicLink (fileInfo: FileInfo) =
    let hasLinkTarget =
        try
            not (isNull fileInfo.LinkTarget)
        with _ ->
            false

    if hasLinkTarget then
        true
    else
        try
            fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
        with _ ->
            false

let private readFileBounded (path: string) =
    try
        use stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite ||| FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan
            )

        use captured =
            new MemoryStream(
                min
                    maxWorktreeDiffBytes
                    (int (min stream.Length (int64 maxWorktreeDiffBytes)))
            )

        let buffer = Array.zeroCreate<byte> (64 * 1024)

        let rec read () =
            let count = stream.Read(buffer, 0, buffer.Length)

            if count = 0 then
                FileBytes(captured.ToArray())
            else
                let remaining = maxWorktreeDiffBytes - int captured.Length

                if count > remaining then
                    FileTooLarge
                else
                    captured.Write(buffer, 0, count)
                    read ()

        read ()
    with _ ->
        FileReadFailed

let private gitPatchPath prefix (path: string) =
    let value = prefix + path.Replace('\\', '/')

    let needsQuotes =
        value
        |> Seq.exists (fun character ->
            Char.IsWhiteSpace(character)
            || Char.IsControl(character)
            || character = '"'
            || character = '\\')

    if not needsQuotes then
        value
    else
        let escaped =
            value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\t", "\\t")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")

        $"\"{escaped}\""

let private synthesizeUntrackedPatch (path: string) (bytes: byte[]) =
    if bytes |> Array.contains 0uy then
        Binary
    else
        try
            let text = strictUtf8.GetString(bytes)
            let endsWithNewline =
                bytes.Length > 0
                && bytes[bytes.Length - 1] = 0x0Auy
            let oldPath = gitPatchPath "a/" path
            let newPath = gitPatchPath "b/" path

            let contentLines =
                if text.Length = 0 then
                    []
                else
                    let split = text.Split('\n') |> Array.toList
                    if endsWithNewline then split |> List.take (split.Length - 1) else split

            let header =
                [ $"diff --git {oldPath} {newPath}"
                  "new file mode 100644"
                  "--- /dev/null"
                  $"+++ {newPath}" ]

            let hunk =
                if contentLines.IsEmpty then
                    []
                else
                    [ $"@@ -0,0 +1,{contentLines.Length} @@" ]

            let body = contentLines |> List.map (fun line -> "+" + line)

            let noNewlineMarker =
                if contentLines.IsEmpty || endsWithNewline then
                    []
                else
                    [ "\\ No newline at end of file" ]

            let patch =
                header @ hunk @ body @ noNewlineMarker
                |> String.concat "\n"
                |> fun value -> value + "\n"

            let patchBytes = Encoding.UTF8.GetBytes(patch)

            if patchBytes.Length > maxWorktreeDiffBytes then
                Oversized
            elif diffLineCount patchBytes > maxWorktreeDiffLines then
                Truncated
            else
                Text patch
        with :? DecoderFallbackException ->
            Binary

let private getUntrackedDiffFile (repoRoot: string) (entry: WorktreeDiffEntry) =
    async {
        return
            resolveUntrackedPath repoRoot entry.Path
            |> Result.bind (fun path ->
                let info = FileInfo(path)

                if isSymbolicLink info then
                    Ok(Symlink None)
                elif not info.Exists then
                    Error FileUnavailable
                elif info.Length > int64 maxWorktreeDiffBytes then
                    Ok Oversized
                else
                    match readFileBounded path with
                    | FileBytes bytes -> Ok(synthesizeUntrackedPatch entry.Path bytes)
                    | FileTooLarge -> Ok Oversized
                    | FileReadFailed -> Error FileUnavailable)
    }

let getWorktreeDiffFile
    (repoRoot: string)
    (mergeBase: string)
    (entry: WorktreeDiffEntry)
    : Async<Result<WorktreeDiffFile, WorktreeDiffError>> =
    match entry.Status with
    | Untracked -> getUntrackedDiffFile repoRoot entry
    | _ -> getTrackedDiffFile repoRoot mergeBase entry
