module Server.DemoFixture

open System
open Shared

type DemoFrame =
    { Data: FixtureData
      DurationSeconds: int }

let private azDoRepoId = RepoId.create "C:\\code\\CloudPlatform"
let private githubRepoId = RepoId.create "C:\\code\\DataPipeline"

let private azDoPath prefix = WorktreePath.create $"C:\\code\\CloudPlatform\\{prefix}"
let private githubPath prefix = WorktreePath.create $"C:\\code\\DataPipeline\\{prefix}"

let private baseTimestamp = DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.FromHours(2))

let private prAzDoOpen: PrInfo =
    { Id = 4201
      Title = "Add retry logic to blob storage client"
      Url = "https://dev.azure.com/contoso/CloudPlatform/_git/CloudPlatform/pullrequest/4201"
      IsDraft = false
      Comments = WithResolution(2, 5)
      Builds =
        [ { Name = "CI Build"
            Status = Building
            Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88801"
            Failure = None } ]
      IsMerged = false }

let private prAzDoDraft: PrInfo =
    { Id = 4199
      Title = "WIP: Refactor config loading"
      Url = "https://dev.azure.com/contoso/CloudPlatform/_git/CloudPlatform/pullrequest/4199"
      IsDraft = true
      Comments = CountOnly 1
      Builds =
        [ { Name = "CI Build"
            Status = Succeeded
            Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88790"
            Failure = None } ]
      IsMerged = false }

let private prGithubOpen: PrInfo =
    { Id = 312
      Title = "Streaming aggregation for real-time metrics"
      Url = "https://github.com/acme/data-pipeline/pull/312"
      IsDraft = false
      Comments = WithResolution(1, 3)
      Builds =
        [ { Name = "test"
            Status = Succeeded
            Url = Some "https://github.com/acme/data-pipeline/actions/runs/99001"
            Failure = None }
          { Name = "lint"
            Status = PartiallySucceeded
            Url = Some "https://github.com/acme/data-pipeline/actions/runs/99002"
            Failure = None } ]
      IsMerged = false }

let private prGithubMerged: PrInfo =
    { Id = 308
      Title = "Fix CSV parser edge case with quoted newlines"
      Url = "https://github.com/acme/data-pipeline/pull/308"
      IsDraft = false
      Comments = CountOnly 4
      Builds =
        [ { Name = "test"
            Status = Succeeded
            Url = Some "https://github.com/acme/data-pipeline/actions/runs/98800"
            Failure = None } ]
      IsMerged = true }

let private wtAzDoMain: WorktreeStatus =
    { Path = azDoPath "main"
      Branch = "main"
      LastCommitMessage = "Merge PR #4198: Update dependencies to latest stable"
      LastCommitTime = baseTimestamp.AddMinutes(-30.0)
      Beads = { Open = 0; InProgress = 0; Closed = 12 }
      CodingTool = Idle
      CodingToolProvider = None
      LastUserMessage = None
      Pr = NoPr
      MainBehindCount = 0
      IsDirty = false
      WorkMetrics = None
      HasActiveSession = false
      IsArchived = false }

let private wtAzDoRetry: WorktreeStatus =
    { Path = azDoPath "feature-retry"
      Branch = "feature/retry-logic"
      LastCommitMessage = "Add exponential backoff to blob storage retries"
      LastCommitTime = baseTimestamp.AddMinutes(-2.0)
      Beads = { Open = 3; InProgress = 1; Closed = 5 }
      CodingTool = Working
      CodingToolProvider = Some Claude
      LastUserMessage = Some ("implement retry with jitter", baseTimestamp.AddMinutes(-5.0))
      Pr = HasPr prAzDoOpen
      MainBehindCount = 2
      IsDirty = true
      WorkMetrics = Some { CommitCount = 8; LinesAdded = 342; LinesRemoved = 67 }
      HasActiveSession = true
      IsArchived = false }

