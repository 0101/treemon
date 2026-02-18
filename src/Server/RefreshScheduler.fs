module Server.RefreshScheduler

open System
open Shared

type DashboardState =
    { WorktreeList: GitWorktree.WorktreeInfo list
      GitData: Map<string, GitWorktree.GitData>
      BeadsData: Map<string, BeadsSummary>
      PrData: Map<string, PrStatus>
      SchedulerEvents: CardEvent list
      IsReady: bool }

module DashboardState =
    let empty =
        { WorktreeList = []
          GitData = Map.empty
          BeadsData = Map.empty
          PrData = Map.empty
          SchedulerEvents = []
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
        { state with SchedulerEvents = trimEvents (event :: state.SchedulerEvents) }

let createAgent () =
    MailboxProcessor<StateMsg>.Start(fun inbox ->
        let rec loop (state: DashboardState) =
            async {
                let! msg = inbox.Receive()
                let newState = processMessage state msg
                return! loop newState
            }

        loop DashboardState.empty)
