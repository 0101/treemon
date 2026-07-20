namespace Shared

open System

module TestFailureLog =
    let [<Literal>] relPath = ".agents/tests-failure.log"

type RepoId = RepoId of string

module RepoId =
    let value (RepoId id) = id

type WorktreePath = WorktreePath of string

module WorktreePath =
    let value (WorktreePath p) = p

    let displayName (WorktreePath p) =
        let i = max (p.LastIndexOf '/') (p.LastIndexOf '\\')
        if i < 0 then p else p[i + 1..]

type BranchName = BranchName of string

module BranchName =
    let create (name: string) = BranchName name
    let value (BranchName b) = b

type BeadsSummary =
    { Open: int
      InProgress: int
      Blocked: int
      Closed: int }

module BeadsSummary =
    let zero = { Open = 0; InProgress = 0; Blocked = 0; Closed = 0 }

/// Server-side split of a worktree's OPEN beads tasks by their direct parent-feature status,
/// the source of the band's started-vs-awaiting signal:
///   - Planned: open task under an OPEN feature (planning done, awaiting go-ahead)
///   - Queued:  open task under an IN_PROGRESS feature (execution underway, next-up)
///   - Loose:   open task with no/closed/blocked feature parent, or a non-feature parent
/// Loose is kept distinct server-side for fidelity but folds into Planned for display.
/// (FeaturesOpen/FeaturesWip were deliberately dropped — the v1 band shows no feature counts.)
type BeadsPlanning =
    { Planned: int
      Queued: int
      Loose: int }

module BeadsPlanning =
    let zero = { Planned = 0; Queued = 0; Loose = 0 }

type CodingToolStatus =
    | Working
    | WaitingForUser
    | Idle
    | NoSession

/// The coding tool driving a worktree — its launcher, prompt format, and push-status source. A DU of
/// one today: only the Copilot CLI can push its live status. Adding a provider (e.g. a future GitHub
/// App) is a new case; the compiler then flags every provider-specific branch that must handle it.
type CodingToolProvider =
    | CopilotCli
    static member Default = CopilotCli

/// Live-agent activity buckets derived from the skill/command an agent is running,
/// surfaced by the same session scan that drives the red dot. Working is the fallback for
/// an active session with no recognized skill. Activity is always *derived* from CurrentSkill
/// via Activity.classify — never stored separately — so the overview band derives it from one
/// source of truth.
[<RequireQualifiedAccess>]
type CurrentActivity =
    | Investigating
    | Planning
    | Executing
    | Reviewing
    | PR
    | Working

module Activity =
    // First whitespace-delimited token — a Claude slash command can carry args
    // (ClaudeDetector surfaces "<cmd> <args>", e.g. "pr https://..."), so only the command
    // itself is significant. Split never yields an empty array.
    let private firstToken (s: string) =
        s.Split([| ' '; '\t'; '\n'; '\r' |])[0]

    /// Classify a running skill/command name into an activity bucket, per the
    /// beads-overview-band spec table. The name is normalized first — trimmed, reduced to its
    /// first token, stripped of a leading '/' (Claude slash commands), and lower-cased — so a
    /// CLI event name, a Claude slash command and a VS Code tool-call name all map uniformly.
    /// Unknown or empty/whitespace input falls back to Working.
    /// Lives in Shared so server (card stripe) and client (band) classify identically.
    let classify (skill: string) : CurrentActivity =
        let normalized =
            if String.IsNullOrWhiteSpace skill then ""
            else (firstToken (skill.Trim())).TrimStart('/').ToLowerInvariant()

        match normalized with
        | "investigate" | "research" -> CurrentActivity.Investigating
        | "bd-plan" | "bd-improve" | "bd-autoimprove" -> CurrentActivity.Planning
        | "execute" | "bd-execute" | "bd-phase" | "bd-autopilot" | "refactor" -> CurrentActivity.Executing
        | "review-branch" | "reviewing-tests" | "comprehensive-review"
        | "code-review" | "bd-review" | "contribution"
        | "review" | "focused-review:review" -> CurrentActivity.Reviewing
        | "babysit-pr" | "pr" | "github" | "fix-build" -> CurrentActivity.PR
        | _ -> CurrentActivity.Working

[<RequireQualifiedAccess>]
type ActivityLevel =
    | Active
    | Idle
    | DeepIdle

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

[<RequireQualifiedAccess>]
type CanvasPosition =
    | Left
    | Right
    | Top
    | Bottom

/// Relative size of the canvas pane vs the dashboard when the pane is open.
/// Ratio1To1 (default) splits the layout evenly; Ratio2To1 gives the canvas
/// twice the dashboard's share.
[<RequireQualifiedAccess>]
type CanvasSize =
    | Ratio1To1
    | Ratio2To1

type CanvasDocKind =
    | AgentDoc      // authored & owned by a session; interactive; file-driven
    | SystemView    // server-generated; data-driven; no owner (e.g. the beads dashboard)

