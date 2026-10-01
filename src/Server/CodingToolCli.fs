module Server.CodingToolCli

open Shared

type InvocationMode =
    | Start
    | NewSession of sessionId: string
    | Resume of sessionId: string option
    | NonInteractive of prompt: string

type CliInvocation =
    { Executable: string
      Args: string }

    member this.AsShellString = $"{this.Executable} {this.Args}"

let private escape (s: string) = s.Replace("'", "''")

let private quoted value = $"'{escape value}'"

let private withExtensionDiscovery arguments =
    $"--experimental {arguments}"

let build (provider: CodingToolProvider option) (mode: InvocationMode) : CliInvocation =
    let p = provider |> Option.defaultValue CodingToolProvider.Default

    match p, mode with
    | CodingToolProvider.CopilotCli, Start ->
        { Executable = "copilot"
          Args = "--yolo" }
    | CodingToolProvider.CopilotCli, (NewSession id | Resume (Some id)) ->
        { Executable = "copilot"
          Args = withExtensionDiscovery $"--yolo --session-id={quoted id}" }
    | CodingToolProvider.CopilotCli, Resume None ->
        { Executable = "copilot"
          Args = withExtensionDiscovery "--yolo --continue" }
    | CodingToolProvider.CopilotCli, NonInteractive prompt ->
        { Executable = "copilot"
          Args =
            withExtensionDiscovery
                $"-p \"{escape prompt}\" --allow-all --no-ask-user -s --autopilot" }
