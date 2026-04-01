module Server.GitWorktree

open System
open System.IO
open System.Runtime.InteropServices

let [<Literal>] DetachedBranchName = "(detached)"

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
            |> Array.tryFind (fun l -> l.StartsWith(prefix))
            |> Option.map (fun l -> l[prefix.Length..])

        let isPrunable = lines |> Array.exists (fun l -> l.StartsWith("prunable"))

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

let private tryFastForwardMain (repoRoot: string) (mainRef: string) =
    async {
        let! currentBranch = runGit repoRoot "rev-parse --abbrev-ref HEAD"

        match currentBranch |> Option.map _.Trim() with
        | Some "main" ->
            let! result = runGitResult repoRoot $"merge --ff-only {mainRef}"

            match result with
            | Ok _ -> Log.log "Git" $"Fast-forwarded main in {repoRoot}"
            | Error msg -> Log.log "Git" $"Fast-forward skipped in {repoRoot}: {msg}"
        | _ -> ()
    }

let mainRef (upstreamRemote: string) = $"{upstreamRemote}/main"

let fetchUpstream (repoRoot: string) (upstreamRemote: string) =
    async {
        let! _ = runGit repoRoot $"fetch {upstreamRemote} main"
        do! tryFastForwardMain repoRoot (mainRef upstreamRemote)
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
              Branch = branch |> Option.defaultValue DetachedBranchName
              LastCommitMessage = commit |> Option.map _.Message |> Option.defaultValue ""
              LastCommitTime = commit |> Option.map _.Time |> Option.defaultValue DateTimeOffset.MinValue
              UpstreamBranch = upstreamBranch
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

let removeWorktree (repoRoot: string) (worktreePath: string) (branch: string option) =
    async {
        let! removeResult = runGitResult repoRoot $"""worktree remove --force "{worktreePath}" """

        let! effectiveResult =
            match removeResult with
            | Ok _ -> async { return Ok () }
            | Error removeMsg ->
                async {
                    if Directory.Exists(Path.Combine(worktreePath, ".git")) then
                        return Error "Cannot delete the main worktree"
                    else
                        let! listOutput = runGit repoRoot "worktree list --porcelain"
                        let normalizedPath = Server.PathUtils.normalizePath worktreePath

                        let isPrunable =
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
                                    let hasPrunable = lines |> Array.exists (fun line -> line.StartsWith("prunable"))
                                    hasPath && hasPrunable))

                        if not isPrunable then
                            return Error $"git worktree remove failed: {removeMsg}"
                        else
                            let! pruneResult = runGitResult repoRoot "worktree prune"

                            match pruneResult with
                            | Error pruneMsg ->
                                return Error $"git worktree remove failed: {removeMsg} (prune also failed: {pruneMsg})"
                            | Ok _ ->
                                try
                                    if Directory.Exists(worktreePath) then
                                        Directory.Delete(worktreePath, recursive = true)

                                    return
                                        if Directory.Exists(worktreePath) then
                                            Error $"git worktree remove failed: {removeMsg}"
                                        else Ok ()
                                with ex ->
                                    return Error $"Worktree cleanup failed after prune: {ex.Message}"
                }

        match effectiveResult, branch with
        | Error msg, _ -> return Error msg
        | Ok _, None -> return Ok ()
        | Ok _, Some b ->
            let! branchResult = runGitResult repoRoot $"branch -D -- \"{b}\""

            match branchResult with
            | Ok _ -> return Ok ()
            | Error msg -> return Error $"Worktree removed but git branch -D failed: {msg}"
    }

let branchSortKey (name: string) =
    match name with
    | "main" -> (0, name)
    | "master" -> (1, name)
    | "develop" -> (2, name)
    | n when n.StartsWith("dev") -> (3, name)
    | _ -> (4, name)

let private validBranchNamePattern = System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9._/-]+$")

let validateBranchName (branchName: string) =
    if validBranchNamePattern.IsMatch(branchName) then
        Ok branchName
    else
        Error $"Invalid branch name: '{branchName}'"

let resolveWorktreeCommand (repoRoot: string) (sourceWorktreePath: string) (branchName: string) (forkScript: string option) =
    match forkScript with
    | Some scriptPath ->
        let fileName, arguments =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "powershell", $"-File \"{scriptPath}\" \"{branchName}\""
            else "bash", $"\"{scriptPath}\" \"{branchName}\""

        fileName, arguments, Some sourceWorktreePath
    | None ->
        let parentDir = Path.GetDirectoryName(repoRoot)
        let dirName = branchName.Replace('/', '-')
        let worktreePath = Path.Combine(parentDir, $"tm-{dirName}")
        "git", $"-C \"{sourceWorktreePath}\" worktree add -b \"{branchName}\" \"{worktreePath}\"", None

let createWorktree (repoRoot: string) (sourceWorktreePath: string) (branchName: string) =
    async {
        match validateBranchName branchName with
        | Error msg -> return Error msg
        | Ok name ->
            let scriptPath =
                if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then Path.Combine(repoRoot, "fork.ps1")
                else Path.Combine(repoRoot, "fork.sh")

            let forkScript = if File.Exists(scriptPath) then Some scriptPath else None
            let fileName, arguments, workingDir = resolveWorktreeCommand repoRoot sourceWorktreePath name forkScript

            let! result = ProcessRunner.runResult "CreateWorktree" fileName arguments workingDir
            return result |> Result.map ignore
    }
