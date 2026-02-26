module Tests.Win32ExperimentTests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open NUnit.Framework

// Win32 P/Invoke declarations

type EnumWindowsProc = delegate of nativeint * nativeint -> bool

[<DllImport("user32.dll", SetLastError = true)>]
extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nativeint lParam)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool SetForegroundWindow(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 GetWindowThreadProcessId(nativeint hWnd, uint32& lpdwProcessId)

[<DllImport("user32.dll")>]
extern bool IsWindow(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)>]
extern int GetClassName(nativeint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount)

[<DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)>]
extern int GetWindowTextLength(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)>]
extern int GetWindowText(nativeint hWnd, System.Text.StringBuilder lpString, int nMaxCount)

[<DllImport("user32.dll")>]
extern bool IsWindowVisible(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool AllowSetForegroundWindow(uint32 dwProcessId)

[<DllImport("kernel32.dll")>]
extern uint32 GetCurrentProcessId()

[<DllImport("kernel32.dll")>]
extern uint32 GetCurrentThreadId()

[<DllImport("user32.dll")>]
extern nativeint GetForegroundWindow()

[<DllImport("user32.dll")>]
extern bool AttachThreadInput(uint32 idAttach, uint32 idAttachTo, bool fAttach)

[<DllImport("user32.dll")>]
extern bool BringWindowToTop(nativeint hWnd)

[<DllImport("user32.dll")>]
extern bool ShowWindow(nativeint hWnd, int nCmdShow)

[<DllImport("user32.dll")>]
extern void keybd_event(byte bVk, byte bScan, uint32 dwFlags, nativeint dwExtraInfo)

let SW_RESTORE = 9
let VK_MENU = 0x12uy
let KEYEVENTF_EXTENDEDKEY = 0x1u
let KEYEVENTF_KEYUP = 0x2u

// Helpers

let listTopLevelWindows () =
    let windows = Collections.Generic.List<nativeint>()
    let callback = EnumWindowsProc(fun hwnd _ -> windows.Add(hwnd); true)
    EnumWindows(callback, 0n) |> ignore
    windows |> Seq.toList

let getClassName (hwnd: nativeint) =
    let sb = System.Text.StringBuilder(256)
    let len = GetClassName(hwnd, sb, sb.Capacity)
    if len > 0 then sb.ToString() else ""

let getWindowTitle (hwnd: nativeint) =
    let len = GetWindowTextLength(hwnd)
    if len = 0 then ""
    else
        let sb = System.Text.StringBuilder(len + 1)
        GetWindowText(hwnd, sb, sb.Capacity) |> ignore
        sb.ToString()

let getWindowPid (hwnd: nativeint) =
    let mutable pid = 0u
    GetWindowThreadProcessId(hwnd, &pid) |> ignore
    pid

let getWindowThreadId (hwnd: nativeint) =
    let mutable pid = 0u
    GetWindowThreadProcessId(hwnd, &pid)

let listWindowsTerminalWindows () =
    listTopLevelWindows ()
    |> List.filter (fun hwnd ->
        IsWindowVisible(hwnd) && getClassName hwnd = "CASCADIA_HOSTING_WINDOW_CLASS")

let waitForNewWindow (beforeSet: Set<nativeint>) (timeoutMs: int) =
    let stopwatch = Stopwatch.StartNew()
    let rec poll () =
        if stopwatch.ElapsedMilliseconds > int64 timeoutMs then
            None
        else
            let current = listWindowsTerminalWindows () |> Set.ofList
            let newWindows = Set.difference current beforeSet
            if Set.isEmpty newWindows then
                Threading.Thread.Sleep(100)
                poll ()
            else
                Some (Set.minElement newWindows, stopwatch.ElapsedMilliseconds)
    poll ()

let spawnWindowsTerminal () =
    let beforeWindows = listWindowsTerminalWindows () |> Set.ofList
    let psi =
        ProcessStartInfo(
            "wt.exe",
            "--window new -- pwsh -NoExit -Command \"Write-Host 'FOCUS-EXPERIMENT'\"",
            UseShellExecute = false,
            CreateNoWindow = true)
    let wtProcess = Process.Start(psi)
    wtProcess.WaitForExit(10_000) |> ignore
    waitForNewWindow beforeWindows 10_000

/// Try to focus a window using AttachThreadInput + SetForegroundWindow
let focusWithAttachThreadInput (hwnd: nativeint) =
    let foregroundHwnd = GetForegroundWindow()
    let foregroundThreadId = getWindowThreadId foregroundHwnd
    let currentThreadId = GetCurrentThreadId()
    let targetThreadId = getWindowThreadId hwnd

    TestContext.Out.WriteLine($"  Current thread: {currentThreadId}")
    TestContext.Out.WriteLine($"  Foreground window thread: {foregroundThreadId}")
    TestContext.Out.WriteLine($"  Target window thread: {targetThreadId}")

    // Attach our thread to the foreground window's thread
    let attached1 = AttachThreadInput(currentThreadId, foregroundThreadId, true)
    TestContext.Out.WriteLine($"  AttachThreadInput(current->foreground): {attached1}")

    // Also attach to the target thread
    let attached2 = AttachThreadInput(currentThreadId, targetThreadId, true)
    TestContext.Out.WriteLine($"  AttachThreadInput(current->target): {attached2}")

    let result = SetForegroundWindow(hwnd)
    TestContext.Out.WriteLine($"  SetForegroundWindow after attach: {result}")

    // Detach
    if attached1 then
        AttachThreadInput(currentThreadId, foregroundThreadId, false) |> ignore
    if attached2 then
        AttachThreadInput(currentThreadId, targetThreadId, false) |> ignore

    result

/// Try to focus a window by simulating ALT keypress then SetForegroundWindow
let focusWithKeybdEvent (hwnd: nativeint) =
    // Simulate pressing and releasing ALT key to trick Windows
    // into thinking our process has legitimate foreground access
    keybd_event(VK_MENU, 0uy, KEYEVENTF_EXTENDEDKEY, 0n)
    let result = SetForegroundWindow(hwnd)
    keybd_event(VK_MENU, 0uy, KEYEVENTF_EXTENDEDKEY ||| KEYEVENTF_KEYUP, 0n)
    result

/// Try to focus a window using ShowWindow(SW_RESTORE) + BringWindowToTop
let focusWithShowWindowCombo (hwnd: nativeint) =
    let showResult = ShowWindow(hwnd, SW_RESTORE)
    TestContext.Out.WriteLine($"  ShowWindow(SW_RESTORE): {showResult}")
    let bringResult = BringWindowToTop(hwnd)
    TestContext.Out.WriteLine($"  BringWindowToTop: {bringResult}")
    let setFgResult = SetForegroundWindow(hwnd)
    TestContext.Out.WriteLine($"  SetForegroundWindow after ShowWindow+BringWindowToTop: {setFgResult}")
    setFgResult


[<TestFixture>]
[<Category("Local")>]
type Win32ExperimentTests() =

    let mutable spawnedPids: int list = []

    [<TearDown>]
    member _.Cleanup() =
        spawnedPids
        |> List.iter (fun pid ->
            try
                let proc = Process.GetProcessById(pid)
                proc.Kill(entireProcessTree = true)
                TestContext.Out.WriteLine($"Killed process PID={pid}")
            with _ -> ())
        spawnedPids <- []

    /// Experiment 1: HWND resolution via EnumWindows diff
    /// Spawns wt.exe --window new -- pwsh -NoExit, polls for new CASCADIA_HOSTING_WINDOW_CLASS window
    [<Test>]
    member _.``Experiment 1 - HWND resolution via EnumWindows diff``() =
        let beforeWindows = listWindowsTerminalWindows () |> Set.ofList
        TestContext.Out.WriteLine($"Before spawn: {beforeWindows.Count} Windows Terminal windows")

        let psi =
            ProcessStartInfo(
                "wt.exe",
                "--window new -- pwsh -NoExit -Command \"Write-Host 'HWND-EXPERIMENT-MARKER'\"",
                UseShellExecute = false,
                CreateNoWindow = true)

        let wtProcess = Process.Start(psi)
        TestContext.Out.WriteLine($"wt.exe spawned, PID={wtProcess.Id}")

        // wt.exe is a launcher -- it exits quickly after sending IPC to WindowsTerminal.exe
        wtProcess.WaitForExit(10_000) |> ignore
        TestContext.Out.WriteLine($"wt.exe exited: {wtProcess.HasExited}, ExitCode: {if wtProcess.HasExited then wtProcess.ExitCode else -1}")

        match waitForNewWindow beforeWindows 10_000 with
        | Some (hwnd, elapsedMs) ->
            let className = getClassName hwnd
            let title = getWindowTitle hwnd
            let pid = getWindowPid hwnd
            TestContext.Out.WriteLine($"New window found in {elapsedMs}ms")
            TestContext.Out.WriteLine($"  HWND: {hwnd}")
            TestContext.Out.WriteLine($"  ClassName: {className}")
            TestContext.Out.WriteLine($"  Title: {title}")
            TestContext.Out.WriteLine($"  PID: {pid}")
            TestContext.Out.WriteLine($"  IsWindow: {IsWindow(hwnd)}")

            spawnedPids <- int pid :: spawnedPids

            Assert.That(IsWindow(hwnd), Is.True, "Resolved HWND should be valid")
            Assert.That(className, Is.EqualTo("CASCADIA_HOSTING_WINDOW_CLASS"))
            TestContext.Out.WriteLine("RESULT: HWND resolution via EnumWindows diff WORKS")

        | None ->
            let afterWindows = listWindowsTerminalWindows ()
            TestContext.Out.WriteLine($"After timeout: {afterWindows.Length} Windows Terminal windows")
            Assert.Fail("Failed to detect new Windows Terminal window within 10 seconds")


    /// Experiment 2: SetForegroundWindow via P/Invoke
    /// Uses the HWND from Experiment 1 approach and calls SetForegroundWindow
    [<Test>]
    member _.``Experiment 2 - SetForegroundWindow via P-Invoke``() =
        let beforeWindows = listWindowsTerminalWindows () |> Set.ofList

        let psi =
            ProcessStartInfo(
                "wt.exe",
                "--window new -- pwsh -NoExit -Command \"Write-Host 'FOCUS-EXPERIMENT'\"",
                UseShellExecute = false,
                CreateNoWindow = true)

        let wtProcess = Process.Start(psi)
        wtProcess.WaitForExit(10_000) |> ignore

        match waitForNewWindow beforeWindows 10_000 with
        | Some (hwnd, _) ->
            let pid = getWindowPid hwnd
            spawnedPids <- int pid :: spawnedPids
            TestContext.Out.WriteLine($"Window spawned, HWND={hwnd}, PID={pid}")

            // Wait a moment for window to be fully rendered
            Threading.Thread.Sleep(500)

            // Attempt 1: Direct SetForegroundWindow
            let result1 = SetForegroundWindow(hwnd)
            TestContext.Out.WriteLine($"SetForegroundWindow direct call: {result1}")

            if not result1 then
                // Attempt 2: AllowSetForegroundWindow then retry
                TestContext.Out.WriteLine("Direct SetForegroundWindow failed, trying AllowSetForegroundWindow...")
                let allowResult = AllowSetForegroundWindow(pid)
                TestContext.Out.WriteLine($"AllowSetForegroundWindow(PID={pid}): {allowResult}")
                Threading.Thread.Sleep(100)
                let result2 = SetForegroundWindow(hwnd)
                TestContext.Out.WriteLine($"SetForegroundWindow after Allow: {result2}")

                if not result2 then
                    TestContext.Out.WriteLine("RESULT: SetForegroundWindow BLOCKED even with AllowSetForegroundWindow")
                    TestContext.Out.WriteLine("May need AttachThreadInput or UIA approach")
                    Assert.Fail("SetForegroundWindow failed with both approaches")
                else
                    TestContext.Out.WriteLine("RESULT: SetForegroundWindow WORKS with AllowSetForegroundWindow")
            else
                TestContext.Out.WriteLine("RESULT: SetForegroundWindow WORKS directly")

        | None ->
            Assert.Fail("Failed to spawn window for SetForegroundWindow test")


    /// Experiment 2b: AttachThreadInput workaround for SetForegroundWindow
    /// Attach calling thread to foreground thread, call SetForegroundWindow, detach
    [<Test>]
    member _.``Experiment 2b - AttachThreadInput workaround``() =
        match spawnWindowsTerminal () with
        | Some (hwnd, _) ->
            let pid = getWindowPid hwnd
            spawnedPids <- int pid :: spawnedPids
            TestContext.Out.WriteLine($"Window spawned, HWND={hwnd}, PID={pid}")

            // Let the window fully render and lose focus
            Threading.Thread.Sleep(1000)

            TestContext.Out.WriteLine("Attempting AttachThreadInput workaround...")
            let result = focusWithAttachThreadInput hwnd
            let status = if result then "WORKS" else "FAILED"
            TestContext.Out.WriteLine($"RESULT: AttachThreadInput workaround {status}")

            // Log whether the window actually came to foreground
            Threading.Thread.Sleep(200)
            let fgHwnd = GetForegroundWindow()
            TestContext.Out.WriteLine($"  Foreground window after attempt: {fgHwnd} (target was {hwnd})")
            TestContext.Out.WriteLine($"  Window is foreground: {fgHwnd = hwnd}")

        | None ->
            Assert.Fail("Failed to spawn window for AttachThreadInput test")


    /// Experiment 2c: keybd_event ALT key workaround for SetForegroundWindow
    /// Simulate ALT keypress to bypass foreground lock, then SetForegroundWindow
    [<Test>]
    member _.``Experiment 2c - keybd_event ALT workaround``() =
        match spawnWindowsTerminal () with
        | Some (hwnd, _) ->
            let pid = getWindowPid hwnd
            spawnedPids <- int pid :: spawnedPids
            TestContext.Out.WriteLine($"Window spawned, HWND={hwnd}, PID={pid}")

            Threading.Thread.Sleep(1000)

            TestContext.Out.WriteLine("Attempting keybd_event ALT workaround...")
            let result = focusWithKeybdEvent hwnd
            TestContext.Out.WriteLine($"  SetForegroundWindow after ALT simulation: {result}")

            Threading.Thread.Sleep(200)
            let fgHwnd = GetForegroundWindow()
            TestContext.Out.WriteLine($"  Foreground window after attempt: {fgHwnd} (target was {hwnd})")
            TestContext.Out.WriteLine($"  Window is foreground: {fgHwnd = hwnd}")
            let status = if result then "WORKS" else "FAILED"
            TestContext.Out.WriteLine($"RESULT: keybd_event ALT workaround {status}")

        | None ->
            Assert.Fail("Failed to spawn window for keybd_event test")


    /// Experiment 2d: ShowWindow + BringWindowToTop combo
    /// ShowWindow(SW_RESTORE) then BringWindowToTop then SetForegroundWindow
    [<Test>]
    member _.``Experiment 2d - ShowWindow BringWindowToTop combo``() =
        match spawnWindowsTerminal () with
        | Some (hwnd, _) ->
            let pid = getWindowPid hwnd
            spawnedPids <- int pid :: spawnedPids
            TestContext.Out.WriteLine($"Window spawned, HWND={hwnd}, PID={pid}")

            Threading.Thread.Sleep(1000)

            TestContext.Out.WriteLine("Attempting ShowWindow + BringWindowToTop combo...")
            let result = focusWithShowWindowCombo hwnd

            Threading.Thread.Sleep(200)
            let fgHwnd = GetForegroundWindow()
            TestContext.Out.WriteLine($"  Foreground window after attempt: {fgHwnd} (target was {hwnd})")
            TestContext.Out.WriteLine($"  Window is foreground: {fgHwnd = hwnd}")
            let status = if result then "WORKS" else "FAILED"
            TestContext.Out.WriteLine($"RESULT: ShowWindow+BringWindowToTop combo {status}")

        | None ->
            Assert.Fail("Failed to spawn window for ShowWindow+BringWindowToTop test")


    /// Experiment 2e: Combined approach - all techniques together
    /// Try the most promising combination: ALT key + AttachThreadInput + SetForegroundWindow
    [<Test>]
    member _.``Experiment 2e - Combined focus approach``() =
        match spawnWindowsTerminal () with
        | Some (hwnd, _) ->
            let pid = getWindowPid hwnd
            spawnedPids <- int pid :: spawnedPids
            TestContext.Out.WriteLine($"Window spawned, HWND={hwnd}, PID={pid}")

            Threading.Thread.Sleep(1000)

            TestContext.Out.WriteLine("Attempting combined approach (ALT + AttachThreadInput + ShowWindow + SetForegroundWindow)...")

            let foregroundHwnd = GetForegroundWindow()
            let foregroundThreadId = getWindowThreadId foregroundHwnd
            let currentThreadId = GetCurrentThreadId()
            let targetThreadId = getWindowThreadId hwnd

            // Step 1: Simulate ALT key press
            keybd_event(VK_MENU, 0uy, KEYEVENTF_EXTENDEDKEY, 0n)

            // Step 2: Attach threads
            let attached1 = AttachThreadInput(currentThreadId, foregroundThreadId, true)
            let attached2 = AttachThreadInput(currentThreadId, targetThreadId, true)
            TestContext.Out.WriteLine($"  Thread attachments: fg={attached1}, target={attached2}")

            // Step 3: ShowWindow + BringWindowToTop
            ShowWindow(hwnd, SW_RESTORE) |> ignore
            BringWindowToTop(hwnd) |> ignore

            // Step 4: SetForegroundWindow
            let result = SetForegroundWindow(hwnd)
            TestContext.Out.WriteLine($"  SetForegroundWindow: {result}")

            // Step 5: Release ALT key
            keybd_event(VK_MENU, 0uy, KEYEVENTF_EXTENDEDKEY ||| KEYEVENTF_KEYUP, 0n)

            // Step 6: Detach threads
            if attached1 then
                AttachThreadInput(currentThreadId, foregroundThreadId, false) |> ignore
            if attached2 then
                AttachThreadInput(currentThreadId, targetThreadId, false) |> ignore

            Threading.Thread.Sleep(200)
            let fgHwnd = GetForegroundWindow()
            let isNowForeground = fgHwnd = hwnd
            TestContext.Out.WriteLine($"  Foreground window after attempt: {fgHwnd} (target was {hwnd})")
            TestContext.Out.WriteLine($"  Window is foreground: {isNowForeground}")
            let status = if result || isNowForeground then "WORKS" else "FAILED"
            TestContext.Out.WriteLine($"RESULT: Combined focus approach {status}")

        | None ->
            Assert.Fail("Failed to spawn window for combined focus test")


    /// Experiment 3: Claude spawn with HWND resolution
    /// Spawns wt.exe --window new -d <path> -- claude "hello", verifies HWND resolution
    [<Test>]
    member _.``Experiment 3 - Claude spawn with HWND resolution``() =
        let testWorkdir = @"Q:\code\AITestAgent"
        let beforeWindows = listWindowsTerminalWindows () |> Set.ofList
        TestContext.Out.WriteLine($"Before spawn: {beforeWindows.Count} Windows Terminal windows")

        let arguments = $"--window new -d \"{testWorkdir}\" -- claude \"Say hello and exit\""
        TestContext.Out.WriteLine($"Spawning: wt.exe {arguments}")

        let psi =
            ProcessStartInfo(
                "wt.exe",
                arguments,
                UseShellExecute = false,
                CreateNoWindow = true)

        let wtProcess = Process.Start(psi)
        wtProcess.WaitForExit(10_000) |> ignore
        TestContext.Out.WriteLine($"wt.exe exited: {wtProcess.HasExited}")

        // Claude may take longer to start, use longer timeout
        match waitForNewWindow beforeWindows 15_000 with
        | Some (hwnd, elapsedMs) ->
            let className = getClassName hwnd
            let title = getWindowTitle hwnd
            let pid = getWindowPid hwnd
            TestContext.Out.WriteLine($"New window found in {elapsedMs}ms")
            TestContext.Out.WriteLine($"  HWND: {hwnd}")
            TestContext.Out.WriteLine($"  ClassName: {className}")
            TestContext.Out.WriteLine($"  Title: {title}")
            TestContext.Out.WriteLine($"  PID: {pid}")
            TestContext.Out.WriteLine($"  IsWindow: {IsWindow(hwnd)}")

            spawnedPids <- int pid :: spawnedPids

            Assert.That(IsWindow(hwnd), Is.True, "Resolved HWND should be valid")
            Assert.That(className, Is.EqualTo("CASCADIA_HOSTING_WINDOW_CLASS"))

            // Verify SetForegroundWindow works on Claude window too
            Threading.Thread.Sleep(500)
            let focusResult = SetForegroundWindow(hwnd)
            TestContext.Out.WriteLine($"SetForegroundWindow on Claude window: {focusResult}")

            TestContext.Out.WriteLine("RESULT: Claude spawn with HWND resolution WORKS")

        | None ->
            let afterWindows = listWindowsTerminalWindows ()
            TestContext.Out.WriteLine($"After timeout: {afterWindows.Length} Windows Terminal windows")
            Assert.Fail("Failed to detect new Windows Terminal window for Claude spawn within 15 seconds")


    /// Experiment 1b: Spawn pwsh.exe directly as fallback
    /// Gets PID directly, finds its console window
    [<Test>]
    member _.``Experiment 1b - Direct pwsh spawn as fallback``() =
        let psi =
            ProcessStartInfo(
                "pwsh.exe",
                "-NoExit -Command \"Write-Host 'DIRECT-SPAWN-EXPERIMENT'\"",
                UseShellExecute = true,
                CreateNoWindow = false)

        let proc = Process.Start(psi)
        spawnedPids <- proc.Id :: spawnedPids
        TestContext.Out.WriteLine($"pwsh.exe spawned directly, PID={proc.Id}")

        // Wait for window to appear
        Threading.Thread.Sleep(2_000)

        // Find windows owned by this PID
        let matchingWindows =
            listTopLevelWindows ()
            |> List.filter (fun hwnd ->
                let pid = getWindowPid hwnd
                pid = uint32 proc.Id && IsWindowVisible(hwnd))

        TestContext.Out.WriteLine($"Windows owned by PID {proc.Id}: {matchingWindows.Length}")

        matchingWindows
        |> List.iter (fun hwnd ->
            let className = getClassName hwnd
            let title = getWindowTitle hwnd
            TestContext.Out.WriteLine($"  HWND: {hwnd}, Class: {className}, Title: {title}"))

        if matchingWindows.Length > 0 then
            TestContext.Out.WriteLine("RESULT: Direct pwsh spawn WORKS as fallback, PID-based HWND lookup succeeds")
        else
            TestContext.Out.WriteLine("RESULT: Direct pwsh spawn - no visible windows found for PID")
            Assert.Fail("No windows found for directly spawned pwsh.exe")


    /// Timing characterization: how fast can we resolve HWNDs?
    [<Test>]
    member _.``Timing - HWND resolution latency characterization``() =
        let timings = Collections.Generic.List<int64>()

        // Run 3 iterations to characterize timing
        {1..3}
        |> Seq.iter (fun i ->
            let beforeWindows = listWindowsTerminalWindows () |> Set.ofList

            let psi =
                ProcessStartInfo(
                    "wt.exe",
                    $"--window new -- pwsh -NoExit -Command \"Write-Host 'TIMING-RUN-{i}'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true)

            let wtProcess = Process.Start(psi)
            wtProcess.WaitForExit(10_000) |> ignore

            match waitForNewWindow beforeWindows 10_000 with
            | Some (hwnd, elapsedMs) ->
                timings.Add(elapsedMs)
                let pid = getWindowPid hwnd
                spawnedPids <- int pid :: spawnedPids
                TestContext.Out.WriteLine($"Run {i}: HWND resolved in {elapsedMs}ms")
                Threading.Thread.Sleep(500)
            | None ->
                TestContext.Out.WriteLine($"Run {i}: FAILED to resolve HWND"))

        if timings.Count > 0 then
            let avg = timings |> Seq.averageBy float
            let min = timings |> Seq.min
            let max = timings |> Seq.max
            TestContext.Out.WriteLine($"Timing summary: avg={avg:F0}ms, min={min}ms, max={max}ms, samples={timings.Count}")
            TestContext.Out.WriteLine($"RESULT: HWND resolution timing characterized")
        else
            Assert.Fail("No successful HWND resolutions for timing characterization")
