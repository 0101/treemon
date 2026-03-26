module Server.DemoFixture

open System
open Shared

type DemoFrame =
    { Data: FixtureData
      DurationSeconds: int }

// --- Constants ---

let private azDoRepoId = RepoId.create "C:\\code\\CloudPlatform"
let private githubRepoId = RepoId.create "C:\\code\\DataPipeline"
let private azDoPath prefix = WorktreePath.create $"C:\\code\\CloudPlatform\\{prefix}"
let private githubPath prefix = WorktreePath.create $"C:\\code\\DataPipeline\\{prefix}"
let private baseTimestamp = DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.FromHours(2))

// --- Helpers ---

let private evt source message secsAgo status duration =
    { Source = source
      Message = message
      Timestamp = baseTimestamp.AddSeconds(float -secsAgo)
      Status = status
      Duration = duration }

let private updateWorktree repoId branch transform repos =
    repos
    |> List.map (fun repo ->
        if repo.RepoId = repoId then
            { repo with
                Worktrees =
                    repo.Worktrees
                    |> List.map (fun wt -> if wt.Branch = branch then transform wt else wt) }
        else
            repo)

// Fixture-level transforms for chaining frames
let private withRepos f (fix: FixtureData) =
    { fix with
        Worktrees = { fix.Worktrees with Repos = f fix.Worktrees.Repos } }

let private withRetry f = withRepos (updateWorktree azDoRepoId "feature/retry-logic" f)
let private withConfig f = withRepos (updateWorktree azDoRepoId "refactor/config-loading" f)
let private withAuth f = withRepos (updateWorktree azDoRepoId "feature/auth-middleware" f)
let private withStream f = withRepos (updateWorktree githubRepoId "feature/streaming-agg" f)

let private withCpu cpu mem (fix: FixtureData) =
    { fix with
        Worktrees =
            { fix.Worktrees with
                SystemMetrics = Some { CpuPercent = cpu; MemoryUsedMb = mem; MemoryTotalMb = 32768 } } }

let private withCardEvt branch cardEvt (fix: FixtureData) =
    { fix with SyncStatus = fix.SyncStatus |> Map.add branch [ cardEvt ] }

let private withLatest key cardEvt (fix: FixtureData) =
    { fix with
        Worktrees = { fix.Worktrees with LatestByCategory = fix.Worktrees.LatestByCategory |> Map.add key cardEvt } }

let private azDoEvt = "C:\\code\\CloudPlatform"
let private githubEvt = "C:\\code\\DataPipeline"

// --- PRs (retry-logic cycles, others static) ---

let private prRetryBuilding: PrInfo =
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
      IsMerged = false
      HasConflicts = false }

let private prRetryFailed =
    { prRetryBuilding with
        Builds =
            [ { Name = "CI Build"
                Status = Failed
                Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88801"
                Failure = Some { StepName = "Run Tests"; Log = "3 test(s) failed in RetryTests.cs" } } ] }

let private prRetryRebuilding =
    { prRetryBuilding with
        Builds =
            [ { Name = "CI Build"
                Status = Building
                Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88803"
                Failure = None } ] }

let private prRetrySucceeded =
    { prRetryBuilding with
        Comments = WithResolution(1, 5)
        Builds =
            [ { Name = "CI Build"
                Status = Succeeded
                Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88803"
                Failure = None } ] }

let private prConfigDraft: PrInfo =
    { Id = 4199
      Title = "WIP: Refactor config loading"
      Url = "https://dev.azure.com/contoso/CloudPlatform/_git/CloudPlatform/pullrequest/4199"
      IsDraft = true
      Comments = WithResolution(0, 1)
      Builds =
        [ { Name = "CI Build"
            Status = Succeeded
            Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88790"
            Failure = None } ]
      IsMerged = false
      HasConflicts = false }

let private prAuth: PrInfo =
    { Id = 4203
      Title = "Add JWT auth middleware for API endpoints"
      Url = "https://dev.azure.com/contoso/CloudPlatform/_git/CloudPlatform/pullrequest/4203"
      IsDraft = false
      Comments = WithResolution(1, 3)
      Builds =
        [ { Name = "CI Build"
            Status = Succeeded
            Url = Some "https://dev.azure.com/contoso/CloudPlatform/_build/results?buildId=88810"
            Failure = None } ]
      IsMerged = false
      HasConflicts = false }

let private prStreaming: PrInfo =
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
      IsMerged = false
      HasConflicts = false }