module CanvasDocKind =
    /// Classify a canvas doc by filename. System views are server-generated, data-driven docs
    /// with no owner session; currently only the beads dashboard (beads.html) is one. This is the
    /// single place to register future generated views (e.g. a CI/build view), keeping the
    /// discriminator out of CanvasScanner and CanvasDocServer.
    let classify (filename: string) : CanvasDocKind =
        match filename.ToLowerInvariant() with
        | "beads.html" -> SystemView
        | _ -> AgentDoc

type CanvasDoc =
    { Filename: string
      ContentHash: string
      LastModified: DateTimeOffset
      OwnerSessionId: string option
      Kind: CanvasDocKind }

/// Single source of truth for the canvas-session launch prompt, shared by the client
/// (LaunchCanvasSession) and the server (sendCanvasMessage) so the two cannot drift.
module CanvasPrompt =

    /// Prompt handed to the coding tool to (re)start work on an existing canvas doc.
    let continueWorking (worktreePath: string) (filename: string) =
        // On-disk path of the canvas doc within the worktree. Forward slashes are used
        // deliberately: they work on Windows, Linux and macOS, and src/Shared is
        // Fable-compiled to JS so System.IO.Path.Combine is not available here.
        $"Continue working on canvas doc: {worktreePath}/.agents/canvas/{filename}\n"
        + "This is an HTML file served at localhost:5002. Edits are live-reloaded in the canvas pane."

type CanvasMessageRequest =
    { WorktreePath: WorktreePath
      Filename: string
      Payload: string }

[<RequireQualifiedAccess>]
type CanvasMessageResult =
    | Ok
    | Error of string
    | Queued

type BridgeLiveness =
    { IsAlive: bool
      SessionId: string option }

type ActionKind =
    | FixPr of url: string
    | FixBuild of url: string
    | CreatePr
    | FixTests
    | ConfigureTests
    | CanvasSession of prompt: string

type ActionRequest =
    { Path: WorktreePath
      Action: ActionKind }

type LaunchRequest =
    { Path: WorktreePath
      Prompt: string }

type CreateWorktreeRequest =
    { RepoId: string
      BranchName: BranchName
      BaseBranch: BranchName
      Prompt: string option
      /// Which skill wraps the prompt on launch. `None` sends the prompt verbatim
      /// (no skill); `Some name` wraps it via a provider-aware skill invocation.
      Skill: string option }

/// Non-fatal advisories surfaced after a worktree is created (e.g. a legacy fork
/// script is present, or the post-fork setup hook failed). Empty means a clean create.
type CreateWorktreeWarnings = string list

type WorktreeStatus =
    { Path: WorktreePath
      Branch: string
      LastCommitMessage: string
      LastCommitTime: DateTimeOffset
      Beads: BeadsSummary
      Planning: BeadsPlanning
      CodingTool: CodingToolStatus
      CodingToolProvider: CodingToolProvider option
      /// When the agent entered its current Overview category — its classified activity while Working
      /// (Investigating/Executing/…), else its status (WaitingForUser/Idle). Recorded at the transition
      /// so the Overview band can show "time in category" (incl. time-since-idle). None when NoSession.
      CodingToolSince: DateTimeOffset option
      CurrentSkill: string option
      LastUserMessage: (string * DateTimeOffset) option
      Pr: PrStatus
      MainBehindCount: int
      IsDirty: bool
      WorkMetrics: WorkMetrics option
      HasActiveSession: bool
      HasTestFailureLog: bool
      IsMainWorktree: bool
      IsArchived: bool
      CanvasDocs: CanvasDoc list }

module WorktreeStatus =
    let [<Literal>] DetachedBranchName = "(detached)"

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
    let [<Literal>] Sync = "sync"
    let [<Literal>] PostFork = "post-fork"

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
      Provider: RepoProvider option
      BaseBranch: string }

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
      EditorName: string
      /// Skills offered in the create-worktree modal (machine-level `worktreeSkills`
      /// config). Empty means only the built-in "None" option is available.
      WorktreeSkills: string list
      CollapsedRepos: Set<RepoId>
      CanvasPaneOpen: bool
      OverviewPanelOpen: bool
      CanvasPosition: CanvasPosition
      CanvasSize: CanvasSize }

type FixtureData =
    { Worktrees: DashboardResponse
      SyncStatus: Map<string, CardEvent list> }

type ArchiveCanvasDocRequest =
    { WorktreePath: WorktreePath
      Filename: string }

type ShareCanvasDocRequest =
    { WorktreePath: WorktreePath
      Filename: string }

/// Result of publishing a canvas doc: the per-doc read-only SAS URL plus the doc's title
/// (extracted server-side from the HTML) so the client can build the rich clipboard link
/// without re-parsing.
type CanvasShareResult =
    { Url: string
      Title: string }

// IWorktreeApi (the Fable.Remoting contract) lives in WorktreeApi.fs, compiled after OverviewData.fs
// so it can name OverviewData.OverviewSnapshot in getOverviewHistory.
