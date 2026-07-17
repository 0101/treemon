module Tests.WorktreeFixtures

open System
open Shared

/// A neutral WorktreeStatus fixture: every field at its zero / idle / default value. Tests build the
/// case under test with `{ baseWt with ... }`, overriding only the fields they exercise. Shared so
/// the ~20-field WorktreeStatus literal lives in exactly one place (used by the Overview band and
/// card-view fixtures).
let baseWt: WorktreeStatus =
    { Path = WorktreePath "/wt"
      Branch = "b"
      LastCommitMessage = "m"
      LastCommitTime = DateTimeOffset.UnixEpoch
      Beads = BeadsSummary.zero
      Planning = BeadsPlanning.zero
      CodingTool = CodingToolStatus.Idle
      CodingToolProvider = None
      CodingToolSince = None
      CurrentSkill = None
      ContextUsage = None
      LastUserMessage = None
      Pr = PrStatus.NoPr
      MainBehindCount = 0
      IsDirty = false
      WorkMetrics = None
      HasActiveSession = false
      HasTestFailureLog = false
      IsMainWorktree = false
      IsArchived = false
      CanvasDocs = [] }
