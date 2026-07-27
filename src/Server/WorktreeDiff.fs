module Server.WorktreeDiff

open System
open System.Globalization
open System.IO
open System.Security
open System.Text
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling

type WorktreeDiffStatus =
    | Added
    | Modified
    | Deleted
    | Renamed
    | Untracked
    | TrackedAndUntracked of trackedStatus: WorktreeDiffStatus

type WorktreeDiffEntry =
    { Path: string
      OldPath: string option
      LinesAdded: int option
      LinesRemoved: int option
      Status: WorktreeDiffStatus }

type internal WorktreeDiffStatsEntry =
    { Path: string
      OldPath: string option
      LinesAdded: int option
      LinesRemoved: int option }

type private WorktreeDiffRawEntry =
    { Entry: WorktreeDiffEntry
      IsSymlink: bool }

type WorktreeDiffLayers =
    { AlreadyCommitted: bool
      LocalChanges: bool
      Untracked: bool }

type internal DiffComparisonContext =
    { WorktreePath: string
      UpstreamRemote: string
      BaseBranch: string }

type WorktreeDiffSummary =
    { BaseRef: string
      MergeBase: string
      Files: WorktreeDiffEntry list }

type WorktreeDiffOperation =
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

type internal WorktreeDiffLayerCounts =
    { CommittedCount: Result<int, WorktreeDiffError>
      LocalCount: Result<int, WorktreeDiffError>
      UntrackedCount: Result<int, WorktreeDiffError> }

[<RequireQualifiedAccess>]
type WorktreeDiffReplacement =
    | BinaryContent
    | SymbolicLink

type WorktreeDiffFile =
    | Text of patch: string
    | DeletedFile of patch: string
    | Replacement of
        trackedPatch: string *
        replacement: WorktreeDiffReplacement
    | Binary
    | Oversized
    | Truncated
    | Symlink of patch: string option

type internal BoundedFileRead =
    | FileBytes of byte[]
    | FileTooLarge
    | FileReadFailed
    | FileReadTimedOut

type private UntrackedFileKind =
    | RegularFile of length: int64
    | SymbolicLinkFile
    | UnsupportedFile
    | MissingFile

type private UntrackedPatchMetrics =
    { ContentLineCount: int
      PatchByteCount: int
      PatchLineCount: int }

let maxWorktreeDiffFiles = 1_000
let maxWorktreeDiffBytes = 2 * 1024 * 1024
let maxWorktreeDiffLines = 20_000

let allWorktreeDiffLayers =
    { AlreadyCommitted = true
      LocalChanges = true
      Untracked = true }

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
            // A truncated patch is unusable here — the caller parses these bytes — so the diff
            // viewer keeps its typed capture-limit error even though the process exited.
            | Ok output when not output.Truncated.IsEmpty ->
                Error(GitCaptureLimitExceeded(operation, List.head output.Truncated))
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

let private entryWithoutStats path oldPath status : WorktreeDiffEntry =
    { Path = path
      OldPath = oldPath
      LinesAdded = None
      LinesRemoved = None
      Status = status }

