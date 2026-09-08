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

    /// What ttyd is actually told to run. For PowerShell that is the configured shell itself, which
    /// parses its own arguments. A POSIX shell is reached through /bin/sh instead: the configured
    /// shell is whatever SHELL names, and fish, tcsh and nushell are all valid logins that do not
    /// parse `cd -- … && exec`. /bin/sh does, and then execs the configured shell, which only has to
    /// start interactively.
    let launchExecutable shell =
        match shell with
        | PowerShell path -> path
        | PosixShell _ -> "/bin/sh"

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
