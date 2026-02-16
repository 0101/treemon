module Server.ProcessRunner

open System.Diagnostics

let private truncate (s: string) =
    if s.Length > 200 then s.Substring(0, 200) + "..." else s

let run (context: string) (fileName: string) (arguments: string) =
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
                    CreateNoWindow = true
                )

            use proc = Process.Start(psi)
            let stdoutTask = proc.StandardOutput.ReadToEndAsync()
            let stderrTask = proc.StandardError.ReadToEndAsync()
            do! proc.WaitForExitAsync() |> Async.AwaitTask
            let! stdout = stdoutTask |> Async.AwaitTask
            let! stderr = stderrTask |> Async.AwaitTask

            match proc.ExitCode with
            | 0 ->
                Log.log context (sprintf "%s -> exit 0, stdout: %s" cmdString (truncate (stdout.TrimEnd())))
                return Some(stdout.TrimEnd())
            | exitCode ->
                Log.log context (sprintf "%s -> exit %d, stderr: %s" cmdString exitCode (stderr.TrimEnd()))
                return None
        with :? System.ComponentModel.Win32Exception as ex ->
            Log.log context (sprintf "%s -> failed to start: %s" cmdString ex.Message)
            return None
    }