let private trackedEntry (status: char) (paths: string list) =
    match status, paths with
    | ('R' | 'C'), oldPath :: newPath :: rest ->
        Ok(
            entryWithoutStats
                newPath
                (Some oldPath)
                (if status = 'R' then Renamed else Added),
            rest
        )
    | ('A' | 'M' | 'D' | 'T' | 'U' | 'X' | 'B'), path :: rest ->
        let diffStatus =
            match status with
            | 'A' -> Added
            | 'D' -> Deleted
            | _ -> Modified

        Ok(
            entryWithoutStats path None diffStatus,
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

let private parseLineCount value =
    if value = "-" then
        Ok None
    else
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, count when count >= 0 -> Ok(Some count)
        | _ -> Error(InvalidGitOutput EnumerateTracked)

let private parseNumstatHeader (header: string) =
    let firstTab = header.IndexOf('\t')
    let secondTab =
        if firstTab < 0 then -1 else header.IndexOf('\t', firstTab + 1)

    if firstTab < 0 || secondTab < 0 then
        Error(InvalidGitOutput EnumerateTracked)
    else
        let addedText = header[..firstTab - 1]
        let removedText = header[firstTab + 1..secondTab - 1]
        let path = header[secondTab + 1..]

        match parseLineCount addedText, parseLineCount removedText with
        | Ok linesAdded, Ok linesRemoved ->
            match linesAdded, linesRemoved with
            | Some _, Some _
            | None, None -> Ok(linesAdded, linesRemoved, path)
            | _ -> Error(InvalidGitOutput EnumerateTracked)
        | _ -> Error(InvalidGitOutput EnumerateTracked)

let private parseNumstatTokens tokens =
    let rec parse entries remaining =
        match remaining with
        | [] -> Ok(List.rev entries)
        | header :: rest ->
            parseNumstatHeader header
            |> Result.bind (fun (linesAdded, linesRemoved, path) ->
                match path, rest with
                | "", oldPath :: newPath :: tail ->
                    parse
                        ({ Path = newPath
                           OldPath = Some oldPath
                           LinesAdded = linesAdded
                           LinesRemoved = linesRemoved }
                         :: entries)
                        tail
                | "", _ -> Error(InvalidGitOutput EnumerateTracked)
                | path, tail ->
                    parse
                        ({ Path = path
                           OldPath = None
                           LinesAdded = linesAdded
                           LinesRemoved = linesRemoved }
                         :: entries)
                        tail)

    parse [] tokens

let internal parseNumstatEntries (bytes: byte[]) =
    parseNulTokens EnumerateTracked bytes
    |> Result.bind parseNumstatTokens

let private isGitMode (mode: string) =
    mode.Length = 6 && mode |> Seq.forall Char.IsDigit

let private parseRawHeader (header: string) =
    let fields =
        header.Split(' ', StringSplitOptions.RemoveEmptyEntries)

    if
        fields.Length < 5
        || not (fields[0].StartsWith(":", StringComparison.Ordinal))
    then
        Error(InvalidGitOutput EnumerateTracked)
    else
        let oldMode = fields[0][1..]
        let newMode = fields[1]

        fields
        |> Array.tryLast
        |> Option.filter (fun status ->
            status.Length > 0
            && isGitMode oldMode
            && isGitMode newMode)
        |> Option.map (fun status ->
            status.Chars(0),
            oldMode = "120000" || newMode = "120000")
        |> Result.requireSome (InvalidGitOutput EnumerateTracked)

let private parseRawEntries (tokens: string list) =
    let rec parse entries (remaining: string list) =
        match remaining with
        | header :: paths when header.StartsWith(":", StringComparison.Ordinal) ->
            parseRawHeader header
            |> Result.bind (fun (status, isSymlink) ->
                trackedEntry status paths
                |> Result.bind (fun (entry, rest) ->
                    parse
                        ({ Entry = entry
                           IsSymlink = isSymlink }
                         :: entries)
                        rest))
        | rest -> Ok(List.rev entries, rest)

    parse [] tokens

let private entryKey path oldPath = oldPath, path

let private applyTrackedStats
    (tracked: WorktreeDiffRawEntry list)
    (stats: WorktreeDiffStatsEntry list)
    =
    let statsByPath =
        stats
        |> List.map (fun entry -> entryKey entry.Path entry.OldPath, entry)
        |> Map.ofList

    if Map.count statsByPath <> stats.Length then
        Error(InvalidGitOutput EnumerateTracked)
    else
        let rec apply
            (entries: WorktreeDiffEntry list)
            (remaining: WorktreeDiffRawEntry list)
            =
            match remaining with
            | [] -> Ok(List.rev entries)
            | rawEntry :: rest ->
                let entry = rawEntry.Entry

                match statsByPath |> Map.tryFind (entryKey entry.Path entry.OldPath) with
                | None -> Error(InvalidGitOutput EnumerateTracked)
                | Some stats ->
                    let linesAdded, linesRemoved =
                        if rawEntry.IsSymlink then
                            None, None
                        else
                            stats.LinesAdded, stats.LinesRemoved

                    let updated =
                        { entry with
                            LinesAdded = linesAdded
                            LinesRemoved = linesRemoved }

                    apply (updated :: entries) rest

        if tracked.Length <> stats.Length then
            Error(InvalidGitOutput EnumerateTracked)
        else
            apply [] tracked

let private parseTrackedSummaryEntries bytes =
    parseNulTokens EnumerateTracked bytes
    |> Result.bind parseRawEntries
    |> Result.bind (fun (tracked, numstatTokens) ->
        parseNumstatTokens numstatTokens
        |> Result.bind (applyTrackedStats tracked))

let private parseUntrackedEntries (bytes: byte[]) =
    parseNulTokens EnumerateUntracked bytes
    |> Result.map (
        List.map (fun path -> entryWithoutStats path None Untracked)
    )

let private sortDiffEntries (entries: WorktreeDiffEntry list) =
    entries
    |> List.sortWith (fun left right ->
        StringComparer.Ordinal.Compare(left.Path, right.Path))

let private excludeGeneratedDiffViewer (entries: WorktreeDiffEntry list) =
    entries
    |> List.filter (fun entry -> entry.Path <> generatedDiffViewerPath)

let private combineLineCounts left right =
    Option.map2 (+) left right

let private composeTrackedAndUntracked
    (tracked: WorktreeDiffEntry list)
    (untracked: WorktreeDiffEntry list)
    =
    let trackedPaths = tracked |> List.map _.Path |> Set.ofList
    let untrackedByPath = untracked |> List.map (fun entry -> entry.Path, entry) |> Map.ofList

    let composedTracked =
        tracked
        |> List.map (fun entry ->
            match untrackedByPath |> Map.tryFind entry.Path with
            | Some untrackedEntry ->
                { entry with
                    LinesAdded =
                        combineLineCounts entry.LinesAdded untrackedEntry.LinesAdded
                    LinesRemoved =
                        combineLineCounts entry.LinesRemoved untrackedEntry.LinesRemoved
                    Status = TrackedAndUntracked entry.Status }
            | None -> entry)

    let untrackedOnly =
        untracked
        |> List.filter (fun entry -> not (Set.contains entry.Path trackedPaths))

    composedTracked @ untrackedOnly

let private trackedDiffArguments mergeBase layers =
    match layers.AlreadyCommitted, layers.LocalChanges with
    | true, true -> Some [ mergeBase ]
    | true, false -> Some [ mergeBase; "HEAD" ]
    | false, true -> Some [ "HEAD" ]
    | false, false -> None

let private trackedEnumerationArguments
    (formats: string list)
    (comparison: string list)
    =
    [ "diff" ]
    @ formats
    @ [ "-z"
        "--find-renames"
        "--no-ext-diff"
        "--no-textconv" ]
    @ comparison

let private untrackedEnumerationArguments =
    [ "ls-files"
      "--others"
      "--exclude-standard"
      "-z"
      "--" ]

let private resolveComparison
    (deadline: ProcessRunner.ResponseDeadline)
    (context: DiffComparisonContext)
    (layers: WorktreeDiffLayers)
    =
    asyncResult {
        if not layers.AlreadyCommitted then
            return
                if layers.LocalChanges then
                    "HEAD", "HEAD"
                else
                    "working tree", "HEAD"
        else
            let! baseRef =
                resolveDiffBaseRef
                    deadline
                    context.WorktreePath
                    context.UpstreamRemote
                    context.BaseBranch

            let! mergeBaseBytes =
                runDiffGit
                    deadline
                    ResolveMergeBase
                    smallGitCaptureLimitBytes
                    context.WorktreePath
                    [ "merge-base"; "HEAD"; baseRef ]

            let! mergeBase = trimSingleLine ResolveMergeBase mergeBaseBytes
            return baseRef, mergeBase
    }

let private diffLineCount (bytes: byte[]) =
    if bytes.Length = 0 then
        0
    else
        let newlineCount =
            bytes
            |> Array.sumBy (fun value -> if value = 0x0Auy then 1 else 0)

        if bytes[bytes.Length - 1] = 0x0Auy then newlineCount else newlineCount + 1

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
    with
    | :? ArgumentException
    | :? NotSupportedException
    | :? PathTooLongException
    | :? SecurityException ->
        Error FileUnavailable

let private inspectUntrackedFile (path: string) =
    // Portable pre-open guards cover directories, links/reparse points, and devices.
    // Some platforms expose FIFOs as regular files, and FileStream open itself is not cancellable.
    try
        let attributes = File.GetAttributes(path)

        if attributes.HasFlag(FileAttributes.ReparsePoint) then
            SymbolicLinkFile
        elif
            attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.Device)
        then
            UnsupportedFile
        else
            RegularFile(FileInfo(path).Length)
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException
    | :? UnauthorizedAccessException
    | :? IOException
    | :? NotSupportedException
    | :? SecurityException ->
        MissingFile

