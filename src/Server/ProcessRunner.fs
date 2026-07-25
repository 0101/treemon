module Server.ProcessRunner

open System
open System.Diagnostics
open System.IO
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

type ArgumentListFailure =
    | StartFailed of string
    | TimedOut
    | CaptureLimitExceeded of CaptureStream

type ArgumentListOutput =
    { ExitCode: int
      Stdout: byte[]
      Stderr: byte[] }

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

let private truncate (s: string) =
    if s.Length > 200 then s[..199] + "..." else s

let private startAndCapture (timeoutMs: int) (context: string) (fileName: string) (arguments: string) (workingDirectory: string option) =
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
                    CreateNoWindow = true
                )

            workingDirectory |> Option.iter (fun dir -> psi.WorkingDirectory <- dir)

            use proc = Process.Start(psi)
            use cts = new CancellationTokenSource(timeoutMs)
            let ct = cts.Token

            let! waitResult =
                async {
                    try
                        let stdoutTask = proc.StandardOutput.ReadToEndAsync(ct)
                        let stderrTask = proc.StandardError.ReadToEndAsync(ct)
                        do! proc.WaitForExitAsync(ct) |> Async.AwaitTask
                        let! stdout = stdoutTask |> Async.AwaitTask
                        let! stderr = stderrTask |> Async.AwaitTask
                        return Ok(proc.ExitCode, stdout.TrimEnd(), stderr.TrimEnd())
                    with :? System.OperationCanceledException ->
                        try proc.Kill(entireProcessTree = true) with _ -> ()
                        return Error $"Timed out after {timeoutMs}ms"
                }

            match waitResult with
            | Ok(exitCode, stdout, stderr) ->
                Log.log context $"{cmdString} -> exit {exitCode}, stdout: {truncate stdout}, stderr: {truncate stderr}"
                return Ok(exitCode, stdout, stderr)
            | Error msg ->
                Log.log context $"{cmdString} -> {msg}"
                return Error msg
        with :? System.ComponentModel.Win32Exception as ex ->
            Log.log context $"{cmdString} -> failed to start: {ex.Message}"
            return Error ex.Message
    }

let run (context: string) (fileName: string) (arguments: string) =
    async {
        let! result = startAndCapture defaultTimeoutMs context fileName arguments None

        return
            match result with
            | Ok(0, stdout, _) -> Some stdout
            | _ -> None
    }

let private toResult result =
    match result with
    | Ok(0, stdout, _) -> Ok stdout
    | Ok(_, _, stderr) -> Error stderr
    | Error msg -> Error msg

let runResult (context: string) (fileName: string) (arguments: string) (workingDirectory: string option) =
    async {
        let! result = startAndCapture defaultTimeoutMs context fileName arguments workingDirectory
        return toResult result
    }

/// Like `runResult` but with an explicit timeout, for long-running setup hooks
/// (e.g. `npm install`) that would otherwise be killed by the short default.
let runResultWithTimeout (timeoutMs: int) (context: string) (fileName: string) (arguments: string) (workingDirectory: string option) =
    async {
        let! result = startAndCapture timeoutMs context fileName arguments workingDirectory
        return toResult result
    }

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

                    Log.log
                        context
                        $"{fileName} ({arguments.Length} args) -> exit {proc.ExitCode}, stdout bytes: {stdout.Bytes.Length}, stderr bytes: {stderr.Bytes.Length}"

                    return
                        if stdout.LimitExceeded then
                            Error(CaptureLimitExceeded StandardOutput)
                        elif stderr.LimitExceeded then
                            Error(CaptureLimitExceeded StandardError)
                        else
                            Ok
                                { ExitCode = proc.ExitCode
                                  Stdout = stdout.Bytes
                                  Stderr = stderr.Bytes }
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
