namespace Shared

open System

type BeadsSummary =
    { Open: int
      InProgress: int
      Closed: int }

type ClaudeCodeStatus =
    | Active
    | Recent
    | Idle
    | Unknown

type BuildStatus =
    | NoBuild
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

type WorktreeStatus =
    { Path: string
      Branch: string
      Head: string
      LastCommitMessage: string
      LastCommitTime: DateTimeOffset
      UpstreamBranch: string option
      Beads: BeadsSummary
      Claude: ClaudeCodeStatus
      Pr: PrStatus
      IsStale: bool
      MainBehindCount: int }

type WorktreeResponse =
    { RootFolderName: string
      Worktrees: WorktreeStatus list }

type IWorktreeApi =
    { getWorktrees: unit -> Async<WorktreeResponse>
      openTerminal: string -> Async<unit> }
