module Server.GitWorktree

open System
open System.IO
open Shared

type WorktreeInfo =
    { Path: string
      Head: string
      Branch: string option }

type CommitInfo =
    { Hash: string
      Message: string
      Time: DateTimeOffset }

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
            |> Option.map (fun l -> l.Substring(prefix.Length))

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
                    Log.log "Git" (sprintf "getLastCommit(%s): failed to parse time '%s'" worktreePath timeStr)
                    None
            | _ ->
                Log.log "Git" (sprintf "getLastCommit(%s): expected 3 lines (hash/message/time), got %d" worktreePath lines.Length)
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
    type CacheEntry<'T> =
        { Value: 'T
          CachedAt: DateTimeOffset }

    let private worktreeListCache =
        System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry<WorktreeInfo list>>()

    let private ttl = TimeSpan.FromSeconds(60.0)

    let getCachedWorktrees (repoRoot: string) =
        async {
            let now = DateTimeOffset.UtcNow

            match worktreeListCache.TryGetValue(repoRoot) with
            | true, entry when now - entry.CachedAt < ttl -> return entry.Value
            | _ ->
                let! worktrees = listWorktrees repoRoot

                worktreeListCache.[repoRoot] <-
                    { Value = worktrees
                      CachedAt = now }

                return worktrees
        }

    let getCachedAt (repoRoot: string) =
        match worktreeListCache.TryGetValue(repoRoot) with
        | true, entry -> Some entry.CachedAt
        | _ -> None

    let invalidate (repoRoot: string) =
        worktreeListCache.TryRemove(repoRoot) |> ignore

let collectWorktreeGitData (worktreePath: string) (branch: string option) =
    async {
        let! commit = getLastCommit worktreePath
        let! upstream = getUpstreamBranch worktreePath
        let! mainBehind = getMainBehindCount worktreePath

        let commitMessage = commit |> Option.map (fun c -> c.Message) |> Option.defaultValue ""

        let commitTime =
            commit
            |> Option.map (fun c -> c.Time)
            |> Option.defaultValue DateTimeOffset.MinValue

        let upstreamBranch =
            upstream
            |> Option.map (fun u ->
                if u.StartsWith("origin/") then
                    u.Substring("origin/".Length)
                else
                    u)

        let status =
            { Path = worktreePath
              Branch = branch |> Option.defaultValue "(detached)"
              LastCommitMessage = commitMessage
              LastCommitTime = commitTime
              Beads = BeadsSummary.zero
              Claude = ClaudeCodeStatus.Unknown
              Pr = NoPr
              MainBehindCount = mainBehind }

        return status, upstreamBranch
    }

let removeWorktree (repoRoot: string) (worktreePath: string) (branch: string) =
    async {
        let! removeResult = runGitResult repoRoot (sprintf "worktree remove --force \"%s\"" worktreePath)

        match removeResult with
        | Error msg -> return Error (sprintf "git worktree remove failed: %s" msg)
        | Ok _ ->
            let! branchResult = runGitResult repoRoot (sprintf "branch -D %s" branch)

            match branchResult with
            | Error msg -> return Error (sprintf "Worktree removed but git branch -D failed: %s" msg)
            | Ok _ -> return Ok ()
    }
