module Server.WorktreeApi

open System
open System.Diagnostics
open System.IO
open Shared
open Shared.EventUtils
open Newtonsoft.Json

type FixtureData =
    { Worktrees: DashboardResponse
      SyncStatus: Map<string, CardEvent list> }

let loadFixtures (path: string) =
    let json = File.ReadAllText(path)
    let converter = Fable.Remoting.Json.FableJsonConverter()
    JsonConvert.DeserializeObject<FixtureData>(json, converter)

let private assembleFromState
    (repo: RefreshScheduler.PerRepoState)
    (wt: GitWorktree.WorktreeInfo)
    =
    let gitData = repo.GitData |> Map.tryFind wt.Path
    let beads = repo.BeadsData |> Map.tryFind wt.Path |> Option.defaultValue BeadsSummary.zero
    let claude = repo.ClaudeData |> Map.tryFind wt.Path |> Option.defaultValue ClaudeCodeStatus.Idle
    let upstreamBranch = gitData |> Option.bind (fun g -> g.UpstreamBranch)
    let pr = PrStatus.lookupPrStatus repo.PrData upstreamBranch

    { Path = wt.Path
      Branch = wt.Branch |> Option.defaultValue "(detached)"
      LastCommitMessage = gitData |> Option.map (_.LastCommitMessage) |> Option.defaultValue ""
      LastCommitTime = gitData |> Option.map (_.LastCommitTime) |> Option.defaultValue DateTimeOffset.MinValue
      Beads = beads
      Claude = claude
      Pr = pr
      MainBehindCount = gitData |> Option.map (_.MainBehindCount) |> Option.defaultValue 0
      IsDirty = gitData |> Option.map (_.IsDirty) |> Option.defaultValue false
      WorkMetrics = gitData |> Option.bind (fun g -> g.WorkMetrics) }

let private allWorktrees (state: RefreshScheduler.DashboardState) =
    state.Repos
    |> Map.toList
    |> List.collect (fun (_, repo) -> repo.WorktreeList)

let private allKnownPaths (state: RefreshScheduler.DashboardState) =
    state.Repos
    |> Map.toList
    |> List.collect (fun (_, repo) -> repo.KnownPaths |> Set.toList)
    |> Set.ofList

let private findRepoForPath (state: RefreshScheduler.DashboardState) (path: string) =
    state.Repos
    |> Map.tryPick (fun repoId repo ->
        if Set.contains path repo.KnownPaths then Some repoId
        else None)

let getWorktrees
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (worktreeRoot: string)
    (appVersion: string)
    : Async<DashboardResponse> =
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

        let repos =
            state.Repos
            |> Map.toList
            |> List.map (fun (repoId, repo) ->
                let statuses =
                    repo.WorktreeList
                    |> List.filter (fun w -> w.Branch <> Some "main")
                    |> List.map (assembleFromState repo)

                { RepoId = repoId
                  RootFolderName = repoId
                  Worktrees = statuses
                  IsReady = repo.IsReady })

        return
            { Repos = repos
              SchedulerEvents = mergeWithPinnedErrors state.SchedulerEvents state.PinnedErrors
              LatestByCategory = state.LatestByCategory
              AppVersion = appVersion }
    }

let private openTerminal
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (worktreeRoot: string)
    (path: string)
    =
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
        let knownPaths = allKnownPaths state

        if not (Set.contains path knownPaths) then
            Log.log "API" $"openTerminal: rejected unknown path '{path}'"
        else
            let escapedPath = path.Replace("'", "''")
            let startInfo =
                ProcessStartInfo(
                    FileName = "wt.exe",
                    Arguments = $"""-w 0 new-tab pwsh -NoExit -Command "Set-Location -LiteralPath '{escapedPath}'" """,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            try
                Log.log "API" $"openTerminal: launching terminal for '{path}'"
                Process.Start(startInfo) |> ignore
            with
            | :? System.ComponentModel.Win32Exception as ex ->
                Log.log "API" $"openTerminal: failed to start wt.exe: {ex.Message}"
            | ex ->
                Log.log "API" $"openTerminal: unexpected error starting terminal: {ex.Message}"
    }

