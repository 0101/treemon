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

/// Every control character as printf's own octal escape, so the payload is inert text the shell
/// reassembles itself. Backslashes are doubled first, or one already in the prompt would be read as
/// the start of an escape.
let private forPrintf (value: string) =
    value
    |> Seq.map (fun character ->
        if character = '\\' then @"\\"
        elif Char.IsControl character then
            // \0ddd, the octal form %b is specified to take.
            @"\0" + Convert.ToString(int character, 8).PadLeft(3, '0')
        else
            string character)
    |> String.concat ""

/// A control-bearing prompt is carried as inert text and reassembled by the shell, so the command
/// submitted to the terminal stays one line either way.
///
/// Off Windows that is `printf %b` rather than base64. `base64` is not a POSIX utility at all, and
/// where it exists the decode flag is not agreed: GNU coreutils takes -d, BSD and macOS take -D. So
/// the command carrying a multi-line prompt worked on Linux and failed on a Mac - on the one input
/// that needed the encoding in the first place. `printf` is a shell builtin everywhere, and %b with
/// its \0ddd escapes is specified.
let private promptArgument (prompt: string) =
    if prompt |> Seq.exists Char.IsControl then
        if forPowerShell then
            let encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes prompt)
            $"([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{encoded}')))"
        else
            $"\"$(printf %%b {quoted (forPrintf prompt)})\""
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
