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
      CancellationTokenSource: CancellationTokenSource }

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
          Status = Some status
          Duration = None }

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
    syncProcesses.TryGetValue(branch)
    |> function
       | true, { State = SyncState.Running _ } -> true
       | _ -> false

let getState (branch: string) : SyncState =
    syncProcesses.TryGetValue(branch)
    |> function
       | true, sp -> sp.State
       | false, _ -> SyncState.Idle

let beginSync (branch: string) : Result<CancellationToken, string> =
    match isRunning branch with
    | true -> Error "Sync already running for this branch"
    | false ->
        let cts = new CancellationTokenSource()

        let sp =
            { State = SyncState.Running SyncStep.CheckClean
              CancellationTokenSource = cts }

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
                State = finalState })

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
                  Status = Some StepStatus.Cancelled
                  Duration = None }

            pushEvent branch cancelEvent

            completeSync branch StepStatus.Cancelled
        | _ -> ()
    | false, _ -> ()

let private isValidSolutionPath (worktreePath: string) (solutionPath: string) =
    let isSolutionExtension =
        solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)

    let fullPath = Path.Combine(worktreePath, solutionPath) |> Path.GetFullPath
    let normalizedRoot = Path.GetFullPath(worktreePath)

    isSolutionExtension && fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath)

let private readTreemonConfig (worktreePath: string) : string option =
    let configPath = Path.Combine(worktreePath, ".treemon.json")

    match File.Exists(configPath) with
    | false -> None
    | true ->
        try
            let json = File.ReadAllText(configPath)
            use doc = System.Text.Json.JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty("testSolution") with
            | true, elem ->
                let solutionPath = elem.GetString()

                match isValidSolutionPath worktreePath solutionPath with
                | true -> Some solutionPath
                | false ->
                    Log.log "SyncEngine" $"testSolution '{solutionPath}' rejected: must be a .sln/.slnx file within {worktreePath}"
                    None
            | false, _ -> None
        with ex ->
            Log.log "SyncEngine" $"Failed to read .treemon.json: {ex.Message}"
            None

let private truncateStderr (stderr: string) (maxLen: int) : string =
    if stderr = "" then "" else stderr[..min (maxLen - 1) (stderr.Length - 1)]

let private runStep
    (branch: string)
    (step: SyncStep)
    (worktreePath: string)
    (fileName: string)
    (arguments: string)
    (ct: CancellationToken)
    : Async<Result<ProcessResult, StepStatus>> =
    async {
        let cmdString = $"{fileName} {arguments}"
        updateState branch (SyncState.Running step)
        addStepEvent branch step StepStatus.Running cmdString

        let! result = runProcess worktreePath fileName arguments ct

        match result with
        | Error msg ->
            let status = StepStatus.Failed msg
            addStepEvent branch step status cmdString
            return Error status
        | Ok proc when proc.ExitCode <> 0 ->
            let msg = $"exit {proc.ExitCode}: {truncateStderr proc.Stderr 200}"
            let status = StepStatus.Failed msg
            addStepEvent branch step status cmdString
            return Error status
        | Ok proc ->
            addStepEvent branch step StepStatus.Succeeded cmdString
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
                addStepEvent branch SyncStep.CheckClean status "git status --porcelain --untracked-files=no"
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
                addStepEvent branch SyncStep.Merge status "git merge origin/main"
                completeSync branch status
                return ()
            | Ok mergeProc ->

            match mergeProc.ExitCode with
            | 0 ->
                addStepEvent branch SyncStep.Merge StepStatus.Succeeded "git merge origin/main"
            | _ ->
                // Step 4: Conflict resolution
                let mergeMsg = $"exit {mergeProc.ExitCode}: {truncateStderr mergeProc.Stderr 200}"
                addStepEvent branch SyncStep.Merge (StepStatus.Failed mergeMsg) "git merge origin/main"

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

            // Step 5: Test
            let testArgs =
                match readTreemonConfig repoRoot with
                | Some solutionPath ->
                    Log.log "SyncEngine" $"Using testSolution from .treemon.json: {solutionPath}"
                    $"""test "{solutionPath}" """
                | None ->
                    Log.log "SyncEngine" $"No .treemon.json found in {repoRoot}, running default 'dotnet test'"
                    "test"

            let! testResult =
                runStep branch SyncStep.Test worktreePath "dotnet" testArgs ct

            match testResult with
            | Error status ->
                completeSync branch status
                return ()
            | Ok _ -> ()

            completeSync branch StepStatus.Succeeded
            Log.log "SyncEngine" $"Sync pipeline completed successfully for {branch}"
        with :? OperationCanceledException ->
            Log.log "SyncEngine" $"Sync pipeline cancelled for {branch}"
            completeSync branch StepStatus.Cancelled
    }