let private deleteWorktree
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (worktreeRoot: string)
    (branch: string)
    =
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
        let worktrees = allWorktrees state

        let worktree =
            worktrees
            |> List.tryFind (fun wt -> wt.Branch = Some branch)

        match worktree with
        | None -> return Error $"No worktree found for branch '{branch}'"
        | Some wt ->
            let repoId = findRepoForPath state wt.Path

            match repoId with
            | Some rid -> agent.Post(RefreshScheduler.StateMsg.RemoveWorktree(rid, wt.Path))
            | None -> ()

            let repoRoot =
                worktrees
                |> List.tryFind (fun w -> w.Branch = Some "main")
                |> Option.map (fun w -> w.Path)
                |> Option.defaultValue worktreeRoot

            let! result = GitWorktree.removeWorktree repoRoot wt.Path branch
            return result
    }

let worktreeApi
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (worktreeRoot: string)
    (testFixtures: string option)
    (appVersion: string)
    : IWorktreeApi =
    let fixtures = testFixtures |> Option.map loadFixtures

    match fixtures with
    | Some f ->
        { getWorktrees = fun () -> async { return f.Worktrees }
          openTerminal = fun _ -> async { return () }
          startSync = fun _ -> async { return Error "Sync is not available in fixture mode" }
          cancelSync = fun _ -> async { return () }
          getSyncStatus = fun () -> async { return f.SyncStatus }
          deleteWorktree = fun _ -> async { return Error "Delete is not available in fixture mode" } }
    | None ->
        { getWorktrees = fun () -> getWorktrees agent worktreeRoot appVersion
          openTerminal = openTerminal agent worktreeRoot
          startSync = fun branch ->
              async {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
                  let worktrees = allWorktrees state

                  let worktreePath =
                      worktrees
                      |> List.tryFind (fun wt -> wt.Branch = Some branch)
                      |> Option.map (fun wt -> wt.Path)

                  match worktreePath with
                  | None -> return Error $"No worktree found for branch '{branch}'"
                  | Some path ->
                      match SyncEngine.beginSync branch with
                      | Error msg -> return Error msg
                      | Ok ct ->
                          Async.Start(SyncEngine.executeSyncPipeline branch path worktreeRoot ct, ct)
                          return Ok ()
              }
          cancelSync = fun branch -> async { SyncEngine.cancelSync branch }
          getSyncStatus = fun () ->
              async {
                  let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)
                  let worktrees = allWorktrees state

                  let branchToPath =
                      worktrees
                      |> List.choose (fun wt ->
                          wt.Branch |> Option.map (fun b -> b, wt.Path))
                      |> Map.ofList

                  let syncEvents = SyncEngine.getAllEvents ()

                  let allBranches =
                      [ yield! syncEvents |> Map.keys
                        yield! branchToPath |> Map.keys ]
                      |> List.distinct

                  return
                      allBranches
                      |> List.choose (fun branch ->
                          let syncEvts =
                              syncEvents
                              |> Map.tryFind branch
                              |> Option.defaultValue []

                          let claudeEvt =
                              branchToPath
                              |> Map.tryFind branch
                              |> Option.bind ClaudeStatus.getLastClaudeMessage

                          let merged =
                              match claudeEvt with
                              | Some evt -> evt :: syncEvts
                              | None -> syncEvts

                          match merged with
                          | [] -> None
                          | events ->
                              let recent =
                                  events
                                  |> List.sortByDescending (fun e -> e.Timestamp)
                                  |> List.truncate 2
                                  |> List.rev

                              Some(branch, recent))
                      |> Map.ofList
              }
          deleteWorktree = deleteWorktree agent worktreeRoot }