let private wtAzDoConfig: WorktreeStatus =
    { Path = azDoPath "refactor-config"
      Branch = "refactor/config-loading"
      LastCommitMessage = "Extract config validation into separate module"
      LastCommitTime = baseTimestamp.AddMinutes(-45.0)
      Beads = { Open = 1; InProgress = 0; Closed = 3 }
      CodingTool = Idle
      CodingToolProvider = Some Claude
      LastUserMessage = None
      Pr = HasPr prAzDoDraft
      MainBehindCount = 5
      IsDirty = false
      WorkMetrics = Some { CommitCount = 3; LinesAdded = 128; LinesRemoved = 89 }
      HasActiveSession = false
      IsArchived = false }

let private wtAzDoArchived: WorktreeStatus =
    { Path = azDoPath "old-migration"
      Branch = "feature/db-migration"
      LastCommitMessage = "Complete database migration script v2"
      LastCommitTime = baseTimestamp.AddHours(-48.0)
      Beads = { Open = 0; InProgress = 0; Closed = 7 }
      CodingTool = Done
      CodingToolProvider = Some Claude
      LastUserMessage = None
      Pr = NoPr
      MainBehindCount = 12
      IsDirty = false
      WorkMetrics = Some { CommitCount = 15; LinesAdded = 890; LinesRemoved = 234 }
      HasActiveSession = false
      IsArchived = true }

let private wtGithubMain: WorktreeStatus =
    { Path = githubPath "main"
      Branch = "main"
      LastCommitMessage = "Merge pull request #308: Fix CSV parser edge case"
      LastCommitTime = baseTimestamp.AddMinutes(-15.0)
      Beads = BeadsSummary.zero
      CodingTool = Idle
      CodingToolProvider = None
      LastUserMessage = None
      Pr = NoPr
      MainBehindCount = 0
      IsDirty = false
      WorkMetrics = None
      HasActiveSession = false
      IsArchived = false }

let private wtGithubStreaming: WorktreeStatus =
    { Path = githubPath "streaming"
      Branch = "feature/streaming-agg"
      LastCommitMessage = "Add windowed aggregation with tumbling windows"
      LastCommitTime = baseTimestamp.AddMinutes(-1.0)
      Beads = { Open = 2; InProgress = 2; Closed = 4 }
      CodingTool = Working
      CodingToolProvider = Some Copilot
      LastUserMessage = Some ("add tumbling window support", baseTimestamp.AddMinutes(-3.0))
      Pr = HasPr prGithubOpen
      MainBehindCount = 1
      IsDirty = true
      WorkMetrics = Some { CommitCount = 12; LinesAdded = 567; LinesRemoved = 123 }
      HasActiveSession = true
      IsArchived = false }

let private wtGithubCsvFix: WorktreeStatus =
    { Path = githubPath "csv-fix"
      Branch = "fix/csv-parser"
      LastCommitMessage = "Handle quoted newlines in CSV field parser"
      LastCommitTime = baseTimestamp.AddMinutes(-60.0)
      Beads = { Open = 0; InProgress = 0; Closed = 2 }
      CodingTool = Done
      CodingToolProvider = Some Copilot
      LastUserMessage = None
      Pr = HasPr prGithubMerged
      MainBehindCount = 0
      IsDirty = false
      WorkMetrics = Some { CommitCount = 4; LinesAdded = 45; LinesRemoved = 12 }
      HasActiveSession = false
      IsArchived = false }

let private baseSchedulerEvents: CardEvent list =
    [ { Source = "git-fetch"
        Message = "CloudPlatform (fetched 3 new commits)"
        Timestamp = baseTimestamp.AddSeconds(-10.0)
        Status = Some StepStatus.Succeeded
        Duration = Some (TimeSpan.FromSeconds(1.8)) }
      { Source = "git-fetch"
        Message = "DataPipeline (up to date)"
        Timestamp = baseTimestamp.AddSeconds(-8.0)
        Status = Some StepStatus.Succeeded
        Duration = Some (TimeSpan.FromSeconds(0.9)) }
      { Source = "pr-check"
        Message = "feature/retry-logic (4 threads, 2 unresolved)"
        Timestamp = baseTimestamp.AddSeconds(-5.0)
        Status = Some StepStatus.Succeeded
        Duration = Some (TimeSpan.FromSeconds(2.1)) } ]

