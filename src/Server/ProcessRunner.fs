module Server.ProcessRunner

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading

let private defaultTimeoutMs = 60_000
let internal argumentListResponseDeadlineMs = 10_000
let private maxArgumentListShutdownMs = 250
let private maxArgumentListResponseReserveMs = 500

type ResponseDeadline =
    private
        { ExpiresAt: int64
          ResponseReserveMs: int
          ShutdownReserveMs: int }

type CaptureStream =
    | StandardOutput
    | StandardError

/// A run that produced no exit code at all. Reaching a capture limit is deliberately not here: the
/// child still ran and exited, so that outcome is a successful capture carrying `Truncated`.
type ArgumentListFailure =
    | StartFailed of string
    | TimedOut

type ArgumentListOutput =
    { ExitCode: int
      Stdout: byte[]
      Stderr: byte[]
      /// Streams whose capture hit its byte limit, stdout first. Truncation is a property of the
      /// captured bytes, not a verdict on the run: it makes parsed stdout unusable, but is
      /// irrelevant to a caller that only reads the exit code.
      Truncated: CaptureStream list }

type private BoundedCapture =
    { Bytes: byte[]
      LimitExceeded: bool }

/// How many bytes of each stream a run may keep. The right cap depends on the operation, so this
/// is always chosen explicitly — the presets below name the three policies actually in use.
type CaptureLimits =
    { StdoutBytes: int
      StderrBytes: int }

module CaptureLimits =
    /// Status collectors, JSON output, and diff summaries: large stdout, small stderr.
    let data =
        { StdoutBytes = 16 * 1024 * 1024
          StderrBytes = 64 * 1024 }

    /// Short single-value reads such as a branch name or a resolved ref.
    let small =
        { StdoutBytes = 64 * 1024
          StderrBytes = 64 * 1024 }

    /// Probes that only ask "was there any output at all".
    let tiny = { StdoutBytes = 1024; StderrBytes = 1024 }

/// Where a run's time budget comes from. `SharedDeadline` is the only case that spends a budget
/// established before the call, so several sequential runs can share one response deadline.
type Deadline =
    | DefaultTimeout
    | Timeout of ms: int
    | InteractiveDeadline
    | SharedDeadline of ResponseDeadline

/// Everything about a run except its arguments. Build one per command per module and override
/// individual fields at the call sites that differ.
type Spawn =
    { FileName: string
      Context: string
      Limits: CaptureLimits
      Deadline: Deadline
      WorkingDirectory: string option }

module Spawn =
    /// A data-collecting run of `fileName` on the 60 s default, logged under the executable's name.
    let create fileName =
        { FileName = fileName
          Context = fileName
          Limits = CaptureLimits.data
          Deadline = DefaultTimeout
          WorkingDirectory = None }

let private responseReserveMs responseDeadlineMs =
    min
        maxArgumentListResponseReserveMs
        (max 1 (responseDeadlineMs / 4))

let private shutdownReserveMs responseDeadlineMs =
    min
        maxArgumentListShutdownMs
        (max 1 (responseDeadlineMs / 8))

let internal createResponseDeadline responseDeadlineMs =
    let durationMs = max 1 responseDeadlineMs

    { ExpiresAt =
        Stopwatch.GetTimestamp()
        + int64 durationMs * Stopwatch.Frequency / 1_000L
      ResponseReserveMs = responseReserveMs durationMs
      ShutdownReserveMs = shutdownReserveMs durationMs }

let internal responseDeadlineRemainingMs deadline =
    let remainingTicks =
        deadline.ExpiresAt - Stopwatch.GetTimestamp()

    if remainingTicks <= 0L then
        0
    else
        int
            ((remainingTicks * 1_000L
              + Stopwatch.Frequency
              - 1L)
             / Stopwatch.Frequency)

let internal responseDeadlineOperationRemainingMs deadline =
    max
        0
        (responseDeadlineRemainingMs deadline
         - deadline.ResponseReserveMs)

let internal responseDeadlineCanContinue deadline =
    responseDeadlineOperationRemainingMs deadline > 0

let rec private drainBoundedCapture
    (stream: Stream)
    (maxBytes: int)
    (cancellationToken: CancellationToken)
    (captured: MemoryStream)
    (buffer: byte[])
    limitExceeded
    =
    task {
        let! count = stream.ReadAsync(buffer.AsMemory(), cancellationToken)

        if count = 0 then
            return
                { Bytes = captured.ToArray()
                  LimitExceeded = limitExceeded }
        else
            let remaining = maxBytes - int captured.Length
            let captureCount = min count (max 0 remaining)

            if captureCount > 0 then
                do! captured.WriteAsync(buffer.AsMemory(0, captureCount), cancellationToken)

            // Continue draining after the cap so a producer cannot block on a full pipe.
            return!
                drainBoundedCapture
                    stream
                    maxBytes
                    cancellationToken
                    captured
                    buffer
                    (limitExceeded || count > captureCount)
    }

