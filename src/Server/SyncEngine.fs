module Server.SyncEngine

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Shared
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
type SyncStep =
    | CheckClean
    | Pull
    | Merge
    | Rebase
    | ResolveConflicts
    | Test
    | Commit
    | Push

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
    | UpdateProcessState of branch: string * SyncState
    | CompleteSync of branch: string
    | CancelSync of branch: string * AsyncReplyChannel<bool>

type SyncAgentState =
    { Processes: Map<string, SyncProcess> }

type SideEffect =
    | CancelCts of CancellationTokenSource
    | DisposeCts of CancellationTokenSource
    | LogMessage of string

let private mkEvent source message status =
    { Source = source
      Message = message
      Timestamp = DateTimeOffset.Now
      Status = Some status
      Duration = None }

let private isProcessRunning (state: SyncAgentState) (branch: string) =
    state.Processes
    |> Map.tryFind branch
    |> Option.exists (fun sp -> match sp.State with SyncState.Running _ -> true | _ -> false)

let processMessage (state: SyncAgentState) (msg: SyncMsg) : SyncAgentState * SideEffect list =
    match msg with
    | BeginSync (branch, reply) ->
        if isProcessRunning state branch then
            reply.Reply(Error "Sync already running for this branch")
            state, []
        else
            let cts = new CancellationTokenSource()
            let sp = { State = SyncState.Running SyncStep.CheckClean; CancellationTokenSource = cts }
            reply.Reply(Ok cts.Token)
            { state with Processes = state.Processes |> Map.add branch sp }, []

    | UpdateProcessState (branch, syncState) ->
        match state.Processes |> Map.tryFind branch with
        | Some sp ->
            let updated = { sp with State = syncState }
            { state with Processes = state.Processes |> Map.add branch updated }, []
        | None -> state, []

    | CompleteSync branch ->
        match state.Processes |> Map.tryFind branch with
        | Some sp ->
            match sp.State with
            | SyncState.Completed _ | SyncState.Cancelled ->
                state, []
            | _ ->
                { state with Processes = state.Processes |> Map.remove branch }, [ DisposeCts sp.CancellationTokenSource ]
        | None -> state, []

    | CancelSync (branch, reply) ->
        match state.Processes |> Map.tryFind branch with
        | Some sp ->
            match sp.State with
            | SyncState.Running _ ->
                let finalSp = { sp with State = SyncState.Cancelled }
                reply.Reply(true)
                { state with Processes = state.Processes |> Map.add branch finalSp },
                [ LogMessage $"Cancelling sync for branch: {branch}"; CancelCts sp.CancellationTokenSource ]
            | _ ->
                reply.Reply(false)
                state, []
        | None ->
            reply.Reply(false)
            state, []

let private executeSideEffects (effects: SideEffect list) =
    effects
    |> List.iter (fun effect ->
        match effect with
        | CancelCts cts ->
            try cts.Cancel() with _ -> ()
        | DisposeCts cts ->
            try cts.Dispose() with _ -> ()
        | LogMessage msg ->
            Log.log "SyncEngine" msg)

let createSyncAgent () : MailboxProcessor<SyncMsg> =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (state: SyncAgentState) =
            async {
                let! msg = inbox.Receive()
                let newState, sideEffects = processMessage state msg
                executeSideEffects sideEffects
                return! loop newState
            }
        loop { Processes = Map.empty })

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

let private shellCommand (command: string) : string * string =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "cmd", $"/c {command}"
    else "sh", $"-c \"{command}\""

let private truncateStderr(stderr: string) (maxLen: int) : string =
    if stderr = "" then "" else stderr[..min (maxLen - 1) (stderr.Length - 1)]

let testFailureLogRelPath = TestFailureLog.relPath

let testFailureLogPath (worktreePath: string) =
    Path.Combine(worktreePath, testFailureLogRelPath)