let private baseLatestByCategory: Map<string, CardEvent> =
    [ "git-fetch",
      { Source = "git-fetch"
        Message = "All repos fetched"
        Timestamp = baseTimestamp.AddSeconds(-8.0)
        Status = Some StepStatus.Succeeded
        Duration = Some (TimeSpan.FromSeconds(2.7)) }
      "pr-check",
      { Source = "pr-check"
        Message = "3 PRs checked"
        Timestamp = baseTimestamp.AddSeconds(-5.0)
        Status = Some StepStatus.Succeeded
        Duration = Some (TimeSpan.FromSeconds(2.1)) }
      "beads",
      { Source = "beads"
        Message = "Beads refreshed"
        Timestamp = baseTimestamp.AddSeconds(-3.0)
        Status = Some StepStatus.Succeeded
        Duration = Some (TimeSpan.FromSeconds(0.4)) } ]
    |> Map.ofList

let private baseSyncStatus: Map<string, CardEvent list> =
    [ "C:\\code\\CloudPlatform/feature/retry-logic",
      [ { Source = "sync"
          Message = "feature/retry-logic: rebased on main"
          Timestamp = baseTimestamp.AddMinutes(-10.0)
          Status = Some StepStatus.Succeeded
          Duration = Some (TimeSpan.FromSeconds(3.2)) } ] ]
    |> Map.ofList

let private baseMetrics: SystemMetrics =
    { CpuPercent = 23.4
      MemoryUsedMb = 12800
      MemoryTotalMb = 32768 }

let private baseDashboard: DashboardResponse =
    { Repos =
        [ { RepoId = azDoRepoId
            RootFolderName = "CloudPlatform"
            Worktrees = [ wtAzDoMain; wtAzDoRetry; wtAzDoConfig; wtAzDoArchived ]
            IsReady = true }
          { RepoId = githubRepoId
            RootFolderName = "DataPipeline"
            Worktrees = [ wtGithubMain; wtGithubStreaming; wtGithubCsvFix ]
            IsReady = true } ]
      SchedulerEvents = baseSchedulerEvents
      LatestByCategory = baseLatestByCategory
      AppVersion = "demo|0"
      DeployBranch = Some "feature/retry-logic"
      SystemMetrics = Some baseMetrics
      EditorName = "VS Code" }

let private baseFixture: FixtureData =
    { Worktrees = baseDashboard
      SyncStatus = baseSyncStatus }

let private prAzDoOpenFailed =
    { prAzDoOpen with
        Builds =
            [ { Name = "CI Build"
                Status = Failed
                Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88801"
                Failure = Some { StepName = "Run Tests"; Log = "3 test(s) failed in RetryTests.cs" } } ] }

let private prGithubOpenCanceled =
    { prGithubOpen with
        Builds =
            [ { Name = "test"
                Status = Canceled
                Url = Some "https://github.com/acme/data-pipeline/actions/runs/99001"
                Failure = None }
              { Name = "lint"
                Status = PartiallySucceeded
                Url = Some "https://github.com/acme/data-pipeline/actions/runs/99002"
                Failure = None } ] }

let private frame2SchedulerEvents =
    baseSchedulerEvents @
    [ { Source = "sync"
        Message = "feature/retry-logic: sync started"
        Timestamp = baseTimestamp.AddSeconds(3.0)
        Status = Some StepStatus.Running
        Duration = None } ]

let private frame2LatestByCategory =
    baseLatestByCategory
    |> Map.add "beads"
        { Source = "beads"
          Message = "CloudPlatform: bd command failed"
          Timestamp = baseTimestamp.AddSeconds(3.0)
          Status = Some (StepStatus.Failed "bd: database locked")
          Duration = Some (TimeSpan.FromSeconds(5.0)) }

