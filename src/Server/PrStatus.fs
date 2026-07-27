module Server.PrStatus

open System
open System.IO
open System.Text.Json
open FsToolkit.ErrorHandling
open Shared
open Server.JsonHelpers
open Server.PrOpenState

type AzDoRemote =
    { Org: string
      Project: string
      Repo: string }

type RemoteInfo =
    | AzureDevOps of AzDoRemote
    | GitHub of GithubPrStatus.GithubRemote

let parseAzureDevOpsUrl (url: string) =
    try
        let parts = url.TrimEnd('/').Split('/')

        if url.Contains("dev.azure.com") then
            if url.StartsWith("git@") then
                Some
                    { Org = parts[parts.Length - 3]
                      Project = parts[parts.Length - 2]
                      Repo = parts[parts.Length - 1].Replace(".git", "") }
            else
                Some
                    { Org = parts[parts.Length - 4]
                      Project = parts[parts.Length - 3]
                      Repo = parts[parts.Length - 1].Replace(".git", "") }
        elif url.Contains("visualstudio.com") then
            let org = (url.Split("//")[1]).Split('.')[0]
            let repo = parts[parts.Length - 1].Replace(".git", "")
            let gitIdx = parts |> Array.findIndex ((=) "_git")
            let project = parts[gitIdx - 1]
            Some { Org = org; Project = project; Repo = repo }
        else
            None
    with ex ->
        Log.log "PR" $"Failed to parse Azure DevOps URL '{url}': {ex.Message}"
        None

let detectProvider (url: string) =
    parseAzureDevOpsUrl url
    |> Option.map AzureDevOps
    |> Option.orElseWith (fun () ->
        GithubPrStatus.parseGithubUrl url
        |> Option.map GitHub)

let toRepoProvider = function
    | AzureDevOps r -> AzDoProvider $"https://dev.azure.com/{r.Org}/{r.Project}/_git/{r.Repo}"
    | GitHub r -> GitHubProvider $"https://github.com/{r.Owner}/{r.Repo}"

let private azPythonExe =
    lazy
        let azCmd =
            (Environment.GetEnvironmentVariable("PATH") |> Option.ofObj |> Option.defaultValue "").Split(Path.PathSeparator)
            |> Array.tryPick (fun dir ->
                let candidate = Path.Combine(dir, "az.cmd")
                if File.Exists(candidate) then Some candidate else None)

        azCmd
        |> Option.bind (fun cmd ->
            let python = Path.Combine(Path.GetDirectoryName(cmd), "..", "python.exe") |> Path.GetFullPath
            if File.Exists(python) then Some python else None)

let private runAz (arguments: string) =
    match azPythonExe.Value with
    | Some python ->
        ProcessRunner.run "PR" python $"-IBm azure.cli {arguments}"
    | None ->
        Log.log "PR" "Could not locate Azure CLI python.exe via PATH"
        async { return None }

let buildRemoteUrlArgs (repoRoot: string) (remoteName: string) =
    $"""-C "{repoRoot}" remote get-url {remoteName}"""

let getRemoteUrl (repoRoot: string) (remoteName: string) =
    ProcessRunner.run "PR" "git" (buildRemoteUrlArgs repoRoot remoteName)

type internal ParsedPr =
    { BranchName: string
      HeadSha: string option
      PrId: int
      Title: string
      IsDraft: bool
      IsMerged: bool
      HasConflicts: bool
      ClosedDate: DateTimeOffset option }

/// Azure DevOps names branches by full ref; every branch Treemon knows is a short name.
let private branchFromRef (sourceRef: string) =
    if sourceRef.StartsWith("refs/heads/") then
        sourceRef["refs/heads/".Length..]
    else
        sourceRef

