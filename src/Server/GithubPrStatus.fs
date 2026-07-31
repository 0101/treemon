module Server.GithubPrStatus

open System
open System.Text.Json
open System.Text.RegularExpressions
open Shared
open Server.JsonHelpers

type GithubRemote = { Owner: string; Repo: string }

let parseGithubUrl (url: string) =
    let httpsPattern = @"https?://github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$"
    let sshPattern = @"git@github\.com:([^/]+)/([^/]+?)(?:\.git)?$"

    [ httpsPattern; sshPattern ]
    |> List.tryPick (fun pattern ->
        let m = Regex.Match(url, pattern)

        if m.Success then
            Some { Owner = m.Groups[1].Value; Repo = m.Groups[2].Value }
        else
            None)

let private gh =
    { ProcessRunner.Spawn.create "gh" with Context = "GH" }

let private runGh (arguments: string list) = ProcessRunner.text gh arguments

type internal ParsedGithubPr =
    { BranchName: string
      HeadSha: string option
      /// The login owning the head repository, absent when that repository is gone (a deleted fork).
      HeadOwner: string option
      PrNumber: int
      Title: string
      IsDraft: bool
      State: PrState
      AutoMergeEnabled: bool }

let internal parsePrList (json: string) =
    try
        use doc = JsonDocument.Parse(json)

        doc.RootElement.EnumerateArray()
        |> Seq.toList
        |> List.choose (fun el ->
                let number = el.GetProperty("number").GetInt32()
                let title = el.GetProperty("title").GetString()
                let isDraft = el |> tryBool "draft" |> Option.defaultValue false

                // `merged_at` outranks `state`: GitHub reports a merged pull request as closed.
                let state =
                    if el |> tryProp "merged_at" |> Option.isSome then PrState.Merged
                    elif el |> tryString "state" = Some "open" then PrState.Open
                    else PrState.ClosedUnmerged

                let head = el.GetProperty("head")
                let branchName = head.GetProperty("ref").GetString()
                Some
                    { BranchName = branchName
                      HeadSha = head |> tryString "sha"
                      HeadOwner =
                        head
                        |> tryProp "repo"
                        |> Option.bind (tryProp "owner")
                        |> Option.bind (tryString "login")
                      PrNumber = number
                      Title = title
                      IsDraft = isDraft
                      State = state
                      AutoMergeEnabled =
                        state <> PrState.Merged && (el |> tryProp "auto_merge" |> Option.isSome) })
    with ex ->
        Log.log "GH" $"Failed to parse GitHub PR list JSON: {ex.Message}"
        []

let internal parseReviewThreads (json: string) =
    try
        use doc = JsonDocument.Parse(json)

        let nodes =
            doc.RootElement
                .GetProperty("data")
                .GetProperty("repository")
                .GetProperty("pullRequest")
                .GetProperty("reviewThreads")
                .GetProperty("nodes")
                .EnumerateArray()
            |> Seq.toList

        let unresolved =
            nodes
            |> List.sumBy (fun node ->
                if node.GetProperty("isResolved").GetBoolean() then 0 else 1)

        WithResolution(unresolved, nodes.Length)
    with ex ->
        Log.log "GH" $"Failed to parse GitHub review threads JSON: {ex.Message}"
        WithResolution(0, 0)

let private fetchPrThreadCounts (remote: GithubRemote) (prNumber: int) =
    async {
        let query =
            $"""{{ repository(owner: "{remote.Owner}", name: "{remote.Repo}") {{ pullRequest(number: {prNumber}) {{ reviewThreads(first: 100) {{ nodes {{ isResolved }} }} }} }} }}"""

        let! output = runGh [ "api"; "graphql"; "-f"; $"query={query}" ]
        return output |> Option.map parseReviewThreads |> Option.defaultValue (WithResolution(0, 0))
    }

let private mapConclusion (conclusion: string option) =
    match conclusion with
    | Some "success" -> Some BuildStatus.Succeeded
    | Some "failure" -> Some BuildStatus.Failed
    | Some "cancelled" -> Some BuildStatus.Canceled
    | None -> Some BuildStatus.Building
    | Some _ -> None

let internal parseActionRuns (json: string) =
    try
        use doc = JsonDocument.Parse(json)

        doc.RootElement.GetProperty("workflow_runs").EnumerateArray()
        |> Seq.toList
        |> List.choose (fun run ->
            let status = run.GetProperty("status").GetString()

            let conclusion =
                if status = "completed" then
                    run |> tryString "conclusion"
                else
                    None

            let name =
                run |> tryString "name" |> Option.defaultValue "Workflow"

            let runId = run |> tryInt64 "id"
            let htmlUrl = run |> tryString "html_url"

            mapConclusion conclusion
            |> Option.map (fun buildStatus ->
                { Name = name
                  Status = buildStatus
                  Url = htmlUrl
                  Failure = None },
                runId))
    with ex ->
        Log.log "GH" $"Failed to parse GitHub Actions runs JSON: {ex.Message}"
        []

let internal parseFailedJobs (json: string) =
    try
        use doc = JsonDocument.Parse(json)

        doc.RootElement.GetProperty("jobs").EnumerateArray()
        |> Seq.toList
        |> List.tryPick (fun job ->
            let conclusion =
                job |> tryString "conclusion"

            if conclusion = Some "failure" then
                job.GetProperty("steps").EnumerateArray()
                |> Seq.toList
                |> List.tryPick (fun step ->
                    let stepConclusion =
                        step |> tryString "conclusion"

                    if stepConclusion = Some "failure" then
                        step |> tryString "name"
                    else
                        None)
            else
                None)
    with ex ->
        Log.log "GH" $"Failed to parse GitHub Actions jobs JSON: {ex.Message}"
        None

