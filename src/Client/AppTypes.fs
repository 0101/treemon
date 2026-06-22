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
open Elmish
open Fable.Remoting.Client

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
      EyeDirection: float * float
      FocusedElement: FocusTarget option
      CreateModal: CreateWorktreeModal.ModalState
      ConfirmModal: ConfirmModal.ConfirmModal
      DeletedPaths: Set<string>
      DeployBranch: string option
      SystemMetrics: SystemMetrics option
      ActionCooldowns: Set<WorktreePath>
      LastActivityTime: float
      ActivityLevel: ActivityLevel
      Canvas: CanvasState.CanvasState }

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
    | ArchiveMsg of ArchiveViews.Msg
    | LaunchAction of path: WorktreePath * action: ActionKind
    | LaunchActionResult of Result<unit, string>
    | ClearActionCooldown of WorktreePath
    | ResumeSession of WorktreePath
    | ModalMsg of CreateWorktreeModal.Msg
    | UserActivity of now: float
    | ToggleCanvasPane
    | SetCanvasPosition of CanvasPosition
    | SelectCanvasDoc of scopedKey: string * filename: string
    | FocusOverviewCard of scopedKey: string
    | OpenCanvasDoc of scopedKey: string * filename: string
    | ArchiveCanvasDoc of scopedKey: string * filename: string
    | ArchiveCanvasDocResult of scopedKey: string * filename: string * Result<unit, string>
    | NavigateCanvasDoc of filename: string
    | CanvasMessageReceived of payload: string
    | CanvasSendResult of CanvasMessageResult * scopedKey: string
    | DismissCanvasMessageError
    // Doc-side JS error forwarded from an AgentDoc iframe (window.onerror / unhandledrejection via
    // the injected errorOverlayScript). Carried as its own message + model field, distinct from
    // CanvasSendState.Failed (which is a pane→session *delivery* failure, a different source).
    | CanvasDocError of message: string
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