let private frame2SyncStatus =
    baseSyncStatus
    |> Map.add "C:\\code\\CloudPlatform/feature/retry-logic"
        [ { Source = "sync"
            Message = "feature/retry-logic: pulling latest"
            Timestamp = baseTimestamp.AddSeconds(3.0)
            Status = Some StepStatus.Running
            Duration = None }
          { Source = "sync"
            Message = "feature/retry-logic: sync started"
            Timestamp = baseTimestamp.AddSeconds(3.0)
            Status = Some StepStatus.Running
            Duration = None } ]

let private frame2Repos =
    baseDashboard.Repos
    |> List.map (fun repo ->
        match RepoId.value repo.RepoId with
        | id when id = "C:\\code\\CloudPlatform" ->
            { repo with
                Worktrees =
                    repo.Worktrees
                    |> List.map (fun wt ->
                        match wt.Branch with
                        | "feature/retry-logic" ->
                            { wt with
                                CodingTool = WaitingForUser
                                LastUserMessage = Some ("implement retry with jitter", baseTimestamp.AddMinutes(-5.0))
                                Pr = HasPr prAzDoOpenFailed }
                        | _ -> wt) }
        | id when id = "C:\\code\\DataPipeline" ->
            { repo with
                Worktrees =
                    repo.Worktrees
                    |> List.map (fun wt ->
                        match wt.Branch with
                        | "feature/streaming-agg" ->
                            { wt with
                                CodingTool = WaitingForUser
                                Pr = HasPr prGithubOpenCanceled }
                        | _ -> wt) }
        | _ -> repo)

let private frame2: FixtureData =
    { Worktrees =
        { baseDashboard with
            Repos = frame2Repos
            SchedulerEvents = frame2SchedulerEvents
            LatestByCategory = frame2LatestByCategory
            SystemMetrics = Some { baseMetrics with CpuPercent = 67.8; MemoryUsedMb = 18200 } }
      SyncStatus = frame2SyncStatus }

let private frame3SyncStatus =
    baseSyncStatus
    |> Map.add "C:\\code\\CloudPlatform/feature/retry-logic"
        [ { Source = "sync"
            Message = "feature/retry-logic: sync complete"
            Timestamp = baseTimestamp.AddSeconds(6.0)
            Status = Some StepStatus.Succeeded
            Duration = Some (TimeSpan.FromSeconds(3.0)) } ]

let private frame3LatestByCategory =
    baseLatestByCategory
    |> Map.add "beads"
        { Source = "beads"
          Message = "Beads refreshed"
          Timestamp = baseTimestamp.AddSeconds(6.0)
          Status = Some StepStatus.Succeeded
          Duration = Some (TimeSpan.FromSeconds(0.3)) }