let internal parsePrList (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let prElements = doc.RootElement.EnumerateArray() |> Seq.toList

        let repoGuid =
            prElements
            |> List.tryHead
            |> Option.bind (tryProp "repository")
            |> Option.bind (tryString "id")

        let prs =
            prElements
            |> List.choose (fun el ->
                try
                    let prId = el.GetProperty("pullRequestId").GetInt32()
                    let title = el.GetProperty("title").GetString()
                    let isDraft = el.GetProperty("isDraft").GetBoolean()

                    let sourceRef = el.GetProperty("sourceRefName").GetString()
                    let branchName = branchFromRef sourceRef

                    let status = el.GetProperty("status").GetString()
                    let isMerged = status = "completed"

                    let closedDate =
                        el
                        |> tryProp "closedDate"
                        |> Option.bind (fun v ->
                            match DateTimeOffset.TryParse(v.GetString()) with
                            | true, dt -> Some dt
                            | _ -> None)

                    let hasConflicts =
                        el |> tryString "mergeStatus" = Some "conflicts"

                    let headSha =
                        el
                        |> tryProp "lastMergeSourceCommit"
                        |> Option.bind (tryString "commitId")

                    Some
                        { BranchName = branchName
                          HeadSha = headSha
                          PrId = prId
                          Title = title
                          IsDraft = isDraft
                          IsMerged = isMerged
                          HasConflicts = hasConflicts
                          ClosedDate = closedDate }
                with ex ->
                    Log.log "PR" $"Failed to parse PR entry: {ex.Message}"
                    None)

        repoGuid, prs
    with ex ->
        Log.log "PR" $"Failed to parse PR list JSON: {ex.Message}"
        None, []

let internal parseThreadCounts (json: string) =
    try
        use doc = JsonDocument.Parse(json)

        let threads =
            doc.RootElement.GetProperty("value").EnumerateArray()
            |> Seq.filter (fun thread ->
                let isDeleted =
                    thread |> tryBool "isDeleted" |> Option.defaultValue false

                let hasStatus = (thread |> tryProp "status").IsSome

                not isDeleted && hasStatus)
            |> Seq.toList

        let unresolved =
            threads
            |> List.filter (fun thread ->
                match thread.GetProperty("status").GetString() with
                | "active"
                | "pending" -> true
                | _ -> false)
            |> List.length

        WithResolution(unresolved, threads.Length)
    with ex ->
        Log.log "PR" $"Failed to parse thread list JSON: {ex.Message}"
        WithResolution(0, 0)

let private parseBuildRun (run: JsonElement) =
    let status = run.GetProperty("status").GetString()

    match status with
    | "inProgress" -> Some BuildStatus.Building
    | "completed" ->
        run
        |> tryProp "result"
        |> Option.bind (fun result ->
            match result.GetString() with
            | "succeeded" -> Some BuildStatus.Succeeded
            | "failed" -> Some BuildStatus.Failed
            | "partiallySucceeded" -> Some BuildStatus.PartiallySucceeded
            | "canceled" -> Some BuildStatus.Canceled
            | _ -> None)
    | _ -> None

let private parseBuildInfo (remote: AzDoRemote) (run: JsonElement) =
    let definition = run |> tryProp "definition"

    let name =
        definition
        |> Option.bind (tryString "name")
        |> Option.defaultValue "Unknown"

    let definitionId =
        definition
        |> Option.bind (tryInt "id")

    let buildId =
        run |> tryInt "id"

    let url =
        buildId
        |> Option.map (fun id ->
            $"https://dev.azure.com/{remote.Org}/{remote.Project}/_build/results?buildId={id}")

    parseBuildRun run
    |> Option.map (fun buildStatus ->
        let info =
            { Name = name
              Status = buildStatus
              Url = url
              Failure = None }

        info, definitionId, buildId)

let internal parseBuilds (remote: AzDoRemote) (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let runs = doc.RootElement.GetProperty("value").EnumerateArray() |> Seq.toList

        runs
        |> List.choose (parseBuildInfo remote)
        |> List.choose (fun (info, defId, buildId) ->
            defId |> Option.map (fun defId -> defId, (info, buildId)))
        |> List.distinctBy fst
        |> List.map snd
    with ex ->
        Log.log "PR" $"Failed to parse build status JSON: {ex.Message}"
        []

let internal parseFailedStep (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let records = doc.RootElement.GetProperty("records").EnumerateArray() |> Seq.toList

        records
        |> List.tryFind (fun r ->
            let isTask =
                r |> tryString "type" |> Option.map ((=) "Task") |> Option.defaultValue false
            let isFailed =
                r |> tryString "result" |> Option.map ((=) "failed") |> Option.defaultValue false
            isTask && isFailed)
        |> Option.bind (fun r ->
            let name =
                r |> tryString "name" |> Option.defaultValue "Unknown step"
            let logId =
                r |> tryProp "log" |> Option.bind (tryInt "id")
            logId |> Option.map (fun id -> name, id))
    with ex ->
        Log.log "PR" $"Failed to parse build timeline: {ex.Message}"
        None

let internal parseBuildLog (json: string) =
    try
        use doc = JsonDocument.Parse(json)

        let lines =
            doc.RootElement.GetProperty("value").EnumerateArray()
            |> Seq.map _.GetString()
            |> Seq.toList

        let trimmedLines =
            lines
            |> List.map (fun line ->
                let spaceIdx = line.IndexOf(" ")
                if spaceIdx > 20 then line[spaceIdx + 1..] else line)

        let tail =
            let start = max 0 (trimmedLines.Length - 50)
            trimmedLines[start..]

        Some(String.concat Environment.NewLine tail)
    with ex ->
        Log.log "PR" $"Failed to parse build log: {ex.Message}"
        None

let private fetchBuildFailure (remote: AzDoRemote) (buildId: int) =
    async {
        let timelineArgs =
            $"devops invoke --area build --resource timeline --route-parameters project={remote.Project} buildId={buildId} --org https://dev.azure.com/{remote.Org} --api-version 7.1 -o json"

        let! timelineOutput = runAz timelineArgs

        match timelineOutput |> Option.bind parseFailedStep with
        | None -> return None
        | Some(stepName, logId) ->
            let logArgs =
                $"devops invoke --area build --resource logs --route-parameters project={remote.Project} buildId={buildId} logId={logId} --org https://dev.azure.com/{remote.Org} --api-version 7.1 -o json"

            let! logOutput = runAz logArgs

            let logText =
                logOutput
                |> Option.bind parseBuildLog
                |> Option.defaultValue ""

            return
                Some
                    { StepName = stepName
                      Log = logText }
    }

let private fetchPrThreadCount (remote: AzDoRemote) (prId: int) =
    async {
        let args =
            $"devops invoke --area git --resource pullRequestThreads --route-parameters project={remote.Project} repositoryId={remote.Repo} pullRequestId={prId} --org https://dev.azure.com/{remote.Org} --api-version 7.1 -o json"

        let! output = runAz args

        return
            output
            |> Option.map parseThreadCounts
            |> Option.defaultValue (WithResolution(0, 0))
    }

let private fetchBuildStatus (remote: AzDoRemote) (repoGuid: string) (prId: int) =
    async {
        let args =
            $"devops invoke --area build --resource builds --route-parameters project={remote.Project} --query-parameters \"repositoryId={repoGuid}&repositoryType=TfsGit&branchName=refs/pull/{prId}/merge&queryOrder=queueTimeDescending&$top=10\" --org https://dev.azure.com/{remote.Org} --api-version 7.1 -o json"

        let! output = runAz args

        let builds =
            output
            |> Option.map (parseBuilds remote)
            |> Option.defaultValue []

        let! enriched =
            builds
            |> List.map (fun (build, buildId) ->
                match build.Status, buildId with
                | BuildStatus.Failed, Some id ->
                    async {
                        let! failure = fetchBuildFailure remote id
                        return { build with Failure = failure }
                    }
                | _ -> async { return build })
            |> Async.Parallel

        return enriched |> Array.toList
    }

let internal firstPerBranch (prs: ParsedPr list) =
    prs
    |> List.sortBy (fun pr ->
        (pr.IsMerged, pr.ClosedDate |> Option.map (fun d -> -d.Ticks) |> Option.defaultValue Int64.MaxValue))
    |> List.distinctBy _.BranchName

let internal filterRelevantPrs (knownBranches: Set<string>) (prs: ParsedPr list) =
    prs
    |> firstPerBranch
    |> List.filter (fun pr -> Set.contains pr.BranchName knownBranches)

let private fetchPrList (remote: AzDoRemote) (status: string) (top: int option) =
    async {
        let topArg = top |> Option.map (fun n -> $" --top {n}") |> Option.defaultValue ""
        let args =
            $"repos pr list --org https://dev.azure.com/{remote.Org} --project \"{remote.Project}\" --repository \"{remote.Repo}\" --status {status}{topArg} -o json"

        let! output = runAz args
        return
            output
            |> Option.map parsePrList
            |> Option.defaultValue (None, [])
    }

let fetchPrStatuses (remote: AzDoRemote) (knownBranches: Set<string>) =
    async {
        let! activeChild = Async.StartChild(fetchPrList remote "active" None)
        let! completedChild = Async.StartChild(fetchPrList remote "completed" (Some 50))
        let! activeGuid, activePrs = activeChild
        let! completedGuid, completedPrs = completedChild

        let allPrs = activePrs @ completedPrs
        let repoGuid = activeGuid |> Option.orElse completedGuid

        match allPrs with
        | [] -> return Map.empty
        | _ ->
            let relevant = filterRelevantPrs knownBranches allPrs

            Log.log "PR" $"PRs: {List.length allPrs} fetched, {List.length relevant} relevant to worktrees"

            if repoGuid.IsNone then
                Log.log "PR" "No repository GUID found in PR list, builds will be empty"

            let! entries =
                relevant
                |> List.map (fun pr ->
                    async {
                        let! threadCounts, builds =
                            if pr.IsMerged then
                                async { return WithResolution(0, 0), [] }
                            else
                                async {
                                    let! tcChild = Async.StartChild(fetchPrThreadCount remote pr.PrId)
                                    let! bsChild =
                                        Async.StartChild(
                                            match repoGuid with
                                            | Some guid -> fetchBuildStatus remote guid pr.PrId
                                            | None -> async { return [] })
                                    let! tc = tcChild
                                    let! bs = bsChild
                                    return tc, bs
                                }

                        let url =
                            $"https://dev.azure.com/{remote.Org}/{remote.Project}/_git/{remote.Repo}/pullrequest/{pr.PrId}"

                        return
                            pr.BranchName,
                            (HasPr
                                { Id = pr.PrId
                                  Title = pr.Title
                                  Url = url
                                  IsDraft = pr.IsDraft
                                  Comments = threadCounts
                                  Builds = builds
                                  IsMerged = pr.IsMerged
                                  HasConflicts = pr.HasConflicts },
                             pr.HeadSha)
                    })
                |> Async.Parallel

            return Map entries
    }

let fetchPrStatusesByRepoRoot (repoRoot: string) (upstreamRemote: string) (knownBranches: Set<string>) =
    async {
        let! remoteUrl = getRemoteUrl repoRoot upstreamRemote

        let provider =
            remoteUrl |> Option.bind detectProvider

        match provider with
        | Some(AzureDevOps remote) -> return! fetchPrStatuses remote knownBranches
        | Some(GitHub remote) -> return! GithubPrStatus.fetchGithubPrStatuses remote knownBranches
        | None -> return Map.empty
    }

/// Asks Azure DevOps only whether this branch currently sources an active pull request - no threads,
/// builds, or completed pull requests - because the push decision needs presence and nothing else.
/// `--source-branch` makes the service do the filtering, so a response holding any other branch means
/// the filter did not apply and the state stays unknown.
let internal openPrQueryArgs (remote: AzDoRemote) (branch: string) =
    [ "repos"
      "pr"
      "list"
      "--org"
      $"https://dev.azure.com/{remote.Org}"
      "--project"
      remote.Project
      "--repository"
      remote.Repo
      "--status"
      "active"
      "--source-branch"
      $"refs/heads/{branch}"
      "--top"
      "1"
      "-o"
      "json" ]

/// Reads the branch a pull request is merging *from*, as the short name Treemon knows it by.
let private sourceBranch (pr: JsonElement) =
    pr |> tryStringValue "sourceRefName" |> Option.map branchFromRef

let internal parseOpenPrState (branch: string) (json: string) =
    classifyResponse "PR" sourceBranch branch json

let queryOpenPrState (remote: AzDoRemote) (branch: string) =
    async {
        match azPythonExe.Value with
        | None ->
            Log.log "PR" "Open PR lookup could not locate Azure CLI python.exe via PATH"
            return UnknownPrState
        | Some python ->
            match! runQuery "PR" python ("-IBm" :: "azure.cli" :: openPrQueryArgs remote branch) with
            | Ok response -> return parseOpenPrState branch response
            | Error state -> return state
    }

/// Git's own record of where a branch is published: the remote it tracks, which is also the remote
/// `GitWorktree.pushSyncedBranch` sends the branch to.
let internal branchRemoteArgs (repoRoot: string) (branch: string) =
    [ "-C"; repoRoot; "config"; "--get"; $"branch.{branch}.remote" ]

/// Where a push to that remote actually lands. A remote may point `remote.<name>.pushurl` at a fork
/// while `remote.<name>.url` still reads the upstream repository, and `git push` follows the push URL,
/// so `--push` - which falls back to the fetch URL when no push URL is set - is the only reading that
/// matches the destination the branch is published to.
let internal remotePushUrlArgs (repoRoot: string) (remoteName: string) =
    [ "-C"; repoRoot; "remote"; "get-url"; "--push"; remoteName ]

/// One git setting the open-PR lookup needs. Read through an argument list rather than `getRemoteUrl`'s
/// command string, because both the branch and the remote name it yields are repository-controlled
/// text that may legally hold characters a command string would re-parse, and because this shares the
/// bounded runner the rest of the lookup uses. A missing key exits non-zero, so it fails here.
let private readGitValue (arguments: string list) =
    runQuery "PR" "git" arguments |> AsyncResult.map _.Trim()

/// The account whose repository holds the branch a pull request is merging *from*. GitHub filters
/// open pull requests by `owner:branch`, and in a fork workflow that owner is the fork rather than
/// the repository the pull request targets - filtering under the target's owner answers an empty
/// list, which is a confirmed absence, so a fork-origin pull request would silently lose its push.
/// The owner therefore comes from git's own record of where this branch lives, resolved the same way
/// the push resolves its destination: the branch's remote, then that remote's *effective push* URL,
/// which a fork workflow may set away from the fetch URL. A branch git records no remote for, or a
/// remote with no GitHub push destination, cannot be located - and could not be pushed either - so it
/// stays unknown rather than being guessed onto the upstream owner. Injectable so the fork case can
/// be exercised without a repository.
let internal resolveHeadOwnerWith
    (readValue: string list -> Async<Result<string, OpenPrState>>)
    (repoRoot: string)
    (branch: string)
    =
    asyncResult {
        let! remoteName = readValue (branchRemoteArgs repoRoot branch)
        let! pushUrl = readValue (remotePushUrlArgs repoRoot remoteName)

        return!
            match GithubPrStatus.parseGithubUrl pushUrl with
            | Some remote -> Ok remote.Owner
            | None ->
                Log.log "PR" "Open PR lookup could not read a GitHub owner from the branch remote's push URL"
                Error UnknownPrState
    }

let internal resolveHeadOwner repoRoot branch =
    resolveHeadOwnerWith readGitValue repoRoot branch

/// The live push decision for a mechanically synced branch. It reuses provider detection and the
/// configured upstream remote but never the dashboard's cached PR map: that map is eventually
/// consistent, and on GitHub a closed-unmerged pull request there is indistinguishable from an open
/// one. An unsupported remote is unknown, not an absence.
let queryOpenPrStateByRepoRoot (repoRoot: string) (upstreamRemote: string) (branch: string) =
    async {
        let! remoteUrl = getRemoteUrl repoRoot upstreamRemote

        match remoteUrl |> Option.bind detectProvider with
        | Some(AzureDevOps remote) -> return! queryOpenPrState remote branch
        | Some(GitHub remote) ->
            match! resolveHeadOwner repoRoot branch with
            | Ok headOwner -> return! GithubPrStatus.queryOpenPrState remote headOwner branch
            | Error state -> return state
        | None ->
            Log.log "PR" "Open PR lookup found no supported provider on the upstream remote"
            return UnknownPrState
    }

let lookupPrStatus (prMap: Map<string, PrStatus>) (branchName: string option) =
    branchName
    |> Option.bind (fun b -> prMap |> Map.tryFind b)
    |> Option.defaultValue NoPr