let rec private readStreamBoundedCore
    (stream: Stream)
    (cancellationToken: CancellationToken)
    (captured: MemoryStream)
    (buffer: byte[])
    =
    task {
        let! count =
            stream.ReadAsync(buffer.AsMemory(), cancellationToken)

        if count = 0 then
            return FileBytes(captured.ToArray())
        else
            let remaining =
                maxWorktreeDiffBytes - int captured.Length

            if count > remaining then
                return FileTooLarge
            else
                do!
                    captured.WriteAsync(
                        buffer.AsMemory(0, count),
                        cancellationToken
                    )

                return!
                    readStreamBoundedCore
                        stream
                        cancellationToken
                        captured
                        buffer
    }

let internal readStreamBounded
    (cancellationToken: CancellationToken)
    (stream: Stream)
    =
    task {
        try
            use captured = new MemoryStream(64 * 1024)
            let buffer = Array.zeroCreate<byte> (64 * 1024)

            return!
                readStreamBoundedCore
                    stream
                    cancellationToken
                    captured
                    buffer
        with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
            return FileReadTimedOut
    }

let private readFileBounded
    (deadline: ProcessRunner.ResponseDeadline)
    (path: string)
    =
    async {
        use cts = new CancellationTokenSource()
        let remainingMs =
            ProcessRunner.responseDeadlineOperationRemainingMs deadline

        if remainingMs <= 0 then
            return FileReadTimedOut
        else
            cts.CancelAfter(remainingMs)

            let options = FileStreamOptions()
            options.Mode <- FileMode.Open
            options.Access <- FileAccess.Read
            options.Share <- FileShare.ReadWrite ||| FileShare.Delete
            options.BufferSize <- 64 * 1024
            options.Options <-
                FileOptions.Asynchronous
                ||| FileOptions.SequentialScan

            try
                use stream = new FileStream(path, options)

                // `inspectUntrackedFile` checked the path, not this handle, so a concurrent process
                // could have swapped the file for a link in between. .NET exposes no portable
                // no-follow open, so re-check after opening: an attacker must now win a far tighter
                // race, and if they do the content is discarded instead of served.
                if File.ResolveLinkTarget(path, returnFinalTarget = false) <> null then
                    return FileReadFailed
                else

                let! result =
                    readStreamBounded cts.Token stream
                    |> Async.AwaitTask

                return
                    if
                        ProcessRunner.responseDeadlineCanContinue
                            deadline
                    then
                        result
                    else
                        FileReadTimedOut
            with
            | :? OperationCanceledException when cts.IsCancellationRequested ->
                return FileReadTimedOut
            | :? FileNotFoundException
            | :? DirectoryNotFoundException
            | :? UnauthorizedAccessException
            | :? IOException
            | :? NotSupportedException
            | :? SecurityException ->
                return
                    if
                        ProcessRunner.responseDeadlineCanContinue
                            deadline
                    then
                        FileReadFailed
                    else
                        FileReadTimedOut
    }

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