let private prCsvMerged: PrInfo =
    { Id = 308
      Title = "Fix CSV parser edge case with quoted newlines"
      Url = "https://github.com/acme/data-pipeline/pull/308"
      IsDraft = false
      Comments = WithResolution(0, 4)
      Builds =
        [ { Name = "test"
            Status = Succeeded
            Url = Some "https://github.com/acme/data-pipeline/actions/runs/98800"
            Failure = None } ]
      IsMerged = true
      HasConflicts = false }

// --- Worktrees (base definitions) ---

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

let private wtRetryLogic: WorktreeStatus =
    { Path = azDoPath "feature-retry"
      Branch = "feature/retry-logic"
      LastCommitMessage = "Add exponential backoff to blob storage retries"
      LastCommitTime = baseTimestamp.AddMinutes(-2.0)
      Beads = { Open = 3; InProgress = 1; Closed = 5 }
      CodingTool = Working
      CodingToolProvider = Some Claude
      LastUserMessage = Some("implement retry with jitter", baseTimestamp.AddMinutes(-5.0))
      Pr = HasPr prRetryBuilding
      MainBehindCount = 2
      IsDirty = true
      WorkMetrics = Some { CommitCount = 8; LinesAdded = 342; LinesRemoved = 67 }
      HasActiveSession = true
      IsArchived = false }

let private wtConfigLoading: WorktreeStatus =
    { Path = azDoPath "refactor-config"
      Branch = "refactor/config-loading"
      LastCommitMessage = "Extract config validation into separate module"
      LastCommitTime = baseTimestamp.AddMinutes(-12.0)
      Beads = { Open = 1; InProgress = 1; Closed = 3 }
      CodingTool = Working
      CodingToolProvider = Some Claude
      LastUserMessage = Some("refactor env-specific config loading", baseTimestamp.AddMinutes(-15.0))
      Pr = HasPr prConfigDraft
      MainBehindCount = 5
      IsDirty = false
      WorkMetrics = Some { CommitCount = 3; LinesAdded = 128; LinesRemoved = 89 }
      HasActiveSession = true
      IsArchived = false }

let private wtAuthMiddleware: WorktreeStatus =
    { Path = azDoPath "feature-auth"
      Branch = "feature/auth-middleware"
      LastCommitMessage = "Add JWT validation and claims extraction"
      LastCommitTime = baseTimestamp.AddMinutes(-8.0)
      Beads = { Open = 1; InProgress = 0; Closed = 5 }
      CodingTool = Done
      CodingToolProvider = Some Copilot
      LastUserMessage = Some("add admin role check to delete endpoint", baseTimestamp.AddMinutes(-35.0))
      Pr = HasPr prAuth
      MainBehindCount = 1
      IsDirty = false
      WorkMetrics = Some { CommitCount = 5; LinesAdded = 210; LinesRemoved = 34 }
      HasActiveSession = false
      IsArchived = false }

let private wtArchived: WorktreeStatus =
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

let private wtStreaming: WorktreeStatus =
    { Path = githubPath "streaming"
      Branch = "feature/streaming-agg"
      LastCommitMessage = "Add windowed aggregation with tumbling windows"
      LastCommitTime = baseTimestamp.AddMinutes(-1.0)
      Beads = { Open = 2; InProgress = 2; Closed = 4 }
      CodingTool = Working
      CodingToolProvider = Some Copilot
      LastUserMessage = Some("add tumbling window support", baseTimestamp.AddMinutes(-3.0))
      Pr = HasPr prStreaming
      MainBehindCount = 1
      IsDirty = false
      WorkMetrics = Some { CommitCount = 12; LinesAdded = 567; LinesRemoved = 123 }
      HasActiveSession = true
      IsArchived = false }

let private wtCsvFix: WorktreeStatus =
    { Path = githubPath "csv-fix"
      Branch = "fix/csv-parser"
      LastCommitMessage = "Handle quoted newlines in CSV field parser"
      LastCommitTime = baseTimestamp.AddMinutes(-60.0)
      Beads = { Open = 0; InProgress = 0; Closed = 2 }
      CodingTool = Done
      CodingToolProvider = Some Copilot
      LastUserMessage = None
      Pr = HasPr prCsvMerged
      MainBehindCount = 0
      IsDirty = false
      WorkMetrics = Some { CommitCount = 4; LinesAdded = 45; LinesRemoved = 12 }
      HasActiveSession = false
      IsArchived = false }

// --- Scheduler footer (all 6 categories populated) ---

