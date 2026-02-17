module Server.SyncEngine

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open Shared

[<RequireQualifiedAccess>]
type SyncStep =
    | CheckClean
    | Pull
    | Merge
    | ResolveConflicts
    | Test

[<RequireQualifiedAccess>]
type SyncState =
    | Idle
    | Running of currentStep: SyncStep
    | Completed of lastResult: StepStatus
    | Cancelled

type ProcessResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

type SyncProcess =
    { State: SyncState
      CancellationTokenSource: CancellationTokenSource
      RunningProcess: Process option }

let private syncProcesses = ConcurrentDictionary<string, SyncProcess>()

let private branchEvents = ConcurrentDictionary<string, CardEvent list>()

let private updateProcess (branch: string) (update: SyncProcess -> SyncProcess) =
    syncProcesses.AddOrUpdate(
        branch,
        (fun _ -> failwith "Cannot update non-existent sync process"),
        fun _ existing -> update existing
    )
    |> ignore

let pushEvent (branch: string) (event: CardEvent) =
    branchEvents.AddOrUpdate(
        branch,
        [ event ],
        fun _ existing -> event :: existing
    )
    |> ignore

let getEvents (branch: string) : CardEvent list =
    match branchEvents.TryGetValue(branch) with
    | true, events -> events
    | false, _ -> []

let getAllEvents () : Map<string, CardEvent list> =
    branchEvents
    |> Seq.map (fun kvp -> kvp.Key, kvp.Value)
    |> Map.ofSeq

let addStepEvent (branch: string) (step: SyncStep) (status: StepStatus) (message: string) =
    let event =
        { Source = $"{step}"
          Message = message
          Timestamp = DateTimeOffset.Now
          Status = Some status }

    pushEvent branch event

let runProcess
    (workingDir: string)
    (fileName: string)
    (arguments: string)
    (cancellationToken: CancellationToken)
    : Async<Result<ProcessResult, string>> =
    async {
        let cmdString = $"{fileName} {arguments}"
        try
            let psi =
                ProcessStartInfo(
                    fileName,
                    arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir
                )

            use proc = Process.Start(psi)

            let registration =
                cancellationToken.Register(fun () ->
                    try
                        if not proc.HasExited then
                            Log.log "SyncEngine" $"Killing process: {cmdString} (PID {proc.Id})"
                            proc.Kill(entireProcessTree = true)
                    with ex ->
                        Log.log "SyncEngine" $"Failed to kill process {proc.Id}: {ex.Message}")

            try
                let stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken)
                let stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken)

                do! proc.WaitForExitAsync(cancellationToken) |> Async.AwaitTask
                let! stdout = stdoutTask |> Async.AwaitTask
                let! stderr = stderrTask |> Async.AwaitTask

                let result =
                    { ExitCode = proc.ExitCode
                      Stdout = stdout.TrimEnd()
                      Stderr = stderr.TrimEnd() }

                Log.log "SyncEngine" $"{cmdString} -> exit {result.ExitCode}"
                return Ok result
            finally
                registration.Dispose()
        with
        | :? OperationCanceledException ->
            Log.log "SyncEngine" $"{cmdString} -> cancelled"
            return Error "Cancelled"
        | :? System.ComponentModel.Win32Exception as ex ->
            Log.log "SyncEngine" $"{cmdString} -> failed to start: {ex.Message}"
            return Error $"Failed to start process: {ex.Message}"
    }

let isRunning (branch: string) : bool =
    match syncProcesses.TryGetValue(branch) with
    | true, sp ->
        match sp.State with
        | SyncState.Running _ -> true
        | _ -> false
    | false, _ -> false

let getState (branch: string) : SyncState =
    match syncProcesses.TryGetValue(branch) with
    | true, sp -> sp.State
    | false, _ -> SyncState.Idle

let beginSync (branch: string) : Result<CancellationToken, string> =
    match isRunning branch with
    | true -> Error "Sync already running for this branch"
    | false ->
        let cts = new CancellationTokenSource()

        let sp =
            { State = SyncState.Running SyncStep.CheckClean
              CancellationTokenSource = cts
              RunningProcess = None }

        syncProcesses.AddOrUpdate(branch, sp, fun _ _ -> sp) |> ignore
        branchEvents.AddOrUpdate(branch, [], fun _ _ -> []) |> ignore
        Ok cts.Token

let updateState (branch: string) (state: SyncState) =
    match syncProcesses.TryGetValue(branch) with
    | true, _ -> updateProcess branch (fun sp -> { sp with State = state })
    | false, _ -> ()

let private clearRunningEvents (branch: string) =
    branchEvents.AddOrUpdate(
        branch,
        [],
        fun _ existing ->
            existing
            |> List.filter (fun evt -> evt.Status <> Some StepStatus.Running))
    |> ignore

let completeSync (branch: string) (result: StepStatus) =
    match syncProcesses.TryGetValue(branch) with
    | true, sp ->
        let finalState =
            match result with
            | StepStatus.Cancelled -> SyncState.Cancelled
            | _ -> SyncState.Completed result

        clearRunningEvents branch

        updateProcess branch (fun s ->
            { s with
                State = finalState
                RunningProcess = None })

        sp.CancellationTokenSource.Dispose()
    | false, _ -> ()

