module Server.RefreshScheduler

open System
open System.Diagnostics
open System.Threading
open Shared
open Shared.EventUtils

type DashboardState =
    { WorktreeList: GitWorktree.WorktreeInfo list
      GitData: Map<string, GitWorktree.GitData>
      BeadsData: Map<string, BeadsSummary>
      PrData: Map<string, PrStatus>
      SchedulerEvents: CardEvent list
      PinnedErrors: Map<string * string, CardEvent>
      IsReady: bool }

module DashboardState =
    let empty =
        { WorktreeList = []
          GitData = Map.empty
          BeadsData = Map.empty
          PrData = Map.empty
          SchedulerEvents = []
          PinnedErrors = Map.empty
          IsReady = false }

type StateMsg =
    | UpdateWorktreeList of GitWorktree.WorktreeInfo list
    | UpdateGit of path: string * GitWorktree.GitData
    | UpdateBeads of path: string * BeadsSummary
    | UpdatePr of Map<string, PrStatus>
    | RemoveWorktree of path: string
    | GetState of AsyncReplyChannel<DashboardState>
    | LogSchedulerEvent of CardEvent

let private maxEvents = 50

let private knownPaths (state: DashboardState) =
    state.WorktreeList
    |> List.choose (fun wt -> Some wt.Path)
    |> Set.ofList

let private trimEvents (events: CardEvent list) =
    events
    |> List.sortByDescending (fun e -> e.Timestamp)
    |> List.truncate maxEvents

let private updatePinnedErrors (errors: Map<string * string, CardEvent>) (event: CardEvent) =
    let key = eventKey event
    match event.Status with
    | Some (StepStatus.Failed _) -> errors |> Map.add key event
    | Some StepStatus.Succeeded -> errors |> Map.remove key
    | _ -> errors

let private removeWorktreeData (path: string) (state: DashboardState) =
    { state with
        WorktreeList = state.WorktreeList |> List.filter (fun wt -> wt.Path <> path)
        GitData = state.GitData |> Map.remove path
        BeadsData = state.BeadsData |> Map.remove path }

let private processMessage (state: DashboardState) (msg: StateMsg) =
    match msg with
    | UpdateWorktreeList worktrees ->
        let newPaths = worktrees |> List.choose (fun wt -> Some wt.Path) |> Set.ofList
        let oldPaths = knownPaths state
        let removedPaths = Set.difference oldPaths newPaths

        let cleaned =
            removedPaths
            |> Set.fold (fun s path -> removeWorktreeData path s) state

        { cleaned with
            WorktreeList = worktrees
            IsReady = true }

    | UpdateGit(path, gitData) ->
        let paths = knownPaths state

        match Set.contains path paths with
        | true -> { state with GitData = state.GitData |> Map.add path gitData }
        | false -> state

    | UpdateBeads(path, beads) ->
        let paths = knownPaths state

        match Set.contains path paths with
        | true -> { state with BeadsData = state.BeadsData |> Map.add path beads }
        | false -> state

    | UpdatePr prMap ->
        { state with PrData = prMap }

    | RemoveWorktree path ->
        removeWorktreeData path state

    | GetState replyChannel ->
        replyChannel.Reply(state)
        state

    | LogSchedulerEvent event ->
        { state with
            SchedulerEvents = trimEvents (event :: state.SchedulerEvents)
            PinnedErrors = updatePinnedErrors state.PinnedErrors event }

let createAgent () =
    MailboxProcessor<StateMsg>.Start(fun inbox ->
        let rec loop (state: DashboardState) =
            async {
                let! msg = inbox.Receive()
                let newState = processMessage state msg
                return! loop newState
            }

        loop DashboardState.empty)

type RefreshTask =
    | RefreshWorktreeList
    | RefreshGit of path: string
    | RefreshBeads of path: string
    | RefreshPr
    | RefreshFetch

let private taskLabel = function
    | RefreshWorktreeList -> "WorktreeList", ""
    | RefreshGit path -> "GitRefresh", System.IO.Path.GetFileName(path)
    | RefreshBeads path -> "BeadsRefresh", System.IO.Path.GetFileName(path)
    | RefreshPr -> "PrFetch", ""
    | RefreshFetch -> "GitFetch", ""

let private intervalOf = function
    | RefreshWorktreeList -> TimeSpan.FromSeconds(60.0)
    | RefreshGit _ -> TimeSpan.FromSeconds(15.0)
    | RefreshBeads _ -> TimeSpan.FromSeconds(15.0)
    | RefreshPr -> TimeSpan.FromSeconds(120.0)
    | RefreshFetch -> TimeSpan.FromSeconds(120.0)