let private baseLatestByCategory: Map<string, CardEvent> =
    [ "WorktreeList", evt "WorktreeList" $"{azDoEvt}" 18 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 0.3))
      "GitRefresh", evt "GitRefresh" $"{azDoEvt}/feature-retry (3 files)" 10 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 0.8))
      "BeadsRefresh", evt "BeadsRefresh" $"{azDoEvt}/feature-retry" 6 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 0.4))
      "CodingToolRefresh", evt "CodingToolRefresh" "4 agents checked" 4 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 1.2))
      "PrFetch", evt "PrFetch" $"{azDoEvt}" 8 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 1.8))
      "GitFetch", evt "GitFetch" $"{azDoEvt} (2 new commits)" 12 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 2.1)) ]
    |> Map.ofList

let private baseSchedulerEvents: CardEvent list =
    [ evt "GitFetch" "CloudPlatform (fetched 2 commits)" 12 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 2.1))
      evt "GitFetch" "DataPipeline (up to date)" 11 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 0.9))
      evt "PrFetch" "CloudPlatform (4 PRs checked)" 8 (Some StepStatus.Succeeded) (Some(TimeSpan.FromSeconds 1.8)) ]

// --- Card events: only 1 "Running" at a time, rest are recent "Succeeded" ---

let private retryEvt msg secsAgo = evt "claude" msg secsAgo

let private baseSyncStatus: Map<string, CardEvent list> =
    [ $"{azDoEvt}/feature/retry-logic",
      [ retryEvt "Reading BlobStorageClient retry logic" 3 None None ]
      $"{azDoEvt}/refactor/config-loading",
      [ evt "claude" "Extracting config validation rules" 8 None None ]
      $"{azDoEvt}/feature/auth-middleware",
      [ evt "copilot" "All tests passing" 5 None None ]
      $"{githubEvt}/feature/streaming-agg",
      [ evt "copilot" "Implementing tumbling window support" 5 None None ] ]
    |> Map.ofList

// --- Base dashboard ---

let private baseDashboard: DashboardResponse =
    { Repos =
        [ { RepoId = azDoRepoId
            RootFolderName = "CloudPlatform"
            Worktrees = [ wtAzDoMain; wtRetryLogic; wtConfigLoading; wtAuthMiddleware; wtArchived ]
            IsReady = true
            Provider = Some(AzDoProvider "https://dev.azure.com/contoso/CloudPlatform") }
          { RepoId = githubRepoId
            RootFolderName = "DataPipeline"
            Worktrees = [ wtGithubMain; wtStreaming; wtCsvFix ]
            IsReady = true
            Provider = Some(GitHubProvider "https://github.com/contoso/DataPipeline") } ]
      SchedulerEvents = baseSchedulerEvents
      LatestByCategory = baseLatestByCategory
      AppVersion = "demo|0"
      DeployBranch = None
      SystemMetrics = Some { CpuPercent = 42.0; MemoryUsedMb = 14200; MemoryTotalMb = 32768 }
      EditorName = "VS Code" }

let private baseFixture: FixtureData =
    { Worktrees = baseDashboard
      SyncStatus = baseSyncStatus }

// ============================================================
// FRAME SEQUENCE — 12 frames, 24s total
//
// Starting state: 3 Working (retry, config, streaming) + 1 Done (auth)
// Each frame changes ONE card. Distributed over time.
//
// Story arcs:
//   retry-logic: Building → Failed → fix → Rebuilding → Succeeded
//   config:      working steadily, commits accumulate
//   auth:        Done → user prompt → Working → Copilot events → Done (loops)
//   streaming:   steady background work
// ============================================================

// F1 (0-2s): Baseline — see base state above

// F2 (2-4s): Config commits (lines grow)
let private f2 =
    baseFixture
    |> withConfig (fun wt ->
        { wt with
            LastCommitMessage = "Add config schema validation"
            WorkMetrics = Some { CommitCount = 4; LinesAdded = 186; LinesRemoved = 95 } })
    |> withCpu 38.0 14400

// F3 (4-6s): Auth — Copilot starts working (dot changes, event updates)
let private f3 =
    f2
    |> withAuth (fun wt -> { wt with CodingTool = Working })
    |> withCardEvt $"{azDoEvt}/feature/auth-middleware"
        (evt "copilot" "Reading authorization middleware" 1 None None)
    |> withCpu 45.0 14800

// F4 (6-8s): Retry build fails (red badge appears)
let private f4 =
    f3
    |> withRetry (fun wt -> { wt with Pr = HasPr prRetryFailed })
    |> withCardEvt $"{azDoEvt}/feature/retry-logic"
        (retryEvt "CI build failed — analyzing test results" 1 None None)
    |> withCpu 58.0 15200

// F5 (8-10s): Auth copilot progresses
let private f5 =
    f4
    |> withCardEvt $"{azDoEvt}/feature/auth-middleware"
        (evt "copilot" "Generating role-based access checks" 1 None None)
    |> withCpu 72.0 16100

