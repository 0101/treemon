module Server.GitWorktree

open System
open System.IO
open System.Runtime.InteropServices
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

/// Outcome of resolving a worktree's upstream tracking branch (`git rev-parse --abbrev-ref @{u}`).
/// Distinguishes git's deterministic "no upstream configured" from a transient read failure
/// (timeout, `index.lock`, IO error) so downstream prune logic never mistakes a failed read for
/// "this branch has no upstream" and wrongly forgets a merged PR (spec merged-pr-persistence.md,
/// Decision #8 residual).
type UpstreamResult =
    | Upstream of string
    | NoUpstream
    | UpstreamReadFailed

type GitData =
    { Path: string
      Branch: string
      /// The worktree tip commit hash (from `getLastCommit`), used as the identity stamp for a
      /// merged-PR record so a reused branch name cannot resurrect a prior incarnation's badge
      /// (spec merged-pr-persistence.md, Decision #11). Empty when no commit could be read.
      HeadCommit: string
      LastCommitMessage: string
      LastCommitTime: DateTimeOffset
      /// Resolved upstream tracking state. `Upstream` carries the remote-stripped branch name;
      /// `UpstreamReadFailed` marks a transient read (timeout/lock/IO) rather than git deterministically
      /// reporting no upstream, so the merged-PR prune enumeration can exclude this worktree instead of
      /// reading a failed read as "no branch" (Decision #8 residual).
      Upstream: UpstreamResult
      MainBehindCount: int
      IsDirty: bool
      WorkMetrics: Shared.WorkMetrics option }

/// The remote-stripped upstream branch name, present only when a tracking branch was read successfully.
let upstreamBranchName =
    function
    | Upstream u -> Some u
    | NoUpstream
    | UpstreamReadFailed -> None

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

let listWorktrees (repoRoot: string) =
    async {
        let! output = runGit repoRoot "worktree list --porcelain"

        return
            output
            |> Option.map parseWorktreeList
            |> Option.defaultValue []
            |> List.filter (fun wt -> Directory.Exists(wt.Path))
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

/// git reports a genuine, stable "no upstream" deterministically via one of these fatals: the branch
/// never configured a tracking ref ("no upstream configured"), HEAD is detached ("does not point to
/// a branch"), or the branch is unborn / has no commits ("no such branch: '<name>'"). These are the
/// only error states safe to treat as "this worktree contributes no branch" — each is stable and
/// carries no merged-PR record to lose, so pruning may proceed. EVERY other stderr — a timeout, an
/// `index.lock`, an IO error, or `ambiguous argument '@{u}': unknown revision` (an upstream that WAS
/// configured but is now unresolvable, e.g. a merged-then-deleted remote branch after `fetch
/// --prune`) — is a read failure whose branch is *unknown*, not absent, so we must not mistake it for
/// "no upstream" and prune a still-valid record. See `classifyUpstream`. NOTE: these match English
/// git output; the target (Git for Windows) ships without gettext localization, so they are stable.
let private noUpstreamMarkers =
    [ "no upstream configured"
      "does not point to a branch"
      "no such branch" ]

/// Pure classification of a `git rev-parse --abbrev-ref @{u}` result into the three cases the
/// merged-PR prune logic distinguishes (spec merged-pr-persistence.md, Decision #8 residual):
///  - `Upstream name` — configured and read cleanly;
///  - `NoUpstream` — git deterministically reports no upstream (branch tracks nothing, detached, or
///    unborn) — a stable state carrying no record to lose, so it is safe to prune against;
///  - `UpstreamReadFailed` — anything else: a transient failure (timeout/lock/IO), an unrecognized
///    error, a configured-but-unresolvable upstream, or an anomalous empty success. The upstream is
///    *unknown*, not proven absent, so the branch must be excluded from the prune enumeration.
/// Defaulting the unrecognized case to `UpstreamReadFailed` is deliberate: only the explicit markers
/// are safe to prune against; everything else errs toward never forgetting a merged PR.
let internal classifyUpstream (result: Result<string, string>) : UpstreamResult =
    match result with
    | Ok output ->
        let trimmed = output.Trim()
        if String.IsNullOrEmpty trimmed then UpstreamReadFailed else Upstream trimmed
    | Error message ->
        let lowered = message.ToLowerInvariant()

        if noUpstreamMarkers |> List.exists (fun marker -> lowered.Contains(marker)) then
            NoUpstream
        else
            UpstreamReadFailed

let getUpstreamBranch (worktreePath: string) : Async<UpstreamResult> =
    async {
        let! result = runGitResult worktreePath "rev-parse --abbrev-ref @{u}"
        return classifyUpstream result
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

        // Strip the remote prefix ("origin/foo" -> "foo"); the store/PR-map key is the bare branch.
        let stripRemote (u: string) =
            match u.IndexOf('/') with
            | -1 -> u
            | i -> u[(i + 1)..]

        // Store the remote-stripped upstream state, preserving the DU so a transient read failure
        // (vs. git's clean "no upstream") stays distinct and the prune enumeration can exclude this
        // worktree instead of mistaking it for "no branch" (Decision #8).
        let upstreamState =
            match upstream with
            | Upstream u -> Upstream(stripRemote u)
            | NoUpstream -> NoUpstream
            | UpstreamReadFailed -> UpstreamReadFailed

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
              HeadCommit = commit |> Option.map _.Hash |> Option.defaultValue ""
              LastCommitMessage = commit |> Option.map _.Message |> Option.defaultValue ""
              LastCommitTime = commit |> Option.map _.Time |> Option.defaultValue DateTimeOffset.MinValue
              Upstream = upstreamState
              MainBehindCount = mainBehind
              IsDirty = dirty
              WorkMetrics = workMetrics }
    }

