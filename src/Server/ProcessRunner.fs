module Server.ProcessRunner

open System.Diagnostics
open System.Threading

let private defaultTimeoutMs = 60_000

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
