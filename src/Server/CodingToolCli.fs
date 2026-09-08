module Server.CodingToolCli

open System
open System.Text
open Shared

type InvocationMode =
    | Interactive of prompt: string
    | Resume of sessionId: string option
    | NonInteractive of prompt: string

type CliInvocation =
    { Executable: string
      Args: string }

    member this.AsShellString = $"{this.Executable} {this.Args}"

// Keep the readable single-quoted form for control-free values. Control-bearing prompts are
// decoded from inert base64 data so the emitted terminal command remains one line.
//
// Both forms are PowerShell: '' escapes a quote and the base64 decode is a .NET expression. That
// holds wherever the embedded terminal runs PowerShell, which off Windows it does not - a POSIX
// shell reads '' as ending the string. Launching an agent from a Linux terminal therefore needs
// shell-aware quoting here, for every provider, not just this one.
let private escape (s: string) = s.Replace("'", "''")

let private quoted value = $"'{escape value}'"

let private promptArgument (prompt: string) =
    if prompt |> Seq.exists Char.IsControl then
        let encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes prompt)
        $"([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{encoded}')))"
    else
        quoted prompt

let build (provider: CodingToolProvider option) (mode: InvocationMode) : CliInvocation =
    let p = provider |> Option.defaultValue CodingToolProvider.Default

    match p, mode with
    | CodingToolProvider.CopilotCli, Interactive prompt ->
        { Executable = "copilot"
          Args = $"--yolo -i {promptArgument prompt}" }
    | CodingToolProvider.CopilotCli, Resume (Some id) ->
        { Executable = "copilot"
          Args = $"--yolo --resume {quoted id}" }
    | CodingToolProvider.CopilotCli, Resume None ->
        { Executable = "copilot"
          Args = "--yolo --continue" }
    | CodingToolProvider.CopilotCli, NonInteractive prompt ->
        { Executable = "copilot"
          Args = $"-p \"{escape prompt}\" --allow-all --no-ask-user -s --autopilot" }
    // Claude Code takes the prompt positionally when interactive and behind -p when not. Permissions
    // are bypassed to match the Copilot arms: an agent Treemon launches runs unattended, so a
    // permission prompt nobody is watching would simply hang the session.
    | CodingToolProvider.ClaudeCode, Interactive prompt ->
        { Executable = "claude"
          Args = $"--permission-mode bypassPermissions {promptArgument prompt}" }
    | CodingToolProvider.ClaudeCode, Resume (Some id) ->
        { Executable = "claude"
          Args = $"--permission-mode bypassPermissions --resume {quoted id}" }
    | CodingToolProvider.ClaudeCode, Resume None ->
        { Executable = "claude"
          Args = "--permission-mode bypassPermissions --continue" }
    | CodingToolProvider.ClaudeCode, NonInteractive prompt ->
        { Executable = "claude"
          Args = $"--permission-mode bypassPermissions -p {promptArgument prompt}" }
