namespace Shared

open System

type BeadsSummary =
    { Open: int
      InProgress: int
      Closed: int }

module BeadsSummary =
    let zero = { Open = 0; InProgress = 0; Closed = 0 }

type ClaudeCodeStatus =
    | Working
    | WaitingForUser
    | Done
    | Idle

type BuildStatus =
    | Building
    | Succeeded
    | Failed
    | PartiallySucceeded
    | Canceled

type ThreadCounts =
    { Unresolved: int
      Total: int }

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
      ThreadCounts: ThreadCounts
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
      Claude: ClaudeCodeStatus
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

type WorktreeResponse =
    { RootFolderName: string
      Worktrees: WorktreeStatus list
      IsReady: bool
      SchedulerEvents: CardEvent list
      AppVersion: string }

type IWorktreeApi =
    { getWorktrees: unit -> Async<WorktreeResponse>
      openTerminal: string -> Async<unit>
      startSync: string -> Async<Result<unit, string>>
      cancelSync: string -> Async<unit>
      getSyncStatus: unit -> Async<Map<string, CardEvent list>>
      deleteWorktree: string -> Async<Result<unit, string>> }
