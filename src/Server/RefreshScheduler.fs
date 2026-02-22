module Server.RefreshScheduler

open System
open System.Diagnostics
open System.IO
open System.Threading
open Shared
open Shared.EventUtils

type PerRepoState =
    { WorktreeList: GitWorktree.WorktreeInfo list
      KnownPaths: Set<string>
      GitData: Map<string, GitWorktree.GitData>
      BeadsData: Map<string, BeadsSummary>
      ClaudeData: Map<string, ClaudeCodeStatus>
      PrData: Map<string, PrStatus>
      IsReady: bool }

module PerRepoState =
    let empty =
        { WorktreeList = []
          KnownPaths = Set.empty
          GitData = Map.empty
          BeadsData = Map.empty
          ClaudeData = Map.empty
          PrData = Map.empty
          IsReady = false }

type DashboardState =
    { Repos: Map<string, PerRepoState>
      SchedulerEvents: CardEvent list
      PinnedErrors: Map<string * string, CardEvent>
      LatestByCategory: Map<string, CardEvent> }

module DashboardState =
    let empty =
        { Repos = Map.empty
          SchedulerEvents = []
          PinnedErrors = Map.empty
          LatestByCategory = Map.empty }

type StateMsg =
    | UpdateWorktreeList of repoId: string * GitWorktree.WorktreeInfo list
    | UpdateGit of repoId: string * path: string * GitWorktree.GitData
    | UpdateBeads of repoId: string * path: string * BeadsSummary
    | UpdateClaude of repoId: string * path: string * ClaudeCodeStatus
    | UpdatePr of repoId: string * Map<string, PrStatus>
    | RemoveWorktree of repoId: string * path: string
    | GetState of AsyncReplyChannel<DashboardState>
    | LogSchedulerEvent of CardEvent

let private maxEvents = 50

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

let private getRepo (repoId: string) (state: DashboardState) =
    state.Repos
    |> Map.tryFind repoId
    |> Option.defaultValue PerRepoState.empty

let private updateRepo (repoId: string) (repo: PerRepoState) (state: DashboardState) =
    { state with Repos = state.Repos |> Map.add repoId repo }

let private removeWorktreeData (path: string) (repo: PerRepoState) =
    { repo with
        WorktreeList = repo.WorktreeList |> List.filter (fun wt -> wt.Path <> path)
        GitData = repo.GitData |> Map.remove path
        BeadsData = repo.BeadsData |> Map.remove path
        ClaudeData = repo.ClaudeData |> Map.remove path }

let private processMessage (state: DashboardState) (msg: StateMsg) =
    match msg with
    | UpdateWorktreeList(repoId, worktrees) ->
        let repo = getRepo repoId state
        let newPaths = worktrees |> List.map (fun wt -> wt.Path) |> Set.ofList
        let removedPaths = Set.difference repo.KnownPaths newPaths

        let cleaned =
            removedPaths
            |> Set.fold (fun r path -> removeWorktreeData path r) repo

        let updated =
            { cleaned with
                WorktreeList = worktrees
                KnownPaths = newPaths
                IsReady = true }

        updateRepo repoId updated state

    | UpdateGit(repoId, path, gitData) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId { repo with GitData = repo.GitData |> Map.add path gitData } state
        else
            state

    | UpdateBeads(repoId, path, beads) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId { repo with BeadsData = repo.BeadsData |> Map.add path beads } state
        else
            state

    | UpdateClaude(repoId, path, status) ->
        let repo = getRepo repoId state
        if Set.contains path repo.KnownPaths then
            updateRepo repoId { repo with ClaudeData = repo.ClaudeData |> Map.add path status } state
        else
            state

    | UpdatePr(repoId, prMap) ->
        let repo = getRepo repoId state
        updateRepo repoId { repo with PrData = prMap } state

    | RemoveWorktree(repoId, path) ->
        let repo = getRepo repoId state
        updateRepo repoId (removeWorktreeData path repo) state

    | GetState replyChannel ->
        replyChannel.Reply(state)
        state

    | LogSchedulerEvent event ->
        { state with
            SchedulerEvents = trimEvents (event :: state.SchedulerEvents)
            PinnedErrors = updatePinnedErrors state.PinnedErrors event
            LatestByCategory = state.LatestByCategory |> Map.add event.Source event }

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
    | RefreshWorktreeList of repoId: string
    | RefreshGit of repoId: string * path: string
    | RefreshBeads of repoId: string * path: string
    | RefreshClaude of repoId: string * path: string
    | RefreshPr of repoId: string
    | RefreshFetch of repoId: string

let private taskLabel = function
    | RefreshWorktreeList repoId -> "WorktreeList", repoId
    | RefreshGit(repoId, path) -> "GitRefresh", $"{repoId}/{Path.GetFileName(path)}"
    | RefreshBeads(repoId, path) -> "BeadsRefresh", $"{repoId}/{Path.GetFileName(path)}"
    | RefreshClaude(repoId, path) -> "ClaudeRefresh", $"{repoId}/{Path.GetFileName(path)}"
    | RefreshPr repoId -> "PrFetch", repoId
    | RefreshFetch repoId -> "GitFetch", repoId

