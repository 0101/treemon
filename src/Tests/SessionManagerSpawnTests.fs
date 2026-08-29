module Tests.SessionManagerSpawnTests

open NUnit.Framework
open Shared
open Server.SessionManager
open Tests.GitTestHelpers
open Tests.TerminalSessionFixture
open Tests.TestUtils

[<TestFixture>]
[<Category("Local")>]
[<Explicit("Spawns terminal windows - run manually during session management development")>]
[<NonParallelizable>]
type SessionManagerSpawnTests() =

    // NUnit owns fixture lifecycle, so the environment must span SetUp, each test, and TearDown.
    let mutable environment: TerminalTestEnvironment option = None

    [<SetUp>]
    member _.Setup() =
        environment <- Some(create "treemon-session-spawn" initRepoOnMain)

    [<TearDown>]
    member _.Cleanup() =
        let result = environment |> Option.map cleanup |> Option.defaultValue (Ok())
        environment <- None
        assertOk result "Terminal fixture cleanup failed"

    [<Test>]
    member _.``spawnTerminal returns Ok and HWND is resolved``() =
        let testEnvironment = environment.Value
        let a = testEnvironment.Agent
        let testPath = testEnvironment.WorktreePath
        let testPathStr = WorktreePath.value testPath |> Server.PathUtils.normalizePath

        let result = runAsync (spawnTerminal a testPath)
        assertOk result "spawnTerminal should return Ok"

        let sessions = runAsync (getActiveSessions a)
        Assert.That(sessions.ContainsKey(testPathStr), Is.True, "Session map should contain the worktree path")
        let hwnd = sessions[testPathStr]
        Assert.That(Server.Win32.isWindowValid hwnd, Is.True, "Tracked HWND should be a valid window")
        TestContext.Out.WriteLine($"HWND={hwnd} resolved for {testPathStr}")

    [<Test>]
    member _.``killSession closes the window``() =
        let testEnvironment = environment.Value
        let a = testEnvironment.Agent
        let testPath = testEnvironment.WorktreePath
        let testPathStr = WorktreePath.value testPath |> Server.PathUtils.normalizePath

        let result = runAsync (spawnTerminal a testPath)
        assertOk result "spawnTerminal should return Ok"

        let sessions = runAsync (getActiveSessions a)
        let hwnd = sessions[testPathStr]
        Assert.That(Server.Win32.isWindowValid hwnd, Is.True, "HWND should be valid before kill")

        let killResult = runAsync (killSession a testPath)
        assertOk killResult "killSession should return Ok"

        Assert.That(Server.Win32.isWindowValid hwnd, Is.False, "HWND should be invalid after kill")

        let sessionsAfter = runAsync (getActiveSessions a)
        Assert.That(sessionsAfter.ContainsKey(testPathStr), Is.False, "Session map should not contain killed session")

    [<Test>]
    member _.``re-spawn works after killSession``() =
        let testEnvironment = environment.Value
        let a = testEnvironment.Agent
        let testPath = testEnvironment.WorktreePath
        let testPathStr = WorktreePath.value testPath |> Server.PathUtils.normalizePath

        let result1 = runAsync (spawnTerminal a testPath)
        assertOk result1 "First spawn should return Ok"

        let sessions1 = runAsync (getActiveSessions a)
        let hwnd1 = sessions1[testPathStr]
        TestContext.Out.WriteLine($"First spawn: HWND={hwnd1}")

        let killResult = runAsync (killSession a testPath)
        assertOk killResult "killSession should return Ok"
        Assert.That(Server.Win32.isWindowValid hwnd1, Is.False, "First HWND should be invalid after kill")

        let result2 = runAsync (spawnTerminal a testPath)
        assertOk result2 "Re-spawn should return Ok"

        let sessions2 = runAsync (getActiveSessions a)
        Assert.That(sessions2.ContainsKey(testPathStr), Is.True, "Session map should contain re-spawned session")
        let hwnd2 = sessions2[testPathStr]
        TestContext.Out.WriteLine($"Re-spawn: HWND={hwnd2}")

        Assert.That(Server.Win32.isWindowValid hwnd2, Is.True, "Re-spawned HWND should be valid")

    [<Test>]
    member _.``focus and new tab preserve the tracked native window``() =
        let testEnvironment = environment.Value
        let a = testEnvironment.Agent
        let testPath = testEnvironment.WorktreePath
        let testPathStr = WorktreePath.value testPath |> Server.PathUtils.normalizePath

        spawnTerminal a testPath
        |> runAsync
        |> fun result -> assertOk result "spawnTerminal should return Ok"

        let hwnd = runAsync (getActiveSessions a) |> Map.find testPathStr
        let windowsBeforeFocus = Server.Win32.listWindowsTerminalWindows () |> Set.ofList

        let shellsBeforeFocus =
            match runAsync (ownedPowerShellProcessIds testPathStr) with
            | Ok pids -> Set.ofList pids
            | Error message ->
                Assert.Fail($"Failed to inspect fixture PowerShell processes: {message}")
                Set.empty

        Assert.Multiple(fun () ->
            Assert.That(windowsBeforeFocus, Does.Contain(hwnd))
            Assert.That(shellsBeforeFocus, Is.Not.Empty))

        focusSession a testPath
        |> runAsync
        |> fun result -> assertOk result "focusSession should return Ok"

        let windowsAfterFocus = Server.Win32.listWindowsTerminalWindows () |> Set.ofList

        let shellsAfterFocus =
            match runAsync (ownedPowerShellProcessIds testPathStr) with
            | Ok pids -> Set.ofList pids
            | Error message ->
                Assert.Fail($"Failed to inspect fixture PowerShell processes: {message}")
                Set.empty

        Assert.Multiple(fun () ->
            Assert.That(windowsAfterFocus, Is.EqualTo(windowsBeforeFocus))
            Assert.That(shellsAfterFocus, Is.EqualTo(shellsBeforeFocus)))

        openNewTab a testPath
        |> runAsync
        |> fun result -> assertOk result "openNewTab should return Ok"

        let sessionsAfter = runAsync (getActiveSessions a)

        Assert.Multiple(fun () ->
            Assert.That(sessionsAfter[testPathStr], Is.EqualTo(hwnd))
            Assert.That(Server.Win32.isWindowValid hwnd, Is.True))
