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
            None
    with _ ->
        None

let private runProcess (fileName: string) (arguments: string) =
    async {
        try
            let psi =
                ProcessStartInfo(
                    fileName,
                    arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            use proc = Process.Start(psi)
            let! output = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
            do! proc.WaitForExitAsync() |> Async.AwaitTask
            return if proc.ExitCode = 0 then Some(output.TrimEnd()) else None
        with
        | :? System.ComponentModel.Win32Exception -> return None
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

                let reviewerVotes =
                    match el.TryGetProperty("reviewers") with
                    | true, reviewers ->
                        reviewers.EnumerateArray()
                        |> Seq.map (fun r ->
                            let name = r.GetProperty("displayName").GetString()
                            let vote = r.GetProperty("vote").GetInt32()
                            name, vote)
                        |> Map.ofSeq
                    | _ -> Map.empty

                Some(branchName, prId, title, isDraft, reviewerVotes)
            with _ ->
                None)
        |> Seq.toList
    with _ ->
        []

let private countUnresolvedThreads (json: string) =
    try
        let doc = JsonDocument.Parse(json)

        doc.RootElement.EnumerateArray()
        |> Seq.filter (fun thread ->
            match thread.TryGetProperty("status") with
            | true, status -> status.GetString() = "active"
            | _ -> false)
        |> Seq.length
    with _ ->
        0

let private parseBuildStatus (json: string) =
    try
        let doc = JsonDocument.Parse(json)
        let runs = doc.RootElement.EnumerateArray() |> Seq.tryHead

        match runs with
        | Some run ->
            let status = run.GetProperty("status").GetString()

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
        | None -> BuildStatus.NoBuild
    with _ ->
        BuildStatus.NoBuild

let private fetchPrThreadCount (remote: AzDoRemote) (prId: int) =
    async {
        let args =
            $"repos pr thread list --id {prId} --org https://dev.azure.com/{remote.Org} -o json"

        let! output = runProcess "az" args
        return output |> Option.map countUnresolvedThreads |> Option.defaultValue 0
    }

let private fetchBuildStatus (remote: AzDoRemote) (branchName: string) =
    async {
        let args =
            $"pipelines runs list --branch \"{branchName}\" --reason pullRequest --top 1 --org https://dev.azure.com/{remote.Org} --project \"{remote.Project}\" -o json"

        let! output = runProcess "az" args
        return output |> Option.map parseBuildStatus |> Option.defaultValue BuildStatus.NoBuild
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
                |> List.map (fun (branchName, prId, title, isDraft, reviewerVotes) ->
                    async {
                        let! threadCount = fetchPrThreadCount remote prId
                        let! buildStatus = fetchBuildStatus remote branchName

                        return
                            branchName,
                            HasPr
                                { Id = prId
                                  Title = title
                                  IsDraft = isDraft
                                  ReviewerVotes = reviewerVotes
                                  UnresolvedThreadCount = threadCount
                                  BuildStatus = buildStatus }
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