let private untrackedPatchMetrics (path: string) (bytes: byte[]) =
    if bytes |> Array.contains 0uy then
        None
    else
        try
            strictUtf8.GetCharCount(bytes) |> ignore

            let endsWithNewline =
                bytes.Length > 0
                && bytes[bytes.Length - 1] = 0x0Auy
            let contentLineCount = diffLineCount bytes
            let oldPath = gitPatchPath "a/" path
            let newPath = gitPatchPath "b/" path

            let header =
                [ $"diff --git {oldPath} {newPath}"
                  "new file mode 100644"
                  "--- /dev/null"
                  $"+++ {newPath}" ]

            let hunk =
                if contentLineCount = 0 then
                    []
                else
                    [ $"@@ -0,0 +1,{contentLineCount} @@" ]

            let noNewlineMarker =
                if contentLineCount = 0 || endsWithNewline then
                    []
                else
                    [ "\\ No newline at end of file" ]

            let fixedByteCount =
                header @ hunk @ noNewlineMarker
                |> List.sumBy (fun line ->
                    strictUtf8.GetByteCount(line) + 1)

            let bodyByteCount =
                bytes.Length
                + contentLineCount
                + (if contentLineCount > 0 && not endsWithNewline then 1 else 0)

            Some
                { ContentLineCount = contentLineCount
                  PatchByteCount = fixedByteCount + bodyByteCount
                  PatchLineCount =
                    header.Length
                    + hunk.Length
                    + contentLineCount
                    + noNewlineMarker.Length }
        with :? DecoderFallbackException ->
            None

