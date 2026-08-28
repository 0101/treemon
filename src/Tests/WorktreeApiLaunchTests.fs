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

[<RequireQualifiedAccess>]
type private LaunchCall =
    | OpenNativeTerminal of WorktreePath
    | OpenNativeTab of WorktreePath
    | StartEmbeddedTerminal of WorktreePath
    | StartEmbeddedCommand of WorktreePath * string

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
    terminalLaunch
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
        terminalLaunch
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
    member _.``Worktree API selects typed launch operations and preserves exact embedded results``() =
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
            let calls = ConcurrentQueue<LaunchCall>()

            let terminalLaunch: TerminalLaunch.Operations =
                { OpenNativeTerminal =
                    fun requestedPath ->
                        async {
                            calls.Enqueue(LaunchCall.OpenNativeTerminal requestedPath)
                            return Ok()
                        }
                  OpenNativeTab =
                    fun requestedPath ->
                        async {
                            calls.Enqueue(LaunchCall.OpenNativeTab requestedPath)
                            return Ok()
                        }
                  StartEmbeddedTerminal =
                    fun requestedPath ->
                        async {
                            calls.Enqueue(LaunchCall.StartEmbeddedTerminal requestedPath)

                            return
                                Ok(
                                    startResult
                                        requestedPath
                                        (EmbeddedTerminalId.value plainId)
                                )
                        }
                  StartEmbeddedCommand =
                    fun requestedPath command ->
                        async {
                            calls.Enqueue(
                                LaunchCall.StartEmbeddedCommand(requestedPath, command)
                            )

                            return
                                match command with
                                | value when value = launchCommand ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value launchId)
                                    )
                                | value when value = actionCommand ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value actionId)
                                    )
                                | value when value = canvasCommand ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value canvasId)
                                    )
                                | value when value = resumeCommand ->
                                    Ok(
                                        startResult
                                            requestedPath
                                            (EmbeddedTerminalId.value resumeId)
                                    )
                                | _ -> Error "Unexpected embedded command"
                        } }

            let api = createApi root path None terminalLaunch

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
                    [| LaunchCall.OpenNativeTerminal path
                       LaunchCall.OpenNativeTab path
                       LaunchCall.StartEmbeddedTerminal path
                       LaunchCall.StartEmbeddedCommand(path, launchCommand)
                       LaunchCall.StartEmbeddedCommand(path, actionCommand)
                       LaunchCall.StartEmbeddedCommand(path, canvasCommand)
                       LaunchCall.StartEmbeddedCommand(path, resumeCommand) |]
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
                    WorktreePath * string * bool
                 >(TaskCreationOptions.RunContinuationsAsynchronously)
            let release =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let completed =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)

            let terminalLaunch: TerminalLaunch.Operations =
                { OpenNativeTerminal =
                    fun _ -> async { return Error "Unexpected native terminal launch" }
                  OpenNativeTab =
                    fun _ -> async { return Error "Unexpected native tab launch" }
                  StartEmbeddedTerminal =
                    fun _ -> async { return Error "Unexpected plain embedded launch" }
                  StartEmbeddedCommand =
                    fun requestedPath command ->
                        async {
                            let markerExists =
                                File.Exists(
                                    Path.Combine(
                                        WorktreePath.value requestedPath,
                                        "post-fork-ready.txt"
                                    )
                                )

                            started.TrySetResult((requestedPath, command, markerExists))
                            |> ignore

                            do! release.Task |> Async.AwaitTask
                            completed.TrySetResult() |> ignore

                            return
                                Ok(
                                    startResult
                                        requestedPath
                                        "66666666666666666666666666666666"
                                )
                        } }

            let api = createApi repoRoot rootPath None terminalLaunch
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

            let launchedPath, command, markerExists =
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
                    Assert.That(command, Is.EqualTo(expectedCommand))
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
            let calls = ConcurrentQueue<WorktreePath * string>()

            let terminalLaunch: TerminalLaunch.Operations =
                { OpenNativeTerminal =
                    fun _ -> async { return Error "Unexpected native terminal launch" }
                  OpenNativeTab =
                    fun _ -> async { return Error "Unexpected native tab launch" }
                  StartEmbeddedTerminal =
                    fun _ -> async { return Error "Unexpected plain embedded launch" }
                  StartEmbeddedCommand =
                    fun requestedPath command ->
                        async {
                            calls.Enqueue((requestedPath, command))

                            return
                                Ok(
                                    startResult
                                        requestedPath
                                        "77777777777777777777777777777777"
                                )
                        } }

            let api = createApi root path None terminalLaunch
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
                    Is.EqualTo([| (path, expectedCommand) |])
                )

                assertTerminalCommandAccepted expectedCommand))
