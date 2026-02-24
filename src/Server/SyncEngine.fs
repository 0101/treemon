module Server.SyncEngine

open System
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
    | Commit

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

type SyncMsg =
    | BeginSync of branch: string * AsyncReplyChannel<Result<CancellationToken, string>>
    | PushEvent of branch: string * CardEvent
    | UpdateProcessState of branch: string * SyncState
    | CompleteSync of branch: string * StepStatus
    | CancelSync of branch: string
    | GetAllEvents of AsyncReplyChannel<Map<string, CardEvent list>>

type SyncAgentState =
    { Processes: Map<string, SyncProcess>
      Events: Map<string, CardEvent list> }

type SideEffect =
    | CancelCts of CancellationTokenSource
    | DisposeCts of CancellationTokenSource

let private clearRunningEvents (events: CardEvent list) =
    events |> List.filter (fun evt -> evt.Status <> Some StepStatus.Running)

let private isProcessRunning (state: SyncAgentState) (branch: string) =
    state.Processes
    |> Map.tryFind branch
    |> Option.map (fun sp -> match sp.State with SyncState.Running _ -> true | _ -> false)
    |> Option.defaultValue false

let processMessage (state: SyncAgentState) (msg: SyncMsg) : SyncAgentState * SideEffect list =
    match msg with
    | BeginSync (branch, reply) ->
        if isProcessRunning state branch then
            reply.Reply(Error "Sync already running for this branch")
            state, []
        else
            let cts = new CancellationTokenSource()
            let sp = { State = SyncState.Running SyncStep.CheckClean; CancellationTokenSource = cts }
            let initialEvent =
                { Source = "sync"
                  Message = "Sync starting"
                  Timestamp = DateTimeOffset.Now
                  Status = Some StepStatus.Running
                  Duration = None }
            let newState =
                { Processes = state.Processes |> Map.add branch sp
                  Events = state.Events |> Map.add branch [ initialEvent ] }
            reply.Reply(Ok cts.Token)
            newState, []

    | PushEvent (branch, event) ->
        let existing = state.Events |> Map.tryFind branch |> Option.defaultValue []
        { state with Events = state.Events |> Map.add branch (event :: existing) }, []

    | UpdateProcessState (branch, syncState) ->
        match state.Processes |> Map.tryFind branch with
        | Some sp ->
            let updated = { sp with State = syncState }
            { state with Processes = state.Processes |> Map.add branch updated }, []
        | None -> state, []

    | CompleteSync (branch, result) ->
        match state.Processes |> Map.tryFind branch with
        | Some sp ->
            match sp.State with
            | SyncState.Completed _ | SyncState.Cancelled ->
                state, []
            | _ ->
                let finalState =
                    match result with
                    | StepStatus.Cancelled -> SyncState.Cancelled
                    | _ -> SyncState.Completed result
                let updated = { sp with State = finalState }
                let cleanedEvents =
                    state.Events
                    |> Map.tryFind branch
                    |> Option.defaultValue []
                    |> clearRunningEvents
                let newState =
                    { Processes = state.Processes |> Map.add branch updated
                      Events = state.Events |> Map.add branch cleanedEvents }
                newState, [ DisposeCts sp.CancellationTokenSource ]
        | None -> state, []

    | CancelSync branch ->
        match state.Processes |> Map.tryFind branch with
        | Some sp ->
            match sp.State with
            | SyncState.Running _ ->
                Log.log "SyncEngine" $"Cancelling sync for branch: {branch}"
                let cancelEvent =
                    { Source = "sync"
                      Message = "Sync cancelled"
                      Timestamp = DateTimeOffset.Now
                      Status = Some StepStatus.Cancelled
                      Duration = None }
                let finalSp = { sp with State = SyncState.Cancelled }
                let existing = state.Events |> Map.tryFind branch |> Option.defaultValue []
                let cleanedEvents = clearRunningEvents existing
                let newState =
                    { Processes = state.Processes |> Map.add branch finalSp
                      Events = state.Events |> Map.add branch (cancelEvent :: cleanedEvents) }
                newState, [ CancelCts sp.CancellationTokenSource ]
            | _ -> state, []
        | None -> state, []

    | GetAllEvents reply ->
        reply.Reply(state.Events)
        state, []

let private executeSideEffects (effects: SideEffect list) =
    effects
    |> List.iter (fun effect ->
        match effect with
        | CancelCts cts ->
            try cts.Cancel() with _ -> ()
        | DisposeCts cts ->
            try cts.Dispose() with _ -> ())

let createSyncAgent () : MailboxProcessor<SyncMsg> =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (state: SyncAgentState) =
            async {
                let! msg = inbox.Receive()
                let newState, sideEffects = processMessage state msg
                executeSideEffects sideEffects
                return! loop newState
            }
        loop { Processes = Map.empty; Events = Map.empty })

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

let private conflictResolutionCommand (provider: Shared.CodingToolProvider option) =
    match provider |> Option.defaultValue Shared.CodingToolProvider.Claude with
    | Shared.CodingToolProvider.Claude ->
        "claude", """-p "/conflict" --dangerously-skip-permissions"""
    | Shared.CodingToolProvider.Copilot ->
        "copilot", """-p "Resolve all merge conflicts in this branch. Run 'git status' to find conflicted files, then resolve each conflict. After resolving, stage the files with 'git add'." --allow-all --no-ask-user -s --autopilot"""