let resolveUpstreamRemote (repoRoot: string) =
    async {
        match TreemonConfig.readUpstreamRemote repoRoot with
        | Some remote -> return remote
        | None ->
            let! output = runGit repoRoot "remote"

            let hasUpstream =
                output
                |> Option.exists (fun s ->
                    s.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.exists (fun r -> r.Trim() = "upstream"))

            return if hasUpstream then "upstream" else "origin"
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

/// Resolves the base branch to a concrete git ref to fork from. Prefers the
/// remote-tracking ref (e.g. `upstream/main`) so a new worktree forks from the
/// upstream tip rather than a possibly-stale local branch, falling back to the
/// local branch when no remote-tracking ref exists. Does not require any worktree
/// to currently have the base checked out.
let resolveBaseRef (repoRoot: string) (upstreamRemote: string) (baseBranch: string) =
    async {
        let remoteRef = mainRef upstreamRemote baseBranch
        let! remoteExists = gitRefExists repoRoot $"refs/remotes/{remoteRef}"

        if remoteExists then
            return Ok remoteRef
        else
            let! localExists = gitRefExists repoRoot $"refs/heads/{baseBranch}"

            return
                if localExists then Ok baseBranch
                else Error $"Base branch '{baseBranch}' not found as '{remoteRef}' or as a local branch"
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
/// worktree path.
let resolveWorktreeCommand (repoRoot: string) (baseRef: string) (branchName: string) =
    let worktreePath = worktreeDir repoRoot branchName
    let arguments = $"-C \"{repoRoot}\" worktree add -b \"{branchName}\" \"{worktreePath}\" \"{baseRef}\""
    "git", arguments, worktreePath

let private legacyForkScriptWarning (scriptName: string) (exists: bool) =
    if exists then
        Some $"'{scriptName}' is no longer used — Treemon now creates worktrees itself. Move any setup steps into 'post-fork.ps1'/'post-fork.sh'."
    else
        None

/// Generous timeout for the post-fork setup hook — it runs `npm install` and
/// `bd init`, which can far exceed the short default used for quick git probes.
let private postForkTimeoutMs = 10 * 60 * 1000

/// Runs an optional `post-fork` setup script inside the freshly created worktree,
/// passing the worktree path, the source repo root, the base ref and the branch
/// name. A failure is reported as a warning, never a hard error — the worktree
/// already exists at this point.
let private runPostFork (repoRoot: string) (worktreePath: string) (baseRef: string) (branchName: string) =
    async {
        let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        let scriptName = if isWindows then "post-fork.ps1" else "post-fork.sh"
        let scriptPath = Path.Combine(repoRoot, scriptName)

        if not (File.Exists scriptPath) then
            return None
        else
            let fileName, arguments =
                if isWindows then "pwsh", $"-NoProfile -File \"{scriptPath}\" \"{worktreePath}\" \"{repoRoot}\" \"{baseRef}\" \"{branchName}\""
                else "bash", $"\"{scriptPath}\" \"{worktreePath}\" \"{repoRoot}\" \"{baseRef}\" \"{branchName}\""

            let! result = ProcessRunner.runResultWithTimeout postForkTimeoutMs "PostFork" fileName arguments (Some worktreePath)

            return
                match result with
                | Ok _ -> None
                | Error msg -> Some $"Worktree created, but '{scriptName}' setup failed: {msg}. Dependencies may be incomplete — re-run setup in the worktree."
    }

/// Creates a new worktree, forking `branchName` from `baseBranch`. Treemon owns
/// the forking: it fetches the base from upstream, forks from the remote-tracking
/// ref when available, then runs an optional `post-fork` setup script. Returns any
/// non-fatal warnings (a legacy fork script is present, or post-fork failed).
let createWorktree (repoRoot: string) (baseBranch: string) (branchName: string) =
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

        let! postForkWarning = runPostFork repoRoot worktreePath baseRef name

        let legacyScriptName = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "fork.ps1" else "fork.sh"
        let legacyScriptExists = File.Exists(Path.Combine(repoRoot, legacyScriptName))

        return List.choose id [ legacyForkScriptWarning legacyScriptName legacyScriptExists; postForkWarning ]
    }
