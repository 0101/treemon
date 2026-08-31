module TestAgentRecorder.Program

open System
open System.IO
open System.Text
open System.Text.Json

[<EntryPoint>]
let main args =
    try
        let recorderPath =
            Environment.GetEnvironmentVariable("TM_COPILOT_RECORDER")

        if String.IsNullOrWhiteSpace recorderPath then
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
        eprintfn $"Recorder failed: {error.Message}"
        1
