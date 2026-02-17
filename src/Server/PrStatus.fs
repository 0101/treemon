module Server.PrStatus

open System
open System.IO
open System.Text.Json
open Shared

type AzDoRemote =
    { Org: string
      Project: string
      Repo: string }

let parseAzureDevOpsUrl (url: string) =
    try
        let parts = url.TrimEnd('/').Split('/')

        if url.Contains("dev.azure.com") then
            if url.StartsWith("git@") then
                Some
                    { Org = parts.[parts.Length - 3]
                      Project = parts.[parts.Length - 2]
                      Repo = parts.[parts.Length - 1].Replace(".git", "") }
            else
                Some
                    { Org = parts.[parts.Length - 4]
                      Project = parts.[parts.Length - 3]
                      Repo = parts.[parts.Length - 1].Replace(".git", "") }
        elif url.Contains("visualstudio.com") then
            let org = url.Split("//").[1].Split('.').[0]
            let repo = parts.[parts.Length - 1].Replace(".git", "")
            let gitIdx = parts |> Array.findIndex ((=) "_git")
            let project = parts.[gitIdx - 1]
            Some { Org = org; Project = project; Repo = repo }
        else
            Log.log "PR" $"URL not recognized as Azure DevOps: {url}"
            None
    with ex ->
        Log.log "PR" $"Failed to parse Azure DevOps URL '{url}': {ex.Message}"
        None

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

let getRemoteUrl (repoRoot: string) =
    ProcessRunner.run "PR" "git" $"""-C "{repoRoot}" remote get-url origin"""

type private ParsedPr =
    { BranchName: string
      PrId: int
      Title: string
      IsDraft: bool
      IsMerged: bool
      ClosedDate: DateTimeOffset option }

let private parsePrList (json: string) =
    try
        let doc = JsonDocument.Parse(json)
        let prElements = doc.RootElement.EnumerateArray() |> Seq.toList

        let repoGuid =
            prElements
            |> List.tryHead
            |> Option.bind (fun el ->
                match el.TryGetProperty("repository") with
                | true, repo ->
                    match repo.TryGetProperty("id") with
                    | true, id -> Some(id.GetString())
                    | _ -> None
                | _ -> None)

        let prs =
            prElements
            |> List.choose (fun el ->
                try
                    let prId = el.GetProperty("pullRequestId").GetInt32()
                    let title = el.GetProperty("title").GetString()
                    let isDraft = el.GetProperty("isDraft").GetBoolean()

                    let sourceRef = el.GetProperty("sourceRefName").GetString()

                    let branchName =
                        if sourceRef.StartsWith("refs/heads/") then
                            sourceRef.Substring("refs/heads/".Length)
                        else
                            sourceRef

                    let status = el.GetProperty("status").GetString()
                    let isMerged = status = "completed"

                    let closedDate =
                        match el.TryGetProperty("closedDate") with
                        | true, v when v.ValueKind <> JsonValueKind.Null ->
                            match DateTimeOffset.TryParse(v.GetString()) with
                            | true, dt -> Some dt
                            | _ -> None
                        | _ -> None

                    Some
                        { BranchName = branchName
                          PrId = prId
                          Title = title
                          IsDraft = isDraft
                          IsMerged = isMerged
                          ClosedDate = closedDate }
                with ex ->
                    Log.log "PR" $"Failed to parse PR entry: {ex.Message}"
                    None)

        repoGuid, prs
    with ex ->
        Log.log "PR" $"Failed to parse PR list JSON: {ex.Message}"
        None, []

let private parseThreadCounts (json: string) =
    try
        let doc = JsonDocument.Parse(json)

        let threads =
            doc.RootElement.GetProperty("value").EnumerateArray()
            |> Seq.filter (fun thread ->
                let isDeleted =
                    match thread.TryGetProperty("isDeleted") with
                    | true, v -> v.GetBoolean()
                    | _ -> false

                let hasStatus =
                    match thread.TryGetProperty("status") with
                    | true, _ -> true
                    | _ -> false

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

        { Unresolved = unresolved
          Total = threads.Length }
    with ex ->
        Log.log "PR" $"Failed to parse thread list JSON: {ex.Message}"
        { Unresolved = 0; Total = 0 }

let private parseBuildRun (run: JsonElement) =
    let status = run.GetProperty("status").GetString()

    match status with
    | "inProgress" -> Some BuildStatus.Building
    | "completed" ->
        match run.TryGetProperty("result") with
        | true, result ->
            match result.GetString() with
            | "succeeded" -> Some BuildStatus.Succeeded
            | "failed" -> Some BuildStatus.Failed
            | "partiallySucceeded" -> Some BuildStatus.PartiallySucceeded
            | "canceled" -> Some BuildStatus.Canceled
            | _ -> None
        | _ -> None
    | _ -> None

let private parseBuildInfo (remote: AzDoRemote) (run: JsonElement) =
    let name =
        match run.TryGetProperty("definition") with
        | true, def ->
            match def.TryGetProperty("name") with
            | true, n -> n.GetString()
            | _ -> "Unknown"
        | _ -> "Unknown"

    let definitionId =
        match run.TryGetProperty("definition") with
        | true, def ->
            match def.TryGetProperty("id") with
            | true, id -> Some(id.GetInt32())
            | _ -> None
        | _ -> None

    let buildId =
        match run.TryGetProperty("id") with
        | true, v -> Some(v.GetInt32())
        | _ -> None

    let url =
        buildId
        |> Option.map (fun id ->
            sprintf "https://dev.azure.com/%s/%s/_build/results?buildId=%d" remote.Org remote.Project id)

    parseBuildRun run
    |> Option.map (fun buildStatus ->
        let info =
            { Name = name
              Status = buildStatus
              Url = url
              Failure = None }

        info, definitionId, buildId)

let private parseBuilds (remote: AzDoRemote) (json: string) =
    try
        let doc = JsonDocument.Parse(json)
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

let private parseFailedStep (json: string) =
    try
        let doc = JsonDocument.Parse(json)
        let records = doc.RootElement.GetProperty("records").EnumerateArray() |> Seq.toList

        records
        |> List.tryFind (fun r ->
            let isTask =
                match r.TryGetProperty("type") with
                | true, v -> v.GetString() = "Task"
                | _ -> false
            let isFailed =
                match r.TryGetProperty("result") with
                | true, v -> v.GetString() = "failed"
                | _ -> false
            isTask && isFailed)
        |> Option.bind (fun r ->
            let name =
                match r.TryGetProperty("name") with
                | true, v -> v.GetString()
                | _ -> "Unknown step"
            let logId =
                match r.TryGetProperty("log") with
                | true, log ->
                    match log.TryGetProperty("id") with
                    | true, id -> Some(id.GetInt32())
                    | _ -> None
                | _ -> None
            logId |> Option.map (fun id -> name, id))
    with ex ->
        Log.log "PR" $"Failed to parse build timeline: {ex.Message}"
        None

let private parseBuildLog (json: string) =
    try
        let doc = JsonDocument.Parse(json)

        let lines =
            doc.RootElement.GetProperty("value").EnumerateArray()
            |> Seq.map (fun el -> el.GetString())
            |> Seq.toList

        let trimmedLines =
            lines
            |> List.map (fun line ->
                match line.IndexOf(" ") with
                | i when i > 20 -> line.Substring(i + 1)
                | _ -> line)

        let tail =
            trimmedLines
            |> List.rev
            |> List.truncate 50
            |> List.rev

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
            |> Option.defaultValue { Unresolved = 0; Total = 0 }
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
                async {
                    match build.Status, buildId with
                    | BuildStatus.Failed, Some id ->
                        let! failure = fetchBuildFailure remote id
                        return { build with Failure = failure }
                    | _ -> return build
                })
            |> Async.Parallel

        return enriched |> Array.toList
    }

