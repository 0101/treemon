module Server.SyncEngine

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open Shared

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
        { Source = sprintf "%A" step
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
        let cmdString = sprintf "%s %s" fileName arguments

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
                            Log.log "SyncEngine" (sprintf "Killing process: %s (PID %d)" cmdString proc.Id)
                            proc.Kill(entireProcessTree = true)
                    with ex ->
                        Log.log "SyncEngine" (sprintf "Failed to kill process %d: %s" proc.Id ex.Message))

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

                Log.log "SyncEngine" (sprintf "%s -> exit %d" cmdString result.ExitCode)
                return Ok result
            finally
                registration.Dispose()
        with
        | :? OperationCanceledException ->
            Log.log "SyncEngine" (sprintf "%s -> cancelled" cmdString)
            return Error "Cancelled"
        | :? System.ComponentModel.Win32Exception as ex ->
            Log.log "SyncEngine" (sprintf "%s -> failed to start: %s" cmdString ex.Message)
            return Error (sprintf "Failed to start process: %s" ex.Message)
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

let completeSync (branch: string) (result: StepStatus) =
    match syncProcesses.TryGetValue(branch) with
    | true, sp ->
        let finalState =
            match result with
            | StepStatus.Cancelled -> SyncState.Cancelled
            | _ -> SyncState.Completed result

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
            Log.log "SyncEngine" (sprintf "Cancelling sync for branch: %s" branch)
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
