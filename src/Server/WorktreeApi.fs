module Server.WorktreeApi

open System
open Shared

let private isStale (status: WorktreeStatus) =
    let twentyFourHoursAgo = DateTimeOffset.UtcNow.AddHours(-24.0)
    let commitOld = status.LastCommitTime < twentyFourHoursAgo
    let ccInactive = status.Claude = ClaudeCodeStatus.Idle || status.Claude = ClaudeCodeStatus.Unknown
    let noBeadsInProgress = status.Beads.InProgress = 0

    let prInactive =
        match status.Pr with
        | NoPr -> true
        | HasPr _ -> false

    commitOld && ccInactive && noBeadsInProgress && prInactive

let private assembleWorktreeStatus
    (repoRoot: string)
    (prMap: Map<string, PrStatus>)
    (wt: GitWorktree.WorktreeInfo)
    =
    async {
        try
            let! gitData = GitWorktree.collectWorktreeGitData wt.Path wt.Branch
            let! beads = BeadsStatus.Cache.getCachedBeadsSummary wt.Path
            let claude = ClaudeStatus.Cache.getCachedClaudeStatus wt.Path

            let upstreamBranch = gitData.UpstreamBranch
            let pr = PrStatus.Cache.lookupPrStatus prMap upstreamBranch

            let status =
                { gitData with
                    Beads = beads
                    Claude = claude
                    Pr = pr }

            return { status with IsStale = isStale status }
        with ex ->
            Log.log "API" (sprintf "Failed to assemble status for worktree %s (%s): %s" wt.Path (wt.Branch |> Option.defaultValue "(detached)") ex.Message)

            let degraded =
                { Branch = wt.Branch |> Option.defaultValue "(detached)"
                  Head = ""
                  LastCommitMessage = ""
                  LastCommitTime = DateTimeOffset.MinValue
                  UpstreamBranch = None
                  Beads = { Open = 0; InProgress = 0; Closed = 0 }
                  Claude = Unknown
                  Pr = NoPr
                  IsStale = true }

            return degraded
    }

let getWorktrees (worktreeRoot: string) : Async<WorktreeStatus list> =
    async {
        let! worktrees = GitWorktree.Cache.getCachedWorktrees worktreeRoot
        let! prMap = PrStatus.Cache.getCachedPrStatuses worktreeRoot

        let! statuses =
            worktrees
            |> List.map (assembleWorktreeStatus worktreeRoot prMap)
            |> Async.Parallel

        return statuses |> Array.toList
    }

let worktreeApi (worktreeRoot: string) : IWorktreeApi =
    { getWorktrees = fun () -> getWorktrees worktreeRoot }