let private synthesizeUntrackedPatch (path: string) (bytes: byte[]) =
    match untrackedPatchMetrics path bytes with
    | None -> Binary
    | Some metrics when metrics.PatchByteCount > maxWorktreeDiffBytes ->
        Oversized
    | Some metrics when metrics.PatchLineCount > maxWorktreeDiffLines ->
        Truncated
    | Some _ ->
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

        let patchLines =
            [ $"diff --git {oldPath} {newPath}"
              "new file mode 100644"
              "--- /dev/null"
              $"+++ {newPath}" ]
            @ (if contentLines.IsEmpty then
                   []
               else
                   [ $"@@ -0,0 +1,{contentLines.Length} @@" ])
            @ (contentLines |> List.map (fun line -> "+" + line))
            @ (if contentLines.IsEmpty || endsWithNewline then
                   []
               else
                   [ "\\ No newline at end of file" ])

        patchLines
        |> String.concat "\n"
        |> fun value -> Text(value + "\n")

let internal untrackedLineCountsWithinDeadline
    (deadline: ProcessRunner.ResponseDeadline)
    repoRoot
    (entry: WorktreeDiffEntry)
    =
    async {
        let unavailable =
            { entry with
                LinesAdded = None
                LinesRemoved = None }

        let timedOut () =
            not (ProcessRunner.responseDeadlineCanContinue deadline)

        if timedOut () then
            return Error(GitTimedOut EnumerateUntracked)
        else
            match resolveUntrackedPath repoRoot entry.Path with
            | Error _ when timedOut () ->
                return Error(GitTimedOut EnumerateUntracked)
            | Error _ -> return Ok unavailable
            | Ok path when timedOut () ->
                return Error(GitTimedOut EnumerateUntracked)
            | Ok path ->
                match inspectUntrackedFile path with
                | _ when timedOut () ->
                    return Error(GitTimedOut EnumerateUntracked)
                | SymbolicLinkFile
                | UnsupportedFile
                | MissingFile ->
                    return Ok unavailable
                | RegularFile length
                    when length > int64 maxWorktreeDiffBytes ->
                    return Ok unavailable
                | RegularFile 0L ->
                    return
                        Ok
                            { entry with
                                LinesAdded = Some 0
                                LinesRemoved = Some 0 }
                | RegularFile _ ->
                    let! read = readFileBounded deadline path

                    if timedOut () then
                        return Error(GitTimedOut EnumerateUntracked)
                    else
                        match read with
                        | FileReadTimedOut ->
                            return Error(GitTimedOut EnumerateUntracked)
                        | FileBytes bytes ->
                            let metrics =
                                untrackedPatchMetrics entry.Path bytes

                            if timedOut () then
                                return Error(GitTimedOut EnumerateUntracked)
                            else
                                return
                                    match metrics with
                                    | Some metrics
                                        when metrics.PatchByteCount <= maxWorktreeDiffBytes
                                             && metrics.PatchLineCount <= maxWorktreeDiffLines ->
                                        Ok
                                            { entry with
                                                LinesAdded =
                                                    Some metrics.ContentLineCount
                                                LinesRemoved = Some 0 }
                                    | _ -> Ok unavailable
                        | FileTooLarge
                        | FileReadFailed ->
                            return Ok unavailable
    }

