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

type PrStatus =
    | NoPr
    | HasPr of PrInfo

and PrInfo =
    { Id: int
      Title: string
      Url: string
      IsDraft: bool
      ReviewerVotes: Map<string, int>
      UnresolvedThreadCount: int
      BuildStatus: BuildStatus }

type WorktreeStatus =
    { Branch: string
      Head: string
      LastCommitMessage: string
      LastCommitTime: DateTimeOffset
      UpstreamBranch: string option
      Beads: BeadsSummary
      Claude: ClaudeCodeStatus
      Pr: PrStatus
      IsStale: bool }

type IWorktreeApi =
    { getWorktrees: unit -> Async<WorktreeStatus list> }