let private frame3Repos =
    baseDashboard.Repos
    |> List.map (fun repo ->
        match RepoId.value repo.RepoId with
        | id when id = "C:\\code\\CloudPlatform" ->
            { repo with
                Worktrees =
                    repo.Worktrees
                    |> List.map (fun wt ->
                        match wt.Branch with
                        | "feature/retry-logic" ->
                            { wt with
                                CodingTool = Working
                                MainBehindCount = 0
                                Pr = HasPr { prAzDoOpen with Builds = [ { Name = "CI Build"; Status = Succeeded; Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88802"; Failure = None } ] } }
                        | _ -> wt) }
        | id when id = "C:\\code\\DataPipeline" ->
            { repo with
                Worktrees =
                    repo.Worktrees
                    |> List.map (fun wt ->
                        match wt.Branch with
                        | "feature/streaming-agg" ->
                            { wt with
                                CodingTool = Done
                                IsDirty = false
                                Pr = HasPr { prGithubOpen with Builds = [ { Name = "test"; Status = Succeeded; Url = Some "https://github.com/acme/data-pipeline/actions/runs/99003"; Failure = None }; { Name = "lint"; Status = Succeeded; Url = Some "https://github.com/acme/data-pipeline/actions/runs/99004"; Failure = None } ] } }
                        | _ -> wt) }
        | _ -> repo)

let private frame3: FixtureData =
    { Worktrees =
        { baseDashboard with
            Repos = frame3Repos
            SchedulerEvents = baseSchedulerEvents
            LatestByCategory = frame3LatestByCategory
            SystemMetrics = Some { baseMetrics with CpuPercent = 31.2; MemoryUsedMb = 14100 } }
      SyncStatus = frame3SyncStatus }

let private frame4Repos =
    baseDashboard.Repos
    |> List.map (fun repo ->
        match RepoId.value repo.RepoId with
        | id when id = "C:\\code\\CloudPlatform" ->
            { repo with
                Worktrees =
                    repo.Worktrees
                    |> List.map (fun wt ->
                        match wt.Branch with
                        | "feature/retry-logic" ->
                            { wt with
                                CodingTool = Working
                                IsDirty = true
                                LastCommitMessage = "Add jitter to retry delay calculation"
                                Pr = HasPr { prAzDoOpen with Builds = [ { Name = "CI Build"; Status = Building; Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88803"; Failure = None } ] } }
                        | "refactor/config-loading" ->
                            { wt with
                                CodingTool = WaitingForUser
                                CodingToolProvider = Some Claude
                                LastUserMessage = Some ("review the config changes", baseTimestamp.AddMinutes(-1.0)) }
                        | _ -> wt) }
        | id when id = "C:\\code\\DataPipeline" ->
            { repo with
                Worktrees =
                    repo.Worktrees
                    |> List.map (fun wt ->
                        match wt.Branch with
                        | "feature/streaming-agg" ->
                            { wt with
                                CodingTool = Idle
                                CodingToolProvider = Some Copilot }
                        | _ -> wt) }
        | _ -> repo)

let private frame4: FixtureData =
    { Worktrees =
        { baseDashboard with
            Repos = frame4Repos
            SystemMetrics = Some { baseMetrics with CpuPercent = 18.9; MemoryUsedMb = 13400 } }
      SyncStatus = baseSyncStatus }

let private frames: DemoFrame list =
    [ { Data = baseFixture; DurationSeconds = 3 }
      { Data = frame2; DurationSeconds = 3 }
      { Data = frame3; DurationSeconds = 2 }
      { Data = frame4; DurationSeconds = 2 } ]

let private totalDurationSeconds =
    frames |> List.sumBy _.DurationSeconds

let private adjustWorktreeTimestamps (now: DateTimeOffset) (wt: WorktreeStatus) =
    let shift = now - baseTimestamp
    { wt with
        LastCommitTime = wt.LastCommitTime + shift
        LastUserMessage =
            wt.LastUserMessage
            |> Option.map (fun (msg, ts) -> msg, ts + shift) }

let private adjustEventTimestamp (now: DateTimeOffset) (evt: CardEvent) =
    let shift = now - baseTimestamp
    { evt with Timestamp = evt.Timestamp + shift }

let adjustTimestamps (now: DateTimeOffset) (fixture: FixtureData) : FixtureData =
    let adjustEvent = adjustEventTimestamp now
    let adjustWt = adjustWorktreeTimestamps now
    { Worktrees =
        { fixture.Worktrees with
            Repos =
                fixture.Worktrees.Repos
                |> List.map (fun repo ->
                    { repo with
                        Worktrees = repo.Worktrees |> List.map adjustWt })
            SchedulerEvents =
                fixture.Worktrees.SchedulerEvents |> List.map adjustEvent
            LatestByCategory =
                fixture.Worktrees.LatestByCategory |> Map.map (fun _ -> adjustEvent) }
      SyncStatus =
        fixture.SyncStatus
        |> Map.map (fun _ events -> events |> List.map adjustEvent) }

let rec private findFrame (positionSeconds: int) (remaining: DemoFrame list) : FixtureData =
    match remaining with
    | [] -> baseFixture
    | [ frame ] -> frame.Data
    | frame :: rest ->
        if positionSeconds < frame.DurationSeconds then frame.Data
        else findFrame (positionSeconds - frame.DurationSeconds) rest

let selectFrame (startTime: DateTimeOffset) (now: DateTimeOffset) : FixtureData =
    let elapsed = now - startTime
    let positionSeconds = (int elapsed.TotalSeconds) % totalDurationSeconds
    findFrame positionSeconds frames
    |> adjustTimestamps now
