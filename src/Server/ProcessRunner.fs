module Server.ProcessRunner

open Fli

let private truncate (s: string) =
    if s.Length > 200 then s.Substring(0, 200) + "..." else s

let private handleOutput (context: string) (cmdString: string) (output: Domain.Output) =
    match output.ExitCode, output.Text with
    | 0, Some text ->
        Log.log context (sprintf "%s -> exit 0, stdout: %s" cmdString (truncate text))
        Some text
    | exitCode, _ ->
        let error = output.Error |> Option.defaultValue ""
        Log.log context (sprintf "%s -> exit %d, stderr: %s" cmdString exitCode error)
        None

let runExec (context: string) (execContext: Domain.ExecContext) =
    async {
        let cmdString = Command.toString execContext

        try
            let! output = Command.executeAsync execContext
            return handleOutput context cmdString output
        with :? System.ComponentModel.Win32Exception as ex ->
            Log.log context (sprintf "%s -> failed to start: %s" cmdString ex.Message)
            return None
    }

let runShell (context: string) (shellContext: Domain.ShellContext) =
    async {
        let cmdString = Command.toString shellContext

        try
            let! output = Command.executeAsync shellContext
            return handleOutput context cmdString output
        with :? System.ComponentModel.Win32Exception as ex ->
            Log.log context (sprintf "%s -> failed to start: %s" cmdString ex.Message)
            return None
    }
