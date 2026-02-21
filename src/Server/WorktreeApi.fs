module Server.WorktreeApi

open System
open System.Diagnostics
open System.IO
open Shared
open Shared.EventUtils
open Newtonsoft.Json

type FixtureData =
    { Worktrees: WorktreeResponse
      SyncStatus: Map<string, CardEvent list> }

let loadFixtures (path: string) =
    let json = File.ReadAllText(path)
    let converter = Fable.Remoting.Json.FableJsonConverter()
    JsonConvert.DeserializeObject<FixtureData>(json, converter)

let private assembleFromState
    (state: RefreshScheduler.DashboardState)
    (wt: GitWorktree.WorktreeInfo)
    =
    let gitData = state.GitData |> Map.tryFind wt.Path
    let beads = state.BeadsData |> Map.tryFind wt.Path |> Option.defaultValue BeadsSummary.zero
    let claude = state.ClaudeData |> Map.tryFind wt.Path |> Option.defaultValue ClaudeCodeStatus.Idle
    let upstreamBranch = gitData |> Option.bind (fun g -> g.UpstreamBranch)
    let pr = PrStatus.lookupPrStatus state.PrData upstreamBranch

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

let getWorktrees
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (worktreeRoot: string)
    (appVersion: string)
    : Async<WorktreeResponse> =
    async {
        let! state = agent.PostAndAsyncReply(RefreshScheduler.StateMsg.GetState)

        let statuses =
            state.WorktreeList
            |> List.filter (fun w -> w.Branch <> Some "main")
            |> List.map (assembleFromState state)

        let folderName = Path.GetFileName worktreeRoot

        return
            { RootFolderName = folderName
              Worktrees = statuses
              IsReady = state.IsReady
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

        if not (Set.contains path state.KnownPaths) then
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

        let worktree =
            state.WorktreeList
            |> List.tryFind (fun wt -> wt.Branch = Some branch)

        match worktree with
        | None -> return Error $"No worktree found for branch '{branch}'"
        | Some wt ->
            agent.Post(RefreshScheduler.StateMsg.RemoveWorktree wt.Path)

            let repoRoot =
                state.WorktreeList
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

                  let worktreePath =
                      state.WorktreeList
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

                  let branchToPath =
                      state.WorktreeList
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
