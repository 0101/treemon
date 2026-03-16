module Tests.ActionLaunchSpawnTests

open System
open System.Diagnostics
open System.Threading
open NUnit.Framework
open Shared
open Server.SessionManager
open Server.CodingToolStatus
open Tests.TestUtils

[<TestFixture>]
[<Category("Local")>]
[<Explicit("Spawns terminal windows - run manually during contextual action development")>]
[<NonParallelizable>]
type ActionLaunchSpawnTests() =

    let mutable spawnedPids: int list = []
    let mutable agent: SessionAgent option = None
    let testPath = WorktreePath.create @"Q:\code\AITestAgent"
    let testPathStr = WorktreePath.value testPath

    let trackWindowPid (hwnd: nativeint) =
        let pid = Server.Win32.getWindowPid hwnd
        if pid > 0 then
            spawnedPids <- pid :: spawnedPids

    [<SetUp>]
    member _.Setup() =
        agent <- Some(createAgent ())

    [<TearDown>]
    member _.Cleanup() =
        agent
        |> Option.iter (fun a ->
            try
                let sessions = runAsync (getActiveSessions a)
                sessions |> Map.iter (fun _ hwnd ->
                    try Server.Win32.closeWindow hwnd |> ignore with _ -> ()
                    trackWindowPid hwnd)
            with _ -> ())

        Thread.Sleep(500)

        spawnedPids
        |> List.iter (fun pid ->
            try
                let proc = Process.GetProcessById(pid)
                if not proc.HasExited then
                    proc.Kill(entireProcessTree = true)
                    TestContext.Out.WriteLine($"TearDown: killed PID={pid}")
            with _ -> ())

        spawnedPids <- []
        agent <- None

    [<Test>]
    member _.``launchAction with no existing session spawns new window and tracks HWND``() =
        let a = agent.Value
        let prompt = actionPrompt (Some CodingToolProvider.Claude) (FixPr "https://dev.azure.com/org/proj/_git/repo/pullrequest/42")
        let command = buildInteractiveCommand (Some CodingToolProvider.Claude) prompt

        let result = runAsync (launchAction a testPath command)
        assertOk result "launchAction should return Ok when no session exists"

        let sessions = runAsync (getActiveSessions a)
        Assert.That(sessions.ContainsKey(testPathStr), Is.True,
            "Session map should contain the worktree path after launchAction spawn")

        let hwnd = sessions[testPathStr]
        Assert.That(Server.Win32.isWindowValid hwnd, Is.True,
            "Tracked HWND should be a valid window")
        trackWindowPid hwnd
        TestContext.Out.WriteLine($"launchAction spawn: HWND={hwnd} for {testPathStr}")

    [<Test>]
    member _.``launchAction with existing tracked session opens new tab without new window``() =
        let a = agent.Value

        let spawnResult = runAsync (spawnTerminal a testPath)
        assertOk spawnResult "Initial spawnTerminal should return Ok"

        let sessionsBefore = runAsync (getActiveSessions a)
        let existingHwnd = sessionsBefore[testPathStr]
        trackWindowPid existingHwnd
        TestContext.Out.WriteLine($"Existing session HWND={existingHwnd}")

        let windowsBefore = Server.Win32.listWindowsTerminalWindows () |> Set.ofList
        let windowCountBefore = windowsBefore.Count
        TestContext.Out.WriteLine($"WT windows before launchAction: {windowCountBefore}")

        let prompt = actionPrompt (Some CodingToolProvider.Claude) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123")
        let command = buildInteractiveCommand (Some CodingToolProvider.Claude) prompt
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
        let prompt = "Commit all changes, push to origin with upstream tracking, and create a pull request for this branch"
        let command = buildInteractiveCommand (Some CodingToolProvider.Claude) prompt

        let result = runAsync (launchAction a testPath command)
        assertOk result "launchAction should return Ok"

        let sessions = runAsync (getActiveSessions a)
        let hwnd = sessions[testPathStr]
        trackWindowPid hwnd

        Thread.Sleep(3000)

        Assert.That(Server.Win32.isWindowValid hwnd, Is.True,
            "Window should remain open after 3 seconds (interactive mode keeps session alive)")
        TestContext.Out.WriteLine($"Interactive session still alive: HWND={hwnd}")

    [<Test>]
    member _.``launchAction with special characters in prompt succeeds``() =
        let a = agent.Value
        let prompt = actionPrompt (Some CodingToolProvider.Claude) (FixBuild "https://dev.azure.com/org/proj/_build/results?buildId=123&view=logs&s=abc")
        let command = buildInteractiveCommand (Some CodingToolProvider.Claude) prompt

        let result = runAsync (launchAction a testPath command)
        assertOk result "launchAction with URL containing & and ? should return Ok"

        let sessions = runAsync (getActiveSessions a)
        Assert.That(sessions.ContainsKey(testPathStr), Is.True,
            "Session should be tracked after spawn with special-character prompt")

        let hwnd = sessions[testPathStr]
        Assert.That(Server.Win32.isWindowValid hwnd, Is.True,
            "Spawned window with special-character prompt should be valid")
        trackWindowPid hwnd
        TestContext.Out.WriteLine($"Special-char prompt spawn: HWND={hwnd}")
