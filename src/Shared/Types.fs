namespace Shared

open System

module TestFailureLog =
    let [<Literal>] relPath = ".agents/tests-failure.log"

type RepoId = RepoId of string

module RepoId =
    let create (path: string) = RepoId path
    let value (RepoId id) = id

type WorktreePath = WorktreePath of string

module WorktreePath =
    let create (path: string) = WorktreePath path
    let value (WorktreePath p) = p

type BranchName = BranchName of string

module BranchName =
    let create (name: string) = BranchName name
    let value (BranchName b) = b

type BeadsSummary =
    { Open: int
      InProgress: int
      Closed: int }

module BeadsSummary =
    let zero = { Open = 0; InProgress = 0; Closed = 0 }

type CodingToolStatus =
    | Working
    | WaitingForUser
    | Done
    | Idle

type CodingToolProvider =
    | Claude
    | Copilot

type BuildStatus =
    | Building
    | Succeeded
    | Failed
    | PartiallySucceeded
    | Canceled

type CommentSummary =
    | WithResolution of unresolved: int * total: int

type BuildFailure =
    { StepName: string
      Log: string }

type BuildInfo =
    { Name: string
      Status: BuildStatus
      Url: string option
      Failure: BuildFailure option }

type PrStatus =
    | NoPr
    | HasPr of PrInfo

and PrInfo =
    { Id: int
      Title: string
      Url: string
      IsDraft: bool
      Comments: CommentSummary
      Builds: BuildInfo list
      IsMerged: bool
      HasConflicts: bool }

type WorkMetrics =
    { CommitCount: int
      LinesAdded: int
      LinesRemoved: int }

type ActionKind =
    | FixPr of url: string
    | FixBuild of url: string
    | CreatePr
    | FixTests
    | ConfigureTests

type ActionRequest =
    { Path: WorktreePath
      Action: ActionKind }

type LaunchRequest =
    { Path: WorktreePath
      Prompt: string }

type CreateWorktreeRequest =
    { RepoId: string
      BranchName: BranchName
      BaseBranch: BranchName }

type WorktreeStatus =
    { Path: WorktreePath
      Branch: string
      LastCommitMessage: string
      LastCommitTime: DateTimeOffset
      Beads: BeadsSummary
      CodingTool: CodingToolStatus
      CodingToolProvider: CodingToolProvider option
      LastUserMessage: (string * DateTimeOffset) option
      Pr: PrStatus
      MainBehindCount: int
      IsDirty: bool
      WorkMetrics: WorkMetrics option
      HasActiveSession: bool
      HasTestFailureLog: bool
      IsArchived: bool }

[<RequireQualifiedAccess>]
type StepStatus =
    | Pending
    | Running
    | Succeeded
    | Failed of message: string
    | Cancelled
    | NotConfigured

module EventSource =
    let [<Literal>] Test = "Test"

type CardEvent =
    { Source: string
      Message: string
      Timestamp: DateTimeOffset
      Status: StepStatus option
      Duration: TimeSpan option }

type RepoProvider =
    | GitHubProvider of url: string
    | AzDoProvider of url: string
    | UnknownProvider

type RepoWorktrees =
    { RepoId: RepoId
      RootFolderName: string
      Worktrees: WorktreeStatus list
      IsReady: bool
      Provider: RepoProvider option }

type SystemMetrics =
    { CpuPercent: float
      MemoryUsedMb: int
      MemoryTotalMb: int }

type DashboardResponse =
    { Repos: RepoWorktrees list
      SchedulerEvents: CardEvent list
      LatestByCategory: Map<string, CardEvent>
      AppVersion: string
      DeployBranch: string option
      SystemMetrics: SystemMetrics option
      EditorName: string }

type FixtureData =
    { Worktrees: DashboardResponse
      SyncStatus: Map<string, CardEvent list> }

type IWorktreeApi =
    { getWorktrees: unit -> Async<DashboardResponse>
      openTerminal: WorktreePath -> Async<unit>
      openEditor: WorktreePath -> Async<unit>
      startSync: WorktreePath -> Async<Result<unit, string>>
      cancelSync: WorktreePath -> Async<unit>
      getSyncStatus: unit -> Async<Map<string, CardEvent list>>
      deleteWorktree: WorktreePath -> Async<Result<unit, string>>
      launchSession: LaunchRequest -> Async<Result<unit, string>>
      focusSession: WorktreePath -> Async<Result<unit, string>>
      killSession: WorktreePath -> Async<Result<unit, string>>
      archiveWorktree: WorktreePath -> Async<Result<unit, string>>
      unarchiveWorktree: WorktreePath -> Async<Result<unit, string>>
      getBranches: string -> Async<string list>
      createWorktree: CreateWorktreeRequest -> Async<Result<unit, string>>
      openNewTab: WorktreePath -> Async<Result<unit, string>>
      launchAction: ActionRequest -> Async<Result<unit, string>> }
