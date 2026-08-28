module Tests.WorktreeApiLaunchTests

open System
open System.Collections.Concurrent
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Server.CodingToolCli
open Server.SchedulerState
open Tests.GitTestHelpers
open Tests.TestUtils

let private terminalId value =
    EmbeddedTerminalId value

let private assertTerminalCommandAccepted command =
    Assert.That(
        TerminalHostClient.validateTerminalCommand command,
        Is.EqualTo(Ok command : Result<string, string>)
    )

let private startResult path id =
    let id = terminalId id

    { Snapshot =
        { Tabs =
            [ { Id = id
                Worktree = path
                ReportedActivity = None
                Lifecycle =
                    EmbeddedTerminalLifecycle.Running
                        $"http://127.0.0.1:41001/{EmbeddedTerminalId.value id}/" } ] }
      TerminalId = id }

let private createApi
    root
    worktreePath
    activityStore
    launchTerminal
    =
    let agent = SchedulerState.createAgent ()
    let repoId = PathUtils.toRepoId root

    agent.Post(
        UpdateWorktreeList(
            repoId,
            [ { GitWorktree.WorktreeInfo.Path = WorktreePath.value worktreePath
                Head = "head"
                Branch = Some "main" } ]))

    agent.PostAndAsyncReply(GetState)
    |> runAsync
    |> ignore

    WorktreeApi.worktreeApiWithLaunch
        launchTerminal
        { Agent = agent
          CardLog = CardEventLog.createAgent ()
          // These backends are deliberately unavailable: every start in this fixture must cross
          // the injected TerminalLaunch boundary or fail loudly by dereferencing the test sentinel.
          SessionAgent = Unchecked.defaultof<SessionManager.SessionAgent>
          EmbeddedTerminal = Unchecked.defaultof<EmbeddedTerminal.Manager>
          ActivityStore = activityStore
          SnapshotStore = None
          AutoSyncStore = None
          WorktreeRoots = [ root ]
          TestFixtures = None
          AppVersion = "test"
          DeployBranch = None }

let private assertStart expectedId result =
    match result with
    | Ok started ->
        Assert.Multiple(fun () ->
            Assert.That(started.TerminalId, Is.EqualTo expectedId)
            Assert.That(
                started.Snapshot.Tabs |> List.map _.Id,
                Is.EqualTo([ expectedId ])
            ))
    | Error error ->
        Assert.Fail($"Expected embedded launch success but got: {error}")

