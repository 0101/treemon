module Server.WorktreeApi

open System
open System.Diagnostics
open System.IO
open Shared
open Newtonsoft.Json

type FixtureData =
    { Worktrees: WorktreeResponse
      SyncStatus: Map<string, CardEvent list> }

let private loadFixtures (path: string) =
    let json = File.ReadAllText(path)
    let converter = Fable.Remoting.Json.FableJsonConverter()
    JsonConvert.DeserializeObject<FixtureData>(json, converter)

let private assembleWorktreeStatus
    (repoRoot: string)
    (prMap: Map<string, PrStatus>)
    (wt: GitWorktree.WorktreeInfo)
    =
    async {
        try
            let! gitData = GitWorktree.collectWorktreeGitData wt.Path wt.Branch
            let! beads = BeadsStatus.Cache.getCachedBeadsSummary wt.Path
            let claude = ClaudeStatus.Cache.getCachedClaudeStatus wt.Path

            let pr = PrStatus.Cache.lookupPrStatus prMap gitData.UpstreamBranch

            return
                { Path = gitData.Path
                  Branch = gitData.Branch
                  LastCommitMessage = gitData.LastCommitMessage
                  LastCommitTime = gitData.LastCommitTime
                  Beads = beads
                  Claude = claude
                  Pr = pr
                  MainBehindCount = gitData.MainBehindCount }
        with ex ->
            Log.log "API" (sprintf "Failed to assemble status for worktree %s (%s): %s" wt.Path (wt.Branch |> Option.defaultValue "(detached)") ex.Message)

            return
                { Path = wt.Path
                  Branch = wt.Branch |> Option.defaultValue "(detached)"
                  LastCommitMessage = ""
                  LastCommitTime = DateTimeOffset.MinValue
                  Beads = BeadsSummary.zero
                  Claude = Unknown
                  Pr = NoPr
                  MainBehindCount = 0 }
    }

let getWorktrees (worktreeRoot: string) : Async<WorktreeResponse> =
    async {
        let! worktrees = GitWorktree.Cache.getCachedWorktrees worktreeRoot
        let! prMap = PrStatus.Cache.getCachedPrStatuses worktreeRoot

        let! statuses =
            worktrees
            |> List.filter (fun w -> w.Branch <> Some "main")
            |> List.map (assembleWorktreeStatus worktreeRoot prMap)
            |> Async.Parallel

        let folderName = System.IO.Path.GetFileName worktreeRoot

        let syncTimes =
            { Git = GitWorktree.Cache.getCachedAt worktreeRoot
              Beads = BeadsStatus.Cache.getOldestCachedAt ()
              Claude = ClaudeStatus.Cache.getOldestCachedAt ()
              Pr = PrStatus.Cache.getCachedAt worktreeRoot }

        return
            { RootFolderName = folderName
              Worktrees = statuses |> Array.toList
              SyncTimes = syncTimes }
    }

let private openTerminal (worktreeRoot: string) (path: string) =
    async {
        let! worktrees = GitWorktree.Cache.getCachedWorktrees worktreeRoot

        let isKnownWorktree =
            worktrees
            |> List.exists (fun wt ->
                String.Equals(wt.Path, path, StringComparison.OrdinalIgnoreCase))

        match isKnownWorktree with
        | false ->
            Log.log "API" (sprintf "openTerminal: rejected unknown path '%s'" path)
        | true ->
            let startInfo =
                ProcessStartInfo(
                    FileName = "wt.exe",
                    Arguments = sprintf "-w 0 new-tab pwsh -NoExit -Command \"Set-Location '%s'\"" path,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            Log.log "API" (sprintf "openTerminal: launching terminal for '%s'" path)
            Process.Start(startInfo) |> ignore
    }

let private deleteWorktree (worktreeRoot: string) (branch: string) =
    async {
        let! worktrees = GitWorktree.Cache.getCachedWorktrees worktreeRoot

        let worktree =
            worktrees
            |> List.tryFind (fun wt -> wt.Branch = Some branch)

        match worktree with
        | None -> return Error (sprintf "No worktree found for branch '%s'" branch)
        | Some wt ->
            let mainWorktree =
                worktrees
                |> List.tryFind (fun w -> w.Branch = Some "main")

            let repoRoot =
                mainWorktree
                |> Option.map (fun w -> w.Path)
                |> Option.defaultValue worktreeRoot

            let! result = GitWorktree.removeWorktree repoRoot wt.Path branch
            GitWorktree.Cache.invalidate worktreeRoot
            return result
    }

let worktreeApi (worktreeRoot: string) (testFixtures: string option) : IWorktreeApi =
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
        { getWorktrees = fun () -> getWorktrees worktreeRoot
          openTerminal = openTerminal worktreeRoot
          startSync = fun branch ->
              async {
                  let! worktrees = GitWorktree.Cache.getCachedWorktrees worktreeRoot

                  let worktreePath =
                      worktrees
                      |> List.tryFind (fun wt -> wt.Branch = Some branch)
                      |> Option.map (fun wt -> wt.Path)

                  match worktreePath with
                  | None -> return Error (sprintf "No worktree found for branch '%s'" branch)
                  | Some path ->
                      match SyncEngine.beginSync branch with
                      | Error msg -> return Error msg
                      | Ok ct ->
                          Async.Start(SyncEngine.executeSyncPipeline branch path ct, ct)
                          return Ok ()
              }
          cancelSync = fun branch -> async { SyncEngine.cancelSync branch }
          getSyncStatus = fun () ->
              async {
                  let! worktrees = GitWorktree.Cache.getCachedWorktrees worktreeRoot

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
                              |> Option.bind ClaudeStatus.Cache.getCachedLastMessage

                          let merged =
                              match claudeEvt with
                              | Some evt -> evt :: syncEvts
                              | None -> syncEvts

                          match merged with
                          | [] -> None
                          | events ->
                              let top3 =
                                  events
                                  |> List.sortByDescending (fun e -> e.Timestamp)
                                  |> List.truncate 3

                              Some(branch, top3))
                      |> Map.ofList
              }
          deleteWorktree = deleteWorktree worktreeRoot }
