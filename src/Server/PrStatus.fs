module Server.PrStatus

open System
open System.Collections.Concurrent
open System.Diagnostics
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

let private runProcess (fileName: string) (arguments: string) =
    async {
        try
            let file, args =
                match Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.Windows) with
                | true -> "cmd", $"/c {fileName} {arguments}"
                | false -> fileName, arguments

            Log.log "PR" $"Running: {fileName} {arguments}"

            let psi =
                ProcessStartInfo(
                    file,
                    args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            use proc = Process.Start(psi)
            let! output = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
            let! stderr = proc.StandardError.ReadToEndAsync() |> Async.AwaitTask
            do! proc.WaitForExitAsync() |> Async.AwaitTask

            match proc.ExitCode with
            | 0 ->
                let truncated = if output.Length > 200 then output.Substring(0, 200) + "..." else output.TrimEnd()
                Log.log "PR" $"Exit 0: {truncated}"
                return Some(output.TrimEnd())
            | code ->
                Log.log "PR" $"Exit {code}: {stderr.TrimEnd()}"
                return None
        with
        | :? System.ComponentModel.Win32Exception as ex ->
            Log.log "PR" $"Process failed: {ex.Message}"
            return None
    }

let getRemoteUrl (repoRoot: string) =
    runProcess "git" $"-C \"{repoRoot}\" remote get-url origin"

let private parsePrList (json: string) =
    try
        let doc = JsonDocument.Parse(json)

        doc.RootElement.EnumerateArray()
        |> Seq.choose (fun el ->
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

                Some(branchName, prId, title, isDraft)
            with ex ->
                Log.log "PR" $"Failed to parse PR entry: {ex.Message}"
                None)
        |> Seq.toList
    with ex ->
        Log.log "PR" $"Failed to parse PR list JSON: {ex.Message}"
        []

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

let private parseBuildStatus (json: string) =
    try
        let doc = JsonDocument.Parse(json)
        let runs = doc.RootElement.EnumerateArray() |> Seq.tryHead

        match runs with
        | Some run ->
            let buildId =
                match run.TryGetProperty("id") with
                | true, v -> Some(v.GetInt32())
                | _ -> None

            let status = run.GetProperty("status").GetString()

            let buildStatus =
                match status with
                | "inProgress" -> BuildStatus.Building
                | "completed" ->
                    match run.TryGetProperty("result") with
                    | true, result ->
                        match result.GetString() with
                        | "succeeded" -> BuildStatus.Succeeded
                        | "failed" -> BuildStatus.Failed
                        | "partiallySucceeded" -> BuildStatus.PartiallySucceeded
                        | "canceled" -> BuildStatus.Canceled
                        | _ -> BuildStatus.NoBuild
                    | _ -> BuildStatus.NoBuild
                | _ -> BuildStatus.NoBuild

            buildStatus, buildId
        | None -> BuildStatus.NoBuild, None
    with ex ->
        Log.log "PR" $"Failed to parse build status JSON: {ex.Message}"
        BuildStatus.NoBuild, None

let private fetchPrThreadCount (remote: AzDoRemote) (prId: int) =
    async {
        let args =
            $"devops invoke --area git --resource pullRequestThreads --route-parameters project={remote.Project} repositoryId={remote.Repo} pullRequestId={prId} --org https://dev.azure.com/{remote.Org} --api-version 7.1 -o json"

        let! output = runProcess "az" args

        return
            output
            |> Option.map parseThreadCounts
            |> Option.defaultValue { Unresolved = 0; Total = 0 }
    }

let private fetchBuildStatus (remote: AzDoRemote) (branchName: string) =
    async {
        let args =
            $"pipelines runs list --branch \"{branchName}\" --reason pullRequest --top 1 --org https://dev.azure.com/{remote.Org} --project \"{remote.Project}\" -o json"

        let! output = runProcess "az" args

        let buildStatus, buildId =
            output
            |> Option.map parseBuildStatus
            |> Option.defaultValue (BuildStatus.NoBuild, None)

        let buildUrl =
            buildId
            |> Option.map (fun id ->
                sprintf "https://dev.azure.com/%s/%s/_build/results?buildId=%d" remote.Org remote.Project id)

        return buildStatus, buildUrl
    }

let fetchPrStatuses (remote: AzDoRemote) =
    async {
        let args =
            $"repos pr list --org https://dev.azure.com/{remote.Org} --project \"{remote.Project}\" --repository \"{remote.Repo}\" --status active -o json"

        let! output = runProcess "az" args

        match output with
        | None -> return Map.empty
        | Some json ->
            let prs = parsePrList json

            let! entries =
                prs
                |> List.map (fun (branchName, prId, title, isDraft) ->
                    async {
                        let! threadCounts = fetchPrThreadCount remote prId
                        let! buildStatus, buildUrl = fetchBuildStatus remote branchName

                        let url =
                            sprintf "https://dev.azure.com/%s/%s/_git/%s/pullrequest/%d" remote.Org remote.Project remote.Repo prId

                        return
                            branchName,
                            HasPr
                                { Id = prId
                                  Title = title
                                  Url = url
                                  IsDraft = isDraft
                                  ThreadCounts = threadCounts
                                  BuildStatus = buildStatus
                                  BuildUrl = buildUrl
                                  IsMerged = false }
                    })
                |> Async.Parallel

            return entries |> Map.ofArray
    }

module Cache =
    type CacheEntry<'T> =
        { Value: 'T
          CachedAt: DateTimeOffset }

    let private cache = ConcurrentDictionary<string, CacheEntry<Map<string, PrStatus>>>()
    let private ttl = TimeSpan.FromSeconds(120.0)

    let getCachedPrStatuses (repoRoot: string) =
        async {
            let now = DateTimeOffset.UtcNow

            match cache.TryGetValue(repoRoot) with
            | true, entry when now - entry.CachedAt < ttl -> return entry.Value
            | _ ->
                let! remoteUrl = getRemoteUrl repoRoot

                let remote =
                    remoteUrl |> Option.bind parseAzureDevOpsUrl

                match remote with
                | None -> return Map.empty
                | Some r ->
                    let! statuses = fetchPrStatuses r
                    cache.[repoRoot] <- { Value = statuses; CachedAt = now }
                    return statuses
        }

    let lookupPrStatus (prMap: Map<string, PrStatus>) (branchName: string option) =
        branchName
        |> Option.bind (fun b -> prMap |> Map.tryFind b)
        |> Option.defaultValue NoPr
