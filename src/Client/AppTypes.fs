module AppTypes

// Foundation module for the dashboard client: holds the Elmish `Model` and `Msg`
// plus the shared plumbing (worktree API proxy, `findWorktree`, `saveCollapsedReposCmd`)
// that both `App.fs` and the canvas update arms depend on. Compiled before
// `CanvasUpdate.fs` so the canvas update logic can be extracted out of `App.fs`
// without forming a cyclic reference. This is field/type relocation only — `update`
// remains a single function in `App.fs` (no Cmd.map sub-component split).

open Shared
open Shared.EventUtils
open Navigation
open OverviewData
open OverviewPresentation
open Elmish
open Fable.Remoting.Client

type OverviewHistoryRequest =
    { Window: HistoryWindow
      RequestedAt: System.DateTimeOffset }

type InstalledOverviewHistory =
    { Window: HistoryWindow
      Response: OverviewHistoryResponse }

type Model =
    { Repos: RepoModel list
      IsLoading: bool
      HasError: bool
      SortMode: SortMode
      IsCompact: bool
      SchedulerEvents: CardEvent list
      LatestByCategory: Map<string, CardEvent>
      BranchEvents: Map<string, CardEvent list>
      SyncPending: Set<string>
      AppVersion: string option
      EditorName: string
      WorktreeSkills: string list
      FocusedElement: FocusTarget option
      CreateModal: CreateWorktreeModal.ModalState
      ConfirmModal: ConfirmModal.ConfirmModal
      DeletedPaths: Set<string>
      DeployBranch: string option
      SystemMetrics: SystemMetrics option
      ActionCooldowns: Set<WorktreePath>
      Activity: ActivityState.ActivityState
      Mascot: MascotState.MascotState
      Canvas: CanvasState.CanvasState
      OverviewPanelOpen: bool
      SelectedOverviewGroup: OverviewSelection option
      // None is the client-only Hidden state; only a concrete shared HistoryWindow can cross the API.
      // The response anchor drives normal refresh cadence. RequestedAt provides failure retry backoff,
      // while the request identity prevents overlapping polls and stale completions.
      OverviewHistoryWindow: HistoryWindow option
      OverviewHistory: InstalledOverviewHistory option
      OverviewHistoryRequestedAt: System.DateTimeOffset
      OverviewHistoryRequestInFlight: OverviewHistoryRequest option }

