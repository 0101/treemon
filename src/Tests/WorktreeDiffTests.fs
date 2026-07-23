module Tests.WorktreeDiffTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open NUnit.Framework
open Server
open Server.GitWorktree
open Server.WorktreeDiff
open Shared
open Tests.GitTestHelpers

let private writeText (repoDir: string) (relativePath: string) (content: string) =
    let path = Path.Combine(repoDir, relativePath)
    Path.GetDirectoryName(path) |> Directory.CreateDirectory |> ignore
    File.WriteAllText(path, content)

let private normalizeNewlines (value: string) =
    value.Replace("\r\n", "\n").Replace("\r", "\n")

let private generatedDiffViewerGitPath =
    String.concat "/" [ ".agents"; "canvas"; "diff.html" ]

let private layers committed local untracked : WorktreeDiffLayers =
    { AlreadyCommitted = committed
      LocalChanges = local
      Untracked = untracked }

let private comparisonContext worktreePath : DiffComparisonContext =
    { WorktreePath = worktreePath
      UpstreamRemote = "origin"
      BaseBranch = "main" }

let private assertSummaryOk result =
    match result with
    | Ok summary -> summary
    | Error error ->
        Assert.Fail($"Expected diff summary, got {error}")
        Unchecked.defaultof<_>

let private findEntry path (summary: WorktreeDiffSummary) =
    summary.Files
    |> List.tryFind (fun entry -> entry.Path = path)
    |> Option.defaultWith (fun () ->
        Assert.Fail($"Missing diff entry '{path}'")
        Unchecked.defaultof<_>)

