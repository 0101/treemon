module Server.GitWorktree

open System
open System.IO

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
    ProcessRunner.runResult "Git" "git" $"-C \"{workingDir}\" {arguments}"

let private parseWorktreeList (porcelainOutput: string) =
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
            |> Option.map (fun l -> l.[prefix.Length..])

        match findValue "worktree ", findValue "HEAD " with
        | Some path, Some head ->
            let branch =
                findValue "branch refs/heads/"

            Some
                { Path = path
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

let private parseCommitOutput (worktreePath: string) (output: string option) =
    output
    |> Option.bind (fun raw ->
        match raw with
        | "" -> None
        | _ ->
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

let private fetchCache = Cache.TtlCache<unit>(TimeSpan.FromSeconds(120.0))

let fetchFromOrigin (repoRoot: string) =
    fetchCache.GetOrRefreshAsync repoRoot (fun root ->
        async {
            let! _ = runGit root "fetch origin main"
            return ()
        })

let getMainBehindCount (worktreePath: string) =
    async {
        let! output = runGit worktreePath "rev-list --count HEAD..origin/main"

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

                if String.IsNullOrEmpty(trimmed) then
                    None
                else
                    Some trimmed)
    }

module Cache =
    let private cache = Cache.TtlCache<WorktreeInfo list>(TimeSpan.FromSeconds(60.0))

    let getCachedWorktrees (repoRoot: string) =
        cache.GetOrRefreshAsync repoRoot (fun key -> listWorktrees key)

    let getCachedAt (repoRoot: string) = cache.GetCachedAt repoRoot
    let invalidate (repoRoot: string) = cache.Invalidate repoRoot

let isDirty (worktreePath: string) =
    async {
        let! output = runGit worktreePath "status --porcelain"

        return
            output
            |> Option.map (fun s -> s.Trim().Length > 0)
            |> Option.defaultValue false
    }

let getCommitCount (worktreePath: string) =
    async {
        let! output = runGit worktreePath "rev-list --count --no-merges origin/main..HEAD"

        return
            output
            |> Option.bind (fun s ->
                match Int32.TryParse(s.Trim()) with
                | true, count -> Some count
                | _ -> None)
            |> Option.defaultValue 0
    }

let private parseDiffStats (output: string option) =
    output
    |> Option.bind (fun s ->
        let trimmed = s.Trim()

        match trimmed with
        | "" -> None
        | _ ->
            let insertions =
                System.Text.RegularExpressions.Regex.Match(trimmed, @"(\d+) insertion")
                |> fun m ->
                    match m.Success with
                    | true -> Int32.Parse(m.Groups.[1].Value)
                    | false -> 0

            let deletions =
                System.Text.RegularExpressions.Regex.Match(trimmed, @"(\d+) deletion")
                |> fun m ->
                    match m.Success with
                    | true -> Int32.Parse(m.Groups.[1].Value)
                    | false -> 0

            Some(insertions, deletions))
    |> Option.defaultValue (0, 0)

let getDiffStats (worktreePath: string) =
    async {
        let! output = runGit worktreePath "diff --shortstat origin/main...HEAD"
        return parseDiffStats output
    }

let collectWorktreeGitData (repoRoot: string) (worktreePath: string) (branch: string option) =
    async {
        let! commitChild = Async.StartChild(getLastCommit worktreePath)
        let! upstreamChild = Async.StartChild(getUpstreamBranch worktreePath)
        let! dirtyChild = Async.StartChild(isDirty worktreePath)
        let! commitCountChild = Async.StartChild(getCommitCount worktreePath)
        let! diffStatsChild = Async.StartChild(getDiffStats worktreePath)
        do! fetchFromOrigin repoRoot
        let! mainBehindChild = Async.StartChild(getMainBehindCount worktreePath)

        let! commit = commitChild
        let! upstream = upstreamChild
        let! mainBehind = mainBehindChild
        let! dirty = dirtyChild
        let! commitCount = commitCountChild
        let! (linesAdded, linesRemoved) = diffStatsChild

        let upstreamBranch =
            upstream
            |> Option.map (fun (u: string) ->
                if u.StartsWith("origin/") then
                    u.["origin/".Length..]
                else
                    u)

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
              Branch = branch |> Option.defaultValue "(detached)"
              LastCommitMessage = commit |> Option.map (fun c -> c.Message) |> Option.defaultValue ""
              LastCommitTime = commit |> Option.map (fun c -> c.Time) |> Option.defaultValue DateTimeOffset.MinValue
              UpstreamBranch = upstreamBranch
              MainBehindCount = mainBehind
              IsDirty = dirty
              WorkMetrics = workMetrics }
    }

let removeWorktree (repoRoot: string) (worktreePath: string) (branch: string) =
    async {
        let! removeResult = runGitResult repoRoot $"""worktree remove --force "{worktreePath}" """

        match removeResult with
        | Error msg -> return Error $"git worktree remove failed: {msg}"
        | Ok _ ->
            let! branchResult = runGitResult repoRoot $"branch -D {branch}"

            match branchResult with
            | Error msg -> return Error $"Worktree removed but git branch -D failed: {msg}"
            | Ok _ -> return Ok ()
    }