let cancelSync (branch: string) =
    match syncProcesses.TryGetValue(branch) with
    | true, sp ->
        match sp.State with
        | SyncState.Running _ ->
            Log.log "SyncEngine" $"Cancelling sync for branch: {branch}"
            sp.CancellationTokenSource.Cancel()

            let cancelEvent =
                { Source = "sync"
                  Message = "Sync cancelled"
                  Timestamp = DateTimeOffset.Now
                  Status = Some StepStatus.Cancelled }

            pushEvent branch cancelEvent

            completeSync branch StepStatus.Cancelled
        | _ -> ()
    | false, _ -> ()

let private readTreemonConfig (worktreePath: string) : string option =
    let configPath = Path.Combine(worktreePath, ".treemon.json")

    match File.Exists(configPath) with
    | false -> None
    | true ->
        try
            let json = File.ReadAllText(configPath)
            let doc = System.Text.Json.JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty("testSolution") with
            | true, elem -> Some(elem.GetString())
            | false, _ -> None
        with ex ->
            Log.log "SyncEngine" $"Failed to read .treemon.json: {ex.Message}"
            None

let private runStep
    (branch: string)
    (step: SyncStep)
    (worktreePath: string)
    (fileName: string)
    (arguments: string)
    (ct: CancellationToken)
    : Async<Result<ProcessResult, StepStatus>> =
    async {
        updateState branch (SyncState.Running step)
        addStepEvent branch step StepStatus.Running $"{fileName} {arguments}"

        let! result = runProcess worktreePath fileName arguments ct

        match result with
        | Error msg ->
            let status = StepStatus.Failed msg
            addStepEvent branch step status msg
            return Error status
        | Ok proc when proc.ExitCode <> 0 ->
            let msg =
                match proc.Stderr with
                | "" -> $"exit {proc.ExitCode}"
                | stderr -> $"exit {proc.ExitCode}: {stderr.[..min 199 (stderr.Length - 1)]}"

            let status = StepStatus.Failed msg
            addStepEvent branch step status msg
            return Error status
        | Ok proc ->
            addStepEvent branch step StepStatus.Succeeded "success"
            return Ok proc
    }

let executeSyncPipeline (branch: string) (worktreePath: string) (repoRoot: string) (ct: CancellationToken) : Async<unit> =
    async {
        try
            Log.log "SyncEngine" $"Starting sync pipeline for {branch} at {worktreePath}"

            // Step 1: Check clean
            let! checkResult = runStep branch SyncStep.CheckClean worktreePath "git" "status --porcelain --untracked-files=no" ct

            match checkResult with
            | Error status ->
                completeSync branch status
                return ()
            | Ok proc when proc.Stdout <> "" ->
                let status = StepStatus.Failed "Working tree is dirty"
                addStepEvent branch SyncStep.CheckClean status "Working tree is dirty"
                completeSync branch status
                return ()
            | Ok _ ->

            // Step 2: Pull
            let! pullResult = runStep branch SyncStep.Pull worktreePath "git" "fetch origin" ct

            match pullResult with
            | Error status ->
                completeSync branch status
                return ()
            | Ok _ ->

            // Step 3: Merge
            updateState branch (SyncState.Running SyncStep.Merge)
            addStepEvent branch SyncStep.Merge StepStatus.Running "git merge origin/main"

            let! mergeResult = runProcess worktreePath "git" "merge origin/main" ct

            match mergeResult with
            | Error msg ->
                let status = StepStatus.Failed msg
                addStepEvent branch SyncStep.Merge status msg
                completeSync branch status
                return ()
            | Ok mergeProc ->

            match mergeProc.ExitCode with
            | 0 ->
                addStepEvent branch SyncStep.Merge StepStatus.Succeeded "success"
            | _ ->
                // Step 4: Conflict resolution
                let mergeMsg =
                    match mergeProc.Stderr with
                    | "" -> $"exit {mergeProc.ExitCode}"
                    | stderr -> $"exit {mergeProc.ExitCode}: {stderr.[..min 199 (stderr.Length - 1)]}"

                addStepEvent branch SyncStep.Merge (StepStatus.Failed mergeMsg) mergeMsg

                let! conflictResult =
                    runStep
                        branch
                        SyncStep.ResolveConflicts
                        worktreePath
                        "claude"
                        "-p \"/conflict\" --dangerously-skip-permissions"
                        ct

                match conflictResult with
                | Error status ->
                    completeSync branch status
                    return ()
                | Ok _ -> ()

            // Step 5: Test (optional, from .treemon.json)
            match readTreemonConfig worktreePath with
            | None ->
                Log.log "SyncEngine" $"No .treemon.json found, skipping test step for {branch}"
            | Some solutionPath ->
                let! testResult =
                    runStep branch SyncStep.Test worktreePath "dotnet" $"test {solutionPath}" ct

                match testResult with
                | Error status ->
                    completeSync branch status
                    return ()
                | Ok _ -> ()

            GitWorktree.Cache.invalidate repoRoot
            completeSync branch StepStatus.Succeeded
            Log.log "SyncEngine" $"Sync pipeline completed successfully for {branch}"
        with :? OperationCanceledException ->
            Log.log "SyncEngine" $"Sync pipeline cancelled for {branch}"
            completeSync branch StepStatus.Cancelled
    }
