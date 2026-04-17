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

let private escape (s: string) = s.Replace("'", "''")

let build (provider: CodingToolProvider option) (mode: InvocationMode) : CliInvocation =
    let p = provider |> Option.defaultValue CodingToolProvider.Default

    match p, mode with
    | CodingToolProvider.Claude, Interactive prompt ->
        { Executable = "claude"
          Args = $"--dangerously-skip-permissions '{escape prompt}'" }
    | CodingToolProvider.Claude, Resume (Some id) ->
        { Executable = "claude"
          Args = $"--dangerously-skip-permissions --resume {id}" }
    | CodingToolProvider.Claude, Resume None ->
        { Executable = "claude"
          Args = "--dangerously-skip-permissions --continue" }
    | CodingToolProvider.Claude, NonInteractive prompt ->
        { Executable = "claude"
          Args = $"-p \"{escape prompt}\" --dangerously-skip-permissions" }
    | CodingToolProvider.Copilot, Interactive prompt ->
        { Executable = "copilot"
          Args = $"--yolo -i '{escape prompt}'" }
    | CodingToolProvider.Copilot, Resume (Some id) ->
        { Executable = "copilot"
          Args = $"--yolo --resume {id}" }
    | CodingToolProvider.Copilot, Resume None ->
        { Executable = "copilot"
          Args = "--yolo --continue" }
    | CodingToolProvider.Copilot, NonInteractive prompt ->
        { Executable = "copilot"
          Args = $"-p \"{escape prompt}\" --allow-all --no-ask-user -s --autopilot" }