let internal collectUntrackedLineCounts
    (canContinue: WorktreeDiffEntry -> bool)
    (readLineCounts:
        WorktreeDiffEntry
            -> Async<Result<WorktreeDiffEntry, WorktreeDiffError>>)
    entries
    =
    let rec collect collected remaining =
        async {
            match remaining with
            | [] -> return Ok(List.rev collected)
            | entry :: _ when not (canContinue entry) ->
                return Error(GitTimedOut EnumerateUntracked)
            | entry :: rest ->
                let! result = readLineCounts entry

                if not (canContinue entry) then
                    return Error(GitTimedOut EnumerateUntracked)
                else
                    match result with
                    | Error error -> return Error error
                    | Ok counted ->
                        return!
                            collect
                                (counted :: collected)
                                rest
        }

    collect [] entries

let internal getWorktreeDiffSummaryWithinDeadline
    (deadline: ProcessRunner.ResponseDeadline)
    (context: DiffComparisonContext)
    (layers: WorktreeDiffLayers)
    : Async<Result<WorktreeDiffSummary, WorktreeDiffError>> =
    asyncResult {
        let! baseRef, mergeBase = resolveComparison deadline context layers

        let tracked =
            match trackedDiffArguments mergeBase layers with
            | None -> async.Return(Ok [])
            | Some comparison ->
                asyncResult {
                    let! bytes =
                        runDiffGit
                            deadline
                            EnumerateTracked
                            summaryCaptureLimitBytes
                            context.WorktreePath
                            (trackedEnumerationArguments
                                [ "--raw"; "--numstat" ]
                                comparison)

                    return!
                        parseTrackedSummaryEntries bytes
                        |> Result.map excludeGeneratedDiffViewer
                }

        let untrackedPaths =
            if not layers.Untracked then
                async.Return(Ok [])
            else
                asyncResult {
                    let! untrackedBytes =
                        runDiffGit
                            deadline
                            EnumerateUntracked
                            summaryCaptureLimitBytes
                            context.WorktreePath
                            untrackedEnumerationArguments

                    return!
                        parseUntrackedEntries untrackedBytes
                        |> Result.map excludeGeneratedDiffViewer
                }

        let! enumerated = [| tracked; untrackedPaths |] |> Async.Parallel
        let! tracked = enumerated[0]
        let! untrackedPaths = enumerated[1]
        let selectedPaths =
            composeTrackedAndUntracked tracked untrackedPaths

        if selectedPaths.Length > maxWorktreeDiffFiles then
            return! Error(TooManyFiles selectedPaths.Length)

        let! untracked =
            collectUntrackedLineCounts
                (fun _ ->
                    ProcessRunner.responseDeadlineCanContinue deadline)
                (untrackedLineCountsWithinDeadline
                    deadline
                    context.WorktreePath)
                untrackedPaths

        let files =
            composeTrackedAndUntracked tracked untracked
            |> sortDiffEntries

        if
            layers.Untracked
            && not (ProcessRunner.responseDeadlineCanContinue deadline)
        then
            return! Error(GitTimedOut EnumerateUntracked)

        return
            { BaseRef = baseRef
              MergeBase = mergeBase
              Files = files }
    }

let internal getWorktreeDiffSummary (context: DiffComparisonContext) =
    getWorktreeDiffSummaryWithinDeadline
        (ProcessRunner.createResponseDeadline
            ProcessRunner.argumentListResponseDeadlineMs)
        context
        allWorktreeDiffLayers

let internal getFilteredWorktreeDiffSummary
    (context: DiffComparisonContext)
    (layers: WorktreeDiffLayers)
    =
    getWorktreeDiffSummaryWithinDeadline
        (ProcessRunner.createResponseDeadline
            ProcessRunner.argumentListResponseDeadlineMs)
        context
        layers

