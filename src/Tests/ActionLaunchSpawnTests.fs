module Tests.ActionLaunchSpawnTests

open System
open System.IO
open System.Threading
open NUnit.Framework
open Shared
open Server.SessionManager
open Server.CodingToolStatus
open Server.CodingToolCli
open Tests.GitTestHelpers
open Tests.TestUtils

[<TestFixture>]
[<Category("Local")>]
[<Explicit("Spawns terminal windows - run manually during contextual action development")>]
[<NonParallelizable>]
type ActionLaunchSpawnTests() =

    let originalCwd = Environment.CurrentDirectory
    // NUnit owns fixture lifecycle, so these values must span SetUp, each test, and TearDown.
    let mutable agent: SessionAgent option = None
    let mutable tempRoot = ""

    let testPath () =
        WorktreePath(Path.Combine(tempRoot, "repo"))

    [<SetUp>]
    member _.Setup() =
        tempRoot <- Path.Combine(Path.GetTempPath(), $"treemon-action-spawn-{Guid.NewGuid():N}")
        initRepoOnMain (WorktreePath.value (testPath ()))
        Environment.CurrentDirectory <- tempRoot

        try
            agent <- Some(createAgent ())
        with _ ->
            Environment.CurrentDirectory <- originalCwd

            try
                Directory.Delete(tempRoot, recursive = true)
            with cleanupEx ->
                TestContext.Error.WriteLine($"SetUp cleanup failed: {cleanupEx.Message}")

            reraise ()

    [<TearDown>]
    member _.Cleanup() =
        let result =
            cleanupTerminalTestEnvironment
                agent
                originalCwd
                tempRoot
                (WorktreePath.value (testPath ()))
        agent <- None
        assertOk result "Terminal fixture cleanup failed"

    [<Test>]
    member _.``launchAction with no existing session spawns new window and tracks HWND``() =
        let a = agent.Value
        let testPath = testPath ()
        let testPathStr = WorktreePath.value testPath |> Server.PathUtils.normalizePath
        let prompt = actionPrompt (Some CodingToolProvider.CopilotCli) (FixPr "https://dev.azure.com/org/proj/_git/repo/pullrequest/42")
        let command = (build (Some CodingToolProvider.CopilotCli) (Interactive prompt)).AsShellString

        let result = runAsync (launchAction a testPath command)
        assertOk result "launchAction should return Ok when no session exists"

        let sessions = runAsync (getActiveSessions a)
        Assert.That(sessions.ContainsKey(testPathStr), Is.True,
            "Session map should contain the worktree path after launchAction spawn")

        let hwnd = sessions[testPathStr]
        Assert.That(Server.Win32.isWindowValid hwnd, Is.True,
            "Tracked HWND should be a valid window")
        TestContext.Out.WriteLine($"launchAction spawn: HWND={hwnd} for {testPathStr}")

    [<Test>]
    member _.``launchAction with existing tracked session opens new tab without new window``() =
        let a = agent.Value
        let testPath = testPath ()
        let testPathStr = WorktreePath.value testPath |> Server.PathUtils.normalizePath

        let spawnResult = runAsync (spawnTerminal a testPath)
        assertOk spawnResult "Initial spawnTerminal should return Ok"

        let sessionsBefore = runAsync (getActiveSessions a)
        let existingHwnd = sessionsBefore[testPathStr]
        TestContext.Out.WriteLine($"Existing session HWND={existingHwnd}")

        let windowsBefore = Server.Win32.listWindowsTerminalWindows () |> Set.ofList
        let windowCountBefore = windowsBefore.Count
        TestContext.Out.WriteLine($"WT windows before launchAction: {windowCountBefore}")

        let prompt = actionPrompt (Some CodingToolProvider.CopilotCli) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123")
        let command = (build (Some CodingToolProvider.CopilotCli) (Interactive prompt)).AsShellString
        let actionResult = runAsync (launchAction a testPath command)
        assertOk actionResult "launchAction should return Ok when session exists (new tab)"

        Thread.Sleep(2000)

        let sessionsAfter = runAsync (getActiveSessions a)
        Assert.That(sessionsAfter[testPathStr], Is.EqualTo(existingHwnd),
            "Session HWND should remain the same (reused existing window)")

        let windowsAfter = Server.Win32.listWindowsTerminalWindows () |> Set.ofList
        let windowCountAfter = windowsAfter.Count
        TestContext.Out.WriteLine($"WT windows after launchAction: {windowCountAfter}")

        Assert.That(windowCountAfter, Is.LessThanOrEqualTo(windowCountBefore + 1),
            "launchAction with existing session should reuse existing window, not spawn a brand new one")

    [<Test>]
    member _.``launchAction spawns session that stays open (interactive mode)``() =
        let a = agent.Value
        let testPath = testPath ()
        let testPathStr = WorktreePath.value testPath |> Server.PathUtils.normalizePath
        let prompt = "Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"
        let command = (build (Some CodingToolProvider.CopilotCli) (Interactive prompt)).AsShellString

        let result = runAsync (launchAction a testPath command)
        assertOk result "launchAction should return Ok"

        let sessions = runAsync (getActiveSessions a)
        let hwnd = sessions[testPathStr]

        Thread.Sleep(3000)

        Assert.That(Server.Win32.isWindowValid hwnd, Is.True,
            "Window should remain open after 3 seconds (interactive mode keeps session alive)")
        TestContext.Out.WriteLine($"Interactive session still alive: HWND={hwnd}")

    [<Test>]
    member _.``launchAction with special characters in prompt succeeds``() =
        let a = agent.Value
        let testPath = testPath ()
        let testPathStr = WorktreePath.value testPath |> Server.PathUtils.normalizePath
        let prompt = actionPrompt (Some CodingToolProvider.CopilotCli) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123&view=logs&s=abc")
        let command = (build (Some CodingToolProvider.CopilotCli) (Interactive prompt)).AsShellString

        let result = runAsync (launchAction a testPath command)
        assertOk result "launchAction with URL containing & and ? should return Ok"

        let sessions = runAsync (getActiveSessions a)
        Assert.That(sessions.ContainsKey(testPathStr), Is.True,
            "Session should be tracked after spawn with special-character prompt")

        let hwnd = sessions[testPathStr]
        Assert.That(Server.Win32.isWindowValid hwnd, Is.True,
            "Spawned window with special-character prompt should be valid")
        TestContext.Out.WriteLine($"Special-char prompt spawn: HWND={hwnd}")
