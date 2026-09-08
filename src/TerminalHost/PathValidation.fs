namespace TerminalHost

open System
open System.Diagnostics
open System.IO

[<RequireQualifiedAccess>]
type WorktreeValidationError =
    | InvalidPath
    | UnknownWorktree

[<RequireQualifiedAccess>]
module PathValidation =
    let private pathComparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let private canonicalText path =
        Path.GetFullPath(path)
        |> Path.TrimEndingDirectorySeparator

    let private exactGitTopLevel path =
        let marker = Path.Combine(path, ".git")

        if not (File.Exists marker || Directory.Exists marker) then
            false
        else
            try
                let startInfo =
                    ProcessStartInfo(
                        FileName = "git",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    )

                [ "-C"; path; "rev-parse"; "--show-toplevel" ]
                |> List.iter startInfo.ArgumentList.Add

                match Process.Start startInfo |> Option.ofObj with
                | None -> false
                | Some child ->
                    use child = child

                    if not (child.WaitForExit 5_000) then
                        child.Kill(entireProcessTree = true)
                        child.WaitForExit()
                        false
                    else
                        let topLevel = child.StandardOutput.ReadToEnd().Trim()
                        child.StandardError.ReadToEnd() |> ignore

                        child.ExitCode = 0
                        && not (String.IsNullOrWhiteSpace topLevel)
                        && String.Equals(canonicalText topLevel, path, pathComparison)
            with _ ->
                false

    // A directory root - a folder Treemon watches that is not a repository - is marked by the state
    // file its agent writes. TerminalHost depends on nothing of Treemon's, so it spells that name out
    // itself; a test binds the two spellings.
    let [<Literal>] private DirectoryStateFileName = ".treemon-state.json"

    let private isDirectoryRoot path =
        File.Exists(Path.Combine(path, DirectoryStateFileName))

    let validate path =
        try
            if
                String.IsNullOrWhiteSpace path
                || path.Length > 32_767
                || path.IndexOf('\u0000') >= 0
                || not (Path.IsPathFullyQualified path)
            then
                Error WorktreeValidationError.InvalidPath
            else
                let canonical = canonicalText path

                if not (Directory.Exists canonical) then
                    Error WorktreeValidationError.UnknownWorktree
                // A repository top-level or a marked directory. This check is what stops anything
                // reaching the control port from opening a shell in an arbitrary folder, so both arms
                // are deliberate opt-ins and an unmarked folder stays unreachable.
                elif not (exactGitTopLevel canonical || isDirectoryRoot canonical) then
                    Error WorktreeValidationError.UnknownWorktree
                else
                    Ok(CanonicalWorktree.create canonical)
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException ->
            Error WorktreeValidationError.InvalidPath
        | _ ->
            Error WorktreeValidationError.UnknownWorktree