let private firstPerBranch (prs: ParsedPr list) =
    prs
    |> List.sortBy (fun pr ->
        // Active PRs first (isMerged=false sorts before true), then most recently closed
        pr.IsMerged,
        pr.ClosedDate |> Option.defaultValue DateTimeOffset.MaxValue |> (fun d -> -d.Ticks))
    |> List.distinctBy (fun pr -> pr.BranchName)

let fetchPrStatuses (remote: AzDoRemote) =
    async {
        let args =
            $"repos pr list --org https://dev.azure.com/{remote.Org} --project \"{remote.Project}\" --repository \"{remote.Repo}\" --status all -o json"

        let! output = runAz args

        match output with
        | None -> return Map.empty
        | Some json ->
            let repoGuid, prs = parsePrList json
            let prsFiltered = firstPerBranch prs

            match repoGuid with
            | None ->
                Log.log "PR" "No repository GUID found in PR list, builds will be empty"
                let! entries =
                    prsFiltered
                    |> List.map (fun pr ->
                        async {
                            let! threadCounts =
                                match pr.IsMerged with
                                | true -> async { return { Unresolved = 0; Total = 0 } }
                                | false -> fetchPrThreadCount remote pr.PrId

                            let url =
                                sprintf "https://dev.azure.com/%s/%s/_git/%s/pullrequest/%d" remote.Org remote.Project remote.Repo pr.PrId

                            return
                                pr.BranchName,
                                HasPr
                                    { Id = pr.PrId
                                      Title = pr.Title
                                      Url = url
                                      IsDraft = pr.IsDraft
                                      ThreadCounts = threadCounts
                                      Builds = []
                                      IsMerged = pr.IsMerged }
                        })
                    |> Async.Parallel

                return entries |> Map.ofArray
            | Some guid ->
                let! entries =
                    prsFiltered
                    |> List.map (fun pr ->
                        async {
                            let! threadCounts, builds =
                                match pr.IsMerged with
                                | true ->
                                    async { return { Unresolved = 0; Total = 0 }, [] }
                                | false ->
                                    async {
                                        let! tc = fetchPrThreadCount remote pr.PrId
                                        let! bs = fetchBuildStatus remote guid pr.PrId
                                        return tc, bs
                                    }

                            let url =
                                sprintf "https://dev.azure.com/%s/%s/_git/%s/pullrequest/%d" remote.Org remote.Project remote.Repo pr.PrId

                            return
                                pr.BranchName,
                                HasPr
                                    { Id = pr.PrId
                                      Title = pr.Title
                                      Url = url
                                      IsDraft = pr.IsDraft
                                      ThreadCounts = threadCounts
                                      Builds = builds
                                      IsMerged = pr.IsMerged }
                        })
                    |> Async.Parallel

                return entries |> Map.ofArray
    }

module Cache =
    let private cache = Cache.TtlCache<Map<string, PrStatus>>(TimeSpan.FromSeconds(120.0))

    let getCachedPrStatuses (repoRoot: string) =
        cache.GetOrRefreshAsync repoRoot (fun key ->
            async {
                let! remoteUrl = getRemoteUrl key

                let remote =
                    remoteUrl |> Option.bind parseAzureDevOpsUrl

                match remote with
                | None -> return Map.empty
                | Some r -> return! fetchPrStatuses r
            })

    let getCachedAt (repoRoot: string) = cache.GetCachedAt repoRoot

    let lookupPrStatus (prMap: Map<string, PrStatus>) (branchName: string option) =
        branchName
        |> Option.bind (fun b -> prMap |> Map.tryFind b)
        |> Option.defaultValue NoPr