let private intervalOf = function
    | RefreshWorktreeList _ -> TimeSpan.FromSeconds(60.0)
    | RefreshGit _ -> TimeSpan.FromSeconds(15.0)
    | RefreshBeads _ -> TimeSpan.FromSeconds(15.0)
    | RefreshClaude _ -> TimeSpan.FromSeconds(15.0)
    | RefreshPr _ -> TimeSpan.FromSeconds(120.0)
    | RefreshFetch _ -> TimeSpan.FromSeconds(120.0)

let buildTaskList (repos: Map<string, PerRepoState>) =
    let repoList = repos |> Map.toList

    let worktreeLists =
        repoList |> List.map (fun (repoId, _) -> RefreshWorktreeList repoId)

    let localTasks =
        repoList
        |> List.collect (fun (repoId, repo) ->
            repo.WorktreeList
            |> List.collect (fun wt ->
                [ RefreshGit(repoId, wt.Path)
                  RefreshBeads(repoId, wt.Path)
                  RefreshClaude(repoId, wt.Path) ]))

    let networkTasks =
        repoList
        |> List.collect (fun (repoId, _) ->
            [ RefreshPr repoId; RefreshFetch repoId ])

    worktreeLists @ localTasks @ networkTasks

let private deadlineOf (lastRuns: Map<RefreshTask, DateTimeOffset>) (task: RefreshTask) =
    lastRuns
    |> Map.tryFind task
    |> Option.map (fun t -> t + intervalOf task)
    |> Option.defaultValue DateTimeOffset.MinValue

let private executeTask
    (agent: MailboxProcessor<StateMsg>)
    (rootPaths: Map<string, string>)
    (task: RefreshTask)
    =
    async {
        match task with
        | RefreshWorktreeList repoId ->
            let root = rootPaths |> Map.find repoId
            let! worktrees = GitWorktree.listWorktrees root
            agent.Post(UpdateWorktreeList(repoId, worktrees))

        | RefreshGit(repoId, path) ->
            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.tryFind repoId |> Option.defaultValue PerRepoState.empty

            let branch =
                repo.WorktreeList
                |> List.tryFind (fun wt -> wt.Path = path)
                |> Option.bind (fun wt -> wt.Branch)

            let! gitData = GitWorktree.collectWorktreeGitData path branch
            agent.Post(UpdateGit(repoId, path, gitData))

        | RefreshBeads(repoId, path) ->
            let! beads = BeadsStatus.getBeadsSummary path
            agent.Post(UpdateBeads(repoId, path, beads))

        | RefreshClaude(repoId, path) ->
            let status = ClaudeStatus.getClaudeStatus path
            agent.Post(UpdateClaude(repoId, path, status))

        | RefreshPr repoId ->
            let root = rootPaths |> Map.find repoId
            let! state = agent.PostAndAsyncReply(GetState)
            let repo = state.Repos |> Map.tryFind repoId |> Option.defaultValue PerRepoState.empty

            let knownBranches =
                repo.GitData
                |> Map.values
                |> Seq.choose (fun g -> g.UpstreamBranch)
                |> set

            let! prMap = PrStatus.fetchPrStatusesByRepoRoot root knownBranches
            agent.Post(UpdatePr(repoId, prMap))

        | RefreshFetch repoId ->
            let root = rootPaths |> Map.find repoId
            do! GitWorktree.fetchFromOrigin root
    }

let private timeoutMs = 60_000

let private executeWithTimeout
    (agent: MailboxProcessor<StateMsg>)
    (rootPaths: Map<string, string>)
    (task: RefreshTask)
    =
    async {
        let sw = Stopwatch.StartNew()

        try
            let! child = Async.StartChild(executeTask agent rootPaths task, timeoutMs)
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
            target

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
    |> List.tryHead

let computeSleepMs (now: DateTimeOffset) (lastRuns: Map<RefreshTask, DateTimeOffset>) (tasks: RefreshTask list) =
    tasks
    |> List.map (fun task ->
        let deadline = deadlineOf lastRuns task
        (deadline - now).TotalMilliseconds |> int)
    |> List.fold min Int32.MaxValue
    |> max 100

let start (agent: MailboxProcessor<StateMsg>) (worktreeRoots: string list) (ct: CancellationToken) =
    let rootPaths =
        worktreeRoots
        |> List.map (fun root -> Path.GetFileName(root), root)
        |> Map.ofList

    let initialRepos =
        rootPaths
        |> Map.map (fun _ _ -> PerRepoState.empty)

    rootPaths
    |> Map.iter (fun repoId _ ->
        agent.Post(UpdateWorktreeList(repoId, [])))

    let rec loop (lastRuns: Map<RefreshTask, DateTimeOffset>) =
        async {
            let! state = agent.PostAndAsyncReply(GetState)

            let repos =
                if Map.isEmpty state.Repos then initialRepos
                else state.Repos

            let tasks = buildTaskList repos
            let now = DateTimeOffset.UtcNow

            match pickMostOverdue now lastRuns tasks with
            | Some task ->
                let! result = executeWithTimeout agent rootPaths task
                logTaskResult agent task result
                let updatedRuns = lastRuns |> Map.add task now
                return! loop updatedRuns
            | None ->
                let sleepMs = computeSleepMs now lastRuns tasks
                do! Async.Sleep sleepMs
                return! loop lastRuns
        }

    Async.Start(loop Map.empty, ct)
