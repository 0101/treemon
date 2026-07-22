module Server.WorktreeDiff

open System
open System.IO
open System.Text
open FsToolkit.ErrorHandling

type WorktreeDiffStatus =
    | Added
    | Modified
    | Deleted
    | Renamed
    | Untracked

type WorktreeDiffEntry =
    { Path: string
      OldPath: string option
      Status: WorktreeDiffStatus }

type WorktreeDiffSummary =
    { BaseRef: string
      MergeBase: string
      Files: WorktreeDiffEntry list }

type WorktreeDiffOperation =
    | ResolveRemote
    | ResolveBase
    | ResolveMergeBase
    | EnumerateTracked
    | EnumerateUntracked
    | LoadFile

type WorktreeDiffError =
    | BaseNotFound of baseBranch: string * remoteRef: string
    | GitStartFailed of WorktreeDiffOperation
    | GitTimedOut of WorktreeDiffOperation
    | GitFailed of WorktreeDiffOperation * exitCode: int
    | GitCaptureLimitExceeded of WorktreeDiffOperation * ProcessRunner.CaptureStream
    | InvalidGitOutput of WorktreeDiffOperation
    | TooManyFiles of minimumCount: int
    | FileUnavailable

type WorktreeDiffFile =
    | Text of patch: string
    | DeletedFile of patch: string
    | Binary
    | Oversized
    | Truncated
    | Symlink of patch: string option

type private BoundedFileRead =
    | FileBytes of byte[]
    | FileTooLarge
    | FileReadFailed

let maxWorktreeDiffFiles = 1_000
let maxWorktreeDiffBytes = 2 * 1024 * 1024
let maxWorktreeDiffLines = 20_000

let private diffStderrLimitBytes = 64 * 1024
let private summaryCaptureLimitBytes = 16 * 1024 * 1024
let private smallGitCaptureLimitBytes = 64 * 1024
let private strictUtf8 = UTF8Encoding(false, true)
let private generatedDiffViewerPath =
    String.concat "/" [ ".agents"; "canvas"; "diff.html" ]

let private mapDiffProcessFailure
    (operation: WorktreeDiffOperation)
    (failure: ProcessRunner.ArgumentListFailure)
    =
    match failure with
    | ProcessRunner.StartFailed _ -> GitStartFailed operation
    | ProcessRunner.TimedOut -> GitTimedOut operation
    | ProcessRunner.CaptureLimitExceeded stream ->
        GitCaptureLimitExceeded(operation, stream)

let private runDiffGit
    (deadline: ProcessRunner.ResponseDeadline)
    (operation: WorktreeDiffOperation)
    (stdoutLimitBytes: int)
    (repoRoot: string)
    (arguments: string list)
    =
    async {
        let gitArguments =
            [ "-C"; repoRoot; "-c"; "core.quotepath=false" ] @ arguments

        let! result =
            ProcessRunner.runArgumentListWithinResponseDeadline
                deadline
                stdoutLimitBytes
                diffStderrLimitBytes
                "WorktreeDiff"
                "git"
                gitArguments
                None

        return
            match result with
            | Error failure -> Error(mapDiffProcessFailure operation failure)
            | Ok output when output.ExitCode <> 0 ->
                Log.log
                    "WorktreeDiff"
                    $"Git {operation} failed with exit {output.ExitCode} and {output.Stderr.Length} stderr bytes"

                Error(GitFailed(operation, output.ExitCode))
            | Ok output -> Ok output.Stdout
    }

let private decodeGitOutput
    (operation: WorktreeDiffOperation)
    (bytes: byte[])
    =
    try
        Ok(strictUtf8.GetString(bytes))
    with :? DecoderFallbackException ->
        Error(InvalidGitOutput operation)

let private trimSingleLine
    (operation: WorktreeDiffOperation)
    (bytes: byte[])
    =
    decodeGitOutput operation bytes
    |> Result.bind (fun output ->
        let lines =
            output.Trim().Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

        match lines with
        | [| line |] -> Ok line
        | _ -> Error(InvalidGitOutput operation))

let private runRefExists
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    (gitRef: string)
    =
    async {
        let! result =
            ProcessRunner.runArgumentListWithinResponseDeadline
                deadline
                smallGitCaptureLimitBytes
                diffStderrLimitBytes
                "WorktreeDiff"
                "git"
                [ "-C"
                  repoRoot
                  "rev-parse"
                  "--verify"
                  "--quiet"
                  gitRef ]
                None

        return
            match result with
            | Error failure -> Error(mapDiffProcessFailure ResolveBase failure)
            | Ok output when output.ExitCode = 0 -> Ok true
            | Ok output when output.ExitCode = 1 -> Ok false
            | Ok output ->
                Log.log
                    "WorktreeDiff"
                    $"Git {ResolveBase} failed with exit {output.ExitCode} and {output.Stderr.Length} stderr bytes"

                Error(GitFailed(ResolveBase, output.ExitCode))
    }