type Msg =
    | DataLoaded of DashboardResponse * now: System.DateTimeOffset
    | DataFailed of exn
    | ToggleSort
    | ToggleCompact
    | ToggleCollapse of repoId: RepoId
    | Tick of now: float
    | OpenTerminal of WorktreePath
    | OpenEditor of WorktreePath
    | StartSync of path: WorktreePath * scopedKey: string
    | SyncStarted of key: string * Result<unit, string>
    | SyncStatusUpdate of Map<string, CardEvent list>
    | CancelSync of WorktreePath
    | SyncTick
    | ConfirmDeleteWorktree of scopedKey: string
    | ConfirmArchiveWorktree of scopedKey: string
    | ConfirmMsg of ConfirmModal.Msg
    | SessionKilledForDelete of path: WorktreePath
    | SessionKilledForArchive of path: WorktreePath
    | DeleteCompleted of Result<unit, string>
    | FocusSession of path: WorktreePath
    | OpenNewTab of path: WorktreePath
    | SessionResult of Result<unit, string>
    | KeyPressed of key: string * hasModifier: bool
    | SetFocus of FocusTarget option
    | SetFocusNoRetarget of FocusTarget option
    | ArchiveMsg of ArchiveViews.Msg
    | LaunchAction of path: WorktreePath * action: ActionKind
    | LaunchActionResult of Result<unit, string>
    | ClearActionCooldown of WorktreePath
    | ResumeSession of WorktreePath
    | ModalMsg of CreateWorktreeModal.Msg
    | UserActivity of now: float
    | ToggleCanvasPane
    | ToggleOverviewPanel
    // Overview drill-down (spec: docs/spec/overview-drilldown.md). SelectOverviewGroup toggles the
    // clicked group's breakdown panel (re-selecting the current group clears it). SelectOverviewWorktree
    // is the arrow-nav-parity handler: it focuses/expands/scrolls the clicked member card WITHOUT
    // opening the Canvas pane (the deliberate difference from FocusOverviewCard).
    | SelectOverviewGroup of OverviewSelection
    | SelectOverviewWorktree of scopedKey: string
    // In-band history chart (spec: docs/spec/overview-activity-history.md). CycleOverviewChart advances
    // Hidden -> 12h -> 24h -> 72h -> Hidden. The requested window travels with the response so a slower
    // request for a previous selection can be ignored. The request identity also distinguishes
    // separate requests for the same window when the user cycles away and back.
    | CycleOverviewChart of now: System.DateTimeOffset
    | OverviewHistoryLoaded of request: OverviewHistoryRequest * response: OverviewHistoryResponse option
    | SetCanvasPosition of CanvasPosition
    | SetCanvasSize of CanvasSize
    | SelectCanvasDoc of scopedKey: string * filename: string
    | FocusOverviewCard of scopedKey: string
    | OpenCanvasDoc of scopedKey: string * filename: string
    | ArchiveCanvasDoc of scopedKey: string * filename: string
    | ArchiveCanvasDocResult of scopedKey: string * filename: string * Result<unit, string>
    // Share the focused AgentDoc: publish it (server mints a per-doc read-only SAS URL + returns the
    // doc title) then write a rich clipboard link. ShareCanvasDocResult carries the CanvasShareResult
    // on Ok (→ dual-format clipboard write, deferring the banner to ClipboardWriteResult) or an error
    // message on failure (→ the existing error banner). ClipboardWriteResult reports whether the async
    // navigator.clipboard.write actually landed — Ok raises "Shared — link copied", Error raises a
    // "copy it manually: <url>" banner — so the success notice never claims a copy that a rejected
    // write (lost transient activation / defocused document across the share round-trip, a revoked
    // permission, or an unsupported clipboard API) never made. DismissShareNotice clears the banner.
    | ShareCanvasDoc of scopedKey: string * filename: string
    | ShareCanvasDocResult of scopedKey: string * filename: string * Result<CanvasShareResult, string>
    | ClipboardWriteResult of url: string * Result<unit, string>
    | DismissShareNotice
    | NavigateCanvasDoc of filename: string
    | CanvasMessageReceived of payload: string
    | CanvasSendResult of CanvasMessageResult * scopedKey: string
    | DismissCanvasMessageError
    // Doc-side JS error forwarded from an AgentDoc iframe (window.onerror / unhandledrejection via
    // the injected errorOverlayScript). `scopedKey`+`filename` are the emitting worktree + doc
    // (carried in the postMessage `wt`/`doc` fields) so the reducer stamps the error with the doc that
    // actually threw, not the active tab. Carried as its own message + model field, distinct from
    // CanvasSendState.Failed (which is a pane→session *delivery* failure, a different source).
    | CanvasDocError of scopedKey: string * filename: string * message: string
    // A canvas doc posted a valid object with no usable top-level string `action`, so CanvasPane could
    // not route it. Surfaced (not silently dropped) via the doc-error banner, attributed to the active
    // visible doc.
    | CanvasMalformedDocMessage
    | DismissCanvasDocError
    | MarkDocViewed of scopedKey: string * filename: string
    | LoadLastViewedHashes of Map<string, Map<string, string>>
    | BridgeLivenessLoaded of Map<string, BridgeLiveness>
    | LaunchCanvasSession of scopedKey: string
    | MorphActiveDoc
    | MorphComplete
    | NoOp

// Deferred so that merely loading the AppTypes module does not run buildProxy.
// buildProxy uses reflection that throws under the .NET test host; building it
// lazily means it runs only on first remoting call (in the Fable/JS client),
// while tests that reference the client's pure helpers no longer trip the static ctor.
// Lazy caches the built proxy, so all call sites still share one instance.
let worktreeApi : Lazy<IWorktreeApi> =
    lazy (Remoting.createApi ()
          |> Remoting.buildProxy<IWorktreeApi>)

let findWorktree (scopedKey: string) (model: Model) =
    model.Repos
    |> List.tryPick (fun r ->
        r.Worktrees |> List.tryFind (fun wt -> WorktreePath.value wt.Path = scopedKey))

let saveCollapsedReposCmd (repos: RepoModel list) : Cmd<Msg> =
    let collapsedRepos = repos |> List.filter _.IsCollapsed |> List.map _.RepoId
    Cmd.OfAsync.attempt worktreeApi.Value.saveCollapsedRepos collapsedRepos (fun _ -> NoOp)
