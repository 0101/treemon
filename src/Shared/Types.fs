namespace Shared

open System

type RepoId = RepoId of string

module RepoId =
    let create (path: string) = RepoId path
    let value (RepoId id) = id

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
    | CountOnly of total: int

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
      IsMerged: bool }

type WorkMetrics =
    { CommitCount: int
      LinesAdded: int
      LinesRemoved: int }

type WorktreeStatus =
    { Path: string
      Branch: string
      LastCommitMessage: string
      LastCommitTime: DateTimeOffset
      Beads: BeadsSummary
      CodingTool: CodingToolStatus
      CodingToolProvider: CodingToolProvider option
      Pr: PrStatus
      MainBehindCount: int
      IsDirty: bool
      WorkMetrics: WorkMetrics option }

[<RequireQualifiedAccess>]
type StepStatus =
    | Pending
    | Running
    | Succeeded
    | Failed of message: string
    | Cancelled

type CardEvent =
    { Source: string
      Message: string
      Timestamp: DateTimeOffset
      Status: StepStatus option
      Duration: TimeSpan option }

type RepoWorktrees =
    { RepoId: RepoId
      RootFolderName: string
      Worktrees: WorktreeStatus list
      IsReady: bool }

type DashboardResponse =
    { Repos: RepoWorktrees list
      SchedulerEvents: CardEvent list
      LatestByCategory: Map<string, CardEvent>
      AppVersion: string }

type IWorktreeApi =
    { getWorktrees: unit -> Async<DashboardResponse>
      openTerminal: string -> Async<unit>
      startSync: string -> Async<Result<unit, string>>
      cancelSync: string -> Async<unit>
      getSyncStatus: unit -> Async<Map<string, CardEvent list>>
      deleteWorktree: string -> Async<Result<unit, string>> }