let private resolveDiffRemote
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    =
    async {
        match TreemonConfig.readUpstreamRemote repoRoot with
        | Some remote -> return Ok remote
        | None ->
            let! remotes =
                runDiffGit
                    deadline
                    ResolveRemote
                    smallGitCaptureLimitBytes
                    repoRoot
                    [ "remote" ]

            return
                remotes
                |> Result.bind (fun bytes ->
                    decodeGitOutput ResolveRemote bytes
                    |> Result.map (Some >> GitWorktree.selectUpstreamRemote None))
    }

let private resolveDiffBaseRef
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    (upstreamRemote: string)
    (baseBranch: string)
    =
    async {
        let remoteRef = GitWorktree.mainRef upstreamRemote baseBranch
        let! remoteExists =
            runRefExists
                deadline
                repoRoot
                $"refs/remotes/{remoteRef}"

        match remoteExists with
        | Error error -> return Error error
        | Ok remoteExists ->
            let! localResult =
                if remoteExists then
                    async.Return(Ok false)
                else
                    runRefExists
                        deadline
                        repoRoot
                        $"refs/heads/{baseBranch}"

            return
                match localResult with
                | Error error -> Error error
                | Ok localExists ->
                    GitWorktree.selectBaseRef
                        upstreamRemote
                        baseBranch
                        remoteExists
                        localExists
                    |> Result.requireSome (BaseNotFound(baseBranch, remoteRef))
    }

let private parseNulTokens
    (operation: WorktreeDiffOperation)
    (bytes: byte[])
    =
    decodeGitOutput operation bytes
    |> Result.map (fun output ->
        let tokens = output.Split([| '\000' |], StringSplitOptions.None)

        if tokens.Length = 0 then
            []
        elif tokens[tokens.Length - 1] = "" then
            if tokens.Length = 1 then
                []
            else
                tokens[..tokens.Length - 2] |> Array.toList
        else
            tokens |> Array.toList)

let private trackedEntry (status: char) (paths: string list) =
    match status, paths with
    | ('R' | 'C'), oldPath :: newPath :: rest ->
        Ok(
            { Path = newPath
              OldPath = Some oldPath
              Status = if status = 'R' then Renamed else Added },
            rest
        )
    | ('A' | 'M' | 'D' | 'T' | 'U' | 'X' | 'B'), path :: rest ->
        let diffStatus =
            match status with
            | 'A' -> Added
            | 'D' -> Deleted
            | _ -> Modified

        Ok(
            { Path = path
              OldPath = None
              Status = diffStatus },
            rest
        )
    | _ -> Error(InvalidGitOutput EnumerateTracked)

let private parseTrackedEntries (bytes: byte[]) =
    parseNulTokens EnumerateTracked bytes
    |> Result.bind (fun tokens ->
        let rec parse
            (entries: WorktreeDiffEntry list)
            (remaining: string list)
            =
            match remaining with
            | [] -> Ok(List.rev entries)
            | statusToken :: paths when statusToken.Length > 0 ->
                trackedEntry (statusToken.Chars(0)) paths
                |> Result.bind (fun (entry, rest) -> parse (entry :: entries) rest)
            | _ -> Error(InvalidGitOutput EnumerateTracked)

        parse [] tokens)

let private parseUntrackedEntries (bytes: byte[]) =
    parseNulTokens EnumerateUntracked bytes
    |> Result.map (
        List.map (fun path ->
            { Path = path
              OldPath = None
              Status = Untracked })
    )

let private sortDiffEntries (entries: WorktreeDiffEntry list) =
    entries
    |> List.sortWith (fun left right ->
        StringComparer.Ordinal.Compare(left.Path, right.Path))

let private excludeGeneratedDiffViewer (entries: WorktreeDiffEntry list) =
    entries
    |> List.filter (fun entry -> entry.Path <> generatedDiffViewerPath)

