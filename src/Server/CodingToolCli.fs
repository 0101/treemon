module Server.CodingToolCli

open Shared

type InvocationMode =
    | Interactive of prompt: string
    | Resume of sessionId: string option
    | NonInteractive of prompt: string

type CliInvocation =
    { Executable: string
      Args: string }

    member this.AsShellString = $"{this.Executable} {this.Args}"

// Injection-safety chokepoint: every value interpolated into an Args string MUST be wrapped in
// single quotes with embedded single quotes doubled, so a hostile value (';', newline, '$(...)')
// cannot break out of the quoted literal once the shell string is embedded into the pwsh
// -EncodedCommand script by SessionManager.buildScript.
let private escape (s: string) = s.Replace("'", "''")

let build (provider: CodingToolProvider option) (mode: InvocationMode) : CliInvocation =
    let p = provider |> Option.defaultValue CodingToolProvider.Default

    match p, mode with
    | CodingToolProvider.CopilotCli, Interactive prompt ->
        { Executable = "copilot"
          Args = $"--yolo -i '{escape prompt}'" }
    | CodingToolProvider.CopilotCli, Resume (Some id) ->
        { Executable = "copilot"
          Args = $"--yolo --resume '{escape id}'" }
    | CodingToolProvider.CopilotCli, Resume None ->
        { Executable = "copilot"
          Args = "--yolo --continue" }
    | CodingToolProvider.CopilotCli, NonInteractive prompt ->
        { Executable = "copilot"
          Args = $"-p \"{escape prompt}\" --allow-all --no-ask-user -s --autopilot" }
