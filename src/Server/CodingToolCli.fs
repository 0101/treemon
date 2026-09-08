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

// These strings are submitted to the embedded terminal, so they have to be quoted for the shell that
// terminal is running: PowerShell on Windows, a POSIX shell elsewhere. The two disagree on the one
// character that matters most here - PowerShell escapes a quote by doubling it, where a POSIX shell
// reads '' as ending the string - so emitting one form everywhere corrupted every prompt containing
// an apostrophe off Windows, and made a multi-line prompt a syntax error.
let private forPowerShell = OperatingSystem.IsWindows()

let private quoted (value: string) =
    if forPowerShell then
        "'" + value.Replace("'", "''") + "'"
    else
        "'" + value.Replace("'", @"'\''") + "'"

/// A control-bearing prompt is carried as inert base64 and decoded by the shell, so the command
/// submitted to the terminal stays one line either way.
let private promptArgument (prompt: string) =
    if prompt |> Seq.exists Char.IsControl then
        let encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes prompt)

        if forPowerShell then
            $"([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{encoded}')))"
        else
            // base64 -d is POSIX-portable in a way `echo -n` flags are not.
            $"\"$(printf %%s '{encoded}' | base64 -d)\""
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
          // Single-quoted like every other argument here: the double-quoted form escaped no quote at
          // all, so a prompt containing one broke the command on either shell.
          Args = $"-p {quoted prompt} --allow-all --no-ask-user -s --autopilot" }
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