let private captureBounded
    (stream: Stream)
    (maxBytes: int)
    (cancellationToken: CancellationToken)
    =
    task {
        use captured = new MemoryStream(min maxBytes (64 * 1024))
        let buffer = Array.zeroCreate<byte> (64 * 1024)

        return!
            drainBoundedCapture
                stream
                maxBytes
                cancellationToken
                captured
                buffer
                false
    }

let private killProcessTree (proc: Process) =
    try
        if not proc.HasExited then
            proc.Kill(entireProcessTree = true)
    with _ ->
        ()

let private observeCapture (captureTask: Tasks.Task<BoundedCapture>) =
    task {
        try
            let! _ = captureTask
            return ()
        with _ ->
            return ()
    }

let private observeCapturesWithin
    timeoutMs
    (captureTasks: Tasks.Task<BoundedCapture> array)
    =
    task {
        if timeoutMs > 0 then
            let observation =
                captureTasks
                |> Array.map observeCapture
                |> Tasks.Task.WhenAll

            let! completed =
                Tasks.Task.WhenAny(
                    observation,
                    Tasks.Task.Delay(timeoutMs)
                )

            if obj.ReferenceEquals(completed, observation) then
                let! _ = observation
                return ()
    }

let private describeTruncation streams =
    match streams with
    | [] -> ""
    | _ ->
        let names =
            streams
            |> List.map (function
                | StandardOutput -> "stdout"
                | StandardError -> "stderr")
            |> String.concat "+"

        $", truncated: {names}"

let internal shouldLogCompletion exitCode wasTruncated elapsed =
    exitCode <> 0 || wasTruncated || Log.isSlowOperation elapsed

