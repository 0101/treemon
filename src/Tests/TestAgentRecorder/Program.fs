module TestAgentRecorder.Program

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading

[<DllImport("kernel32.dll", EntryPoint = "GetConsoleWindow")>]
extern nativeint private getConsoleWindow()

[<EntryPoint>]
let main args =
    try
        let recorderPath =
            Environment.GetEnvironmentVariable("TM_COPILOT_RECORDER")

        let consoleHandleFile =
            Environment.GetEnvironmentVariable("TM_CONSOLE_HANDLE_FILE")

        if not (String.IsNullOrWhiteSpace consoleHandleFile) then
            File.WriteAllText(
                consoleHandleFile,
                string (getConsoleWindow().ToInt64())
            )

            Thread.Sleep Timeout.Infinite
            0
        elif String.IsNullOrWhiteSpace recorderPath then
            eprintfn "TM_COPILOT_RECORDER is required"
            1
        else
            let payload =
                {| terminalSessionId =
                    Environment.GetEnvironmentVariable(
                        "TREEMON_TERMINAL_SESSION_ID"
                    )
                   worktreePath = Environment.CurrentDirectory
                   args = args |}

            File.AppendAllText(
                recorderPath,
                JsonSerializer.Serialize(payload) + Environment.NewLine,
                UTF8Encoding(false)
            )

            printfn
                $"RECORDED:{payload.terminalSessionId}"

            0
    with error ->
        eprintfn $"Test helper failed: {error.Message}"
        1