let private buildTaskList (worktrees: GitWorktree.WorktreeInfo list) =
    [ RefreshWorktreeList
      RefreshPr
      RefreshFetch
      yield! worktrees |> List.map (fun wt -> RefreshGit wt.Path)
      yield! worktrees |> List.map (fun wt -> RefreshBeads wt.Path) ]

let private deadlineOf (lastRuns: Map<RefreshTask, DateTimeOffset>) (task: RefreshTask) =
    lastRuns
    |> Map.tryFind task
    |> Option.map (fun t -> t + intervalOf task)
    |> Option.defaultValue DateTimeOffset.MinValue

let private executeTask
    (agent: MailboxProcessor<StateMsg>)
    (worktreeRoot: string)
    (task: RefreshTask)
    =
    async {
        match task with
        | RefreshWorktreeList ->
            let! worktrees = GitWorktree.listWorktrees worktreeRoot
            agent.Post(UpdateWorktreeList worktrees)

        | RefreshGit path ->
            let! state = agent.PostAndAsyncReply(GetState)

            let branch =
                state.WorktreeList
                |> List.tryFind (fun wt -> wt.Path = path)
                |> Option.bind (fun wt -> wt.Branch)

            let! gitData = GitWorktree.collectWorktreeGitData path branch
            agent.Post(UpdateGit(path, gitData))

        | RefreshBeads path ->
            let! beads = BeadsStatus.getBeadsSummary path
            agent.Post(UpdateBeads(path, beads))

        | RefreshPr ->
            let! prMap = PrStatus.fetchPrStatusesByRepoRoot worktreeRoot
            agent.Post(UpdatePr prMap)

        | RefreshFetch ->
            do! GitWorktree.fetchFromOrigin worktreeRoot
    }

let private timeoutMs = 60_000

let private executeWithTimeout
    (agent: MailboxProcessor<StateMsg>)
    (worktreeRoot: string)
    (task: RefreshTask)
    =
    async {
        let sw = Stopwatch.StartNew()

        try
            let! child = Async.StartChild(executeTask agent worktreeRoot task, timeoutMs)
            do! child
            sw.Stop()
            return Ok sw.Elapsed
        with
        | :? TimeoutException ->
            sw.Stop()
            return Error $"Timed out after {timeoutMs}ms"
        | ex ->
            sw.Stop()
            return Error ex.Message
    }

let private logTaskResult (agent: MailboxProcessor<StateMsg>) (task: RefreshTask) (result: Result<TimeSpan, string>) =
    let source, target = taskLabel task

    let status, duration, message =
        match result with
        | Ok elapsed ->
            Some StepStatus.Succeeded,
            Some elapsed,
            target
        | Error msg ->
            Some(StepStatus.Failed msg),
            None,
            (match target with
             | "" -> msg
             | t -> $"{t}: {msg}")

    agent.Post(
        LogSchedulerEvent
            { Source = source
              Message = message
              Timestamp = DateTimeOffset.Now
              Status = status
              Duration = duration })

    match result with
    | Ok elapsed ->
        Log.log "Scheduler" $"{source} {target} completed in {elapsed.TotalMilliseconds:F0}ms"
    | Error msg ->
        Log.log "Scheduler" $"{source} {target} failed: {msg}"

let pickMostOverdue (now: DateTimeOffset) (lastRuns: Map<RefreshTask, DateTimeOffset>) (tasks: RefreshTask list) =
    tasks
    |> List.filter (fun task -> deadlineOf lastRuns task <= now)
    |> List.sortBy (deadlineOf lastRuns)
    |> List.tryHead

let computeSleepMs (now: DateTimeOffset) (lastRuns: Map<RefreshTask, DateTimeOffset>) (tasks: RefreshTask list) =
    tasks
    |> List.map (fun task ->
        let deadline = deadlineOf lastRuns task
        (deadline - now).TotalMilliseconds |> int)
    |> List.fold min Int32.MaxValue
    |> max 100

let start (agent: MailboxProcessor<StateMsg>) (worktreeRoot: string) (ct: CancellationToken) =
    let rec loop (lastRuns: Map<RefreshTask, DateTimeOffset>) =
        async {
            let! state = agent.PostAndAsyncReply(GetState)
            let tasks = buildTaskList state.WorktreeList
            let now = DateTimeOffset.UtcNow

            match pickMostOverdue now lastRuns tasks with
            | Some task ->
                let! result = executeWithTimeout agent worktreeRoot task
                logTaskResult agent task result
                let updatedRuns = lastRuns |> Map.add task now
                return! loop updatedRuns
            | None ->
                let sleepMs = computeSleepMs now lastRuns tasks
                do! Async.Sleep sleepMs
                return! loop lastRuns
        }

    Async.Start(loop Map.empty, ct)