/// Runs a process without shell argument parsing. Output capture is bounded and
/// timeout cancellation terminates the complete process tree.
let private runArgumentListCore
    (timeoutMs: int)
    (shutdownTimeoutMs: int)
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    : Async<Result<ArgumentListOutput, ArgumentListFailure>> =
    async {
        let executionStopwatch = Stopwatch.StartNew()

        try
            let psi =
                ProcessStartInfo(
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            arguments |> List.iter psi.ArgumentList.Add
            workingDirectory |> Option.iter (fun dir -> psi.WorkingDirectory <- dir)

            use proc = new Process(StartInfo = psi)

            if not (proc.Start()) then
                return Error(StartFailed "Process did not start")
            else
                use cts = new CancellationTokenSource()

                let remainingTimeoutMs =
                    timeoutMs
                    - int executionStopwatch.ElapsedMilliseconds

                if remainingTimeoutMs <= 0 then
                    cts.Cancel()
                else
                    cts.CancelAfter(remainingTimeoutMs)

                let stdoutTask = captureBounded proc.StandardOutput.BaseStream stdoutLimitBytes cts.Token
                let stderrTask = captureBounded proc.StandardError.BaseStream stderrLimitBytes cts.Token

                try
                    do! proc.WaitForExitAsync(cts.Token) |> Async.AwaitTask
                    let! stdout = stdoutTask |> Async.AwaitTask
                    let! stderr = stderrTask |> Async.AwaitTask

                    let truncated =
                        [ if stdout.LimitExceeded then StandardOutput
                          if stderr.LimitExceeded then StandardError ]

                    executionStopwatch.Stop()

                    if shouldLogCompletion proc.ExitCode (not (List.isEmpty truncated)) executionStopwatch.Elapsed then
                        Log.log
                            context
                            $"{fileName} ({arguments.Length} args) -> exit {proc.ExitCode} in {executionStopwatch.ElapsedMilliseconds}ms, stdout bytes: {stdout.Bytes.Length}, stderr bytes: {stderr.Bytes.Length}{describeTruncation truncated}"

                    // The child exited, so the exit code is the answer. A caller that parses the
                    // captured bytes decides for itself whether truncation invalidates that answer.
                    return
                        Ok
                            { ExitCode = proc.ExitCode
                              Stdout = stdout.Bytes
                              Stderr = stderr.Bytes
                              Truncated = truncated }
                with :? OperationCanceledException ->
                    killProcessTree proc

                    let remainingShutdownMs () =
                        max
                            0
                            (timeoutMs
                             + shutdownTimeoutMs
                             - int executionStopwatch.ElapsedMilliseconds)

                    let exitWaitMs = remainingShutdownMs ()

                    if exitWaitMs > 0 then
                        use killCts =
                            new CancellationTokenSource(exitWaitMs)

                        try
                            do!
                                proc.WaitForExitAsync(killCts.Token)
                                |> Async.AwaitTask
                        with :? OperationCanceledException ->
                            ()

                    do!
                        observeCapturesWithin
                            (remainingShutdownMs ())
                            [| stdoutTask; stderrTask |]
                        |> Async.AwaitTask

                    Log.log context $"{fileName} ({arguments.Length} args) -> timed out after {timeoutMs}ms"
                    return Error TimedOut
        with :? ComponentModel.Win32Exception as ex ->
            Log.log context $"{fileName} ({arguments.Length} args) -> failed to start"
            return Error(StartFailed ex.Message)
    }

let private defaultShutdownTimeoutMs = 5_000

/// A response deadline yields the time left after reserving room to send the response and to reap
/// a killed child. `None` means the budget is already spent.
let private responseDeadlineBudget deadline =
    let processTimeoutMs =
        max
            0
            (responseDeadlineRemainingMs deadline
             - deadline.ResponseReserveMs
             - deadline.ShutdownReserveMs)

    if processTimeoutMs = 0 then
        None
    else
        Some(processTimeoutMs, deadline.ShutdownReserveMs)

let private resolveDeadline deadline =
    match deadline with
    | DefaultTimeout -> Some(defaultTimeoutMs, defaultShutdownTimeoutMs)
    | Timeout ms -> Some(ms, defaultShutdownTimeoutMs)
    | InteractiveDeadline ->
        responseDeadlineBudget (createResponseDeadline argumentListResponseDeadlineMs)
    | SharedDeadline deadline -> responseDeadlineBudget deadline

/// The run plus the budget it was given, so failure messages can name the timeout that applied.
let private runWithBudget (spawn: Spawn) (arguments: string list) =
    match resolveDeadline spawn.Deadline with
    | None -> 0, async.Return(Error TimedOut)
    | Some(timeoutMs, shutdownTimeoutMs) ->
        timeoutMs,
        runArgumentListCore
            timeoutMs
            shutdownTimeoutMs
            spawn.Limits.StdoutBytes
            spawn.Limits.StderrBytes
            spawn.Context
            spawn.FileName
            arguments
            spawn.WorkingDirectory

/// Exit code with raw stdout/stderr bytes, for callers that parse machine output themselves.
let capture (spawn: Spawn) (arguments: string list) =
    runWithBudget spawn arguments |> snd

/// Status collectors want text, not bytes: invalid sequences become replacement
/// characters rather than an error, which the strict diff decoder deliberately does not do.
let private decodeCapture (bytes: byte[]) =
    Encoding.UTF8.GetString(bytes).TrimEnd()

let private describeFailure (timeoutMs: int) failure =
    match failure with
    | StartFailed message -> $"Failed to start process: {message}"
    | TimedOut -> $"Timed out after {timeoutMs}ms"

/// Text callers parse the stdout they receive, so a truncated capture is not a shorter answer —
/// it is a wrong one, and must fail rather than pass as a complete string.
let private stdoutTruncatedMessage =
    "Standard output exceeded its capture limit"

/// Decoded stdout on exit 0; decoded stderr or a described runner failure otherwise.
let textResult (spawn: Spawn) (arguments: string list) : Async<Result<string, string>> =
    async {
        let timeoutMs, run = runWithBudget spawn arguments
        let! result = run

        return
            match result with
            // A failing command's stderr is the diagnostic, and stdout is never returned on this
            // path, so a truncated stdout capture must not mask it.
            | Ok output when output.ExitCode <> 0 -> Error(decodeCapture output.Stderr)
            | Ok output when List.contains StandardOutput output.Truncated ->
                Error stdoutTruncatedMessage
            | Ok output -> Ok(decodeCapture output.Stdout)
            | Error failure -> Error(describeFailure timeoutMs failure)
    }

/// Decoded stdout on exit 0; `None` for a non-zero exit or a runner failure.
let text (spawn: Spawn) (arguments: string list) =
    async {
        let! result = textResult spawn arguments

        return
            match result with
            | Ok stdout -> Some stdout
            | Error _ -> None
    }

/// For callers that run a command for its effect and discard its output: success is the child's
/// exit code alone, so a chatty-but-successful run stays a success no matter how much output the
/// capture had to drop. `Error` carries the child's stderr or a described runner failure.
let exitResult (spawn: Spawn) (arguments: string list) : Async<Result<unit, string>> =
    async {
        let timeoutMs, run = runWithBudget spawn arguments
        let! result = run

        return
            match result with
            | Ok output when output.ExitCode = 0 -> Ok()
            | Ok output -> Error(decodeCapture output.Stderr)
            | Error failure -> Error(describeFailure timeoutMs failure)
    }