let internal getWorktreeDiffSummaryWithinDeadline
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    : Async<Result<WorktreeDiffSummary, WorktreeDiffError>> =
    asyncResult {
        let! upstreamRemote = resolveDiffRemote deadline repoRoot
        let baseBranch = TreemonConfig.readBaseBranch repoRoot
        let! baseRef =
            resolveDiffBaseRef
                deadline
                repoRoot
                upstreamRemote
                baseBranch

        let! mergeBaseBytes =
            runDiffGit
                deadline
                ResolveMergeBase
                smallGitCaptureLimitBytes
                repoRoot
                [ "merge-base"; "HEAD"; baseRef ]

        let! mergeBase = trimSingleLine ResolveMergeBase mergeBaseBytes

        let! trackedBytes =
            runDiffGit
                deadline
                EnumerateTracked
                summaryCaptureLimitBytes
                repoRoot
                [ "diff"
                  "--name-status"
                  "-z"
                  "--find-renames"
                  "--no-ext-diff"
                  "--no-textconv"
                  mergeBase ]

        let! tracked =
            parseTrackedEntries trackedBytes
            |> Result.map excludeGeneratedDiffViewer

        if tracked.Length > maxWorktreeDiffFiles then
            return! Error(TooManyFiles tracked.Length)

        let! untrackedBytes =
            runDiffGit
                deadline
                EnumerateUntracked
                summaryCaptureLimitBytes
                repoRoot
                [ "ls-files"
                  "--others"
                  "--exclude-standard"
                  "-z"
                  "--" ]

        let! untracked =
            parseUntrackedEntries untrackedBytes
            |> Result.map excludeGeneratedDiffViewer

        let files = tracked @ untracked

        if files.Length > maxWorktreeDiffFiles then
            return! Error(TooManyFiles files.Length)

        return
            { BaseRef = baseRef
              MergeBase = mergeBase
              Files = sortDiffEntries files }
    }

let getWorktreeDiffSummary (repoRoot: string) =
    getWorktreeDiffSummaryWithinDeadline
        (ProcessRunner.createResponseDeadline
            ProcessRunner.argumentListResponseDeadlineMs)
        repoRoot

let private diffLineCount (bytes: byte[]) =
    if bytes.Length = 0 then
        0
    else
        let newlineCount =
            bytes
            |> Array.sumBy (fun value -> if value = 0x0Auy then 1 else 0)

        if bytes[bytes.Length - 1] = 0x0Auy then newlineCount else newlineCount + 1

let private isBinaryPatch (patch: string) =
    patch.Split('\n')
    |> Array.exists (fun line ->
        line.StartsWith("Binary files ", StringComparison.Ordinal)
        && line.TrimEnd('\r').EndsWith(" differ", StringComparison.Ordinal))

let private isSymlinkPatch (patch: string) =
    patch.Split('\n')
    |> Array.takeWhile (fun line -> not (line.StartsWith("@@", StringComparison.Ordinal)))
    |> Array.exists (fun line ->
        let header = line.TrimEnd('\r')

        header.EndsWith(" 120000", StringComparison.Ordinal)
        || header = "old mode 120000"
        || header = "new mode 120000"
        || header = "new file mode 120000"
        || header = "deleted file mode 120000")

let private classifyTrackedPatch entry bytes =
    if diffLineCount bytes > maxWorktreeDiffLines then
        Ok Truncated
    else
        match decodeGitOutput LoadFile bytes with
        | Error _ -> Ok Binary
        | Ok patch when isBinaryPatch patch -> Ok Binary
        | Ok patch when entry.Status = Deleted -> Ok(DeletedFile patch)
        | Ok patch when isSymlinkPatch patch -> Ok(Symlink(Some patch))
        | Ok patch -> Ok(Text patch)

let private trackedDiffPaths entry =
    match entry.OldPath with
    | Some oldPath when oldPath <> entry.Path -> [ oldPath; entry.Path ]
    | _ -> [ entry.Path ]

let private getTrackedDiffFile
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    (mergeBase: string)
    (entry: WorktreeDiffEntry)
    =
    async {
        let! patchResult =
            runDiffGit
                deadline
                LoadFile
                maxWorktreeDiffBytes
                repoRoot
                ([ "diff"
                   "--no-ext-diff"
                   "--no-textconv"
                   "--find-renames"
                   "--full-index"
                   "--no-color"
                   mergeBase
                   "--" ]
                 @ trackedDiffPaths entry)

        return
            match patchResult with
            | Error(GitCaptureLimitExceeded(LoadFile, ProcessRunner.StandardOutput)) ->
                Ok Oversized
            | Error error -> Error error
            | Ok bytes when bytes.Length = 0 -> Error FileUnavailable
            | Ok bytes -> classifyTrackedPatch entry bytes
    }

let private resolveUntrackedPath (repoRoot: string) (relativePath: string) =
    try
        let root = Path.GetFullPath(repoRoot)
        let fullPath = Path.GetFullPath(Path.Combine(root, relativePath))
        let relative = Path.GetRelativePath(root, fullPath)
        let parentPrefix = ".." + string Path.DirectorySeparatorChar

        if
            Path.IsPathRooted(relative)
            || relative = ".."
            || relative.StartsWith(parentPrefix, StringComparison.Ordinal)
        then
            Error FileUnavailable
        else
            Ok fullPath
    with _ ->
        Error FileUnavailable

