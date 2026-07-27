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

type internal ResponseDeadline =
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

                    Log.log
                        context
                        $"{fileName} ({arguments.Length} args) -> exit {proc.ExitCode}, stdout bytes: {stdout.Bytes.Length}, stderr bytes: {stderr.Bytes.Length}{describeTruncation truncated}"

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

let runArgumentListWithTimeout
    (timeoutMs: int)
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    =
    runArgumentListCore
        timeoutMs
        5_000
        stdoutLimitBytes
        stderrLimitBytes
        context
        fileName
        arguments
        workingDirectory

let internal runArgumentListWithinResponseDeadline
    (deadline: ResponseDeadline)
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    =
    let processTimeoutMs =
        max
            0
            (responseDeadlineRemainingMs deadline
             - deadline.ResponseReserveMs
             - deadline.ShutdownReserveMs)

    if processTimeoutMs = 0 then
        async.Return(Error TimedOut)
    else
        runArgumentListCore
            processTimeoutMs
            deadline.ShutdownReserveMs
            stdoutLimitBytes
            stderrLimitBytes
            context
            fileName
            arguments
            workingDirectory

/// Argument-list process execution within the production 10-second response deadline.
let runArgumentList
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    =
    runArgumentListWithinResponseDeadline
        (createResponseDeadline argumentListResponseDeadlineMs)
        stdoutLimitBytes
        stderrLimitBytes
        context
        fileName
        arguments
        workingDirectory

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

/// Like `runArgumentListTextResult` but with an explicit timeout, for long-running setup hooks
/// (e.g. `npm install`) that would otherwise be killed by the short default.
let runArgumentListTextResultWithTimeout
    (timeoutMs: int)
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    : Async<Result<string, string>> =
    async {
        let! result =
            runArgumentListWithTimeout
                timeoutMs
                stdoutLimitBytes
                stderrLimitBytes
                context
                fileName
                arguments
                workingDirectory

        return
            match result with
            | Ok output when List.contains StandardOutput output.Truncated ->
                Error stdoutTruncatedMessage
            | Ok output when output.ExitCode = 0 -> Ok(decodeCapture output.Stdout)
            | Ok output -> Error(decodeCapture output.Stderr)
            | Error failure -> Error(describeFailure timeoutMs failure)
    }

/// Decoded stdout on exit 0; decoded stderr or a described runner failure otherwise.
let runArgumentListTextResult
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    =
    runArgumentListTextResultWithTimeout
        defaultTimeoutMs
        stdoutLimitBytes
        stderrLimitBytes
        context
        fileName
        arguments
        workingDirectory

/// Decoded stdout on exit 0; `None` for a non-zero exit or a runner failure.
let runArgumentListText
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    =
    async {
        let! result =
            runArgumentListTextResult
                stdoutLimitBytes
                stderrLimitBytes
                context
                fileName
                arguments
                workingDirectory

        return
            match result with
            | Ok stdout -> Some stdout
            | Error _ -> None
    }

/// Like `runArgumentListExitResult` but with an explicit timeout, for the post-fork setup hook.
let runArgumentListExitResultWithTimeout
    (timeoutMs: int)
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    : Async<Result<unit, string>> =
    async {
        let! result =
            runArgumentListWithTimeout
                timeoutMs
                stdoutLimitBytes
                stderrLimitBytes
                context
                fileName
                arguments
                workingDirectory

        return
            match result with
            | Ok output when output.ExitCode = 0 -> Ok()
            | Ok output -> Error(decodeCapture output.Stderr)
            | Error failure -> Error(describeFailure timeoutMs failure)
    }

/// For callers that run a command for its effect and discard its output: success is the child's
/// exit code alone, so a chatty-but-successful run stays a success no matter how much output the
/// capture had to drop. `Error` carries the child's stderr or a described runner failure.
let runArgumentListExitResult
    (stdoutLimitBytes: int)
    (stderrLimitBytes: int)
    (context: string)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string option)
    =
    runArgumentListExitResultWithTimeout
        defaultTimeoutMs
        stdoutLimitBytes
        stderrLimitBytes
        context
        fileName
        arguments
        workingDirectory
