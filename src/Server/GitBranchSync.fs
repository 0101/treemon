module Server.GitBranchSync

open System
open FsToolkit.ErrorHandling

/// What Treemon's own mechanical sync of a worktree onto its base branch did. A closed set carrying
/// no Git text: the caller turns a failure case into an agent prompt, and a prompt must never quote
/// stdout, stderr, refs, or paths (see `docs/spec/worktree-monitor.md`, Branch Sync).
[<RequireQualifiedAccess>]
type BranchSyncOutcome =
    | FastForwarded
    | Merged
    | AlreadyCurrent
    | RefusedDirty
    | Conflicted
    /// The conflicted merge could not be aborted, so a merge may still be in progress.
    | AbortFailed
    /// The worktree is no longer on the branch this sync was started for, a detached `HEAD`
    /// included, so nothing was merged.
    | BranchChanged
    | CommandFailed

/// One sync's inputs. A record rather than a parameter list because every field is a string and two
/// of them name branches, so a swapped pair would silently fetch the wrong ref or merge into a
/// branch nobody observed. `Branch` is the branch the caller observed the worktree on: the merge
/// runs only while the worktree still has that branch checked out.
type BranchSyncRequest =
    { WorktreePath: string
      UpstreamRemote: string
      BaseBranch: string
      Branch: string }

/// A sync fetches over the network from background work that no HTTP response is waiting on, so it
/// gets its own timeout instead of the interactive response deadline `runArgumentList` applies.
let private branchSyncTimeoutMs = 120_000

/// Nothing in the sync reads Git's output; the bound only stops a pathological repository from
/// streaming megabytes through the server process.
let private branchSyncCaptureLimitBytes = 64 * 1024

/// Every sync and push command goes through an argument list, so a remote or branch name can never
/// be parsed as part of a command string.
let private runBranchGit (worktreePath: string) (arguments: string list) =
    ProcessRunner.runArgumentListWithTimeout
        branchSyncTimeoutMs
        branchSyncCaptureLimitBytes
        branchSyncCaptureLimitBytes
        "Git"
        "git"
        ("-C" :: worktreePath :: arguments)
        None

let private singleLineStdout (output: ProcessRunner.ArgumentListOutput) =
    Text.Encoding.UTF8.GetString(output.Stdout).Trim()

/// The branch a worktree currently has checked out, or `None` when there is no branch to act on: a
/// detached `HEAD` answers the literal `HEAD`, and a command that failed answers nothing. Both mean
/// the same thing to a caller holding an earlier observation — this is not the branch you saw.
let private checkedOutBranch (worktreePath: string) =
    async {
        match! runBranchGit worktreePath [ "rev-parse"; "--abbrev-ref"; "HEAD" ] with
        | Ok output when output.ExitCode = 0 ->
            return
                match singleLineStdout output with
                | ""
                | "HEAD" -> None
                | branch -> Some branch
        | _ -> return None
    }

/// The tree must still be on the branch an earlier observation named. Asked of git as late as the
/// CLI allows, immediately before the command that acts, because a checkout decides which branch a
/// merge lands on and which branch's work a push publishes. Each caller supplies the outcome its own
/// vocabulary uses for a worktree that moved on.
let private ensureOnBranch branchChanged worktreePath branch =
    async {
        match! checkedOutBranch worktreePath with
        | Some current when current = branch -> return Ok()
        | _ -> return Error branchChanged
    }

let private runBranchSyncGit (worktreePath: string) (arguments: string list) =
    runBranchGit worktreePath arguments
    |> AsyncResult.mapError (fun _ -> BranchSyncOutcome.CommandFailed)

let private branchSyncExitCode worktreePath arguments =
    runBranchSyncGit worktreePath arguments |> AsyncResult.map _.ExitCode

/// The revision this worktree just fetched. `FETCH_HEAD` is per-worktree, so it is exactly what the
/// preceding fetch wrote; resolving it once means the merge and the ancestry check afterwards refer
/// to the same revision even if the base advances again mid-operation.
let private fetchedBaseRevision worktreePath =
    asyncResult {
        let! output = runBranchSyncGit worktreePath [ "rev-parse"; "--verify"; "FETCH_HEAD" ]
        let revision = singleLineStdout output

        if output.ExitCode <> 0 || revision = "" then
            return! Error BranchSyncOutcome.CommandFailed
        else
            return revision
    }