let private isSymbolicLink (fileInfo: FileInfo) =
    let hasLinkTarget =
        try
            not (isNull fileInfo.LinkTarget)
        with _ ->
            false

    if hasLinkTarget then
        true
    else
        try
            fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
        with _ ->
            false

let private readFileBounded (path: string) =
    try
        use stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite ||| FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan
            )

        use captured =
            new MemoryStream(
                min
                    maxWorktreeDiffBytes
                    (int (min stream.Length (int64 maxWorktreeDiffBytes)))
            )

        let buffer = Array.zeroCreate<byte> (64 * 1024)

        let rec read () =
            let count = stream.Read(buffer, 0, buffer.Length)

            if count = 0 then
                FileBytes(captured.ToArray())
            else
                let remaining = maxWorktreeDiffBytes - int captured.Length

                if count > remaining then
                    FileTooLarge
                else
                    captured.Write(buffer, 0, count)
                    read ()

        read ()
    with _ ->
        FileReadFailed

let private gitPatchPath prefix (path: string) =
    let value = prefix + path.Replace('\\', '/')

    let needsQuotes =
        value
        |> Seq.exists (fun character ->
            Char.IsWhiteSpace(character)
            || Char.IsControl(character)
            || character = '"'
            || character = '\\')

    if not needsQuotes then
        value
    else
        let escaped =
            value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\t", "\\t")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")

        $"\"{escaped}\""

let private synthesizeUntrackedPatch (path: string) (bytes: byte[]) =
    if bytes |> Array.contains 0uy then
        Binary
    else
        try
            let text = strictUtf8.GetString(bytes)
            let endsWithNewline =
                bytes.Length > 0
                && bytes[bytes.Length - 1] = 0x0Auy
            let oldPath = gitPatchPath "a/" path
            let newPath = gitPatchPath "b/" path

            let contentLines =
                if text.Length = 0 then
                    []
                else
                    let split = text.Split('\n') |> Array.toList
                    if endsWithNewline then split |> List.take (split.Length - 1) else split

            let header =
                [ $"diff --git {oldPath} {newPath}"
                  "new file mode 100644"
                  "--- /dev/null"
                  $"+++ {newPath}" ]

            let hunk =
                if contentLines.IsEmpty then
                    []
                else
                    [ $"@@ -0,0 +1,{contentLines.Length} @@" ]

            let body = contentLines |> List.map (fun line -> "+" + line)

            let noNewlineMarker =
                if contentLines.IsEmpty || endsWithNewline then
                    []
                else
                    [ "\\ No newline at end of file" ]

            let patch =
                header @ hunk @ body @ noNewlineMarker
                |> String.concat "\n"
                |> fun value -> value + "\n"

            let patchBytes = Encoding.UTF8.GetBytes(patch)

            if patchBytes.Length > maxWorktreeDiffBytes then
                Oversized
            elif diffLineCount patchBytes > maxWorktreeDiffLines then
                Truncated
            else
                Text patch
        with :? DecoderFallbackException ->
            Binary

let private getUntrackedDiffFile (repoRoot: string) (entry: WorktreeDiffEntry) =
    async {
        return
            resolveUntrackedPath repoRoot entry.Path
            |> Result.bind (fun path ->
                let info = FileInfo(path)

                if isSymbolicLink info then
                    Ok(Symlink None)
                elif not info.Exists then
                    Error FileUnavailable
                elif info.Length > int64 maxWorktreeDiffBytes then
                    Ok Oversized
                else
                    match readFileBounded path with
                    | FileBytes bytes -> Ok(synthesizeUntrackedPatch entry.Path bytes)
                    | FileTooLarge -> Ok Oversized
                    | FileReadFailed -> Error FileUnavailable)
    }

let internal getWorktreeDiffFileWithinDeadline
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    (mergeBase: string)
    (entry: WorktreeDiffEntry)
    : Async<Result<WorktreeDiffFile, WorktreeDiffError>> =
    match entry.Status with
    | Untracked -> getUntrackedDiffFile repoRoot entry
    | _ -> getTrackedDiffFile deadline repoRoot mergeBase entry

let getWorktreeDiffFile
    (repoRoot: string)
    (mergeBase: string)
    (entry: WorktreeDiffEntry)
    =
    getWorktreeDiffFileWithinDeadline
        (ProcessRunner.createResponseDeadline
            ProcessRunner.argumentListResponseDeadlineMs)
        repoRoot
        mergeBase
        entry