let private countLayer deadline context layers =
    asyncResult {
        let! _, mergeBase = resolveComparison deadline context layers

        let! tracked =
            match trackedDiffArguments mergeBase layers with
            | None -> async.Return(Ok [])
            | Some comparison ->
                asyncResult {
                    let! bytes =
                        runDiffGit
                            deadline
                            EnumerateTracked
                            summaryCaptureLimitBytes
                            context.WorktreePath
                            (trackedEnumerationArguments [ "--name-status" ] comparison)

                    return!
                        parseTrackedEntries bytes
                        |> Result.map excludeGeneratedDiffViewer
                }

        let! untracked =
            if not layers.Untracked then
                async.Return(Ok [])
            else
                asyncResult {
                    let! bytes =
                        runDiffGit
                            deadline
                            EnumerateUntracked
                            summaryCaptureLimitBytes
                            context.WorktreePath
                            untrackedEnumerationArguments

                    return!
                        parseUntrackedEntries bytes
                        |> Result.map excludeGeneratedDiffViewer
                }

        return composeTrackedAndUntracked tracked untracked |> List.length
    }

let internal getWorktreeDiffLayerCountsWithinDeadline
    (deadline: ProcessRunner.ResponseDeadline)
    (context: DiffComparisonContext)
    =
    async {
        let! counts =
            [| { AlreadyCommitted = true
                 LocalChanges = false
                 Untracked = false }
               { AlreadyCommitted = false
                 LocalChanges = true
                 Untracked = false }
               { AlreadyCommitted = false
                 LocalChanges = false
                 Untracked = true } |]
            |> Array.map (countLayer deadline context)
            |> Async.Parallel

        return
            { CommittedCount = counts[0]
              LocalCount = counts[1]
              UntrackedCount = counts[2] }
    }

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

let private combinePatchText (trackedPatch: string) (untrackedPatch: string) =
    let separator =
        if trackedPatch.EndsWith("\n", StringComparison.Ordinal) then
            ""
        else
            "\n"

    let patch = trackedPatch + separator + untrackedPatch
    let bytes = Encoding.UTF8.GetBytes(patch)

    if bytes.Length > maxWorktreeDiffBytes then
        Oversized
    elif diffLineCount bytes > maxWorktreeDiffLines then
        Truncated
    else
        Text patch

let private combineTrackedAndUntrackedFiles tracked untracked =
    match tracked, untracked with
    | Oversized, _
    | _, Oversized -> Oversized
    | Truncated, _
    | _, Truncated -> Truncated
    | (Text trackedPatch
      | DeletedFile trackedPatch
      | Symlink(Some trackedPatch)),
      Binary ->
        Replacement(trackedPatch, WorktreeDiffReplacement.BinaryContent)
    | (Text trackedPatch
      | DeletedFile trackedPatch
      | Symlink(Some trackedPatch)),
      Symlink None ->
        Replacement(trackedPatch, WorktreeDiffReplacement.SymbolicLink)
    | (Replacement _ as replacement), _
    | _, (Replacement _ as replacement) -> replacement
    | Binary, _
    | _, Binary -> Binary
    | Symlink None, _
    | _, Symlink None -> Symlink None
    | (Text trackedPatch
      | DeletedFile trackedPatch
      | Symlink(Some trackedPatch)),
      (Text untrackedPatch
      | DeletedFile untrackedPatch
      | Symlink(Some untrackedPatch)) ->
        combinePatchText trackedPatch untrackedPatch

/// Git-derived paths are fed back to Git as pathspecs. `--` only ends option parsing; it does not
/// disable pathspec magic, so a tracked file literally named `:(top)**` would be reinterpreted as a
/// pattern and could return an unrelated patch. `:(literal)` pins each path to itself.
let private literalPathspec (path: string) = $":(literal){path}"

let private trackedDiffPaths (entry: WorktreeDiffEntry) =
    match entry.OldPath with
    | Some oldPath when oldPath <> entry.Path -> [ oldPath; entry.Path ]
    | _ -> [ entry.Path ]
    |> List.map literalPathspec