let private saveTestFailureLog (worktreePath: string) (command: string) (proc: ProcessResult) =
    try
        let logPath = testFailureLogPath worktreePath
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)) |> ignore
        let nl = Environment.NewLine
        let content = $"Command: {command}{nl}Exit code: {proc.ExitCode}{nl}{nl}--- stdout ---{nl}{proc.Stdout}{nl}{nl}--- stderr ---{nl}{proc.Stderr}"
        File.WriteAllText(logPath, content)
        Log.log "SyncEngine" $"Saved test failure log to {logPath}"
    with ex ->
        Log.log "SyncEngine" $"Failed to save test failure log: {ex.Message}"

let private deleteTestFailureLog (worktreePath: string) =
    try
        let logPath = testFailureLogPath worktreePath
        if File.Exists(logPath) then
            File.Delete(logPath)
            Log.log "SyncEngine" $"Cleaned up test failure log: {logPath}"
    with ex ->
        Log.log "SyncEngine" $"Failed to delete test failure log: {ex.Message}"

let buildFetchArgs (upstreamRemote: string) = $"fetch {upstreamRemote}"

/// Sinks the sync pipeline writes to, decoupling it from the concrete agents:
/// card events flow to the CardEventLog, process-state and completion to the sync
/// agent. Wired in `WorktreeApi` where both agents are in scope.
type PipelineSinks =
    { PushEvent: string -> CardEvent -> unit
      SetProcessState: string -> SyncState -> unit
      Complete: string -> unit }

type StepContext =
    { Sinks: PipelineSinks
      Branch: string
      WorktreePath: string
      Ct: CancellationToken }