let private initializeDiffRepo repoDir =
    initRepoOnMain repoDir
    writeText repoDir "tracked.txt" "base"
    writeText repoDir "rename-old.txt" "rename me"
    writeText repoDir "delete.txt" "delete me"
    gitOk repoDir [ "add"; "--"; "." ]
    gitOk repoDir [ "commit"; "-m"; "base files" ]
    gitOk repoDir [ "checkout"; "-b"; "feature" ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ProcessRunnerArgumentListTests() =

    let mutable tempDir = "" // NUnit SetUp and TearDown must share the per-test directory through fixture state.

    [<SetUp>]
    member _.Setup() =
        tempDir <-
            Path.Combine(
                Path.GetTempPath(),
                $"treemon-process-runner {Guid.NewGuid():N}"
            )

        Directory.CreateDirectory(tempDir) |> ignore
        initRepoOnMain tempDir

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            try
                Directory.Delete(tempDir, recursive = true)
            with _ ->
                ()

    [<Test>]
    member _.``argument-list execution preserves a working path containing spaces``() =
        let result =
            ProcessRunner.runArgumentList
                1024
                1024
                "Test"
                "git"
                [ "-C"; tempDir; "rev-parse"; "--is-inside-work-tree" ]
                None
            |> TestUtils.runAsync

        match result with
        | Ok output ->
            Assert.That(output.ExitCode, Is.EqualTo(0))
            Assert.That(Encoding.UTF8.GetString(output.Stdout).Trim(), Is.EqualTo("true"))
        | Error error -> Assert.Fail($"Expected process output, got {error}")

    [<Test>]
    member _.``stdout capture reports its byte limit instead of returning a prefix``() =
        writeText tempDir "large.txt" (String('x', 4096))
        gitOk tempDir [ "add"; "--"; "large.txt" ]
        gitOk tempDir [ "commit"; "-m"; "large output" ]

        let result =
            ProcessRunner.runArgumentList
                16
                1024
                "Test"
                "git"
                [ "-C"; tempDir; "show"; "HEAD:large.txt" ]
                None
            |> TestUtils.runAsync

        let expected =
            Error(
                ProcessRunner.CaptureLimitExceeded
                    ProcessRunner.StandardOutput
            )

        Assert.That(
            (result = expected),
            Is.True,
            $"Expected stdout capture limit, got {result}"
        )

    [<Test>]
    member _.``stderr capture reports its byte limit``() =
        let result =
            ProcessRunner.runArgumentList
                1024
                1
                "Test"
                "git"
                [ "not-a-real-git-command" ]
                None
            |> TestUtils.runAsync

        let expected =
            Error(
                ProcessRunner.CaptureLimitExceeded
                    ProcessRunner.StandardError
            )

        Assert.That(
            (result = expected),
            Is.True,
            $"Expected stderr capture limit, got {result}"
        )

    [<Test>]
    member _.``timeout returns a typed error and terminates the process tree``() =
        let childPidPath = Path.Combine(tempDir, "child.pid")

        let fileName, arguments =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                let escapedPidPath = childPidPath.Replace("'", "''")

                "powershell",
                [ "-NoProfile"
                  "-Command"
                  $"$child = Start-Process ping -ArgumentList '127.0.0.1','-n','30' -PassThru; Set-Content -NoNewline -Path '{escapedPidPath}' -Value $child.Id; Wait-Process -Id $child.Id" ]
            else
                let escapedPidPath = childPidPath.Replace("'", "'\\''")
                "sh", [ "-c"; $"sleep 30 & echo $! > '{escapedPidPath}'; wait" ]

        let result =
            ProcessRunner.runArgumentListWithTimeout
                2_000
                1024
                1024
                "Test"
                fileName
                arguments
                None
            |> TestUtils.runAsync

        Assert.That(
            (result = Error ProcessRunner.TimedOut),
            Is.True,
            $"Expected timeout, got {result}"
        )

        Assert.That(
            File.Exists(childPidPath),
            Is.True,
            "The child process did not start before the timeout"
        )

        let childPid = File.ReadAllText(childPidPath) |> Int32.Parse

        let childExited =
            SpinWait.SpinUntil(
                (fun () ->
                    try
                        use child = Process.GetProcessById(childPid)
                        child.HasExited
                    with :? ArgumentException ->
                        true),
                TimeSpan.FromSeconds(5.0)
            )

        if not childExited then
            use child = Process.GetProcessById(childPid)
            child.Kill(entireProcessTree = true)

        Assert.That(childExited, Is.True, "Timed-out child process was left running")

    [<Test>]
    member _.``missing executable returns a typed start failure``() =
        let result =
            ProcessRunner.runArgumentList
                1024
                1024
                "Test"
                $"missing-executable-{Guid.NewGuid():N}"
                []
                None
            |> TestUtils.runAsync

        match result with
        | Error(ProcessRunner.StartFailed _) -> ()
        | _ -> Assert.Fail($"Expected start failure, got {result}")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WorktreeDiffIntegrationTests() =

    let mutable tempDir = "" // NUnit SetUp and TearDown must share the per-test directory through fixture state.

    [<SetUp>]
    member _.Setup() =
        tempDir <-
            Path.Combine(
                Path.GetTempPath(),
                $"treemon-worktree-diff-{Guid.NewGuid():N}"
            )

        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists(tempDir) then
            try
                Directory.Delete(tempDir, recursive = true)
            with _ ->
                ()

    [<Test>]
    member _.``summary compares the merge base to committed staged unstaged and untracked changes``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir

        writeText repoDir "committed.txt" "committed"
        gitOk repoDir [ "add"; "--"; "committed.txt" ]
        gitOk repoDir [ "commit"; "-m"; "feature commit" ]
        writeText repoDir "staged.txt" "staged"
        gitOk repoDir [ "add"; "--"; "staged.txt" ]
        writeText repoDir "tracked.txt" "unstaged"
        writeText repoDir "untracked.txt" "untracked"

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let directMergeBase = gitText repoDir [ "merge-base"; "HEAD"; "main" ]

        let trackedPaths =
            (gitText
                repoDir
                [ "diff"
                  "--name-only"
                  "-z"
                  directMergeBase ])
                .Split([| '\000' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        let untrackedPaths =
            (gitText
                repoDir
                [ "ls-files"
                  "--others"
                  "--exclude-standard"
                  "-z"
                  "--" ])
                .Split([| '\000' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        let expectedPaths =
            trackedPaths @ untrackedPaths
            |> List.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left, right))

        Assert.That(summary.BaseRef, Is.EqualTo("main"))
        Assert.That(summary.MergeBase, Is.EqualTo(directMergeBase))
        Assert.That(summary.Files |> List.map _.Path, Is.EqualTo(expectedPaths))
        Assert.That((findEntry "committed.txt" summary).Status, Is.EqualTo(Added))
        Assert.That((findEntry "staged.txt" summary).Status, Is.EqualTo(Added))
        Assert.That((findEntry "tracked.txt" summary).Status, Is.EqualTo(Modified))
        Assert.That((findEntry "untracked.txt" summary).Status, Is.EqualTo(Untracked))

        let trackedPatch =
            getWorktreeDiffFile repoDir summary.MergeBase (findEntry "tracked.txt" summary)
            |> TestUtils.runAsync

        let directPatch =
            gitOutput
                repoDir
                [ "-c"
                  "core.quotepath=false"
                  "diff"
                  "--no-ext-diff"
                  "--no-textconv"
                  "--find-renames"
                  "--full-index"
                  "--no-color"
                  summary.MergeBase
                  "--"
                  "tracked.txt" ]

        match trackedPatch with
        | Ok(Text patch) ->
            Assert.That(
                normalizeNewlines patch,
                Is.EqualTo(normalizeNewlines directPatch)
            )
        | _ -> Assert.Fail($"Expected text patch, got {trackedPatch}")

    [<Test>]
    member _.``layer selections compose exact tracked ranges without duplicate paths``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir

        writeText repoDir "committed.txt" "committed"
        writeText repoDir "tracked.txt" "committed version"
        gitOk repoDir [ "add"; "--"; "committed.txt"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "committed layer" ]

        writeText repoDir "staged.txt" "staged"
        gitOk repoDir [ "add"; "--"; "staged.txt" ]
        writeText repoDir "tracked.txt" "local version"
        writeText repoDir "rename-old.txt" "unstaged"
        writeText repoDir "untracked.txt" "untracked"

        let expectedCommitted = Set.ofList [ "committed.txt"; "tracked.txt" ]
        let expectedLocal = Set.ofList [ "rename-old.txt"; "staged.txt"; "tracked.txt" ]
        let expectedUntracked = Set.singleton "untracked.txt"

        [ false, false, false
          false, false, true
          false, true, false
          false, true, true
          true, false, false
          true, false, true
          true, true, false
          true, true, true ]
        |> List.iter (fun (committed, local, untracked) ->
            let selected = layers committed local untracked

            let summary =
                getFilteredWorktreeDiffSummary (comparisonContext repoDir) selected
                |> TestUtils.runAsync
                |> assertSummaryOk

            let expected =
                [ if committed then expectedCommitted
                  if local then expectedLocal
                  if untracked then expectedUntracked ]
                |> Set.unionMany

            let actual = summary.Files |> List.map _.Path

            Assert.Multiple(fun () ->
                Assert.That(Set.ofList actual, Is.EqualTo(expected), $"{selected}")
                Assert.That(actual.Length, Is.EqualTo(Set.count expected), $"{selected}")))

        let assertTrackedPatch selected comparison =
            let summary =
                getFilteredWorktreeDiffSummary (comparisonContext repoDir) selected
                |> TestUtils.runAsync
                |> assertSummaryOk

            let result =
                getFilteredWorktreeDiffFile
                    repoDir
                    summary.MergeBase
                    selected
                    (findEntry "tracked.txt" summary)
                |> TestUtils.runAsync

            let direct =
                gitOutput
                    repoDir
                    ([ "-c"
                       "core.quotepath=false"
                       "diff"
                       "--no-ext-diff"
                       "--no-textconv"
                       "--find-renames"
                       "--full-index"
                       "--no-color" ]
                     @ comparison summary.MergeBase
                     @ [ "--"; "tracked.txt" ])

            match result with
            | Ok(Text patch) ->
                Assert.That(
                    normalizeNewlines patch,
                    Is.EqualTo(normalizeNewlines direct),
                    $"{selected}"
                )
            | _ -> Assert.Fail($"Expected text patch for {selected}, got {result}")

        assertTrackedPatch
            (layers true false false)
            (fun mergeBase -> [ mergeBase; "HEAD" ])

        assertTrackedPatch
            (layers false true false)
            (fun _ -> [ "HEAD" ])

        assertTrackedPatch
            (layers true true false)
            (fun mergeBase -> [ mergeBase ])

    [<Test>]
    member _.``layer counts independently count files touched by committed local and untracked changes``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir

        writeText repoDir "tracked.txt" "committed"
        gitOk repoDir [ "add"; "--"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "committed overlap" ]

        writeText repoDir "tracked.txt" "local overlap"
        writeText repoDir "staged.txt" "staged"
        gitOk repoDir [ "add"; "--"; "staged.txt" ]
        writeText repoDir "untracked.txt" "untracked"

        let counts =
            getWorktreeDiffLayerCountsWithinDeadline
                (ProcessRunner.createResponseDeadline
                    ProcessRunner.argumentListResponseDeadlineMs)
                (comparisonContext repoDir)
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(counts.CommittedCount = Ok 1, Is.True)
            Assert.That(counts.LocalCount = Ok 2, Is.True)
            Assert.That(counts.UntrackedCount = Ok 1, Is.True))

    [<Test>]
    member _.``tracked deletion recreated as untracked composes into one modified file``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir
        File.Delete(Path.Combine(repoDir, "delete.txt"))
        gitOk repoDir [ "add"; "--"; "delete.txt" ]
        gitOk repoDir [ "commit"; "-m"; "delete tracked file" ]
        writeText repoDir "delete.txt" "replacement"

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let entries =
            summary.Files
            |> List.filter (fun entry -> entry.Path = "delete.txt")

        let entry =
            entries
            |> List.tryExactlyOne
            |> Option.defaultWith (fun () ->
                Assert.Fail($"Expected one composed entry, got {entries}")
                Unchecked.defaultof<_>)

        Assert.That(
            entry.Status,
            Is.EqualTo(TrackedAndUntracked Deleted)
        )

        let result =
            getWorktreeDiffFile repoDir summary.MergeBase entry
            |> TestUtils.runAsync

        let trackedPatch =
            gitOutput
                repoDir
                [ "-c"
                  "core.quotepath=false"
                  "diff"
                  "--no-ext-diff"
                  "--no-textconv"
                  "--find-renames"
                  "--full-index"
                  "--no-color"
                  summary.MergeBase
                  "--"
                  "delete.txt" ]

        let untrackedPatch =
            String.concat
                Environment.NewLine
                [ "diff --git a/delete.txt b/delete.txt"
                  "new file mode 100644"
                  "--- /dev/null"
                  "+++ b/delete.txt"
                  "@@ -0,0 +1,1 @@"
                  "+replacement"
                  "\\ No newline at end of file"
                  "" ]

        let expected = trackedPatch + untrackedPatch

        match result with
        | Ok(Text patch) ->
            Assert.That(
                normalizeNewlines patch,
                Is.EqualTo(normalizeNewlines expected)
            )
        | _ -> Assert.Fail($"Expected composed text patch, got {result}")

    [<TestCase("binary")>]
    [<TestCase("symlink")>]
    member _.``tracked deletion patch survives a non-renderable untracked recreation``(
        replacement: string
    ) =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir
        File.Delete(Path.Combine(repoDir, "delete.txt"))
        gitOk repoDir [ "add"; "--"; "delete.txt" ]
        gitOk repoDir [ "commit"; "-m"; "delete tracked file" ]
        let replacementPath = Path.Combine(repoDir, "delete.txt")

        let expectedReplacement =
            match replacement with
            | "binary" ->
                File.WriteAllBytes(replacementPath, [| 1uy; 0uy; 2uy |])
                WorktreeDiffReplacement.BinaryContent
            | "symlink" ->
                let target = Path.Combine(tempDir, "replacement-target.txt")
                File.WriteAllText(target, "replacement target")

                try
                    File.CreateSymbolicLink(replacementPath, target) |> ignore
                with
                | :? UnauthorizedAccessException
                | :? PlatformNotSupportedException ->
                    Assert.Ignore("Symbolic links are unavailable in this environment")

                WorktreeDiffReplacement.SymbolicLink
            | other -> failwith $"Unexpected replacement {other}"

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let entry = findEntry "delete.txt" summary
        let result =
            getWorktreeDiffFile repoDir summary.MergeBase entry
            |> TestUtils.runAsync

        let trackedPatch =
            gitOutput
                repoDir
                [ "-c"
                  "core.quotepath=false"
                  "diff"
                  "--no-ext-diff"
                  "--no-textconv"
                  "--find-renames"
                  "--full-index"
                  "--no-color"
                  summary.MergeBase
                  "--"
                  "delete.txt" ]

        match result with
        | Ok(Replacement(actualPatch, actualReplacement)) ->
            Assert.Multiple(fun () ->
                Assert.That(entry.Status, Is.EqualTo(TrackedAndUntracked Deleted))
                Assert.That(
                    normalizeNewlines actualPatch,
                    Is.EqualTo(normalizeNewlines trackedPatch)
                )
                Assert.That(actualReplacement, Is.EqualTo(expectedReplacement)))
        | _ -> Assert.Fail($"Expected composed replacement, got {result}")

    [<Test>]
    member _.``HasDiff_untracked stays independent from tracked dirty``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir

        writeText repoDir "untracked.txt" "content"

        Assert.Multiple(fun () ->
            Assert.That(GitWorktree.isDirty repoDir |> TestUtils.runAsync, Is.False)
            Assert.That(GitWorktree.hasLocalDiff repoDir |> TestUtils.runAsync, Is.True))

        File.Delete(Path.Combine(repoDir, "untracked.txt"))
        let manyUntracked =
            [ 1..50 ]
            |> List.map (fun index -> $"untracked-{index:D2}-{String('x', 24)}.txt")

        manyUntracked
        |> List.iter (fun path -> writeText repoDir path "content")

        let porcelain =
            gitOutput
                repoDir
                [ "status"
                  "--porcelain"
                  "--untracked-files=all" ]

        Assert.That(Encoding.UTF8.GetByteCount(porcelain), Is.GreaterThan(1024))
        Assert.That(GitWorktree.hasLocalDiff repoDir |> TestUtils.runAsync, Is.True)

        manyUntracked
        |> List.iter (fun path -> File.Delete(Path.Combine(repoDir, path)))

        writeText repoDir "tracked.txt" "changed"

        Assert.Multiple(fun () ->
            Assert.That(GitWorktree.isDirty repoDir |> TestUtils.runAsync, Is.True)
            Assert.That(GitWorktree.hasLocalDiff repoDir |> TestUtils.runAsync, Is.True))

        gitOk repoDir [ "add"; "--"; "tracked.txt" ]

        Assert.Multiple(fun () ->
            Assert.That(GitWorktree.isDirty repoDir |> TestUtils.runAsync, Is.True)
            Assert.That(GitWorktree.hasLocalDiff repoDir |> TestUtils.runAsync, Is.True))

    [<Test>]
    member _.``HasDiff_net follows committed comparison content``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        writeText repoDir "tracked.txt" "base"
        gitOk repoDir [ "add"; "--"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "base" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]
        writeText repoDir "tracked.txt" "feature"
        gitOk repoDir [ "add"; "--"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "feature" ]

        let committed =
            collectWorktreeGitData repoDir (Some "feature") "origin" "main"
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(committed.HasDiff, Is.True)
            Assert.That(committed.IsDirty, Is.False)
            Assert.That(committed.WorkMetrics.IsSome, Is.True))

        gitOk repoDir [ "revert"; "--no-edit"; "HEAD" ]

        let reverted =
            collectWorktreeGitData repoDir (Some "feature") "origin" "main"
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(reverted.HasDiff, Is.False)
            Assert.That(reverted.IsDirty, Is.False)
            Assert.That(reverted.WorkMetrics, Is.EqualTo(None)))

    [<Test>]
    member _.``provisioned untracked diff viewer does not dirty a clean summary without an agents ignore``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        gitOk repoDir [ "checkout"; "-b"; "feature" ]

        DiffProvisioner.provisionViewer repoDir |> ignore

        let directUntracked =
            gitText
                repoDir
                [ "ls-files"
                  "--others"
                  "--exclude-standard"
                  "--" ]

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        Assert.That(directUntracked, Is.EqualTo(generatedDiffViewerGitPath))
        Assert.That(summary.Files, Is.Empty)
        Assert.That(
            GitWorktree.isDirty repoDir |> TestUtils.runAsync,
            Is.False,
            "Untracked files must not affect the tracked-dirty sync guard"
        )
        Assert.That(
            GitWorktree.hasLocalDiff repoDir |> TestUtils.runAsync,
            Is.False,
            "The generated untracked viewer must not count as comparison content"
        )
        Assert.That(
            (collectWorktreeGitData repoDir (Some "feature") "origin" "main"
             |> TestUtils.runAsync)
                .HasDiff,
            Is.False
        )

        writeText repoDir (Path.Combine(".agents", "canvas", "diff.html.backup")) "content"

        Assert.That(
            GitWorktree.isDirty repoDir |> TestUtils.runAsync,
            Is.False,
            "Untracked files must not affect the tracked-dirty sync guard"
        )
        Assert.That(
            GitWorktree.hasLocalDiff repoDir |> TestUtils.runAsync,
            Is.True,
            "Only the exact generated viewer path may be excluded"
        )

    [<Test>]
    member _.``GitMetrics_local_base_fallback aligns card data with the diff summary``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        writeText repoDir "tracked.txt" "base"
        gitOk repoDir [ "add"; "--"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "base" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]
        writeText repoDir "tracked.txt" "feature"
        gitOk repoDir [ "add"; "--"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "feature" ]

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let gitData =
            collectWorktreeGitData repoDir (Some "feature") "origin" "main"
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(summary.BaseRef, Is.EqualTo("main"))
            Assert.That(summary.Files |> List.map _.Path, Is.EqualTo([ "tracked.txt" ]))
            Assert.That(gitData.HasDiff, Is.True)
            Assert.That(gitData.MainBehindCount, Is.EqualTo(0))
            Assert.That(
                gitData.WorkMetrics,
                Is.EqualTo(
                    Some
                        { CommitCount = 1
                          LinesAdded = 1
                          LinesRemoved = 1 }
                )
            ))

    [<Test>]
    member _.``GitMetrics_committed_stats suppress external diff and textconv commands``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        writeText repoDir "tracked.txt" "base"
        writeText repoDir ".gitattributes" "tracked.txt diff=metrics"
        gitOk repoDir [ "add"; "--"; "." ]
        gitOk repoDir [ "commit"; "-m"; "base" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]
        writeText repoDir "tracked.txt" "feature"
        gitOk repoDir [ "add"; "--"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "feature" ]

        gitOk
            repoDir
            [ "config"
              "diff.external"
              "git config --local converter.externalExecuted true; false #" ]

        gitOk
            repoDir
            [ "config"
              "diff.metrics.textconv"
              "git config --local converter.textconvExecuted true; false #" ]

        let gitData =
            collectWorktreeGitData repoDir (Some "feature") "origin" "main"
            |> TestUtils.runAsync

        let converterWasExecuted key =
            let exitCode, _, _ =
                runGitArgs repoDir [ "config"; "--get"; key ]

            exitCode = 0

        Assert.Multiple(fun () ->
            Assert.That(gitData.HasDiff, Is.True)
            Assert.That(gitData.IsDirty, Is.False)
            Assert.That(
                gitData.WorkMetrics,
                Is.EqualTo(
                    Some
                        { CommitCount = 1
                          LinesAdded = 1
                          LinesRemoved = 1 }
                )
            )
            Assert.That(converterWasExecuted "converter.externalExecuted", Is.False)
            Assert.That(converterWasExecuted "converter.textconvExecuted", Is.False))

    [<Test>]
    member _.``GitMetrics_committed_generated_viewer excludes only the exact artifact``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        gitOk repoDir [ "checkout"; "-b"; "feature" ]

        DiffProvisioner.provisionViewer repoDir |> ignore
        gitOk repoDir [ "add"; "--"; generatedDiffViewerGitPath ]
        gitOk repoDir [ "commit"; "-m"; "generated diff viewer" ]

        let viewerOnlySummary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let viewerOnlyGitData =
            collectWorktreeGitData repoDir (Some "feature") "origin" "main"
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(viewerOnlySummary.Files, Is.Empty)
            Assert.That(viewerOnlyGitData.HasDiff, Is.False)
            Assert.That(viewerOnlyGitData.IsDirty, Is.False)
            Assert.That(viewerOnlyGitData.WorkMetrics, Is.EqualTo(None)))

        let backupGitPath = generatedDiffViewerGitPath + ".backup"
        writeText repoDir (Path.Combine(".agents", "canvas", "diff.html.backup")) "backup"
        gitOk repoDir [ "add"; "--"; backupGitPath ]
        gitOk repoDir [ "commit"; "-m"; "nearby backup" ]

        let backupSummary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let backupGitData =
            collectWorktreeGitData repoDir (Some "feature") "origin" "main"
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(backupSummary.Files |> List.map _.Path, Is.EqualTo([ backupGitPath ]))
            Assert.That(backupGitData.HasDiff, Is.True)
            Assert.That(backupGitData.IsDirty, Is.False)
            Assert.That(backupGitData.WorkMetrics.IsSome, Is.True))

    [<Test>]
    member _.``GitMetrics_missing_base keeps local changes without committed metrics``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        writeText repoDir "untracked.txt" "local"

        let gitData =
            collectWorktreeGitData repoDir (Some "main") "origin" "missing"
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(gitData.LastCommitMessage, Is.EqualTo("init"))
            Assert.That(gitData.IsDirty, Is.False)
            Assert.That(gitData.HasDiff, Is.True)
            Assert.That(gitData.WorkMetrics, Is.EqualTo(None))
            Assert.That(gitData.MainBehindCount, Is.Zero))

    [<Test>]
    member _.``GitMetrics_remote_base computes behind count``() =
        let repoDir, _ = initRepoWithOrigin tempDir
        gitOk repoDir [ "checkout"; "-b"; "feature" ]
        gitOk repoDir [ "checkout"; "main" ]
        writeText repoDir "base-advance.txt" "remote"
        gitOk repoDir [ "add"; "--"; "base-advance.txt" ]
        gitOk repoDir [ "commit"; "-m"; "advance base" ]
        gitOk repoDir [ "push"; "origin"; "main" ]
        gitOk repoDir [ "checkout"; "feature" ]

        let gitData =
            collectWorktreeGitData repoDir (Some "feature") "origin" "main"
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(gitData.MainBehindCount, Is.EqualTo(1))
            Assert.That(gitData.HasDiff, Is.False)
            Assert.That(gitData.WorkMetrics, Is.EqualTo(None)))

    [<Test>]
    member _.``provisioned tracked diff viewer is excluded only from comparison content``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        let relativeDiffPath = Path.Combine(".agents", "canvas", "diff.html")
        let diffPath = Path.Combine(repoDir, relativeDiffPath)
        writeText repoDir relativeDiffPath "stale"
        gitOk repoDir [ "add"; "--"; generatedDiffViewerGitPath ]
        gitOk repoDir [ "commit"; "-m"; "track stale diff viewer" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]

        DiffProvisioner.provisionViewer repoDir |> ignore

        let directTracked =
            gitText repoDir [ "diff"; "--name-only"; "main"; "--" ]

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        Assert.That(File.ReadAllText(diffPath), Is.EqualTo(DiffTemplate.html))
        Assert.That(directTracked, Is.EqualTo(generatedDiffViewerGitPath))
        Assert.That(summary.Files, Is.Empty)
        Assert.That(
            GitWorktree.isDirty repoDir |> TestUtils.runAsync,
            Is.True,
            "The tracked-dirty Sync guard retains its prior semantics"
        )
        Assert.That(
            GitWorktree.hasLocalDiff repoDir |> TestUtils.runAsync,
            Is.False,
            "A tracked generated viewer update must not count as comparison content"
        )

    [<Test>]
    member _.``remote tracking base is preferred without fetching``() =
        let repoDir, _ = initRepoWithOrigin tempDir
        writeText repoDir "local-main-only.txt" "local"
        gitOk repoDir [ "add"; "--"; "local-main-only.txt" ]
        gitOk repoDir [ "commit"; "-m"; "local main commit" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        Assert.That(summary.BaseRef, Is.EqualTo("origin/main"))
        Assert.That(
            summary.MergeBase,
            Is.EqualTo(gitText repoDir [ "merge-base"; "HEAD"; "origin/main" ])
        )
        Assert.That(
            summary.Files |> List.map _.Path,
            Is.EqualTo([ "local-main-only.txt" ])
        )

    [<Test>]
    member _.``missing scheduler-resolved base is a typed summary error``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        let result =
            getWorktreeDiffSummary
                { comparisonContext repoDir with
                    BaseBranch = "missing" }
            |> TestUtils.runAsync

        Assert.That(
            (result = Error(BaseNotFound("missing", "origin/missing"))),
            Is.True,
            $"Expected missing base, got {result}"
        )

    [<Test>]
    member _.``local and untracked layers do not require the scheduler-resolved base``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        writeText repoDir "tracked.txt" "base"
        gitOk repoDir [ "add"; "--"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "base" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]

        writeText repoDir "tracked.txt" "local"
        writeText repoDir "untracked.txt" "untracked"

        let localSummary =
            getFilteredWorktreeDiffSummary
                { comparisonContext repoDir with
                    BaseBranch = "missing" }
                (layers false true false)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let untrackedSummary =
            getFilteredWorktreeDiffSummary
                { comparisonContext repoDir with
                    BaseBranch = "missing" }
                (layers false false true)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let counts =
            getWorktreeDiffLayerCountsWithinDeadline
                (ProcessRunner.createResponseDeadline
                    ProcessRunner.argumentListResponseDeadlineMs)
                { comparisonContext repoDir with
                    BaseBranch = "missing" }
            |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(localSummary.BaseRef, Is.EqualTo("HEAD"))
            Assert.That(localSummary.Files |> List.map _.Path, Is.EqualTo([ "tracked.txt" ]))
            Assert.That(untrackedSummary.BaseRef, Is.EqualTo("working tree"))
            Assert.That(untrackedSummary.Files |> List.map _.Path, Is.EqualTo([ "untracked.txt" ]))
            Assert.That(
                (counts.CommittedCount = Error(BaseNotFound("missing", "origin/missing"))),
                Is.True
            )
            Assert.That(counts.LocalCount = Ok 1, Is.True)
            Assert.That(counts.UntrackedCount = Ok 1, Is.True))

    [<Test>]
    member _.``Git command failure is typed and does not produce a partial summary``() =
        let nonRepo = Path.Combine(tempDir, "not-a-repo")
        Directory.CreateDirectory(nonRepo) |> ignore

        let result =
            getWorktreeDiffSummary (comparisonContext nonRepo)
            |> TestUtils.runAsync

        match result with
        | Error(GitFailed(ResolveBase, _)) -> ()
        | _ -> Assert.Fail($"Expected typed Git failure, got {result}")

    [<Test>]
    member _.``unrelated base history returns a typed merge-base failure``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        gitOk repoDir [ "checkout"; "--orphan"; "feature" ]
        gitOk repoDir [ "commit"; "--allow-empty"; "-m"; "unrelated feature" ]

        let result =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync

        match result with
        | Error(GitFailed(ResolveMergeBase, 1)) -> ()
        | _ -> Assert.Fail($"Expected merge-base failure, got {result}")

    [<Test>]
    member _.``rename and delete preserve paths and match direct Git patches``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir
        gitOk repoDir [ "mv"; "--"; "rename-old.txt"; "rename new.txt" ]
        File.Delete(Path.Combine(repoDir, "delete.txt"))

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let renamed = findEntry "rename new.txt" summary
        let deleted = findEntry "delete.txt" summary

        Assert.That(renamed.Status, Is.EqualTo(Renamed))
        Assert.That(renamed.OldPath, Is.EqualTo(Some "rename-old.txt"))
        Assert.That(deleted.Status, Is.EqualTo(Deleted))

        let renameResult =
            getWorktreeDiffFile repoDir summary.MergeBase renamed
            |> TestUtils.runAsync

        let directRename =
            gitOutput
                repoDir
                [ "-c"
                  "core.quotepath=false"
                  "diff"
                  "--no-ext-diff"
                  "--no-textconv"
                  "--find-renames"
                  "--full-index"
                  "--no-color"
                  summary.MergeBase
                  "--"
                  "rename-old.txt"
                  "rename new.txt" ]

        match renameResult with
        | Ok(Text patch) ->
            Assert.That(
                normalizeNewlines patch,
                Is.EqualTo(normalizeNewlines directRename)
            )
        | _ -> Assert.Fail($"Expected rename patch, got {renameResult}")

        let deleteResult =
            getWorktreeDiffFile repoDir summary.MergeBase deleted
            |> TestUtils.runAsync

        let directDelete =
            gitOutput
                repoDir
                [ "-c"
                  "core.quotepath=false"
                  "diff"
                  "--no-ext-diff"
                  "--no-textconv"
                  "--find-renames"
                  "--full-index"
                  "--no-color"
                  summary.MergeBase
                  "--"
                  "delete.txt" ]

        match deleteResult with
        | Ok(DeletedFile patch) ->
            Assert.That(
                normalizeNewlines patch,
                Is.EqualTo(normalizeNewlines directDelete)
            )
        | _ -> Assert.Fail($"Expected deleted patch, got {deleteResult}")

    [<Test>]
    member _.``untracked paths support spaces Unicode and leading dashes``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir

        let paths =
            [ "space name.txt", "\"a/space name.txt\"", "\"b/space name.txt\""
              "žluťoučký.txt", "a/žluťoučký.txt", "b/žluťoučký.txt"
              "-leading.txt", "a/-leading.txt", "b/-leading.txt" ]

        paths
        |> List.iter (fun (path, _, _) -> writeText repoDir path "hello")

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        paths
        |> List.iter (fun (path, oldPath, newPath) ->
            let entry = findEntry path summary
            Assert.That(entry.Status, Is.EqualTo(Untracked))

            let result =
                getWorktreeDiffFile repoDir summary.MergeBase entry
                |> TestUtils.runAsync

            let expected =
                String.concat
                    Environment.NewLine
                    [ $"diff --git {oldPath} {newPath}"
                      "new file mode 100644"
                      "--- /dev/null"
                      $"+++ {newPath}"
                      "@@ -0,0 +1,1 @@"
                      "+hello"
                      "\\ No newline at end of file"
                      "" ]

            match result with
            | Ok(Text patch) ->
                Assert.That(
                    normalizeNewlines patch,
                    Is.EqualTo(normalizeNewlines expected)
                )
            | _ -> Assert.Fail($"Expected synthesized patch for {path}, got {result}"))

    [<Test>]
    member _.``binary untracked file returns an explicit binary state``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir
        File.WriteAllBytes(Path.Combine(repoDir, "binary.dat"), [| 1uy; 0uy; 2uy |])

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let result =
            getWorktreeDiffFile repoDir summary.MergeBase (findEntry "binary.dat" summary)
            |> TestUtils.runAsync

        Assert.That(
            (result = Ok Binary),
            Is.True,
            $"Expected binary state, got {result}"
        )

    [<Test>]
    member _.``tracked binary file returns an explicit binary state``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        File.WriteAllBytes(Path.Combine(repoDir, "binary.dat"), [| 1uy; 0uy; 2uy |])
        gitOk repoDir [ "add"; "--"; "binary.dat" ]
        gitOk repoDir [ "commit"; "-m"; "binary base" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]
        File.WriteAllBytes(Path.Combine(repoDir, "binary.dat"), [| 3uy; 0uy; 4uy |])

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let result =
            getWorktreeDiffFile repoDir summary.MergeBase (findEntry "binary.dat" summary)
            |> TestUtils.runAsync

        Assert.That(
            (result = Ok Binary),
            Is.True,
            $"Expected binary state, got {result}"
        )

    [<Test>]
    member _.``untracked patch byte cap returns oversized with no partial patch``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir
        writeText repoDir "large.txt" (String('x', maxWorktreeDiffBytes))

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let result =
            getWorktreeDiffFile repoDir summary.MergeBase (findEntry "large.txt" summary)
            |> TestUtils.runAsync

        Assert.That(
            (result = Ok Oversized),
            Is.True,
            $"Expected oversized state, got {result}"
        )

    [<Test>]
    member _.``patch line cap accepts exactly twenty thousand lines and rejects the next``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir

        let content lineCount =
            List.replicate lineCount "x"
            |> String.concat Environment.NewLine
            |> fun text -> text + Environment.NewLine

        writeText repoDir "at-limit.txt" (content (maxWorktreeDiffLines - 5))
        writeText repoDir "over-limit.txt" (content (maxWorktreeDiffLines - 4))

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let atLimit =
            getWorktreeDiffFile repoDir summary.MergeBase (findEntry "at-limit.txt" summary)
            |> TestUtils.runAsync

        let overLimit =
            getWorktreeDiffFile repoDir summary.MergeBase (findEntry "over-limit.txt" summary)
            |> TestUtils.runAsync

        match atLimit with
        | Ok(Text _) -> ()
        | _ -> Assert.Fail($"Expected boundary patch, got {atLimit}")

        Assert.That(
            (overLimit = Ok Truncated),
            Is.True,
            $"Expected truncated state, got {overLimit}"
        )

    [<Test>]
    member _.``summary counts composed replacements once before enforcing file limit``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        [ 1..maxWorktreeDiffFiles ]
        |> List.iter (fun index ->
            writeText repoDir (Path.Combine("many", $"file-{index:D4}.txt")) "")

        gitOk repoDir [ "add"; "--"; "many" ]
        gitOk repoDir [ "commit"; "-m"; "base files" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]
        Directory.Delete(Path.Combine(repoDir, "many"), recursive = true)
        gitOk repoDir [ "add"; "--all"; "--"; "many" ]
        gitOk repoDir [ "commit"; "-m"; "delete tracked files" ]

        [ 1..maxWorktreeDiffFiles ]
        |> List.iter (fun index ->
            writeText repoDir (Path.Combine("many", $"file-{index:D4}.txt")) "")

        let atLimit =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        Assert.That(atLimit.Files.Length, Is.EqualTo(maxWorktreeDiffFiles))
        Assert.That(
            atLimit.Files |> List.map _.Status |> Set.ofList,
            Is.EqualTo(Set.singleton (TrackedAndUntracked Deleted))
        )

        writeText repoDir "many/one-too-many.txt" ""
        let overLimit =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync

        Assert.That(
            (overLimit = Error(TooManyFiles(maxWorktreeDiffFiles + 1))),
            Is.True,
            $"Expected too-many-files state, got {overLimit}"
        )

    [<Test>]
    member _.``untracked symlink is reported without reading its target``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir
        let target = Path.Combine(tempDir, "outside-secret.txt")
        File.WriteAllText(target, "must not be read")
        let link = Path.Combine(repoDir, "link.txt")

        try
            File.CreateSymbolicLink(link, target) |> ignore
        with
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException ->
            Assert.Ignore("Symbolic links are unavailable in this environment")

        let summary =
            getWorktreeDiffSummary (comparisonContext repoDir)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let result =
            getWorktreeDiffFile repoDir summary.MergeBase (findEntry "link.txt" summary)
            |> TestUtils.runAsync

        Assert.That(
            (result = Ok(Symlink None)),
            Is.True,
            $"Expected symlink state, got {result}"
        )