/// `merge-base --is-ancestor` answers 0 or 1 and reserves every other exit code for a real failure,
/// so a broken command is never read as "the base has not reached HEAD".
let private headContains worktreePath revision =
    asyncResult {
        let! exitCode =
            branchSyncExitCode worktreePath [ "merge-base"; "--is-ancestor"; revision; "HEAD" ]

        return!
            match exitCode with
            | 0 -> Ok true
            | 1 -> Ok false
            | _ -> Error BranchSyncOutcome.CommandFailed
    }

/// A merge that did not complete either left a conflict behind or refused before touching anything.
/// `MERGE_HEAD` is git's own record of a merge in progress, so the two are told apart without reading
/// any message text, and `AbortFailed` keeps meaning "a merge may still be in progress".
let private failedMergeOutcome worktreePath =
    async {
        match! branchSyncExitCode worktreePath [ "rev-parse"; "--verify"; "--quiet"; "MERGE_HEAD" ] with
        | Ok 0 ->
            match! branchSyncExitCode worktreePath [ "merge"; "--abort" ] with
            | Ok 0 -> return BranchSyncOutcome.Conflicted
            | _ -> return BranchSyncOutcome.AbortFailed
        | Ok _ -> return BranchSyncOutcome.CommandFailed
        | Error outcome -> return outcome
    }

/// One merge attempt, bound to the branch the sync was started for. `git merge` acts on whatever
/// `HEAD` names at the moment it runs, so the branch is re-read from the tree here — immediately
/// before the mutation — rather than trusted from the observation that started the run; from the
/// command itself onwards git's own worktree locks take over.
let private mergeOnBranch (request: BranchSyncRequest) arguments =
    asyncResult {
        do! ensureOnBranch BranchSyncOutcome.BranchChanged request.WorktreePath request.Branch
        return! branchSyncExitCode request.WorktreePath ("merge" :: arguments)
    }

let private mergeFetchedBase request revision =
    asyncResult {
        let! fastForwardExit = mergeOnBranch request [ "--ff-only"; "--quiet"; revision ]

        if fastForwardExit = 0 then
            return BranchSyncOutcome.FastForwarded
        else
            // A refused fast-forward leaves the worktree untouched, so a real merge can still follow
            // it. `--no-edit` keeps git from opening an editor in a background server process.
            let! mergeExit = mergeOnBranch request [ "--no-edit"; "--quiet"; revision ]

            if mergeExit = 0 then
                return BranchSyncOutcome.Merged
            else
                let! failure = failedMergeOutcome request.WorktreePath
                return! Error failure
    }

/// Treemon's own bounded sync of a worktree onto its base branch, for when no coding session is open
/// to do it. It merges only a worktree proven clean, fetches that worktree's own base rather than
/// the repo root's (`fetchUpstream` also fast-forwards the base worktree, an unrelated side effect),
/// prefers a fast-forward over a merge commit, aborts a conflict it created, and confirms the fetched
/// revision actually reached `HEAD` instead of trusting an exit code.
let syncWithBase (request: BranchSyncRequest) =
    async {
        let worktreePath = request.WorktreePath

        let! result =
            asyncResult {
                let! localContent = GitWorktree.localComparisonContent worktreePath

                // An unreadable working tree is not an empty one, so a probe that could not answer
                // fails to the agent path rather than merging over work it cannot see.
                do!
                    match localContent with
                    | GitWorktree.Clean -> Ok()
                    | GitWorktree.HasContent -> Error BranchSyncOutcome.RefusedDirty
                    | GitWorktree.Undetermined -> Error BranchSyncOutcome.CommandFailed

                let! fetchExit =
                    branchSyncExitCode
                        worktreePath
                        [ "fetch"; "--quiet"; request.UpstreamRemote; "--"; request.BaseBranch ]

                if fetchExit <> 0 then
                    return! Error BranchSyncOutcome.CommandFailed

                let! baseRevision = fetchedBaseRevision worktreePath
                let! alreadyCurrent = headContains worktreePath baseRevision

                if alreadyCurrent then
                    return BranchSyncOutcome.AlreadyCurrent
                else
                    let! outcome = mergeFetchedBase request baseRevision
                    let! verified = headContains worktreePath baseRevision

                    if verified then
                        return outcome
                    else
                        return! Error BranchSyncOutcome.CommandFailed
            }

        let outcome =
            match result with
            | Ok outcome
            | Error outcome -> outcome

        Log.log "Git" $"Branch sync of {worktreePath}: {outcome}"
        return outcome
    }