module private PipelineSteps =

    let formatExitError (proc: ProcessResult) =
        $"exit {proc.ExitCode}: {truncateStderr proc.Stderr 200}"

    let runStep (ctx: StepContext) (step: SyncStep) (cmd: string) (args: string) (check: ProcessResult -> Result<'a, string>) : Async<Result<'a, StepStatus>> =
        async {
            let cmdString = $"{cmd} {args}"
            ctx.Sinks.SetProcessState ctx.Branch (SyncState.Running step)
            ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{step}" cmdString StepStatus.Running)

            let! result = runProcess ctx.WorktreePath cmd args ctx.Ct

            match result with
            | Error msg ->
                let status = StepStatus.Failed msg
                ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{step}" cmdString status)
                return Error status
            | Ok proc ->
                match check proc with
                | Ok value ->
                    ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{step}" cmdString StepStatus.Succeeded)
                    return Ok value
                | Error msg ->
                    let status = StepStatus.Failed msg
                    ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{step}" cmdString status)
                    return Error status
        }

    let checkExitCode (proc: ProcessResult) =
        if proc.ExitCode = 0 then Ok ()
        else Error (formatExitError proc)

    let checkClean (ctx: StepContext) =
        runStep ctx SyncStep.CheckClean "git" "status --porcelain --untracked-files=no" (fun proc ->
            if proc.ExitCode <> 0 then Error (formatExitError proc)
            elif proc.Stdout <> "" then Error "Working tree is dirty"
            else Ok ())

    let fetch (ctx: StepContext) (upstreamRemote: string) =
        runStep ctx SyncStep.Pull "git" (buildFetchArgs upstreamRemote) checkExitCode

    let merge (ctx: StepContext) (upstreamRemote: string) (baseBranch: string) : Async<Result<bool, StepStatus>> =
        let mergeTarget = GitWorktree.mainRef upstreamRemote baseBranch
        let cmdString = $"git merge {mergeTarget}"
        async {
            ctx.Sinks.SetProcessState ctx.Branch (SyncState.Running SyncStep.Merge)
            ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{SyncStep.Merge}" cmdString StepStatus.Running)

            let! result = runProcess ctx.WorktreePath "git" $"merge {mergeTarget}" ctx.Ct

            match result with
            | Error msg ->
                let status = StepStatus.Failed msg
                ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{SyncStep.Merge}" cmdString status)
                return Error status
            | Ok proc when proc.ExitCode = 0 ->
                ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{SyncStep.Merge}" cmdString StepStatus.Succeeded)
                return Ok false
            | Ok proc ->
                ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{SyncStep.Merge}" cmdString (StepStatus.Failed (formatExitError proc)))
                return Ok true
        }

    let resolveConflicts (ctx: StepContext) (provider: Shared.CodingToolProvider option) =
        let conflictPrompt =
            match provider |> Option.defaultValue Shared.CodingToolProvider.Default with
            | Shared.CodingToolProvider.Claude -> "/conflict"
            | Shared.CodingToolProvider.Copilot -> "use conflict skill to resolve conflicts"
        let inv = CodingToolCli.build provider (CodingToolCli.NonInteractive conflictPrompt)
        runStep ctx SyncStep.ResolveConflicts inv.Executable inv.Args checkExitCode

    let runTests (ctx: StepContext) (repoRoot: string) : Async<Result<unit, StepStatus>> =
        async {
            match TreemonConfig.readTestCommand repoRoot with
            | None ->
                Log.log "SyncEngine" $"No testCommand configured in {repoRoot}, skipping tests"
                deleteTestFailureLog ctx.WorktreePath
                ctx.Sinks.PushEvent ctx.Branch (mkEvent $"{SyncStep.Test}" "not configured" StepStatus.NotConfigured)
                return Ok ()
            | Some testCommand ->
                let fileName, args = shellCommand testCommand
                deleteTestFailureLog ctx.WorktreePath
                return! runStep ctx SyncStep.Test fileName args (fun proc ->
                    if proc.ExitCode = 0 then Ok ()
                    else
                        saveTestFailureLog ctx.WorktreePath testCommand proc
                        Error (formatExitError proc))
        }

    let commitIfNeeded (ctx: StepContext) : Async<Result<unit, StepStatus>> =
        async {
            let! diffResult = runProcess ctx.WorktreePath "git" "diff --cached --quiet" ctx.Ct |> Async.map Result.toOption
            match diffResult with
            | Some proc when proc.ExitCode = 1 ->
                return! runStep ctx SyncStep.Commit "git" "commit --no-edit" checkExitCode
            | _ -> return Ok ()
        }

    let push (ctx: StepContext) =
        runStep ctx SyncStep.Push "git" "push" checkExitCode

    let rebase (ctx: StepContext) (upstreamRemote: string) (baseBranch: string) =
        let mergeTarget = GitWorktree.mainRef upstreamRemote baseBranch
        runStep ctx SyncStep.Rebase "git" $"rebase {mergeTarget}" checkExitCode


let executeSyncPipeline (sinks: PipelineSinks) (branch: string) (worktreePath: string) (repoRoot: string) (provider: Shared.CodingToolProvider option) (upstreamRemote: string) (baseBranch: string) (prStatus: PrStatus) (ct: CancellationToken) : Async<unit> =
    let ctx = { Sinks = sinks; Branch = branch; WorktreePath = worktreePath; Ct = ct }

    let pipeline () =
        asyncResult {
            do! PipelineSteps.checkClean ctx
            do! PipelineSteps.fetch ctx upstreamRemote
            let mainRef = GitWorktree.mainRef upstreamRemote baseBranch
            let! commitCount = GitWorktree.getCommitCount ctx.WorktreePath mainRef |> Async.map Ok
            if commitCount = 0 then
                do! PipelineSteps.rebase ctx upstreamRemote baseBranch
            else
                let! hasConflicts = PipelineSteps.merge ctx upstreamRemote baseBranch
                if hasConflicts then
                    do! PipelineSteps.resolveConflicts ctx provider
                do! PipelineSteps.runTests ctx repoRoot
                do! PipelineSteps.commitIfNeeded ctx
                match prStatus with
                | HasPr _ -> do! PipelineSteps.push ctx
                | NoPr -> ()
        }

    async {
        try
            Log.log "SyncEngine" $"Starting sync pipeline for {branch} at {worktreePath}"
            let! _ = pipeline ()
            sinks.Complete branch
            Log.log "SyncEngine" $"Sync pipeline completed for {branch}"
        with
        | :? OperationCanceledException ->
            Log.log "SyncEngine" $"Sync pipeline cancelled for {branch}"
            sinks.Complete branch
        | ex ->
            Log.log "SyncEngine" $"Sync pipeline failed for {branch}: {ex.Message}"
            sinks.Complete branch
    }
