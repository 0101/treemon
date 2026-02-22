module Server.GithubPrStatus

open System
open System.Text.Json
open System.Text.RegularExpressions
open Shared

type GithubRemote = { Owner: string; Repo: string }

let parseGithubUrl (url: string) =
    let httpsPattern = @"https?://github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$"
    let sshPattern = @"git@github\.com:([^/]+)/([^/]+?)(?:\.git)?$"

    [ httpsPattern; sshPattern ]
    |> List.tryPick (fun pattern ->
        let m = Regex.Match(url, pattern)

        if m.Success then
            Some { Owner = m.Groups.[1].Value; Repo = m.Groups.[2].Value }
        else
            None)

let private tryProp (name: string) (el: JsonElement) =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
    | _ -> None

let private runGh (arguments: string) =
    ProcessRunner.run "GH" "gh" arguments

type internal ParsedGithubPr =
    { BranchName: string
      PrNumber: int
      Title: string
      IsDraft: bool
      IsMerged: bool
      CommentCount: int
      ReviewCommentCount: int }

let internal parsePrList (json: string) =
    try
        use doc = JsonDocument.Parse(json)

        doc.RootElement.EnumerateArray()
        |> Seq.toList
        |> List.choose (fun el ->
            try
                let number = el.GetProperty("number").GetInt32()
                let title = el.GetProperty("title").GetString()
                let isDraft = el |> tryProp "draft" |> Option.map (fun v -> v.GetBoolean()) |> Option.defaultValue false
                let isMerged = el |> tryProp "merged_at" |> Option.isSome
                let branchName = el.GetProperty("head").GetProperty("ref").GetString()
                let comments = el |> tryProp "comments" |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                let reviewComments = el |> tryProp "review_comments" |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0

                Some
                    { BranchName = branchName
                      PrNumber = number
                      Title = title
                      IsDraft = isDraft
                      IsMerged = isMerged
                      CommentCount = comments
                      ReviewCommentCount = reviewComments }
            with ex ->
                Log.log "GH" $"Failed to parse GitHub PR entry: {ex.Message}"
                None)
    with ex ->
        Log.log "GH" $"Failed to parse GitHub PR list JSON: {ex.Message}"
        []

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
                    run |> tryProp "conclusion" |> Option.map (fun v -> v.GetString())
                else
                    None

            let name =
                run |> tryProp "name" |> Option.map (fun v -> v.GetString()) |> Option.defaultValue "Workflow"

            let runId = run |> tryProp "id" |> Option.map (fun v -> v.GetInt64())
            let htmlUrl = run |> tryProp "html_url" |> Option.map (fun v -> v.GetString())

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
                job |> tryProp "conclusion" |> Option.map (fun v -> v.GetString())

            if conclusion = Some "failure" then
                job.GetProperty("steps").EnumerateArray()
                |> Seq.toList
                |> List.tryPick (fun step ->
                    let stepConclusion =
                        step |> tryProp "conclusion" |> Option.map (fun v -> v.GetString())

                    if stepConclusion = Some "failure" then
                        step |> tryProp "name" |> Option.map (fun v -> v.GetString())
                    else
                        None)
            else
                None)
    with ex ->
        Log.log "GH" $"Failed to parse GitHub Actions jobs JSON: {ex.Message}"
        None

let private fetchFailedStepName (remote: GithubRemote) (runId: int64) =
    async {
        let! output = runGh $"api /repos/{remote.Owner}/{remote.Repo}/actions/runs/{runId}/jobs"

        return output |> Option.bind parseFailedJobs
    }

let private fetchActionRuns (remote: GithubRemote) (branch: string) =
    async {
        let! output =
            runGh $"api \"/repos/{remote.Owner}/{remote.Repo}/actions/runs?branch={Uri.EscapeDataString(branch)}&per_page=10\""

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

let internal firstPerBranch (prs: ParsedGithubPr list) =
    prs
    |> List.sortBy (fun pr -> pr.IsMerged)
    |> List.distinctBy (fun pr -> pr.BranchName)

let internal filterRelevantPrs (knownBranches: Set<string>) (prs: ParsedGithubPr list) =
    prs
    |> firstPerBranch
    |> List.filter (fun pr -> Set.contains pr.BranchName knownBranches)

let private fetchPrList (remote: GithubRemote) (state: string) (extraParams: string) =
    async {
        let! output =
            runGh $"api \"/repos/{remote.Owner}/{remote.Repo}/pulls?state={state}{extraParams}\""

        return
            output
            |> Option.map parsePrList
            |> Option.defaultValue []
    }

let fetchGithubPrStatuses (remote: GithubRemote) (knownBranches: Set<string>) =
    async {
        let! openChild = Async.StartChild(fetchPrList remote "open" "")
        let! closedChild = Async.StartChild(fetchPrList remote "closed" "&sort=updated&direction=desc&per_page=10")
        let! openPrs = openChild
        let! closedPrs = closedChild

        let allPrs = openPrs @ closedPrs

        match allPrs with
        | [] -> return Map.empty
        | _ ->
            let relevant = filterRelevantPrs knownBranches allPrs

            Log.log "GH" $"PRs: {List.length allPrs} fetched, {List.length relevant} relevant to worktrees"

            let! entries =
                relevant
                |> List.map (fun pr ->
                    async {
                        let! builds =
                            if pr.IsMerged then
                                async { return [] }
                            else
                                fetchActionRuns remote pr.BranchName

                        let commentCount = pr.CommentCount + pr.ReviewCommentCount

                        let url =
                            $"https://github.com/{remote.Owner}/{remote.Repo}/pull/{pr.PrNumber}"

                        return
                            pr.BranchName,
                            HasPr
                                { Id = pr.PrNumber
                                  Title = pr.Title
                                  Url = url
                                  IsDraft = pr.IsDraft
                                  Comments = CountOnly commentCount
                                  Builds = builds
                                  IsMerged = pr.IsMerged }
                    })
                |> Async.Parallel

            return entries |> Array.toList |> Map.ofList
    }