/// What Treemon's own push of a just-synced branch did. A refusal (no configured upstream) and a
/// failure (authentication, a diverged remote, a broken command) mean the same thing to the caller —
/// Treemon could not finish the sync mechanically, so an agent takes over. Neither may carry Git text
/// into a prompt (see `docs/spec/worktree-monitor.md`, Branch Sync).
[<RequireQualifiedAccess>]
type BranchPushOutcome =
    | Pushed
    /// The worktree is not on the branch this push was authorized for, a detached `HEAD` included.
    | BranchChanged
    | PushFailed

let private runBranchPushGit worktreePath arguments =
    runBranchGit worktreePath arguments
    |> AsyncResult.mapError (fun _ -> BranchPushOutcome.PushFailed)

/// A command whose single-token answer the push needs. An empty answer is a failure rather than a
/// value to push with, so an unconfigured or unreadable setting can never become part of a refspec.
let private branchPushValue worktreePath arguments =
    asyncResult {
        let! output = runBranchPushGit worktreePath arguments
        let value = singleLineStdout output

        if output.ExitCode <> 0 || value = "" then
            return! Error BranchPushOutcome.PushFailed
        else
            return value
    }

/// Where the branch has to go, taken from the two config keys git itself writes for a tracking
/// branch. Reading `remote` and `merge` separately avoids splitting a combined `origin/feature` at a
/// slash, which guesses wrong for any branch name containing one. The remote is the one argument the
/// push `--` cannot cover, since it has to precede it, so a value git would read as an option rather
/// than as a destination is refused before the command is built.
let private configuredUpstreamTarget worktreePath branch =
    asyncResult {
        let! remote = branchPushValue worktreePath [ "config"; "--get"; $"branch.{branch}.remote" ]

        if remote.StartsWith("-", StringComparison.Ordinal) then
            return! Error BranchPushOutcome.PushFailed

        let! mergeRef = branchPushValue worktreePath [ "config"; "--get"; $"branch.{branch}.merge" ]

        let remoteRef =
            if mergeRef.StartsWith("refs/", StringComparison.Ordinal) then
                mergeRef
            else
                $"refs/heads/{mergeRef}"

        return remote, remoteRef
    }

/// Publishes the branch a mechanical sync just updated. The branch is named by the caller and
/// re-read from the tree here, so a worktree that moved on stops before publishing anything. Both
/// halves of the refspec are spelled out, so neither `push.default`, nor `HEAD`, nor a remote-side
/// default picks what moves. There is deliberately no `--force` and no `--force-with-lease`: a remote
/// that has moved on is exactly the case Treemon must not resolve by itself, so the push fails with
/// both sides untouched and the caller hands the worktree to an agent.
let pushSyncedBranch (worktreePath: string) (branch: string) =
    async {
        let! result =
            asyncResult {
                do! ensureOnBranch BranchPushOutcome.BranchChanged worktreePath branch
                let! remote, remoteRef = configuredUpstreamTarget worktreePath branch

                let! output =
                    runBranchPushGit
                        worktreePath
                        [ "push"; "--quiet"; remote; "--"; $"refs/heads/{branch}:{remoteRef}" ]

                if output.ExitCode = 0 then
                    return BranchPushOutcome.Pushed
                else
                    return! Error BranchPushOutcome.PushFailed
            }

        let outcome =
            match result with
            | Ok outcome
            | Error outcome -> outcome

        Log.log "Git" $"Branch push of {worktreePath}: {outcome}"
        return outcome
    }
