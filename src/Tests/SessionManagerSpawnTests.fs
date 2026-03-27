module Tests.SessionManagerSpawnTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open NUnit.Framework
open Shared
open Server.SessionManager
open Tests.TestUtils

[<TestFixture>]
[<Category("Local")>]
[<Explicit("Spawns terminal windows - run manually during session management development")>]
[<NonParallelizable>]
type SessionManagerSpawnTests() =

    let mutable spawnedPids: int list = []
    let mutable agent: SessionAgent option = None

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
    member _.``spawnTerminal returns Ok and HWND is resolved``() =
        let a = agent.Value
        let testPath = WorktreePath @"Q:\code\AITestAgent"
        let testPathStr = WorktreePath.value testPath

        let result = runAsync (spawnTerminal a testPath)
        assertOk result "spawnTerminal should return Ok"

        let sessions = runAsync (getActiveSessions a)
        Assert.That(sessions.ContainsKey(testPathStr), Is.True, "Session map should contain the worktree path")

        let hwnd = sessions[testPathStr]
        Assert.That(Server.Win32.isWindowValid hwnd, Is.True, "Tracked HWND should be a valid window")
        trackWindowPid hwnd
        TestContext.Out.WriteLine($"HWND={hwnd} resolved for {testPathStr}")

    [<Test>]
    member _.``killSession closes the window``() =
        let a = agent.Value
        let testPath = WorktreePath @"Q:\code\AITestAgent"
        let testPathStr = WorktreePath.value testPath

        let result = runAsync (spawnTerminal a testPath)
        assertOk result "spawnTerminal should return Ok"

        let sessions = runAsync (getActiveSessions a)
        let hwnd = sessions[testPathStr]
        trackWindowPid hwnd
        Assert.That(Server.Win32.isWindowValid hwnd, Is.True, "HWND should be valid before kill")

        let killResult = runAsync (killSession a testPath)
        assertOk killResult "killSession should return Ok"

        Thread.Sleep(1000)

        Assert.That(Server.Win32.isWindowValid hwnd, Is.False, "HWND should be invalid after kill")

        let sessionsAfter = runAsync (getActiveSessions a)
        Assert.That(sessionsAfter.ContainsKey(testPathStr), Is.False, "Session map should not contain killed session")

    [<Test>]
    member _.``re-spawn works after killSession``() =
        let a = agent.Value
        let testPath = WorktreePath @"Q:\code\AITestAgent"
        let testPathStr = WorktreePath.value testPath

        let result1 = runAsync (spawnTerminal a testPath)
        assertOk result1 "First spawn should return Ok"

        let sessions1 = runAsync (getActiveSessions a)
        let hwnd1 = sessions1[testPathStr]
        trackWindowPid hwnd1
        TestContext.Out.WriteLine($"First spawn: HWND={hwnd1}")

        let killResult = runAsync (killSession a testPath)
        assertOk killResult "killSession should return Ok"
        Thread.Sleep(1000)

        let result2 = runAsync (spawnTerminal a testPath)
        assertOk result2 "Re-spawn should return Ok"

        let sessions2 = runAsync (getActiveSessions a)
        Assert.That(sessions2.ContainsKey(testPathStr), Is.True, "Session map should contain re-spawned session")

        let hwnd2 = sessions2[testPathStr]
        trackWindowPid hwnd2
        TestContext.Out.WriteLine($"Re-spawn: HWND={hwnd2}")

        Assert.That(hwnd2, Is.Not.EqualTo(hwnd1), "Re-spawned window should have a different HWND")
        Assert.That(Server.Win32.isWindowValid hwnd2, Is.True, "Re-spawned HWND should be valid")