let private writePostForkMarkerScript repoRoot =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        File.WriteAllText(
            Path.Combine(repoRoot, "post-fork.ps1"),
            "param($wt, $root, $baseRef, $branch)\nSet-Content -LiteralPath (Join-Path $wt 'post-fork-ready.txt') -Value 'ready'"
        )
    else
        File.WriteAllText(
            Path.Combine(repoRoot, "post-fork.sh"),
            "#!/usr/bin/env bash\nprintf 'ready' > \"$1/post-fork-ready.txt\"\n"
        )

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WorktreeApiLaunchTests() =

    [<Test>]
    member _.``Worktree API selects launch intent and preserves exact embedded results``() =
        withTempDir "treemon-worktree-api-launch" (fun root ->
            let path =
                root
                |> Path.GetFullPath
                |> PathUtils.normalizePath
                |> WorktreePath

            let launchPrompt = "Read the copied prompt and implement it"
            let action = FixBuild "https://example.test/build/42"
            let canvasPrompt =
                CanvasSessionPrompt.forAgentDoc
                    (WorktreePath.value path)
                    "review.html"
            let launchCommand =
                (build None (Interactive launchPrompt)).AsShellString
            let actionCommand =
                action
                |> CodingToolStatus.actionPrompt None
                |> Interactive
                |> build None
                |> _.AsShellString
            let canvasCommand =
                (build None (Interactive canvasPrompt)).AsShellString
            let resumeCommand =
                (build None (Resume None)).AsShellString

            let plainId = terminalId "11111111111111111111111111111111"
            let launchId = terminalId "22222222222222222222222222222222"
            let actionId = terminalId "33333333333333333333333333333333"
            let canvasId = terminalId "44444444444444444444444444444444"
            let resumeId = terminalId "55555555555555555555555555555555"
            let calls =
                ConcurrentQueue<TerminalLaunch.Intent * WorktreePath>()

            let launchTerminal intent requestedPath =
                async {
                    calls.Enqueue((intent, requestedPath))

                    return
                        match intent with
                        | TerminalLaunch.Intent.OpenNativeTerminal
                        | TerminalLaunch.Intent.OpenNativeTab ->
                            Ok TerminalLaunch.LaunchResult.Native
                        | TerminalLaunch.Intent.StartEmbeddedTerminal ->
                            Ok(
                                TerminalLaunch.LaunchResult.Embedded(
                                    startResult requestedPath (EmbeddedTerminalId.value plainId)
                                )
                            )
                        | TerminalLaunch.Intent.StartEmbeddedCommand command
                            when command = launchCommand ->
                            Ok(
                                TerminalLaunch.LaunchResult.Embedded(
                                    startResult requestedPath (EmbeddedTerminalId.value launchId)
                                )
                            )
                        | TerminalLaunch.Intent.StartEmbeddedCommand command
                            when command = actionCommand ->
                            Ok(
                                TerminalLaunch.LaunchResult.Embedded(
                                    startResult requestedPath (EmbeddedTerminalId.value actionId)
                                )
                            )
                        | TerminalLaunch.Intent.StartEmbeddedCommand command
                            when command = canvasCommand ->
                            Ok(
                                TerminalLaunch.LaunchResult.Embedded(
                                    startResult requestedPath (EmbeddedTerminalId.value canvasId)
                                )
                            )
                        | TerminalLaunch.Intent.StartEmbeddedCommand command
                            when command = resumeCommand ->
                            Ok(
                                TerminalLaunch.LaunchResult.Embedded(
                                    startResult requestedPath (EmbeddedTerminalId.value resumeId)
                                )
                            )
                        | TerminalLaunch.Intent.StartEmbeddedCommand _ ->
                            Error "Unexpected embedded command"
                }

            let api = createApi root path None launchTerminal

            api.openTerminal path |> runAsync
            let nativeTab = api.openNewTab path |> runAsync
            let plain = api.startEmbeddedTerminal path |> runAsync
            let launched =
                api.launchSession
                    { Path = path
                      Prompt = launchPrompt }
                |> runAsync
            let actionLaunched =
                api.launchAction
                    { Path = path
                      Action = action }
                |> runAsync
            let canvasLaunched =
                api.launchAction
                    { Path = path
                      Action = CanvasSession canvasPrompt }
                |> runAsync
            let resumed = api.resumeSession path |> runAsync

            Assert.That(
                nativeTab,
                Is.EqualTo(Ok() : Result<unit, string>)
            )
            assertStart plainId plain
            assertStart launchId launched
            assertStart actionId actionLaunched
            assertStart canvasId canvasLaunched
            assertStart resumeId resumed
            Assert.That(
                calls.ToArray(),
                Is.EqualTo(
                    [| (TerminalLaunch.Intent.OpenNativeTerminal, path)
                       (TerminalLaunch.Intent.OpenNativeTab, path)
                       (TerminalLaunch.Intent.StartEmbeddedTerminal, path)
                       (TerminalLaunch.Intent.StartEmbeddedCommand launchCommand, path)
                       (TerminalLaunch.Intent.StartEmbeddedCommand actionCommand, path)
                       (TerminalLaunch.Intent.StartEmbeddedCommand canvasCommand, path)
                       (TerminalLaunch.Intent.StartEmbeddedCommand resumeCommand, path) |]
                )
            )

            assertTerminalCommandAccepted canvasCommand)

    [<Test>]
    member _.``Create with prompt launches after post-fork without awaiting terminal completion``() =
        withTempDir "treemon-create-embedded-launch" (fun parent ->
            let repoRoot = Path.Combine(parent, "repo")
            initRepoOnMain repoRoot
            writePostForkMarkerScript repoRoot

            let rootPath =
                repoRoot
                |> Path.GetFullPath
                |> PathUtils.normalizePath
                |> WorktreePath

            let started =
                TaskCompletionSource<
                    TerminalLaunch.Intent * WorktreePath * bool
                 >(TaskCreationOptions.RunContinuationsAsynchronously)
            let release =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let completed =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)

            let launchTerminal intent requestedPath =
                async {
                    let markerExists =
                        File.Exists(
                            Path.Combine(
                                WorktreePath.value requestedPath,
                                "post-fork-ready.txt"
                            )
                        )

                    started.TrySetResult((intent, requestedPath, markerExists))
                    |> ignore

                    do! release.Task |> Async.AwaitTask
                    completed.TrySetResult() |> ignore

                    return
                        Ok(
                            TerminalLaunch.LaunchResult.Embedded(
                                startResult
                                    requestedPath
                                    "66666666666666666666666666666666"
                            )
                        )
                }

            let api = createApi repoRoot rootPath None launchTerminal
            let prompt =
                "Implement the next ready task.\r\n"
                + "Preserve this second line exactly."
            let skill = "bd-execute"
            let branch = "routed-create"

            let createResult =
                api.createWorktree
                    { RepoId =
                        repoRoot
                        |> PathUtils.toRepoId
                        |> RepoId.value
                      BranchName = BranchName.create branch
                      BaseBranch = BranchName.create "main"
                      Prompt = Some prompt
                      Skill = Some skill }
                |> runAsync

            let intent, launchedPath, markerExists =
                started.Task
                    .WaitAsync(TimeSpan.FromSeconds 15.0)
                    .GetAwaiter()
                    .GetResult()

            try
                let wrapped =
                    CodingToolStatus.skillInvocation None skill prompt
                let expectedCommand =
                    (build None (Interactive wrapped)).AsShellString
                let expectedPath =
                    Path.Combine(parent, $"tm-{branch}")

                Assert.Multiple(fun () ->
                    Assert.That(Result.isOk createResult, Is.True)
                    Assert.That(markerExists, Is.True,
                        "the embedded launch must wait until post-fork setup has completed")
                    Assert.That(
                        PathUtils.pathEquals
                            (WorktreePath.value launchedPath)
                            expectedPath,
                        Is.True
                    )
                    Assert.That(
                        intent,
                        Is.EqualTo(
                            TerminalLaunch.Intent.StartEmbeddedCommand
                                expectedCommand
                        )
                    )
                    assertTerminalCommandAccepted expectedCommand
                    Assert.That(completed.Task.IsCompleted, Is.False,
                        "createWorktree must not wait for the fire-and-forget terminal launch"))
            finally
                release.TrySetResult() |> ignore
                completed.Task
                    .WaitAsync(TimeSpan.FromSeconds 5.0)
                    .GetAwaiter()
                    .GetResult())

    [<Test>]
    member _.``Queued SystemView fallback starts embedded command without changing its queued result``() =
        withTempDir "treemon-canvas-embedded-launch" (fun root ->
            let path =
                root
                |> Path.GetFullPath
                |> PathUtils.normalizePath
                |> WorktreePath
            let calls =
                ConcurrentQueue<TerminalLaunch.Intent * WorktreePath>()

            let launchTerminal intent requestedPath =
                async {
                    calls.Enqueue((intent, requestedPath))

                    return
                        Ok(
                            TerminalLaunch.LaunchResult.Embedded(
                                startResult
                                    requestedPath
                                    "77777777777777777777777777777777"
                            )
                        )
                }

            let api = createApi root path None launchTerminal
            let filename = "diff.html"
            let result =
                api.sendCanvasMessage
                    { WorktreePath = path
                      Filename = filename
                      Payload = """{"action":"canvas-selection"}""" }
                |> runAsync
            let expectedCommand =
                CanvasPrompt.continueWorking
                    (WorktreePath.value path)
                    filename
                |> Interactive
                |> build None
                |> _.AsShellString

            Assert.Multiple(fun () ->
                Assert.That(result, Is.EqualTo CanvasMessageResult.Queued)
                Assert.That(
                    calls.ToArray(),
                    Is.EqualTo(
                        [| TerminalLaunch.Intent.StartEmbeddedCommand
                               expectedCommand,
                           path |]
                    )
                )

                assertTerminalCommandAccepted expectedCommand))
