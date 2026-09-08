namespace TerminalHost

open System

/// The interactive shell ttyd runs inside a terminal. ttyd already starts its child in the worktree
/// via `-w`, but a shell profile is free to move it again, so each convention carries the arguments
/// that land the shell back in the worktree it was opened for.
type TerminalShell =
    | PowerShell of executable: string
    | PosixShell of executable: string

[<RequireQualifiedAccess>]
module TerminalShell =
    let [<Literal>] WorktreeEnvironmentVariable = "TREEMON_TERMINAL_WORKTREE"

    /// The host and the shell it launches always run on the same machine, so the platform alone
    /// decides which convention applies to the executable the host was configured with.
    let forCurrentPlatform executable =
        if OperatingSystem.IsWindows() then
            PowerShell executable
        else
            PosixShell executable

    let executable shell =
        match shell with
        | PowerShell path
        | PosixShell path -> path

    /// A POSIX shell takes the directory change as shell source rather than as an argument, so the
    /// executable is interpolated into a command string and has to survive a path containing a quote.
    let private singleQuoted (value: string) =
        let escaped = value.Replace("'", @"'\''")
        $"'{escaped}'"

    let arguments shell =
        match shell with
        | PowerShell _ ->
            [ "-WorkingDirectory"
              "."
              "-NoExit"
              "-Command"
              $"Set-Location -LiteralPath $env:{WorktreeEnvironmentVariable}" ]
        | PosixShell path ->
            // `exec` replaces this shell with the interactive one, so the worktree `cd` costs no
            // extra process and the pid ttyd owns stays the pid of the shell the user types into.
            [ "-c"
              $"cd -- \"${WorktreeEnvironmentVariable}\" && exec {singleQuoted path} -i" ]
