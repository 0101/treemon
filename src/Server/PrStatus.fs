module Server.PrStatus

open System
open System.IO
open System.Text.Json
open Shared

let private tryProp (name: string) (el: JsonElement) =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
    | _ -> None

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

type internal ParsedPr =
    { BranchName: string
      PrId: int
      Title: string
      IsDraft: bool
      IsMerged: bool
      ClosedDate: DateTimeOffset option }

let private parsePrList (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let prElements = doc.RootElement.EnumerateArray() |> Seq.toList

        let repoGuid =
            prElements
            |> List.tryHead
            |> Option.bind (tryProp "repository")
            |> Option.bind (tryProp "id")
            |> Option.map (fun v -> v.GetString())

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
                            sourceRef.["refs/heads/".Length..]
                        else
                            sourceRef

                    let status = el.GetProperty("status").GetString()
                    let isMerged = status = "completed"

                    let closedDate =
                        el
                        |> tryProp "closedDate"
                        |> Option.bind (fun v ->
                            match DateTimeOffset.TryParse(v.GetString()) with
                            | true, dt -> Some dt
                            | _ -> None)

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
        use doc = JsonDocument.Parse(json)

        let threads =
            doc.RootElement.GetProperty("value").EnumerateArray()
            |> Seq.filter (fun thread ->
                let isDeleted =
                    thread |> tryProp "isDeleted" |> Option.map (fun v -> v.GetBoolean()) |> Option.defaultValue false

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
        |> Option.bind (tryProp "name")
        |> Option.map (fun v -> v.GetString())
        |> Option.defaultValue "Unknown"

    let definitionId =
        definition
        |> Option.bind (tryProp "id")
        |> Option.map (fun v -> v.GetInt32())

    let buildId =
        run |> tryProp "id" |> Option.map (fun v -> v.GetInt32())

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

let private parseBuilds (remote: AzDoRemote) (json: string) =
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

let private parseFailedStep (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let records = doc.RootElement.GetProperty("records").EnumerateArray() |> Seq.toList

        records
        |> List.tryFind (fun r ->
            let isTask =
                r |> tryProp "type" |> Option.map (fun v -> v.GetString() = "Task") |> Option.defaultValue false
            let isFailed =
                r |> tryProp "result" |> Option.map (fun v -> v.GetString() = "failed") |> Option.defaultValue false
            isTask && isFailed)
        |> Option.bind (fun r ->
            let name =
                r |> tryProp "name" |> Option.map (fun v -> v.GetString()) |> Option.defaultValue "Unknown step"
            let logId =
                r |> tryProp "log" |> Option.bind (tryProp "id") |> Option.map (fun v -> v.GetInt32())
            logId |> Option.map (fun id -> name, id))
    with ex ->
        Log.log "PR" $"Failed to parse build timeline: {ex.Message}"
        None

let private parseBuildLog (json: string) =
    try
        use doc = JsonDocument.Parse(json)

        let lines =
            doc.RootElement.GetProperty("value").EnumerateArray()
            |> Seq.map (fun el -> el.GetString())
            |> Seq.toList

        let trimmedLines =
            lines
            |> List.map (fun line ->
                let spaceIdx = line.IndexOf(" ")
                if spaceIdx > 20 then line.[spaceIdx + 1..] else line)

        let tail =
            let start = max 0 (trimmedLines.Length - 50)
            trimmedLines.[start..]

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
    |> List.distinctBy (fun pr -> pr.BranchName)

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
                                async { return { Unresolved = 0; Total = 0 }, [] }
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

            return entries |> Array.toList |> Map.ofList
    }

let fetchPrStatusesByRepoRoot (repoRoot: string) (knownBranches: Set<string>) =
    async {
        let! remoteUrl = getRemoteUrl repoRoot

        let remote =
            remoteUrl |> Option.bind parseAzureDevOpsUrl

        match remote with
        | None -> return Map.empty
        | Some r -> return! fetchPrStatuses r knownBranches
    }

let lookupPrStatus (prMap: Map<string, PrStatus>) (branchName: string option) =
    branchName
    |> Option.bind (fun b -> prMap |> Map.tryFind b)
    |> Option.defaultValue NoPr
