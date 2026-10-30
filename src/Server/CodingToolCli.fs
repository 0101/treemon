module Server.CodingToolCli

open System
open Shared

/// A nonempty model identifier safe through both PowerShell and Windows CMD.
type ModelId = private ModelId of string

module ModelId =
    let create (value: string) : Result<ModelId, PromptedLaunchError> =
        if
            not (String.IsNullOrEmpty value)
            && (value |> Seq.forall (fun character ->
                Char.IsAsciiLetterOrDigit character || "._-/:+".Contains character))
        then
            Ok(ModelId value)
        else
            Error PromptedLaunchError.InvalidModelId

type InvocationMode =
    | Start
    | NewSession of model: ModelId option
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
    | CodingToolProvider.CopilotCli, NewSession model ->
        let modelArgument =
            model
            |> Option.map (fun (ModelId value) -> $" --model={quoted value}")
            |> Option.defaultValue ""

        { Executable = "copilot"
          Args = withExtensionDiscovery $"--yolo{modelArgument}" }
    | CodingToolProvider.CopilotCli, Resume (Some id) ->
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
