module Server.ProcessRunner

open System.Diagnostics

let private truncate (s: string) =
    if s.Length > 200 then s.[..199] + "..." else s

let private startAndCapture (context: string) (fileName: string) (arguments: string) =
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

            use proc = Process.Start(psi)
            let stdoutTask = proc.StandardOutput.ReadToEndAsync()
            let stderrTask = proc.StandardError.ReadToEndAsync()
            do! proc.WaitForExitAsync() |> Async.AwaitTask
            let! stdout = stdoutTask |> Async.AwaitTask
            let! stderr = stderrTask |> Async.AwaitTask

            Log.log context $"{cmdString} -> exit {proc.ExitCode}, stdout: {truncate (stdout.TrimEnd())}, stderr: {truncate (stderr.TrimEnd())}"
            return Ok(proc.ExitCode, stdout.TrimEnd(), stderr.TrimEnd())
        with :? System.ComponentModel.Win32Exception as ex ->
            Log.log context $"{cmdString} -> failed to start: {ex.Message}"
            return Error ex.Message
    }

let run (context: string) (fileName: string) (arguments: string) =
    async {
        let! result = startAndCapture context fileName arguments

        return
            match result with
            | Ok(0, stdout, _) -> Some stdout
            | _ -> None
    }

let runResult (context: string) (fileName: string) (arguments: string) =
    async {
        let! result = startAndCapture context fileName arguments

        return
            match result with
            | Ok(0, stdout, _) -> Ok stdout
            | Ok(_, _, stderr) -> Error stderr
            | Error msg -> Error msg
    }
