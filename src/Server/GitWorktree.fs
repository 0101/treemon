module Server.GitWorktree

open System
open System.Diagnostics
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
    async {
        try
            let psi =
                ProcessStartInfo(
                    "git",
                    arguments,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            use proc = Process.Start(psi)
            let! stdout = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
            let! stderr = proc.StandardError.ReadToEndAsync() |> Async.AwaitTask
            do! proc.WaitForExitAsync() |> Async.AwaitTask
            let exitCode = proc.ExitCode

            match exitCode with
            | 0 ->
                let trimmed = stdout.TrimEnd()
                let preview = if trimmed.Length > 200 then trimmed.Substring(0, 200) + "..." else trimmed
                Log.log "Git" (sprintf "git %s (in %s) -> exit 0, stdout: %s" arguments workingDir preview)
                return Some trimmed
            | _ ->
                Log.log "Git" (sprintf "git %s (in %s) -> exit %d, stderr: %s" arguments workingDir exitCode (stderr.TrimEnd()))
                return None
        with
        | :? System.ComponentModel.Win32Exception as ex ->
            Log.log "Git" (sprintf "git %s (in %s) -> failed to start: %s" arguments workingDir ex.Message)
            return None
    }

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

let getLastCommit (worktreePath: string) =
    async {
        let! output = runGit worktreePath "log -1 --format=%H%n%s%n%aI"

        return
            output
            |> Option.bind (fun raw ->
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

let collectWorktreeGitData (worktreePath: string) (branch: string option) =
    async {
        let! commit = getLastCommit worktreePath
        let! upstream = getUpstreamBranch worktreePath

        let commitHash = commit |> Option.map (fun c -> c.Hash) |> Option.defaultValue ""
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

        return
            { Branch = branch |> Option.defaultValue "(detached)"
              Head = commitHash
              LastCommitMessage = commitMessage
              LastCommitTime = commitTime
              UpstreamBranch = upstreamBranch
              Beads = { Open = 0; InProgress = 0; Closed = 0 }
              Claude = ClaudeCodeStatus.Unknown
              Pr = NoPr
              IsStale = false }
    }