let private runStep
    (post: SyncMsg -> unit)
    (branch: string)
    (step: SyncStep)
    (worktreePath: string)
    (fileName: string)
    (arguments: string)
    (ct: CancellationToken)
    : Async<Result<ProcessResult, StepStatus>> =
    async {
        let cmdString = $"{fileName} {arguments}"
        post (UpdateProcessState (branch, SyncState.Running step))
        post (PushEvent (branch, { Source = $"{step}"; Message = cmdString; Timestamp = DateTimeOffset.Now; Status = Some StepStatus.Running; Duration = None }))

        let! result = runProcess worktreePath fileName arguments ct

        match result with
        | Error msg ->
            let status = StepStatus.Failed msg
            post (PushEvent (branch, { Source = $"{step}"; Message = cmdString; Timestamp = DateTimeOffset.Now; Status = Some status; Duration = None }))
            return Error status
        | Ok proc when proc.ExitCode <> 0 ->
            let msg = $"exit {proc.ExitCode}: {truncateStderr proc.Stderr 200}"
            let status = StepStatus.Failed msg
            post (PushEvent (branch, { Source = $"{step}"; Message = cmdString; Timestamp = DateTimeOffset.Now; Status = Some status; Duration = None }))
            return Error status
        | Ok proc ->
            post (PushEvent (branch, { Source = $"{step}"; Message = cmdString; Timestamp = DateTimeOffset.Now; Status = Some StepStatus.Succeeded; Duration = None }))
            return Ok proc
    }

let executeSyncPipeline (post: SyncMsg -> unit) (branch: string) (worktreePath: string) (repoRoot: string) (provider: Shared.CodingToolProvider option) (ct: CancellationToken) : Async<unit> =
    async {
        try
            Log.log "SyncEngine" $"Starting sync pipeline for {branch} at {worktreePath}"

            let! checkResult = runStep post branch SyncStep.CheckClean worktreePath "git" "status --porcelain --untracked-files=no" ct

            match checkResult with
            | Error status ->
                post (CompleteSync (branch, status))
                return ()
            | Ok proc when proc.Stdout <> "" ->
                let status = StepStatus.Failed "Working tree is dirty"
                post (PushEvent (branch, { Source = $"{SyncStep.CheckClean}"; Message = "git status --porcelain --untracked-files=no"; Timestamp = DateTimeOffset.Now; Status = Some status; Duration = None }))
                post (CompleteSync (branch, status))
                return ()
            | Ok _ ->

            let! pullResult = runStep post branch SyncStep.Pull worktreePath "git" "fetch origin" ct

            match pullResult with
            | Error status ->
                post (CompleteSync (branch, status))
                return ()
            | Ok _ ->

            post (UpdateProcessState (branch, SyncState.Running SyncStep.Merge))
            post (PushEvent (branch, { Source = $"{SyncStep.Merge}"; Message = "git merge origin/main"; Timestamp = DateTimeOffset.Now; Status = Some StepStatus.Running; Duration = None }))

            let! mergeResult = runProcess worktreePath "git" "merge origin/main" ct

            match mergeResult with
            | Error msg ->
                let status = StepStatus.Failed msg
                post (PushEvent (branch, { Source = $"{SyncStep.Merge}"; Message = "git merge origin/main"; Timestamp = DateTimeOffset.Now; Status = Some status; Duration = None }))
                post (CompleteSync (branch, status))
                return ()
            | Ok mergeProc ->

            match mergeProc.ExitCode with
            | 0 ->
                post (PushEvent (branch, { Source = $"{SyncStep.Merge}"; Message = "git merge origin/main"; Timestamp = DateTimeOffset.Now; Status = Some StepStatus.Succeeded; Duration = None }))
            | _ ->
                let mergeMsg = $"exit {mergeProc.ExitCode}: {truncateStderr mergeProc.Stderr 200}"
                post (PushEvent (branch, { Source = $"{SyncStep.Merge}"; Message = "git merge origin/main"; Timestamp = DateTimeOffset.Now; Status = Some (StepStatus.Failed mergeMsg); Duration = None }))

                let fileName, arguments = conflictResolutionCommand provider

                let! conflictResult =
                    runStep post branch SyncStep.ResolveConflicts worktreePath fileName arguments ct

                match conflictResult with
                | Error status ->
                    post (CompleteSync (branch, status))
                    return ()
                | Ok _ -> ()

            let testArgs =
                match readTreemonConfig repoRoot with
                | Some solutionPath ->
                    Log.log "SyncEngine" $"Using testSolution from .treemon.json: {solutionPath}"
                    $"""test "{solutionPath}" """
                | None ->
                    Log.log "SyncEngine" $"No .treemon.json found in {repoRoot}, running default 'dotnet test'"
                    "test"

            let! testResult =
                runStep post branch SyncStep.Test worktreePath "dotnet" testArgs ct

            match testResult with
            | Error status ->
                post (CompleteSync (branch, status))
                return ()
            | Ok _ -> ()

            let! diffResult = runProcess worktreePath "git" "diff --cached --quiet" ct

            match diffResult with
            | Ok proc when proc.ExitCode = 1 ->
                let! commitResult =
                    runStep post branch SyncStep.Commit worktreePath "git" "commit --no-edit" ct

                match commitResult with
                | Error status ->
                    post (CompleteSync (branch, status))
                    return ()
                | Ok _ -> ()
            | _ -> ()

            post (CompleteSync (branch, StepStatus.Succeeded))
            Log.log "SyncEngine" $"Sync pipeline completed successfully for {branch}"
        with
        | :? OperationCanceledException ->
            Log.log "SyncEngine" $"Sync pipeline cancelled for {branch}"
            post (CompleteSync (branch, StepStatus.Cancelled))
        | ex ->
            Log.log "SyncEngine" $"Sync pipeline failed for {branch}: {ex.Message}"
            post (CompleteSync (branch, StepStatus.Failed ex.Message))
    }
