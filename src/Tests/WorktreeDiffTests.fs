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
            getWorktreeDiffSummary repoDir
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
                getFilteredWorktreeDiffSummary repoDir selected
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
                getFilteredWorktreeDiffSummary repoDir selected
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
    member _.``tracked deletion recreated as untracked composes into one modified file``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initializeDiffRepo repoDir
        File.Delete(Path.Combine(repoDir, "delete.txt"))
        gitOk repoDir [ "add"; "--"; "delete.txt" ]
        gitOk repoDir [ "commit"; "-m"; "delete tracked file" ]
        writeText repoDir "delete.txt" "replacement"

        let summary =
            getWorktreeDiffSummary repoDir
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
            getWorktreeDiffSummary repoDir
            |> TestUtils.runAsync
            |> assertSummaryOk

        Assert.That(directUntracked, Is.EqualTo(generatedDiffViewerGitPath))
        Assert.That(summary.Files, Is.Empty)

    [<Test>]
    member _.``provisioned tracked diff viewer does not dirty a clean summary``() =
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
            getWorktreeDiffSummary repoDir
            |> TestUtils.runAsync
            |> assertSummaryOk

        Assert.That(File.ReadAllText(diffPath), Is.EqualTo(DiffTemplate.html))
        Assert.That(directTracked, Is.EqualTo(generatedDiffViewerGitPath))
        Assert.That(summary.Files, Is.Empty)

    [<Test>]
    member _.``remote tracking base is preferred without fetching``() =
        let repoDir, _ = initRepoWithOrigin tempDir
        writeText repoDir "local-main-only.txt" "local"
        gitOk repoDir [ "add"; "--"; "local-main-only.txt" ]
        gitOk repoDir [ "commit"; "-m"; "local main commit" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]

        let summary =
            getWorktreeDiffSummary repoDir
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
    member _.``missing configured base is a typed summary error``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir

        File.WriteAllText(
            Path.Combine(repoDir, ".treemon.json"),
            """{ "baseBranch": "missing" }"""
        )

        let result = getWorktreeDiffSummary repoDir |> TestUtils.runAsync

        Assert.That(
            (result = Error(BaseNotFound("missing", "origin/missing"))),
            Is.True,
            $"Expected missing base, got {result}"
        )

    [<Test>]
    member _.``local and untracked layers do not require a configured base``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        writeText repoDir "tracked.txt" "base"
        gitOk repoDir [ "add"; "--"; "tracked.txt" ]
        gitOk repoDir [ "commit"; "-m"; "base" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]

        File.WriteAllText(
            Path.Combine(repoDir, ".treemon.json"),
            """{ "baseBranch": "missing" }"""
        )
        gitOk repoDir [ "add"; "--"; ".treemon.json" ]
        gitOk repoDir [ "commit"; "-m"; "configure missing base" ]

        writeText repoDir "tracked.txt" "local"
        writeText repoDir "untracked.txt" "untracked"

        let localSummary =
            getFilteredWorktreeDiffSummary
                repoDir
                (layers false true false)
            |> TestUtils.runAsync
            |> assertSummaryOk

        let untrackedSummary =
            getFilteredWorktreeDiffSummary
                repoDir
                (layers false false true)
            |> TestUtils.runAsync
            |> assertSummaryOk

        Assert.Multiple(fun () ->
            Assert.That(localSummary.BaseRef, Is.EqualTo("HEAD"))
            Assert.That(localSummary.Files |> List.map _.Path, Is.EqualTo([ "tracked.txt" ]))
            Assert.That(untrackedSummary.BaseRef, Is.EqualTo("working tree"))
            Assert.That(untrackedSummary.Files |> List.map _.Path, Is.EqualTo([ "untracked.txt" ])))

    [<Test>]
    member _.``Git command failure is typed and does not produce a partial summary``() =
        let nonRepo = Path.Combine(tempDir, "not-a-repo")
        Directory.CreateDirectory(nonRepo) |> ignore

        let result = getWorktreeDiffSummary nonRepo |> TestUtils.runAsync

        match result with
        | Error(GitFailed(ResolveRemote, _)) -> ()
        | _ -> Assert.Fail($"Expected typed Git failure, got {result}")

    [<Test>]
    member _.``unrelated base history returns a typed merge-base failure``() =
        let repoDir = Path.Combine(tempDir, "repo")
        initRepoOnMain repoDir
        gitOk repoDir [ "checkout"; "--orphan"; "feature" ]
        gitOk repoDir [ "commit"; "--allow-empty"; "-m"; "unrelated feature" ]

        let result = getWorktreeDiffSummary repoDir |> TestUtils.runAsync

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
            getWorktreeDiffSummary repoDir
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
            getWorktreeDiffSummary repoDir
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
            getWorktreeDiffSummary repoDir
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
            getWorktreeDiffSummary repoDir
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
            getWorktreeDiffSummary repoDir
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
            getWorktreeDiffSummary repoDir
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
            writeText repoDir $"many/file-{index:D4}.txt" "")

        gitOk repoDir [ "add"; "--"; "many" ]
        gitOk repoDir [ "commit"; "-m"; "base files" ]
        gitOk repoDir [ "checkout"; "-b"; "feature" ]
        Directory.Delete(Path.Combine(repoDir, "many"), recursive = true)
        gitOk repoDir [ "add"; "--all"; "--"; "many" ]
        gitOk repoDir [ "commit"; "-m"; "delete tracked files" ]

        [ 1..maxWorktreeDiffFiles ]
        |> List.iter (fun index ->
            writeText repoDir $"many/file-{index:D4}.txt" "")

        let atLimit =
            getWorktreeDiffSummary repoDir
            |> TestUtils.runAsync
            |> assertSummaryOk

        Assert.That(atLimit.Files.Length, Is.EqualTo(maxWorktreeDiffFiles))
        Assert.That(
            atLimit.Files |> List.map _.Status |> Set.ofList,
            Is.EqualTo(Set.singleton (TrackedAndUntracked Deleted))
        )

        writeText repoDir "many/one-too-many.txt" ""
        let overLimit = getWorktreeDiffSummary repoDir |> TestUtils.runAsync

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
            getWorktreeDiffSummary repoDir
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