let private getTrackedDiffFile
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    (mergeBase: string)
    (layers: WorktreeDiffLayers)
    (entry: WorktreeDiffEntry)
    =
    async {
        let! patchResult =
            match trackedDiffArguments mergeBase layers with
            | None -> async.Return(Error FileUnavailable)
            | Some comparison ->
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
                       "--no-color" ]
                     @ comparison
                     @ [ "--" ]
                     @ trackedDiffPaths entry)

        return
            match patchResult with
            | Error(GitCaptureLimitExceeded(LoadFile, ProcessRunner.StandardOutput)) ->
                Ok Oversized
            | Error error -> Error error
            | Ok bytes when bytes.Length = 0 -> Error FileUnavailable
            | Ok bytes -> classifyTrackedPatch entry bytes
    }

let private getUntrackedDiffFile
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    (entry: WorktreeDiffEntry)
    =
    async {
        let timedOut () =
            not (ProcessRunner.responseDeadlineCanContinue deadline)

        if timedOut () then
            return Error(GitTimedOut LoadFile)
        else
            match resolveUntrackedPath repoRoot entry.Path with
            | Error _ when timedOut () ->
                return Error(GitTimedOut LoadFile)
            | Error error -> return Error error
            | Ok path when timedOut () ->
                return Error(GitTimedOut LoadFile)
            | Ok path ->
                match inspectUntrackedFile path with
                | _ when timedOut () ->
                    return Error(GitTimedOut LoadFile)
                | SymbolicLinkFile -> return Ok(Symlink None)
                | UnsupportedFile
                | MissingFile ->
                    return Error FileUnavailable
                | RegularFile length
                    when length > int64 maxWorktreeDiffBytes ->
                    return Ok Oversized
                | RegularFile 0L ->
                    return Ok(synthesizeUntrackedPatch entry.Path Array.empty)
                | RegularFile _ ->
                    let! read = readFileBounded deadline path

                    if timedOut () then
                        return Error(GitTimedOut LoadFile)
                    else
                        match read with
                        | FileBytes bytes ->
                            let file =
                                synthesizeUntrackedPatch
                                    entry.Path
                                    bytes

                            return
                                if timedOut () then
                                    Error(GitTimedOut LoadFile)
                                else
                                    Ok file
                        | FileTooLarge -> return Ok Oversized
                        | FileReadFailed -> return Error FileUnavailable
                        | FileReadTimedOut ->
                            return Error(GitTimedOut LoadFile)
    }

let private getTrackedAndUntrackedDiffFile
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    (mergeBase: string)
    (layers: WorktreeDiffLayers)
    (entry: WorktreeDiffEntry)
    (trackedStatus: WorktreeDiffStatus)
    =
    asyncResult {
        let trackedEntry =
            { entry with
                Status = trackedStatus }

        let untrackedEntry =
            { entry with
                OldPath = None
                Status = Untracked }

        let! tracked =
            getTrackedDiffFile
                deadline
                repoRoot
                mergeBase
                layers
                trackedEntry

        let! untracked =
            getUntrackedDiffFile deadline repoRoot untrackedEntry
        return combineTrackedAndUntrackedFiles tracked untracked
    }

let internal getWorktreeDiffFileWithinDeadline
    (deadline: ProcessRunner.ResponseDeadline)
    (repoRoot: string)
    (mergeBase: string)
    (layers: WorktreeDiffLayers)
    (entry: WorktreeDiffEntry)
    : Async<Result<WorktreeDiffFile, WorktreeDiffError>> =
    match entry.Status with
    | Untracked -> getUntrackedDiffFile deadline repoRoot entry
    | TrackedAndUntracked trackedStatus ->
        getTrackedAndUntrackedDiffFile
            deadline
            repoRoot
            mergeBase
            layers
            entry
            trackedStatus
    | _ -> getTrackedDiffFile deadline repoRoot mergeBase layers entry

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
        allWorktreeDiffLayers
        entry

let getFilteredWorktreeDiffFile
    (repoRoot: string)
    (mergeBase: string)
    (layers: WorktreeDiffLayers)
    (entry: WorktreeDiffEntry)
    =
    getWorktreeDiffFileWithinDeadline
        (ProcessRunner.createResponseDeadline
            ProcessRunner.argumentListResponseDeadlineMs)
        repoRoot
        mergeBase
        layers
        entry