let private fetchFailedStepName (remote: GithubRemote) (runId: int64) =
    async {
        let! output = runGh [ "api"; $"/repos/{remote.Owner}/{remote.Repo}/actions/runs/{runId}/jobs" ]

        return output |> Option.bind parseFailedJobs
    }

let internal parsePrMergeability (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let mergeable = doc.RootElement |> tryBool "mergeable"
        mergeable = Some false
    with ex ->
        Log.log "GH" $"Failed to parse GitHub PR mergeability JSON: {ex.Message}"
        false

let private fetchMergeability (remote: GithubRemote) (prNumber: int) =
    async {
        let! output = runGh [ "api"; $"/repos/{remote.Owner}/{remote.Repo}/pulls/{prNumber}" ]
        return output |> Option.map parsePrMergeability |> Option.defaultValue false
    }

let private fetchActionRuns (remote: GithubRemote) (branch: string) =
    async {
        let! output =
            runGh
                [ "api"
                  $"/repos/{remote.Owner}/{remote.Repo}/actions/runs?branch={Uri.EscapeDataString(branch)}&per_page=10" ]

        let runs =
            output
            |> Option.map parseActionRuns
            |> Option.defaultValue []

        let uniqueByName =
            runs
            |> List.distinctBy (fun (info, _) -> info.Name)

        let! enriched =
            uniqueByName
            |> List.map (fun (info, runId) ->
                match info.Status, runId with
                | BuildStatus.Failed, Some id ->
                    async {
                        let! stepName = fetchFailedStepName remote id

                        return
                            { info with
                                Failure =
                                    stepName
                                    |> Option.map (fun name -> { StepName = name; Log = "" }) }
                    }
                | _ -> async { return info })
            |> Async.Parallel

        return enriched |> Array.toList
    }

/// Expects the fetch order produced by `fetchGithubPrStatuses`: open PRs first, then closed PRs
/// sorted by most recently updated. `distinctBy` keeps the first occurrence, so an open PR wins and
/// otherwise the newest closed PR does — sorting by `State` instead would promote an old
/// closed-unmerged PR above a newer merged one for a reused branch name and mask the merge.
let internal firstPerBranch (prs: ParsedGithubPr list) =
    prs |> List.distinctBy _.BranchName

/// A pull request only speaks for a local branch if its head lives in a repository this checkout
/// could itself push to. GitHub matches pull requests by head *ref*, so an outsider's fork PR shares
/// the bare branch name of any local branch and would otherwise decide that branch's auto-sync
/// eligibility and push gate. Owners come from the configured remotes, which is what keeps the fork
/// workflow working; GitHub logins are case-insensitive, so the comparison is too. A head whose
/// repository is gone resolves to no owner and is excluded rather than trusted.
let internal fromKnownOwners (headOwners: Set<string>) (prs: ParsedGithubPr list) =
    prs
    |> List.filter (fun pr ->
        pr.HeadOwner
        |> Option.map (fun owner -> owner.ToLowerInvariant())
        |> Option.exists (fun owner -> Set.contains owner headOwners))

let internal filterRelevantPrs (knownBranches: Set<string>) (prs: ParsedGithubPr list) =
    prs
    |> firstPerBranch
    |> List.filter (fun pr -> Set.contains pr.BranchName knownBranches)

let private fetchPrList (remote: GithubRemote) (state: string) (extraParams: string) =
    async {
        let! output =
            runGh [ "api"; $"/repos/{remote.Owner}/{remote.Repo}/pulls?state={state}{extraParams}" ]

        return
            output
            |> Option.map parsePrList
            |> Option.defaultValue []
    }

let fetchGithubPrStatuses (remote: GithubRemote) (headOwners: Set<string>) (knownBranches: Set<string>) =
    async {
        let! openChild = Async.StartChild(fetchPrList remote "open" "")
        let! closedChild = Async.StartChild(fetchPrList remote "closed" "&sort=updated&direction=desc&per_page=30")
        let! openPrs = openChild
        let! closedPrs = closedChild

        let allPrs = (openPrs @ closedPrs) |> fromKnownOwners headOwners

        match allPrs with
        | [] -> return Map.empty
        | _ ->
            let relevant = filterRelevantPrs knownBranches allPrs

            Log.log "GH" $"PRs: {List.length allPrs} fetched, {List.length relevant} relevant to worktrees"

            let! entries =
                relevant
                |> List.map (fun pr ->
                    async {
                        let! builds, hasConflicts, threadCounts =
                            if pr.State = PrState.Merged then
                                async { return [], false, WithResolution(0, 0) }
                            else
                                async {
                                    let! buildsChild = Async.StartChild(fetchActionRuns remote pr.BranchName)
                                    let! mergeabilityChild = Async.StartChild(fetchMergeability remote pr.PrNumber)
                                    let! threadsChild = Async.StartChild(fetchPrThreadCounts remote pr.PrNumber)
                                    let! b = buildsChild
                                    let! c = mergeabilityChild
                                    let! t = threadsChild
                                    return b, c, t
                                }

                        let url =
                            $"https://github.com/{remote.Owner}/{remote.Repo}/pull/{pr.PrNumber}"

                        return
                            pr.BranchName,
                            (HasPr
                                { Id = pr.PrNumber
                                  Title = pr.Title
                                  Url = url
                                  IsDraft = pr.IsDraft
                                  Comments = threadCounts
                                  Builds = builds
                                  State = pr.State
                                  AutoMergeEnabled = pr.AutoMergeEnabled
                                  HasConflicts = hasConflicts },
                             pr.HeadSha)
                    })
                |> Async.Parallel

            return Map entries
    }