// F6 (10-12s): Retry agent pushes fix, CI rebuilds
let private f6 =
    f5
    |> withRetry (fun wt ->
        { wt with
            LastCommitMessage = "Fix flaky retry test timing"
            Pr = HasPr prRetryRebuilding })
    |> withCardEvt $"{azDoEvt}/feature/retry-logic"
        (retryEvt "Pushed fix, waiting for CI" 1 None None)
    |> withCpu 84.0 17400

// F7 (12-14s): Streaming commits (lines grow)
let private f7 =
    f6
    |> withStream (fun wt ->
        { wt with
            LastCommitMessage = "Add watermark tracking for event windows"
            WorkMetrics = Some { CommitCount = 13; LinesAdded = 612; LinesRemoved = 131 } })
    |> withCpu 68.0 16800

// F8 (14-16s): Auth — Copilot finishes, back to Done (matches base)
let private f8 =
    f7
    |> withAuth (fun wt -> { wt with CodingTool = Done })
    |> withCardEvt $"{azDoEvt}/feature/auth-middleware"
        (evt "copilot" "All tests passing" 5 None None)
    |> withCpu 52.0 15800

// F9 (16-18s): Retry build passes
let private f9 =
    f8
    |> withRetry (fun wt -> { wt with Pr = HasPr prRetrySucceeded })
    |> withCardEvt $"{azDoEvt}/feature/retry-logic"
        (retryEvt "All tests passing — task complete" 1 None None)
    |> withCpu 41.0 15200

// F10 (18-20s): Config commits again
let private f10 =
    f9
    |> withConfig (fun wt ->
        { wt with
            LastCommitMessage = "Simplify config provider chain"
            WorkMetrics = Some { CommitCount = 5; LinesAdded = 221; LinesRemoved = 108 } })
    |> withCpu 35.0 14600

// F11 (20-22s): Retry picks up new task
let private f11 =
    f10
    |> withRetry (fun wt ->
        { wt with LastUserMessage = Some("add request deduplication", baseTimestamp.AddMinutes(-1.0)) })
    |> withCardEvt $"{azDoEvt}/feature/retry-logic"
        (retryEvt "Starting next task: request deduplication" 1 None None)
    |> withCpu 39.0 14400

// F12 (22-24s): Retry beads update, settling toward start
let private f12 =
    f11
    |> withRetry (fun wt ->
        { wt with Beads = { Open = 2; InProgress = 1; Closed = 6 } })
    |> withCpu 42.0 14200

// --- Frame list ---

let private frames: DemoFrame list =
    [ { Data = baseFixture; DurationSeconds = 2 }
      { Data = f2; DurationSeconds = 2 }
      { Data = f3; DurationSeconds = 2 }
      { Data = f4; DurationSeconds = 2 }
      { Data = f5; DurationSeconds = 2 }
      { Data = f6; DurationSeconds = 2 }
      { Data = f7; DurationSeconds = 2 }
      { Data = f8; DurationSeconds = 2 }
      { Data = f9; DurationSeconds = 2 }
      { Data = f10; DurationSeconds = 2 }
      { Data = f11; DurationSeconds = 2 }
      { Data = f12; DurationSeconds = 2 } ]

let private totalDurationSeconds =
    frames |> List.sumBy _.DurationSeconds

// --- Timestamp adjustment ---

let private adjustWorktreeTimestamps (now: DateTimeOffset) (wt: WorktreeStatus) =
    let shift = now - baseTimestamp
    { wt with
        LastCommitTime = wt.LastCommitTime + shift
        LastUserMessage =
            wt.LastUserMessage
            |> Option.map (fun (msg, ts) -> msg, ts + shift) }

let private adjustEventTimestamp (now: DateTimeOffset) (e: CardEvent) =
    let shift = now - baseTimestamp
    { e with Timestamp = e.Timestamp + shift }

let adjustTimestamps (now: DateTimeOffset) (fixture: FixtureData) : FixtureData =
    let adjustEvent = adjustEventTimestamp now
    let adjustWt = adjustWorktreeTimestamps now
    { Worktrees =
        { fixture.Worktrees with
            Repos =
                fixture.Worktrees.Repos
                |> List.map (fun repo ->
                    { repo with Worktrees = repo.Worktrees |> List.map adjustWt })
            SchedulerEvents =
                fixture.Worktrees.SchedulerEvents |> List.map adjustEvent
            LatestByCategory =
                fixture.Worktrees.LatestByCategory |> Map.map (fun _ e -> adjustEvent e) }
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
