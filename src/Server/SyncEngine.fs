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
    | LogMessage of string

let private mkEvent source message status =
    { Source = source
      Message = message
      Timestamp = DateTimeOffset.Now
      Status = Some status
      Duration = None }

let private clearRunningEvents (events: CardEvent list) =
    events |> List.filter (fun evt -> evt.Status <> Some StepStatus.Running)

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
            let newState =
                { Processes = state.Processes |> Map.add branch sp
                  Events = state.Events |> Map.add branch [ mkEvent "sync" "Sync starting" StepStatus.Running ] }
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
                let cleanedEvents =
                    state.Events
                    |> Map.tryFind branch
                    |> Option.defaultValue []
                    |> clearRunningEvents
                let newState =
                    { Processes = state.Processes |> Map.remove branch
                      Events = state.Events |> Map.add branch cleanedEvents }
                newState, [ DisposeCts sp.CancellationTokenSource ]
        | None -> state, []

    | CancelSync branch ->
        match state.Processes |> Map.tryFind branch with
        | Some sp ->
            match sp.State with
            | SyncState.Running _ ->
                let finalSp = { sp with State = SyncState.Cancelled }
                let existing = state.Events |> Map.tryFind branch |> Option.defaultValue []
                let cleanedEvents = clearRunningEvents existing
                let newState =
                    { Processes = state.Processes |> Map.add branch finalSp
                      Events = state.Events |> Map.add branch (mkEvent "sync" "Sync cancelled" StepStatus.Cancelled :: cleanedEvents) }
                newState, [ LogMessage $"Cancelling sync for branch: {branch}"; CancelCts sp.CancellationTokenSource ]
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

type StepContext =
    { Post: SyncMsg -> unit
      Branch: string
      WorktreePath: string
      Ct: CancellationToken }

module private PipelineSteps =

    let formatExitError (proc: ProcessResult) =
        $"exit {proc.ExitCode}: {truncateStderr proc.Stderr 200}"

    let runStep (ctx: StepContext) (step: SyncStep) (cmd: string) (args: string) (check: ProcessResult -> Result<'a, string>) : Async<Result<'a, StepStatus>> =
        async {
            let cmdString = $"{cmd} {args}"
            ctx.Post (UpdateProcessState (ctx.Branch, SyncState.Running step))
            ctx.Post (PushEvent (ctx.Branch, mkEvent $"{step}" cmdString StepStatus.Running))

            let! result = runProcess ctx.WorktreePath cmd args ctx.Ct

            match result with
            | Error msg ->
                let status = StepStatus.Failed msg
                ctx.Post (PushEvent (ctx.Branch, mkEvent $"{step}" cmdString status))
                return Error status
            | Ok proc ->
                match check proc with
                | Ok value ->
                    ctx.Post (PushEvent (ctx.Branch, mkEvent $"{step}" cmdString StepStatus.Succeeded))
                    return Ok value
                | Error msg ->
                    let status = StepStatus.Failed msg
                    ctx.Post (PushEvent (ctx.Branch, mkEvent $"{step}" cmdString status))
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

    let merge (ctx: StepContext) (upstreamRemote: string) : Async<Result<bool, StepStatus>> =
        let mergeTarget = GitWorktree.mainRef upstreamRemote
        let cmdString = $"git merge {mergeTarget}"
        async {
            ctx.Post (UpdateProcessState (ctx.Branch, SyncState.Running SyncStep.Merge))
            ctx.Post (PushEvent (ctx.Branch, mkEvent $"{SyncStep.Merge}" cmdString StepStatus.Running))

            let! result = runProcess ctx.WorktreePath "git" $"merge {mergeTarget}" ctx.Ct

            match result with
            | Error msg ->
                let status = StepStatus.Failed msg
                ctx.Post (PushEvent (ctx.Branch, mkEvent $"{SyncStep.Merge}" cmdString status))
                return Error status
            | Ok proc when proc.ExitCode = 0 ->
                ctx.Post (PushEvent (ctx.Branch, mkEvent $"{SyncStep.Merge}" cmdString StepStatus.Succeeded))
                return Ok false
            | Ok proc ->
                ctx.Post (PushEvent (ctx.Branch, mkEvent $"{SyncStep.Merge}" cmdString (StepStatus.Failed (formatExitError proc))))
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
                ctx.Post (PushEvent (ctx.Branch, mkEvent $"{SyncStep.Test}" "not configured" StepStatus.NotConfigured))
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

    let rebase (ctx: StepContext) (upstreamRemote: string) =
        let mergeTarget = GitWorktree.mainRef upstreamRemote
        runStep ctx SyncStep.Rebase "git" $"rebase {mergeTarget}" checkExitCode


let executeSyncPipeline (post: SyncMsg -> unit) (branch: string) (worktreePath: string) (repoRoot: string) (provider: Shared.CodingToolProvider option) (upstreamRemote: string) (prStatus: PrStatus) (ct: CancellationToken) : Async<unit> =
    let ctx = { Post = post; Branch = branch; WorktreePath = worktreePath; Ct = ct }

    let pipeline () =
        asyncResult {
            do! PipelineSteps.checkClean ctx
            do! PipelineSteps.fetch ctx upstreamRemote
            let mainRef = GitWorktree.mainRef upstreamRemote
            let! commitCount = GitWorktree.getCommitCount ctx.WorktreePath mainRef |> Async.map Ok
            if commitCount = 0 then
                do! PipelineSteps.rebase ctx upstreamRemote
            else
                let! hasConflicts = PipelineSteps.merge ctx upstreamRemote
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
            let! result = pipeline ()
            match result with
            | Ok () -> post (CompleteSync (branch, StepStatus.Succeeded))
            | Error status -> post (CompleteSync (branch, status))
            Log.log "SyncEngine" $"Sync pipeline completed for {branch}"
        with
        | :? OperationCanceledException ->
            Log.log "SyncEngine" $"Sync pipeline cancelled for {branch}"
            post (CompleteSync (branch, StepStatus.Cancelled))
        | ex ->
            Log.log "SyncEngine" $"Sync pipeline failed for {branch}: {ex.Message}"
            post (CompleteSync (branch, StepStatus.Failed ex.Message))
    }
