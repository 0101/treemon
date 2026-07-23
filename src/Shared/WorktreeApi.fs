namespace Shared

open System

/// The Fable.Remoting contract between client and server. Kept in its own file (not Types.fs) purely
/// for compile order: `getOverviewHistory` uses the history types in `OverviewData`, and
/// `OverviewData` is compiled AFTER `Types.fs`, so the interface must live after it to name that type.
/// Everything else it references (requests/results, WorktreePath, etc.) is defined earlier in Types.fs.
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
      createWorktree: CreateWorktreeRequest -> Async<Result<CreateWorktreeWarnings, string>>
      openNewTab: WorktreePath -> Async<Result<unit, string>>
      launchAction: ActionRequest -> Async<Result<unit, string>>
      reportActivity: ActivityLevel -> Async<unit>
      saveCollapsedRepos: RepoId list -> Async<unit>
      saveCanvasPaneOpen: bool -> Async<unit>
      saveOverviewPanelOpen: bool -> Async<unit>
      saveCanvasPosition: CanvasPosition -> Async<unit>
      saveCanvasSize: CanvasSize -> Async<unit>
      resumeSession: WorktreePath -> Async<Result<unit, string>>
      sendCanvasMessage: CanvasMessageRequest -> Async<CanvasMessageResult>
      archiveCanvasDoc: ArchiveCanvasDocRequest -> Async<Result<unit, string>>
      shareCanvasDoc: ShareCanvasDocRequest -> Async<Result<CanvasShareResult, string>>
      saveLastViewedHashes: Map<string, Map<string, string>> -> Async<unit>
      loadLastViewedHashes: unit -> Async<Map<string, Map<string, string>>>
      getBridgeLiveness: string list -> Async<Map<string, BridgeLiveness>>
      addRoot: string -> Async<Result<unit, string>>
      removeRoot: string -> Async<Result<unit, string>>
      getRoots: unit -> Async<string list>
      /// Count-only Overview history for the explicitly requested window, anchored to the server
      /// computation instant so every caller renders identical timeline edges.
      getOverviewHistory: OverviewData.HistoryWindow -> Async<OverviewData.OverviewHistoryResponse> }
